using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP MATCH COORDINATOR + LAUNCH. Owns the PvP-mode lifecycle:
    ///   • MODE is derived in SteamNet from the lobby's invr_mode tag into Config.PvpActive.
    ///   • LAUNCH: the HOST starts the duel arena from the lobby (it's not on the demo board, so we resolve the
    ///     combat MissionGraph/OperationGraph by scene name and call MissionManager.StartOperation directly). The
    ///     existing CoopScene host→client replication then carries every member into the SAME scene together — so
    ///     nobody loads the arena alone and runs its clock ahead of the others.
    ///   • BARE ARENA: the scripted PvE content is suppressed so a duel scene is just map + artillery + the two
    ///     players — spawn/scout nodes via CoopSim's PvP prefixes (installed at load, before any scene boots, so no
    ///     script fires early), and the counter-battery system (scripted incoming fire + its timer-expiry auto-fail)
    ///     disabled here once the arena scene is live.
    ///
    /// Co-op is fully isolated: when PvpActive the conflicting co-op replication self-disables.
    /// </summary>
    internal static class PvpMatch
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Candidate arena scenes (demo combat maps), in PREFERENCE order (earliest wins). 'Challenging' (ChallangeFDC)
        // is preferred because it ships the COUNTER-BATTERY system — we disable its scripted barrage but reuse its
        // impact prefab for the PvP hit FX (Chill has no counter-battery, so the explosion couldn't play there). Chill
        // stays as a fallback if Challenging doesn't resolve.
        private static readonly string[] ArenaSceneHints = { "challenging", "chill" };

        private static bool _wasActive;

        // PvP "you got hit" effect = the base game's OWN counter-battery-impact FEEL, driven directly on the VICTIM
        // (what the user described seeing in the real game: massive screenshake + the screen darkening + a hit sound):
        //   • SCREEN SHAKE — the bunker physically SWINGS. SwingController.TriggerExternalImpulse(worldXZ, twist) is the
        //     one-shot external impulse the game exposes (the same swing system the gun-fire bridge drives, just bigger).
        //     Always present (the cockpit's SwingController), so it does NOT depend on the gated counter-battery objects.
        //   • DARKEN — the bunker lights cut. SwingLightFlickerController.SetMasterPower(false) kills them; we restore
        //     (true, withSequence) a beat later → the "power disruption" flicker.
        //   • EXPLOSIONS — a barrage of the cinematic impact prefab around the player (VFX; carries the boom if the
        //     prefab emits on enable). The native CounterBatteryTimer is NOT used: with the scripted sequence sim-gated,
        //     the timer GameObject stays inactive and can't be found (client log: no timer ⇒ the old klaxon drive no-op'd).
        private static CounterBatteryTimer _cbTimer;                       // cached (suppression backstop only)
        private static CounterBatteryCinematicImpactSpawner _cbSpawner;    // cached (impact-prefab source)
        private static SwingController _swing;                             // the bunker swing driver = SCREEN SHAKE
        private static GunFireToSwingImpulseBridge _gunSwing;              // gun-recoil swing bridge — HandleGunFired() = the proven-visible kick
        private static SwingLightFlickerController _flicker;               // the bunker light power/flicker = DARKEN
        private static float _swingStrength;     // resolved from the gun-recoil bridge's impulseStrength (a known-visible magnitude); 0 ⇒ use default
        private static float _flickerRestoreAt;  // when to restore the lights after a darken (0 = not armed)
        private static bool _cbFound;            // located the arena FX systems at least once this arena
        private static float _nextCbScan;        // periodic full re-scan (re-acquire refs, suppress any leaked timer start)
        private const float HitSwingMultiplier = 3f;     // a counter-battery hit ≈ 3× a gun-fire swing ("massive")
        private const float DefaultSwingStrength = 9f;   // fallback impulse magnitude if no SwingImpulseOnEnable is found
        private const float FlickerDarkSec = 1.1f;       // how long the lights stay cut before the restore flicker

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
        private static int _pendingImpacts;        // remaining impacts in the current salvo
        private static float _nextImpactAt;
        private static bool _warnedNoImpactPrefab;
        private static bool _loggedClone;          // one-time impact-prefab component inventory dumped?
        private static bool _loggedSound;          // one-time impact emitter event-path logged?
        private const int BurstCount = 8;          // a sustained barrage (not a single pop), spread across the burst
        private const float BurstIntervalSec = 0.7f;   // 8 × 0.7s ≈ 5.6s of walking impacts — reads as a counter-battery salvo

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

        // PvP REQUISITION-CARD WINDOW. Turret-move and recon are PLAYER ABILITIES driven by requisition cards: redeeming
        // a card runs its PunchcardGraph, which contains the State_MoveTurret / State_SetTurretLocation /
        // State_SpawnScoutPlane nodes. Those nodes are CARD-GATED in CoopSim — suppressed in a bare PvP arena (so the
        // mission script can't relocate the turret or spawn recon on its own) EXCEPT while a card is driving them.
        // CoopPunchcards.OnAttemptRequisition calls NoteCardRedeem() the instant the local player pulls the requisition
        // lever; the card graph's node OnEnter fires a beat later (it runs as a coroutine over a few frames) and is let
        // through while this window is open. A bare-arena mission node firing OUTSIDE the window is still suppressed.
        private static float _cardGraphUntil;
        private const float CardGraphWindowSec = 8f;
        public static void NoteCardRedeem() { try { _cardGraphUntil = Time.unscaledTime + CardGraphWindowSec; } catch { } }
        public static bool CardGraphActive() { try { return Time.unscaledTime < _cardGraphUntil; } catch { return false; } }

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
                    // DEV: host launches the arena. Ctrl+Shift+L.
                    if (kb[UnityEngine.InputSystem.Key.L].wasPressedThisFrame) LaunchArena();
                    // DEV: force-spot the enemy (test the move/hit lane without first proving the recon-card path). Ctrl+Shift+R.
                    if (kb[UnityEngine.InputSystem.Key.R].wasPressedThisFrame) { PvpPlayers.OnReconReveal(); Log.LogInfo("[pvp] DEV force-reveal enemy (Ctrl+Shift+R)"); }
                }
