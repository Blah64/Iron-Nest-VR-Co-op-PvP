using System;
using BepInEx.Logging;
using Silk.NET.OpenXR;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace IronNestVR
{
    /// <summary>
    /// Builds and drives the two per-eye cameras. Each frame the rig origin is snapped to the game's
    /// main camera position (the gunner's seat) with a recenterable yaw; the eye cameras are placed
    /// at the OpenXR head pose and given the runtime's asymmetric projection, then rendered into their
    /// own RenderTextures. <see cref="XrSession"/> copies those into the OpenXR swapchains.
    /// </summary>
    internal sealed class CameraRig
    {
        private static ManualLogSource Log => Plugin.Logger;

        private GameObject _origin;
        private readonly Camera[] _cam = new Camera[2];
        private readonly RenderTexture[] _rt = new RenderTexture[2];
        // One flip buffer PER EYE. A single shared buffer is NOT safe here: Graphics.Blit is deferred into
        // Unity's render phase while the raw CopyResource runs immediately on the D3D11 immediate context, so
        // the two aren't ordered — both eyes' copies would read whichever blit landed last (broken/mono image).
        private readonly RenderTexture[] _flipRT = new RenderTexture[2];
        private bool _ready;
        private int _eyeMask = -1; // captured scene cull mask (for the eye-cull diagnostic)
        private bool _useEnabledFallback;
        private bool _loggedFallback;
        private float _recenterYaw;
        private float _turnYaw; // accumulated right-stick view turn (degrees)
        private bool _yawInit;

        public bool Ready => _ready;

        /// <summary>The seated rig origin (game camera pose + recenter yaw). Controllers are placed
        /// relative to this so they line up with the rendered world. Null until cameras exist.</summary>
        public Transform OriginTransform => _origin != null ? _origin.transform : null;

        /// <summary>Full head pose (midpoint of the two eyes + head orientation) for HUD follow.
        /// Returns false until the eye cameras exist.</summary>
        public bool TryGetHeadPose(out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (_cam[0] == null) return false;
            if (_cam[1] != null)
            {
                pos = (_cam[0].transform.position + _cam[1].transform.position) * 0.5f;
                rot = Quaternion.Slerp(_cam[0].transform.rotation, _cam[1].transform.rotation, 0.5f);
            }
            else { pos = _cam[0].transform.position; rot = _cam[0].transform.rotation; }
            return true;
        }

        /// <summary>Horizontal head facing (left eye forward/right, flattened) for stick locomotion.
        /// Falls back to the rig origin before the eyes exist. Returns false if neither is ready.</summary>
        public bool TryGetHeadBasis(out Vector3 fwd, out Vector3 right)
        {
            fwd = Vector3.forward; right = Vector3.right;
            Transform t = (_cam[0] != null) ? _cam[0].transform : (_origin != null ? _origin.transform : null);
            if (t == null) return false;
            Vector3 f = t.forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-5f) { f = t.up; f.y = 0f; } // looking straight up/down: use head-up
            if (f.sqrMagnitude < 1e-5f) return false;
            fwd = f.normalized;
            right = new Vector3(fwd.z, 0f, -fwd.x); // 90° CW horizontal
            return true;
        }

        public bool EnsureCameras(uint w, uint h, GraphicsFormat fmt)
        {
            if (_ready) return true;
            Dbg.Step("EnsureCameras: Camera.main");
            var main = Camera.main;
            if (main == null) return false;

            Dbg.Step("read main props");
            var mClear = main.clearFlags;
            var mBg = main.backgroundColor;
            var mMask = main.cullingMask;
            _eyeMask = mMask;
            var mDepth = main.depth;
            Dbg.Step($"main props ok: clear={mClear} mask={mMask} depth={mDepth}");

            _origin = new GameObject("IronNestVR_Rig");
            UnityEngine.Object.DontDestroyOnLoad(_origin);
            _origin.hideFlags = HideFlags.HideAndDontSave;
            Dbg.Step("origin created");

            for (int i = 0; i < 2; i++)
            {
                Dbg.Step($"eye{i}: new RenderTextureDescriptor");
                var desc = new RenderTextureDescriptor((int)w, (int)h, fmt, 24) { msaaSamples = 1 };
                Dbg.Step($"eye{i}: new RenderTexture");
                _rt[i] = new RenderTexture(desc);
                Dbg.Step($"eye{i}: RT.Create()");
                _rt[i].Create();
                if (Config.FlipY)
                {
                    _flipRT[i] = new RenderTexture(desc);
                    _flipRT[i].Create();
                }
                Dbg.Step($"eye{i}: GetNativeTexturePtr");
                var nptr = _rt[i].GetNativeTexturePtr();
                Dbg.Step($"eye{i}: RT native=0x{nptr:X}");

                Dbg.Step($"eye{i}: new GameObject");
                var go = new GameObject("IronNestVR_Eye" + i);
                Dbg.Step($"eye{i}: SetParent");
                go.transform.SetParent(_origin.transform, false);
                Dbg.Step($"eye{i}: AddComponent<Camera>");
                var c = go.AddComponent<Camera>();
                Dbg.Step($"eye{i}: set clearFlags");
                c.clearFlags = mClear;
                Dbg.Step($"eye{i}: set backgroundColor");
                c.backgroundColor = mBg;
                Dbg.Step($"eye{i}: set cullingMask");
                c.cullingMask = mMask;
                Dbg.Step($"eye{i}: set allowHDR");
                c.allowHDR = Config.EyeAllowHDR; // force LDR on the eye cameras (the RTs are RGBA8 anyway)
                // NOTE: do NOT set camera.stereoTargetEye here — it pokes Unity's stereo/XR subsystem
                // (uninitialized in this no-XR IL2CPP build) and hard-crashes. We render via target
                // texture anyway, so it isn't needed.
                Dbg.Step($"eye{i}: set targetTexture");
                c.targetTexture = _rt[i];
                Dbg.Step($"eye{i}: set near/far/depth");
                c.nearClipPlane = Config.NearClip;
                c.farClipPlane = Config.FarClip;
                // Unique depth per eye: the game's Ultimate LOD System spams a per-frame LogError when two
                // active cameras share a depth, so no two of our cameras may collide (eye0/eye1 here, and
                // the pointer cams in CockpitInteractor).
                c.depth = mDepth - 10 - i;
                Dbg.Step($"eye{i}: set enabled={Config.UseEnabledCameras}");
                c.enabled = Config.UseEnabledCameras;

                if (Config.SolidColorTest)
                {
                    c.cullingMask = 0;
                    c.clearFlags = CameraClearFlags.SolidColor;
                    c.backgroundColor = i == 0 ? new Color(0.10f, 0.10f, 0.45f) : new Color(0.10f, 0.45f, 0.10f);
                }
                _cam[i] = c;
                Dbg.Step($"eye{i}: DONE");
            }

            if (Config.UseEnabledCameras) _useEnabledFallback = true;
            Log.LogInfo($"Eye cameras created ({w}x{h}, {fmt}). Mode={(Config.UseEnabledCameras ? "enabled-cameras" : "render-request")}.");
            _ready = true;
            return true;
        }

        public void UpdateOrigin()
        {
            var main = Camera.main;
            if (main == null || _origin == null) return;
            if (!_yawInit) { _recenterYaw = main.transform.eulerAngles.y; _yawInit = true; }
            _origin.transform.SetPositionAndRotation(main.transform.position, Quaternion.Euler(0f, _recenterYaw + _turnYaw, 0f));
        }

        /// <summary>Right-stick smooth view turn: rotate the whole rig yaw by <paramref name="degrees"/>.</summary>
        public void ApplyTurn(float degrees) => _turnYaw += degrees;

        public void Recenter()
        {
            _yawInit = false; // re-capture the game camera's yaw on the next UpdateOrigin
            _turnYaw = 0f;    // and face forward again
            Log.LogInfo("VR view recentered.");
        }

        /// <summary>Positions an eye camera from the XR view, renders it, returns its RT native handle.</summary>
        private int _renderCalls;

        public IntPtr RenderEye(int eye, in View view)
        {
            bool tr = _renderCalls++ < 4;
            var c = _cam[eye];
            var p = view.Pose.Position;
            var q = view.Pose.Orientation;

            // OpenXR (right-handed, -Z fwd) -> Unity (left-handed, +Z fwd).
            if (tr) Dbg.Step($"RenderEye{eye}: set transform");
            c.transform.localPosition = new Vector3(p.X, p.Y, -p.Z);
            c.transform.localRotation = new Quaternion(-q.X, -q.Y, q.Z, q.W);
            if (tr) Dbg.Step($"RenderEye{eye}: set projectionMatrix");
            c.projectionMatrix = ProjectionFromFov(view.Fov, Config.NearClip, Config.FarClip);

            if (!_useEnabledFallback)
            {
                try
                {
                    if (tr) Dbg.Step($"RenderEye{eye}: new SingleCameraRequest");
                    var req = new UniversalRenderPipeline.SingleCameraRequest { destination = _rt[eye] };
                    if (tr) Dbg.Step($"RenderEye{eye}: SupportsRenderRequest");
                    bool supported = RenderPipeline.SupportsRenderRequest(c, req);
                    if (tr) Dbg.Step($"RenderEye{eye}: supported={supported}");
                    if (supported)
                    {
                        if (tr) Dbg.Step($"RenderEye{eye}: SubmitRenderRequest");
                        RenderPipeline.SubmitRenderRequest(c, req);
                        if (tr) Dbg.Step($"RenderEye{eye}: SubmitRenderRequest returned");
                    }
                    else
                        EnableFallback();
                }
                catch (Exception e)
                {
                    Log.LogWarning("Render request failed, switching to enabled-camera mode: " + e.Message);
                    EnableFallback();
                }
            }
            else if (tr) Dbg.Step($"RenderEye{eye}: enabled-camera mode (auto render)");
            // In fallback mode the camera auto-renders each frame into its RT (one frame latency).
            // Vertical flip (Unity RT origin vs OpenXR swapchain) via a Blit — NOT a projection
            // Y-flip, which would reverse triangle winding and render geometry inside-out.
            if (Config.FlipY && _flipRT[eye] != null)
            {
                Graphics.Blit(_rt[eye], _flipRT[eye], new Vector2(1f, -1f), new Vector2(0f, 1f));
                return _flipRT[eye].GetNativeTexturePtr();
            }
            return _rt[eye].GetNativeTexturePtr();
        }

        private void EnableFallback()
        {
            _useEnabledFallback = true;
            for (int i = 0; i < 2; i++) if (_cam[i] != null) _cam[i].enabled = true;
            if (!_loggedFallback) { _loggedFallback = true; Log.LogInfo("Using enabled-camera rendering fallback."); }
        }

        /// <summary>
        /// In enabled-camera (auto-render) mode, toggle whether the two eye cameras render this frame.
        /// They otherwise auto-render the full scene EVERY Unity frame regardless of whether anything is
        /// submitted — pure wasted GPU while the runtime says we shouldn't render (SteamVR dashboard up,
        /// headset off the face). Drive it from the per-frame OpenXR <c>ShouldRender</c> flag. No-op in
        /// render-request mode, where the cameras stay disabled and are rendered explicitly only when we
        /// submit. Cheap and idempotent (only writes on a state change).
        /// </summary>
        public void SetEyeCamerasEnabled(bool on)
        {
            if (!_useEnabledFallback) return;
            for (int i = 0; i < 2; i++)
                if (_cam[i] != null && _cam[i].enabled != on) _cam[i].enabled = on;
        }

        /// <summary>Diagnostic: toggle the eye cameras' SCENE rendering via cullingMask WITHOUT disabling
        /// them, so the pipeline stays healthy (they still clear to skybox and produce a valid image). Off ⇒
        /// mask 0 (skybox only) to measure how much of the frame is the per-eye scene draw; on ⇒ the captured
        /// real mask. Cheap + idempotent (writes only on change).</summary>
        public void SetEyeSceneRender(bool on)
        {
            int m = on ? _eyeMask : 0;
            for (int i = 0; i < 2; i++)
                if (_cam[i] != null && _cam[i].cullingMask != m) _cam[i].cullingMask = m;
        }

        /// <summary>Builds an OpenGL-convention asymmetric projection from an OpenXR FOV.</summary>
        private static Matrix4x4 ProjectionFromFov(Fovf fov, float near, float far)
        {
            float l = Mathf.Tan(fov.AngleLeft);
            float r = Mathf.Tan(fov.AngleRight);
            float d = Mathf.Tan(fov.AngleDown);
            float u = Mathf.Tan(fov.AngleUp);
            float w = r - l;
            float h = u - d;

            // Do NOT flip Y here — vertical flip is handled by the Blit in RenderEye. Flipping both
            // (projection + blit) cancels out, and a projection flip also reverses triangle winding.
            var m = new Matrix4x4();
            m.m00 = 2f / w;
            m.m02 = (r + l) / w;
            m.m11 = 2f / h;
            m.m12 = (u + d) / h;
            m.m22 = -(far + near) / (far - near);
            m.m23 = -(2f * far * near) / (far - near);
            m.m32 = -1f;
            return m;
        }

        public void Destroy()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_cam[i] != null) UnityEngine.Object.Destroy(_cam[i].gameObject);
                if (_rt[i] != null) { _rt[i].Release(); UnityEngine.Object.Destroy(_rt[i]); }
                if (_flipRT[i] != null) { _flipRT[i].Release(); UnityEngine.Object.Destroy(_flipRT[i]); }
            }
            if (_origin != null) UnityEngine.Object.Destroy(_origin);
            _ready = false;
        }
    }
}
