#if !PUBLIC_BUILD
using System;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// DEV-ONLY fire-state probe: read/log-only hooks on GunController.RequestFire / GunController.FireShell /
    /// ShellVisual.Initialize. Answers four questions about the fire state machine before a per-side intent queue
    /// is built. Completely absent from the public build (#if !PUBLIC_BUILD). Zero overhead unless
    /// Config.FireProbe = true in IronNestVR.cfg (the flag gates both installation AND every hook body).
    ///
    /// Q1 — 1:1 ordering: every RequestFire that proceeds yields exactly one FireShell then one ShellVisual.Initialize.
    /// Q2 — synchronous no-op detection: when !CanFire / IsReloading / pendingReload, does RequestFire no-op?
    /// Q3 — reload spacing: FireShell inter-shot spacing vs fireDelay (two same-gun in-flight shots from one source).
    /// Q4 — call source: is the MSG_CLICK-replay lane actually reaching RequestFire?
    ///
    /// CoopControls.ReplayClick sets InClickReplay=true on entry / false in finally.
    /// Plugin.cs calls ApplyPatches() under #if !PUBLIC_BUILD.
    /// </summary>
    internal static class CoopFireProbe
    {
        // Integration surface — CoopControls.ReplayClick sets this.
        public static bool InClickReplay;

        private static ManualLogSource Log => Plugin.Logger;
        private static Harmony _harmony;

        // Q1: per-side sequence counters (index 0=Left, 1=Right).
        private static readonly int[] _reqSeq  = new int[2];
        private static readonly int[] _fireSeq = new int[2];
        private static int _visSeqTotal;

        // Q2: per-side "did FireShell fire during THIS RequestFire call?".
        private static readonly bool[] _fireSeenDuringReq = new bool[2];

        // Q3: per-side time of previous FireShell (unscaledTime). Init to -1 = not yet seen.
        private static readonly float[] _lastFireT = new float[] { -1f, -1f };

        // ---- Reload-state probe (2026-06-28) — answers, for the reload-sync design: ----
        //  R1: does ArtilleryReloadController.CurrentStateIndex / CurrentState.stateKey faithfully track
        //      loaded<->empty? (watch the [reload] state-change timeline as you ram/charge/fire)
        //  R2: does firing advance the state index / clear the chamber? (FireShell.PRE vs .POST snapshot)
        //  R3: do the gun's ChamberedShellBlueprint and the controller's chamberedShell agree, and does
        //      PowderCharges move with the cycle? (every snapshot logs all three + isReloadCompleteState)
        // Polled per-frame from VrManager via Tick(); keyed by the turret's guns-list index (0..3).
        private static readonly int[]  _lastStateIdx = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private static readonly int[]  _lastLoaded   = { int.MinValue, int.MinValue, int.MinValue, int.MinValue }; // -1 empty / 1 loaded
        private static readonly int[]  _lastPowder   = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private static readonly bool[] _statesDumped = { false, false, false, false };
        // R4 (2026-06-28): the reload rammer/advance controls act on the turret's CURRENTLY-CONTROLLED gun, and the
        // mod never syncs TurretController.controlledGunIndex — so a replayed rammer click loads the WRONG gun on the
        // peer. Track it here to confirm host vs client divergence at charge-ram time.
        private static int _lastControlledGun = int.MinValue;

        // ---- mutate-test — LOCAL-ONLY, dev keybinds (Ctrl+Alt+digit). ----
        // Answers the one question the read-only probe cannot: does ArtilleryReloadController.SetState(idx, force:true)
        // rebuild the chamber/powder when force-jumping, or do those only happen via the skipped animation events?
        // Each key snapshots (idx, gunChamber, ctrlChamber, powder) BEFORE → runs one mutation → snapshots AFTER, so
        // the apply recipe falls straight out of the log. Chord = Ctrl+Alt (PvpProbe owns Ctrl+Shift).
        private static int _mutTargetSide; // 0=Left, 1=Right; toggled with Ctrl+Alt+Minus.

        // ---- "Drive to loaded" prototype (the apply-path candidate): converge an EMPTY gun to loaded+CanFire using
        // ONLY the game's own legit advance/load calls (AdvanceState / TryLoadShell / SetPowderCharge), paced by the
        // controller's `working` flag so each animation clip plays through. This is the exact mechanism the peer would
        // use to reach the host's target state without poking internals. Multi-frame → driven from Tick() every frame.
        private static bool  _driving;
        private static int   _driveSide;
        private static int   _drivePowderTarget = 2;
        private static float _driveDeadline;     // unscaledTime hard stop
        private static float _driveLastActionT;  // min spacing between actions
        private static int   _driveActions;      // hard cap on total actions
        private static int   _driveLastIdx = int.MinValue;
        private static int   _driveSameIdxActions; // stall detector: actions taken without idx moving

        // ============================= ApplyPatches =============================

        public static void ApplyPatches()
        {
            if (!Config.FireProbe) return;   // when off, install NOTHING — zero overhead

            try { _harmony = new Harmony("com.ironnest.vr.fireprobe"); }
            catch (Exception e) { Log.LogError("[fireprobe] Harmony init failed: " + e); return; }

            // GunController.RequestFire — prefix + postfix
            try
            {
                var mi = AccessTools.Method(typeof(GunController), "RequestFire");
                if (mi != null)
                {
                    _harmony.Patch(mi,
                        prefix:  new HarmonyMethod(typeof(CoopFireProbe), nameof(RequestFirePre)),
                        postfix: new HarmonyMethod(typeof(CoopFireProbe), nameof(RequestFirePost)));
                    Log.LogInfo("[fireprobe] GunController.RequestFire patched (prefix+postfix)");
                }
                else Log.LogWarning("[fireprobe] GunController.RequestFire not found — Q1/Q2/Q4 hooks NOT installed");
            }
            catch (Exception e) { Log.LogWarning("[fireprobe] RequestFire patch: " + e.Message); }

            // GunController.FireShell — prefix
            try
            {
                var mi = AccessTools.Method(typeof(GunController), "FireShell");
                if (mi != null)
                {
                    _harmony.Patch(mi,
                        prefix:  new HarmonyMethod(typeof(CoopFireProbe), nameof(FireShellPre)),
                        postfix: new HarmonyMethod(typeof(CoopFireProbe), nameof(FireShellPost)));
                    Log.LogInfo("[fireprobe] GunController.FireShell patched (prefix+postfix)");
                }
                else Log.LogWarning("[fireprobe] GunController.FireShell not found — Q1/Q3 hooks NOT installed");
            }
            catch (Exception e) { Log.LogWarning("[fireprobe] FireShell patch: " + e.Message); }

            // ShellVisual.Initialize — postfix
            try
            {
                var mi = AccessTools.Method(typeof(ShellVisual), "Initialize");
                if (mi != null)
                {
                    _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(CoopFireProbe), nameof(ShellVisualPost)));
                    Log.LogInfo("[fireprobe] ShellVisual.Initialize patched (postfix)");
                }
                else Log.LogWarning("[fireprobe] ShellVisual.Initialize not found — Q1 visual-seq hook NOT installed");
            }
            catch (Exception e) { Log.LogWarning("[fireprobe] ShellVisual patch: " + e.Message); }

            Log.LogInfo("[fireprobe] armed — hooking RequestFire/FireShell/ShellVisual.Initialize (answers Q1-Q4)");
        }

        // ============================= Hooks =============================

        // PREFIX on GunController.RequestFire.
        // Q1: bump _reqSeq[side].
        // Q2: snapshot gun state fields; arm _fireSeenDuringReq[side]=false.
        // Q4: classify call source.
        // Returns void — observe-only, never skips the original.
        public static void RequestFirePre(GunController __instance)
        {
            if (!Config.FireProbe) return;
            try
            {
                int side = SideOf(__instance);
                float t = Time.unscaledTime;

                // Q1
                int req = ++_reqSeq[side];

                // Q2 — arm per-side flag
                _fireSeenDuringReq[side] = false;

                // Q4 — call source
                string src;
                try
                {
                    if (CoopBallistics.IsApplyingRemoteImpact) src = "MSG_FIRE-replay";
                    else if (InClickReplay)                     src = "MSG_CLICK-replay(fire-button)";
                    else                                        src = "local-trigger";
                }
                catch { src = "n/a"; }

                // Q2 — snapshot gun state (each field in its own try/catch)
                string canFire     = "n/a"; try { canFire     = __instance.CanFire.ToString();     } catch { }
                string isReloading = "n/a"; try { isReloading = __instance.IsReloading.ToString(); } catch { }
                string pendRel     = "n/a"; try { pendRel     = __instance.pendingReload.ToString();} catch { }
                string hasFiredPre = "n/a"; try { hasFiredPre = __instance.hasFired.ToString();    } catch { }
                string fdStr       = "n/a"; try { fdStr       = __instance.fireDelay.ToString("0.000"); } catch { }

                string nm = "?"; try { if (__instance != null) nm = __instance.name ?? "?"; } catch { }

                Log.LogInfo($"[fireprobe] RequestFire.PRE t={t:0.000} gun={nm} side={(side == 0 ? "L" : "R")} reqSeq={req} " +
                            $"src={src} CanFire={canFire} IsReloading={isReloading} pendingReload={pendRel} hasFired={hasFiredPre} fireDelay={fdStr} {ReloadSnapshot(__instance)}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] RequestFirePre: " + e.Message); } catch { }
            }
        }

        // POSTFIX on GunController.RequestFire.
        // Q2: read hasFired again; log flip + whether FireShell was seen during the call.
        // Returns void — observe-only.
        public static void RequestFirePost(GunController __instance)
        {
            if (!Config.FireProbe) return;
            try
            {
                int side = SideOf(__instance);
                float t = Time.unscaledTime;

                string hasFiredPost = "n/a"; try { hasFiredPost = __instance.hasFired.ToString(); } catch { }
                bool fireSeen = _fireSeenDuringReq[side];
                string nm = "?"; try { if (__instance != null) nm = __instance.name ?? "?"; } catch { }

                Log.LogInfo($"[fireprobe] RequestFire.POST t={t:0.000} gun={nm} side={(side == 0 ? "L" : "R")} " +
                            $"hasFired={hasFiredPost} fireShellDuringCall={fireSeen}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] RequestFirePost: " + e.Message); } catch { }
            }
        }

        // PREFIX on GunController.FireShell.
        // Q1: bump _fireSeq[side]; log reqSeq vs fireSeq so a reader can confirm 1:1 tracking.
        // Q2: set _fireSeenDuringReq[side]=true.
        // Q3: log spacing since last FireShell on this side vs fireDelay.
        // Returns void — observe-only, never skips the original.
        public static void FireShellPre(GunController __instance)
        {
            if (!Config.FireProbe) return;
            try
            {
                int side = SideOf(__instance);
                float t = Time.unscaledTime;

                // Q1
                int fire = ++_fireSeq[side];
                int req  = _reqSeq[side];

                // Q2
                _fireSeenDuringReq[side] = true;

                // Q3
                float last = _lastFireT[side];
                string spacing = last < 0f ? "first" : (t - last).ToString("0.000");
                _lastFireT[side] = t;

                string fdStr = "n/a"; try { fdStr = __instance.fireDelay.ToString("0.000"); } catch { }
                string nm = "?"; try { if (__instance != null) nm = __instance.name ?? "?"; } catch { }

                Log.LogInfo($"[fireprobe] FireShell.PRE t={t:0.000} gun={nm} side={(side == 0 ? "L" : "R")} " +
                            $"reqSeq={req} fireSeq={fire} spacingSinceLastShot={spacing} fireDelay={fdStr} {ReloadSnapshot(__instance)}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] FireShellPre: " + e.Message); } catch { }
            }
        }

        // POSTFIX on GunController.FireShell — R2: capture reload/chamber state the instant AFTER a shot fires,
        // so a reader can see whether firing itself ejects the chambered shell / advances the reload state index.
        public static void FireShellPost(GunController __instance)
        {
            if (!Config.FireProbe) return;
            try
            {
                int side = SideOf(__instance);
                float t = Time.unscaledTime;
                string nm = "?"; try { if (__instance != null) nm = __instance.name ?? "?"; } catch { }
                Log.LogInfo($"[fireprobe] FireShell.POST t={t:0.000} gun={nm} side={(side == 0 ? "L" : "R")} {ReloadSnapshot(__instance)}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] FireShellPost: " + e.Message); } catch { }
            }
        }

        // POSTFIX on ShellVisual.Initialize.
        // Q1: bump global visual counter and log. No gun ref available; correlation to a side is best-effort/none.
        // Returns void — observe-only.
        public static void ShellVisualPost()
        {
            if (!Config.FireProbe) return;
            try
            {
                int vis = ++_visSeqTotal;
                float t = Time.unscaledTime;
                Log.LogInfo($"[fireprobe] ShellVisual.Initialize.POST t={t:0.000} visSeqTotal={vis}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] ShellVisualPost: " + e.Message); } catch { }
            }
        }

        // ============================= Reload-state timeline =============================

        // Polled every frame from VrManager. Dumps each gun's reload STATE TABLE once (the index->stateKey legend),
        // then logs a [reload] line whenever a gun's CurrentStateIndex changes — building the full ram/charge/fire
        // timeline so we can see exactly which states mean loaded/empty and whether firing advances the machine.
        public static void Tick()
        {
            if (!Config.FireProbe) return;
            MutateTick();   // Step 0 dev keybinds (Ctrl+Alt+digit) — local-only, no networking
            DriveTick();    // multi-frame "drive to loaded" prototype (apply-path candidate)
            try
            {
                TurretController tc = null; try { tc = TurretController.Instance; } catch { }
                if (tc == null) return;

                // R4: log every change of the controlled-gun selection (the un-synced state that mis-routes rammers).
                int cgi = int.MinValue; try { cgi = tc.controlledGunIndex; } catch { }
                if (cgi != _lastControlledGun)
                {
                    _lastControlledGun = cgi;
                    Log.LogInfo($"[reload] controlledGun -> {cgi} t={Time.unscaledTime:0.000}  (un-synced; rammer clicks act on THIS gun)");
                }

                var guns = tc.guns;
                if (guns == null) return;
                int n = guns.Count; if (n > 4) n = 4;
                for (int i = 0; i < n; i++)
                {
                    GunController g = null; try { g = guns[i]; } catch { }
                    if (g == null) continue;
                    ArtilleryReloadController rc = null; try { rc = g.artilleryReloadController; } catch { }
                    if (rc == null) continue;

                    if (!_statesDumped[i]) { _statesDumped[i] = true; DumpStates(g, rc, i); }

                    int idx = int.MinValue; try { idx = rc.CurrentStateIndex; } catch { }
                    // Trigger on ANY change of (index, chamber-loaded, powder) — NOT index alone. State 0 (BreachLocked)
                    // is loaded-OR-empty, so an index-only trigger is blind to a fired-vs-loaded desync at the same index.
                    int loaded = int.MinValue; try { loaded = g.ChamberedShellBlueprint != null ? 1 : -1; } catch { }
                    int powder = int.MinValue; try { powder = g.PowderCharges; } catch { }
                    if (idx != _lastStateIdx[i] || loaded != _lastLoaded[i] || powder != _lastPowder[i])
                    {
                        _lastStateIdx[i] = idx; _lastLoaded[i] = loaded; _lastPowder[i] = powder;
                        string nm = "?"; try { nm = g.name ?? "?"; } catch { }
                        Log.LogInfo($"[reload] state-change t={Time.unscaledTime:0.000} gun={nm} side={(SideOf(g) == 0 ? "L" : "R")} ctrlGun={cgi} {ReloadSnapshot(g)}");
                    }
                }
            }
            catch (Exception e) { try { Log.LogWarning("[reload] Tick: " + e.Message); } catch { } }
        }

        // One-time per gun: log the ordered reload-state table (index -> stateKey / displayName / isReloadCompleteState).
        // This is the legend that makes every later [reload] state-change line interpretable.
        private static void DumpStates(GunController g, ArtilleryReloadController rc, int i)
        {
            try
            {
                string nm = "?"; try { nm = g.name ?? "?"; } catch { }
                var states = rc.reloadStates;
                int count = 0; try { count = states != null ? states.Count : 0; } catch { }
                Log.LogInfo($"[reload] STATE TABLE gun={nm} side={(SideOf(g) == 0 ? "L" : "R")} listIndex={i} count={count}");
                for (int s = 0; s < count; s++)
                {
                    ReloadStateDef def = null; try { def = states[s]; } catch { }
                    string key = "?", disp = "?"; bool complete = false, auto = false;
                    if (def != null)
                    {
                        try { key = def.stateKey ?? "?"; } catch { }
                        try { disp = def.displayName ?? "?"; } catch { }
                        try { complete = def.isReloadCompleteState; } catch { }
                        try { auto = def.autoAdvanceToNextState; } catch { }
                    }
                    Log.LogInfo($"[reload]   [{s}] key='{key}' disp='{disp}' reloadComplete={complete} autoAdvance={auto}");
                }
            }
            catch (Exception e) { try { Log.LogWarning("[reload] DumpStates: " + e.Message); } catch { } }
        }

        // Compact one-liner of a gun's full reload/chamber/powder state. Appended to RequestFire/FireShell logs and
        // used by the [reload] timeline. R3: logs BOTH chamber views (gun.ChamberedShellBlueprint vs controller's
        // chamberedShell GameObject) so we can confirm they agree, plus powder + the current state's reloadComplete flag.
        private static string ReloadSnapshot(GunController g)
        {
            try
            {
                if (g == null) return "[rs n/a]";
                ArtilleryReloadController rc = null; try { rc = g.artilleryReloadController; } catch { }
                int idx = -1, total = -1; string key = "?"; bool complete = false, working = false, ctrlChamber = false;
                if (rc != null)
                {
                    try { idx = rc.CurrentStateIndex; } catch { }
                    try { var st = rc.reloadStates; total = st != null ? st.Count : -1; } catch { }
                    try { working = rc.working; } catch { }
                    try { ctrlChamber = rc.chamberedShell != null; } catch { }
                    try { var cs = rc.CurrentState; if (cs != null) { key = cs.stateKey ?? "?"; complete = cs.isReloadCompleteState; } } catch { }
                }
                bool gunChamber = false; try { gunChamber = g.ChamberedShellBlueprint != null; } catch { }
                int powder = -1; try { powder = g.PowderCharges; } catch { }
                // Arming-lever / fire-flow dimensions (the state EjectChamberedShell leaves stale): fireButton.isActive
                // is the "arming lever active" flag the user observed; plus the three fire/reload booleans + CanFire.
                string lever = "?"; try { var fb = g.fireButton; if (fb != null) lever = fb.isActive.ToString(); } catch { }
                string canFire = "?"; try { canFire = g.CanFire.ToString(); } catch { }
                string hasFired = "?"; try { hasFired = g.hasFired.ToString(); } catch { }
                string isReloading = "?"; try { isReloading = g.isReloading.ToString(); } catch { }
                string pendingReload = "?"; try { pendingReload = g.pendingReload.ToString(); } catch { }
                return $"[rs idx={idx}/{total} key='{key}' complete={complete} working={working} ctrlChamber={ctrlChamber} gunChamber={gunChamber} powder={powder} leverActive={lever} CanFire={canFire} hasFired={hasFired} isReloading={isReloading} pendingReload={pendingReload}]";
            }
            catch { return "[rs err]"; }
        }

        // ============================= Step 0 mutate-test =============================

        // Polled every frame (from Tick). Dev-only LOCAL tests on the target gun via the game's OWN legit advance/load
        // calls — NO internal-poking (eject/SetState-force are removed: eject was proven to CORRUPT the gun). NOTHING
        // here networks. Chord = Ctrl+Alt; PvpProbe owns Ctrl+Shift.
        //   Ctrl+Alt+Minus : toggle target side L<->R
        //   Ctrl+Alt+0     : dump current snapshot (no mutation)
        //   Ctrl+Alt+1     : AdvanceState()         — single legit advance (does it play the clip / load the chamber?)
        //   Ctrl+Alt+2     : TryLoadShell()         — load a shell into the cylinder (logs CanLoadBullet before/after)
        //   Ctrl+Alt+3     : SetPowderCharge(2)     — set powder (only sticks with a chambered shell)
        //   Ctrl+Alt+9     : START "drive to loaded" — converge to loaded+CanFire via legit calls, paced by `working`
        //   Ctrl+Alt+Backspace : ABORT an in-progress drive
        // NEW apply-path candidates for a NON-OPERATED gun (the actual peer case — bare AdvanceState was proven to NOT load
        // a gun nobody is operating: AdvanceState is what the clip's animation event CALLS, it doesn't FIRE the clip; the
        // clip is fired by the animator TRIGGER, raised by the button via OnUserInput_Advance). Test on a JUST-FIRED gun
        // sitting at BreechOpen (idx3, where it waits for the player) WITHOUT pulling levers, then watch working->True +
        // gunChamber->True:
        //   Ctrl+Alt+5     : AutoReloadManager.StartAutoReload() — the game's OWN automated reload (presses real buttons)
        //   Ctrl+Alt+6     : direct chamber set — Instantiate shell prefab -> ReceiveChamberedBullet -> OnShellLoaded ->
        //                    SetPowderCharge(2) -> UpdateFireButtonActiveState (animation-independent poke)
        //   Ctrl+Alt+7     : OnUserInput_Advance() — the BUTTON's real handler (fires the trigger -> plays the clip ->
        //                    the clip's event loads the chamber). The mechanism AdvanceState skipped.
        //   Ctrl+Alt+8     : fire the CURRENT state's animator triggers directly (ReloadStateDef.triggers -> SetTrigger).
        //   Ctrl+Alt+0 now ALSO dumps the reload animators' enabled/active/culling state (are they even on?).
        private static void MutateTick()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return;
                bool chord = (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)
                          && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
                if (!chord) return;

                if (kb[UnityEngine.InputSystem.Key.Minus].wasPressedThisFrame)
                {
                    _mutTargetSide = _mutTargetSide == 0 ? 1 : 0;
                    Log.LogInfo($"[mut] target side -> {(_mutTargetSide == 0 ? "L" : "R")}");
                    return;
                }
                if (kb[UnityEngine.InputSystem.Key.Digit0].wasPressedThisFrame) MutateRun("dump", null);
                else if (kb[UnityEngine.InputSystem.Key.Digit1].wasPressedThisFrame) MutateRun("AdvanceState", (g, rc) => rc.AdvanceState());
                else if (kb[UnityEngine.InputSystem.Key.Digit2].wasPressedThisFrame) MutateRun("TryLoadShell", (g, rc) => { bool can = false; try { can = rc.CanLoadBullet(); } catch { } Log.LogInfo($"[mut]   CanLoadBullet={can}"); rc.TryLoadShell(); });
                else if (kb[UnityEngine.InputSystem.Key.Digit3].wasPressedThisFrame) MutateRun("SetPowderCharge(2)", (g, rc) => g.SetPowderCharge(2));
                else if (kb[UnityEngine.InputSystem.Key.Digit5].wasPressedThisFrame) AutoReloadTest();
                else if (kb[UnityEngine.InputSystem.Key.Digit6].wasPressedThisFrame) DirectChamberTest();
                else if (kb[UnityEngine.InputSystem.Key.Digit7].wasPressedThisFrame) MutateRun("OnUserInput_Advance", (g, rc) => { AnimatorDump(rc, "pre"); rc.OnUserInput_Advance(); });
                else if (kb[UnityEngine.InputSystem.Key.Digit8].wasPressedThisFrame) FireTriggersTest();
                else if (kb[UnityEngine.InputSystem.Key.Digit9].wasPressedThisFrame) StartDrive();
                else if (kb[UnityEngine.InputSystem.Key.Backspace].wasPressedThisFrame) { if (_driving) { _driving = false; Log.LogInfo("[drive] ABORTED by user"); } }
            }
            catch (Exception e) { try { Log.LogWarning("[mut] tick: " + e.Message); } catch { } }
        }

        // Begin the "drive to loaded" prototype on the selected side.
        private static void StartDrive()
        {
            GunController g = ResolveGunBySide(_mutTargetSide);
            if (g == null) { Log.LogWarning("[drive] no gun for selected side"); return; }
            _driving = true; _driveSide = _mutTargetSide;
            _driveDeadline = Time.unscaledTime + 25f; _driveLastActionT = 0f; _driveActions = 0;
            _driveLastIdx = int.MinValue; _driveSameIdxActions = 0;
            Log.LogInfo($"[drive] START side={(_driveSide == 0 ? "L" : "R")} target=loaded+CanFire powder={_drivePowderTarget} {ReloadSnapshot(g)}");
        }

        // The multi-frame driver. Runs every frame while _driving. Each time the controller is NOT `working` (no clip
        // playing) and the gun isn't yet loaded, it takes ONE legit action — load a shell into the cylinder when the
        // game allows it, set powder at SelectPowderCharge, else AdvanceState — and waits for the clip to finish. If the
        // index hasn't moved after several actions, or we hit the time/action cap, it stops and logs WHY (that tells us
        // exactly where the legit path stalls). This is the literal candidate for the peer-apply path.
        private static void DriveTick()
        {
            if (!_driving) return;
            try
            {
                GunController g = ResolveGunBySide(_driveSide);
                ArtilleryReloadController rc = null; try { if (g != null) rc = g.artilleryReloadController; } catch { }
                if (g == null || rc == null) { Log.LogWarning("[drive] gun/controller vanished — stop"); _driving = false; return; }

                float now = Time.unscaledTime;
                if (now > _driveDeadline) { Log.LogWarning($"[drive] TIMEOUT — stop. {ReloadSnapshot(g)}"); _driving = false; return; }

                // Success = chamber loaded + CanFire (powder is NOT required — CanFire is True at powder=0; we just set
                // powder to the target on the way out so it matches).
                bool loaded = false; try { loaded = g.ChamberedShellBlueprint != null; } catch { }
                bool canFire = false; try { canFire = g.CanFire; } catch { }
                int powder = 0; try { powder = g.PowderCharges; } catch { }
                if (loaded && canFire)
                {
                    if (powder != _drivePowderTarget) { try { g.SetPowderCharge(_drivePowderTarget); } catch { } }
                    Log.LogInfo($"[drive] SUCCESS in {_driveActions} actions. {ReloadSnapshot(g)}"); _driving = false; return;
                }

                bool working = false; try { working = rc.working; } catch { }
                if (working) return;                                  // a clip is playing — let it finish
                if (now - _driveLastActionT < 0.20f) return;          // min spacing between actions

                int idx = -1; try { idx = rc.CurrentStateIndex; } catch { }
                string key = "?"; try { var cs = rc.CurrentState; if (cs != null) key = cs.stateKey ?? "?"; } catch { }

                // Stall detection: count consecutive actions that didn't move the index.
                if (idx == _driveLastIdx) _driveSameIdxActions++; else { _driveSameIdxActions = 0; _driveLastIdx = idx; }
                if (_driveSameIdxActions > 6) { Log.LogWarning($"[drive] STALLED at idx={idx} key='{key}' (6 actions, no progress) — stop. {ReloadSnapshot(g)}"); _driving = false; return; }
                if (++_driveActions > 60) { Log.LogWarning($"[drive] action cap — stop. {ReloadSnapshot(g)}"); _driving = false; return; }

                // ONE legit action: set powder at the charge state (so charges ram to the target), else AdvanceState().
                // AdvanceState alone drives the whole sequence — it loads the chamber during ShellRamming via the clip.
                // (TryLoadShell is NOT used here: at BreechOpen CanLoadBullet stays True forever and it doesn't advance.)
                string act;
                if (key == "SelectPowderCharge" && powder < _drivePowderTarget) { try { g.SetPowderCharge(_drivePowderTarget); } catch (Exception e) { Log.LogWarning("[drive] SetPowderCharge: " + e.Message); } act = $"SetPowderCharge({_drivePowderTarget})"; }
                else { try { rc.AdvanceState(); } catch (Exception e) { Log.LogWarning("[drive] AdvanceState: " + e.Message); } act = "AdvanceState"; }

                _driveLastActionT = now;
                Log.LogInfo($"[drive] act#{_driveActions} {act} (from idx={idx} key='{key}') -> {ReloadSnapshot(g)}");
            }
            catch (Exception e) { try { Log.LogWarning("[drive] tick: " + e.Message); _driving = false; } catch { } }
        }

        // Snapshot BEFORE -> run the mutation -> snapshot AFTER, logging both. action==null = dump only.
        private static void MutateRun(string label, Action<GunController, ArtilleryReloadController> action)
        {
            try
            {
                GunController g = ResolveGunBySide(_mutTargetSide);
                if (g == null) { Log.LogWarning($"[mut] {label}: no gun for side {(_mutTargetSide == 0 ? "L" : "R")} (in a mission?)"); return; }
                ArtilleryReloadController rc = null; try { rc = g.artilleryReloadController; } catch { }
                if (rc == null) { Log.LogWarning($"[mut] {label}: gun has no artilleryReloadController"); return; }

                string side = _mutTargetSide == 0 ? "L" : "R";
                if (action == null) { Log.LogInfo($"[mut] DUMP side={side} {ReloadSnapshot(g)}"); AnimatorDump(rc, "dump"); return; }

                Log.LogInfo($"[mut] {label} side={side} BEFORE {ReloadSnapshot(g)}");
                try { action(g, rc); }
                catch (Exception e) { Log.LogWarning($"[mut] {label}: action threw: {e.Message}"); }
                Log.LogInfo($"[mut] {label} side={side} AFTER  {ReloadSnapshot(g)}");
            }
            catch (Exception e) { try { Log.LogWarning($"[mut] {label}: " + e.Message); } catch { } }
        }

        // ============================= NEW apply-path candidates (non-operated peer gun) =============================

        // [Ctrl+Alt+5] AutoReloadManager.StartAutoReload() — the game's OWN automated reload (presses the real cockpit
        // buttons via a coroutine, so the real clips play + their animation events load the chamber). This is the most
        // promising peer-apply path: it's the engine's built-in "reload a gun with no player." Resolve the component for
        // the selected gun, enable it + set desired powder, fire it, and log. The actual load unfolds over several
        // seconds — WATCH the [reload] timeline + Ctrl+Alt+0 dump for gunChamber=True / CanFire=True.
        private static void AutoReloadTest()
        {
            try
            {
                GunController g = ResolveGunBySide(_mutTargetSide);
                if (g == null) { Log.LogWarning("[auto] no gun for selected side (in a mission?)"); return; }
                string side = _mutTargetSide == 0 ? "L" : "R";
                AutoReloadManager m = ResolveAutoReload(g);
                if (m == null)
                {
                    Log.LogWarning($"[auto] side={side}: NO AutoReloadManager found for this gun (not GetComponent/Children/Parent, not in any loaded scene matching gunController). This apply path is unavailable on this gun.");
                    return;
                }
                string where = "?";
                try { where = m.gameObject != null ? m.gameObject.name : "?"; } catch { }
                bool valid = false; try { valid = m.ValidateReferences(); } catch { }
                bool slotA = false; try { slotA = m.CylinderSlotAHasShell(); } catch { }
                Log.LogInfo($"[auto] side={side}: found AutoReloadManager on '{where}' ValidateReferences={valid} CylinderSlotAHasShell={slotA}");
                Log.LogInfo($"[auto] side={side} BEFORE {ReloadSnapshot(g)}");
                try { m.desiredPowderCharges = 2; } catch { }
                try { m.autoReloadEnabled = true; } catch { }
                try { m.SetAutoReload(true); } catch { }
                try { m.StartAutoReload(); } catch (Exception e) { Log.LogWarning("[auto] StartAutoReload threw: " + e.Message); }
                bool running = false; try { running = m.isAutoReloading; } catch { }
                Log.LogInfo($"[auto] side={side}: StartAutoReload() called, isAutoReloading={running} — watch the [reload] timeline; press Ctrl+Alt+0 when it settles.");
            }
            catch (Exception e) { try { Log.LogWarning("[auto] test: " + e.Message); } catch { } }
        }

        // [Ctrl+Alt+6] Direct, animation-INDEPENDENT chamber set: Instantiate a shell prefab, ReceiveChamberedBullet it,
        // notify the gun (OnShellLoaded), set powder, refresh the fire button. Bypasses the reload clips entirely. Logs
        // each step + the prefab source used. (RUN 2 found EjectChamberedShell corrupts; this is the LOAD direction, which
        // was never cleanly tested because the old force-load path used a null prefab field — now we have shellPrefab/etc.)
        private static void DirectChamberTest()
        {
            try
            {
                GunController g = ResolveGunBySide(_mutTargetSide);
                if (g == null) { Log.LogWarning("[poke] no gun for selected side (in a mission?)"); return; }
                ArtilleryReloadController rc = null; try { rc = g.artilleryReloadController; } catch { }
                if (rc == null) { Log.LogWarning("[poke] gun has no artilleryReloadController"); return; }
                string side = _mutTargetSide == 0 ? "L" : "R";

                GameObject prefab = null; string srcName = "none";
                try { prefab = rc.shellPrefab; if (prefab != null) srcName = "rc.shellPrefab"; } catch { }
                if (prefab == null) try { prefab = rc.transferShell; if (prefab != null) srcName = "rc.transferShell"; } catch { }
                if (prefab == null) try { var css = rc.cylinderShellSelector; if (css != null) { prefab = css.lastLoadedShellPrefab; if (prefab != null) srcName = "css.lastLoadedShellPrefab"; } } catch { }
                if (prefab == null)
                {
                    Log.LogWarning($"[poke] side={side}: no shell prefab source (shellPrefab/transferShell/lastLoadedShellPrefab all null) — cannot poke a chamber. Try after a real reload once.");
                    return;
                }

                Log.LogInfo($"[poke] side={side} src={srcName} BEFORE {ReloadSnapshot(g)}");
                GameObject inst = null;
                try { inst = UnityEngine.Object.Instantiate(prefab); } catch (Exception e) { Log.LogWarning("[poke] Instantiate threw: " + e.Message); }
                try { if (inst != null) rc.ReceiveChamberedBullet(inst); } catch (Exception e) { Log.LogWarning("[poke] ReceiveChamberedBullet threw: " + e.Message); }
                try { g.OnShellLoaded(); } catch (Exception e) { Log.LogWarning("[poke] OnShellLoaded threw: " + e.Message); }
                try { g.SetPowderCharge(2); } catch (Exception e) { Log.LogWarning("[poke] SetPowderCharge threw: " + e.Message); }
                try { g.UpdateFireButtonActiveState(); } catch (Exception e) { Log.LogWarning("[poke] UpdateFireButtonActiveState threw: " + e.Message); }
                Log.LogInfo($"[poke] side={side} AFTER  {ReloadSnapshot(g)}");
            }
            catch (Exception e) { try { Log.LogWarning("[poke] test: " + e.Message); } catch { } }
        }

        // [Ctrl+Alt+8] Fire the CURRENT reload state's animator triggers directly on the controller's animators. This is
        // the most direct "play the clip" — if the peer's animators are enabled, SetTrigger(trigger) should play the
        // state's clip, whose animation events then advance the state AND transfer the shell. Logs the triggers + the
        // animator states before, and the resulting working/chamber after.
        private static void FireTriggersTest()
        {
            try
            {
                GunController g = ResolveGunBySide(_mutTargetSide);
                if (g == null) { Log.LogWarning("[trig] no gun for selected side (in a mission?)"); return; }
                ArtilleryReloadController rc = null; try { rc = g.artilleryReloadController; } catch { }
                if (rc == null) { Log.LogWarning("[trig] gun has no artilleryReloadController"); return; }
                string side = _mutTargetSide == 0 ? "L" : "R";

                // Current state + its trigger list.
                System.Collections.Generic.List<string> trigs = new System.Collections.Generic.List<string>();
                string key = "?";
                try
                {
                    var cs = rc.CurrentState;
                    if (cs != null)
                    {
                        try { key = cs.stateKey ?? "?"; } catch { }
                        var t = cs.triggers;
                        if (t != null) for (int i = 0; i < t.Count; i++) { try { var s = t[i]; if (!string.IsNullOrEmpty(s)) trigs.Add(s); } catch { } }
                    }
                }
                catch { }

                Log.LogInfo($"[trig] side={side} state='{key}' triggers=[{string.Join(",", trigs.ToArray())}] BEFORE {ReloadSnapshot(g)}");
                AnimatorDump(rc, "pre");
                if (trigs.Count == 0) { Log.LogWarning("[trig] current state has NO triggers — nothing to fire (advance may be auto/event-only here)"); return; }

                var anims = rc.animators;
                int n = 0; try { n = anims != null ? anims.Count : 0; } catch { }
                int fired = 0;
                for (int i = 0; i < n; i++)
                {
                    Animator a = null; try { a = anims[i]; } catch { }
                    if (a == null) continue;
                    foreach (var trg in trigs)
                    {
                        try { a.SetTrigger(trg); fired++; } catch (Exception e) { Log.LogWarning($"[trig] SetTrigger('{trg}') on animator {i}: {e.Message}"); }
                    }
                }
                Log.LogInfo($"[trig] side={side} fired {fired} (trigger x animator) — watch for working->True + gunChamber->True on the [reload] timeline; Ctrl+Alt+0 to dump.");
            }
            catch (Exception e) { try { Log.LogWarning("[trig] test: " + e.Message); } catch { } }
        }

        // Dump the reload controller's animators' enabled/active/culling state — the crux diagnostic for whether a clip
        // CAN play on a gun nobody is operating. If these are disabled or culled, no trigger will play a clip.
        private static void AnimatorDump(ArtilleryReloadController rc, string tag)
        {
            try
            {
                if (rc == null) { Log.LogInfo($"[anim] ({tag}) rc null"); return; }
                var anims = rc.animators;
                int n = 0; try { n = anims != null ? anims.Count : 0; } catch { }
                Log.LogInfo($"[anim] ({tag}) reload animators count={n}");
                for (int i = 0; i < n; i++)
                {
                    Animator a = null; try { a = anims[i]; } catch { }
                    if (a == null) { Log.LogInfo($"[anim]   [{i}] null"); continue; }
                    string nm = "?"; bool en = false, act = false; float sp = 0f; string cm = "?";
                    try { nm = a.gameObject != null ? a.gameObject.name : "?"; } catch { }
                    try { en = a.enabled; } catch { }
                    try { act = a.gameObject != null && a.gameObject.activeInHierarchy; } catch { }
                    try { sp = a.speed; } catch { }
                    try { cm = a.cullingMode.ToString(); } catch { }
                    Log.LogInfo($"[anim]   [{i}] '{nm}' enabled={en} activeInHierarchy={act} speed={sp:0.##} culling={cm}");
                }
            }
            catch (Exception e) { try { Log.LogWarning("[anim] dump: " + e.Message); } catch { } }
        }

        // Resolve an AutoReloadManager for a gun: component on the gun, its children, its parents, else any loaded
        // instance whose gunController points back to this gun.
        private static AutoReloadManager ResolveAutoReload(GunController g)
        {
            if (g == null) return null;
            AutoReloadManager m = null;
            try { m = g.GetComponent<AutoReloadManager>(); } catch { }
            if (m == null) try { m = g.GetComponentInChildren<AutoReloadManager>(true); } catch { }
            if (m == null) try { m = g.GetComponentInParent<AutoReloadManager>(); } catch { }
            if (m == null)
            {
                try
                {
                    var all = Resources.FindObjectsOfTypeAll<AutoReloadManager>();
                    if (all != null)
                    {
                        for (int i = 0; i < all.Length; i++)
                        {
                            var cand = all[i];
                            GunController cg = null; try { cg = cand.gunController; } catch { }
                            if (cg != null && cg.Pointer == g.Pointer) { m = cand; break; }
                        }
                    }
                }
                catch { }
            }
            return m;
        }

        // Resolve the GunController for a side (0=L,1=R) from the live turret guns list, matching SideOf by name.
        private static GunController ResolveGunBySide(int side)
        {
            try
            {
                TurretController tc = null; try { tc = TurretController.Instance; } catch { }
                if (tc == null) return null;
                var guns = tc.guns;
                if (guns == null) return null;
                int n = guns.Count;
                for (int i = 0; i < n; i++)
                {
                    GunController g = null; try { g = guns[i]; } catch { }
                    if (g == null) continue;
                    if (SideOf(g) == side) return g;
                }
            }
            catch { }
            return null;
        }

        // ============================= Helpers =============================

        // Map a GunController to side index (0=Left, 1=Right). Defaults to 0 on any failure or ambiguity.
        // Mirrors the name-based disambiguation used by CoopControls.ScanGuns.
        private static int SideOf(GunController g)
        {
            try
            {
                if (g == null) return 0;
                string nm = g.name;
                if (nm == null) return 0;
                string lo = nm.ToLowerInvariant();
                if (lo.Contains("right")) return 1;
                // "left" or anything else -> 0
                return 0;
            }
            catch { return 0; }
        }
    }
}
#endif
