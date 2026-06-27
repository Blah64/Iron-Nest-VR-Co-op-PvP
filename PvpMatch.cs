using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP Phase 1/2 — MATCH COORDINATOR + LAUNCH. Owns the PvP-mode lifecycle:
    ///   • MODE is derived in SteamNet from the lobby's invr_mode tag into Config.PvpActive (PLAN-pvp.md §1a).
    ///   • LAUNCH: the HOST starts the duel arena from the lobby (it's not on the demo board, so we resolve the
    ///     combat MissionGraph/OperationGraph by scene name and call MissionManager.StartOperation directly). The
    ///     existing CoopScene host→client replication then carries every member into the SAME scene together — so
    ///     nobody loads the arena alone and runs its clock ahead of the others.
    ///   • BARE ARENA: the scripted PvE content is suppressed so a duel scene is just map + artillery + the two
    ///     players — spawn/scout nodes via CoopSim's PvP prefixes (installed at load, before any scene boots, so no
    ///     script fires early), and the counter-battery system (scripted incoming fire + its timer-expiry auto-fail)
    ///     disabled here once the arena scene is live.
    ///
    /// Co-op is fully isolated: when PvpActive the conflicting co-op replication self-disables (Appendix B guards).
    /// Match state (rounds, win/lose) and the shot/damage lane grow here + in PvpCombat in later phases.
    /// </summary>
    internal static class PvpMatch
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Candidate arena scenes (demo combat maps). The first that resolves to a MissionGraph is launched.
        private static readonly string[] ArenaSceneHints = { "chill", "challenging" };

        private static bool _wasActive;
        private static bool _cbDisabled;       // counter-battery suppressed for the current arena
        private static float _nextCbSweep;

        // Match-end flow: after a result is declared, hold the banner briefly, then the HOST returns everyone to the
        // operations map (CoopScene replicates the phase change) so teams unlock and players can re-launch.
        private static float _matchEndReturnAt;   // when to fire the return (0 = not armed)
        private const float MatchEndReturnSec = 6f;

        // The base game's counter-battery CINEMATIC IMPACT (explosion VFX + sound) reused as the PvP "an opponent's
        // round landed on us" hit effect. We disable the scripted barrage (timer + auto-fail) but keep this prefab;
        // cached from the spawner in DisableCounterBattery, instantiated on each team hit. The spawner's transform is
        // where the base game lands incoming fire = near the player, so it's the natural anchor. Cleared per match.
        private static GameObject _impactPrefab;
        private static Transform _impactAnchor;
        private static float _impactYOffset;
        private static int _fxSeq;

        // Deferred host launch — route through the operations map (BrowsingMap) before StartOperation so the player
        // rig initializes (a direct MainMenu->Mission start leaves the host's CharacterController inactive = "can't
        // move"; the client already worked because it reaches the mission VIA the map through replicated phases).
        private static bool _launchPending;
        public static bool LaunchPending => _launchPending;   // for the team-panel Launch button feedback
        private static SleepyNodes.OperationGraph _launchOp;
        private static SleepyNodes.MissionGraph _launchMission;
        private static string _launchScene;
        private static float _launchStartAt;     // when to fire StartOperation once in BrowsingMap (0 = not armed yet)
        private static float _launchDeadline;    // give up if BrowsingMap never comes up
        private static float _nextEnterTry;      // throttle EnterBrowsingMap re-issue while stuck at MainMenu

        public static bool Active => Config.PvpActive && SteamNet.InLobby && CoopP2P.HasPeer;

        public static void Tick(float dt)
        {
            try
            {
#if !PUBLIC_BUILD
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)
                               && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed))
                {
                    // DEV: loopback has no Steam lobby data — force the mode for testing. Ctrl+Shift+M.
                    if (kb[UnityEngine.InputSystem.Key.M].wasPressedThisFrame)
                    {
                        Config.PvpActive = !Config.PvpActive;
                        Log.LogInfo($"[pvp] DEV toggle → Config.PvpActive={Config.PvpActive} (co-op replication {(Config.PvpActive ? "DISABLED" : "restored")})");
                    }
                    // DEV: host launches the arena. Ctrl+Shift+L. (Eventual UI: a lobby "Launch Match" button.)
                    if (kb[UnityEngine.InputSystem.Key.L].wasPressedThisFrame) LaunchArena();
                }
