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

        // Phase 1 scene probe (still handy).
        private float _nextScan;
        private bool _loggedScene;

        private void Awake()
        {
            Log.LogInfo("VrManager active. Bringing up OpenXR (needs a headset + active OpenXR runtime).");
        }

        private int _stFrame;
        private bool _stStarted;

        private void Update()
        {
            ScanSceneOnce();
            Diagnostics.Tick();

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
                }
                _interactor.Apply(_xr.Input, _rig, dt, active, active && (_menu.IsOpen || _handManip.Active));

                if (shouldRender) _xr.RenderAndSubmit(_rig, _bridge);
                else _xr.EndFrame();

                // HUD follow runs after the eye cameras have been posed this frame.
                _hud.Tick(_rig, active);

                if (shouldRender && _xr.ViewsValid && Time.unscaledTime >= _nextPoseLog)
                {
                    _nextPoseLog = Time.unscaledTime + Config.PoseLogIntervalSec;
                    LogHeadPose();
                }
            }
            catch (Exception e)
            {
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
