using System;
using BepInEx.Logging;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Renders the other player from the pose stream in <see cref="CoopP2P"/>: a head sphere with a small
    /// "nose" cube (so facing is visible) plus two hand spheres. Primitive on purpose — Phase 2 just needs
    /// the avatar visible enough to confirm sync; real hand meshes (reuse <see cref="HandVisuals"/>) come
    /// later. Built on layer 0 with an unlit material so both the VR eye cameras and the flat Main Camera
    /// draw it, and with colliders stripped so it never blocks the laser / physics.
    /// </summary>
    internal static class RemoteAvatar
    {
        private static ManualLogSource Log => Plugin.Logger;

        private static GameObject _root, _head, _lhand, _rhand;
        private static bool _built;
        private static string _shaderUsed = "?";   // which shader Shader.Find actually resolved for the avatar
        private static float _nextDiag;

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
                bool hands = CoopP2P.HasHands;
                if (_lhand.activeSelf != hands) _lhand.SetActive(hands);
                if (_rhand.activeSelf != hands) _rhand.SetActive(hands);
                if (hands)
                {
                    _lhand.transform.SetPositionAndRotation(CoopP2P.LPos, CoopP2P.LRot);
                    _rhand.transform.SetPositionAndRotation(CoopP2P.RPos, CoopP2P.RRot);
                }
                DiagTick();
            }
            catch (Exception e) { Log.LogWarning("[avatar] update: " + e.Message); }
        }

        private static void Ensure()
        {
            if (_built && _root != null) return;
            _root = new GameObject("CoopRemoteAvatar");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            _head = Ball(0.22f, new Color(0.30f, 0.70f, 1f));
            _head.transform.SetParent(_root.transform, false);
            // Nose: a small cube poking forward (+local Z) so head orientation is readable.
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            StripCollider(nose);
            nose.transform.SetParent(_head.transform, false);
            nose.transform.localScale = new Vector3(0.05f, 0.05f, 0.14f);
            nose.transform.localPosition = new Vector3(0f, 0f, 0.16f);
            Paint(nose, new Color(1f, 0.85f, 0.2f));
            nose.layer = 0;

            _lhand = Ball(0.07f, new Color(1f, 0.4f, 0.4f)); _lhand.transform.SetParent(_root.transform, false);
            _rhand = Ball(0.07f, new Color(0.4f, 1f, 0.5f)); _rhand.transform.SetParent(_root.transform, false);

            _built = true;
            int mask = 0; string camName = "none"; string scene = "?";
            try { var cam = Camera.main; if (cam != null) { mask = cam.cullingMask; camName = SafeName(cam); } } catch { }
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            bool layerRendered = (mask & (1 << _head.layer)) != 0;
            // One-shot render context: if avatarLayerRendered=False the local camera culls the avatar; if the
            // shader is NULL/unexpected it won't draw; scene name flags a "different level loaded" mismatch.
            Log.LogInfo($"[avatar] built (head+nose+2 hands) shader='{_shaderUsed}' layer={_head.layer} headScale={_head.transform.localScale.x:F2} mainCam='{camName}' cullMask=0x{mask:X} avatarLayerRendered={layerRendered} scene='{scene}'");
        }

        // Periodic render diagnostic: headVisible (Renderer.isVisible) tells us if ANY active camera actually
        // drew the head last frame — the key split between "not rendered" (cull/material/layer) and "rendered
        // but at a position you aren't looking at" (a coordinate problem). Throttled to 5s like the p2p stat.
        private static void DiagTick()
        {
            if (Time.unscaledTime < _nextDiag) return;
            _nextDiag = Time.unscaledTime + 5f;
            bool vis = false, en = false;
            try { var mr = _head.GetComponent<MeshRenderer>(); if (mr != null) { vis = mr.isVisible; en = mr.enabled; } } catch { }
            Log.LogInfo($"[avatar] active={_root.activeSelf} headPos={V(_head.transform.position)} headVisible={vis} rendEnabled={en} hands={CoopP2P.HasHands} shader='{_shaderUsed}'");
        }

        private static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o.GetName(); } catch { }
            try { return o.name; } catch { }
            return "?";
        }

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

        private static void Paint(GameObject go, Color c)
        {
            try
            {
                var mr = go.GetComponent<MeshRenderer>();
                if (mr == null) return;
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                _shaderUsed = sh != null ? SafeName(sh) : "NULL";
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
