using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using UnityEngine.Experimental.Rendering;
// NOTE: deliberately NOT `using UnityEngine;` — UnityEngine.Space collides with Silk.NET.OpenXR.Space.
// UnityEngine types (Graphics, SystemInfo, Rendering.CommandBuffer) are fully qualified below.

namespace IronNestVR
{
    /// <summary>
    /// Owns the OpenXR lifecycle and per-frame loop, driving the runtime directly (no Unity XR
    /// plugin). Brings up instance/system/session/spaces, runs the canonical frame loop, exposes
    /// per-eye poses + FOV, and submits swapchains + the projection layer inside
    /// <see cref="BeginFrameLocateViews"/> / <see cref="EndFrame"/>.
    /// </summary>
    internal sealed unsafe class XrSession : IDisposable
    {
        private const string D3D11Ext = "XR_KHR_D3D11_enable";

        private static ManualLogSource Log => Plugin.Logger;

        private XR _xr;
        private Instance _instance;
        private ulong _systemId;
        private Session _session;
        private Space _localSpace; // seated world origin (set where the headset starts)
        private Space _viewSpace;  // head
        private KhrD3D11Enable _d3d11;

        private SessionState _state = SessionState.Unknown;
        private bool _running;
        private bool _exitRequested;
        private bool _inFrame;
        private bool _instanceCreated, _sessionCreated, _localCreated, _viewCreated;

        private readonly VrInput _input = new VrInput();
        private bool _inputCreated, _inputAttached;

        private readonly Swapchain[] _swapchains = new Swapchain[2];
        private readonly IntPtr[][] _imageTex = new IntPtr[2][];
        private bool _swapchainsReady;
        private int _submitFrames;
        private long _swapchainFormat;
        private GraphicsFormat _gfxFormat = GraphicsFormat.R8G8B8A8_UNorm;

        // ---- render-thread copy + release (Config.RenderThreadCopy) ----
        // Per eye, the raw CopyResource (reusing the proven D3D11Bridge path) and xrReleaseSwapchainImage
        // are issued as ONE CommandBuffer.IssuePluginEventAndData event, so they replay in program order on
        // whatever thread runs the render command stream: the MAIN thread while -force-gfx-direct is on
        // (no cross-thread race), the RENDER thread once it's dropped. Default OFF.
        [StructLayout(LayoutKind.Sequential)]
        private struct CopyReleaseData { public IntPtr Dst; public IntPtr Src; public int Eye; }

        // __stdcall matches Unity's UnityRenderingEventAndData(int eventId, void* data). Do NOT switch to
        // [UnmanagedCallersOnly] — it collides (CS0433) with IL2CPP's own copy in UnityEngine.CoreModule.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RenderEventAndData(int eventId, IntPtr data);

        private const int RtCopyEvent = 0x4953; // 'IS'
        private static readonly RenderEventAndData _rtcDel = OnRenderThreadEvent; // static-kept-alive (else dangles)
        private static IntPtr _rtcCbPtr;
        private static volatile bool _rtcActive;
        private static volatile int _rtcFires;
        private static XR _rtcXr;
        private static D3D11Bridge _rtcBridge;
        private static Swapchain[] _rtcSwapchains;
        private static IntPtr _rtcData0, _rtcData1; // CopyReleaseData* (AllocHGlobal — GC-stable for the callback)
        private bool _rtcInit;
        private UnityEngine.Rendering.CommandBuffer[] _rtcCb;
        private int _rtcNextLog;

        // ---- render-thread xrEndFrame + completion gate ----
        private const int RtEndFrameEvent = 0x4546;   // 'EF'
        private const int RtGateTimeoutMs = 34;        // ~2.4 frames @72Hz; proceed (frame may discard) if exceeded
        private static Session _rtcSession;
        private static volatile int _rtcEndDone;       // ++ on the render thread after xrEndFrame
        private int _rtcEndQueued;                     // ++ on the main thread when an endframe event is queued
        private int _rtcLastGateWarn;
        // Persistent native FrameEndInfo tree for the threaded endframe (single buffer — the gate guarantees
        // frame N's endframe finishes reading this before frame N+1 overwrites it).
        private IntPtr _efFei;       // FrameEndInfo*
        private IntPtr _efLayerArr;  // CompositionLayerBaseHeader** (1 entry → _efProj)
        private IntPtr _efProj;      // CompositionLayerProjection*
        private IntPtr _efViews;     // CompositionLayerProjectionView[2]
        private UnityEngine.Rendering.CommandBuffer _rtcEndCb;

        private int _lastRcWarnTick;
        // Finite swapchain wait. The old long.MaxValue could freeze the whole game (the wait runs on
        // the main thread) if the compositor ever stalled handing an image back — e.g. while the
        // session is Synchronized-but-not-Visible. 1s per wait, retried a few times, then bail.
        private const long WaitTimeoutNs = 1_000_000_000; // 1s
        private const int WaitMaxTries = 4;               // ~4s total before abandoning the frame

