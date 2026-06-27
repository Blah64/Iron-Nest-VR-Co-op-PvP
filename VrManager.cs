using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Top-level driver attached to a persistent GameObject. Lazily brings up the D3D11 bridge and
    /// OpenXR session (retrying until a headset/runtime is present), then runs the per-frame loop.
    /// Phase 2: bring up the session and log head pose. Phase 3 adds the stereo camera rig.
    /// </summary>
    public class VrManager : MonoBehaviour
    {
        public VrManager(IntPtr ptr) : base(ptr) { }

        private static ManualLogSource Log => Plugin.Logger;

        private D3D11Bridge _bridge;
        private bool _bridgeOk;
        private XrSession _xr;
        private CameraRig _rig;
        private CockpitInteractor _interactor;
        private Locomotion _locomotion;
        private HudFollower _hud;
        private GrabManager _grab;
        private HandVisuals _hands;
        private HandManipulator _handManip;
        private VrSettingsMenu _menu;
        private VrPopup _popup;
        private MapScope _scope;
        private bool _prevChord;
        private float _appliedRenderScale;
        private bool _xrReady;
        private bool _frameActive;   // this frame's input-active state, cached for LateUpdate re-application
        private float _nextXrTry;
        private int _frameFailStreak;   // consecutive failing frames (xrWaitFrame runtime error)
        private bool _vrAbandoned;      // VR came up but the runtime kept erroring per-frame → flatscreen for the rest of this session
        private string _lastError;
        private float _nextErrLog;
        private float _nextPoseLog;
        private float _loopDiagNext;   // same-machine loopback test: per-second focus/runInBackground/timeScale log
        private float _focusedSince = -1f; // unscaledTime the session last became focused (LowSpec settle window)
        private float _lowSpecSlowAccum;   // continuous seconds spent below LowSpecFpsTrigger while focused
        private int _savedQualityLevel = -1; // game QualitySettings level LowSpec lowered (-1 = untouched, restore in TeardownVr)

        // Phase 1 scene probe (still handy).
        private float _nextScan;
        private bool _loggedScene;

        private void Awake()
        {
            Log.LogInfo("VrManager active. Bringing up OpenXR (needs a headset + active OpenXR runtime).");
        }

        // Flatscreen co-op lobby browser (crossplay counterpart to the in-VR menu page) + the join toast.
        private void OnGUI()
        {
            try { LobbyGui.Draw(); } catch { }
            try { PvpEffects.DrawFlat(); } catch { }  // PvP damage red-flash (under the HUD/toast; shipping game-feel)
            try { Notify.DrawFlat(); } catch { }   // non-focus-pulling "X joined" toast (flatscreen)
            try { PvpHud.DrawFlat(); } catch { }   // dev PvP duel readout (non-public builds, in a PvP mission)
#if !PUBLIC_BUILD
            try { PvpTeams.DrawPanel(); } catch { }  // dev flatscreen team roster / slot picker (PvP lobby; VR panel TBD)
#endif
        }

        // The game is an FPS that locks the OS cursor to centre for mouselook, so the flatscreen lobby
        // panel is unclickable unless we free the cursor and stop the look while it's open. Done in
        // LateUpdate (after the game's own Update) so we win the per-frame cursor-lock race.
        private bool _lookFrozen;
        private void LateUpdate()
        {
            bool lobbyFlat = LobbyGui.Shown && !_xrReady;
            LobbyGui.FlatInteractive = lobbyFlat;
            // Local two-window co-op testing. By DEFAULT we touch nothing, so the game's normal mouselook/camera
            // works. When you need the OS cursor — to click UI or move the mouse to the OTHER window (an
            // in-mission FPS otherwise locks it to the focused window's centre and hides it) — either HOLD LEFT
            // ALT (momentary) or press Ctrl+F5 (sticky, LoopbackTransport.FreeCursor). While free we drop FPS
            // look so the camera doesn't spin. Gated to CoopLoopback + an Active link → shipped play is untouched.
            bool testLink = !_xrReady && Config.CoopLoopback && LoopbackTransport.Active;
            bool freeCursor = lobbyFlat || (testLink && (LoopbackTransport.FreeCursor || AltHeld()));
            if (freeCursor)
            {
                try { UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None; UnityEngine.Cursor.visible = true; } catch { }
                SetFpsLook(false);
                _lookFrozen = true;
            }
            else if (_lookFrozen)
            {
                // Back to gameplay: re-lock + hide the cursor ourselves so mouselook resumes. The game may lock
                // the cursor only once on entering a mission, so after we forced it unlocked it might not re-lock
                // on its own — leaving the camera frozen. Re-asserting it here makes the camera reliably move.
                try { UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.Locked; UnityEngine.Cursor.visible = false; } catch { }
                SetFpsLook(true);   // hand control back to the game
                _lookFrozen = false;
            }

            // Phase 3: apply the peer's control visuals + snap turret/gun state here, AFTER the game's Update
            // (so our snap wins the frame); re-applies the turret transform immediately so the visible
            // rotating cockpit matches. Map token positions are applied here too (after the game's drag logic).
            CoopControls.LateApply();
            CoopMap.LateApply();
            CoopPunchcards.LateApply();   // apply peer-owned punchcard poses after the game's drag logic

            // Re-assert the HUD clipboard + watch poses HERE (after the game's menu Animator + Main Camera
            // motion this frame), so the end-of-frame eye render sees them attached to the hand/waist instead
            // of dragged away. Their holders are children of Main Camera, so an Update-phase placement is stale
            // by render time; world manuals (not Main-Camera children, no Animator) don't need this.
            if (_xrReady && _frameActive && _grab != null && _rig != null)
            {
                try { _grab.LateApply(_rig, _xr.Input); } catch { }
            }
            // Re-assert the held cockpit LEVER pose here too — the game re-poses the lever meshes it animates AFTER our
            // Update, overwriting the swing before render (a confirmed-correct lever moved in our log yet looked static).
            if (_xrReady && _frameActive && _handManip != null)
            {
                try { _handManip.LateApply(_hands); } catch { }
            }

            // Same-machine test: the game pauses its SCALED-time sim/animations on an UNFOCUSED window
            // (Time.timeScale → 0) even though the player loop keeps running (runInBackground). Our net layer
            // uses unscaled time so it survives, but the background instance's turret motion + animations freeze.
            // Force timeScale back to 1 on a live test link (in LateUpdate, after the game's Update, so we win
            // the frame). Gated on the test flag so normal play is untouched.
            try { if (Config.CoopLoopback && LoopbackTransport.Active && Time.timeScale != 1f) Time.timeScale = 1f; } catch { }
        }

        private static void SetFpsLook(bool enabled)
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<FirstPersonController>(), FindObjectsSortMode.None);
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var f = arr[i].TryCast<FirstPersonController>();
                        if (f != null) f.cameraCanMove = enabled;
                    }
            }
            catch { }
        }

        private int _stFrame;
        private bool _stStarted;

        // Frame-time tracker: distinguishes uniform slowness (GPU/scene bound) from hitches (a stall spiking
        // worst-ms). Co-op staleness + smoothness depend on framerate, so log it during bring-up.
        private float _perfNext;
        private int _perfFrames;
        private float _perfWorst;

        private void PerfTick()
        {
            float dt = Time.unscaledDeltaTime;
            _perfFrames++;
            if (dt > _perfWorst) _perfWorst = dt;
            if (Time.unscaledTime >= _perfNext)
            {
                float span = Config.PerfLogIntervalSec;
                float fps = _perfFrames / Mathf.Max(span, 0.001f);
                Log.LogInfo($"[perf] fps~{fps:F1} avg={(1000f * span / Mathf.Max(_perfFrames, 1)):F0}ms worst={_perfWorst * 1000f:F0}ms xr={_xrReady}");
                _perfFrames = 0; _perfWorst = 0f; _perfNext = Time.unscaledTime + span;
            }
        }

        // Same-machine test ONLY (runs while a loopback link is up — never for a normal flatscreen player, who
        // never connects it, so flatscreen parity is preserved). Two standalone instances can't both hold
        // EXCLUSIVE fullscreen on one display: when the host (re)grabs it — e.g. on a mission scene load — the
        // background client is knocked out of the display mode and Unity drops it to a tiny resolution (the
        // "client window shrank when the host launched the mission" report). We call SetResolution NOWHERE else;
        // the game owns it. Fix: keep this instance in a plain WINDOW at the configured Coop test size (the rig
        // runs two windowed 2560x1080), which never contends for the exclusive display. Re-applies only on drift
        // (≤1×/s) — so after one correction it goes quiet, and a deliberate same-size window is left untouched.
        private void KeepWindowedForTest()
        {
            if (!Config.CoopLoopback || !Config.CoopTestForceWindow) return;
            try
            {
                int wantW = Config.CoopTestWindowW, wantH = Config.CoopTestWindowH;
                if (wantW <= 0 || wantH <= 0) return;
                var mode = Screen.fullScreenMode;
                int w = Screen.width, h = Screen.height;
                bool drift = mode != FullScreenMode.Windowed || Mathf.Abs(w - wantW) > 8 || Mathf.Abs(h - wantH) > 8;
                if (drift)
                {
                    Screen.SetResolution(wantW, wantH, FullScreenMode.Windowed);
                    Log.LogInfo($"[loop] forced windowed {wantW}x{wantH} (was mode={mode} {w}x{h}) — avoids the 2-instance exclusive-fullscreen resize");
                }
            }
            catch (Exception e) { Log.LogWarning("[loop] window mode: " + e.Message); }
        }

        private bool _loggedEnv;
        // One-time environment banner so a tester's log self-proves the two things that decide the D3D
        // race hypothesis — the launch options actually applied, and the real graphics threading mode —
        // plus GPU/VRAM and where the crash-proof trace files are written. Logged from the first Update
        // (Unity's graphics device is fully up by then).
        private void LogEnvironmentOnce()
        {
            if (_loggedEnv) return;
            _loggedEnv = true;
            try
            {
                Log.LogInfo("[env] cmdline: " + Environment.CommandLine);
                Log.LogInfo($"[env] gpu='{SystemInfo.graphicsDeviceName}' vendor='{SystemInfo.graphicsDeviceVendor}' " +
                            $"api={SystemInfo.graphicsDeviceType} ver='{SystemInfo.graphicsDeviceVersion}' vram={SystemInfo.graphicsMemorySize}MB");
                Log.LogInfo($"[env] threadingMode={SystemInfo.renderingThreadingMode} gfxMultiThreaded={SystemInfo.graphicsMultiThreaded} " +
                            $"sysRAM={SystemInfo.systemMemorySize}MB cpu='{SystemInfo.processorType}' x{SystemInfo.processorCount}");
                Log.LogInfo("[env] trace+heartbeat dir: " + Dbg.Directory);
            }
            catch (Exception e) { Log.LogWarning("[env] dump failed: " + e.Message); }
        }

        private bool _autoTuned;
        // Pick a weak-GPU-friendly default render scale once, from the first Update (graphics device up,
        // SystemInfo populated) and before XR bring-up sizes the eye swapchains. Respects an explicit
        // cfg/menu value; only lowers a fresh install. See Config.AutoTuneRenderScale.
        private void AutoTuneOnce()
        {
            if (_autoTuned) return;
            _autoTuned = true;
            Config.AutoTuneRenderScale();
        }

        // Auto-enable the LowSpec eye profile (Config.EyeLowSpec) when a weak system can't keep up. Once the
        // headset is focused and running below Config.LowSpecFpsTrigger for Config.LowSpecSustainSec straight
        // seconds — past a LowSpecGraceSec settle window so the scene-load spike doesn't trip it — strip the
        // per-eye shadows/post/MSAA via CameraRig.ApplyEyeQuality. One-way latch for the process (no shadow
        // flicker). Skipped if the cfg pinned EyeLowSpec (EyeLowSpecExplicit) or auto is off. De-focus
        // (dashboard up / headset off) pauses AND resets the timer, so the multi-second focus-loss freezes
        // can't latch it — we judge in-game framerate only.
        private void AutoLowSpecTick()
        {
            // Already on (pinned via cfg, or latched earlier this session): once focused, make sure the global
            // game-quality drop is applied too (the per-eye trim is handled at camera creation), then we're done.
            if (Config.EyeLowSpec)
            {
                if (_savedQualityLevel < 0 && _xr != null && _xr.IsFocused) ApplyLowSpecQuality();
                return;
            }
            if (Config.EyeLowSpecExplicit || !Config.EyeLowSpecAuto) return;
            bool focused = _xr != null && _xr.IsFocused && _rig != null && _rig.Ready;
            if (!focused) { _focusedSince = -1f; _lowSpecSlowAccum = 0f; return; }

            float now = Time.unscaledTime;
            if (_focusedSince < 0f) { _focusedSince = now; _lowSpecSlowAccum = 0f; }
            if (now - _focusedSince < Config.LowSpecGraceSec) return;   // ignore the load-in spike window

            float dt = Time.unscaledDeltaTime;
            float fps = dt > 0.0001f ? 1f / dt : 999f;
            if (fps < Config.LowSpecFpsTrigger) _lowSpecSlowAccum += dt;
            else _lowSpecSlowAccum = Mathf.Max(0f, _lowSpecSlowAccum - 2f * dt); // recover faster than it builds

            if (_lowSpecSlowAccum >= Config.LowSpecSustainSec)
            {
                Config.EyeLowSpec = true;
                try { _rig.ApplyEyeQuality(); } catch (Exception e) { Log.LogWarning("[perf] LowSpec apply failed: " + e.Message); }
                ApplyLowSpecQuality();
                Log.LogInfo($"[perf] LowSpec AUTO-ENABLED — sustained <{Config.LowSpecFpsTrigger:0} fps for {Config.LowSpecSustainSec:0}s. " +
                            "Eye shadows/post/MSAA off + game quality lowered (flatscreen untouched). Set EyeLowSpec=false in IronNestVR.cfg to keep full quality.");
            }
        }

        // Force the GAME'S OWN graphics quality level down (QualitySettings) while LowSpec is active — the
        // tester-proven "lower the game settings first" workaround, automated. Broader than the per-eye trim:
        // it cuts pixel-light count / LOD bias / texture resolution / shadow cascades the eye cameras inherit
        // globally (the per-eye shadow toggle alone didn't move the tester's render bucket). Global, so the
        // prior level is saved here and restored in TeardownVr (covers the VR→flatscreen fallback). Index 0 =
        // Unity's conventional lowest ("Very Low"); names are logged so it's verifiable + cfg-tunable
        // (Config.LowSpecQualityLevel) if the game orders its levels unusually. Idempotent (guards on saved).
        private void ApplyLowSpecQuality()
        {
            if (!Config.LowSpecForceQualityLevel || _savedQualityLevel >= 0) return;
            try
            {
                var names = QualitySettings.names;
                int count = names != null ? names.Length : 0;
                int target = Config.LowSpecQualityLevel;
                if (target < 0 || target >= count) target = 0;
                int cur = QualitySettings.GetQualityLevel();
                if (count <= 1 || target >= cur)
                {
                    Log.LogInfo($"[perf] LowSpec: game quality NOT lowered (cur idx {cur}, {count} level(s), target idx {target} — already at/below).");
                    return;
                }
                string list = "";
                for (int i = 0; i < count; i++) list += (i > 0 ? ", " : "") + names[i];
                _savedQualityLevel = cur;
                QualitySettings.SetQualityLevel(target, true);   // applyExpensiveChanges=true so texture/LOD/shadow drops actually take effect
                Log.LogInfo($"[perf] LowSpec: game quality '{names[cur]}'(idx {cur}) -> '{names[target]}'(idx {target}); levels=[{list}]; restored on VR exit.");
            }
            catch (Exception e) { Log.LogWarning("[perf] LowSpec quality apply failed: " + e.Message); _savedQualityLevel = -1; }
        }

        // Put the game's graphics quality back to whatever it was before LowSpec lowered it. No-op if untouched.
        private void RestoreQuality()
        {
            if (_savedQualityLevel < 0) return;
            try { QualitySettings.SetQualityLevel(_savedQualityLevel, true); Log.LogInfo($"[perf] restored game quality level to idx {_savedQualityLevel}."); }
            catch (Exception e) { Log.LogWarning("[perf] quality restore failed: " + e.Message); }
            _savedQualityLevel = -1;
        }

        private bool _mirrorBlanked;
        private int _mirrorSavedMask;
        // Blank the desktop mirror (Camera.main) while VR is active: it's a full 3rd scene render the
        // headset user never sees. Setting cullingMask=0 keeps the camera ENABLED — so Camera.main still
        // resolves and the rig keeps reading its transform — but it submits no draws. Only after the eye
        // cameras exist: they copy main.cullingMask at creation (EnsureCameras), so zeroing it earlier would
        // blank the headset too. Re-asserted each frame (the game may reset the mask on a scene change).
        // Restored in TeardownVr. VR-only → flatscreen rendering is never touched.
        private void ApplyDesktopMirror()
        {
            var main = Camera.main;
            if (!Config.DisableDesktopMirror)
            {
                if (_mirrorBlanked && main != null) main.cullingMask = _mirrorSavedMask;
                _mirrorBlanked = false;
                return;
            }
            if (main == null || _rig == null || !_rig.Ready) return; // wait until the eyes captured the real mask
            if (!_mirrorBlanked)
            {
                _mirrorSavedMask = main.cullingMask;
                _mirrorBlanked = true;
                Log.LogInfo($"[perf] desktop mirror blanked while in VR (saved cullingMask 0x{_mirrorSavedMask:X}). " +
                            "Set DisableDesktopMirror=false in IronNestVR.cfg to keep a monitor spectator view.");
            }
            if (main.cullingMask != 0) main.cullingMask = 0;
        }

        private Camera _lbCam;
        private float _lbNextRender;
        private float _lbNextSearch;
        private bool _lbThrottleLogged;
        // The [cams] dump found 'LeaderboardCam' rendering a full-scene-mask view to a texture EVERY frame
        // (~3ms / ~20% of the VR render budget) for a scoreboard display. We gate its per-frame render:
        //   • Config.DisableLeaderboardCam (diagnostic) — hard off.
        //   • Config.LeaderboardCamThrottle (shipped)   — render only every 1/Hz s (enable for one frame,
        //     skip the rest); the camera's RT keeps the last frame between updates, so the board still shows.
        // The camera is (re)acquired on a 1 s search so a scene change / late spawn is picked up; access is
        // wrapped so a destroyed handle just triggers a re-acquire. Driven from the VR loop ⇒ VR-only.
        private void ApplyLeaderboardCam()
        {
            bool want = Config.DisableLeaderboardCam || Config.LeaderboardCamThrottle;
            if (!want)
            {
                if (_lbCam != null) { try { if (!_lbCam.enabled) _lbCam.enabled = true; } catch { _lbCam = null; } }
                return;
            }
            if (_lbCam == null && Time.unscaledTime >= _lbNextSearch)
            {
                _lbNextSearch = Time.unscaledTime + 1f;
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Camera>(), FindObjectsSortMode.None);
                for (int i = 0; i < (arr?.Length ?? 0); i++)
                {
                    var c = arr[i].TryCast<Camera>();
                    if (c != null && c.name == "LeaderboardCam") { _lbCam = c; break; }
                }
            }
            if (_lbCam == null) return;
            try
            {
                if (Config.DisableLeaderboardCam)
                {
                    if (_lbCam.enabled) { _lbCam.enabled = false; Log.LogInfo("[perf] LeaderboardCam DISABLED (diag)"); }
                    return;
                }
                // Throttle: render this frame only when the interval has elapsed; otherwise skip it.
                float now = Time.unscaledTime;
                if (now >= _lbNextRender)
                {
                    _lbNextRender = now + 1f / Mathf.Max(Config.LeaderboardCamHz, 1f);
                    if (!_lbCam.enabled) _lbCam.enabled = true;
                    if (!_lbThrottleLogged) { _lbThrottleLogged = true; Log.LogInfo($"[perf] LeaderboardCam throttled to {Config.LeaderboardCamHz:0.#} Hz (was every frame)."); }
                }
                else if (_lbCam.enabled) _lbCam.enabled = false;
            }
            catch { _lbCam = null; } // destroyed (scene change) — re-acquire on the next search
        }

        private bool _loggedCams;
        // One-time dump of every active camera and where it renders. Resolves what the desktop is still
        // drawing after the mirror blank (skybox via clearFlags, vs a SECOND screen camera the eye rig
        // doesn't replicate) and shows the true render topology — how many full-scene passes per frame.
        private void LogCamerasOnce()
        {
            if (_loggedCams || _rig == null || !_rig.Ready) return;
            _loggedCams = true;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Camera>(), FindObjectsSortMode.None);
                int n = arr?.Length ?? 0;
                Log.LogInfo($"[cams] {n} active camera(s) — SCREEN targets render to the monitor, RT targets are our eyes/pointers:");
                for (int i = 0; i < n; i++)
                {
                    var c = arr[i].TryCast<Camera>();
                    if (c == null) continue;
                    bool toRt = c.targetTexture != null;
                    var r = c.rect;
                    Log.LogInfo($"[cams]  '{c.name}' depth={c.depth} clear={c.clearFlags} mask=0x{(uint)c.cullingMask:X8} " +
                                $"target={(toRt ? "RT" : "SCREEN")} rect=({r.x:0.##},{r.y:0.##},{r.width:0.##},{r.height:0.##}) " +
                                $"enabled={c.enabled} tag='{c.tag}'");
                }
            }
            catch (Exception e) { Log.LogWarning("[cams] dump failed: " + e.Message); }
        }

        private void Update()
        {
            PerfProbe.UpdateBegin();
            LogEnvironmentOnce();
            AutoTuneOnce();
            RenderThreadProbe.Tick();   // PLANXR feasibility test (self-terminating; logs one [rtprobe] RESULT)
            PerfTick();
            AutoLowSpecTick();   // weak-system relief: latch the LowSpec eye profile if fps stays low while focused
            ScanSceneOnce();
            Diagnostics.Tick();
            MapToolsProbe.Tick();  // F1 dump / Shift+F1 live-test: decouple map-tools palette from the focus camera
            PvpProbe.Tick();       // PvP plan Phase 0 probes (Ctrl+Shift+1/2/3/4/0/9; inert unless Config.PvpProbe)
            SteamNet.Tick();   // Phase 1 co-op: Steam lobby create/browse/join (F9/F10/F11/F12)
            LobbyGui.HandleInput();  // flatscreen panel clicks via the new Input System (legacy is off)
#if !PUBLIC_BUILD
            try { PvpTeams.HandleInput(); } catch { }   // flatscreen team-slot clicks (while the F7 panel frees the cursor)
#endif

            // Phase 2 co-op: P2P pose channel + remote avatar. Tick (peer discovery + receive) and the
            // avatar update run in BOTH modes; the VR head+hand send happens in the frame loop below, the
            // flatscreen camera-pose send happens here. F6 toggles the solo render self-test.
            if (KeyDown(UnityEngine.InputSystem.Key.F6)) { CoopP2P.SelfTest = !CoopP2P.SelfTest; Log.LogInfo("[p2p] self-test " + (CoopP2P.SelfTest ? "ON" : "OFF")); }
            LoopbackTransport.PollKeys();   // same-machine co-op test link: Ctrl+F2 connect (both windows) / Ctrl+F3 stop
            // Re-assert every frame while the test transport is enabled, in case the game resets it on focus
            // change — a single un-set would freeze the unfocused instance and silently kill its half of sync.
            try { if (Config.CoopLoopback && !Application.runInBackground) Application.runInBackground = true; } catch { }
            // Per-second state line so a same-machine test PROVES what the background window is doing: if these
            // keep printing while a window is unfocused, the player loop is alive (runInBackground works); a
            // timeScale of 0 there means the game paused its scaled-time sim/animations (which we force back to
            // 1 in LateUpdate). A GAP in these lines instead = the loop really is paused (runInBackground issue).
            if (LoopbackTransport.Active && Time.unscaledTime >= _loopDiagNext)
            {
                _loopDiagNext = Time.unscaledTime + 1f;
                KeepWindowedForTest();   // stop the 2-instance exclusive-fullscreen fight that shrinks the bg window on scene load
                try { Log.LogInfo($"[loop] state focused={Application.isFocused} runInBackground={Application.runInBackground} timeScale={Time.timeScale:0.##} mode={Screen.fullScreenMode} res={Screen.width}x{Screen.height} connected={LoopbackTransport.Connected}"); } catch { }
            }
            CoopP2P.Tick(Time.unscaledDeltaTime);
            CoopControls.Tick(Time.unscaledDeltaTime);   // Phase 3: detect local control drags + transmit
            CoopClipboard.Tick(Time.unscaledDeltaTime);  // Phase 3: replicate HUD clipboard contents
            CoopMap.Tick(Time.unscaledDeltaTime);        // Phase 3: replicate tactical-map token placements
            CoopEntities.Tick(Time.unscaledDeltaTime);   // Phase 4: replicate host mission entities to the client
            CoopScene.Tick(Time.unscaledDeltaTime);      // Phase 4: replicate mission/scene transitions (host drives)
            CoopScore.Tick(Time.unscaledDeltaTime);      // Phase 4: replicate score/requisition (host-authoritative, applied out-of-mission)
            CoopPunchcards.Tick(Time.unscaledDeltaTime); // Phase 4: host-authoritative punchcard deck + redemption
            CoopNetDiag.Tick(Time.unscaledDeltaTime);    // REVIEW-fix: cross-machine desync detector (diagnostic only)
            PvpTeams.Tick(Time.unscaledDeltaTime);       // PvP teams: host-authoritative roster (inert unless Config.PvpActive)
            PvpMatch.Tick(Time.unscaledDeltaTime);       // PvP Phase 1: match-mode coordinator (inert unless Config.PvpActive)
            PvpPlayers.Tick(Time.unscaledDeltaTime);     // PvP Phase 1: player-as-entity presence + position sync
            if (!_xrReady)
            {
                var fcam = Camera.main;
                if (fcam != null)
                    SendLocalPose(fcam.transform.position, fcam.transform.rotation, false,
                                  Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.identity);
                RemoteAvatar.SetViewer(Vector3.zero, false);   // flatscreen: name tags face Camera.main
            }
            RemoteAvatar.Update();

            if (Config.SelfTestRender) { RunSelfTest(); return; }

            if (!_xrReady)
            {
                // VR was abandoned after the runtime kept failing per-frame: stay flatscreen for the rest of
                // the session. Re-initialising would recreate the same session and re-enter the freeze, so we
                // do NOT retry here (unlike the no-headset case below, where init quietly fails and VR can
                // still come up later if a headset appears).
                if (_vrAbandoned) return;
                if (Time.unscaledTime >= _nextXrTry)
                {
                    _nextXrTry = Time.unscaledTime + Config.XrRetryIntervalSec;
                    TryInitVr();
                }
                return;
            }

            // --- VR frame loop (canonical OpenXR order) ---
            try
            {
                PerfProbe.FrameBegin();
                Dbg.Beat("poll");
                _xr.PollEvents();
                if (_xr.ExitRequested) { Log.LogWarning("OpenXR requested exit; tearing down VR."); TeardownVr(); return; }

                if (RecenterPressed()) _rig.Recenter();

                // Apply a queued resolution-scale change (from the menu) between frames: rebuild the
                // swapchains + eye RTs at the new size. Only when the menu is closed, to avoid churn.
                if (!_menu.IsOpen && Mathf.Abs(Config.RenderScale - _appliedRenderScale) > 0.001f)
                {
                    if (_xr.ResizeEyes(out var rerr)) { _rig.Destroy(); _appliedRenderScale = Config.RenderScale; }
                    else Log.LogWarning("[resize] failed: " + rerr);
                }

                bool shouldRender = _xr.BeginFrameLocateViews();
                Dbg.Beat($"begin shouldRender={shouldRender} focused={_xr.IsFocused}");

                // Stop the two auto-render eye cameras from drawing the full scene while the runtime says we
                // shouldn't render (dashboard up / headset off) — otherwise they keep rendering every Unity
                // frame for nothing. No-op in render-request mode.
                _rig.SetEyeCamerasEnabled(shouldRender);

                // Blank the wasted full-res desktop mirror render while in VR (cullingMask=0). Runs after the
                // eyes exist (gated inside) so the headset keeps the real mask. The single biggest GPU/draw win.
                ApplyDesktopMirror();
                LogCamerasOnce();
                if (_rig != null && _rig.Ready) _rig.SetEyeSceneRender(!Config.EyeCullTest); // diag: eyes skybox-only
                ApplyLeaderboardCam();                                                        // diag: kill the leaderboard cam

                // Runtime-failure fallback. A present-but-broken runtime (e.g. headset not actually streaming)
                // makes xrWaitFrame block for seconds and return an error EVERY frame — the game freezes at
                // <1 fps instead of running. This is NOT the no-headset case (which never gets a session this
                // far); the session came up and is now erroring. After a few consecutive failures, abandon VR
                // and run flatscreen. A benign not-yet-visible frame (WaitFrame success, ShouldRender=0) resets
                // the streak, so normal Synchronized→Visible→Focused bring-up is unaffected.
                if (_xr.LastFrameFailed)
                {
                    if (++_frameFailStreak >= Config.XrFrameFailLimit)
                    {
                        Log.LogWarning($"[vr] OpenXR frame loop failing ({_xr.LastWaitResult}) — runtime is up but not " +
                                       "presenting (headset not streaming?). Abandoning VR; continuing in flatscreen. " +
                                       "Reconnect the headset and relaunch to use VR.");
                        _vrAbandoned = true;
                        TeardownVr();
                        return;
                    }
                }
                else _frameFailStreak = 0;

                // Controller input + cockpit interaction. Pose locate needs the frame's predicted
                // display time (set by BeginFrameLocateViews) and a focused session. The interactor is
                // ticked EVERY frame (even unfocused) so it restores the game's cursor when we step away.
                float dt = Time.unscaledDeltaTime;
                bool active = _xr.IsFocused && _xr.InputReady;
                _frameActive = active;   // cached so LateUpdate can re-assert held-prop poses after the game's animator + camera move
                if (active)
                {
                    _xr.Input.Sync();
                    _xr.Input.LocatePoses(_xr.LocalSpace, _xr.PredictedDisplayTime);
                    if (_xr.Input.RecenterEdge) _rig.Recenter();

                    // Snap the rig origin to the game camera NOW — BEFORE we place the held clipboard + hands —
                    // so they're positioned with the exact origin the eyes render from this frame. The origin
                    // used to be updated only late (interactor + RenderAndSubmit), one frame behind placement,
                    // which made the held board + hands lag the world during locomotion. (Re-applied after
                    // _locomotion.Tick below, which may add this frame's view-turn yaw.)
                    _rig.UpdateOrigin();

                    // Comfort vignette: drive it EVERY active frame (even with the menu/popup open, where
                    // Locomotion.Tick is skipped) so the on-screen toggle gives immediate feedback.
                    _locomotion.DriveVignette(_xr.Input, _rig, dt);

                    // Click BOTH thumbsticks at once to open/close the VR settings menu.
                    bool chord = _xr.Input.StickClickL && _xr.Input.StickClickR;
                    if (chord && !_prevChord) _menu.Toggle(_rig);
                    _prevChord = chord;

                    if (_menu.IsOpen)
                    {
                        _menu.Tick(_xr.Input, _rig); // menu owns the trigger while open
                        _handManip.Tick(_xr.Input, _rig, _hands, false); // release any held control
                        // Grab-to-place calibration runs with the menu open (the menu uses the trigger, grab
                        // uses the grip), so you can arm it on the HUD tab and immediately place the prop.
                        if (_grab.IsCalibrating) _grab.Tick(_xr.Input, _rig, active);
                    }
                    else
                    {
                        // Screen-space confirmation popups ("I understand", exit-mission, …) are modal and
                        // invisible in VR; mirror + operate them here. While one is up it owns the trigger
                        // (like the settings menu), so locomotion/grab/cockpit clicks stand down.
                        _popup.Tick(_xr.Input, _rig);
                        if (_popup.Active)
                        {
                            _handManip.Tick(_xr.Input, _rig, _hands, false); // release any held control
                        }
                        else
                        {
                            HandleMenuEsc(_xr.Input);
                            _locomotion.Tick(_xr.Input, _rig, dt);
                            _rig.UpdateOrigin(); // re-freeze the origin AFTER this frame's move + view-turn, so held props match the render
                            // Gravity-glove dial/lever grab runs first; while it holds a control it owns the
                            // right grip, so the prop GrabManager stands down to avoid fighting over it.
                            _handManip.Tick(_xr.Input, _rig, _hands, true);
                            if (!_handManip.Active) { _grab.Tick(_xr.Input, _rig, active); MapTools.Tick(_xr.Input, _grab); }
                        }
                    }
                    _grab.ReconcileScale(); // live clipboard size, even with the menu open
                    _hands.SetClipboardHold(_grab.ClipboardHoldHand); // pose the holding hand as cradling the board
                    _hands.SetHandleGrip(_handManip.HandleGripHand);  // looser finger curl while gripping a lever/handle
                    _hands.Tick(_xr.Input, _rig, active); // pose hand models (after manip sets overrides)

                    // Co-op: stream our head + hand world poses (and finger curl) to the peer (mirrored as
                    // fake-remote if F6). The head pose also drives the remote name-tag billboard.
                    if (_rig.TryGetHeadPose(out var coHp, out var coHr))
                    {
                        RemoteAvatar.SetViewer(coHp, true);   // name tags face the VR head, not Camera.main
                        var origin = _rig.OriginTransform;
                        Vector3 lp = coHp, rp = coHp; Quaternion lr = coHr, rr = coHr;
                        if (origin != null)
                        {
                            if (_xr.Input.GripValidL) { lp = PoseWorldPos(_xr.Input.GripPoseL, origin); lr = PoseWorldRot(_xr.Input.GripPoseL, origin); }
                            if (_xr.Input.GripValid)  { rp = PoseWorldPos(_xr.Input.GripPose, origin);  rr = PoseWorldRot(_xr.Input.GripPose, origin); }
                        }
                        float lci = 0f, lco = 0f, rci = 0f, rco = 0f;
                        bool curl = _hands != null;
                        if (curl)
                        {
                            lci = _hands.LeftCurlIndex; lco = _hands.LeftCurlOther;
                            rci = _hands.RightCurlIndex; rco = _hands.RightCurlOther;
                        }
                        SendLocalPose(coHp, coHr, true, lp, lr, rp, rr, curl, lci, lco, rci, rco);
                    }
                }
                bool uiModal = _menu.IsOpen || _popup.Active;
                _interactor.Apply(_xr.Input, _rig, dt, active, active && (uiModal || _handManip.Active), uiModal);

                // Map magnifier "scope": a zoomed view of the map around where the controller points. Render-
                // only; suppressed while a VR menu/popup owns the trigger. Hides itself when not over the map.
                _scope.Tick(_xr.Input, _rig, _grab, active && !uiModal);

                if (shouldRender) _xr.RenderAndSubmit(_rig, _bridge);
                else { Dbg.Beat("endFrame(noRender)"); _xr.EndFrame(); }

                // HUD follow runs after the eye cameras have been posed this frame.
                Dbg.Beat("hud");
                _hud.Tick(_rig, active);
                Notify.TickVr(_rig);   // world-space "X joined" toast in front of the head
                Dbg.Beat("loopEnd");

                if (shouldRender && _xr.ViewsValid && Time.unscaledTime >= _nextPoseLog)
                {
                    _nextPoseLog = Time.unscaledTime + Config.PoseLogIntervalSec;
                    LogHeadPose();
                }

                PerfProbe.FrameEnd(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                Dbg.Beat("FRAME LOOP EXCEPTION: " + e.Message);
                Log.LogError("VR frame loop error: " + e);
                TeardownVr();
            }
        }

        private void TryInitVr()
        {
            try
            {
                if (!_bridgeOk)
                {
                    _bridge ??= new D3D11Bridge();
                    if (!_bridge.TryInit(out var berr)) { ReportRetry("D3D11: " + berr); return; }
                    _bridgeOk = true;
                    Log.LogInfo($"D3D11 device acquired. Adapter LUID 0x{_bridge.AdapterLuid:X}.");
                }

                // Persist the session object across retries: the instance is created once and only
                // xrGetSystem is retried. Destroying/recreating the instance each retry crashes the
                // VDXR runtime when no headset is connected.
                _xr ??= new XrSession();
                if (!_xr.TryInitialize(_bridge, out var xerr))
                {
                    ReportRetry("OpenXR: " + xerr);
                    return;
                }

                _rig = new CameraRig();
                _interactor = new CockpitInteractor();
                _locomotion = new Locomotion();
                _hud = new HudFollower();
                _grab = new GrabManager();
                _hands = new HandVisuals();
                _handManip = new HandManipulator();
                _interactor.Manip = _handManip; // so the interactor can pin a left-held control to the left pointer cam
                _interactor.Grab = _grab; // so A->[E] yields to the map-tools toggle while the right hand holds the clipboard
                _handManip.Cockpit = _interactor; // so a left-hand dial/lever grab reads the left pointer cam immediately
                _menu = new VrSettingsMenu { Hands = _hands, Grab = _grab, Manip = _handManip };
                _popup = new VrPopup();
                _scope = new MapScope();
                _prevChord = false;
                _appliedRenderScale = Config.RenderScale;
                _frameFailStreak = 0;
                _xrReady = true;
                LobbyGui.Shown = false;   // VR uses the in-headset menu page, not the flatscreen panel
                _lastError = null;
                Dbg.Reset();
                Dbg.Step("xr session up; entering frame loop");
                Log.LogInfo("=== OpenXR session up. Stereo rendering active once the session is focused. (F8 = recenter) ===");
            }
            catch (Exception e)
            {
                ReportRetry("init exception: " + e.Message);
            }
        }

        // Avoid log spam: print a retry reason only when it changes or every ~30s.
        private void ReportRetry(string err)
        {
            if (err != _lastError || Time.unscaledTime >= _nextErrLog)
            {
                _lastError = err;
                _nextErrLog = Time.unscaledTime + 30f;
                Log.LogInfo($"[vr-init] not ready: {err} (retrying every {Config.XrRetryIntervalSec:0}s)");
            }
        }

        private void LogHeadPose()
        {
            var p = _xr.Views[0].Pose.Position;
            var q = _xr.Views[0].Pose.Orientation;
            var fov = _xr.Views[0].Fov;
            Log.LogInfo($"[pose] L-eye pos=({p.X:0.000},{p.Y:0.000},{p.Z:0.000}) " +
                        $"rot=({q.X:0.00},{q.Y:0.00},{q.Z:0.00},{q.W:0.00}) " +
                        $"fov(L{fov.AngleLeft:0.00} R{fov.AngleRight:0.00} U{fov.AngleUp:0.00} D{fov.AngleDown:0.00})");
        }

        private void TeardownVr()
        {
            _xrReady = false;
            RestoreQuality();   // put the game's graphics quality back if LowSpec lowered it (also covers VR→flatscreen)
            try { _interactor?.Dispose(); } catch { }
            _interactor = null;
            _locomotion?.Reset();
            _locomotion = null;
            try { _hud?.Dispose(); } catch { }
            _hud = null;
            _grab?.Reset();
            _grab = null;
            _handManip?.Reset();
            _handManip = null;
            try { _hands?.Dispose(); } catch { }
            _hands = null;
            try { _menu?.Dispose(); } catch { }
            _menu = null;
            try { _popup?.Dispose(); } catch { }
            _popup = null;
            try { _scope?.Dispose(); } catch { }
            _scope = null;
            try { Notify.DisposeVr(); } catch { }
            try { _rig?.Destroy(); } catch { }
            _rig = null;
            try { _xr?.Dispose(); } catch { }
            _xr = null;
        }

        // Headless validation of the render pipeline (no OpenXR). Step logs pinpoint a native crash.
        private void RunSelfTest()
        {
            try
            {
                if (Camera.main == null) return; // wait until the scene + graphics device are ready
                if (!_stStarted) { _stStarted = true; Dbg.Reset(); Dbg.Step($"selftest start stage={Config.SelfTestStage}"); Log.LogInfo($"[selftest] start; stage={Config.SelfTestStage}, UseEnabledCameras={Config.UseEnabledCameras}, SolidColor={Config.SolidColorTest}"); }

                if (Config.SelfTestStage >= 2 && !_bridgeOk)
                {
                    _bridge ??= new D3D11Bridge();
                    if (!_bridge.TryInit(out var e)) { ReportRetry("selftest D3D11: " + e); return; }
                    _bridgeOk = true;
                    Log.LogInfo($"[selftest] D3D11 ok (LUID 0x{_bridge.AdapterLuid:X})");
                }
                _rig ??= new CameraRig();
                if (!_rig.EnsureCameras(1024, 1024, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB)) return;

                _stFrame++;
                int stage = Config.SelfTestStage;
                _rig.UpdateOrigin();

                if (stage >= 1)
                {
                    var view = MakeTestView();
                    var rt0 = _rig.RenderEye(0, view);
                    var rt1 = _rig.RenderEye(1, view);
                    if (stage >= 2 && rt0 != IntPtr.Zero && rt1 != IntPtr.Zero)
                    {
                        bool tr = _stFrame <= 3;
                        if (tr) Dbg.Step($"copy: CopyTexture rt0=0x{rt0:X} -> rt1=0x{rt1:X}");
                        _bridge.CopyTexture(rt1, rt0);
                        if (tr) Dbg.Step("copy: returned");
                    }
                }

                if (_stFrame % 60 == 0) Log.LogInfo($"[selftest] stage {stage} ALIVE frame {_stFrame}");
            }
            catch (Exception ex)
            {
                Log.LogError("[selftest] managed exception: " + ex);
                Config.SelfTestRender = false;
            }
        }

        private static View MakeTestView()
        {
            return new View
            {
                Fov = new Fovf { AngleLeft = -0.7f, AngleRight = 0.7f, AngleUp = 0.6f, AngleDown = -0.6f },
                Pose = new Posef { Orientation = new Quaternionf(0, 0, 0, 1), Position = new Vector3f(0, 0, 0) }
            };
        }

        private bool _prevMenu;

        // Left menu button -> ESC key (pause/back). Synthesized like the other buttons.
        private void HandleMenuEsc(VrInput input)
        {
            bool menu = input.MenuHeld;
            if (menu && !_prevMenu) Win32Input.SendKey(Win32Input.VK_ESCAPE, true);
            else if (!menu && _prevMenu) Win32Input.SendKey(Win32Input.VK_ESCAPE, false);
            _prevMenu = menu;
        }

        // --- Phase 2 co-op pose helpers ---
        private static Vector3 PoseWorldPos(Posef p, Transform origin)
        { var lp = new Vector3(p.Position.X, p.Position.Y, -p.Position.Z); return origin.TransformPoint(lp); }

        private static Quaternion PoseWorldRot(Posef p, Transform origin)
        { var lr = new Quaternion(-p.Orientation.X, -p.Orientation.Y, p.Orientation.Z, p.Orientation.W); return origin.rotation * lr; }

        private void SendLocalPose(Vector3 hp, Quaternion hr, bool hands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr,
                                   bool curl = false, float lci = 0f, float lco = 0f, float rci = 0f, float rco = 0f)
        {
            CoopP2P.SendPose(hp, hr, hands, lp, lr, rp, rr, curl, lci, lco, rci, rco);
            if (CoopP2P.SelfTest)   // mirror local pose 0.8 m ahead so the avatar is visible solo
            {
                Vector3 off = hr * Vector3.forward * 0.8f;
                CoopP2P.InjectRemote(hp + off, hr, hands, lp + off, lr, rp + off, rr, curl, lci, lco, rci, rco);
            }
        }

        private static bool KeyDown(UnityEngine.InputSystem.Key k)
        { try { var kb = UnityEngine.InputSystem.Keyboard.current; return kb != null && kb[k].wasPressedThisFrame; } catch { return false; } }

        // Held-state (not edge): hold Left/Right Alt for a momentary free cursor during local two-window testing.
        private static bool AltHeld()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                return kb != null && (kb[UnityEngine.InputSystem.Key.LeftAlt].isPressed || kb[UnityEngine.InputSystem.Key.RightAlt].isPressed);
            }
            catch { return false; }
        }

        private static bool RecenterPressed()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                return kb != null && kb[UnityEngine.InputSystem.Key.F8].wasPressedThisFrame;
            }
            catch { return false; }
        }

        private void ScanSceneOnce()
        {
            if (_loggedScene || Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 3f;
            try
            {
                var cam = Camera.main;
                var turret = FirstOf(Il2CppType.Of<TurretController>());
                if (cam != null && turret != null)
                {
                    int guns = Count(Il2CppType.Of<GunController>());
                    Log.LogInfo($"[scan] Main camera '{cam.name}' (fov {cam.fieldOfView:0.#}); TurretController on " +
                                $"'{turret.TryCast<TurretController>().gameObject.name}'; {guns} gun(s).");
                    _loggedScene = true;
                }
            }
            catch (Exception e) { Log.LogWarning("[scan] " + e.Message); }
        }

        private static UnityEngine.Object FirstOf(Il2CppSystem.Type t)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            return (arr != null && arr.Length > 0) ? arr[0] : null;
        }

        private static int Count(Il2CppSystem.Type t)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            return arr?.Length ?? 0;
        }
    }
}
