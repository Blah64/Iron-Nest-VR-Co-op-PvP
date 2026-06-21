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
        private bool _prevChord;
        private float _appliedRenderScale;
        private bool _xrReady;
        private float _nextXrTry;
        private string _lastError;
        private float _nextErrLog;
        private float _nextPoseLog;
        private float _loopDiagNext;   // same-machine loopback test: per-second focus/runInBackground/timeScale log

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
            try { Notify.DrawFlat(); } catch { }   // non-focus-pulling "X joined" toast (flatscreen)
        }

        // The game is an FPS that locks the OS cursor to centre for mouselook, so the flatscreen lobby
        // panel is unclickable unless we free the cursor and stop the look while it's open. Done in
        // LateUpdate (after the game's own Update) so we win the per-frame cursor-lock race.
        private bool _lookFrozen;
        private void LateUpdate()
        {
            bool flat = LobbyGui.Shown && !_xrReady;
            LobbyGui.FlatInteractive = flat;
            if (flat)
            {
                try { UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None; UnityEngine.Cursor.visible = true; } catch { }
                SetFpsLook(false);
                _lookFrozen = true;
            }
            else if (_lookFrozen)
            {
                SetFpsLook(true);   // hand control back to the game
                _lookFrozen = false;
            }

            // Phase 3: apply the peer's control visuals + snap turret/gun state here, AFTER the game's Update
            // (so our snap wins the frame); re-applies the turret transform immediately so the visible
            // rotating cockpit matches. Map token positions are applied here too (after the game's drag logic).
            CoopControls.LateApply();
            CoopMap.LateApply();

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

        private void Update()
        {
            LogEnvironmentOnce();
            PerfTick();
            ScanSceneOnce();
            Diagnostics.Tick();
            SteamNet.Tick();   // Phase 1 co-op: Steam lobby create/browse/join (F9/F10/F11/F12)
            LobbyGui.HandleInput();  // flatscreen panel clicks via the new Input System (legacy is off)

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
            CoopNetDiag.Tick(Time.unscaledDeltaTime);    // REVIEW-fix: cross-machine desync detector (diagnostic only)
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

                // Controller input + cockpit interaction. Pose locate needs the frame's predicted
                // display time (set by BeginFrameLocateViews) and a focused session. The interactor is
                // ticked EVERY frame (even unfocused) so it restores the game's cursor when we step away.
                float dt = Time.unscaledDeltaTime;
                bool active = _xr.IsFocused && _xr.InputReady;
                if (active)
                {
                    _xr.Input.Sync();
                    _xr.Input.LocatePoses(_xr.LocalSpace, _xr.PredictedDisplayTime);
                    if (_xr.Input.RecenterEdge) _rig.Recenter();

                    // Click BOTH thumbsticks at once to open/close the VR settings menu.
                    bool chord = _xr.Input.StickClickL && _xr.Input.StickClickR;
                    if (chord && !_prevChord) _menu.Toggle(_rig);
                    _prevChord = chord;

                    if (_menu.IsOpen)
                    {
                        _menu.Tick(_xr.Input, _rig); // menu owns the trigger while open
                        _handManip.Tick(_xr.Input, _rig, _hands, false); // release any held control
                    }
                    else
                    {
                        HandleMenuEsc(_xr.Input);
                        _locomotion.Tick(_xr.Input, _rig, dt);
                        // Gravity-glove dial/lever grab runs first; while it holds a control it owns the
                        // right grip, so the prop GrabManager stands down to avoid fighting over it.
                        _handManip.Tick(_xr.Input, _rig, _hands, true);
                        if (!_handManip.Active) _grab.Tick(_xr.Input, _rig, active);
                    }
                    _grab.ReconcileScale(); // live clipboard size, even with the menu open
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
                _interactor.Apply(_xr.Input, _rig, dt, active, active && (_menu.IsOpen || _handManip.Active), _menu.IsOpen);

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
                _menu = new VrSettingsMenu { Hands = _hands };
                _prevChord = false;
                _appliedRenderScale = Config.RenderScale;
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