        public bool Running => _running;
        public bool ExitRequested => _exitRequested;
        // Result of the most recent xrWaitFrame (Success when the session simply isn't running/visible yet).
        // A NEGATIVE result is a genuine OpenXR error (runtime/session/instance lost) — the signal the caller
        // uses to abandon VR rather than spin on a wedged runtime. Positive warnings (e.g. SessionLossPending)
        // are surfaced via the state-change events / ExitRequested, not here.
        public Result LastWaitResult { get; private set; }
        public bool LastFrameFailed => (long)LastWaitResult < 0;
        public bool IsFocused => _state == SessionState.Focused;
        public bool InputReady => _inputAttached;
        public VrInput Input => _input;
        public long PredictedDisplayTime { get; private set; }
        public uint EyeWidth { get; private set; }
        public uint EyeHeight { get; private set; }

        // Per-eye view data (pose + fov), refreshed each frame when ShouldRender.
        public readonly View[] Views = new View[2];
        public bool ViewsValid { get; private set; }
        public Space LocalSpace => _localSpace;

        // -------- init --------

        private bool _instanceReady;

        /// <summary>True once the OpenXR instance + D3D11 extension are created (done exactly once). While this is
        /// false, a failed <see cref="TryInitialize"/> is a loader/runtime-level failure — the EXPENSIVE path
        /// (xrCreateInstance can block ~1-2s before returning ErrorRuntimeUnavailable). Once true, a later failure
        /// is only the cheap xrGetSystem "no HMD yet" retry, which is safe to poll fast.</summary>
        public bool InstanceReady => _instanceReady;

        /// <summary>
        /// Resumable init. The instance + extensions are created exactly once; if no HMD is present
        /// yet, only <c>xrGetSystem</c> is retried on later calls. This avoids destroying/recreating
        /// the OpenXR instance every retry, which crashes some runtimes (e.g. VDXR with no headset).
        /// </summary>
        public bool TryInitialize(D3D11Bridge d3d, out string error)
        {
            error = null;
            if (!_instanceReady)
            {
                try { _xr = XR.GetApi(); }
                catch (Exception e) { error = "XR.GetApi failed (openxr_loader.dll not found?): " + e.Message; return false; }

                if (!HasExtension(D3D11Ext)) { error = D3D11Ext + " not offered by the active OpenXR runtime."; return false; }
                if (!CreateInstance(out error)) return false;
                if (!_xr.TryGetInstanceExtension(null, _instance, out _d3d11)) { error = "could not load KhrD3D11Enable extension functions."; return false; }
                _instanceReady = true;
            }

            // Actions + suggested bindings must exist before the session is attached. Non-fatal: VR
            // still renders if input setup fails, so just warn and carry on.
            if (!_inputCreated)
            {
                if (_input.CreateActions(_xr, _instance, out var ierr)) _inputCreated = true;
                else Log.LogWarning("[input] action setup failed (turret control disabled): " + ierr);
            }

            if (!GetSystem(out error)) return false; // keep the instance alive; retried next call
            QueryEyeSizes();
            if (!GraphicsRequirementsGate(d3d, out error)) return false;
            if (!CreateSessionAndSpaces(d3d, out error)) return false;

            // Attach the action set to the freshly-created session (once, before the first xrSyncActions).
            if (_inputCreated && !_inputAttached)
            {
                if (_input.Attach(_session, out var aerr)) _inputAttached = true;
                else Log.LogWarning("[input] attach failed (turret control disabled): " + aerr);
            }

            Log.LogInfo($"OpenXR initialized. eye={EyeWidth}x{EyeHeight}. Waiting for session to become Ready...");
            return true;
        }

        private bool HasExtension(string name)
        {
            uint count = 0;
            _xr.EnumerateInstanceExtensionProperties((byte*)null, 0, ref count, null);
            if (count == 0) return false;
            var props = stackalloc ExtensionProperties[(int)count];
            for (int i = 0; i < count; i++) props[i].Type = StructureType.TypeExtensionProperties;
            _xr.EnumerateInstanceExtensionProperties((byte*)null, count, ref count, props);
            for (int i = 0; i < count; i++)
            {
                var s = SilkMarshal.PtrToString((IntPtr)props[i].ExtensionName, NativeStringEncoding.UTF8);
                if (s == name) return true;
            }
            return false;
        }

        private bool CreateInstance(out string error)
        {
            error = null;
            var extPtr = SilkMarshal.StringToPtr(D3D11Ext, NativeStringEncoding.UTF8);
            byte** exts = stackalloc byte*[1];
            exts[0] = (byte*)extPtr;

            var appInfo = new ApplicationInfo { ApplicationVersion = 1, EngineVersion = 1, ApiVersion = MakeVersion(1, 0, 0) };
            SetFixed(appInfo.ApplicationName, "IronNestVR");
            SetFixed(appInfo.EngineName, "Unity");

            var ci = new InstanceCreateInfo
            {
                Type = StructureType.TypeInstanceCreateInfo,
                ApplicationInfo = appInfo,
                EnabledExtensionCount = 1,
                EnabledExtensionNames = exts
            };
            var r = _xr.CreateInstance(&ci, ref _instance);
            SilkMarshal.Free(extPtr);
            if (r != Result.Success) { error = "xrCreateInstance failed: " + r; return false; }
            _instanceCreated = true;
            return true;
        }

