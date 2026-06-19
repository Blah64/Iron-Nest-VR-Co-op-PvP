using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Renders the two tracked hands. The mesh + finger animation come from a free Unity hand asset
    /// shipped as an <b>AssetBundle</b> next to the plugin (a shipped IL2CPP build can't import a Unity
    /// package, so the art rides in via a bundle and we drive it from code). If the bundle or a prefab
    /// is missing, a primitive proxy hand is built so the grab mechanic still works without the art.
    ///
    /// Each frame a hand follows its controller's grip pose, with finger curl from trigger/grip. The
    /// <see cref="SetGrab"/> override lets <see cref="HandManipulator"/> peel a hand off the controller
    /// and ride a grabbed dial/lever (the "gravity glove" — the hand flies to the control and stays on
    /// it while your real hand is across the cockpit). The hand lerps to/from the override over
    /// <see cref="Config.HandSnapTime"/>.
    /// </summary>
    internal sealed class HandVisuals
    {
        private static ManualLogSource Log => Plugin.Logger;

        // One finger joint bone we curl procedurally, remembering its loaded (bind) local rotation.
        private sealed class FingerJoint
        {
            public Transform T;
            public Quaternion Bind;
            public bool IsThumb;
            public bool IsIndex;
        }

        private sealed class Hand
        {
            public GameObject Root;        // pose anchor (set to the grip pose); model is a child carrying offsets
            public Transform T;
            public Transform Model;        // the instantiated model child (offsets/scale live here)
            public Vector3 IntrinsicScale; // model localScale as loaded, before HandScale
            public bool Mirrored;          // model X-mirrored (right prefab reused for left)
            public Animator Anim;          // optional finger-curl driver (if the prefab ships one)
            public List<FingerJoint> Joints = new List<FingerJoint>(); // procedural curl bones
            public float CurlIndex, CurlOther;  // smoothed 0..1 curl amounts
            public bool HasOverride;
            public Vector3 OverridePos;
            public Quaternion OverrideRot;
            public float Blend;            // 0 = follow controller, 1 = at override target
        }

        private readonly Hand _right = new Hand();
        private readonly Hand _left = new Hand();
        private AssetBundle _bundle;
        private bool _bundleTried;
        private bool _built;
        private bool _dumpedBones;   // log the hand bone hierarchy only once

        // Unity 6 marshals AssetBundle byte[]/string params through Il2CppSystem.Span.GetPinnableReference,
        // which this il2cpp build never AOT-compiled — so the GENERATED LoadFromFile/LoadFromMemory/LoadAsset
        // wrappers all throw (ReadOnlySpan MissingMethod / "Object was garbage collected"). We bypass them by
        // resolving the raw engine icalls and feeding them the pointer+length struct ourselves. Il2CppInterop's
        // ManagedSpanWrapper is internal to each interop assembly, so we declare a layout-identical struct
        // ({ void* begin; int length }) — the engine reads it the same way. We pass its address, matching the
        // engine wrapper's `Unsafe.AsPointer(ref span)`. (Memory load returned null for this build's bundle;
        // LoadFromFile is a separate, more robust engine path, so it's primary.)
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct NativeSpan { public void* Ptr; public int Length; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LoadFromFileInjected(IntPtr pathSpan, uint crc, ulong offset);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LoadFromMemoryInjected(IntPtr binarySpan, uint crc);
        private static LoadFromFileInjected _loadFromFileICall;
        private static LoadFromMemoryInjected _loadFromMemICall;

        // ---------------- per-frame ----------------

        public void Tick(VrInput input, CameraRig rig, bool active)
        {
            try
            {
                if (!Config.HandsEnabled) { Hide(); return; }
                if (!active) { Hide(); return; }
                var origin = rig.OriginTransform;
                if (origin == null) { Hide(); return; }

                EnsureBuilt();

                float dt = Time.unscaledDeltaTime;

                // Right hand: grip pose + trigger (index curl) + grip (other fingers).
                PoseHand(_right, input.GripValid, origin, input.GripPose,
                         input.Trigger, input.GrabR ? 1f : 0f, dt);
                // Left hand: no left trigger action exists, so the index follows the grip too (full fist).
                float lgrip = input.GrabL ? 1f : 0f;
                PoseHand(_left, input.GripValidL, origin, input.GripPoseL, lgrip, lgrip, dt);
            }
            catch (Exception e)
            {
                Log.LogWarning("[hands] " + e.Message);
            }
        }

        private void PoseHand(Hand h, bool tracked, Transform origin, Posef pose,
                              float trigger, float grip, float dt)
        {
            if (h.Root == null) return;
            bool show = tracked || h.HasOverride;
            if (h.Root.activeSelf != show) h.Root.SetActive(show);
            if (!show) return;

            GetWorld(origin, pose, out Vector3 followPos, out Quaternion followRot);

            float target = h.HasOverride ? 1f : 0f;
            float rate = Config.HandSnapTime > 0.001f ? dt / Config.HandSnapTime : 1f;
            h.Blend = Mathf.MoveTowards(h.Blend, target, rate);

            Vector3 pos = followPos;
            Quaternion rot = followRot;
            if (h.Blend > 0f)
            {
                pos = Vector3.Lerp(followPos, h.OverridePos, h.Blend);
                rot = Quaternion.Slerp(followRot, h.OverrideRot, h.Blend);
            }
            h.T.SetPositionAndRotation(pos, rot);

            ApplyOffsets(h);   // live, so the in-VR menu can tune scale/pose without a rebuild

            // Fingers close fully as the hand reaches the grabbed control (Blend -> 1).
            float idx = Mathf.Lerp(trigger, 1f, h.Blend);
            float oth = Mathf.Lerp(grip, 1f, h.Blend);
            // Ease the on/off grip so the fist doesn't pop (trigger is already analog).
            float k = Config.FingerCurlSmooth > 0f ? 1f - Mathf.Exp(-Config.FingerCurlSmooth * dt) : 1f;
            h.CurlIndex = Mathf.Lerp(h.CurlIndex, idx, k);
            h.CurlOther = Mathf.Lerp(h.CurlOther, oth, k);
            ApplyCurl(h);
        }

        // Reapply the model child's scale/offset/rotation from Config every frame, so the settings menu
        // edits it live. HandScale multiplies the model's intrinsic scale; left mirror is a negative X.
        private void ApplyOffsets(Hand h)
        {
            if (h.Model == null) return;
            float s = Config.HandScale;
            Vector3 bs = h.IntrinsicScale;
            h.Model.localScale = new Vector3((h.Mirrored ? -1f : 1f) * bs.x * s, bs.y * s, bs.z * s);
            h.Model.localPosition = new Vector3(Config.HandPosOffsetX, Config.HandPosOffsetY, Config.HandPosOffsetZ);
            h.Model.localEulerAngles = new Vector3(Config.HandEulerX, Config.HandEulerY, Config.HandEulerZ);
        }

        // Curl the finger bones from the smoothed amounts. Index follows the trigger; thumb + other
        // fingers follow the grip. Each joint rotates off its bind pose about the configured local axis.
        private void ApplyCurl(Hand h)
        {
            // If the prefab shipped an Animator with the named params, prefer that.
            if (h.Anim != null)
            {
                try
                {
                    if (!string.IsNullOrEmpty(Config.HandTriggerParam)) h.Anim.SetFloat(Config.HandTriggerParam, h.CurlIndex);
                    if (!string.IsNullOrEmpty(Config.HandGripParam)) h.Anim.SetFloat(Config.HandGripParam, h.CurlOther);
                }
                catch { }
                return;
            }
            if (!Config.FingerCurlEnabled || h.Joints == null || h.Joints.Count == 0) return;

            Vector3 axis = Config.FingerCurlAxis == 0 ? Vector3.right
                         : Config.FingerCurlAxis == 1 ? Vector3.up : Vector3.forward;
            float sign = Config.FingerCurlSign * (h.Mirrored ? -1f : 1f);
            float max = Config.FingerCurlMaxDeg;
            for (int i = 0; i < h.Joints.Count; i++)
            {
                var j = h.Joints[i];
                if (j.T == null) continue;
                float amt = j.IsIndex ? h.CurlIndex : h.CurlOther;
                float deg = sign * amt * max * (j.IsThumb ? 0.6f : 1f); // thumb folds less
                j.T.localRotation = j.Bind * Quaternion.AngleAxis(deg, axis);
            }
        }

        // Override the right/left hand to a world pose (used while it rides a grabbed control).
        public void SetGrab(bool right, Vector3 pos, Quaternion rot)
        {
            var h = right ? _right : _left;
            h.HasOverride = true;
            h.OverridePos = pos;
            h.OverrideRot = rot;
        }

        public void ClearGrab(bool right)
        {
            (right ? _right : _left).HasOverride = false;
        }

        private void Hide()
        {
            if (_right.Root != null && _right.Root.activeSelf) _right.Root.SetActive(false);
            if (_left.Root != null && _left.Root.activeSelf) _left.Root.SetActive(false);
        }

        // ---------------- build / load ----------------

        private void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            TryLoadBundle();
            Build(_right, true);
            Build(_left, false);
            Log.LogInfo($"[hands] built (bundle={(_bundle != null ? Config.HandBundleFile : "none — primitive fallback")}).");
        }

        private void TryLoadBundle()
        {
            if (_bundleTried) return;
            _bundleTried = true;
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, Config.HandBundleFile);
                if (!File.Exists(path)) { Log.LogInfo($"[hands] no AssetBundle at '{path}' — using primitive hands."); return; }

                // PRIMARY: LoadFromStream over an il2cpp MemoryStream. This is a fully GENERATED interop path
                // that touches NO spans (the stream is passed as an object pointer), so it sidesteps the
                // GetPinnableReference AOT gap that kills LoadFromMemory/LoadFromFile here. The raw icalls are
                // kept only as fallbacks (both returned null on this build).
                _bundle = LoadViaStream(path);
                if (_bundle == null) { Log.LogWarning("[hands] LoadFromStream null; trying LoadFromFile icall..."); _bundle = LoadViaFileICall(path); }
                if (_bundle == null) { Log.LogWarning("[hands] LoadFromFile null; trying LoadFromMemory icall..."); _bundle = LoadViaMemoryICall(path); }
                if (_bundle == null) { Log.LogWarning("[hands] all bundle load paths returned null — primitive fallback."); return; }
                // NOTE: do NOT call LoadAllAssets()/LoadAsset(name,Type) here — those use the injected span path
                // and throw GetPinnableReference in this build. We load each hand later via the generic
                // LoadAsset<T>(name), which marshals the name the old (span-free) way. See LoadPrefab.
                Log.LogInfo("[hands] bundle loaded OK.");
            }
            catch (Exception e) { Log.LogWarning("[hands] bundle load failed: " + e.Message); }
        }

        // LoadFromStream via the GENERATED wrapper — the stream is marshalled as an object pointer, so this
        // path never touches Il2CppSystem.Span/GetPinnableReference. We wrap the bundle bytes in an il2cpp
        // MemoryStream (creating an Il2CppStructArray<byte> is fine; only LoadFromMemory's INTERNAL span
        // conversion was broken). This is the robust "proper loader" the manual icalls failed to be.
        private AssetBundle LoadViaStream(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Log.LogInfo($"[hands] LoadFromStream (il2cpp MemoryStream, {bytes.Length} bytes)...");
                var arr = new Il2CppStructArray<byte>(bytes);              // managed byte[] -> il2cpp byte[]
                var ms = new Il2CppSystem.IO.MemoryStream(arr);           // CanRead + CanSeek (Validate passes)
                var bundle = AssetBundle.LoadFromStream(ms);
                return bundle;
            }
            catch (Exception e) { Log.LogWarning("[hands] LoadFromStream threw: " + e.Message); return null; }
        }

        // LoadFromFile via the raw engine icall — bypasses the generated wrapper whose
        // Il2CppSystem.ReadOnlySpan.GetPinnableReference isn't AOT-compiled in this build.
        private unsafe AssetBundle LoadViaFileICall(string path)
        {
            try
            {
                if (_loadFromFileICall == null)
                    _loadFromFileICall = IL2CPP.ResolveICall<LoadFromFileInjected>("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected");
                if (_loadFromFileICall == null) { Log.LogWarning("[hands] could not resolve LoadFromFile_Internal_Injected."); return null; }
                Log.LogInfo($"[hands] LoadFromFile icall ('{path}', {path.Length} chars)...");
                fixed (char* cp = path)
                {
                    NativeSpan w = new NativeSpan { Ptr = cp, Length = path.Length };
                    IntPtr res = _loadFromFileICall((IntPtr)(&w), 0u, 0uL);
                    return res != IntPtr.Zero ? new AssetBundle(res) : null;
                }
            }
            catch (Exception e) { Log.LogWarning("[hands] LoadFromFile icall threw: " + e.Message); return null; }
        }

        private unsafe AssetBundle LoadViaMemoryICall(string path)
        {
            try
            {
                if (_loadFromMemICall == null)
                    _loadFromMemICall = IL2CPP.ResolveICall<LoadFromMemoryInjected>("UnityEngine.AssetBundle::LoadFromMemory_Internal_Injected");
                if (_loadFromMemICall == null) { Log.LogWarning("[hands] could not resolve LoadFromMemory_Internal_Injected."); return null; }
                byte[] bytes = File.ReadAllBytes(path);
                Log.LogInfo($"[hands] LoadFromMemory icall ({bytes.Length} bytes)...");
                fixed (byte* p = bytes)
                {
                    NativeSpan w = new NativeSpan { Ptr = p, Length = bytes.Length };
                    IntPtr res = _loadFromMemICall((IntPtr)(&w), 0u);
                    return res != IntPtr.Zero ? new AssetBundle(res) : null;
                }
            }
            catch (Exception e) { Log.LogWarning("[hands] LoadFromMemory icall threw: " + e.Message); return null; }
        }

        private void Build(Hand h, bool right)
        {
            var root = new GameObject(right ? "IronNestVR_HandR" : "IronNestVR_HandL");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;

            bool mirrored = false;
            GameObject model = LoadPrefab(right);
            if (model == null && !right) { model = LoadPrefab(true); mirrored = true; } // mirror right for left if no left
            if (model == null) model = BuildPrimitive();

            model.transform.SetParent(root.transform, false);
            Vector3 bs = model.transform.localScale; // intrinsic; ApplyOffsets multiplies HandScale onto it live

            StripColliders(root);   // never let a hand block the laser raycast or physics
            SetLayer(root, 0);      // Default layer — eye cameras copy Main Camera's cullingMask
            if (Config.HandFixMaterials) FixMaterials(root);
            PrepareRenderers(root, right, bs);

            h.Root = root;
            h.T = root.transform;
            h.Model = model.transform;
            h.IntrinsicScale = bs;
            h.Mirrored = mirrored;
            h.Anim = root.GetComponentInChildren<Animator>(true);
            CollectJoints(h, model.transform);
            ApplyOffsets(h);        // apply scale/pose once now (then live each frame while shown)
            root.SetActive(false);
        }

        // Find finger-joint bones for procedural curl by matching their names (the XR Hands FBX has no
        // Animator). We match finger {thumb/index/middle/ring/little|pinky} × segment
        // {proximal/intermediate/middle/distal} and remember each bone's bind-pose local rotation.
        // Metacarpals/tips/wrist are skipped. Logs the full bone list ONCE so exact rig names are knowable.
        private void CollectJoints(Hand h, Transform model)
        {
            h.Joints.Clear();
            if (h.Anim != null) return; // an Animator-driven prefab curls itself

            var all = model.GetComponentsInChildren<Transform>(true);
            int n = all != null ? all.Length : 0;
            int dumped = 0;
            for (int i = 0; i < n; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string nm = SafeName(t);
                if (nm == null) continue;
                if (!_dumpedBones && dumped < 64) { Log.LogInfo($"[hands] bone: {nm}"); dumped++; }
                string low = nm.ToLowerInvariant();

                bool thumb = low.Contains("thumb");
                bool index = low.Contains("index");
                bool middle = low.Contains("middle");
                bool ring = low.Contains("ring");
                bool little = low.Contains("little") || low.Contains("pinky");
                if (!(thumb || index || middle || ring || little)) continue;

                // Curl the bendy segments; skip metacarpal (palm), tip, and the wrist/hand root.
                bool curlSeg = low.Contains("proximal") || low.Contains("intermediate")
                            || low.Contains("distal") || low.Contains("middle");
                // "middle" is both a finger AND a segment word; for the middle finger require an explicit segment.
                if (middle && !(low.Contains("proximal") || low.Contains("intermediate") || low.Contains("distal")))
                    curlSeg = false;
                if (!curlSeg) continue;
                if (low.Contains("metacarpal") || low.Contains("tip")) continue;

                h.Joints.Add(new FingerJoint { T = t, Bind = t.localRotation, IsThumb = thumb, IsIndex = index });
            }
            _dumpedBones = true;
            Log.LogInfo($"[hands] {(h.Mirrored ? "L(mirror)" : (h == _right ? "R" : "L"))}: {h.Joints.Count} curl joints from {n} bones.");
        }

        // A skinned-mesh FBX moved only by its root often frustum-culls (its bounds don't follow), so it loads
        // fine yet renders nothing. Force every renderer on, make skinned meshes recompute bounds each frame,
        // and log what we actually got (renderer count/type/bounds + the FBX's intrinsic scale) so an invisible
        // hand is diagnosable from the log rather than guesswork.
        private void PrepareRenderers(GameObject root, bool right, Vector3 intrinsicScale)
        {
            try
            {
                var rends = root.GetComponentsInChildren<Renderer>(true);
                int n = rends != null ? rends.Length : 0;
                Log.LogInfo($"[hands] {(right ? "R" : "L")}: intrinsicScale={intrinsicScale} renderers={n}");
                for (int i = 0; i < n; i++)
                {
                    var r = rends[i];
                    if (r == null) continue;
                    r.enabled = true;
                    r.forceRenderingOff = false;
                    var smr = r.TryCast<SkinnedMeshRenderer>();
                    if (smr != null) smr.updateWhenOffscreen = true; // bounds follow the bones every frame
                    var b = r.bounds;
                    Log.LogInfo($"[hands]   rend[{i}] {(smr != null ? "SMR" : "MR")} enabled={r.enabled} center={b.center} size={b.size}");
                }
            }
            catch (Exception e) { Log.LogWarning("[hands] PrepareRenderers: " + e.Message); }
        }

        // Load a hand prefab from the bundle by its addressable name (Config.HandPrefabRight/Left =
        // "HandRight"/"HandLeft"). The GENERIC LoadAsset<T>(string) marshals the name via
        // ManagedStringToIl2Cpp + il2cpp_runtime_invoke (the old, span-free path) — unlike the non-generic
        // LoadAsset(name,Type)/LoadAllAssets, which hit the broken GetPinnableReference injected path here.
        private GameObject LoadPrefab(bool right)
        {
            if (_bundle == null) return null;
            string name = right ? Config.HandPrefabRight : Config.HandPrefabLeft;
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                var prefab = _bundle.LoadAsset<GameObject>(name);
                if (prefab == null) { Log.LogWarning($"[hands] LoadAsset<GameObject>('{name}') returned null."); return null; }
                var inst = UnityEngine.Object.Instantiate(prefab);
                return inst != null ? inst.TryCast<GameObject>() : null;
            }
            catch (Exception e) { Log.LogWarning($"[hands] LoadAsset '{name}': " + e.Message); return null; }
        }

        // Minimal proxy: a flat palm box + an offset thumb box, so orientation is readable without art.
        private static GameObject BuildPrimitive()
        {
            var palm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            palm.name = "ProxyPalm";
            palm.transform.localScale = new Vector3(0.085f, 0.022f, 0.10f);
            Tint(palm, new Color(0.55f, 0.45f, 0.40f));

            var thumb = GameObject.CreatePrimitive(PrimitiveType.Cube);
            thumb.name = "ProxyThumb";
            thumb.transform.SetParent(palm.transform, false);
            thumb.transform.localScale = new Vector3(0.5f, 1.0f, 0.35f);
            thumb.transform.localPosition = new Vector3(0.6f, 0f, 0.25f);
            Tint(thumb, new Color(0.55f, 0.45f, 0.40f));
            return palm;
        }

        private static void Tint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh == null) return;
            var m = new Material(sh);
            try { m.color = c; } catch { }
            try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
            r.material = m;
        }

        private static void StripColliders(GameObject root)
        {
            var cols = root.GetComponentsInChildren<Collider>(true);
            if (cols == null) return;
            for (int i = 0; i < cols.Length; i++)
                if (cols[i] != null) UnityEngine.Object.Destroy(cols[i]);
        }

        // Read a name without the injected get_name property (which hits the broken span path in this build);
        // Object.GetName() is the span-free engine call.
        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.GetName() : null; } catch { return null; }
        }

        private static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            var t = go.transform;
            int n = t.childCount;
            for (int i = 0; i < n; i++) SetLayer(t.GetChild(i).gameObject, layer);
        }

        // A shader instance baked into an AssetBundle frequently won't render under the GAME's own URP
        // (variant/version mismatch) — the hand loads but draws nothing. The primitive proxies render fine
        // because Tint() uses the GAME's live URP/Lit shader via Shader.Find. So do the same here: build a
        // FRESH material on the game's live shader for every hand renderer (keeping the bundle's colour/texture
        // when present, else a skin tone). Logs the bundle's original shader so an invisible hand is diagnosable.
        private static void FixMaterials(GameObject root)
        {
            var urp = Shader.Find("Universal Render Pipeline/Lit");
            if (urp == null) urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp == null) urp = Shader.Find("Standard");
            if (urp == null) { Log.LogWarning("[hands] no live shader found for hand material."); return; }

            var skin = new Color(0.80f, 0.62f, 0.50f, 1f);
            var rends = root.GetComponentsInChildren<Renderer>(true);
            if (rends == null) return;
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null) continue;
                try
                {
                    var old = r.sharedMaterial;
                    string sh = (old != null && old.shader != null) ? old.shader.name : "<none>";
                    Color oc = (old != null && old.HasProperty("_BaseColor")) ? old.GetColor("_BaseColor")
                             : (old != null && old.HasProperty("_Color")) ? old.color : Color.clear;
                    Texture tex = (old != null && old.HasProperty("_BaseMap")) ? old.GetTexture("_BaseMap")
                                : (old != null && old.HasProperty("_MainTex")) ? old.mainTexture : null;
                    Log.LogInfo($"[hands]   mat[{i}] bundleShader='{sh}' color={oc} tex={(tex != null)}");

                    Color c = (oc.a > 0.01f) ? new Color(oc.r, oc.g, oc.b, 1f) : skin;
                    var m = new Material(urp);
                    try { m.color = c; } catch { }
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                    if (tex != null)
                    {
                        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
                    }
                    r.material = m; // live shader, guaranteed to render like the primitive proxies do
                }
                catch (Exception e) { Log.LogWarning($"[hands] mat[{i}]: " + e.Message); }
            }
        }

        private static void GetWorld(Transform origin, Posef pose, out Vector3 pos, out Quaternion rot)
        {
            // OpenXR (right-handed, -Z fwd) -> Unity, then into world via the seated rig origin.
            var lp = new Vector3(pose.Position.X, pose.Position.Y, -pose.Position.Z);
            var lr = new Quaternion(-pose.Orientation.X, -pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
            pos = origin.TransformPoint(lp);
            rot = origin.rotation * lr;
        }

        public void Dispose()
        {
            try { if (_right.Root != null) UnityEngine.Object.Destroy(_right.Root); } catch { }
            try { if (_left.Root != null) UnityEngine.Object.Destroy(_left.Root); } catch { }
            _right.Root = null; _left.Root = null;
            try { if (_bundle != null) _bundle.Unload(false); } catch { }
            _bundle = null;
            _built = false; _bundleTried = false;
        }
    }
}
