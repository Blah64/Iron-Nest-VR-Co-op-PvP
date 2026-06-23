using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// A floating "scope" magnifier over the tactical map. While a controller's aim ray lands on the
    /// world-space MAP CANVAS — the surface the bearing/range lines + hover grid are drawn on
    /// (<c>MapMarkerPlacer.mapRect</c>, or the grid highlighter's canvas) — a dedicated ORTHOGRAPHIC camera
    /// hovers directly above the pointed-at spot looking straight down the surface normal, framing a small
    /// region into a RenderTexture. That texture is shown on a billboarded quad floating just above the aim
    /// point and facing the player — a zoomed-in view of the map around where you're pointing (terrain + the
    /// grid/markers + any tokens, since the magnifier renders the same scene cull mask the eyes do, from a
    /// tight top-down ortho box).
    ///
    /// The map surface is a UI RectTransform with NO physics collider, so detection is a ray∩plane test
    /// bounded by the rect — not a raycast. Entirely render-only and VR-only: never touches game state or
    /// input, and a flatscreen player never runs it. Gated to "actually over the map", so the extra camera
    /// render only happens while the scope is up. The camera gets a UNIQUE depth so the game's Ultimate LOD
    /// System doesn't error-spam on a shared camera depth.
    /// </summary>
    internal sealed class MapScope
    {
        private static ManualLogSource Log => Plugin.Logger;

        // The map surface = a world-space UI RectTransform (the line/grid canvas). Re-resolved on a light
        // timer / instance-id change.
        private RectTransform _rect;
        private int _rectIid = -1;
        private float _nextScan;

        // Magnifier camera + its target.
        private GameObject _camGo;
        private Camera _cam;
        private RenderTexture _rt;

        // Billboard panel that displays the RT.
        private GameObject _panelGo;
        private Material _panelMat;

        private Vector3 _smHit;   // smoothed framed centre (reduces hand jitter)
        private bool _hasSm;
        private bool _visible;
        private bool _everShown;

        public void Tick(VrInput input, CameraRig rig, bool active)
        {
            try
            {
                if (!Config.MapScopeEnabled || !active) { Hide(); return; }
                var origin = rig.OriginTransform;
                if (origin == null) { Hide(); return; }

                ResolveSurface();
                if (_rect == null) { Hide(); Diag("no map surface (MapMarkerPlacer.mapRect / grid canvas) in scene"); return; }

                if (!AimMapHit(input, origin, out Vector3 hit, out Vector3 normal, out Vector3 inPlaneUp))
                { Hide(); Diag($"not over map (aimR={input.AimValid} aimL={input.AimValidL})"); return; }

                Diag($"OVER MAP at ({hit.x:0.00},{hit.y:0.00},{hit.z:0.00}) -> showing");
                EnsureObjects();

                // Keep the panel on its dedicated layer (so the magnifier can exclude it) and make sure the
                // eyes render that layer (else the panel would be invisible in the headset).
                int panelLayer = Mathf.Clamp(Config.MapScopePanelLayer, 0, 31);
                if (_panelGo.layer != panelLayer) _panelGo.layer = panelLayer;
                rig.IncludeLayerInEyes(panelLayer);

                // Smooth the framed point so the zoomed view doesn't shimmer with hand tremor.
                float tc = Config.MapScopeSmooth;
                if (!_hasSm || tc <= 0f) { _smHit = hit; _hasSm = true; }
                else
                {
                    float a = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.0001f, tc));
                    _smHit = Vector3.Lerp(_smHit, hit, a);
                }
                Vector3 framed = _smHit;

                // --- magnifier camera: ortho box just above the framed point, looking down the normal ---
                float standoff = Mathf.Max(0.05f, Config.MapScopeCamHeight);
                _camGo.transform.SetPositionAndRotation(
                    framed + normal * standoff,
                    Quaternion.LookRotation(-normal, inPlaneUp) * Quaternion.Euler(0f, 0f, Config.MapScopeRotationDeg));
                _cam.orthographicSize = Mathf.Max(0.01f, Config.MapScopeZoom);
                _cam.nearClipPlane = 0.01f;
                _cam.farClipPlane = standoff + Mathf.Max(0.02f, Config.MapScopeDepthBelow);
                // The REAL scene cull mask (the eyes'), NOT Camera.main's — the desktop-mirror blank zeroes
                // main's mask while in VR, which would make the magnifier render nothing. EXCLUDE the panel's
                // own layer so the camera never films its own output (the duplicate-map-inside-the-scope bug).
                int mask = rig.SceneCullMask;
                if (mask == 0) mask = ~0;
                _cam.cullingMask = mask & ~(1 << panelLayer);

                // --- billboard panel: lifted above the aim point and pulled toward the player, facing them ---
                rig.TryGetHeadPose(out var hp, out _);
                Vector3 toHead = hp - framed;
                Vector3 toHeadH = new Vector3(toHead.x, 0f, toHead.z);
                toHeadH = toHeadH.sqrMagnitude > 1e-5f ? toHeadH.normalized : -Vector3.forward;
                Vector3 panelPos = framed + Vector3.up * Config.MapScopeHeight + toHeadH * Config.MapScopeToward;
                Quaternion panelRot = Quaternion.LookRotation((panelPos - hp).normalized, Vector3.up);
                if (Config.MapScopeFlip) panelRot *= Quaternion.Euler(0f, 180f, 0f);
                _panelGo.transform.SetPositionAndRotation(panelPos, panelRot);
                float sz = Mathf.Max(0.05f, Config.MapScopeSize);
                _panelGo.transform.localScale = new Vector3(sz, sz, 1f);

                Show(true);
                if (!_everShown) { _everShown = true; Log.LogInfo("[scope] map magnifier shown (aiming at the map)."); }
            }
            catch (Exception e)
            {
                Log.LogWarning("[scope] " + e.Message);
                Hide();
            }
        }

        public void Dispose()
        {
            try { if (_panelGo != null) UnityEngine.Object.Destroy(_panelGo); } catch { }
            try { if (_camGo != null) UnityEngine.Object.Destroy(_camGo); } catch { }
            try { if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); } } catch { }
            _panelGo = null; _camGo = null; _cam = null; _rt = null; _panelMat = null;
            _rect = null; _rectIid = -1; _visible = false; _hasSm = false;
        }

        // ---------------- map surface resolution ----------------

        // The map surface is the world-space canvas the lines + grid are drawn on. Prefer the line placer's
        // mapRect; fall back to the hover-grid highlighter's canvas; last resort, a MapPiece3D token board.
        private void ResolveSurface()
        {
            if (_rect != null && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1.5f;
            try
            {
                RectTransform rect = null; string src = null;

                var placers = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapMarkerPlacer>(), FindObjectsSortMode.None);
                if (placers != null && placers.Length > 0)
                {
                    var p = placers[0].TryCast<MapMarkerPlacer>();
                    if (p != null) { try { rect = p.mapRect; } catch { } if (rect != null) src = "MapMarkerPlacer.mapRect"; }
                }
                if (rect == null)
                {
                    var grids = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<GridSquareHighlighterWithSubsector>(), FindObjectsSortMode.None);
                    if (grids != null && grids.Length > 0)
                    {
                        var g = grids[0].TryCast<GridSquareHighlighterWithSubsector>();
                        if (g != null)
                        {
                            try { rect = g.gridParent; } catch { }
                            if (rect == null) { try { var c = g.worldSpaceCanvas; if (c != null) rect = c.transform.TryCast<RectTransform>(); } catch { } }
                            if (rect != null) src = "GridSquareHighlighter";
                        }
                    }
                }
                if (rect == null)
                {
                    var maps = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapPiece3D>(), FindObjectsSortMode.None);
                    if (maps != null && maps.Length > 0)
                    {
                        var m = maps[0].TryCast<MapPiece3D>();
                        if (m != null) { rect = m.transform.TryCast<RectTransform>(); if (rect != null) src = "MapPiece3D"; }
                    }
                }

                int iid = rect != null ? rect.GetInstanceID() : -1;
                if (iid != _rectIid)
                {
                    _rectIid = iid; _rect = rect;
                    if (rect != null) Log.LogInfo($"[scope] map surface = {src} '{rect.GetName()}' size={rect.rect.size}.");
                }
            }
            catch (Exception e) { Log.LogWarning("[scope] resolve: " + e.Message); _rect = null; }
        }

        // ---------------- aim → map hit ----------------

        // Where (if anywhere) a controller is pointing at the map surface. Tries the RIGHT controller first
        // (the usual pointer), then the LEFT (so it still works when the right hand holds the HUD clipboard).
        // Intersects the aim ray with the canvas plane and confirms the hit is inside the rect — the same
        // surface the lines/grid live on, so it fires exactly when you're over the drawable map.
        private bool AimMapHit(VrInput input, Transform origin, out Vector3 hit, out Vector3 normal, out Vector3 inPlaneUp)
        {
            hit = Vector3.zero; normal = Vector3.up; inPlaneUp = Vector3.forward;
            if (_rect == null) return false;
            return TryRectRay(input.AimValid, input.AimPose, origin, out hit, out normal, out inPlaneUp)
                || TryRectRay(input.AimValidL, input.AimPoseL, origin, out hit, out normal, out inPlaneUp);
        }

        private bool TryRectRay(bool valid, Posef pose, Transform origin, out Vector3 hit, out Vector3 normal, out Vector3 inPlaneUp)
        {
            hit = Vector3.zero; normal = Vector3.up; inPlaneUp = Vector3.forward;
            if (!valid) return false;
            var lp = new Vector3(pose.Position.X, pose.Position.Y, -pose.Position.Z);
            var lr = new Quaternion(-pose.Orientation.X, -pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
            Vector3 ro = origin.TransformPoint(lp);
            Vector3 rd = (origin.rotation * lr) * Vector3.forward;

            Vector3 n = _rect.forward;                  // canvas normal (its local +Z)
            if (n.sqrMagnitude < 1e-6f) return false;
            n.Normalize();
            Rect r = _rect.rect;
            Vector3 center = _rect.TransformPoint(new Vector3(r.center.x, r.center.y, 0f));

            float denom = Vector3.Dot(rd, n);
            if (Mathf.Abs(denom) < 1e-5f) return false;  // ray parallel to the canvas
            float t = Vector3.Dot(center - ro, n) / denom;
            if (t <= 0f || t > Config.LaserMaxDistance + 2f) return false;
            Vector3 p = ro + rd * t;

            // Inside the rect? Compare in the canvas's own local space against its rect bounds.
            Vector3 local = _rect.InverseTransformPoint(p);
            float pad = Config.MapScopeEdgePad;
            if (local.x < r.xMin - pad || local.x > r.xMax + pad || local.y < r.yMin - pad || local.y > r.yMax + pad)
                return false;

            hit = p;
            // Orient the normal toward the viewer side (the controller) so the camera sits ABOVE the map.
            normal = Vector3.Dot(n, ro - p) >= 0f ? n : -n;
            inPlaneUp = _rect.up.sqrMagnitude > 1e-6f ? _rect.up.normalized : Vector3.up;
            return true;
        }

        private float _diagNext;
        private void Diag(string s)
        {
            if (!Config.LogInteractGeometry || Time.unscaledTime < _diagNext) return;
            _diagNext = Time.unscaledTime + 1.5f;
            Log.LogInfo("[scope] " + s);
        }

        // ---------------- objects ----------------

        private void EnsureObjects()
        {
            if (_camGo == null)
            {
                int res = Mathf.Clamp(Config.MapScopeResolution, 128, 1024);
                var desc = new RenderTextureDescriptor(res, res, RenderTextureFormat.ARGB32, 16) { msaaSamples = 1 };
                _rt = new RenderTexture(desc);
                _rt.Create();

                _camGo = new GameObject("IronNestVR_ScopeCam");
                UnityEngine.Object.DontDestroyOnLoad(_camGo);
                _camGo.hideFlags = HideFlags.HideAndDontSave;
                _cam = _camGo.AddComponent<Camera>();
                _cam.orthographic = true;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                _cam.allowHDR = false;
                _cam.allowMSAA = false;
                // Unique depth so the Ultimate LOD System doesn't error-spam (pointer cams are -100/-101,
                // eye cams ~ main.depth-10/-11). Fractional ⇒ won't collide with a game camera's integer depth.
                _cam.depth = -97.5f;
                _cam.targetTexture = _rt;
                _cam.enabled = false;   // rendered (auto, to its RT) only while the scope is up
            }
            if (_panelGo == null)
            {
                _panelGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _panelGo.name = "IronNestVR_ScopePanel";
                UnityEngine.Object.DontDestroyOnLoad(_panelGo);
                _panelGo.hideFlags = HideFlags.HideAndDontSave;
                _panelGo.layer = 0;   // a layer the eye cameras draw (they copy Main Camera's mask = includes 0)
                var mc = _panelGo.GetComponent<MeshCollider>();
                if (mc != null) UnityEngine.Object.Destroy(mc);   // the scope isn't a laser target

                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Texture");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                _panelMat = new Material(sh);
                try { if (_panelMat.HasProperty("_Cull")) _panelMat.SetFloat("_Cull", 0f); } catch { } // double-sided
                try { _panelMat.mainTexture = _rt; } catch { }
                try { if (_panelMat.HasProperty("_BaseMap")) _panelMat.SetTexture("_BaseMap", _rt); } catch { }
                try { _panelMat.color = Color.white; } catch { }
                try { if (_panelMat.HasProperty("_BaseColor")) _panelMat.SetColor("_BaseColor", Color.white); } catch { }

                var mr = _panelGo.GetComponent<MeshRenderer>();
                if (mr != null) mr.material = _panelMat;
                _panelGo.SetActive(false);
            }
        }

        private void Show(bool on)
        {
            if (_visible == on) return;
            _visible = on;
            if (_panelGo != null && _panelGo.activeSelf != on) _panelGo.SetActive(on);
            if (_cam != null && _cam.enabled != on) _cam.enabled = on;
        }

        private void Hide() { _hasSm = false; Show(false); }
    }
}
