using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Drives the game's interaction pipeline from the right motion controller. We repoint EVERY
    /// interaction raycast camera down the controller — the cursor manager's, plus each control's own
    /// (<c>DialInteractable</c>, <c>LinearSliderInteractable</c>, <c>HoverTooltip</c> all carry their
    /// own <c>raycastCamera</c>). That makes hover highlight, click, dial/lever grab-and-turn AND the
    /// floating tooltip all follow the laser. The trigger synthesizes the left mouse button so clicks
    /// and drags use the game's own pipeline.
    ///
    /// Only engaged while in a mission (a <c>TurretController</c> exists) AND focused with a tracked
    /// controller; otherwise everything is restored so the desktop mouse/menus keep working.
    /// </summary>
    internal sealed class CockpitInteractor
    {
        private static ManualLogSource Log => Plugin.Logger;

        private DynamicCursorManager _mgr;
        private bool _engaged;
        private Camera _origCam;
        private float _origMaxDist;
        private bool _origSanitize;
        private DynamicCursorManager.PresentationMode _origMode;

        private GameObject _camGo;
        private Camera _cam;
        private GameObject _laserGo;
        private LineRenderer _laser;
        private bool _hoverColor;

        private bool _triggerWasHeld;
        private bool _interactWasHeld;
        private Interactable _pressTarget;
        private bool _inMission;
        private float _nextDiscover;
        private float _nextRepoint;
        private float _nextGeoLog;

        /// <param name="active">true only when the session is focused and input is ready.</param>
        /// <param name="suppressClick">true while the VR settings menu owns the trigger: keep the
        /// laser/hover alive but don't synthesize clicks/keys into the game.</param>
        public void Apply(VrInput input, CameraRig rig, float dt, bool active, bool suppressClick = false)
        {
            try
            {
                rig.UpdateOrigin();
                var origin = rig.OriginTransform;

                Discover(active);
                bool want = active && _inMission && _mgr != null && origin != null;

                if (!want)
                {
                    if (_engaged) Restore("not in interaction context");
                    ShowLaser(false);
                    return;
                }

                if (!_engaged) Engage();
                EnsureRigObjects();

                // Keep the cursor ray on our controller cam, and (throttled) every control's own
                // raycast camera too, so dials/levers/tooltips track the controller, not the flat camera.
                if (_mgr.raycastCamera != _cam) _mgr.raycastCamera = _cam;
                if (Time.unscaledTime >= _nextRepoint)
                {
                    _nextRepoint = Time.unscaledTime + 0.5f;
                    RepointInteractionCameras(_cam);
                }

                if (!input.AimValid)
                {
                    // Tracking lost this frame: stop pointing/clicking but stay engaged (don't thrash).
                    EndAllInput();
                    ShowLaser(false);
                    return;
                }

                Posef pose = input.AimPose;
                // OpenXR (right-handed, -Z fwd) -> Unity, then into world via the seated rig origin.
                var lp = new Vector3(pose.Position.X, pose.Position.Y, -pose.Position.Z);
                var lr = new Quaternion(-pose.Orientation.X, -pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
                Vector3 wp = origin.TransformPoint(lp);
                Quaternion wr = origin.rotation * lr;

                _camGo.transform.SetPositionAndRotation(wp, wr);

                UpdateLaser(wp, wr);

                if (suppressClick)
                {
                    // The VR settings menu has the trigger this frame: release anything held in the
                    // game and don't click/keypress, but keep pointing the laser at the panel.
                    EndAllInput();
                }
                else
                {
                    HandleClick(input);
                    HandleInteract(input);
                }

                GeometryLog(wp, wr);
            }
            catch (Exception e)
            {
                // A scene change can invalidate the cached manager; reset and rediscover next frame.
                Log.LogWarning("[interact] " + e.Message);
                _mgr = null; _engaged = false; _triggerWasHeld = false; _inMission = false;
            }
        }

        // Throttled scene discovery. Only caches the cursor manager while a TurretController exists
        // (in a mission) — so the MAIN MENU's cursor manager is never touched.
        private void Discover(bool active)
        {
            if (!active) { _inMission = false; return; }
            if (_mgr != null && Time.unscaledTime < _nextDiscover) return;
            if (Time.unscaledTime < _nextDiscover && !_inMission) return;
            _nextDiscover = Time.unscaledTime + 0.5f;

            _inMission = FirstOf(Il2CppType.Of<TurretController>()) != null;
            if (!_inMission) { _mgr = null; return; }
            if (_mgr == null)
            {
                var m = FirstOf(Il2CppType.Of<DynamicCursorManager>());
                _mgr = m != null ? m.TryCast<DynamicCursorManager>() : null;
                if (_mgr != null) Log.LogInfo("[interact] found DynamicCursorManager (in mission)");
            }
        }

        private void Engage()
        {
            _origCam = _mgr.raycastCamera;
            _origMaxDist = _mgr.maxRayDistance;
            _origSanitize = _mgr.sanitizeClickStateEachFrame;
            _origMode = _mgr.CurrentMode;

            _mgr.sanitizeClickStateEachFrame = false;
            _mgr.emitPrimaryClickEvents = true;
            try { _mgr.SwitchToFPSLocked(); } catch { }
            var vc = _mgr.virtualCursor;
            if (vc != null) vc.lockToCenterWhenFPSLocked = true;

            EnsureRigObjects();
            _mgr.raycastCamera = _cam;
            _mgr.maxRayDistance = Mathf.Max(_origMaxDist, Config.LaserMaxDistance);
            RepointInteractionCameras(_cam);
            _engaged = true;
            Log.LogInfo($"[interact] ENGAGED (origCam={(_origCam != null ? _origCam.name : "null")}, origMode={_origMode}).");
        }

        // Point every control's own raycast camera at the controller. Dials, levers and the hover
        // tooltip each carry their own raycastCamera; without this they keep using the flat camera and
        // dial twist / tooltips don't follow the laser.
        private void RepointInteractionCameras(Camera cam)
        {
            SetRaycastCameraOn(Il2CppType.Of<DialInteractable>(), cam);
            SetRaycastCameraOn(Il2CppType.Of<LinearSliderInteractable>(), cam);
            SetRaycastCameraOn(Il2CppType.Of<HoverTooltip>(), cam);
        }

        private static void SetRaycastCameraOn(Il2CppSystem.Type t, Camera cam)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<DialInteractable>();
                if (d != null) { d.raycastCamera = cam; continue; }
                var s = arr[i].TryCast<LinearSliderInteractable>();
                if (s != null) { s.raycastCamera = cam; continue; }
                var h = arr[i].TryCast<HoverTooltip>();
                if (h != null) { h.raycastCamera = cam; }
            }
        }

        private void HandleClick(VrInput input)
        {
            bool held = input.TriggerHeld;
            if (InputSynth.Supported)
            {
                if (held) InputSynth.SetMouseLeft(true);
                else if (_triggerWasHeld) InputSynth.SetMouseLeft(false);
                if (held && !_triggerWasHeld)
                {
                    input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
                    var h = _mgr.CurrentHover;
                    Log.LogInfo($"[interact] trigger DOWN over '{(h != null ? h.name : "nothing")}'");
                }
            }
            else
            {
                if (held && !_triggerWasHeld) BeginPress(input);
                else if (!held && _triggerWasHeld) ReleasePress();
            }
            _triggerWasHeld = held;
        }

        // Right A -> 'E' key (synthesized) for items that interact via [E] instead of a click.
        private void HandleInteract(VrInput input)
        {
            bool held = input.InteractHeld;
            if (held && !_interactWasHeld)
            {
                Win32Input.SendKey(Win32Input.VK_E, true);
                input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
                Log.LogInfo("[interact] [E] interact pressed (OS key)");
            }
            else if (!held && _interactWasHeld)
            {
                Win32Input.SendKey(Win32Input.VK_E, false);
            }
            _interactWasHeld = held;
        }

        private void EndAllInput()
        {
            if (_triggerWasHeld) InputSynth.SetMouseLeft(false);
            if (_interactWasHeld) Win32Input.SendKey(Win32Input.VK_E, false);
            if (_pressTarget != null) ReleasePress();
            _triggerWasHeld = false;
            _interactWasHeld = false;
        }

        // Fallback click path (only used if input synthesis is unavailable).
        private void BeginPress(VrInput input)
        {
            var hover = _mgr.CurrentHover;
            if (hover == null) return;
            _pressTarget = hover;
            var down = _mgr.OnPrimaryClickDown;
            if (down != null) down.Invoke(hover);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
        }

        private void ReleasePress()
        {
            if (_pressTarget != null && _mgr != null)
            {
                var up = _mgr.OnPrimaryClickUp;
                if (up != null) { try { up.Invoke(_pressTarget); } catch { } }
            }
            _pressTarget = null;
            _triggerWasHeld = false;
        }

        private void Restore(string reason)
        {
            try
            {
                EndAllInput();
                var restoreCam = _origCam != null ? _origCam : Camera.main;
                if (_mgr != null)
                {
                    _mgr.raycastCamera = _origCam;
                    _mgr.maxRayDistance = _origMaxDist;
                    _mgr.sanitizeClickStateEachFrame = _origSanitize;
                    try { _mgr.SwitchToPresentationMode(_origMode); } catch { }
                }
                if (restoreCam != null) RepointInteractionCameras(restoreCam); // give desktop control back
            }
            catch { }
            _triggerWasHeld = false;
            _engaged = false;
            Log.LogInfo($"[interact] restored cursor manager ({reason}).");
        }

        private void EnsureRigObjects()
        {
            if (_camGo == null)
            {
                _camGo = new GameObject("IronNestVR_PointerCam");
                UnityEngine.Object.DontDestroyOnLoad(_camGo);
                _camGo.hideFlags = HideFlags.HideAndDontSave;
                _cam = _camGo.AddComponent<Camera>();
                _cam.clearFlags = CameraClearFlags.Nothing; // draws nothing; exists only to cast the ray
                _cam.cullingMask = 0;
                _cam.depth = -100f;
                _cam.nearClipPlane = 0.01f;
                _cam.fieldOfView = 60f;
            }
            if (_laserGo == null)
            {
                _laserGo = new GameObject("IronNestVR_Laser");
                UnityEngine.Object.DontDestroyOnLoad(_laserGo);
                _laserGo.hideFlags = HideFlags.HideAndDontSave;
                _laser = _laserGo.AddComponent<LineRenderer>();
                _laser.useWorldSpace = true;
                _laser.positionCount = 2;
                _laser.startWidth = Config.LaserWidth;
                _laser.endWidth = Config.LaserWidth;
                _laser.numCapVertices = 0;
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh != null) _laser.material = new Material(sh);
                _hoverColor = false;
                ApplyLaserColor(false);
            }
        }

        // Laser is cyan normally, GREEN when the game reports something interactable under it — so the
        // player knows when they're actually aimed at a control (catches any VR aim/parallax offset).
        // Set BOTH the LineRenderer vertex colors AND the material's colour properties: URP/Unlit
        // ignores vertex colours, so the gradient alone wouldn't change anything.
        private void ApplyLaserColor(bool hovering)
        {
            if (_laser == null) return;
            Color c = hovering ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(0.2f, 0.9f, 1f, 1f);
            _laser.startColor = c;
            _laser.endColor = new Color(c.r, c.g, c.b, 0.35f);
            var m = _laser.material;
            if (m != null)
            {
                try { m.color = c; } catch { }
                try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
                try { if (m.HasProperty("_Color")) m.SetColor("_Color", c); } catch { }
            }
        }

        // End the laser at the first surface it hits so it visibly lands on the control.
        private void UpdateLaser(Vector3 origin, Quaternion rot)
        {
            if (_laser == null) return;
            Vector3 dir = rot * Vector3.forward;
            float len = Config.LaserMaxDistance;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Ignore))
                len = hit.distance;
            _laser.SetPosition(0, origin);
            _laser.SetPosition(1, origin + dir * len);

            bool hovering = false;
            try { hovering = _mgr.CurrentHover != null; } catch { }
            if (hovering != _hoverColor) { _hoverColor = hovering; ApplyLaserColor(hovering); }
            ShowLaser(Config.ShowLaser); // live on/off from the VR settings menu
        }

        private void GeometryLog(Vector3 wp, Quaternion wr)
        {
            if (!Config.LogInteractGeometry || Time.unscaledTime < _nextGeoLog) return;
            _nextGeoLog = Time.unscaledTime + Config.InteractLogIntervalSec;
            Vector3 f = wr * Vector3.forward;
            string hover = "none";
            try { var h = _mgr.CurrentHover; if (h != null) hover = h.name; } catch { }
            Log.LogInfo($"[interact] ctrl=({wp.x:0.00},{wp.y:0.00},{wp.z:0.00}) fwd=({f.x:0.00},{f.y:0.00},{f.z:0.00}) hover={hover}");
        }

        private void ShowLaser(bool on)
        {
            if (_laserGo != null && _laserGo.activeSelf != on) _laserGo.SetActive(on);
        }

        public void Dispose()
        {
            if (_engaged) Restore("dispose");
            if (_laserGo != null) UnityEngine.Object.Destroy(_laserGo);
            if (_camGo != null) UnityEngine.Object.Destroy(_camGo);
            _laserGo = null; _camGo = null; _laser = null; _cam = null;
            _mgr = null; _engaged = false;
        }

        private static UnityEngine.Object FirstOf(Il2CppSystem.Type t)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            return (arr != null && arr.Length > 0) ? arr[0] : null;
        }
    }
}
