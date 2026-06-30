using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Renders the other players from the per-peer pose stream in <see cref="CoopP2P"/>. Each remote player gets a
    /// pooled <see cref="Avatar"/> rig: a head (sphere + a "nose" cube so facing is readable), a torso capsule so
    /// they read as a person rather than a floating head, two hands, a floating persona name tag, and optional
    /// "uniform" props (gas mask / Castile flag). The hands reuse the SAME XR-hand meshes as the local player
    /// (<see cref="HandVisuals.SharedBundle"/>), posed at the remote grip poses with the same per-hand offset
    /// (<see cref="Config.HandOffsetPosR"/> etc.), so a VR teammate looks like real hands instead of dots. Falls
    /// back to coloured spheres when the bundle isn't loaded (e.g. on a flatscreen viewer, which never runs
    /// HandVisuals) — and upgrades to meshes if the bundle appears later. A flatscreen player has no tracked
    /// hands, so they show as head + torso only.
    ///
    /// Pooling: one <see cref="Avatar"/> per <see cref="CoopP2P.RemotePoses"/> key (the authoring SteamID; the F6
    /// self-test injects under <see cref="CoopP2P.SelfTestId"/> and renders like any peer). When a key disappears
    /// from the dictionary (the transport ages out stale poses) the matching avatar is hidden and reused later —
    /// so we never thrash GameObject create/destroy as players come and go.
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

        // A render-only prop sourced from the game's own assets (built by AvatarProps). Posed manually from the
        // head pose each frame — NOT parented to the head/body primitives, whose 0.22/0.34 localScale children
        // would inherit and shrink; posing directly keeps the offsets in real metres.
        private sealed class PropObj { public GameObject Go; public Vector3 Intrinsic; }

        // ---------------- per-peer avatar rig (pooled) ----------------

        // All the GameObjects/state that used to be static now live per remote player. One instance per peer key;
        // pooled in _avatars and hidden (not destroyed) when its peer drops out of the pose dictionary.
        private sealed class Avatar
        {
            public GameObject Root, Head, Body, Nose;
            public HandObj L, R;

            // Co-op "uniform": the gas mask rides the head (full look direction, yaw + pitch); the Castile flag
            // rides the upright torso (yaw only) so it hangs like a cape off the back. Both are driven entirely by
            // the already-synced head pose — no extra netcode — and behave the same for a VR teammate (VR head
            // pose) or a flatscreen teammate (window camera), since the sender streams whichever camera applies.
            public PropObj Mask, Flag;
            public float NextPropScan;

            // Floating persona name tag above the head.
            public GameObject NameRoot;
            public TextMeshPro NameText;
            public string ShownName;
        }

        private static readonly Dictionary<ulong, Avatar> _avatars = new Dictionary<ulong, Avatar>();
        private static readonly List<ulong> _scratchKeys = new List<ulong>();   // safe iteration for removal pass

        private static readonly string[] MaskTokens = { "gas", "mask" };
        private static readonly string[] FlagTokens = { "castile", "flag" };

        // Where name tags should face. VR sets the head pose each frame (SetViewer); flatscreen falls back to
        // Camera.main. Without this the tags would face the flat camera, which in VR isn't the player's eyes.
        private static Vector3 _viewerPos;
        private static bool _viewerValid;

        private static readonly Color HeadColor = new Color(0.30f, 0.70f, 1f);
        private static readonly Color BodyColor = new Color(0.18f, 0.42f, 0.70f);
        private static readonly Color LeftColor = new Color(1f, 0.45f, 0.45f);
        private static readonly Color RightColor = new Color(0.45f, 1f, 0.55f);

        public static void Update()
        {
            try
            {
                var poses = CoopP2P.RemotePoses;

                // Drive (or spawn) an avatar for every present peer. In PvP, render ONLY teammates — opponents are
                // isolated and appear solely as a map mirror, never as an in-world body. (IsTeammate is
                // false until the roster is known, so opponents — and, briefly, teammates — stay hidden until then.)
                bool pvp = Config.PvpActive;
                foreach (var kv in poses)
                {
                    if (pvp && !PvpTeams.IsTeammate(kv.Key)) continue;
                    Avatar a = GetOrCreate(kv.Key);
                    if (a == null) continue;
                    DriveAvatar(a, kv.Value);
                }

                // Hide any pooled avatar whose peer dropped out of the dictionary (kept for reuse, not destroyed) —
                // or, in PvP, is no longer a teammate (so an opponent we briefly rendered before the roster resolved
                // gets hidden the moment teams are known).
                _scratchKeys.Clear();
                foreach (var kv in _avatars)
                    if (!poses.ContainsKey(kv.Key) || (pvp && !PvpTeams.IsTeammate(kv.Key))) _scratchKeys.Add(kv.Key);
                for (int i = 0; i < _scratchKeys.Count; i++)
                {
                    var a = _avatars[_scratchKeys[i]];
                    if (a.Root != null && a.Root.activeSelf) a.Root.SetActive(false);
                }
            }
            catch (Exception e) { Log.LogWarning("[avatar] update: " + e.Message); }
        }

        private static void DriveAvatar(Avatar a, CoopP2P.RemotePose pose)
        {
            if (a.Root == null) return;
            if (!a.Root.activeSelf) { a.Root.SetActive(true); Log.LogInfo("[avatar] remote avatar shown"); }

            a.Head.transform.SetPositionAndRotation(pose.HeadPos, pose.HeadRot);
            PositionBody(a, pose.HeadPos, pose.HeadRot);
            UpdateNameTag(a, pose.HeadPos, pose.Name);

            bool hands = pose.HasHands;
            if (a.L.Anchor.activeSelf != hands) a.L.Anchor.SetActive(hands);
            if (a.R.Anchor.activeSelf != hands) a.R.Anchor.SetActive(hands);
            if (hands)
            {
                UpgradeIfBundleArrived(a);
                PlaceHand(a.L, false, pose.LPos, pose.LRot, pose);
                PlaceHand(a.R, true, pose.RPos, pose.RRot, pose);
            }

            TickProps(a, pose.HeadPos, pose.HeadRot);
        }

        // The torso hangs below the head and follows only head YAW (not pitch/roll), so it reads as an upright
        // body leaning at the console rather than tumbling with the head.
        private static void PositionBody(Avatar a, Vector3 headPos, Quaternion headRot)
        {
            if (a.Body == null) return;
            float yaw = headRot.eulerAngles.y;
            var yawRot = Quaternion.Euler(0f, yaw, 0f);
            a.Body.transform.SetPositionAndRotation(headPos + Vector3.down * 0.45f, yawRot);
        }

        // ---------------- uniform props (gas mask + Castile flag) ----------------

        // Resolve each enabled prop from the game's loaded assets on a light timer until found, then pose it from
        // the head pose every frame: the mask tracks the FULL head rotation, the flag tracks YAW ONLY.
        private static void TickProps(Avatar a, Vector3 headPos, Quaternion headRot)
        {
            bool needMask = Config.CoopGasMask && (a.Mask == null || a.Mask.Go == null);
            bool needFlag = Config.CoopFlag && (a.Flag == null || a.Flag.Go == null);
            if ((needMask || needFlag) && Time.unscaledTime >= a.NextPropScan)
            {
                a.NextPropScan = Time.unscaledTime + 2f;
                if (needMask) { var go = AvatarProps.Build("gasmask", MaskTokens, HeadColor); if (go != null) a.Mask = Attach(a, go); }
                if (needFlag) { var go = AvatarProps.BuildCape("castileflag", FlagTokens, BodyColor); if (go != null) a.Flag = Attach(a, go); }
            }

            // Gas mask: parented to nothing, posed at the head with a head-local offset → inherits yaw + pitch.
            PoseProp(a.Mask, Config.CoopGasMask, Config.CoopMaskScale,
                     headPos, headRot, Config.CoopMaskOffset, Config.CoopMaskEuler);

            // Castile flag: posed at the torso anchor with a yaw-only rotation → hangs upright like a cape.
            float yaw = headRot.eulerAngles.y;
            var yawRot = Quaternion.Euler(0f, yaw, 0f);
            PoseProp(a.Flag, Config.CoopFlag, Config.CoopFlagScale,
                     headPos + Vector3.down * 0.45f, yawRot, Config.CoopFlagOffset, Config.CoopFlagEuler);

            // Tuck the facing "nose" cube away while the mask covers the face (cosmetic).
            bool maskOn = a.Mask != null && a.Mask.Go != null && Config.CoopGasMask;
            if (a.Nose != null && a.Nose.activeSelf == maskOn) a.Nose.SetActive(!maskOn);
        }

        // Park a freshly built prop under the avatar root (unscaled) and remember its intrinsic scale so the
        // Config scale stays a clean uniform multiplier (matters for the texture-quad path, whose intrinsic
        // localScale carries the flag's aspect ratio).
        private static PropObj Attach(Avatar a, GameObject go)
        {
            go.transform.SetParent(a.Root.transform, false);
            SetLayer(go, 0);
            return new PropObj { Go = go, Intrinsic = go.transform.localScale };
        }

        private static void PoseProp(PropObj p, bool on, float scale, Vector3 anchorPos, Quaternion anchorRot, Vector3 off, Vector3 eul)
        {
            if (p == null || p.Go == null) return;
            if (p.Go.activeSelf != on) p.Go.SetActive(on);
            if (!on) return;
            var t = p.Go.transform;
            Vector3 bs = p.Intrinsic;
            t.localScale = new Vector3(bs.x * scale, bs.y * scale, bs.z * scale);
            t.SetPositionAndRotation(anchorPos + anchorRot * off, anchorRot * Quaternion.Euler(eul));
        }

        // VR passes the head pose so name tags face the player's eyes; flatscreen leaves it invalid and we fall
        // back to Camera.main. Called every frame from VrManager; applies to every pooled avatar's name tag.
        public static void SetViewer(Vector3 pos, bool valid) { _viewerPos = pos; _viewerValid = valid; }

        private static Vector3 ViewerPos(Avatar a)
        {
            if (_viewerValid) return _viewerPos;
            try { var c = Camera.main; if (c != null) return c.transform.position; } catch { }
            return (a != null && a.Head != null) ? a.Head.transform.position + Vector3.back : Vector3.back;
        }

        // Position + billboard the name tag above the head, and keep its text in sync with the peer's persona.
        private static void UpdateNameTag(Avatar a, Vector3 headPos, string name)
        {
            if (a.NameRoot == null) return;
            bool show = Config.CoopNameTags && !string.IsNullOrEmpty(name);
            if (a.NameRoot.activeSelf != show) a.NameRoot.SetActive(show);
            if (!show) return;

            if (a.ShownName != name)
            {
                a.ShownName = name;
                if (a.NameText != null) { a.NameText.text = name; a.NameText.ForceMeshUpdate(false, false); }
            }

            Vector3 tagPos = headPos + Vector3.up * Config.NameTagHeight;
            a.NameRoot.transform.SetPositionAndRotation(tagPos, VrText.FaceViewer(tagPos, ViewerPos(a)));
        }

        private static void PlaceHand(HandObj h, bool right, Vector3 pos, Quaternion rot, CoopP2P.RemotePose pose)
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
                if (Config.CoopFingerCurlSync && pose.HasCurl && h.Joints != null)
                {
                    float idx = right ? pose.RCurlIndex : pose.LCurlIndex;
                    float oth = right ? pose.RCurlOther : pose.LCurlOther;
                    FingerCurl.Apply(h.Joints, idx, oth);
                }
            }
        }

        // ---------------- build / pool ----------------

        // Pool lookup: reuse a hidden avatar if this peer was seen before, otherwise build a fresh rig.
        private static Avatar GetOrCreate(ulong id)
        {
            if (_avatars.TryGetValue(id, out var a) && a != null && a.Root != null) return a;
            a = Build();
            if (a != null) _avatars[id] = a;
            return a;
        }

        private static Avatar Build()
        {
            var a = new Avatar();
            a.Root = new GameObject("CoopRemoteAvatar");
            UnityEngine.Object.DontDestroyOnLoad(a.Root);
            a.Root.hideFlags = HideFlags.HideAndDontSave;

            a.Head = Ball(0.22f, HeadColor);
            a.Head.transform.SetParent(a.Root.transform, false);
            a.Nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(a.Nose);
            a.Nose.transform.SetParent(a.Head.transform, false);
            a.Nose.transform.localScale = new Vector3(0.05f, 0.05f, 0.14f);
            a.Nose.transform.localPosition = new Vector3(0f, 0f, 0.16f);
            Paint(a.Nose, new Color(1f, 0.85f, 0.2f));
            a.Nose.layer = 0;

            // Torso: a capsule (~0.6 m tall) standing under the head.
            a.Body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            StripCollider(a.Body);
            a.Body.name = "CoopBody";
            a.Body.transform.SetParent(a.Root.transform, false);
            a.Body.transform.localScale = new Vector3(0.34f, 0.30f, 0.34f);
            Paint(a.Body, BodyColor);
            a.Body.layer = 0;

            a.L = BuildHand(a, false);
            a.R = BuildHand(a, true);

            BuildNameTag(a);

            Log.LogInfo($"[avatar] built remote avatar (head + body + hands{(a.L.IsMesh ? " [mesh]" : " [sphere]")} + name tag)");
            return a;
        }

        // A small floating card above the head carrying the peer's Steam persona name. 3D TMP on layer 0, so it
        // draws in both the VR eyes and the flat camera; billboarded toward the viewer each frame.
        private static void BuildNameTag(Avatar a)
        {
            try
            {
                a.NameRoot = new GameObject("CoopNameTag");
                a.NameRoot.transform.SetParent(a.Root.transform, false);
                a.NameRoot.layer = 0;
                var bg = VrText.Panel(a.NameRoot.transform, new Vector2(0.36f, 0.10f),
                                      new Color(0.04f, 0.05f, 0.07f, 0.78f), 0);
                bg.transform.localPosition = Vector3.zero;
                a.NameText = VrText.Make(a.NameRoot.transform, "", new Vector2(0.33f, 0.075f),
                                        new Color(0.90f, 0.95f, 1f, 1f), 0, true);
                a.NameText.transform.localPosition = new Vector3(0f, 0f, -0.006f); // toward viewer => in front of bg
                a.NameRoot.SetActive(false);
            }
            catch (Exception e) { Log.LogWarning("[avatar] name tag build: " + e.Message); }
        }

        private static HandObj BuildHand(Avatar a, bool right)
        {
            var anchor = new GameObject(right ? "CoopHandR" : "CoopHandL");
            anchor.transform.SetParent(a.Root.transform, false);
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
        private static void UpgradeIfBundleArrived(Avatar a)
        {
            if (HandVisuals.SharedBundle == null) return;
            if (!a.L.IsMesh) Rebuild(a.L, false);
            if (!a.R.IsMesh) Rebuild(a.R, true);
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
