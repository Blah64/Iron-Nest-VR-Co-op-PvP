using System;
using BepInEx.Logging;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Co-op diagnostics hub. The earlier one-time investigation dumps (player-rig / body-mesh / spawn-point /
    /// cockpit-frame probe, control-surface + broker enumeration, watch/manual/tutorial dumps, canvas render
    /// survey) did their job designing the netcode and have been removed. What's left is tuned for the CURRENT
    /// 2-player test so one build tells testers everything:
    ///   • a build banner (so a tester can confirm they're on the co-op build) — once;
    ///   • a peer-connect/disconnect banner + an immediate full dump on connect;
    ///   • a periodic STATUS block while in a lobby (connection, packet counts, per-subsystem ownership);
    ///   • an on-demand FULL DUMP on F4 (flatscreen tester: "press F4 and send me the log").
    /// All other per-event breadcrumbs ([ctrl]/[map]/[clip]/[p2p] grab/click/fire/place/section lines) live in
    /// their own modules. Pure logging — no behaviour change.
    /// </summary>
    internal static class Diagnostics
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Verbose co-op breadcrumb. Per-EVENT replication logs (fire/powder/aim/click/grab) route through here so a
        // PUBLIC build stays quiet (Config.CoopVerboseLog defaults off there; the cfg key forces it on for a tester).
        // One-time lifecycle logs and warnings call Log.LogInfo/LogWarning directly and are NOT gated.
        internal static void V(string msg) { if (Config.CoopVerboseLog) { try { Log.LogInfo(msg); } catch { } } }

        private static bool _banner;
        private static bool _hadPeer;
        private static float _next;

        public static void Tick()
        {
            try
            {
                if (KeyDown(UnityEngine.InputSystem.Key.F4)) DumpAll("F4");

                if (!_banner)
                {
                    _banner = true;
                    Log.LogInfo("[coop] === IronNest VR co-op build — sync: controls(drag+click) + gun-fire + clipboard + map(tokens+markers) + join-in-progress + sim-gate + entities + scene-transition + orders + cards + score(P4) ===");
                    Log.LogInfo("[coop] keys: F4 full co-op dump | F6 avatar self-test | F7 flat lobby | F8 recenter | F9-12 lobby create/refresh/join/leave");
                }

                if (!SteamNet.Ready) return;

                bool hasPeer = CoopP2P.HasPeer;
                if (hasPeer && !_hadPeer)
                {
                    Log.LogInfo($"[coop] >>> PEER CONNECTED — this instance is {(CoopP2P.IsHost ? "HOST" : "CLIENT")} <<<");
                    DumpAll("peer-connect");
                }
                else if (!hasPeer && _hadPeer) Log.LogInfo("[coop] <<< peer disconnected >>>");
                _hadPeer = hasPeer;

                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + Config.CoopDiagIntervalSec;
                if (!SteamNet.InLobby) return;   // stay quiet during solo play
                if (!Config.CoopVerboseLog) return;   // public build: no periodic status flood (cfg key can force it on)

                Log.LogInfo("[coop] --- status ---");
                Log.LogInfo("[coop] " + CoopP2P.Status());
                Log.LogInfo("[coop] " + CoopControls.Status());
                Log.LogInfo("[coop] " + CoopMap.Status());
                Log.LogInfo("[coop] " + CoopClipboard.Status());
                Log.LogInfo("[coop] " + CoopSim.Status());
                Log.LogInfo("[coop] " + CoopEntities.Status());
                Log.LogInfo("[coop] " + CoopScene.Status());
                Log.LogInfo("[coop] " + CoopOrders.Status());
                Log.LogInfo("[coop] " + CoopCards.Status());
                Log.LogInfo("[coop] " + CoopScore.Status());
                Log.LogInfo("[coop] " + CoopImpact.Status());
                Log.LogInfo("[coop] " + CoopBallistics.Status());
                Log.LogInfo("[coop] " + CoopPunchcards.Status());
                Log.LogInfo("[coop] " + CoopNetDiag.Status());
                if (Config.CoopNetSim) Log.LogInfo("[coop] " + CoopNetSim.Status());
                if (Config.PvpActive) { Log.LogInfo("[coop] " + PvpMatch.Status()); Log.LogInfo("[coop] " + PvpPlayers.Status()); Log.LogInfo("[coop] " + PvpCombat.Status()); }
            }
            catch (Exception e) { Log.LogWarning("[coop-diag] " + e.Message); }
        }

        private static void DumpAll(string reason)
        {
            try
            {
                Log.LogInfo($"[coop] ===== FULL CO-OP DUMP ({reason}) =====");
                Log.LogInfo("[coop] " + CoopP2P.Status());
                Log.LogInfo("[coop] " + CoopSim.Status());
                CoopControls.Dump();
                CoopMap.Dump();
                CoopClipboard.Dump();
                CoopEntities.Dump();
                Log.LogInfo("[coop] ===== end dump =====");
            }
            catch (Exception e) { Log.LogWarning("[coop-diag] dump: " + e.Message); }
        }

        private static bool KeyDown(UnityEngine.InputSystem.Key k)
        { try { var kb = UnityEngine.InputSystem.Keyboard.current; return kb != null && kb[k].wasPressedThisFrame; } catch { return false; } }
    }
}