        private bool GetSystem(out string error)
        {
            error = null;
            var info = new SystemGetInfo { Type = StructureType.TypeSystemGetInfo, FormFactor = FormFactor.HeadMountedDisplay };
            var r = _xr.GetSystem(_instance, &info, ref _systemId);
            if (r != Result.Success)
            {
                error = $"xrGetSystem failed: {r} (no HMD detected / runtime not ready).";
                return false;
            }
            return true;
        }

        private void QueryEyeSizes()
        {
            uint count = 0;
            _xr.EnumerateViewConfigurationView(_instance, _systemId, ViewConfigurationType.PrimaryStereo, 0, ref count, null);
            if (count == 0) return;
            var views = stackalloc ViewConfigurationView[(int)count];
            for (int i = 0; i < count; i++) views[i].Type = StructureType.TypeViewConfigurationView;
            _xr.EnumerateViewConfigurationView(_instance, _systemId, ViewConfigurationType.PrimaryStereo, count, ref count, views);
            uint recW = views[0].RecommendedImageRectWidth;
            uint recH = views[0].RecommendedImageRectHeight;
            float s = Math.Clamp(Config.RenderScale, 0.2f, 1f);
            EyeWidth = Even((uint)(recW * s));
            EyeHeight = Even((uint)(recH * s));
            Log.LogInfo($"Eye recommended {recW}x{recH}; rendering at {EyeWidth}x{EyeHeight} (scale {s:0.00}).");
        }

        private static uint Even(uint v) => v < 2 ? 2 : (v & ~1u);

        private bool GraphicsRequirementsGate(D3D11Bridge d3d, out string error)
        {
            error = null;
            // xrGetD3D11GraphicsRequirementsKHR is MANDATORY before xrCreateSession with a D3D11 binding.
            var reqs = new GraphicsRequirementsD3D11KHR { Type = StructureType.TypeGraphicsRequirementsD3D11Khr };
            var r = _d3d11.GetD3D11GraphicsRequirements(_instance, _systemId, ref reqs);
            if (r != Result.Success) { error = "xrGetD3D11GraphicsRequirements failed: " + r; return false; }

            if (reqs.AdapterLuid != 0 && reqs.AdapterLuid != d3d.AdapterLuid)
            {
                Log.LogWarning($"Adapter LUID mismatch: Unity=0x{d3d.AdapterLuid:X} but OpenXR requires 0x{reqs.AdapterLuid:X}. " +
                               "Session creation will likely fail (iGPU/dGPU split). Force the game onto the HMD's GPU.");
            }
            else
            {
                Log.LogInfo($"D3D11 graphics gate OK (LUID 0x{d3d.AdapterLuid:X}, minFeatureLevel 0x{reqs.MinFeatureLevel:X}).");
            }
            return true;
        }

        private bool CreateSessionAndSpaces(D3D11Bridge d3d, out string error)
        {
            error = null;
            var binding = new GraphicsBindingD3D11KHR
            {
                Type = StructureType.TypeGraphicsBindingD3D11Khr,
                Device = d3d.Device
            };
            var sci = new SessionCreateInfo
            {
                Type = StructureType.TypeSessionCreateInfo,
                Next = &binding,
                SystemId = _systemId
            };
            var r = _xr.CreateSession(_instance, &sci, ref _session);
            if (r != Result.Success) { error = "xrCreateSession failed: " + r; return false; }
            _sessionCreated = true;

            var idPose = new Posef { Orientation = new Quaternionf(0, 0, 0, 1), Position = new Vector3f(0, 0, 0) };
            var localCi = new ReferenceSpaceCreateInfo { Type = StructureType.TypeReferenceSpaceCreateInfo, ReferenceSpaceType = ReferenceSpaceType.Local, PoseInReferenceSpace = idPose };
            if (_xr.CreateReferenceSpace(_session, &localCi, ref _localSpace) == Result.Success) _localCreated = true;
            var viewCi = new ReferenceSpaceCreateInfo { Type = StructureType.TypeReferenceSpaceCreateInfo, ReferenceSpaceType = ReferenceSpaceType.View, PoseInReferenceSpace = idPose };
            if (_xr.CreateReferenceSpace(_session, &viewCi, ref _viewSpace) == Result.Success) _viewCreated = true;
            return true;
        }

        // -------- per-frame --------

        public void PollEvents()
        {
            var buf = new EventDataBuffer { Type = StructureType.TypeEventDataBuffer };
            while (_xr.PollEvent(_instance, ref buf) == Result.Success)
            {
                if (buf.Type == StructureType.TypeEventDataSessionStateChanged)
                {
                    var ev = *(EventDataSessionStateChanged*)&buf;
                    _state = ev.State;
                    OnStateChanged(ev.State);
                }
                buf = new EventDataBuffer { Type = StructureType.TypeEventDataBuffer };
            }
        }

