using System;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op FOUNDATION: host-authoritative simulation gating.
    ///
    /// The whole game sim is a SleepyNodes node-graph state machine. If BOTH players run it, the client
    /// double-spawns enemies (and RNG-located spawns land in different places → desync). So the HOST runs the
    /// mission graph (spawns, damage, objectives, score) and is the single source of truth; the CLIENT's copy
    /// is GATED OFF here and instead mirrors the host's world via <see cref="CoopEntities"/> (next increment).
    ///
    /// HOW: a Harmony prefix on the per-mission graph drivers returns false (skip original) when this instance
    /// is a co-op CLIENT. We gate the GRAPH methods — <c>MissionGraph</c> / <c>MissionPassiveGraph</c>
    /// <c>Run</c> (bootstrap) + <c>Update</c> (per-frame advance) — NOT <c>MissionManager.Update</c>. That's
    /// deliberate: those graph methods only ever execute while a mission is actually running, so gating them
    /// can NEVER affect the hub / map / menu (where no MissionGraph is active) — the already-validated hub
    /// co-op is untouched. MissionManager.Update, by contrast, also runs in the hub and could break it.
    ///
    /// Gating is active ONLY for a co-op CLIENT in a lobby (never solo, never the host). There is no in-engine
    /// authority/paused flag (verified by decompile), so a Harmony patch is the only lever.
    ///
    /// SCOPE NOTE: this covers the mission sim where enemy spawns/damage live (State_SpawnMapEntity etc. run as
    /// MissionGraph nodes). If a later test shows spawns leaking from ObjectiveGraph/OperationGraph, add those
    /// graph types to <see cref="ApplyPatches"/>. The host side and entity replication are separate increments.
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
            Log.LogInfo($"[sim] host-authoritative sim-gate: {_patched}/4 mission-graph methods patched (client suppresses these; host + solo run normally)");
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

        // Harmony prefix shared by all gated graph methods. Returning false skips the original (the node
        // advance / bootstrap), so the client's mission graph never executes.
        public static bool GatePrefix()
        {
            if (!ShouldGate()) return true;   // run original (solo or host)
            float t = Time.unscaledTime;
            if (t >= _nextGateLog) { _nextGateLog = t + 1f; Log.LogInfo("[sim] client sim-gate ACTIVE — suppressing local mission-graph execution (host is authoritative)"); }
            return false;                     // skip original (client)
        }

        // Gate the sim only for a co-op CLIENT actually connected to a peer. Solo play and the host are never
        // gated. (No mission-phase check is needed: the patched methods only run during a mission anyway.)
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
