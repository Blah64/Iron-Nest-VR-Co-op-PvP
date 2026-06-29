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
#if !PUBLIC_BUILD
        private static float _nextCardNodeLog;   // throttle the card-gated node allow/suppress trace (dev builds only)
#endif

        // Called once from Plugin.Load. The single spawn-node target is patched in its own try/catch so a
        // missing/renamed method can't take down the rest of startup; we log whether it landed so a tester's
        // log proves the gate is installed before any mission even starts.
        public static void ApplyPatches()
        {
            try { _harmony = new Harmony("com.ironnest.vr.sim"); }
            catch (Exception e) { Log.LogError("[sim] Harmony init failed: " + e); return; }

            // NARROW gate: suppress ONLY the enemy/target spawn node's action on a co-op client. The graph keeps
            // running (transitions still fire), so the client's own gun/reload/ammo/objectives run locally;
            // enemies are mirrored from the host via CoopEntities (host-authoritative spawns/RNG).
            _patched = TryPatch(typeof(SleepyNodes.State_SpawnMapEntity), "OnEnter");
            Log.LogInfo($"[sim] host-authoritative SPAWN gate (narrow): {_patched}/1 spawn-node method patched " +
                        "(client runs its own mission machinery; only spawns are host-authoritative; host + solo run normally)");

            // PvP BARE ARENA (PLAN-pvp.md Appendix A): suppress scripted PvE content on BOTH machines while PvpActive,
            // so a duel scene is just map + artillery + the two players. These prefixes are inert unless PvpActive, so
            // co-op + solo are untouched. State_SpawnMapEntity is already covered (GatePrefix gained a PvpActive branch).
            // Content/entity nodes to neutralize in a PvP bare arena. State_DamageEntity is CRITICAL: the engine's
            // own State_ImpactStart.StartImpact INVOKES State_DamageEntity.OnEnter when a shell lands on a registered
            // entity (our player mirror), and it NREs on a programmatically-spawned mirror (no mission-graph damage
            // wiring). That thrown exception aborts StartImpact, so our PvP impact POSTFIX never runs — every close/
            // on-target shot silently failed to adjudicate. We do victim-authoritative damage ourselves, so skipping
            // the engine's damage node is exactly right. SetEntityState/MoveMapEntity are the sibling entity-mutators
            // that would NRE the same way. All inert unless PvpActive (PvpSuppressPrefix returns true to run original).
            // State_MoveTurret/State_SetTurretLocation are suppressed too so the mission can't relocate the player's
            // turret — PvpPlayers.PlaceMyTurret pins each turret to a deterministic grid at match start (these gate the
            // mission NODES, not TurretController.SetTurretLocation the METHOD, so our own placement + the player's
            // manual map-move still work).
            // State_StartTimer / State_UnpauseTimer are the SCRIPTED counter-battery arming on the FDC "Challenging"
            // mission: the graph runs State_StartTimer (gated behind "player fired") → CounterBatteryTimer starts →
            // the scene's wired klaxon/lights fire (onTimerStarted) on whoever fired (the host). Gating the NODE kills
            // the scripted counter-battery on BOTH machines at the source; PvpMatch then drives the timer DIRECTLY
            // (TurretController-independent) as the VICTIM's hit effect. We skip the node's action; the graph advances.
            //
            // HARD-SUPPRESSED (always skip in PvP): scripted PvE content + NRE sources. State_DamageEntity is the critical
            // one — the engine's StartImpact INVOKES it on our mirror and it NREs (aborting StartImpact so our hit postfix
            // never runs); SetEntityState/MoveMapEntity NRE the same way; StartTimer/UnpauseTimer = the scripted klaxon.
            // None of these is ever needed by a player ability, so a blanket skip is correct.
            foreach (var nodeName in new[] { "State_DamageEntity", "State_SetEntityState", "State_MoveMapEntity", "State_StartTimer", "State_UnpauseTimer" })
                PatchPvpNode(nodeName, nameof(PvpSuppressPrefix), "hard-suppress");

            // CARD-GATED (skip in PvP UNLESS a requisition card is driving): these are the PLAYER's two requisition-card
            // abilities — RELOCATE the turret (State_MoveTurret / State_SetTurretLocation) and RECON the enemy
            // (State_SpawnScoutPlane). A bare arena must not let the mission script fire them, but a player who redeems the
            // matching card MUST be able to. PvpCardGatedSuppressPrefix runs the node only while a card graph is in flight
            // (PvpMatch.CardGraphActive, armed by CoopPunchcards.OnAttemptRequisition), and suppresses it otherwise. This
            // is what un-breaks "move the turret by requisition card" and "recon to spot the enemy" in PvP.
            foreach (var nodeName in new[] { "State_MoveTurret", "State_SetTurretLocation", "State_SpawnScoutPlane" })
                PatchPvpNode(nodeName, nameof(PvpCardGatedSuppressPrefix), "card-gated");

            // TELEPRINTER ORDERS are the ONE thing the client can't resolve locally: the gated spawn node is what
            // stashes the target in a graph context variable, so a client that skips it prints a BLANK order (the
            // 2026-06-20 "empty text prompt"). So the HOST captures every resolved Teleprinter.SubmitLines via a
            // POSTFIX and CoopOrders replays it on the client's matching printer. A PREFIX (SuppressLocalPrint)
            // blanks the client's OWN in-mission submits so its locally-adjudicated field reports (e.g. "MISS" on a
            // host-killed target) can't fight the host's replayed "HIT" — the host is the sole field-report source
            // on the client. We blank (not skip) so State_TeleprinterText still gets a completing PrintJob and can't
            // stall on WaitUntilComplete. Both gated by CoopOrdersSync; the replay sets CoopOrders.ApplyingRemote.
            try
            {
                var mi = AccessTools.Method(typeof(Teleprinter), "SubmitLines");
                if (mi != null)
                {
                    _harmony.Patch(mi,
                        prefix: new HarmonyMethod(typeof(CoopOrders), nameof(CoopOrders.SuppressLocalPrint)),
                        postfix: new HarmonyMethod(typeof(CoopOrders), nameof(CoopOrders.OnSubmitLines)));
                    Log.LogInfo("[sim] teleprinter-order capture + client-local suppression patched (Teleprinter.SubmitLines)");
                }
                else Log.LogWarning("[sim] Teleprinter.SubmitLines not found — orders won't sync");
            }
            catch (Exception e) { Log.LogWarning("[sim] teleprinter patch: " + e.Message); }

            // HARDENING: block a co-op CLIENT from starting an operation on its own (only the host's lifecycle
            // drives missions; the host-commanded start passes via CoopScene.ApplyingRemoteStart). Prefix returns
            // false to skip the original. Host + solo are never blocked. Decline politely if the method moved.
            try
            {
                var mi = AccessTools.Method(typeof(MissionManager), "StartOperation", new[] { typeof(SleepyNodes.OperationGraph), typeof(SleepyNodes.MissionGraph) })
                         ?? AccessTools.Method(typeof(MissionManager), "StartOperation");
                if (mi != null) { _harmony.Patch(mi, prefix: new HarmonyMethod(typeof(CoopScene), nameof(CoopScene.GateClientStart))); Log.LogInfo("[sim] client self-start guard patched (MissionManager.StartOperation)"); }
                else Log.LogWarning("[sim] MissionManager.StartOperation not found — client self-start not guarded");
            }
            catch (Exception e) { Log.LogWarning("[sim] self-start guard patch: " + e.Message); }

            // FIRE-MISSION CARD (PATH B, player-driven): capture the resolved 6-string firing solution so the peer
            // can mirror the printed card. Bidirectional + echo-guarded in CoopCards; gated by CoopCardSync.
            try
            {
                var mi = AccessTools.Method(typeof(FireMissionCard), "Apply");
                if (mi != null) { _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(CoopCards), nameof(CoopCards.OnCardApplied))); Log.LogInfo("[sim] fire-mission card capture patched (FireMissionCard.Apply)"); }
                else Log.LogWarning("[sim] FireMissionCard.Apply not found — cards won't sync");
            }
            catch (Exception e) { Log.LogWarning("[sim] card patch: " + e.Message); }

            // PUNCHCARD REDEMPTION (Layer 3, host-authoritative routing — gated by Config.CoopPunchcardRedeemSync).
            // The AttemptRequisition PREFIX is a pure pass-through for solo + host (so the slot behaves exactly like
            // stock — the earlier "prefix breaks the submit" was the base-game scout-plane rejection, which happens
            // solo where this prefix never acts). It only diverts a CLIENT-with-peer redemption to the host. The
            // OnCardUsed PRE/POST captures the real success (AttemptRequisition returns void) so the host broadcasts
            // an authoritative consume. See CoopPunchcards.
            try
            {
                var ar = AccessTools.Method(typeof(RequisitionSlot), "AttemptRequisition");
                if (ar != null) { _harmony.Patch(ar, prefix: new HarmonyMethod(typeof(CoopPunchcards), nameof(CoopPunchcards.OnAttemptRequisition))); Log.LogInfo("[sim] punchcard redeem routing patched (RequisitionSlot.AttemptRequisition prefix)"); }
                else Log.LogWarning("[sim] RequisitionSlot.AttemptRequisition not found — client punchcard redemptions won't route");

                var oc = AccessTools.Method(typeof(PunchcardRuntime), "OnCardUsed");
                if (oc != null) { _harmony.Patch(oc, prefix: new HarmonyMethod(typeof(CoopPunchcards), nameof(CoopPunchcards.OnCardUsedPre)), postfix: new HarmonyMethod(typeof(CoopPunchcards), nameof(CoopPunchcards.OnCardUsedPost))); Log.LogInfo("[sim] punchcard consume capture patched (PunchcardRuntime.OnCardUsed pre/post)"); }
                else Log.LogWarning("[sim] PunchcardRuntime.OnCardUsed not found — punchcard consume won't sync");

                // The validator's synchronous success/fail verdict — lets RunRedeem report the true outcome (point/use
                // changes are coroutine-delayed). Diagnostic only; no behaviour change.
                var ft = AccessTools.Method(typeof(RequisitionSlot), "FireRequisitionTrigger");
                if (ft != null) { _harmony.Patch(ft, postfix: new HarmonyMethod(typeof(CoopPunchcards), nameof(CoopPunchcards.OnRequisitionTrigger))); Log.LogInfo("[sim] punchcard redeem-verdict capture patched (RequisitionSlot.FireRequisitionTrigger)"); }
                else Log.LogWarning("[sim] RequisitionSlot.FireRequisitionTrigger not found — redeem success signal degraded");
            }
            catch (Exception e) { Log.LogWarning("[sim] punchcard redeem patch: " + e.Message); }

            // IMPACT RESULT (4c map hit-markers): capture the host's authoritative per-shell adjudication so the
            // client's ImpactIndicators light up on the map even though the client's own shell locally "missed" a
            // target the host already destroyed. Postfix reads the returned hit list; CoopImpact broadcasts HITS only.
            try
            {
                var mi = AccessTools.Method(typeof(SleepyNodes.State_ImpactStart), "StartImpact");
                // POSTFIX (CoopImpact): runs when the shell LANDS - broadcasts the host's authoritative hit set and
                // logs the map point (diagnostic vs the board target). No prefix: the visible landing is copied at fire
                // time via the ShellVisual hooks, not here (the map point doesn't exist until the shell lands).
                if (mi != null)
                {
                    _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(CoopImpact), nameof(CoopImpact.OnImpactAdjudicated)));
                    // PvP shot/damage lane shares the same adjudication surface: a 2nd postfix routes opponent-mirror
                    // hits to the victim (PvpActive only; bails otherwise). Co-op's postfix bails when PvpActive — no overlap.
                    _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(PvpCombat), nameof(PvpCombat.OnImpactAdjudicated)));
                    Log.LogInfo("[sim] impact-result capture patched (State_ImpactStart.StartImpact) + PvP hit lane");
                }
                else Log.LogWarning("[sim] State_ImpactStart.StartImpact not found — map hit-markers + PvP hits won't work");
            }
            catch (Exception e) { Log.LogWarning("[sim] impact patch: " + e.Message); }

            // DETERMINISTIC FIRING: GunController.FireShell applies RANDOM dispersion with no shared seed, so two
            // machines firing the SAME synced aim/powder/shell land in DIFFERENT spots. The prefix zeros the gun +
            // chambered-shell dispersion coefficients for the duration of the native call (postfix restores them),
            // on BOTH machines, only during a co-op mission — so the impact becomes a deterministic function of the
            // already-synced inputs and host + client land identically. Solo/flatscreen untouched. See CoopBallistics.
            try
            {
                var mi = AccessTools.Method(typeof(GunController), "FireShell");
                if (mi != null) { _harmony.Patch(mi, prefix: new HarmonyMethod(typeof(CoopBallistics), nameof(CoopBallistics.OnFireShellPre)), postfix: new HarmonyMethod(typeof(CoopBallistics), nameof(CoopBallistics.OnFireShellPost))); Log.LogInfo("[sim] deterministic-fire patched (GunController.FireShell — co-op zeros random dispersion)"); }
                else Log.LogWarning("[sim] GunController.FireShell not found — co-op shots will scatter independently");
            }
            catch (Exception e) { Log.LogWarning("[sim] deterministic-fire patch: " + e.Message); }

            // SHELL VISUAL COPY (postfix only): on a LOCAL shot the postfix reads the real flight (start/target/
            // travelTime) and announces it; on a REMOTE shot it overwrites our shell's flight FIELDS with the
            // shooter's so the whole arc + crater match. Field writes, NOT a ref-arg prefix - the latter silently
            // no-ops on IL2CPP (it's what made earlier logs match while screens didn't). Host + solo + local shots
            // are otherwise untouched.
            try
            {
                var mi = AccessTools.Method(typeof(ShellVisual), "Initialize");
                if (mi != null) { _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(CoopBallistics), nameof(CoopBallistics.OnShellVisualPost))); Log.LogInfo("[sim] shell-visual copy patched (ShellVisual.Initialize -> shooter flight)"); }
                else Log.LogWarning("[sim] ShellVisual.Initialize not found — co-op fall-of-shot visual won't copy");
            }
            catch (Exception e) { Log.LogWarning("[sim] shell-visual patch: " + e.Message); }

            // FIRE-INTENT TAGGING (Bug 2 fix): an OBSERVE-ONLY prefix on GunController.RequestFire tags every LOCAL shot
            // at fire time so CoopBallistics' per-side intent queue can tell a real local shot from a peer's replay (the
            // replay sets _replaying around its own RequestFire, so the hook skips it). PREFIX (not postfix) so CanFire
            // is read BEFORE the call flips hasFired. Returns void — it can NEVER skip the original, so PvP/solo fire is
            // untouched (NoteLocalRequestFire is inert unless CoopBallistics.Active()).
            try
            {
                var mi = AccessTools.Method(typeof(GunController), "RequestFire");
                if (mi != null) { _harmony.Patch(mi, prefix: new HarmonyMethod(typeof(CoopSim), nameof(OnLocalRequestFirePre))); Log.LogInfo("[sim] local-fire intent hook patched (GunController.RequestFire prefix, observe-only)"); }
                else Log.LogWarning("[sim] GunController.RequestFire not found — local-fire intent tagging off (Bug 2 fix degraded)");
            }
            catch (Exception e) { Log.LogWarning("[sim] requestfire hook: " + e.Message); }

            // PvP ACQUISITION (scout-plane recon): the enemy turret mirror is fog-hidden until a SCOUT PLANE photographs it.
            // When a scout-plane card launches its photo run (State_SpawnScoutPlane.OnEnter, card-gated above), a POSTFIX
            // → PvpPlayers.OnScoutPlanePhoto reveals only the enemy mirrors INSIDE the photo footprint (near the player's
            // dialed recon target), not a blanket reveal. The reveal is OURS (we own the mirror's visibility) so it doesn't
            // depend on a mod-spawned clone participating in the native fog system. The FORWARD OBSERVER uses a different
            // node (State_SpawnMapEntity) so it never reaches here — it correctly does NOT reveal the map, only the
            // teleprinter, exactly like base game. (The earlier MapReconClearer.ClearAll/DestroyAll hooks never fired —
            // the plane doesn't drive them — so they're replaced by this node postfix.)
            try
            {
                var sp = AccessTools.Method(AccessTools.TypeByName("SleepyNodes.State_SpawnScoutPlane"), "OnEnter");
                if (sp != null) { _harmony.Patch(sp, postfix: new HarmonyMethod(typeof(PvpPlayers), nameof(PvpPlayers.OnScoutPlanePhoto))); Log.LogInfo("[sim] PvP scout-plane recon patched (State_SpawnScoutPlane.OnEnter postfix — arms the footprint watch)"); }
                else Log.LogWarning("[sim] State_SpawnScoutPlane.OnEnter not found — scout-plane recon reveal off");
            }
            catch (Exception e) { Log.LogWarning("[sim] scout-plane recon patch: " + e.Message); }

            // The scout photo lands LATE (the plane flies, then the photo "develops" and a MapReconClearer.Register(handle)
            // adds the REVEALED REGION — that handle's fog-tile children ARE the exact photographed footprint). Capturing it
            // event-driven (vs polling) gives the precise region the instant it appears. PvpPlayers gates it to the scout
            // window armed by State_SpawnScoutPlane above, so a forward observer (no plane, no scout window) never reveals.
            try
            {
                var reg = AccessTools.Method(AccessTools.TypeByName("MapReconClearer"), "Register");
                if (reg != null) { _harmony.Patch(reg, postfix: new HarmonyMethod(typeof(PvpPlayers), nameof(PvpPlayers.OnReconRegionRegistered))); Log.LogInfo("[sim] PvP recon-region patched (MapReconClearer.Register postfix — captures the scout's photo footprint)"); }
                else Log.LogWarning("[sim] MapReconClearer.Register not found — scout footprint falls back to dialed-circle reveal");
            }
            catch (Exception e) { Log.LogWarning("[sim] recon-region patch: " + e.Message); }

            // SCORE / OUTCOME: the host replays mission complete/fail onto the client so the result screens match.
            // CoopScore broadcasts (host-only); the client's replay re-hits these postfixes but bails on !IsHost.
            try
            {
                var c = AccessTools.Method(typeof(MissionManager), "MarkMissionComplete");
                if (c != null) { _harmony.Patch(c, postfix: new HarmonyMethod(typeof(CoopScore), nameof(CoopScore.OnMissionComplete))); }
                var f = AccessTools.Method(typeof(MissionManager), "MarkMissionFailed");
                if (f != null) { _harmony.Patch(f, postfix: new HarmonyMethod(typeof(CoopScore), nameof(CoopScore.OnMissionFailed))); }
                Log.LogInfo($"[sim] mission-outcome capture patched (complete={(c != null)} failed={(f != null)})");
            }
            catch (Exception e) { Log.LogWarning("[sim] outcome patch: " + e.Message); }
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
            // PvP: a duel arena spawns NO scripted enemies/targets on EITHER machine (PvpPlayers spawns the players) —
            // EXCEPT a player's requisition CARD legitimately spawns its OWN native recon asset (the FORWARD OBSERVER
            // card spawns a spotter unit via this node, which then reveals + reports to the teleprinter like base game).
            // So allow the spawn while a card graph is in flight (PvpMatch.CardGraphActive), suppress mission-scripted
            // spawns otherwise. Safe to card-gate: unlike the entity-MUTATOR nodes (Damage/SetState/Move), the spawn node
            // is never invoked by StartImpact, so allowing it can't re-introduce the impact-adjudication NRE.
            try
            {
                if (Config.PvpActive)
                {
                    if (PvpMatch.CardGraphActive())
                    {
#if !PUBLIC_BUILD
                        float tc = Time.unscaledTime; if (tc >= _nextCardNodeLog) { _nextCardNodeLog = tc + 0.5f; Log.LogInfo("[sim] PvP spawn ALLOWED (requisition card driving — e.g. forward observer's spotter)"); }
#endif
                        return true;   // run the spawn — it's the card's own asset
                    }
                    float tp = Time.unscaledTime; if (tp >= _nextGateLog) { _nextGateLog = tp + 1f; Log.LogInfo("[sim] PvP SPAWN-gate ACTIVE — bare arena, no scripted spawns on either machine"); }
                    return false;
                }
            }
            catch { }
            try { if (!ShouldGate()) return true; }   // run original (solo or host, or on any error)
            catch { return true; }
            float t = Time.unscaledTime;
            if (t >= _nextGateLog) { _nextGateLog = t + 1f; Log.LogInfo("[sim] client SPAWN-gate ACTIVE — host authors spawns; client mirrors them (its own gun/reload/ammo run locally)"); }
            return false;                     // skip original (client) — no local spawn
        }

        // PvP-only node suppressor: skip the patched node ONLY in a PvP arena (returns false). Otherwise run the
        // original (returns true), so co-op + solo are unaffected. Never throws into the graph. Used for content
        // nodes a bare PvP arena must never run (damage/state/scripted-timer).
        public static bool PvpSuppressPrefix() { try { return !Config.PvpActive; } catch { return true; } }

        // PvP CARD-GATED node suppressor: in a PvP arena, run the node ONLY while a requisition card graph is driving it
        // (PvpMatch.CardGraphActive — armed when the local player pulls the requisition lever); suppress it otherwise so
        // the mission script can't fire it in the bare arena. Co-op + solo always run the original. Never throws.
        public static bool PvpCardGatedSuppressPrefix()
        {
            try
            {
                if (!Config.PvpActive) return true;            // co-op / solo unaffected
                bool allow = PvpMatch.CardGraphActive();
#if !PUBLIC_BUILD
                float t = Time.unscaledTime;
                if (t >= _nextCardNodeLog) { _nextCardNodeLog = t + 0.5f; Log.LogInfo($"[sim] PvP card-gated node OnEnter -> {(allow ? "ALLOWED (a requisition card is driving)" : "suppressed (no card in flight)")}"); }
#endif
                return allow;
            }
            catch { return true; }
        }

        // Patch one SleepyNodes.<name>.OnEnter with the named PvP prefix (resolved by reflection so a renamed node can't
        // take down startup). Shared by the hard-suppress and card-gated lists.
        private static void PatchPvpNode(string nodeName, string prefixName, string label)
        {
            try
            {
                var t = AccessTools.TypeByName("SleepyNodes." + nodeName);
                var mi = t != null ? AccessTools.Method(t, "OnEnter") : null;
                if (mi != null) { _harmony.Patch(mi, prefix: new HarmonyMethod(typeof(CoopSim), prefixName)); Log.LogInfo($"[sim] PvP {label} suppressor patched ({nodeName}.OnEnter — inert unless PvpActive)"); }
                else Log.LogWarning($"[sim] SleepyNodes.{nodeName}.OnEnter not found — PvP {label} for it off");
            }
            catch (Exception e) { Log.LogWarning($"[sim] {nodeName} patch: " + e.Message); }
        }

        // OBSERVE-ONLY prefix on GunController.RequestFire (Bug 2 fix). Tags a local shot in CoopBallistics' per-side
        // intent queue. Returns void -> never skips the original, so it cannot affect PvP/solo firing; inert unless
        // co-op fire is Active. Must never throw into the gun state machine.
        public static void OnLocalRequestFirePre(GunController __instance)
        {
            try { CoopBallistics.NoteLocalRequestFire(__instance); } catch { }
        }

        // Gate only for a co-op CLIENT actually connected to a peer. Solo play and the host are never gated.
        // (No mission-phase check needed: the spawn node only runs during a mission anyway.)
        public static bool ShouldGate()
        {
            if (Config.PvpActive) return false;   // co-op's client spawn-gate doesn't apply in PvP (PvP sim-gating is separate, later phase)
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