        private void OnStateChanged(SessionState s)
        {
            Log.LogInfo("OpenXR session state -> " + s);
            switch (s)
            {
                case SessionState.Ready:
                    var bi = new SessionBeginInfo { Type = StructureType.TypeSessionBeginInfo, PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo };
                    _xr.BeginSession(_session, &bi);
                    _running = true;
                    break;
                case SessionState.Stopping:
                    _running = false;
                    _xr.EndSession(_session);
                    break;
                case SessionState.Exiting:
                case SessionState.LossPending:
                    _running = false;
                    _exitRequested = true;
                    break;
            }
        }

        /// <summary>xrWaitFrame + xrBeginFrame + xrLocateView. Returns true if the app should render.</summary>
        public bool BeginFrameLocateViews()
        {
            ViewsValid = false;
            LastWaitResult = Result.Success;
            if (!_running) return false;

            // Completion gate: the previous frame's render-thread xrEndFrame must finish before this
            // frame's xrBeginFrame (matched begin/end). No-op in Direct mode (endframe ran synchronously).
            if (Config.RenderThreadCopy && _rtcActive) WaitForEndframeDrain();

            bool tr = _submitFrames < 30;
            if (tr) Dbg.Step("WaitFrame >>");
            var fwi = new FrameWaitInfo { Type = StructureType.TypeFrameWaitInfo };
            var fs = new FrameState { Type = StructureType.TypeFrameState };
            PerfProbe.WaitStart();
            var wfr = _xr.WaitFrame(_session, &fwi, &fs);
            PerfProbe.WaitStop();
            LastWaitResult = wfr;
            if (wfr != Result.Success) { WarnRc("xrWaitFrame", wfr); return false; }
            if (tr) Dbg.Step("WaitFrame <<");
            PredictedDisplayTime = fs.PredictedDisplayTime;

            var fbi = new FrameBeginInfo { Type = StructureType.TypeFrameBeginInfo };
            var bfr = _xr.BeginFrame(_session, &fbi);
            // XR_FRAME_DISCARDED is a benign success-category result (e.g. a re-begun frame); don't warn.
            if (bfr != Result.Success && bfr != Result.FrameDiscarded) WarnRc("xrBeginFrame", bfr);
            if (tr) Dbg.Step("BeginFrame <<");
            _inFrame = true;

            bool shouldRender = fs.ShouldRender != 0;
            if (shouldRender)
            {
                var vli = new ViewLocateInfo
                {
                    Type = StructureType.TypeViewLocateInfo,
                    ViewConfigurationType = ViewConfigurationType.PrimaryStereo,
                    DisplayTime = fs.PredictedDisplayTime,
                    Space = _localSpace
                };
                var vs = new ViewState { Type = StructureType.TypeViewState };
                uint vcount = 2;
                Views[0].Type = StructureType.TypeView;
                Views[1].Type = StructureType.TypeView;
                fixed (View* vptr = Views)
                {
                    var r = _xr.LocateView(_session, &vli, &vs, 2, ref vcount, vptr);
                    ViewsValid = r == Result.Success
                                 && (vs.ViewStateFlags & ViewStateFlags.OrientationValidBit) != 0
                                 && (vs.ViewStateFlags & ViewStateFlags.PositionValidBit) != 0;
                }
            }
            return shouldRender;
        }

        /// <summary>Safe overload for the no-composition-layers case.</summary>
        public void EndFrame() => EndFrame(null, 0);

        /// <summary>
        /// xrEndFrame. Pass <paramref name="layers"/>=null/0 for the no-layer case (valid), or a
        /// projection-layer pointer + count.
        /// </summary>
        public void EndFrame(CompositionLayerBaseHeader** layers, uint layerCount)
        {
            if (!_inFrame) return;
            _inFrame = false;
            var fei = new FrameEndInfo
            {
                Type = StructureType.TypeFrameEndInfo,
                DisplayTime = PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = layerCount,
                Layers = layers
            };
            PerfProbe.WaitStart();
            var efr = _xr.EndFrame(_session, &fei);
            PerfProbe.WaitStop();
            if (efr != Result.Success) WarnRc("xrEndFrame", efr);
        }

        // -------- swapchains + stereo submission --------

