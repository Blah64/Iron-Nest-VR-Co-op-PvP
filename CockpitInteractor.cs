using System;
using System.Collections.Generic;
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
        private bool _forceLaser;   // VR settings menu open -> always show the laser, even outside a mission

        // LEFT pointer: a second ray-cast camera + laser down the LEFT controller, so the left hand can aim at
        // and grab manuals/dials/levers/switches too. A left-hand dial/lever native drag follows THIS camera —
        // we pin the held control's own raycastCamera to it each frame (the periodic repoint pins everything to
        // the right cam). The HandManipulator tells us which control the left hand holds.
        private GameObject _camGoL;
        private Camera _camL;
        private GameObject _laserGoL;
        private LineRenderer _laserL;
        private bool _hoverColorL;
        internal HandManipulator Manip;   // set by VrManager; queried for the left-held control
        internal GrabManager Grab;        // set by VrManager; queried to know which hand holds the HUD clipboard (left-active pointer)
        internal Camera LeftPointerCam => _camL;   // HandManipulator pins a left-held control to this on grab
        // The pointer cam currently acting as the game cursor (left when the right hand holds the HUD clipboard,
        // else right). HandManipulator hands a released dial's raycast camera back to this so hover/laser resume.
        internal Camera ActiveCursorCam => (Grab != null && Grab.HudClipboardHoldHand == 2 && _camL != null) ? _camL : _cam;

        private bool _triggerWasHeld;
        private bool _mapDeleteWasHeld;
        private Interactable _pressTarget;
        private bool _inMission;
        private float _nextDiscover;
        private float _nextRepoint;
        private float _nextGeoLog;

        // Tactical map: cached MapMarkerPlacers (line drawing + marker hover/delete) and the originals of
        // every camera/flag we override, keyed by GetInstanceID so disengage restores them exactly (a scene
        // reload makes new instances with fresh ids; old entries' refs go null and are skipped on restore).
        private readonly Dictionary<int, MapMarkerPlacer> _placers = new Dictionary<int, MapMarkerPlacer>();
        private readonly Dictionary<int, Camera> _origPlacerCam = new Dictionary<int, Camera>();
        private readonly Dictionary<int, bool> _origPlacerHover = new Dictionary<int, bool>();
        private readonly Dictionary<int, Canvas> _mapCanvases = new Dictionary<int, Canvas>();
        private readonly Dictionary<int, Camera> _origCanvasCam = new Dictionary<int, Camera>();
        private int _mapObjCount = -1;

        // Teleprinter (fire-mission line selector): its hit-test camera is the flat Main Camera out of the box,
        // so the hovered line is projected from the head gaze (stuck on a default/last line) instead of the
        // laser. Retarget hitTestCamera to our pointer cam, same as the map placer. Originals per instance id.
        private readonly Dictionary<int, TeleprinterLineRangeSelector3D> _teleprinters = new Dictionary<int, TeleprinterLineRangeSelector3D>();
        private readonly Dictionary<int, Camera> _origTeleCam = new Dictionary<int, Camera>();
        private int _teleCount = -1;

        /// <param name="active">true only when the session is focused and input is ready.</param>
        /// <param name="suppressClick">true while the VR settings menu owns the trigger: keep the
        /// laser/hover alive but don't synthesize clicks/keys into the game.</param>
        public void Apply(VrInput input, CameraRig rig, float dt, bool active, bool suppressClick = false, bool forceLaser = false)
        {
            try
            {
                _forceLaser = forceLaser;
                rig.UpdateOrigin();
                var origin = rig.OriginTransform;

                Discover(active);
                bool want = active && _inMission && _mgr != null && origin != null;

                if (!want)
                {
                    if (_engaged) Restore("not in interaction context");
                    // Keep the laser visible while the VR menu is open — even outside a mission — so the
                    // player can see where they're aiming at the panel. Drawn down the controller without
                    // engaging the game's cursor manager or interaction.
                    if (forceLaser && origin != null)
                    {
                        EnsureRigObjects();
                        if (input.AimValid)
                        {
                            Posef fp = input.AimPose;
                            var flp = new Vector3(fp.Position.X, fp.Position.Y, -fp.Position.Z);
                            var flr = new Quaternion(-fp.Orientation.X, -fp.Orientation.Y, fp.Orientation.Z, fp.Orientation.W);
                            UpdateLaser(origin.TransformPoint(flp), origin.rotation * flr, true);
                        }
                        else ShowLaser(false);
                        DriveLeftPointer(input, origin);
                    }
                    else { ShowLaser(false); ShowLaserL(false); }
                    return;
                }

                if (!_engaged) Engage();
                EnsureRigObjects();

                // Which controller drives the game cursor: normally the RIGHT hand, but when the RIGHT hand is
                // busy holding the HUD clipboard, the LEFT hand takes over so it can pick the pencil colours /
                // compass on the board (and work any other control). The pointing hand is the clicking hand.
                bool leftActive = Grab != null && Grab.HudClipboardHoldHand == 2 && _camL != null;
                Camera activeCam = leftActive ? _camL : _cam;

                // Keep the cursor ray on the ACTIVE controller cam, and (throttled) every control's own
                // raycast camera too, so dials/levers/tooltips track that controller, not the flat camera.
                if (_mgr.raycastCamera != activeCam) _mgr.raycastCamera = activeCam;
                if (Time.unscaledTime >= _nextRepoint)
                {
                    _nextRepoint = Time.unscaledTime + 0.5f;
                    RepointInteractionCameras(activeCam);
                    RepointMapCameras(activeCam);
                    RepointTeleprinters(activeCam);
                }

                // Menus flip the cursor manager to FreeMouse (cursor follows the OS mouse, off-centre),
                // which puts a constant angular offset on the UI ray. Hold it in FPSLocked + lock-to-
                // centre so the ray stays == controller forward. Re-asserted every frame because the
                // game flips it back when a menu/popup takes focus.
                if (Config.MenuForceCenter) ForceCenterCursor();

                // Position the RIGHT pointer cam + laser when tracked. It's the active cursor unless the left
                // hand has taken over; pass that so its laser only colours from the game hover when it's live.
                if (input.AimValid)
                {
                    Posef pose = input.AimPose;
                    // OpenXR (right-handed, -Z fwd) -> Unity, then into world via the seated rig origin.
                    var lp = new Vector3(pose.Position.X, pose.Position.Y, -pose.Position.Z);
                    var lr = new Quaternion(-pose.Orientation.X, -pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
                    Vector3 wp = origin.TransformPoint(lp);
                    Quaternion wr = origin.rotation * lr;
                    _camGo.transform.SetPositionAndRotation(wp, wr);
                    UpdateLaser(wp, wr, !leftActive);
                    GeometryLog(wp, wr);
                }
                else ShowLaser(false);

                // Drive the LEFT pointer (cam + laser + left-held control pin) every frame, AFTER the repoint
                // above so the left-held control's camera override sticks. When it's the active cursor its laser
                // colours from the game hover (the pencil/compass slots), not the grab scan.
                DriveLeftPointer(input, origin, leftActive);

                // Clicking uses the ACTIVE hand's trigger; bail (without thrashing engage) if it isn't tracked.
                bool activeAimValid = leftActive ? input.AimValidL : input.AimValid;
                if (!activeAimValid) { EndAllInput(); return; }

                if (suppressClick)
                {
                    // The VR settings menu has the trigger this frame: release anything held in the
                    // game and don't click/keypress, but keep pointing the laser at the panel.
                    EndAllInput();
                }
                else
                {
                    HandleClick(input, leftActive);
                    HandleMapDelete(input);
                }
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
            RepointMapCameras(_cam);
            RepointTeleprinters(_cam);
            _engaged = true;
            Log.LogInfo($"[interact] ENGAGED (origCam={(_origCam != null ? _origCam.name : "null")}, origMode={_origMode}).");
        }

        // Point every control's own raycast camera at the controller. Dials, levers and the hover
        // tooltip each carry their own raycastCamera; without this they keep using the flat camera and
        // dial twist / tooltips don't follow the laser. EXCEPTION: the dial currently held by the
        // HandManipulator drives itself from its own orbit-follow camera, so we skip repointing it here
        // (else this periodic pass would clobber that assignment and the dial would snap back to laser-aim).
        private void RepointInteractionCameras(Camera cam)
        {
            Transform heldDial = Manip != null ? Manip.HeldDialTransform : null;
            SetRaycastCameraOn(Il2CppType.Of<DialInteractable>(), cam, heldDial);
            SetRaycastCameraOn(Il2CppType.Of<LinearSliderInteractable>(), cam, null);
            SetRaycastCameraOn(Il2CppType.Of<HoverTooltip>(), cam, null);
        }

        // Pin the game's virtual cursor to screen-centre while we drive interaction, so the UI raycast
        // (cursorScreenPos through our controller cam) points exactly down the controller. In FPSLocked
        // with lockToCenterWhenFPSLocked the manager re-centres the cursor itself every frame; we just
        // make sure it stays in that mode (menus flip it to FreeMouse) and the flag is set.
        private void ForceCenterCursor()
        {
            try
            {
                if (_mgr.CurrentMode != DynamicCursorManager.PresentationMode.FPSLocked)
                    _mgr.SwitchToFPSLocked();
                var vc = _mgr.virtualCursor;
                if (vc != null && !vc.lockToCenterWhenFPSLocked) vc.lockToCenterWhenFPSLocked = true;
            }
            catch { }
        }

        // <paramref name="exempt"/>: a control transform to leave alone (the HandManipulator-held dial, which
        // owns its raycast camera while grabbed). Null = repoint everything.
        private static void SetRaycastCameraOn(Il2CppSystem.Type t, Camera cam, Transform exempt)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            if (arr == null) return;
            int exemptId = exempt != null ? exempt.GetInstanceID() : 0;
            for (int i = 0; i < arr.Length; i++)
            {
                var d = arr[i].TryCast<DialInteractable>();
                if (d != null) { if (exemptId == 0 || d.transform.GetInstanceID() != exemptId) d.raycastCamera = cam; continue; }
                var s = arr[i].TryCast<LinearSliderInteractable>();
                if (s != null) { s.raycastCamera = cam; continue; }
                var h = arr[i].TryCast<HoverTooltip>();
                if (h != null) { h.raycastCamera = cam; }
            }
        }

        // Point the tactical-map systems' cameras down our controller, the same fix as the dials/levers.
        // The map line placer (MapMarkerPlacer) and the hover sector grid (GridSquareHighlighterWithSubsector)
        // both project the centre-pinned virtual cursor's SCREEN position through a camera onto the world-
        // space map canvas; out of the box that's the flat Main Camera, so lines + grid land where the head
        // gazes, not where the laser points. We retarget the placer's own raycast camera + the world-space
        // map/grid canvases' event cameras to our pointer cam, force the placer's marker hover on (so the
        // laser highlights a line for deletion), and pin their cursor to centre. Originals are captured per
        // instance id and restored on disengage. Re-run on a throttle so a scene change is picked up.
        private void RepointMapCameras(Camera cam)
        {
            if (!Config.MapVrEnabled) return;
            int found = 0;
            try
            {
                var placers = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapMarkerPlacer>(), FindObjectsSortMode.None);
                if (placers != null)
                    for (int i = 0; i < placers.Length; i++)
                    {
                        var p = placers[i].TryCast<MapMarkerPlacer>();
                        if (p == null) continue;
                        int id = p.GetInstanceID();
                        _placers[id] = p;
                        found++;
                        try { if (!_origPlacerCam.ContainsKey(id)) _origPlacerCam[id] = p.mainCamera; if (p.mainCamera != cam) p.mainCamera = cam; } catch { }
                        try { if (!_origPlacerHover.ContainsKey(id)) _origPlacerHover[id] = p.enableHover; if (!p.enableHover) p.enableHover = true; } catch { }
                        try { PinCursor(p.virtualCursor); } catch { }
                        try { RepointCanvas(p.mapCanvas, cam); } catch { }
                    }

                var grids = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<GridSquareHighlighterWithSubsector>(), FindObjectsSortMode.None);
                if (grids != null)
                    for (int i = 0; i < grids.Length; i++)
                    {
                        var g = grids[i].TryCast<GridSquareHighlighterWithSubsector>();
                        if (g == null) continue;
                        found++;
                        try { PinCursor(g.virtualCursor); } catch { }
                        try { RepointCanvas(g.worldSpaceCanvas, cam); } catch { }
                    }
            }
            catch (Exception e) { Log.LogWarning("[map] repoint: " + e.Message); }

            if (found != _mapObjCount)
            {
                _mapObjCount = found;
                Log.LogInfo($"[map] VR map repoint: {_placers.Count} placer(s), {_mapCanvases.Count} canvas(es) on controller cam.");
            }
        }

        // Point the teleprinter's line hit-test camera down our controller. The teleprinter reads the cursor
        // manager's (centre-pinned) SCREEN position and projects it through hitTestCamera onto its 3D printout to
        // decide which line is hovered; out of the box that camera is the flat Main Camera, so the selection
        // tracks the head — it sticks on a default/last line and a click "sends" only that. Retarget hitTestCamera
        // to our pointer cam (and pin its cursor manager to centre) so the hovered line — and the click-drag
        // range — follow the laser. Originals captured per instance id, restored on disengage.
        private void RepointTeleprinters(Camera cam)
        {
            int found = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TeleprinterLineRangeSelector3D>(), FindObjectsSortMode.None);
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var tp = arr[i].TryCast<TeleprinterLineRangeSelector3D>();
                        if (tp == null) continue;
                        int id = tp.GetInstanceID();
                        _teleprinters[id] = tp;
                        found++;
                        try { if (!_origTeleCam.ContainsKey(id)) _origTeleCam[id] = tp.hitTestCamera; if (tp.hitTestCamera != cam) tp.hitTestCamera = cam; } catch { }
                        try { var m = tp.cursorManager; if (m != null) PinCursor(m.virtualCursor); } catch { }
                    }
            }
            catch (Exception e) { Log.LogWarning("[teleprinter] repoint: " + e.Message); }

            if (found != _teleCount)
            {
                _teleCount = found;
                Log.LogInfo($"[teleprinter] VR hit-test repoint: {found} selector(s) on controller cam.");
            }
        }

        private void RestoreTeleprinters()
        {
            foreach (var kv in _teleprinters)
            {
                var tp = kv.Value;
                if (tp == null) continue;
                try { if (_origTeleCam.TryGetValue(kv.Key, out var c)) tp.hitTestCamera = c; } catch { }
            }
            _teleprinters.Clear(); _origTeleCam.Clear(); _teleCount = -1;
        }

        // Only world-space canvases are safe to re-aim: their event camera affects input projection, not
        // rendering. (Screen-space-camera canvases — e.g. the menus — render THROUGH worldCamera, so
        // reassigning it there breaks them; the renderMode guard keeps us off those.)
        private void RepointCanvas(Canvas c, Camera cam)
        {
            if (c == null || c.renderMode != RenderMode.WorldSpace) return;
            int id = c.GetInstanceID();
            if (!_origCanvasCam.ContainsKey(id)) _origCanvasCam[id] = c.worldCamera;
            _mapCanvases[id] = c;
            if (c.worldCamera != cam) c.worldCamera = cam;
        }

        private static void PinCursor(VirtualCursor vc)
        {
            if (vc != null && !vc.lockToCenterWhenFPSLocked) vc.lockToCenterWhenFPSLocked = true;
        }

        // Right B button -> delete the line marker under the laser. Calls the placer's own secondary-click
        // handler (the same entry a real right-click reaches) rather than synthesizing a global right mouse,
        // so it can only ever delete a hovered marker — no risk of tripping other secondary-bound actions.
        private void HandleMapDelete(VrInput input)
        {
            if (!Config.MapVrEnabled) return;
            bool held = input.MapDeleteHeld;
            if (held && !_mapDeleteWasHeld)
            {
                bool anyHover = false;
                foreach (var kv in _placers)
                {
                    var p = kv.Value;
                    if (p == null) continue;
                    try { if (p.hoveredHitTarget != null) anyHover = true; } catch { }
                    try { p.HandleSecondaryPressed(); } catch (Exception e) { Log.LogWarning("[map] delete: " + e.Message); }
                }
                if (anyHover) input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
                Log.LogInfo($"[map] delete pressed (hovering={anyHover}, placers={_placers.Count})");
            }
            _mapDeleteWasHeld = held;
        }

        // Restore every map camera/flag we overrode to its captured original, then forget them. Stale refs
        // from a destroyed scene throw on access and are skipped.
        private void RestoreMapCameras()
        {
            foreach (var kv in _placers)
            {
                var p = kv.Value;
                if (p == null) continue;
                try { if (_origPlacerCam.TryGetValue(kv.Key, out var c)) p.mainCamera = c; } catch { }
                try { if (_origPlacerHover.TryGetValue(kv.Key, out var h)) p.enableHover = h; } catch { }
            }
            foreach (var kv in _mapCanvases)
            {
                var c = kv.Value;
                if (c == null) continue;
                try { if (_origCanvasCam.TryGetValue(kv.Key, out var cam)) c.worldCamera = cam; } catch { }
            }
            _placers.Clear(); _origPlacerCam.Clear(); _origPlacerHover.Clear();
            _mapCanvases.Clear(); _origCanvasCam.Clear();
            _mapDeleteWasHeld = false;
            _mapObjCount = -1;
        }

        private void HandleClick(VrInput input, bool leftActive)
        {
            // The clicking hand follows the pointing hand: left trigger when the left hand is the active cursor
            // (right hand holding the clipboard), right trigger otherwise.
            bool held = leftActive ? input.TriggerHeldL : input.TriggerHeld;
            if (InputSynth.Supported)
            {
                if (held) InputSynth.SetMouseLeft(true);
                else if (_triggerWasHeld) InputSynth.SetMouseLeft(false);
                if (held && !_triggerWasHeld)
                {
                    input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
                    var h = _mgr.CurrentHover;
                    Log.LogInfo($"[interact] trigger DOWN ({(leftActive ? "L" : "R")}) over '{(h != null ? h.name : "nothing")}'");
                }
            }
            else
            {
                if (held && !_triggerWasHeld) BeginPress(input);
                else if (!held && _triggerWasHeld) ReleasePress();
            }
            _triggerWasHeld = held;
        }

        private void EndAllInput()
        {
            if (_triggerWasHeld) InputSynth.SetMouseLeft(false);
            if (_pressTarget != null) ReleasePress();
            _triggerWasHeld = false;
            _mapDeleteWasHeld = false;
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
                RestoreMapCameras();
                RestoreTeleprinters();
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
                // Unique depth (must differ from the left pointer cam below AND the eye cams): the game's
                // Ultimate LOD System spams a per-frame LogError if two active cameras share a depth.
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
            if (_camGoL == null)
            {
                _camGoL = new GameObject("IronNestVR_PointerCamL");
                UnityEngine.Object.DontDestroyOnLoad(_camGoL);
                _camGoL.hideFlags = HideFlags.HideAndDontSave;
                _camL = _camGoL.AddComponent<Camera>();
                _camL.clearFlags = CameraClearFlags.Nothing; // draws nothing; exists only to cast the left ray
                _camL.cullingMask = 0;
                _camL.depth = -101f; // distinct from the right pointer cam (-100) — see Ultimate LOD note above
                _camL.nearClipPlane = 0.01f;
                _camL.fieldOfView = 60f;
                // Left enabled like the right pointer cam: it renders nothing (cullingMask 0) but must keep a
                // valid viewport so the dial/lever native drag's ScreenPointToRay projects correctly.
            }
            if (_laserGoL == null)
            {
                _laserGoL = new GameObject("IronNestVR_LaserL");
                UnityEngine.Object.DontDestroyOnLoad(_laserGoL);
                _laserGoL.hideFlags = HideFlags.HideAndDontSave;
                _laserL = _laserGoL.AddComponent<LineRenderer>();
                _laserL.useWorldSpace = true;
                _laserL.positionCount = 2;
                _laserL.startWidth = Config.LaserWidth;
                _laserL.endWidth = Config.LaserWidth;
                _laserL.numCapVertices = 0;
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh != null) _laserL.material = new Material(sh);
                _hoverColorL = false;
                SetLaserColor(_laserL, false);
            }
        }

        // Laser is cyan normally, GREEN when the game reports something interactable under it — so the
        // player knows when they're actually aimed at a control (catches any VR aim/parallax offset).
        // Set BOTH the LineRenderer vertex colors AND the material's colour properties: URP/Unlit
        // ignores vertex colours, so the gradient alone wouldn't change anything.
        private void ApplyLaserColor(bool hovering) => SetLaserColor(_laser, hovering);

        private static void SetLaserColor(LineRenderer laser, bool hovering)
        {
            if (laser == null) return;
            Color c = hovering ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(0.2f, 0.9f, 1f, 1f);
            laser.startColor = c;
            laser.endColor = new Color(c.r, c.g, c.b, 0.35f);
            var m = laser.material;
            if (m != null)
            {
                try { m.color = c; } catch { }
                try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
                try { if (m.HasProperty("_Color")) m.SetColor("_Color", c); } catch { }
            }
        }

        // End the laser at the first surface it hits so it visibly lands on the control. <paramref name="activeCursor"/>
        // is true only when the RIGHT hand is the live game cursor — the hover-green comes from the game's
        // CurrentHover then; when the left hand has taken over the cursor, the right laser stays plain.
        private void UpdateLaser(Vector3 origin, Quaternion rot, bool activeCursor)
        {
            if (_laser == null) return;
            Vector3 dir = rot * Vector3.forward;
            float len = Config.LaserMaxDistance;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Ignore))
                len = hit.distance;
            _laser.SetPosition(0, origin);
            _laser.SetPosition(1, origin + dir * len);

            bool hovering = false;
            if (activeCursor) { try { hovering = _mgr.CurrentHover != null; } catch { } }
            if (hovering != _hoverColor) { _hoverColor = hovering; ApplyLaserColor(hovering); }
            // Always-on draws it constantly; force-on while the VR menu is open; otherwise only when
            // aimed at an interactable (CurrentHover). But hide it whenever the RIGHT hand is busy grip-
            // holding something (clipboard/manual/dial/lever/switch) — a laser from a full hand is just noise.
            ShowLaser(!HandGripping(2) && (Config.LaserAlwaysOn || hovering || _forceLaser));
        }

        // True when the given hand (1 left, 2 right) is currently grip-holding a prop (GrabManager) or
        // operating a control (HandManipulator) — i.e. it's not free to point.
        private bool HandGripping(int hand)
            => (Grab != null && Grab.GrabbingHand == hand) || (Manip != null && Manip.HeldHand == hand);

        private void GeometryLog(Vector3 wp, Quaternion wr)
        {
            if (!Config.LogInteractGeometry || Time.unscaledTime < _nextGeoLog) return;
            _nextGeoLog = Time.unscaledTime + Config.InteractLogIntervalSec;
            Vector3 f = wr * Vector3.forward;
            string hover = "none";
            try { var h = _mgr.CurrentHover; if (h != null) hover = h.name; } catch { }
            Log.LogInfo($"[interact] ctrl=({wp.x:0.00},{wp.y:0.00},{wp.z:0.00}) fwd=({f.x:0.00},{f.y:0.00},{f.z:0.00}) hover={hover}");

            // UI-cursor mapping (issue C): how the game maps our pointer to a SCREEN position for menus.
            // If this shifts with render scale, the eye-RT pixel space is leaking into the UI raycast.
            try
            {
                Vector2 ptr = _mgr.GetActivePointerScreenPosition();
                var mc = UnityEngine.InputSystem.Mouse.current;
                Vector2 mouse = mc != null ? mc.position.ReadValue() : Vector2.zero;
                Log.LogInfo($"[ui] screen={Screen.width}x{Screen.height} ptrCam={_cam.pixelWidth}x{_cam.pixelHeight} " +
                            $"pointerScreenPos={ptr} mouse={mouse}");
            }
            catch (Exception e) { Log.LogWarning("[ui] " + e.Message); }
        }

        private void ShowLaser(bool on)
        {
            if (_laserGo != null && _laserGo.activeSelf != on) _laserGo.SetActive(on);
        }

        private void ShowLaserL(bool on)
        {
            if (_laserGoL != null && _laserGoL.activeSelf != on) _laserGoL.SetActive(on);
        }

        // Drive the LEFT pointer cam + laser down the left controller, and (during a left-hand dial/lever
        // drag) pin the held control's own raycast camera to the left cam so its native drag follows the LEFT
        // hand (the periodic repoint otherwise pins every control to the right cam).
        private void DriveLeftPointer(VrInput input, Transform origin, bool activeCursor = false)
        {
            if (_camGoL == null) return;
            if (!input.AimValidL) { ShowLaserL(false); return; }
            Posef p = input.AimPoseL;
            var lp = new Vector3(p.Position.X, p.Position.Y, -p.Position.Z);
            var lr = new Quaternion(-p.Orientation.X, -p.Orientation.Y, p.Orientation.Z, p.Orientation.W);
            Vector3 wp = origin.TransformPoint(lp);
            Quaternion wr = origin.rotation * lr;
            _camGoL.transform.SetPositionAndRotation(wp, wr);

            UpdateLaserL(wp, wr, activeCursor);

            var leftCtrl = Manip != null ? Manip.LeftHeldControlTransform : null;
            if (leftCtrl != null && _camL != null) SetControlRaycastCam(leftCtrl, _camL);
        }

        // When <paramref name="activeCursor"/> the left hand IS the game cursor (right hand holds the clipboard):
        // end the laser at the first solid surface and colour it from the game's CurrentHover (so the pencil/
        // compass slots light it green), and keep it visible. Otherwise it's the secondary pointer — colour off
        // the nearest grabbable under it (dial/lever/switch/manual), as before.
        private void UpdateLaserL(Vector3 origin, Quaternion rot, bool activeCursor)
        {
            if (_laserL == null) return;
            Vector3 dir = rot * Vector3.forward;
            float len; bool hovering;
            if (activeCursor)
            {
                len = Config.LaserMaxDistance;
                if (Physics.Raycast(origin, dir, out RaycastHit hit, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Ignore))
                    len = hit.distance;
                hovering = false;
                try { hovering = _mgr != null && _mgr.CurrentHover != null; } catch { }
            }
            else
            {
                LeftLaserScan(origin, dir, out len, out hovering);
            }
            _laserL.SetPosition(0, origin);
            _laserL.SetPosition(1, origin + dir * len);
            if (hovering != _hoverColorL) { _hoverColorL = hovering; SetLaserColor(_laserL, hovering); }
            // Hide whenever the LEFT hand is busy grip-holding something, even if it's nominally the active
            // cursor (e.g. it grabbed a dial) — a laser from a full hand is just noise.
            ShowLaserL(!HandGripping(1) && (Config.LaserAlwaysOn || hovering || _forceLaser || activeCursor));
        }

        // Nearest grabbable (dial/lever/switch/manual) under the left ray → green + laser ends there; else the
        // laser ends at the first solid surface (or max). Sees through triggers, matching the grab raycasts.
        private static void LeftLaserScan(Vector3 ao, Vector3 dir, out float len, out bool hovering)
        {
            len = Config.LaserMaxDistance; hovering = false;
            var hits = Physics.RaycastAll(ao, dir, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Collide);
            float grab = float.MaxValue, solid = float.MaxValue;
            if (hits != null)
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i]; var col = h.collider; if (col == null) continue;
                    var t = col.transform; if (t == null) continue;
                    bool isGrab = FindUp<DialInteractable>(t) != null
                               || FindUp<LinearSliderInteractable>(t) != null
                               || FindUp<LookAtTarget>(t) != null
                               || FindUp<PickUpZoomTarget>(t) != null;
                    if (isGrab) { if (h.distance < grab) grab = h.distance; }
                    else if (!col.isTrigger && h.distance < solid) solid = h.distance;
                }
            if (grab < float.MaxValue) { hovering = true; len = grab; }
            else if (solid < float.MaxValue) len = solid;
        }

        // Pin a control's OWN raycast camera (dial/lever) so its native drag projects from that camera.
        private static void SetControlRaycastCam(Transform t, Camera cam)
        {
            var d = t.GetComponent<DialInteractable>();
            if (d != null) { d.raycastCamera = cam; return; }
            var s = t.GetComponent<LinearSliderInteractable>();
            if (s != null) { s.raycastCamera = cam; }
        }

        // Walk up from a transform to the first ancestor carrying component T.
        private static T FindUp<T>(Transform t) where T : Component
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                var c = p.GetComponent<T>();
                if (c != null) return c;
            }
            return null;
        }

        public void Dispose()
        {
            if (_engaged) Restore("dispose");
            if (_laserGo != null) UnityEngine.Object.Destroy(_laserGo);
            if (_camGo != null) UnityEngine.Object.Destroy(_camGo);
            if (_laserGoL != null) UnityEngine.Object.Destroy(_laserGoL);
            if (_camGoL != null) UnityEngine.Object.Destroy(_camGoL);
            _laserGo = null; _camGo = null; _laser = null; _cam = null;
            _laserGoL = null; _camGoL = null; _laserL = null; _camL = null;
            _mgr = null; _engaged = false;
        }

        private static UnityEngine.Object FirstOf(Il2CppSystem.Type t)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            return (arr != null && arr.Length > 0) ? arr[0] : null;
        }
    }
}
