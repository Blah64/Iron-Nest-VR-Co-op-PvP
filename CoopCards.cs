using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op: replicate the gun-console FIRE-MISSION CARD (PATH B — player-driven; the printed firing
    /// solution). When a player computes a solution the <c>FireMissionCardPrinter</c> spawns a <c>FireMissionCard</c>
    /// and calls <c>Apply(distance, bearing, gunElevation, powderCharge, shellType, gunSelection)</c> — six ALREADY-
    /// RESOLVED strings. The peer can't re-derive them (they read that machine's own dials / artillery computer), so
    /// we capture the six strings via a Harmony postfix on <c>FireMissionCard.Apply</c> and the peer spawns a mirror
    /// card from the printer's own prefab and Applies the same text.
    ///
    /// BIDIRECTIONAL (unlike the host-authoritative teleprinter orders): EITHER player may print a card and the
    /// other should see it. Each ORIGINAL print happens only on the machine whose player computed it, so there's no
    /// doubling; an <c>_applying</c> echo guard stops the mirror's own Apply from bouncing back. Not phase-scoped —
    /// the fire-mission console is player-driven wherever it exists (hub or mission).
    ///
    /// LIMITATIONS (noted): (1) the target/powder-charge TEXTURES (the icon images) are NOT replicated — only the
    /// six text fields, which are the actual firing solution; the textures need live Texture refs we don't ship.
    /// (2) the mirror spawns at the FIRST FireMissionCardPrinter found; if the console has separate per-gun printers
    /// the physical card may appear at the wrong one (its gunSelection text still reads correctly). (3) the mirror
    /// appears fully-formed rather than animating out of the printer.
    /// </summary>
    internal static class CoopCards
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_CARD = 27;   // [t][dist str][bear str][elev str][powder str][shell str][gun str]  reliable

        private static bool _applying;     // echo guard: true while we Apply a mirrored card (so the postfix doesn't rebroadcast)
        private static int _sent, _applied;

        private static Il2CppStructArray<byte> _buf;

        // ---------------- capture (Harmony postfix on FireMissionCard.Apply) ----------------

        // Registered by CoopSim.ApplyPatches. Fires after every card Apply on BOTH machines; the machine whose
        // player actually printed broadcasts the six strings. Skips a card WE just spawned as a mirror (echo guard).
        // Parameter names match FireMissionCard.Apply so HarmonyX injects the originals.
        public static void OnCardApplied(string distanceToTarget, string bearingToTarget, string gunElevation, string powderCharge, string shellType, string gunSelection)
        {
            try
            {
                if (_applying) return;   // our own mirror being filled in — don't bounce it back
                if (!Config.CoopCardSync) return;
                if (!SteamNet.InLobby || !CoopP2P.HasPeer) return;
                Broadcast(distanceToTarget, bearingToTarget, gunElevation, powderCharge, shellType, gunSelection);
            }
            catch (Exception e) { Log.LogWarning("[card] capture: " + e.Message); }
        }

        private static void Broadcast(string d, string b, string e, string p, string s, string g)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_CARD);
            w.Str(d, 80); w.Str(b, 80); w.Str(e, 80); w.Str(p, 80); w.Str(s, 80); w.Str(g, 80);
            if (w.Overflow) { Log.LogWarning("[card] packet too large for " + _buf.Length + "B - not sent"); return; }
            CoopP2P.Send(_buf, w.Length, true);
            _sent++;
            Log.LogInfo($"[card] fire-mission card -> peer (dist='{d}' bear='{b}' elev='{e}' pow='{p}' shell='{s}' gun='{g}')");
        }

        // ---------------- receive (peer) ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_CARD) return;
            var r = new CoopWire.Reader(a, len, 1);
            string d = r.Str(80);
            string b = r.Str(80);
            string e = r.Str(80);
            string p = r.Str(80);
            string s = r.Str(80);
            string g = r.Str(80);
            if (r.Bad) return;
            SpawnMirror(d ?? "", b ?? "", e ?? "", p ?? "", s ?? "", g ?? "");
        }

        // Spawn a mirror card from the printer's own prefab and fill it in, under the echo guard so our Apply
        // doesn't bounce back through the capture postfix.
        private static void SpawnMirror(string d, string b, string e, string p, string s, string g)
        {
            try
            {
                var printer = FindPrinter();
                if (printer == null) { Log.LogWarning("[card] no FireMissionCardPrinter in scene to spawn a mirror card"); return; }

                GameObject prefab = null; Transform parent = null, point = null;
                try { prefab = printer.fireMissionCardPrefab; } catch { }
                try { parent = printer.spawnParent; } catch { }
                try { point = printer.spawnPoint; } catch { }
                if (prefab == null) { Log.LogWarning("[card] printer has no fireMissionCardPrefab"); return; }

                _applying = true;
                try
                {
                    var inst = (parent != null
                        ? UnityEngine.Object.Instantiate(prefab, parent)
                        : UnityEngine.Object.Instantiate(prefab)).TryCast<GameObject>();
                    if (inst == null) { Log.LogWarning("[card] instantiate failed"); return; }
                    try { inst.SetActive(true); } catch { }
                    try { if (point != null) inst.transform.SetPositionAndRotation(point.position, point.rotation); } catch { }

                    FireMissionCard card = null;
                    try { card = inst.GetComponent<FireMissionCard>(); } catch { }
                    if (card == null) { Log.LogWarning("[card] mirror prefab has no FireMissionCard"); try { UnityEngine.Object.Destroy(inst); } catch { } return; }

                    card.Apply(d, b, e, p, s, g);
                    _applied++;
                    Log.LogInfo($"[card] applied fire-mission card <- peer (dist='{d}' bear='{b}' gun='{g}')");
                }
                finally { _applying = false; }
            }
            catch (Exception ex) { Log.LogWarning("[card] spawn mirror: " + ex.Message); }
        }

        private static FireMissionCardPrinter FindPrinter()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<FireMissionCardPrinter>(), FindObjectsSortMode.None);
                if (arr != null && arr.Length > 0) return arr[0].TryCast<FireMissionCardPrinter>();
            }
            catch { }
            return null;
        }

        // ---------------- diagnostics ----------------

        public static string Status() => $"cards: sent={_sent} applied={_applied}";

        // ---------------- helpers ----------------

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(512); return true; }
            catch (Exception e) { Log.LogWarning("[card] buf: " + e.Message); return false; }
        }

    }
}