        public bool CreateSwapchains(out string error)
        {
            error = null;
            uint fcount = 0;
            _xr.EnumerateSwapchainFormats(_session, 0, ref fcount, (long*)null);
            if (fcount == 0) { error = "runtime offered no swapchain formats."; return false; }
            long* formats = stackalloc long[(int)fcount];
            _xr.EnumerateSwapchainFormats(_session, fcount, ref fcount, formats);

            const long DxgiRgba8Unorm = 28, DxgiRgba8UnormSrgb = 29;
            bool hasSrgb = false, hasUnorm = false;
            for (int i = 0; i < fcount; i++) { if (formats[i] == DxgiRgba8UnormSrgb) hasSrgb = true; if (formats[i] == DxgiRgba8Unorm) hasUnorm = true; }
            if (hasSrgb) { _swapchainFormat = DxgiRgba8UnormSrgb; _gfxFormat = GraphicsFormat.R8G8B8A8_SRGB; }
            else if (hasUnorm) { _swapchainFormat = DxgiRgba8Unorm; _gfxFormat = GraphicsFormat.R8G8B8A8_UNorm; }
            else { _swapchainFormat = formats[0]; _gfxFormat = GraphicsFormat.R8G8B8A8_UNorm; Log.LogWarning($"No RGBA8 swapchain format offered; using 0x{_swapchainFormat:X} (colors may be off)."); }

            for (int eye = 0; eye < 2; eye++)
            {
                var sci = new SwapchainCreateInfo
                {
                    Type = StructureType.TypeSwapchainCreateInfo,
                    UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.TransferDstBit,
                    Format = _swapchainFormat,
                    SampleCount = 1,
                    Width = EyeWidth,
                    Height = EyeHeight,
                    FaceCount = 1,
                    ArraySize = 1,
                    MipCount = 1
                };
                if (_xr.CreateSwapchain(_session, &sci, ref _swapchains[eye]) != Result.Success)
                { error = "xrCreateSwapchain failed for eye " + eye; return false; }

                uint icount = 0;
                _xr.EnumerateSwapchainImages(_swapchains[eye], 0, ref icount, (SwapchainImageBaseHeader*)null);
                var imgs = new SwapchainImageD3D11KHR[icount];
                for (int i = 0; i < icount; i++) imgs[i].Type = StructureType.TypeSwapchainImageD3D11Khr;
                fixed (SwapchainImageD3D11KHR* ip = imgs)
                    _xr.EnumerateSwapchainImages(_swapchains[eye], icount, ref icount, (SwapchainImageBaseHeader*)ip);

                var arr = new IntPtr[icount];
                for (int i = 0; i < icount; i++) arr[i] = (IntPtr)imgs[i].Texture;
                _imageTex[eye] = arr;
            }

            _swapchainsReady = true;
            Log.LogInfo($"Swapchains created (DXGI 0x{_swapchainFormat:X}, {EyeWidth}x{EyeHeight}, {_imageTex[0].Length} images/eye).");
            return true;
        }

