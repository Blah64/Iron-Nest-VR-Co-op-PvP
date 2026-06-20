using System;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op FOUNDATION: host-authoritative CONTENT gating.
    ///
    /// The mission sim is a SleepyNodes node-graph state machine. The host is authoritative for the content we
    /// replicate (enemy/target spawns, teleprinter orders). If the client ALSO generated that content it would
    /// double-spawn / double-print with its own RNG → desync.
    ///
    /// EARLIER APPROACH (replaced): gate the whole <c>MissionGraph</c>/<c>MissionPassiveGraph</c> Run+Update on
    /// the client. That froze the client's mission into a husk — the fire-mission canvas / scene structure never
    /// bootstrapped, so a mirrored entity had no live map to live in (the first 2-player mission test: client
    /// reached the scene but the 9 host entities had nowhere to clone into). The working reference co-op mod
    /// (github.com/not-so-sure/iron-nest-coop) gates only the SPAWN NODE, leaving the rest of the mission alive.
    ///
    /// CURRENT APPROACH (per project direction — full gate, cleaner for a future PvP mode): a Harmony prefix
    /// returns false (skip original) on a co-op CLIENT for the whole per-mission graph — <c>MissionGraph</c> /
    /// <c>MissionPassiveGraph</c> <c>Run</c>+<c>Update</c>. The client is a PURE VIEWER: it runs none of the
    /// mission sim and instead MIRRORS everything the host produces (entities via <see cref="CoopEntities"/>,
    /// orders via <see cref="CoopOrders"/>, map objects/lines via <see cref="CoopMap"/>). Anything the client
    /// must SHOW therefore has to be replicated + spawned client-side from host data — if something doesn't
    /// appear, the replication for it is missing, not the gate. Those graph methods only run during a mission,
    /// so the hub/map/menu are untouched.
    ///
    /// Gating is active ONLY for a co-op CLIENT in a lobby (never solo, never the host). There is no in-engine
    /// authority/paused flag (verified by decompile), so a Harmony patch is the only lever.
    /// </summary>
    internal static class CoopSim
    {
        private static ManualLogSource Log => Plugin.Logger;

        private static Harmony _harmony;
        private static int _patched;
        private static float _nextGateLog;

        // Called once from Plugin.Load. Each target is patched in its own try/catch so one missing/renamed
        // method can't stop the others, and we log exactly how many landed (a tester's log then proves the
        // gate is installed before any mission even starts).
        public static void ApplyPatches()
        {
            try { _harmony = new Harmony("com.ironnest.vr.sim"); }
            catch (Exception e) { Log.LogError("[sim] Harmony init failed: " + e); return; }

            _patched = 0;
            _patched += TryPatch(typeof(SleepyNodes.MissionGraph), "Run");
            _patched += TryPatch(typeof(SleepyNodes.MissionGraph), "Update");
            _patched += TryPatch(typeof(SleepyNodes.MissionPassiveGraph), "Run");
            _patched += TryPatch(typeof(SleepyNodes.MissionPassiveGraph), "Update");
            Log.LogInfo($"[sim] host-authoritative sim-gate: {_patched}/4 mission-graph methods patched (client = pure viewer; host + solo run normally)");

            // Capture teleprinter ORDERS host-side via a postfix on the submit funnel; the client's graph is fully
            // gated, so it never prints locally — CoopOrders replays the host's resolved text.
            try
            {
                var mi = AccessTools.Method(typeof(Teleprinter), "SubmitLines");
                if (mi != null) { _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(CoopOrders), nameof(CoopOrders.OnSubmitLines))); Log.LogInfo("[sim] teleprinter-order capture patched (Teleprinter.SubmitLines)"); }
                else Log.LogWarning("[sim] Teleprinter.SubmitLines not found — orders won't sync");
            }
            catch (Exception e) { Log.LogWarning("[sim] teleprinter patch: " + e.Message); }
        }

        private static int TryPatch(Type t, string method)
        {
            try
            {
                var mi = AccessTools.Method(t, method);
                if (mi == null) { Log.LogWarning($"[sim] gate target not found: {t.Name}.{method}"); return 0; }
                _harmony.Patch(mi, prefix: new HarmonyMethod(typeof(CoopSim), nameof(GatePrefix)));
                return 1;
            }
            catch (Exception e) { Log.LogWarning($"[sim] patch {t.Name}.{method} failed: " + e.Message); return 0; }
        }

        // Harmony prefix shared by the gated graph methods. Returning false skips the original, so a co-op client
        // never runs the mission sim locally (it mirrors the host instead).
        public static bool GatePrefix()
        {
            // CRITICAL: this prefix runs on EVERY mission-graph tick for host AND client. It must NEVER throw —
            // a throw here would propagate into the game's mission state machine and break the mission for everyone.
            // Any error → behave as "don't gate" (run the original), the safe default.
            try { if (!ShouldGate()) return true; }   // run original (solo or host, or on any error)
            catch { return true; }
            float t = Time.unscaledTime;
            if (t >= _nextGateLog) { _nextGateLog = t + 1f; Log.LogInfo("[sim] client sim-gate ACTIVE — client is a pure viewer (host is authoritative)"); }
            return false;                     // skip original (client)
        }

        // Gate only for a co-op CLIENT actually connected to a peer. Solo play and the host are never gated.
        // (No mission-phase check needed: the patched nodes only run during a mission anyway.)
        public static bool ShouldGate()
        {
            if (!Config.CoopSimAuthority) return false;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) return false;
            return !CoopP2P.IsHost;
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            string phase = "n/a";
            try { var mm = MissionManager.Instance; if (mm != null) phase = mm.CurrentPhase.ToString(); } catch { }
            int ents = -1;
            try { var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None); ents = arr != null ? arr.Length : 0; } catch { }
            string role = CoopP2P.IsHost ? "HOST" : (CoopP2P.HasPeer ? "client" : "solo");
            return $"sim: phase={phase} entities={ents} gate={(ShouldGate() ? "ON" : "off")} patched={_patched}/4 role={role}";
        }
    }
}
