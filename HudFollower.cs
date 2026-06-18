using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Makes the first-person HUD (held clipboard, compass/watch, floating tooltips) lag-follow the VR
    /// head instead of staying pinned to the flat FPS camera's orientation. The whole HUD layer is
    /// parented under <c>Main Camera</c> (e.g. "Main Camera > Static notepad Parent > …"), so in VR it
    /// inherits the flat look rotation, not the head. We re-parent Main Camera's (non-camera) children
    /// under an anchor we drive toward the VR head with exponential lag, plus a forward push so the
    /// clipboard sits at a readable/reachable distance. Each child keeps its local (animated) transform.
    /// </summary>
    internal sealed class HudFollower
    {
        private static ManualLogSource Log => Plugin.Logger;

        private GameObject _anchorGo;
        private Transform _anchor;
        private Transform _mainCamT;
        private readonly List<Transform> _moved = new List<Transform>();
        private bool _attached;
        private float _nextFind;

        public void Tick(CameraRig rig, bool active)
        {
            try
            {
                if (!Config.HudFollowEnabled || !active)
                {
                    if (_attached) Detach();
                    return;
                }

                if (!_attached) TryAttach();
                if (!_attached || _anchor == null) return;
                if (!rig.TryGetHeadPose(out var hp, out var hr)) return;

                // Follow head POSITION + YAW only (no pitch/roll) so the HUD doesn't tilt/roll with the
                // head — it stays upright in front of you and translates as you move.
                Quaternion yaw = Quaternion.Euler(0f, hr.eulerAngles.y, 0f);
                Vector3 target = hp + yaw * (Vector3.forward * Config.HudPushForward + Vector3.up * Config.HudPushUp);

                float dt = Time.unscaledDeltaTime;
                float tp = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, Config.HudFollowPosLag));
                float tr = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, Config.HudFollowRotLag));
                _anchor.position = Vector3.Lerp(_anchor.position, target, tp);
                _anchor.rotation = Quaternion.Slerp(_anchor.rotation, yaw, tr);
            }
            catch (Exception e)
            {
                Log.LogWarning("[hud] " + e.Message);
                _moved.Clear(); _attached = false; _mainCamT = null; // destroyed on scene change -> re-find
            }
        }

        private void TryAttach()
        {
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;

            var cam = Camera.main;
            if (cam == null) return;
            _mainCamT = cam.transform;

            EnsureAnchor();
            // Snap the anchor onto Main Camera so re-parenting causes no visible jump.
            _anchor.SetPositionAndRotation(_mainCamT.position, _mainCamT.rotation);

            // Collect children first (re-parenting mutates the child list mid-iteration).
            int n = _mainCamT.childCount;
            var kids = new List<Transform>(n);
            for (int i = 0; i < n; i++) kids.Add(_mainCamT.GetChild(i));

            _moved.Clear();
            foreach (var k in kids)
            {
                if (k == null || k == _anchor) continue;
                var nm = k.name;
                if (nm == null || nm.StartsWith("IronNestVR_")) continue;
                if (!IsHud(nm)) continue;                         // only HUD panels, not lights/cups/etc.
                if (k.GetComponent<Camera>() != null) continue;   // leave sub-cameras with the FPS camera
                k.SetParent(_anchor, false); // keep local (animated) transform relative to the new anchor
                _moved.Add(k);
                Log.LogInfo($"[hud] following: {nm}");
            }

            if (_moved.Count > 0)
            {
                _attached = true;
                Log.LogInfo($"[hud] {_moved.Count} HUD object(s) now lag-follow the VR head (push {Config.HudPushForward:0.##}m).");
            }
        }

        // Only treat known HUD panels as followers — the watch/compass. The held clipboard/notepad is
        // deliberately EXCLUDED: reparenting it corrupted the tutorial "move note onto clipboard"
        // consolidation (notes vanished). The clipboard will instead get a grab-to-place system.
        private static readonly string[] HudNames = { "watch", "compass", "gauge" };
        private static bool IsHud(string name)
        {
            var n = name.ToLowerInvariant();
            for (int i = 0; i < HudNames.Length; i++) if (n.Contains(HudNames[i])) return true;
            return false;
        }

        private void EnsureAnchor()
        {
            if (_anchorGo != null && _anchor != null) return;
            _anchorGo = new GameObject("IronNestVR_HudAnchor");
            _anchorGo.hideFlags = HideFlags.HideAndDontSave;
            _anchor = _anchorGo.transform;
        }

        private void Detach()
        {
            try
            {
                if (_mainCamT != null)
                    foreach (var k in _moved)
                        if (k != null) k.SetParent(_mainCamT, false);
            }
            catch { }
            _moved.Clear();
            _attached = false;
        }

        public void Dispose()
        {
            Detach();
            if (_anchorGo != null) UnityEngine.Object.Destroy(_anchorGo);
            _anchorGo = null; _anchor = null; _mainCamT = null;
        }
    }
}
