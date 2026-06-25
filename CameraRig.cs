using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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
        // Comfort tunnelling vignette: a single world-space dark-edge quad head-locked in front of the player,
        // rendered by BOTH eye cameras (same proven path as the settings menu) on a private layer carved out of
        // the scene mask so only the eyes draw it. Repositioned + sized to the FOV every frame from the eye poses.
        private GameObject _vig;
        private MeshRenderer _vigMr;
        private int _vigLayer = -1;
        private Material _vigMat;          // its colour alpha is the live darkness
        private float _vigCurrent;         // smoothed 0..1 darkness actually applied
        private Fovf _lastFov;             // most recent eye FOV, for sizing the head-locked overlay
        private float _vigDiagNext;        // throttle for the visibility diagnostic
        private bool _ready;
        private int _eyeMask = -1; // captured scene cull mask (for the eye-cull diagnostic)
        private bool _useEnabledFallback;
        private bool _loggedFallback;
        private float _recenterYaw;
        private float _turnYaw; // accumulated right-stick view turn (degrees)
        private bool _yawInit;

        public bool Ready => _ready;

        /// <summary>The real scene culling mask captured from Main Camera when the eye cameras were created —
        /// i.e. what the headset actually renders. Use this instead of <c>Camera.main.cullingMask</c>, which
        /// the desktop-mirror blank zeroes while in VR. -1 (all layers) until the eyes exist.</summary>
        public int SceneCullMask => _eyeMask;

        /// <summary>Make the eye cameras also render <paramref name="layer"/> — folded into the captured scene
        /// mask so the per-frame <see cref="SetEyeSceneRender"/> keeps it. Used so the map-scope panel (on its
        /// own layer, excluded from the magnifier camera so it doesn't film itself) stays visible in the
        /// headset. Idempotent; no-op until the eyes exist.</summary>
        public void IncludeLayerInEyes(int layer)
        {
            if (layer < 0 || layer > 31) return;
            int bit = 1 << layer;
            if ((_eyeMask & bit) != 0) return;
            _eyeMask |= bit;
            for (int i = 0; i < 2; i++) if (_cam[i] != null) _cam[i].cullingMask |= bit;
        }

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
            AllocVignetteLayer();         // carve one private layer out of the scene mask for the overlay
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

            // Head-lock the comfort vignette once both eye poses for this frame are set (place on the 2nd eye).
            _lastFov = view.Fov;
            if (_vigCurrent > 0.0015f && _vig != null && eye == 1) PlaceVignette();

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
            if (_vigLayer >= 0) m |= (1 << _vigLayer); // both eyes render the shared overlay layer (gated by renderer.enabled)
            for (int i = 0; i < 2; i++)
                if (_cam[i] != null && _cam[i].cullingMask != m) _cam[i].cullingMask = m;
        }

        // ---------------- comfort vignette ----------------

        /// <summary>Drive the tunnelling vignette toward <paramref name="target"/> darkness (0..1), smoothing
        /// over <see cref="Config.VignetteFadeTime"/>. Builds the overlay lazily on first use and toggles the
        /// per-eye renderers on/off as it crosses zero. Called every gameplay frame by <see cref="Locomotion"/>.</summary>
        public void SetVignette(float target, float dt)
        {
            target = Mathf.Clamp01(target);
            // Exponential smoothing toward the target (snappy, frame-rate independent).
            float k = (Config.VignetteFadeTime > 0.0001f) ? 1f - Mathf.Exp(-dt / Config.VignetteFadeTime) : 1f;
            _vigCurrent = Mathf.Lerp(_vigCurrent, target, Mathf.Clamp01(k));
            if (_vigCurrent < 0.0015f && target <= 0f) _vigCurrent = 0f;

            bool show = _vigCurrent > 0.0015f;
            if (show) EnsureVignette();
            if (_vigMr != null && _vigMr.enabled != show) _vigMr.enabled = show;
            if (_vigMat != null)
            {
                var c = new Color(0f, 0f, 0f, _vigCurrent);
                try { if (_vigMat.HasProperty("_BaseColor")) _vigMat.SetColor("_BaseColor", c); } catch { }
                try { _vigMat.color = c; } catch { }
                try { if (_vigMat.HasProperty("_Color")) _vigMat.SetColor("_Color", c); } catch { }
            }

            // Decisive diagnostic while the vignette SHOULD be showing: is it culled, or drawing-but-invisible?
            if (_vigMr != null && _vigCurrent > 0.05f && Time.unscaledTime >= _vigDiagNext)
            {
                _vigDiagNext = Time.unscaledTime + 1f;
                Vector3 wp = _vig != null ? _vig.transform.position : Vector3.zero;
                Vector3 ls = _vig != null ? _vig.transform.lossyScale : Vector3.zero;
                Vector3 ep = _cam[0] != null ? _cam[0].transform.position : Vector3.zero;
                float ba = -1f, sb = -1f, db = -1f;
                try { if (_vigMat.HasProperty("_BaseColor")) ba = _vigMat.GetColor("_BaseColor").a; } catch { }
                try { sb = _vigMat.GetFloat("_SrcBlend"); db = _vigMat.GetFloat("_DstBlend"); } catch { }
                Log.LogInfo($"[vignette] state cur={_vigCurrent:0.00} en={_vigMr.enabled} vis={_vigMr.isVisible} " +
                            $"act={(_vig != null && _vig.activeInHierarchy)} pos={wp} eye0={ep} scale={ls} " +
                            $"baseA={ba:0.00} blend={sb}/{db} q={_vigMat.renderQueue} kw={_vigMat.shaderKeywords?.Length ?? -1}");
            }
        }

        // Reserve ONE layer for the overlay. The main camera renders "Everything", so we can't find an unused bit;
        // instead pick an UNNAMED layer (no game geometry by convention) and CARVE it OUT of the eye scene mask,
        // then add it back to BOTH eyes in SetEyeSceneRender. Robust to any mask.
        private void AllocVignetteLayer()
        {
            int a = -1;
            for (int layer = 31; layer >= 8; layer--)
            {
                if (!string.IsNullOrEmpty(SafeLayerName(layer))) continue; // named ⇒ may hold geometry — avoid
                a = layer; break;
            }
            if (a < 0) a = 31;
            _vigLayer = a;
            _eyeMask &= ~(1 << a);
            Log.LogInfo($"[vignette] reserved overlay layer {a} (carved out of the scene mask).");
        }

        private static string SafeLayerName(int layer)
        {
            try { return LayerMask.LayerToName(layer); } catch { return null; }
        }

        // Configure a URP/Unlit material for alpha-blended, double-sided, depth-write-off transparency at runtime
        // (URP reads these float props directly in its pass blend state, so it works without the material editor).
        internal static void MakeTransparentUnlit(Material m)
        {
            if (m == null) return;
            try { m.SetFloat("_Surface", 1f); } catch { }   // 0 opaque, 1 transparent
            try { m.SetFloat("_Blend", 0f); } catch { }      // alpha blend
            try { m.SetFloat("_SrcBlend", 5f); } catch { }   // SrcAlpha
            try { m.SetFloat("_DstBlend", 10f); } catch { }  // OneMinusSrcAlpha
            try { m.SetFloat("_ZWrite", 0f); } catch { }
            try { m.SetFloat("_AlphaClip", 0f); } catch { }
            try { m.SetFloat("_Cull", 0f); } catch { }       // double-sided
            try { m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); } catch { }
            try { m.DisableKeyword("_ALPHATEST_ON"); } catch { }
            try { m.DisableKeyword("_ALPHAPREMULTIPLY_ON"); } catch { }
            m.renderQueue = 3000; // Transparent
        }

        private void EnsureVignette()
        {
            if (_vig != null || _vigLayer < 0) return;
            if (_vigMat == null)
            {
                // MUST be a real URP shader — built-in "Sprites/Default" silently doesn't draw under this URP
                // pipeline (the working menu/laser use URP/Unlit). Configure it for alpha-blended transparency.
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Transparent");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                _vigMat = new Material(sh);
                MakeTransparentUnlit(_vigMat);
                var tex = BuildVignetteTexture();
                _vigMat.mainTexture = tex;
                try { if (_vigMat.HasProperty("_BaseMap")) _vigMat.SetTexture("_BaseMap", tex); } catch { }
            }
            // ONE world-space quad (not a child of a camera) — the exact path the settings menu renders on.
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "IronNestVR_Vignette";
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);    // never block the laser/grab raycasts
            go.layer = _vigLayer;
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.material = _vigMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;
            }
            _vig = go;
            _vigMr = mr;
            PlaceVignette(); // position it now so it isn't drawn at the origin for a frame
            int m0 = _cam[0] != null ? _cam[0].cullingMask : 0;
            bool has = (m0 & (1 << _vigLayer)) != 0;
            Log.LogInfo($"[vignette] overlay built (layer {_vigLayer}; eye mask=0x{(uint)m0:X8} hasLayer={has}; " +
                        $"shader='{(_vigMat != null && _vigMat.shader != null ? _vigMat.shader.name : "null")}').");
        }

        // Head-lock the overlay: centre it in front of the eye-midpoint, facing the head, sized to cover the FOV
        // (+ a little for the per-eye IPD offset). Called each frame once both eye poses are current.
        private void PlaceVignette()
        {
            if (_vig == null || _cam[0] == null || _cam[1] == null) return;
            Vector3 p0 = _cam[0].transform.position, p1 = _cam[1].transform.position;
            Vector3 mid = (p0 + p1) * 0.5f;
            Quaternion rot = Quaternion.Slerp(_cam[0].transform.rotation, _cam[1].transform.rotation, 0.5f);
            float d = Mathf.Max(Config.NearClip + 0.03f, Config.VignetteDistance);
            _vig.transform.SetPositionAndRotation(mid + (rot * Vector3.forward) * d, rot);
            float m = Mathf.Max(1f, Config.VignetteCoverMargin);
            float tanX = Mathf.Max(Mathf.Abs(Mathf.Tan(_lastFov.AngleLeft)), Mathf.Tan(_lastFov.AngleRight));
            float tanY = Mathf.Max(Mathf.Tan(_lastFov.AngleUp), Mathf.Abs(Mathf.Tan(_lastFov.AngleDown)));
            if (tanX < 0.1f || float.IsNaN(tanX)) tanX = 1f; // FOV not read yet — generous (~45°)
            if (tanY < 0.1f || float.IsNaN(tanY)) tanY = 1f;
            // Size STRICTLY proportional to the FOV (no fixed slack — a fixed term swamps a small FOV and pushes
            // the whole dark ring outside the view). Margin is the only overscan, so the ratio holds at any FOV.
            _vig.transform.localScale = new Vector3(2f * d * tanX * m, 2f * d * tanY * m, 1f);
        }

        // Black RGB, radial alpha ramp: clear inside ApertureInner, fully opaque by ApertureOuter (of the
        // half-extent). Generated once; live darkness comes from the material's _Color alpha.
        private static Texture2D BuildVignetteTexture()
        {
            const int N = 96;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            float inner = Config.VignetteApertureInner, outer = Mathf.Max(Config.VignetteApertureOuter, inner + 0.01f);
            var px = new Il2CppStructArray<Color32>(N * N);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N - 0.5f;
                    float dy = (y + 0.5f) / N - 0.5f;
                    float nr = Mathf.Sqrt(dx * dx + dy * dy) / 0.5f; // 0 centre, 1 at edge-midpoint
                    float t = Mathf.Clamp01((nr - inner) / (outer - inner));
                    float a = t * t * (3f - 2f * t);                 // smoothstep
                    px[y * N + x] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
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
            // The vignette is a standalone world-space object now — destroy it explicitly.
            if (_vig != null) { UnityEngine.Object.Destroy(_vig); _vig = null; _vigMr = null; }
            if (_vigMat != null) { UnityEngine.Object.Destroy(_vigMat); _vigMat = null; }
            _vigCurrent = 0f;
            if (_origin != null) UnityEngine.Object.Destroy(_origin);
            _ready = false;
        }
    }
}