#endif

                TickLaunch();   // drive a deferred host launch (MainMenu -> operations map -> StartOperation)

                bool active = Active;
                if (active != _wasActive)
                {
                    _wasActive = active;
                    if (active) Log.LogInfo($"[pvp] === PvP MATCH ACTIVE === (role={(CoopP2P.IsHost ? "host" : "client")} peers={CoopP2P.PeerCount}) — each player owns their own turret; opponents appear as enemy map entities");
                    else { Log.LogInfo("[pvp] === PvP match inactive ==="); _cbFound = false; _cbTimer = null; _cbSpawner = null; _swing = null; _gunSwing = null; _flicker = null; _swingStrength = 0f; _flickerRestoreAt = 0f; _impactPrefab = null; _impactAnchor = null; _pendingImpacts = 0; _warnedNoImpactPrefab = false; _loggedClone = false; _loggedSound = false; }   // drop the scene-specific caches
                }

                // While in a PvP arena: locate the swing/light/impact FX, keep the scripted counter-battery idle, and
                // restore the lights after a hit's darken. The hit effect itself is driven from PvpEffects.OnTeamHit.
                if (active && InMission()) TickCounterBattery();

                TickMatchEnd();
                TickHitFx();
            }
            catch (Exception e) { Log.LogWarning("[pvp] match tick: " + e.Message); }
        }

        // ---------------- hit FX (reuse the counter-battery cinematic impact) ----------------

        // A team hit just landed on US (the victim). Deliver the counter-battery "an opponent's round hit us" effect.
        // PRIMARY: drive the base game's OWN counter-battery sequence on the victim (scene-wired lights + klaxon +
        // the spawner's walking cinematic impacts) via a brief, non-expiring timer burst — the real thing the user
        // wants. GUARANTEED FALLBACK: also instantiate the cinematic impact prefab directly (world-space, so it shows
        // in BOTH the VR eyes and the flat camera) so there's visible feedback even if the native trigger no-ops or the
        // arena has no live timer. Called per-machine on a team hit (PvpEffects) ⇒ every crew member feels it.
        public static void SpawnIncomingImpact()
        {
            if (!Config.PvpActive) return;

            // DARKEN: cut the bunker lights now; TickFlickerRestore brings them back (with the restore flicker) a beat later.
            TriggerLightFlicker();

            // SOUND: fire the game's OWN cinematic impact once (it carries the authored hit sound + impulse). Forced to
            // ignore the (idle) timer via onlyWhileTimerRunning=false so it actually spawns. Its visual lands at the
            // scripted anchor (may be off-screen); we keep the manual barrage below for the near-player explosions.
            TryPlayImpactSound();

            // SHAKE + EXPLOSIONS: a sustained barrage — each beat SWINGS the bunker (screen shake) and drops an
            // explosion around us. Drains over ~5.6s in TickHitFx (≫ 1s, so it reads as a counter-battery pounding).
            _pendingImpacts = BurstCount;
            SpawnOneImpactNow();                 // first impact immediately (instant feedback)
            _pendingImpacts--;
            _nextImpactAt = Time.unscaledTime + BurstIntervalSec;
        }

        // Drain the queued salvo, one impact per BurstIntervalSec. Driven from Tick.
        private static void TickHitFx()
        {
            if (_pendingImpacts <= 0) return;
            if (Time.unscaledTime < _nextImpactAt) return;
            SpawnOneImpactNow();
            _pendingImpacts--;
            _nextImpactAt = Time.unscaledTime + BurstIntervalSec;
        }

        // ONE beat of the barrage: SWING the bunker (screen shake) + drop one explosion around the player.
        private static void SpawnOneImpactNow()
        {
            TriggerSwingHit();      // bunker swing = screen shake (each beat re-shakes, so it's sustained)
            SpawnExplosionNow();    // explosion VFX around the player (+ boom if the prefab emits on enable)
        }

        // SCREEN SHAKE. Two layers, because the bare external impulse alone wasn't visible (swingStrength resolved 0):
        //   • PRIMARY — call the gun-recoil bridge's HandleGunFired(): reproduces the EXACT swing the player already
        //     sees/feels when firing (proven to move the view), once per barrage beat ⇒ sustained.
        //   • SECONDARY — a larger external one-shot impulse for a counter-battery "massive" kick, magnitude grounded
        //     in the gun-recoil impulseStrength × HitSwingMultiplier (SwingReceiver clamps to its maxTilt).
        private static void TriggerSwingHit()
        {
            var gb = _gunSwing;
            if (gb != null) { try { gb.HandleGunFired(); } catch (Exception e) { Log.LogWarning("[pvp] swing recoil: " + e.Message); } }

            var sc = _swing;
            if (sc != null)
            {
                try
                {
                    try { sc.allowExternalOneShot = true; } catch { }   // the gate TriggerExternalImpulse checks
                    float mag = (_swingStrength > 0.01f ? _swingStrength : DefaultSwingStrength) * HitSwingMultiplier;
                    float a = (++_fxSeq) * 2.39996323f;                 // golden angle — decorrelated, no UnityEngine.Random
                    Vector2 impulse = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * mag;
                    float twist = ((_fxSeq & 1) == 0 ? 1f : -1f) * mag * 0.5f;
                    sc.TriggerExternalImpulse(impulse, twist);
                }
                catch (Exception e) { Log.LogWarning("[pvp] swing hit: " + e.Message); }
            }
        }

        // HIT SOUND: spawn the base game's cinematic impact via its OWN SpawnOne (carries the authored impact audio).
        // We force onlyWhileTimerRunning=false so it fires with our idle/inactive timer, and pass the resolved timer.
        private static void TryPlayImpactSound()
        {
            var s = _cbSpawner; if (s == null || _cbTimer == null) return;
            try { s.onlyWhileTimerRunning = false; } catch { }
            try { s.SpawnOne(_cbTimer); } catch (Exception e) { Log.LogWarning("[pvp] impact sound: " + e.Message); }
        }

        // DARKEN: cut the bunker lights (power disruption). TickFlickerRestore brings them back with the restore flicker.
        private static void TriggerLightFlicker()
        {
            var fc = _flicker; if (fc == null) return;
            try
            {
                fc.SetMasterPower(false, false);   // lights out now
                _flickerRestoreAt = Time.unscaledTime + FlickerDarkSec;
                Log.LogInfo("[pvp] hit: bunker lights cut (darken) + swing shake");
            }
            catch (Exception e) { Log.LogWarning("[pvp] light flicker: " + e.Message); }
        }

        // Restore the lights a beat after a darken (true, withSequence = the flicker-back-on power-restore animation).
        private static void TickFlickerRestore(float now)
        {
            if (_flickerRestoreAt <= 0f || now < _flickerRestoreAt) return;
            _flickerRestoreAt = 0f;
            var fc = _flicker; if (fc == null) return;
            try { fc.SetMasterPower(true, true); } catch { }
        }

        // EXPLOSIONS: instantiate ONE cinematic impact on the ground around the PLAYER (camera-anchored so it's reliably
        // near + visible; falls back to the spawner anchor, then origin). Golden-angle scatter + a safety Destroy.
        private static void SpawnExplosionNow()
        {
            if (_impactPrefab == null)
            {
                if (!_warnedNoImpactPrefab) { _warnedNoImpactPrefab = true; Log.LogWarning("[pvp] hit FX: no impact prefab cached — shake + darken only"); }
                return;
            }
            try
            {
                var prefab = _impactPrefab;

                Vector3 center; string src;
                var cam = Camera.main;
                if (cam != null) { center = cam.transform.position; center.y -= 1.4f; src = "camera"; }   // ~ground at the player's feet
                else { center = AnchorPos(out bool ok); src = ok ? "spawner" : "origin"; }

                float a = (++_fxSeq) * 2.39996323f;          // golden angle — decorrelated scatter, no UnityEngine.Random
                float r = 3.5f + (_fxSeq % 3);               // 3.5..5.5 m out, so the salvo walks around the player
                Vector3 pos = new Vector3(center.x + Mathf.Cos(a) * r, center.y + _impactYOffset, center.z + Mathf.Sin(a) * r);

                var go = UnityEngine.Object.Instantiate(prefab).TryCast<GameObject>();
                if (go == null) return;
                try { go.transform.position = pos; } catch { }
                try { go.SetActive(true); } catch { }
                TriggerCloneFx(go);   // fire the prefab's OWN Cinemachine impulse (shake) + FMOD emitter (sound)
                try { UnityEngine.Object.Destroy(go, 6f); } catch { }   // backstop cleanup
#if !PUBLIC_BUILD
                Log.LogInfo($"[pvp] hit FX impact at {pos.ToString("0.0")} (anchor={src})");
#endif
            }
            catch (Exception e) { Log.LogWarning("[pvp] hit FX spawn: " + e.Message); }
        }

        // A bare Instantiate of the impact prefab leaves its OWN authored components dormant — the game's spawner fires
        // them. So we fire them ourselves on the clone: the Cinemachine impulse = the counter-battery SCREEN SHAKE, and
        // the FMOD StudioEventEmitter / AudioSource = the HIT SOUND. One-time, we also dump the clone's component list
        // so the log shows exactly what the prefab carries (and whether the shake/sound sources are even present).
        private static void TriggerCloneFx(GameObject go)
        {
            if (go == null) return;

            // The prefab's SelfDestruct can cull the clone before its emitter is heard / VFX completes — strip it (by
            // type-name so no extra ref). Our own Destroy(go, 6f) handles cleanup.
            try
            {
                var all = go.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
                if (all != null) for (int i = 0; i < all.Count; i++)
                {
                    var c = all[i]; if (c == null) continue;
                    string n = null; try { n = c.GetIl2CppType().Name; } catch { }
                    if (n == "SelfDestruct") { try { UnityEngine.Object.Destroy(c); } catch { } }
                }
            }
            catch { }

            if (!_loggedClone)
            {
                _loggedClone = true;
                try
                {
                    var comps = go.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
                    var sb = new System.Text.StringBuilder();
                    if (comps != null) for (int i = 0; i < comps.Count; i++)
                    {
                        var c = comps[i]; if (c == null) continue;
                        try { if (sb.Length > 0) sb.Append(", "); sb.Append(c.GetIl2CppType().Name); } catch { }
                    }
                    int brains = -1, listeners = -1;
                    try { var b = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Unity.Cinemachine.CinemachineBrain>(), FindObjectsSortMode.None); brains = b != null ? b.Length : 0; } catch { }
                    try { var li = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Unity.Cinemachine.CinemachineImpulseListener>(), FindObjectsSortMode.None); listeners = li != null ? li.Length : 0; } catch { }
                    Log.LogInfo($"[pvp] impact prefab components: [{sb}] — cinemachine brains={brains} impulseListeners={listeners}");
                }
                catch (Exception e) { Log.LogWarning("[pvp] clone inventory: " + e.Message); }
            }
            // SCREEN SHAKE — fire the prefab's Cinemachine impulse with an EXPLICIT strong velocity. Force/parameterless
            // generate can resolve to ~0 if the source has no default velocity (a silent no-op). An incoming
            // round ⇒ a hard downward + lateral slam; the listener (census above) turns it into camera shake.
            try
            {
                var src = go.GetComponentInChildren(Il2CppType.Of<Unity.Cinemachine.CinemachineImpulseSource>(), true);
                if (src != null)
                {
                    var cis = src.TryCast<Unity.Cinemachine.CinemachineImpulseSource>();
                    if (cis != null)
                    {
                        float a = _fxSeq * 2.39996323f;
                        var vel = new Vector3(Mathf.Cos(a) * 18f, -30f, Mathf.Sin(a) * 18f);
                        cis.GenerateImpulseWithVelocity(vel);
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[pvp] impulse: " + e.Message); }
            // HIT SOUND — both GO-bound emitter.Play() (SelfDestruct now stripped, so the GO lives) AND GO-independent
            // RuntimeManager.PlayOneShot. Log the event path once so we can confirm it isn't empty.
            try
            {
                var emi = go.GetComponentInChildren(Il2CppType.Of<FMODUnity.StudioEventEmitter>(), true);
                if (emi != null)
                {
                    var see = emi.TryCast<FMODUnity.StudioEventEmitter>();
                    if (see != null)
                    {
                        try { see.enabled = true; } catch { }
                        try { see.Play(); } catch { }
                        var er = see.EventReference;
                        try { FMODUnity.RuntimeManager.PlayOneShot(er, go.transform.position); } catch { }
                        if (!_loggedSound) { _loggedSound = true; string p = "?"; bool nul = true; try { nul = er.IsNull; } catch { } try { p = er.ToString(); } catch { } Log.LogInfo($"[pvp] impact emitter event ref='{p}' isNull={nul}"); }
                    }
                }
                else
                {
                    var aud = go.GetComponentInChildren(Il2CppType.Of<AudioSource>(), true);
                    if (aud != null) { var a = aud.TryCast<AudioSource>(); if (a != null) a.Play(); }
                }
            }
            catch (Exception e) { Log.LogWarning("[pvp] emitter: " + e.Message); }
        }

        private static Vector3 AnchorPos(out bool ok)
        {
            ok = false;
            try { if (_impactAnchor != null) { ok = true; return _impactAnchor.position; } } catch { }
            return Vector3.zero;
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

        // ---------------- bare arena: arena FX (locate swing/lights + suppress the scripted counter-battery) ----------------

        // Per-frame while in a PvP arena: (re)scan for the swing/flicker/impact systems, restore the lights after a
        // darken, and keep the scripted counter-battery timer idle as a backstop (the node gate is the primary off).
        private static void TickCounterBattery()
        {
            float now = Time.unscaledTime;
            if (now >= _nextCbScan) { _nextCbScan = now + 1f; ScanCounterBattery(); }
            SuppressNativeCached();      // backstop: pause any leaked scripted timer start; keep the native spawner off
            TickFlickerRestore(now);     // bring the bunker lights back after a darken
        }

        // Full scan: locate the SwingController (shake) + SwingLightFlickerController (darken) + a SwingImpulseOnEnable
        // (for the authored impulse magnitude), cache the counter-battery impact prefab for the explosion barrage, and
        // keep any scripted counter-battery timer/spawner idle (the node gate already suppresses the scripted start).
        private static void ScanCounterBattery()
        {
            bool foundNow = false;
            // Swing systems (always present in the cockpit) — the shake + darken drivers.
            if (_swing == null)
            {
                try { var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<SwingController>(), FindObjectsSortMode.None); if (arr != null && arr.Length > 0) _swing = arr[0].TryCast<SwingController>(); } catch { }
            }
            if (_gunSwing == null)
            {
                try { var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<GunFireToSwingImpulseBridge>(), FindObjectsSortMode.None); if (arr != null && arr.Length > 0) _gunSwing = arr[0].TryCast<GunFireToSwingImpulseBridge>(); } catch { }
            }
            if (_flicker == null)
            {
                try { var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<SwingLightFlickerController>(), FindObjectsSortMode.None); if (arr != null && arr.Length > 0) _flicker = arr[0].TryCast<SwingLightFlickerController>(); } catch { }
            }
            if (_swingStrength <= 0f) ResolveSwingStrength();
            if (_swing != null || _gunSwing != null) foundNow = true;

            // Counter-battery impact prefab (explosion VFX) + idle the scripted timer/spawner (backstop to the node gate).
            try
            {
                var timers = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CounterBatteryTimer>(), FindObjectsSortMode.None);
                if (timers != null) for (int i = 0; i < timers.Length; i++)
                {
                    var t = timers[i].TryCast<CounterBatteryTimer>(); if (t == null) continue;
                    if (_cbTimer == null) _cbTimer = t;
                    try { if (t.IsRunning) t.PauseTimer(); } catch { }   // backstop a leaked scripted start (reversible)
                }
                // The scripted timer is INACTIVE (its sequence is sim-gated), so the active scan above misses it. Resolve
                // it from ALL loaded objects so SpawnOne (sound) has a non-null timer to pass.
                if (_cbTimer == null)
                {
                    try { var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CounterBatteryTimer>()); if (all != null) for (int i = 0; i < all.Length; i++) { var t = all[i].TryCast<CounterBatteryTimer>(); if (t != null) { _cbTimer = t; break; } } } catch { }
                }
            }
            catch { }
            try
            {
                var spawners = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<CounterBatteryCinematicImpactSpawner>(), FindObjectsSortMode.None);
                if (spawners != null) for (int i = 0; i < spawners.Length; i++)
                {
                    var s = spawners[i].TryCast<CounterBatteryCinematicImpactSpawner>(); if (s == null) continue;
                    if (_cbSpawner == null) _cbSpawner = s;
                    foundNow = true;
                    if (_impactPrefab == null) { try { var pf = s.impactPrefab; if (pf != null) { _impactPrefab = pf; _impactAnchor = s.transform; _impactYOffset = s.spawnYOffset; } } catch { } }
                    try { if (s.enabled) s.enabled = false; } catch { }   // we provide explosions manually — keep the native walker off
                }
            }
            catch { }
            // If no live spawner exposed an impact prefab, search ALL loaded objects (incl. inactive + asset prefabs).
            if (_impactPrefab == null) TryResolveImpactPrefabFromResources();
            if (foundNow && !_cbFound)
            {
                _cbFound = true;
                int recv = -1, lights = -1;
                try { var r = SwingController.Receivers; if (r != null) recv = r.Count; } catch { }
                try { var l = SwingLightFlickerController.Lights; if (l != null) lights = l.Count; } catch { }
                Log.LogInfo($"[pvp] arena FX located — swing={(_swing != null)} gunSwing={(_gunSwing != null)} flicker={(_flicker != null)} receivers={recv} lights={lights} swingStrength={_swingStrength:0.00} spawner={(_cbSpawner != null)} cbTimer={(_cbTimer != null)} impactPrefab={(_impactPrefab != null)}");
            }
        }

        // The game's authored swing scale, grounded in a KNOWN-VISIBLE magnitude: the gun-recoil bridge's
        // impulseStrength (the kick you feel firing). We multiply it by HitSwingMultiplier for a "massive" hit.
        // Fallback: largest SwingImpulseOnEnable.strength in the scene (usually only on prefabs → often none → 0).
        private static void ResolveSwingStrength()
        {
            try { if (_gunSwing != null) { float s = _gunSwing.impulseStrength; if (s > 0f) { _swingStrength = s; return; } } } catch { }
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<SwingImpulseOnEnable>(), FindObjectsSortMode.None);
                if (arr == null) return;
                float best = 0f;
                for (int i = 0; i < arr.Length; i++)
                {
                    var s = arr[i].TryCast<SwingImpulseOnEnable>(); if (s == null) continue;
                    try { float st = s.strength; if (st > best) best = st; } catch { }
                }
                if (best > 0f) _swingStrength = best;
            }
            catch { }
        }

        // Per-frame cheap backstop: if a scripted counter-battery start leaked past the node gate, PAUSE it (reversible)
        // and keep the native impact walker off (we own the explosions). The node gate is the primary suppression.
        private static void SuppressNativeCached()
        {
            try { if (_cbTimer != null && _cbTimer.IsRunning) _cbTimer.PauseTimer(); } catch { }
            try { if (_cbSpawner != null && _cbSpawner.enabled) _cbSpawner.enabled = false; } catch { }
        }

        // Last-resort impact-prefab resolve: scan every loaded CounterBatteryCinematicImpactSpawner (active, inactive,
        // or asset) for a non-null impactPrefab, so the manual fallback salvo can play even where no live spawner runs.
        private static void TryResolveImpactPrefabFromResources()
        {
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CounterBatteryCinematicImpactSpawner>());
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                {
                    var s = arr[i].TryCast<CounterBatteryCinematicImpactSpawner>(); if (s == null) continue;
                    try { var pf = s.impactPrefab; if (pf != null) { _impactPrefab = pf; _impactYOffset = s.spawnYOffset; return; } } catch { }
                }
            }
            catch (Exception e) { Log.LogWarning("[pvp] resolve impact prefab: " + e.Message); }
        }

        // ---------------- helpers ----------------

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        public static string Status() => $"pvpMatch: mode={(Config.PvpActive ? "PvP" : "coop")} active={Active} fx={(_cbFound ? "found" : "-")} swing={(_swing != null)} flicker={(_flicker != null)}";
    }
}
