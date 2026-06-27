using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP Phase 2 — SHOT / DAMAGE LANE. In a PvP arena each player fires their OWN artillery locally (co-op fire
    /// replication is OFF when PvpActive), so every shell impact THIS machine adjudicates is THIS player's shot.
    ///
    /// HIT DETECTION: a Harmony POSTFIX on <c>State_ImpactStart.StartImpact(state, shell, impactLocation)</c> reads
    /// the engine's returned hit set (the List&lt;MapEntity&gt; within the impact radius — the same query Phase 0
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

        public const byte MSG_PVP_HIT = 43;   // [t][victimLo i32][victimHi i32][dmg i32][shellId str]  attacker -> victim
        public const int BaseHitDamage = 25;  // per direct hit; scaled by the shell's Damage multiplier (AP=2 -> 50, HE=1 -> 25)
        public const float MinHitRadius = 2.0f; // floor so a clean ranging shot near the marker counts (real HE blast is only 0.25; tune toward real for shipping)

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

                int shellDmg = 1; string shellId = ""; float impactRadius = 0f;
                try { if (shell != null) { shellId = shell.ShellId ?? ""; shellDmg = shell.Damage; impactRadius = shell.ImpactRadius; } } catch { }
                int dmg = BaseHitDamage * Mathf.Max(1, shellDmg);
                float radius = Mathf.Max(impactRadius, MinHitRadius);

                LastImpact = impactLocation; LastImpactTime = Time.unscaledTime; LastImpactHit = false;   // HUD ranging feedback

#if !PUBLIC_BUILD
                int engineCount = -1; try { if (__result != null) engineCount = __result.Count; } catch { }
                string origin = "?"; try { var t = TurretController.Instance; if (t != null && t.turretBase != null) origin = t.turretBase.position.ToString("0.0"); } catch { }
                Log.LogInfo($"[pvp] impact ({impactLocation.x:0.00},{impactLocation.y:0.00}) shell='{shellId}' ImpactRadius={impactRadius:0.00} hitRadius={radius:0.00} engineHits={engineCount} mirrors={PvpPlayers.MirrorCount} turretBase={origin}");
                try { PvpPlayers.LogImpactProximity(impactLocation, radius); } catch { }
#endif

                // DISTANCE-BASED adjudication (victim-authoritative). The engine's StartImpact hit set (__result) does
                // NOT return programmatically-spawned entities (Phase 0 / PvpProbe RUN notes), so we don't rely on it:
                // we placed each opponent mirror and know its map grid, and impactLocation is in that same map space —
                // so a shell within `radius` of a mirror is a hit on that peer.
                int n = PvpPlayers.CollectHits(impactLocation, radius, _victims);
                for (int i = 0; i < n; i++)
                {
                    ulong victim = _victims[i];
                    SendHit(victim, dmg, shellId);
                    _dealt++; LastImpactHit = true;
                    Log.LogInfo($"[pvp] my shell hit opponent peer {victim} for {dmg} (shell='{shellId}') at ({impactLocation.x:0.0},{impactLocation.y:0.0})");
                }
            }
            catch (Exception ex) { Log.LogWarning("[pvp] impact: " + ex.Message); }
        }

        private static void SendHit(ulong victim, int dmg, string shellId)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_PVP_HIT);
            w.Int((int)(victim & 0xFFFFFFFFUL));   // SteamID low 32
            w.Int((int)(victim >> 32));            // SteamID high 32
            w.Int(dmg);
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
            int dmg = r.Int();
            string shellId = r.Str(64);
            if (r.Bad) return;
            if (victim != CoopP2P.MyId) return;            // addressed to a different peer (N>2) — ignore
            _taken++;
            PvpPlayers.ApplyTeamDamage(dmg, origin);       // I'm the addressed team captain → apply to shared team hp
        }

        // ---------------- diagnostics ----------------

        public static string Status() => $"pvpCombat: dealt={_dealt} taken={_taken}";

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(128); return true; }
            catch (Exception e) { Log.LogWarning("[pvp] combat buf: " + e.Message); return false; }
        }
    }
}