#endif

                TickLaunch();   // drive a deferred host launch (MainMenu -> operations map -> StartOperation)

                bool active = Active;
                if (active != _wasActive)
                {
                    _wasActive = active;
                    if (active) Log.LogInfo($"[pvp] === PvP MATCH ACTIVE === (role={(CoopP2P.IsHost ? "host" : "client")} peers={CoopP2P.PeerCount}) — each player owns their own turret; opponents appear as enemy map entities");
                    else { Log.LogInfo("[pvp] === PvP match inactive ==="); _cbDisabled = false; _impactPrefab = null; _impactAnchor = null; }   // drop the scene-specific impact cache
                }

                // While in a PvP arena, keep the counter-battery system suppressed (scripted incoming + timer auto-fail).
                if (active && InMission() && !_cbDisabled && Time.unscaledTime >= _nextCbSweep)
                {
                    _nextCbSweep = Time.unscaledTime + 1f;
                    if (DisableCounterBattery()) _cbDisabled = true;
                }

                TickMatchEnd();
            }
            catch (Exception e) { Log.LogWarning("[pvp] match tick: " + e.Message); }
        }

        // ---------------- hit FX (reuse the counter-battery cinematic impact) ----------------

        // Spawn ONE counter-battery cinematic impact (the game's own explosion VFX + sound) near where it lands
        // incoming fire — the PvP "an opponent's round just landed on us" effect. World-space, so it shows in BOTH
        // the VR eyes and the flat camera. Called per-machine on a team hit (PvpEffects), so every crew member sees
        // + hears the round on their vehicle. A golden-angle scatter spreads repeated impacts; a safety Destroy
        // backstops any prefab that doesn't self-clean. No-op if the arena had no counter-battery spawner (the
        // Notify card + flatscreen flash still fire), so it degrades gracefully.
        public static void SpawnIncomingImpact()
        {
            try
            {
                if (!Config.PvpActive) return;
                var prefab = _impactPrefab; var anchor = _impactAnchor;
                if (prefab == null || anchor == null) return;
                Vector3 b;
                try { b = anchor.position; } catch { return; }   // anchor destroyed (scene change) — skip
                float a = (++_fxSeq) * 2.39996323f;              // golden angle — decorrelated scatter, no UnityEngine.Random
                const float r = 3f;
                Vector3 pos = new Vector3(b.x + Mathf.Cos(a) * r, b.y + _impactYOffset, b.z + Mathf.Sin(a) * r);
                var go = UnityEngine.Object.Instantiate(prefab).TryCast<GameObject>();
                if (go == null) return;
                try { go.transform.position = pos; } catch { }
                try { go.SetActive(true); } catch { }
                try { UnityEngine.Object.Destroy(go, 6f); } catch { }   // backstop cleanup
            }
            catch (Exception e) { Log.LogWarning("[pvp] incoming fx: " + e.Message); }
        }

        // ---------------- match end → back to the lobby ----------------

        // Once PvpPlayers declares a result, hold the banner MatchEndReturnSec, then (HOST only) return to the
        // operations map. CoopScene edge-detects the host's phase change and replicates GO_TO_PHASE to the clients, so
        // everyone leaves the arena together → PvpPlayers resets, teams unlock (LockedForMatch false), and the team
        // panel's LAUNCH is available again. Clients arm the same timer but never drive the phase (they follow).
        private static void TickMatchEnd()
        {
            bool over = false;
            try { over = Config.PvpActive && InMission() && PvpPlayers.MatchOver; } catch { }
            if (!over) { _matchEndReturnAt = 0f; return; }

            float now = Time.unscaledTime;
            if (_matchEndReturnAt <= 0f) { _matchEndReturnAt = now + MatchEndReturnSec; return; }
            if (now < _matchEndReturnAt) return;
            _matchEndReturnAt = 0f;

            if (!CoopP2P.IsHost) return;   // host drives the lifecycle; clients follow via CoopScene
            try
            {
                var mm = MissionManager.Instance;
                if (mm != null) { mm.ReturnToMap(); Log.LogInfo("[pvp] match over — returning to operations map (teams unlock for re-launch)"); }
            }
            catch (Exception e) { Log.LogWarning("[pvp] match-end return: " + e.Message); }
        }

        // ---------------- host launch ----------------

        // Host resolves the combat arena (MissionGraph by scene-name hint + its owning OperationGraph) and starts it.
        // CRITICAL: it does NOT call StartOperation directly from the MainMenu — that jumps MainMenu->Mission and
        // skips the operations map (BrowsingMap) where the player rig initializes, leaving the host's
        // CharacterController inactive ("can't move" + Move-on-inactive-controller spam). The client never hit this
        // because it reaches the mission VIA the map (the host's replicated GO_TO_PHASE then MISSION_START). So the
        // host launch ROUTES THROUGH the operations map first (EnterBrowsingMap, settle, then StartOperation) —
        // the deferred steps run in TickLaunch. CoopScene replicates each phase so clients still follow.
        public static void LaunchArena()
        {
            if (!CoopP2P.IsHost) { Log.LogWarning("[pvp] launch: only the HOST launches the match"); return; }
            if (!Config.PvpActive) { Log.LogWarning("[pvp] launch: not a PvP lobby (host with Shift+F9, or DEV Ctrl+Shift+M first) — aborting so the arena isn't started un-gated"); return; }

            var mm = MissionManager.Instance;
            if (mm == null) { Log.LogWarning("[pvp] launch: no MissionManager"); return; }

            // Always log the full catalog on a launch so the right scene/mission is visible in the log even on a hit.
            DumpMissionCatalog();

            SleepyNodes.OperationGraph foundOp = null; SleepyNodes.MissionGraph foundMission = null; string foundScene = null;
            int bestHint = int.MaxValue;   // prefer the EARLIEST hint in ArenaSceneHints (chill before challenging)
            try
            {
                var ops = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SleepyNodes.OperationGraph>());
                if (ops != null) for (int i = 0; i < ops.Length && bestHint > 0; i++)
                {
                    var op = ops[i].TryCast<SleepyNodes.OperationGraph>(); if (op == null) continue;
                    var missions = op.Missions; if (missions == null) continue;
                    for (int j = 0; j < missions.Count && bestHint > 0; j++)
                    {
                        var node = missions[j]; if (node == null) continue;
                        MissionGraphPair(node, out var mg, out var scene);
                        if (mg == null) continue;
                        int h = ArenaHintIndex(scene);
                        if (h >= 0 && h < bestHint) { foundOp = op; foundMission = mg; foundScene = scene; bestHint = h; }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[pvp] launch scan: " + e.Message); }

            if (foundOp == null || foundMission == null)
            {
                Log.LogWarning("[pvp] launch: no arena scene matched " + string.Join("/", ArenaSceneHints) + " — see the mission catalog above to correct the hint.");
                return;
            }

            int phase = -1; try { phase = (int)mm.CurrentPhase; } catch { }
            if (phase == (int)MissionManager.GamePhase.MissionActive) { Log.LogWarning("[pvp] launch: already in a mission — return to the menu/map first"); return; }

            string mid = "?", oid = "?";
            try { mid = foundMission.MissionID; } catch { } try { oid = foundOp.OperationID; } catch { }

            // Queue the launch; TickLaunch fires StartOperation once the operations map is up (and settled).
            _launchOp = foundOp; _launchMission = foundMission; _launchScene = foundScene;
            _launchPending = true; _launchDeadline = Time.unscaledTime + 30f; _nextEnterTry = 0f;

            if (phase == (int)MissionManager.GamePhase.BrowsingMap)
            {
                _launchStartAt = Time.unscaledTime + 0.5f;   // already in the map — brief settle, then start
                Log.LogInfo($"[pvp] launch QUEUED (in operations map): scene='{foundScene}' mission='{mid}' op='{oid}' — starting shortly");
            }
            else
            {
                _launchStartAt = 0f;   // from MainMenu: TickLaunch will EnterBrowsingMap, then arm the start
                Log.LogInfo($"[pvp] launch QUEUED (routing through operations map first for player init): scene='{foundScene}' mission='{mid}' op='{oid}'");
            }
        }

        // Drives a queued launch: MainMenu -> EnterBrowsingMap -> (settle) -> StartOperation. Each phase change is
        // picked up by CoopScene and replicated, so clients follow through the same map->mission path.
        private static void TickLaunch()
        {
            if (!_launchPending) return;
            try
            {
                var mm = MissionManager.Instance; if (mm == null) return;
                int phase = (int)mm.CurrentPhase;
                float now = Time.unscaledTime;

                if (phase == (int)MissionManager.GamePhase.MissionActive) { _launchPending = false; return; }   // started (or someone else did)
                if (now >= _launchDeadline) { _launchPending = false; Log.LogWarning("[pvp] launch: timed out routing through the operations map — aborted"); return; }

                if (phase == (int)MissionManager.GamePhase.BrowsingMap)
                {
                    if (_launchStartAt <= 0f) { _launchStartAt = now + 2f; Log.LogInfo("[pvp] launch: operations map up — settling player rig before starting the arena"); return; }
                    if (now < _launchStartAt) return;
                    _launchPending = false;
                    string mid = "?", oid = "?"; try { mid = _launchMission.MissionID; } catch { } try { oid = _launchOp.OperationID; } catch { }
                    Log.LogInfo($"[pvp] LAUNCHING arena scene='{_launchScene}' mission='{mid}' op='{oid}' — CoopScene carries the client(s) in");
                    try { mm.StartOperation(_launchOp, _launchMission); }
                    catch (Exception e) { Log.LogError("[pvp] StartOperation failed: " + e); }
                }
                else   // MainMenu (or unknown) — get into the operations map first
                {
                    if (now >= _nextEnterTry)
                    {
                        _nextEnterTry = now + 3f;   // throttle so we don't restart the map load every frame
                        try { mm.EnterBrowsingMap(); Log.LogInfo("[pvp] launch: entering operations map…"); }
                        catch (Exception e) { Log.LogWarning("[pvp] EnterBrowsingMap: " + e.Message); }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[pvp] launch tick: " + e.Message); }
        }

        private static void MissionGraphPair(SleepyNodes.MissionNode node, out SleepyNodes.MissionGraph mg, out string scene)
        {
            mg = null; scene = null;
            try { mg = node.Mission; } catch { }
            if (mg == null) return;
            try { var sr = mg.SceneReference; scene = sr.sceneName; } catch { }
        }

        // Index in ArenaSceneHints of the first hint contained in the scene name (lower = preferred), or -1 if none.
        private static int ArenaHintIndex(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return -1;
            string s = scene.ToLowerInvariant();
            for (int i = 0; i < ArenaSceneHints.Length; i++) if (s.Contains(ArenaSceneHints[i])) return i;
            return -1;
        }

        // Diagnostic: list every OperationGraph → its missions → scene, so we can confirm/correct the arena hint.
        public static void DumpMissionCatalog()
        {
            try
            {
                var ops = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SleepyNodes.OperationGraph>());
                int n = ops != null ? ops.Length : 0;
                Log.LogInfo($"[pvp] === MISSION CATALOG ({n} operation graph(s)) ===");
                for (int i = 0; i < n; i++)
                {
                    var op = ops[i].TryCast<SleepyNodes.OperationGraph>(); if (op == null) continue;
                    string oid = "?", disp = "?"; try { oid = op.OperationID; } catch { } try { disp = op.displayName; } catch { }
                    var missions = op.Missions; int mc = missions != null ? missions.Count : 0;
                    Log.LogInfo($"[pvp]  op '{oid}' ('{disp}') — {mc} mission(s)");
                    for (int j = 0; j < mc; j++)
                    {
                        var node = missions[j]; if (node == null) continue;
                        MissionGraphPair(node, out var mg, out var scene);
                        string mid = "?"; try { if (mg != null) mid = mg.MissionID; } catch { }
                        string mtype = "?"; try { if (mg != null) mtype = mg.MissionType.ToString(); } catch { }
                        Log.LogInfo($"[pvp]    mission '{mid}' scene='{scene}' type={mtype}");
                    }
                }
                Log.LogInfo("[pvp] === end catalog ===");
            }
            catch (Exception e) { Log.LogWarning("[pvp] catalog: " + e.Message); }
        }

        // ---------------- bare arena: counter-battery ----------------

        // Pause the scripted counter-battery timer + disable its cinematic impact spawner (scripted incoming fire AND
        // the timer-expiry auto-fail that would end a duel). Returns true once it found+suppressed at least one.
        private static bool DisableCounterBattery()
        {
            bool any = false;
            try
            {
                var timers = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CounterBatteryTimer>(), FindObjectsSortMode.None);
                if (timers != null) for (int i = 0; i < timers.Length; i++)
                {
                    var t = timers[i].TryCast<CounterBatteryTimer>(); if (t == null) continue;
                    try { t.PauseTimer(); any = true; } catch { }
                }
            }
            catch { }
            try
            {
                var spawners = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CounterBatteryCinematicImpactSpawner>(), FindObjectsSortMode.None);
                if (spawners != null) for (int i = 0; i < spawners.Length; i++)
                {
                    var s = spawners[i].TryCast<CounterBatteryCinematicImpactSpawner>(); if (s == null) continue;
                    // Keep the cinematic impact prefab (explosion VFX + sound) + its anchor for the PvP hit FX before
                    // we disable the scripted barrage.
                    if (_impactPrefab == null)
                    {
                        try { var pf = s.impactPrefab; if (pf != null) { _impactPrefab = pf; _impactAnchor = s.transform; _impactYOffset = s.spawnYOffset; Log.LogInfo("[pvp] cached counter-battery impact prefab for hit FX"); } } catch { }
                    }
                    try { s.enabled = false; any = true; } catch { }
                }
            }
            catch { }
            if (any) Log.LogInfo("[pvp] counter-battery suppressed (no scripted incoming fire / timer auto-fail in the duel arena)");
            return any;
        }

        // ---------------- helpers ----------------

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        public static string Status() => $"pvpMatch: mode={(Config.PvpActive ? "PvP" : "coop")} active={Active} cbDisabled={_cbDisabled}";
    }
}
