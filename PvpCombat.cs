using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP — SHOT / DAMAGE LANE. In a PvP arena each player fires their OWN artillery locally (co-op fire
    /// replication is OFF when PvpActive), so every shell impact THIS machine adjudicates is THIS player's shot.
    ///
    /// HIT DETECTION: a Harmony POSTFIX on <c>State_ImpactStart.StartImpact(state, shell, impactLocation)</c> reads
    /// the engine's returned hit set (the List&lt;MapEntity&gt; within the impact radius — the same query the PvpProbe
    /// proved returns a programmatically-spawned entity). If a hit entity is an opponent's player-mirror (a
    /// <see cref="PvpPlayers"/> Enemy MapEntity, ID "PVPPLAYER_&lt;origin&gt;"), this player's shell hit that
    /// opponent → broadcast <c>MSG_PVP_HIT</c> addressed to that peer's SteamID.
    ///
    /// DAMAGE (victim-authoritative, symmetric — no privileged host): the addressed victim is the enemy team's
    /// CAPTAIN (the only member with a mirror), who applies the damage to the shared TEAM health
    /// (<see cref="PvpPlayers.ApplyTeamDamage"/>); the reduced health rides the next <c>MSG_PVP_POS</c> keyframe so
    /// both the attacker's mirror and the victim's teammates reflect it. The hit packet is BROADCAST (the transport
    /// is a host-relayed star — a client can't direct-send another client) with the victim's id embedded, so only
    /// the addressed peer applies it (correct for N&gt;2 too; everyone else ignores).
    ///
    /// Entirely gated on <see cref="Config.PvpActive"/> ⇒ co-op/solo untouched (this postfix bails immediately, and
    /// CoopImpact's postfix on the same method owns the co-op path).
    /// </summary>
    internal static class PvpCombat
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_PVP_HIT = 43;   // [t][victimLo i32][victimHi i32][dmg f32][shellId str]  attacker -> victim
        // DAMAGE vs the 4-HP nest, per shell type (user spec): AP 2, HE 1, HCHE 0.5, STAR 0 (recon only — STAR reveals,
        // it never damages), everything else 1. Keyed on ShellId, NOT the authored ShellDefinition.Damage, because that
        // field is an int (AP=2 / STAR=0 / rest=1) and can't express HCHE's 0.5. See DamageForShell.
        //
        // Hit radius = the shell's OWN authored ImpactRadius (the base game's normal blast radius per shell type), read
        // live from the ShellDefinition. Demo values (grid units, == the game's own EvaluateImpact space): AP 0.15,
        // HE 0.25, HCHE 0.55, PGAS 0.75, SMK 1.0, TGAS 1.0, STAR 0.2. FallbackHitRadius is used ONLY when the shell /
        // its radius couldn't be read (shell null / interop glitch) so a valid impact isn't silently un-hittable.
        public const float FallbackHitRadius = 0.5f;

        private static Il2CppStructArray<byte> _buf;
        private static int _dealt, _taken;
        private static readonly System.Collections.Generic.List<ulong> _victims = new System.Collections.Generic.List<ulong>();

        // Last shell impact this machine adjudicated — exposed for the dev HUD ranging feedback (impact vs target).
        public static Vector2 LastImpact;
        public static float LastImpactTime = -999f;
        public static bool LastImpactHit;
        public static int Dealt => _dealt;
        public static int Taken => _taken;

        // ---------------- hit detection (Harmony postfix on State_ImpactStart.StartImpact) ----------------

        public static void OnImpactAdjudicated(ShellDefinition shell, Vector2 impactLocation,
                                               Il2CppSystem.Collections.Generic.List<MapEntity> __result)
        {
            try
            {
                if (!Config.PvpActive) return;                       // co-op path is CoopImpact's
                if (!SteamNet.InLobby || !CoopP2P.HasPeer) return;

                string shellId = ""; float impactRadius = 0f;
                try { if (shell != null) { shellId = shell.ShellId ?? ""; impactRadius = shell.ImpactRadius; } } catch { }
                float dmg = DamageForShell(shellId);
                float radius = impactRadius > 0f ? impactRadius : FallbackHitRadius;   // the game's normal per-shell blast radius

                LastImpact = impactLocation; LastImpactTime = Time.unscaledTime; LastImpactHit = false;   // HUD ranging feedback

                // STAR (illumination) shells deal no damage — they SPOT the enemy. Reveal enemies near the burst.
                if (IsStar(shellId)) { try { PvpPlayers.OnStarShell(impactLocation); } catch { } }

#if !PUBLIC_BUILD
                int engineCount = -1; try { if (__result != null) engineCount = __result.Count; } catch { }
                string origin = "?"; try { var t = TurretController.Instance; if (t != null && t.turretBase != null) origin = t.turretBase.position.ToString("0.0"); } catch { }
                Log.LogInfo($"[pvp] impact ({impactLocation.x:0.00},{impactLocation.y:0.00}) shell='{shellId}' dmg={dmg:0.#} ImpactRadius={impactRadius:0.00} hitRadius={radius:0.00} engineHits={engineCount} mirrors={PvpPlayers.MirrorCount} turretBase={origin}");
                try { PvpPlayers.LogImpactProximity(impactLocation, radius); } catch { }
#endif

                // DISTANCE-BASED adjudication (victim-authoritative). The engine's StartImpact hit set (__result) does
                // NOT return programmatically-spawned entities (per PvpProbe's findings), so we don't rely on it:
                // we placed each opponent mirror and know its map grid, and impactLocation is in that same map space —
                // so a shell within `radius` of a mirror is a hit on that peer.
                if (dmg <= 0f) return;   // a non-damaging shell (STAR) — its reveal (above) is the whole effect
                int n = PvpPlayers.CollectHits(impactLocation, radius, _victims);
                for (int i = 0; i < n; i++)
                {
                    ulong victim = _victims[i];
                    SendHit(victim, dmg, shellId);
                    _dealt++; LastImpactHit = true;
                    Log.LogInfo($"[pvp] my shell hit opponent peer {victim} for {dmg:0.#} (shell='{shellId}') at ({impactLocation.x:0.0},{impactLocation.y:0.0})");
                }
            }
            catch (Exception ex) { Log.LogWarning("[pvp] impact: " + ex.Message); }
        }

        private static void SendHit(ulong victim, float dmg, string shellId)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_PVP_HIT);
            w.Int((int)(victim & 0xFFFFFFFFUL));   // SteamID low 32
            w.Int((int)(victim >> 32));            // SteamID high 32
            w.Float(dmg);
            w.Str(shellId, 64);
            if (w.Overflow) { Log.LogWarning("[pvp] hit packet overflow"); return; }
            CoopP2P.Send(_buf, w.Length, true);    // broadcast; only the addressed victim applies it
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_PVP_HIT) return;
            if (!Config.PvpActive) return;
            if (origin == CoopP2P.MyId) return;            // never damage myself from my own packet
            var r = new CoopWire.Reader(a, len, 1);
            ulong victim = (uint)r.Int() | ((ulong)(uint)r.Int() << 32);
            float dmg = r.Float();
            string shellId = r.Str(64);
            if (r.Bad) return;
            if (victim != CoopP2P.MyId) return;            // addressed to a different peer (N>2) — ignore
            _taken++;
            PvpPlayers.ApplyTeamDamage(dmg, origin);       // I'm the addressed team captain → apply to shared team hp
        }

        // ---------------- diagnostics ----------------

        public static string Status() => $"pvpCombat: dealt={_dealt} taken={_taken}";

        // Damage a shell deals to the 4-HP nest, by ShellId (user spec). STAR is 0 (it reveals instead); HCHE's 0.5
        // is why this is a float map and not the int ShellDefinition.Damage. Unlisted shells (smoke/gas/empty) -> 1.
        private static float DamageForShell(string id)
        {
            if (string.IsNullOrEmpty(id)) return 1f;
            switch (id.Trim().ToUpperInvariant())
            {
                case "AP":   return 2f;
                case "HE":   return 1f;
                case "HCHE": return 0.5f;
                case "STAR": return 0f;
                default:     return 1f;
            }
        }

        private static bool IsStar(string id) => !string.IsNullOrEmpty(id) && string.Equals(id.Trim(), "STAR", StringComparison.OrdinalIgnoreCase);

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(128); return true; }
            catch (Exception e) { Log.LogWarning("[pvp] combat buf: " + e.Message); return false; }
        }
    }
}
