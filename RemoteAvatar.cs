using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Renders the other player from the pose stream in <see cref="CoopP2P"/>: a head (sphere + a "nose" cube
    /// so facing is readable), a torso capsule so they read as a person rather than a floating head, and two
    /// hands. The hands reuse the SAME XR-hand meshes as the local player (<see cref="HandVisuals.SharedBundle"/>),
    /// posed at the remote grip poses with the same per-hand offset (<see cref="Config.HandOffsetPosR"/> etc.),
    /// so a VR teammate looks like real hands instead of dots. Falls back to coloured spheres when the bundle
    /// isn't loaded (e.g. on a flatscreen viewer, which never runs HandVisuals) — and upgrades to meshes if the
    /// bundle appears later. A flatscreen player has no tracked hands, so they show as head + torso only.
    ///
    /// Built on layer 0 with game-URP materials and colliders stripped, so both the VR eye cameras and the flat
    /// Main Camera draw it and it never blocks the laser / physics.
    /// </summary>
    internal static class RemoteAvatar
    {
        private static ManualLogSource Log => Plugin.Logger;

        private sealed class HandObj
        {
            public GameObject Anchor;     // set to the grip pose
            public Transform Model;       // offset/scale live here (child of Anchor)
            public Vector3 Intrinsic;     // model localScale as built
            public bool IsMesh;           // mesh vs primitive sphere (so we can upgrade later)
            public List<FingerCurl.Joint> Joints;  // finger bones for streamed curl (mesh hands only)
        }

        private static GameObject _root, _head, _body;
        private static HandObj _l, _r;
        private static bool _built;

        // Floating persona name tag above the head.
        private static GameObject _nameRoot;
        private static TextMeshPro _nameText;
        private static string _shownName;

        // Where the name tag should face. VR sets the head pose each frame (SetViewer); flatscreen falls back
        // to Camera.main. Without this the tag would face the flat camera, which in VR isn't the player's eyes.
        private static Vector3 _viewerPos;
        private static bool _viewerValid;

        private static readonly Color HeadColor = new Color(0.30f, 0.70f, 1f);
        private static readonly Color BodyColor = new Color(0.18f, 0.42f, 0.70f);
        private static readonly Color LeftColor = new Color(1f, 0.45f, 0.45f);
        private static readonly Color RightColor = new Color(0.45f, 1f, 0.55f);

        public static void Update()
        {
            if (!CoopP2P.RemoteValid)
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                return;
            }
            try
            {
                Ensure();
                if (!_root.activeSelf) { _root.SetActive(true); Log.LogInfo("[avatar] remote avatar shown"); }

                _head.transform.SetPositionAndRotation(CoopP2P.HeadPos, CoopP2P.HeadRot);
                PositionBody(CoopP2P.HeadPos, CoopP2P.HeadRot);
                UpdateNameTag(CoopP2P.HeadPos);

                bool hands = CoopP2P.HasHands;
                if (_l.Anchor.activeSelf != hands) _l.Anchor.SetActive(hands);
                if (_r.Anchor.activeSelf != hands) _r.Anchor.SetActive(hands);
                if (hands)
                {
                    UpgradeIfBundleArrived();
                    PlaceHand(_l, false, CoopP2P.LPos, CoopP2P.LRot);
                    PlaceHand(_r, true, CoopP2P.RPos, CoopP2P.RRot);
                }
            }
            catch (Exception e) { Log.LogWarning("[avatar] update: " + e.Message); }
        }

        // The torso hangs below the head and follows only head YAW (not pitch/roll), so it reads as an upright
        // body leaning at the console rather than tumbling with the head.
        private static void PositionBody(Vector3 headPos, Quaternion headRot)
        {
            if (_body == null) return;
            float yaw = headRot.eulerAngles.y;
            var yawRot = Quaternion.Euler(0f, yaw, 0f);
            _body.transform.SetPositionAndRotation(headPos + Vector3.down * 0.45f, yawRot);
        }

        // VR passes the head pose so the name tag faces the player's eyes; flatscreen leaves it invalid and we
        // fall back to Camera.main. Called every frame from VrManager.
        public static void SetViewer(Vector3 pos, bool valid) { _viewerPos = pos; _viewerValid = valid; }

        private static Vector3 ViewerPos()
        {
            if (_viewerValid) return _viewerPos;
            try { var c = Camera.main; if (c != null) return c.transform.position; } catch { }
            return (_head != null) ? _head.transform.position + Vector3.back : Vector3.back;
        }

        // Position + billboard the name tag above the head, and keep its text in sync with the peer's persona.
        private static void UpdateNameTag(Vector3 headPos)
        {
            if (_nameRoot == null) return;
            bool show = Config.CoopNameTags && !string.IsNullOrEmpty(CoopP2P.PeerName);
            if (_nameRoot.activeSelf != show) _nameRoot.SetActive(show);
            if (!show) return;

            if (_shownName != CoopP2P.PeerName)
            {
                _shownName = CoopP2P.PeerName;
                if (_nameText != null) { _nameText.text = _shownName; _nameText.ForceMeshUpdate(false, false); }
            }

            Vector3 tagPos = headPos + Vector3.up * Config.NameTagHeight;
            _nameRoot.transform.SetPositionAndRotation(tagPos, VrText.FaceViewer(tagPos, ViewerPos()));
        }

        private static void PlaceHand(HandObj h, bool right, Vector3 pos, Quaternion rot)
        {
            if (h == null || h.Anchor == null) return;
            h.Anchor.transform.SetPositionAndRotation(pos, rot);
            if (h.Model == null) return;
            // Apply the per-hand offset live (so the VR menu's hand calibration also lines up the remote hands).
            if (h.IsMesh)
            {
                float s = Config.HandScale;
                Vector3 bs = h.Intrinsic;
                h.Model.localScale = new Vector3(bs.x * s, bs.y * s, bs.z * s);
                h.Model.localPosition = right ? Config.HandOffsetPosR : Config.HandOffsetPosL;
                h.Model.localEulerAngles = right ? Config.HandOffsetEulR : Config.HandOffsetEulL;

                // Curl the fingers from the peer's streamed amounts (mesh hands aren't mirrored, so no sign flip).
                if (Config.CoopFingerCurlSync && CoopP2P.HasCurl && h.Joints != null)
                {
                    float idx = right ? CoopP2P.RCurlIndex : CoopP2P.LCurlIndex;
                    float oth = right ? CoopP2P.RCurlOther : CoopP2P.LCurlOther;
                    FingerCurl.Apply(h.Joints, idx, oth);
                }
            }
        }

        // ---------------- build ----------------

        private static void Ensure()
        {
            if (_built && _root != null) return;
            _root = new GameObject("CoopRemoteAvatar");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            _head = Ball(0.22f, HeadColor);
            _head.transform.SetParent(_root.transform, false);
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(nose);
            nose.transform.SetParent(_head.transform, false);
            nose.transform.localScale = new Vector3(0.05f, 0.05f, 0.14f);
            nose.transform.localPosition = new Vector3(0f, 0f, 0.16f);
            Paint(nose, new Color(1f, 0.85f, 0.2f));
            nose.layer = 0;

            // Torso: a capsule (~0.6 m tall) standing under the head.
            _body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            StripCollider(_body);
            _body.name = "CoopBody";
            _body.transform.SetParent(_root.transform, false);
            _body.transform.localScale = new Vector3(0.34f, 0.30f, 0.34f);
            Paint(_body, BodyColor);
            _body.layer = 0;

            _l = BuildHand(false);
            _r = BuildHand(true);

            BuildNameTag();

            _built = true;
            Log.LogInfo($"[avatar] built remote avatar (head + body + hands{(_l.IsMesh ? " [mesh]" : " [sphere]")} + name tag)");
        }

        // A small floating card above the head carrying the peer's Steam persona name. 3D TMP on layer 0, so it
        // draws in both the VR eyes and the flat camera; billboarded toward the viewer each frame.
        private static void BuildNameTag()
        {
            try
            {
                _nameRoot = new GameObject("CoopNameTag");
                _nameRoot.transform.SetParent(_root.transform, false);
                _nameRoot.layer = 0;
                var bg = VrText.Panel(_nameRoot.transform, new Vector2(0.36f, 0.10f),
                                      new Color(0.04f, 0.05f, 0.07f, 0.78f), 0);
                bg.transform.localPosition = Vector3.zero;
                _nameText = VrText.Make(_nameRoot.transform, "", new Vector2(0.33f, 0.075f),
                                        new Color(0.90f, 0.95f, 1f, 1f), 0, true);
                _nameText.transform.localPosition = new Vector3(0f, 0f, -0.006f); // toward viewer => in front of bg
                _nameRoot.SetActive(false);
            }
            catch (Exception e) { Log.LogWarning("[avatar] name tag build: " + e.Message); }
        }

        private static HandObj BuildHand(bool right)
        {
            var anchor = new GameObject(right ? "CoopHandR" : "CoopHandL");
            anchor.transform.SetParent(_root.transform, false);
            anchor.layer = 0;
            var h = new HandObj { Anchor = anchor };
            BuildHandModel(h, right);
            return h;
        }

        // Try the shared XR-hand mesh; fall back to a coloured sphere if the bundle isn't available.
        private static void BuildHandModel(HandObj h, bool right)
        {
            GameObject model = TryLoadHandMesh(right);
            if (model != null)
            {
                model.transform.SetParent(h.Anchor.transform, false);
                h.Intrinsic = model.transform.localScale;
                h.Model = model.transform;
                h.IsMesh = true;
                h.Joints = FingerCurl.Collect(model);   // finger bones for the streamed curl
            }
            else
            {
                var ball = Ball(0.07f, right ? RightColor : LeftColor);
                ball.transform.SetParent(h.Anchor.transform, false);
                h.Model = ball.transform;
                h.Intrinsic = ball.transform.localScale;
                h.IsMesh = false;
                h.Joints = null;
            }
        }

        private static GameObject TryLoadHandMesh(bool right)
        {
            try
            {
                var bundle = HandVisuals.SharedBundle;
                if (bundle == null) return null;
                string name = right ? Config.HandPrefabRight : Config.HandPrefabLeft;
                if (string.IsNullOrEmpty(name)) return null;
                var prefab = bundle.LoadAsset<GameObject>(name);   // generic = span-free path (see HandVisuals)
                if (prefab == null) return null;
                var inst = UnityEngine.Object.Instantiate(prefab).TryCast<GameObject>();
                if (inst == null) return null;
                StripColliders(inst);
                SetLayer(inst, 0);
                FixHandMaterials(inst, right ? RightColor : LeftColor);
                ShowRenderers(inst);
                return inst;
            }
            catch (Exception e) { Log.LogWarning("[avatar] hand mesh load: " + e.Message); return null; }
        }

        // If a hand was built as a sphere because the bundle wasn't ready yet, swap it for the real mesh once
        // the bundle has loaded (so a remote avatar that appeared before the local hands finished loading still
        // gets upgraded).
        private static void UpgradeIfBundleArrived()
        {
            if (HandVisuals.SharedBundle == null) return;
            if (!_l.IsMesh) Rebuild(_l, false);
            if (!_r.IsMesh) Rebuild(_r, true);
        }

        private static void Rebuild(HandObj h, bool right)
        {
            try { if (h.Model != null) UnityEngine.Object.Destroy(h.Model.gameObject); } catch { }
            h.Model = null;
            BuildHandModel(h, right);
            if (h.IsMesh) Log.LogInfo($"[avatar] upgraded {(right ? "right" : "left")} hand to mesh");
        }

        // ---------------- primitives / materials ----------------

        private static GameObject Ball(float dia, Color c)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            StripCollider(go);
            go.transform.localScale = Vector3.one * dia;
            go.layer = 0;
            Paint(go, c);
            return go;
        }

        private static void StripCollider(GameObject go)
        {
            try { var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col); } catch { }
        }

        private static void StripColliders(GameObject root)
        {
            try
            {
                var cols = root.GetComponentsInChildren(Il2CppType.Of<Collider>(), true);
                if (cols != null) for (int i = 0; i < cols.Length; i++) { var c = cols[i].TryCast<Collider>(); if (c != null) UnityEngine.Object.Destroy(c); }
            }
            catch { }
        }

        private static void SetLayer(GameObject go, int layer)
        {
            try
            {
                go.layer = layer;
                var ts = go.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
                if (ts != null) for (int i = 0; i < ts.Length; i++) { var t = ts[i].TryCast<Transform>(); if (t != null) t.gameObject.layer = layer; }
            }
            catch { }
        }

        // A bundle-baked shader often won't render under the game's live URP, so rebuild each renderer's
        // material on the game's URP/Lit shader (same lesson as HandVisuals.FixMaterials), tinted so the remote
        // hands stay distinguishable (L red / R green) and clearly "the other player".
        private static void FixHandMaterials(GameObject root, Color tint)
        {
            try
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Standard");
                if (sh == null) return;
                var rends = root.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                if (rends == null) return;
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var m = new Material(sh);
                    try { m.color = tint; } catch { }
                    try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint); } catch { }
                    r.material = m;
                }
            }
            catch (Exception e) { Log.LogWarning("[avatar] hand mat: " + e.Message); }
        }

        // Skinned meshes moved only by their root frustum-cull unless bounds follow the bones; force renderers on.
        private static void ShowRenderers(GameObject root)
        {
            try
            {
                var rends = root.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                if (rends == null) return;
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    r.enabled = true; r.forceRenderingOff = false;
                    var smr = r.TryCast<SkinnedMeshRenderer>();
                    if (smr != null) smr.updateWhenOffscreen = true;
                }
            }
            catch { }
        }

        private static void Paint(GameObject go, Color c)
        {
            try
            {
                var mr = go.GetComponent<MeshRenderer>();
                if (mr == null) return;
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                var m = new Material(sh);
                try { m.color = c; } catch { }
                try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
                try { if (m.HasProperty("_Color")) m.SetColor("_Color", c); } catch { }
                mr.material = m;
            }
            catch { }
        }
    }
}
