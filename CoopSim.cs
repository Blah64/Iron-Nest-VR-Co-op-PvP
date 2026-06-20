using System;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op FOUNDATION: host-authoritative SPAWN gating (NARROW gate).
    ///
    /// The mission sim is a SleepyNodes node-graph state machine. We want both players to run their OWN local
    /// machinery during a mission — gun, reload/ammo, objectives, scoring, animations, teleprinter orders — so
    /// those "just work" on the client from the replicated inputs/entities instead of having to enumerate and
    /// replicate every field by hand. The ONLY thing that must stay host-authoritative is content the host
    /// AUTHORS with its own RNG/placement: enemy/target SPAWNS. If the client also generated them it would
    /// double-spawn with a different RNG → desync.
    ///
    /// HISTORY:
    ///   • v1 (replaced): gate ONLY the spawn node — but at the time the client also had no scene bootstrap, so
    ///     the test stalled. Mislearned as "gate everything."
    ///   • v2 (replaced): FULL gate — Harmony-suppress <c>MissionGraph</c>/<c>MissionPassiveGraph</c> Run+Update
    ///     on the client. That made the client a PURE VIEWER: it ran NONE of the mission sim, so gun/reload/AMMO/
    ///     objectives were all dead and only explicitly-replicated state appeared. Co-op completeness became
    ///     whack-a-mole (ammo empty, etc.). See the coop-mod-sourcemap memory.
    ///   • v3 (CURRENT, per user direction 2026-06-20): NARROW gate — suppress ONLY the spawn node's action
    ///     (<c>State_SpawnMapEntity.OnEnter</c>) on a co-op client. The rest of the mission graph runs normally,
    ///     so the client drives its own gun/reload/ammo/objectives/teleprinter LOCALLY. Enemies/targets are
    ///     mirrored from the host via <see cref="CoopEntities"/>. This matches the working reference co-op mod
    ///     (github.com/not-so-sure/iron-nest-coop), which gates exactly <c>State_SpawnMapEntity.OnEnter</c> and
    ///     nothing else in the graph. More extensive gating for a future PvP mode can be layered on later.
    ///
    /// Why ONLY the one node:
    ///   - The graph DRIVERS (Run/Update) are NOT gated, so node transitions still fire — we skip the spawn
    ///     node's ACTION, not the graph's progression (the node still advances via its <c>To</c>/OnExecute).
    ///   - Other spawn-ish nodes (<c>State_SpawnScoutPlane</c>) and entity-movers (<c>State_MoveMapEntity</c>)
    ///     are deliberately LEFT RUNNING on the client: CoopEntities is keyed by the MapEntity string ID and
    ///     ADOPTS a same-ID local entity instead of cloning (so a client-spawned-then-host-replicated object
    ///     resolves to ONE entity), and the host's authoritative MSG_UPDATE/MSG_MOVE corrects any drift. Gating
    ///     a node whose product ISN'T replicated would make that object MISSING on the client — worse than a
    ///     little redundant local work. If a specific node ever double-produces, gate THAT node here too.
    ///   - The teleprinter (<c>State_TeleprinterText</c>) is NOT gated: its node can wait on print completion
    ///     (<c>WaitUntilComplete</c>), so suppressing its OnEnter risks stalling the client's graph. Instead the
    ///     client prints its own orders locally (entities are replicated, so {grid}/{bearing} tokens resolve to
    ///     the same values). Order REPLICATION (CoopOrders) is therefore off — see Config.CoopOrdersSync.
    ///
    /// Gating is active ONLY for a co-op CLIENT in a lobby (never solo, never the host). There is no in-engine
    /// authority/paused flag (verified by decompile), so a Harmony prefix is the only lever.
    /// </summary>
    internal static class CoopSim
    {
        private static ManualLogSource Log => Plugin.Logger;

        private static Harmony _harmony;
        private static int _patched;
        private static float _nextGateLog;

        // Called once from Plugin.Load. The single spawn-node target is patched in its own try/catch so a
        // missing/renamed method can't take down the rest of startup; we log whether it landed so a tester's
        // log proves the gate is installed before any mission even starts.
        public static void ApplyPatches()
        {
            try { _harmony = new Harmony("com.ironnest.vr.sim"); }
            catch (Exception e) { Log.LogError("[sim] Harmony init failed: " + e); return; }

            // NARROW gate: suppress ONLY the enemy/target spawn node's action on a co-op client. The graph keeps
            // running (transitions still fire), so the client's own gun/reload/ammo/objectives/teleprinter run
            // locally; enemies are mirrored from the host via CoopEntities (host-authoritative spawns/RNG).
            _patched = TryPatch(typeof(SleepyNodes.State_SpawnMapEntity), "OnEnter");
            Log.LogInfo($"[sim] host-authoritative SPAWN gate (narrow): {_patched}/1 spawn-node method patched " +
                        "(client runs its own mission machinery; only spawns are host-authoritative; host + solo run normally)");

            // NOTE: under the narrow gate the client's teleprinter node runs locally and prints its own orders,
            // so we no longer capture+replay them (that would double-print). CoopOrders stays in the tree, dormant
            // behind Config.CoopOrdersSync (default OFF). To re-enable order replication you must ALSO suppress the
            // client's own teleprinter output WITHOUT stalling its graph (State_TeleprinterText.WaitUntilComplete) —
            // a non-trivial follow-up, only worth it if the locally-resolved order text is observed to diverge.
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

        // Harmony prefix on the spawn node's OnEnter. Returning false skips the original, so a co-op client never
        // runs the spawn action locally (it mirrors the host's entities instead). The graph still advances.
        public static bool GatePrefix()
        {
            // CRITICAL: this prefix runs on EVERY spawn-node entry for host AND client. It must NEVER throw — a
            // throw here would propagate into the game's mission state machine and break the mission for everyone.
            // Any error → behave as "don't gate" (run the original), the safe default.
            try { if (!ShouldGate()) return true; }   // run original (solo or host, or on any error)
            catch { return true; }
            float t = Time.unscaledTime;
            if (t >= _nextGateLog) { _nextGateLog = t + 1f; Log.LogInfo("[sim] client SPAWN-gate ACTIVE — host authors spawns; client mirrors them (its own gun/reload/ammo run locally)"); }
            return false;                     // skip original (client) — no local spawn
        }

        // Gate only for a co-op CLIENT actually connected to a peer. Solo play and the host are never gated.
        // (No mission-phase check needed: the spawn node only runs during a mission anyway.)
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
            return $"sim: phase={phase} entities={ents} spawn-gate={(ShouldGate() ? "ON" : "off")} patched={_patched}/1 role={role}";
        }
    }
}