        /// <summary>
        /// Re-apply <see cref="Config.RenderScale"/> at runtime: destroy the swapchains and re-query the
        /// eye render size. The swapchains are recreated lazily on the next <see cref="RenderAndSubmit"/>
        /// (which also rebuilds the rig's eye RTs once the caller has Destroy()'d it). Call only between
        /// frames (not inside Begin/EndFrame).
        /// </summary>
        public bool ResizeEyes(out string error)
        {
            error = null;
            try
            {
                if (_swapchainsReady)
                {
                    _xr.DestroySwapchain(_swapchains[0]);
                    _xr.DestroySwapchain(_swapchains[1]);
                    _swapchainsReady = false;
                }
                QueryEyeSizes(); // re-reads Config.RenderScale
                Log.LogInfo($"[resize] eye render size now {EyeWidth}x{EyeHeight} (scale {Config.RenderScale:0.00}).");
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>Acquire + wait the next image of an eye's swapchain; returns its ID3D11Texture2D ptr.</summary>
        private IntPtr AcquireEye(int eye)
        {
            bool tr = _submitFrames <= 30;
            var ai = new SwapchainImageAcquireInfo { Type = StructureType.TypeSwapchainImageAcquireInfo };
            uint idx = 0;
            if (tr) Dbg.Step($"  acq{eye}: AcquireSwapchainImage");
            var ar = _xr.AcquireSwapchainImage(_swapchains[eye], &ai, ref idx);
            if (ar != Result.Success) { WarnRc($"xrAcquireSwapchainImage(eye{eye})", ar); return IntPtr.Zero; }

            // Finite, retryable wait. xrWaitSwapchainImage may legitimately return XR_TIMEOUT_EXPIRED; the
            // spec allows waiting again on the same still-acquired image. We bound the retries instead of
            // blocking forever. On a genuine give-up the image stays acquired-but-unwaited — the spec
            // forbids releasing it without a successful wait, so RenderAndSubmit skips the release (its
            // sc==Zero guard); that only happens if the runtime is already wedged, and it's logged loudly.
            var wi = new SwapchainImageWaitInfo { Type = StructureType.TypeSwapchainImageWaitInfo, Timeout = WaitTimeoutNs };
            Result wr = Result.TimeoutExpired;
            PerfProbe.WaitStart();
            for (int t = 0; t < WaitMaxTries; t++)
            {
                if (tr) Dbg.Step($"  acq{eye}: idx={idx} WaitSwapchainImage(try {t}, 1s)");
                wr = _xr.WaitSwapchainImage(_swapchains[eye], &wi);
                if (wr != Result.TimeoutExpired) break;
                Dbg.Beat($"eye{eye} wait TIMEOUT (try {t})");
            }
            PerfProbe.WaitStop();
            if (tr) Dbg.Step($"  acq{eye}: wait result={wr}");
            if (wr != Result.Success) { WarnRc($"xrWaitSwapchainImage(eye{eye})", wr); return IntPtr.Zero; }
            return _imageTex[eye][idx];
        }

        private void ReleaseEye(int eye)
        {
            var ri = new SwapchainImageReleaseInfo { Type = StructureType.TypeSwapchainImageReleaseInfo };
            _xr.ReleaseSwapchainImage(_swapchains[eye], &ri);
        }

        /// <summary>
        /// Per-eye acquire -> render the rig camera -> copy into the swapchain image -> release, then
        /// submit a stereo projection layer. Call only when <see cref="BeginFrameLocateViews"/> said
        /// to render (it always ends the frame exactly once).
        /// </summary>
        public void RenderAndSubmit(CameraRig rig, D3D11Bridge bridge)
        {
            if (!ViewsValid) { EndFrame(); return; }
            if (!_swapchainsReady && !CreateSwapchains(out var e)) { Log.LogError("Swapchain init failed: " + e); EndFrame(); return; }
            if (!rig.EnsureCameras(EyeWidth, EyeHeight, _gfxFormat)) { EndFrame(); return; }

            int f = _submitFrames++;
            bool tr = f < 30;
            rig.UpdateOrigin();
            for (int eye = 0; eye < 2; eye++)
            {
                if (tr) Dbg.Step($"submit eye{eye}: AcquireEye");
                IntPtr sc = AcquireEye(eye);
                if (tr) Dbg.Step($"submit eye{eye}: sc=0x{sc:X}; RenderEye");
                IntPtr rt = rig.RenderEye(eye, Views[eye]);
                // Crash-proof breadcrumb immediately before the native GPU copy (the prime AV suspect):
                // if the process vanishes here, the heartbeat file's last line names this exact step.
                Dbg.Beat($"f{f} eye{eye} CopyTexture sc={(sc != IntPtr.Zero ? 1 : 0)} rt={(rt != IntPtr.Zero ? 1 : 0)}");
                if (tr) Dbg.Step($"submit eye{eye}: rt=0x{rt:X}; CopyTexture");
                if (sc != IntPtr.Zero && rt != IntPtr.Zero)
                {
                    if (Config.RenderThreadCopy)
                    {
                        if (!_rtcInit) InitRenderThreadCopy(bridge);
                        IntPtr dp = eye == 0 ? _rtcData0 : _rtcData1;
                        var d = (CopyReleaseData*)dp;
                        d->Dst = sc; d->Src = rt; d->Eye = eye;
                        var cb = _rtcCb[eye];
                        cb.Clear();
                        cb.IssuePluginEventAndData(_rtcCbPtr, RtCopyEvent, dp);
                        if (tr) Dbg.Step($"submit eye{eye}: ExecuteCommandBuffer (copy+release)");
                        UnityEngine.Graphics.ExecuteCommandBuffer(cb); // copy + xrReleaseSwapchainImage, in-order
                    }
                    else
                    {
                        bridge.CopyTexture((void*)sc, (void*)rt);
                        if (tr) Dbg.Step($"submit eye{eye}: ReleaseEye");
                        ReleaseEye(eye);
                    }
                }
                else if (sc != IntPtr.Zero)
                {
                    // Acquired but nothing to copy (rt null): release anyway so the image isn't leaked.
                    if (tr) Dbg.Step($"submit eye{eye}: ReleaseEye (no copy)");
                    ReleaseEye(eye);
                }
            }
            Dbg.Beat($"f{f} SubmitProjection");
            if (tr) Dbg.Step("submit: SubmitProjection");
            if (Config.RenderThreadCopy && _rtcInit) SubmitProjectionThreaded();
            else SubmitProjection();
            if (Config.RenderThreadCopy && _rtcActive && f >= _rtcNextLog)
            {
                _rtcNextLog = f + 120;
                Log.LogInfo($"[rtcopy] frame={f} fires={_rtcFires} endDone={_rtcEndDone}/{_rtcEndQueued} mode={UnityEngine.SystemInfo.renderingThreadingMode}");
            }
            if (tr) Dbg.Step("submit: frame done");
        }

        // Runs on the render-command-stream thread: the MAIN thread in Direct mode, the RENDER thread once
        // -force-gfx-direct is dropped. MUST stay minimal — only raw native calls (CopyResource via the
        // bridge, xrReleaseSwapchainImage, xrEndFrame) and int counters. No managed allocation, no Unity
        // main-thread APIs, no logging. An exception must never propagate across the native boundary.
        // Two event kinds, replayed in stream order: per-eye copy+release, then (after both) endframe.
        private static void OnRenderThreadEvent(int eventId, IntPtr data)
        {
            if (!_rtcActive || data == IntPtr.Zero) return;
            try
            {
                if (eventId == RtEndFrameEvent)
                {
                    _rtcXr.EndFrame(_rtcSession, (FrameEndInfo*)data); // ordered AFTER both eyes' release
                    _rtcEndDone++;                                      // single writer (render thread)
                    return;
                }
                var d = (CopyReleaseData*)data;
                _rtcBridge.CopyTexture(d->Dst, d->Src);            // proven raw CopyResource (immediate ctx)
                var ri = new SwapchainImageReleaseInfo { Type = StructureType.TypeSwapchainImageReleaseInfo };
                _rtcXr.ReleaseSwapchainImage(_rtcSwapchains[d->Eye], &ri); // ordered AFTER the copy, same stream
                _rtcFires++;
            }
            catch { /* swallow: never propagate across the native boundary */ }
        }

        // Block until every queued render-thread xrEndFrame has completed, so this frame's xrBeginFrame can't
        // outrun the previous frame's xrEndFrame (OpenXR requires matched begin/end). In Direct mode the event
        // already ran synchronously → returns immediately. Bounded: if the render thread stalls we proceed
        // after a timeout (xrBeginFrame then returns the benign FRAME_DISCARDED, already handled).
        private void WaitForEndframeDrain()
        {
            int target = _rtcEndQueued;
            if (_rtcEndDone >= target) return;
            int start = Environment.TickCount;
            var sw = new System.Threading.SpinWait();
            while (_rtcEndDone < target)
            {
                if (Environment.TickCount - start > RtGateTimeoutMs)
                {
                    if (Environment.TickCount - _rtcLastGateWarn > 2000)
                    {
                        _rtcLastGateWarn = Environment.TickCount;
                        Log.LogWarning($"[rtcopy] endframe gate timeout {RtGateTimeoutMs}ms (done={_rtcEndDone} target={target}); proceeding.");
                    }
                    return;
                }
                sw.SpinOnce();
            }
        }

        // Lazily arm the render-thread copy path on first use (single session, so static stash is safe).
        private void InitRenderThreadCopy(D3D11Bridge bridge)
        {
            _rtcInit = true;
            _rtcXr = _xr;
            _rtcBridge = bridge;
            _rtcSwapchains = _swapchains;
            _rtcCbPtr = Marshal.GetFunctionPointerForDelegate(_rtcDel);
            _rtcData0 = Marshal.AllocHGlobal(sizeof(CopyReleaseData));
            _rtcData1 = Marshal.AllocHGlobal(sizeof(CopyReleaseData));
            _rtcCb = new[]
            {
                new UnityEngine.Rendering.CommandBuffer { name = "IronNestVR_CopyRelease0" },
                new UnityEngine.Rendering.CommandBuffer { name = "IronNestVR_CopyRelease1" }
            };

            // Persistent FrameEndInfo tree for the threaded xrEndFrame, wired once.
            _rtcSession = _session;
            _efFei = Marshal.AllocHGlobal(sizeof(FrameEndInfo));
            _efLayerArr = Marshal.AllocHGlobal(IntPtr.Size);
            _efProj = Marshal.AllocHGlobal(sizeof(CompositionLayerProjection));
            _efViews = Marshal.AllocHGlobal(sizeof(CompositionLayerProjectionView) * 2);
            ((CompositionLayerBaseHeader**)_efLayerArr)[0] = (CompositionLayerBaseHeader*)_efProj;
            _rtcEndCb = new UnityEngine.Rendering.CommandBuffer { name = "IronNestVR_EndFrame" };

            _rtcActive = true;
            Log.LogInfo($"[rtcopy] Phase 0 armed: copy+release via IssuePluginEventAndData. mode={UnityEngine.SystemInfo.renderingThreadingMode}");
        }

        private void SubmitProjection()
        {
            var pv = stackalloc CompositionLayerProjectionView[2];
            for (int i = 0; i < 2; i++)
            {
                pv[i] = new CompositionLayerProjectionView
                {
                    Type = StructureType.TypeCompositionLayerProjectionView,
                    Pose = Views[i].Pose,
                    Fov = Views[i].Fov,
                    SubImage = new SwapchainSubImage
                    {
                        Swapchain = _swapchains[i],
                        ImageArrayIndex = 0,
                        ImageRect = new Rect2Di
                        {
                            Offset = new Offset2Di { X = 0, Y = 0 },
                            Extent = new Extent2Di { Width = (int)EyeWidth, Height = (int)EyeHeight }
                        }
                    }
                };
            }
            var layer = new CompositionLayerProjection
            {
                Type = StructureType.TypeCompositionLayerProjection,
                LayerFlags = CompositionLayerFlags.None,
                Space = _localSpace,
                ViewCount = 2,
                Views = pv
            };
            var lp = (CompositionLayerBaseHeader*)&layer;
            EndFrame(&lp, 1);
        }

        // Threaded tail: marshal the projection layer into the persistent FrameEndInfo tree and queue
        // xrEndFrame as a render-stream event ordered AFTER both eyes' copy+release. The main thread does NOT
        // block here — the next frame's BeginFrameLocateViews gate (WaitForEndframeDrain) enforces ordering.
        private void SubmitProjectionThreaded()
        {
            var views = (CompositionLayerProjectionView*)_efViews;
            for (int i = 0; i < 2; i++)
            {
                views[i] = new CompositionLayerProjectionView
                {
                    Type = StructureType.TypeCompositionLayerProjectionView,
                    Pose = Views[i].Pose,
                    Fov = Views[i].Fov,
                    SubImage = new SwapchainSubImage
                    {
                        Swapchain = _swapchains[i],
                        ImageArrayIndex = 0,
                        ImageRect = new Rect2Di
                        {
                            Offset = new Offset2Di { X = 0, Y = 0 },
                            Extent = new Extent2Di { Width = (int)EyeWidth, Height = (int)EyeHeight }
                        }
                    }
                };
            }
            var proj = (CompositionLayerProjection*)_efProj;
            *proj = new CompositionLayerProjection
            {
                Type = StructureType.TypeCompositionLayerProjection,
                LayerFlags = CompositionLayerFlags.None,
                Space = _localSpace,
                ViewCount = 2,
                Views = views
            };
            ((CompositionLayerBaseHeader**)_efLayerArr)[0] = (CompositionLayerBaseHeader*)proj; // re-assert (cheap)
            var fei = (FrameEndInfo*)_efFei;
            *fei = new FrameEndInfo
            {
                Type = StructureType.TypeFrameEndInfo,
                DisplayTime = PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 1,
                Layers = (CompositionLayerBaseHeader**)_efLayerArr
            };

            _inFrame = false;           // logically ending the frame now; the gate guarantees it completes
            _rtcEndQueued++;
            _rtcEndCb.Clear();
            _rtcEndCb.IssuePluginEventAndData(_rtcCbPtr, RtEndFrameEvent, _efFei);
            UnityEngine.Graphics.ExecuteCommandBuffer(_rtcEndCb);
        }

        // -------- helpers --------

        // Report an unexpected OpenXR result. Always drops a crash-proof breadcrumb (the heartbeat
        // survives a native crash, so a bad result right before death is visible), and throttles the
        // BepInEx warning so a wedged session can't spam the log. Useful around focus changes, where a
        // stale session can start returning errors the old code silently ignored.
        private void WarnRc(string call, Result r)
        {
            Dbg.Beat($"{call} -> {r}");
            int now = Environment.TickCount;
            if (now - _lastRcWarnTick > 2000) { _lastRcWarnTick = now; Log.LogWarning($"[xr] {call} returned {r}"); }
        }

        private static ulong MakeVersion(ulong major, ulong minor, ulong patch)
            => (major << 48) | (minor << 32) | patch;

        // Writes a null-terminated ASCII string into a fixed buffer (cap incl. terminator = 128).
        private static void SetFixed(byte* dst, string s)
        {
            int n = Math.Min(s.Length, 127);
            for (int i = 0; i < n; i++) dst[i] = (byte)s[i];
            dst[n] = 0;
        }

        public void Dispose()
        {
            try
            {
                // Stop the render-thread copy path first so no in-flight callback touches a destroyed
                // swapchain. In Direct mode callbacks are synchronous so none are pending here.
                _rtcActive = false;
                _rtcXr = null; _rtcBridge = null; _rtcSwapchains = null;
                if (_rtcCb != null) { foreach (var cb in _rtcCb) { try { cb?.Dispose(); } catch { } } _rtcCb = null; }
                try { _rtcEndCb?.Dispose(); } catch { } _rtcEndCb = null;
                if (_rtcData0 != IntPtr.Zero) { Marshal.FreeHGlobal(_rtcData0); _rtcData0 = IntPtr.Zero; }
                if (_rtcData1 != IntPtr.Zero) { Marshal.FreeHGlobal(_rtcData1); _rtcData1 = IntPtr.Zero; }
                if (_efFei != IntPtr.Zero) { Marshal.FreeHGlobal(_efFei); _efFei = IntPtr.Zero; }
                if (_efLayerArr != IntPtr.Zero) { Marshal.FreeHGlobal(_efLayerArr); _efLayerArr = IntPtr.Zero; }
                if (_efProj != IntPtr.Zero) { Marshal.FreeHGlobal(_efProj); _efProj = IntPtr.Zero; }
                if (_efViews != IntPtr.Zero) { Marshal.FreeHGlobal(_efViews); _efViews = IntPtr.Zero; }

                if (_inFrame) EndFrame(null, 0);
                _input.Dispose();
                if (_swapchainsReady) { _xr.DestroySwapchain(_swapchains[0]); _xr.DestroySwapchain(_swapchains[1]); }
                if (_localCreated) _xr.DestroySpace(_localSpace);
                if (_viewCreated) _xr.DestroySpace(_viewSpace);
                if (_sessionCreated) _xr.DestroySession(_session);
                if (_instanceCreated) _xr.DestroyInstance(_instance);
            }
            catch { /* shutting down */ }
        }
    }
}
