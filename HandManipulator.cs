using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// "Gravity glove" grab of cockpit controls. The laser stays the targeting tool; squeezing a grip while
    /// that hand's laser is on a control flies the hand model onto it and operates it. Two families:
    ///
    /// • DRAG controls (<c>DialInteractable</c>/<c>LinearSliderInteractable</c>): start the GAME'S OWN drag
    ///   (<c>BeginDialDrag</c>/<c>BeginSliderDrag</c>) — the exact path the trigger uses, so the control
    ///   actually turns/slides and the turret reacts. While held the control follows the controller pointer
    ///   (the <see cref="CockpitInteractor"/> keeps its raycast camera pinned down the holding controller every
    ///   frame — the right pointer cam for a right-hand grab, the LEFT pointer cam for a left-hand grab) and the
    ///   hand is stuck RIGIDLY to the control's transform.
    ///
    /// • CLICK switches/buttons (<c>LookAtTarget</c>): there is no drag to ride, so we let you PHYSICALLY
    ///   OPERATE them — the hand follows the controller's travel from the grab point, and once it moves past
    ///   <see cref="Config.SwitchThrowDistance"/> we fire the control's click the same way the cursor manager
    ///   does (<c>HandleClickDown/UpFromManager</c>). Moving back re-arms it. (Skips <c>PickUpZoomTarget</c>
    ///   manuals — those stay reposition-grabs owned by <see cref="GrabManager"/>.)
    ///
    /// EITHER hand can grab (both controllers have an aim ray). One control at a time; the right hand wins a
    /// tie. For a left-hand DRAG the held control's raycast camera must follow the LEFT controller — see
    /// <see cref="LeftHeldControlTransform"/>, which CockpitInteractor reads to pin that control to the left cam.
    /// </summary>
    internal sealed class HandManipulator
    {
        private static ManualLogSource Log => Plugin.Logger;

        private enum Kind { None, Dial, Lever, Switch }

        private Kind _kind;
        private int _hand;             // 0 none, 1 left, 2 right (which hand holds the control)
        private DialInteractable _dial;
        private LinearSliderInteractable _lever;

        // Click-switch grab (LookAtTarget): the grabbed control, its Interactable (the click target the
        // manager dispatches to), the controller-grip position at grab, the hand anchor on the control, and
        // a latch so each throw fires exactly one click until the hand returns to re-arm.
        private LookAtTarget _switch;
        private Interactable _switchInteractable;
        private Vector3 _switchGrabGripPos;
        private Vector3 _switchHandAnchor;
        private bool _switchLatched;

        private bool _prevGrabR, _prevGrabL;

        // Set by VrManager. For a LEFT-hand dial/lever drag we point the control's own raycast camera at the
        // LEFT pointer cam right away (before BeginDialDrag), so the first drag frame already reads the left
        // controller instead of the right (CockpitInteractor keeps it pinned there for the rest of the drag).
        internal CockpitInteractor Cockpit;

        // The hand's grab pose expressed in the control's LOCAL space, reapplied each frame so the hand
        // rides the actual knob/handle as the game moves it.
        private Vector3 _stickLocalPos;
        private Quaternion _stickLocalRot;
        private Vector3 _handPos;
        private Quaternion _handRot;

        public bool Active => _kind != Kind.None;

        // The dial/lever currently held by the LEFT hand (else null). CockpitInteractor pins this control's
        // own raycast camera to the LEFT pointer cam each frame so its native drag follows the left controller
        // (the periodic repoint otherwise pins every control to the right cam). Switches don't need it (their
        // activation is camera-independent), so only Dial/Lever are reported.
        public Transform LeftHeldControlTransform =>
            (_hand == 1) ? (_kind == Kind.Dial && _dial != null ? _dial.transform
                          : _kind == Kind.Lever && _lever != null ? _lever.transform : null) : null;

        public void Tick(VrInput input, CameraRig rig, HandVisuals hands, bool active)
        {
            try
            {
                if (!Config.HandManipEnabled || !active)
                {
                    if (Active) Release(input, hands, "inactive");
                    _prevGrabR = _prevGrabL = false;
                    return;
                }
                var origin = rig.OriginTransform;
                if (origin == null) return;

                if (Active) Drive(input, origin, hands);
                else
                {
                    // Right hand wins a tie; each hand needs its OWN aim ray + grip.
                    if (input.GrabR && !_prevGrabR && input.AimValid && input.GripValid)
                        TryAcquire(2, input, origin, hands);
                    else if (input.GrabL && !_prevGrabL && input.AimValidL && input.GripValidL)
                        TryAcquire(1, input, origin, hands);
                }

                _prevGrabR = input.GrabR;
                _prevGrabL = input.GrabL;
            }
            catch (Exception e)
            {
                Log.LogWarning("[manip] " + e.Message);
                EndDrag();
                _kind = Kind.None; _hand = 0; _dial = null; _lever = null;
                _switch = null; _switchInteractable = null; _switchLatched = false;
                try { hands.ClearGrab(true); } catch { }
                try { hands.ClearGrab(false); } catch { }
            }
        }

        // ---------------- per-hand input helpers ----------------

        private static bool HandGrab(int hand, VrInput input) => hand == 2 ? input.GrabR : input.GrabL;
        private static bool HandGripValid(int hand, VrInput input) => hand == 2 ? input.GripValid : input.GripValidL;
        private static Posef HandGripPose(int hand, VrInput input) => hand == 2 ? input.GripPose : input.GripPoseL;
        private static Posef HandAimPose(int hand, VrInput input) => hand == 2 ? input.AimPose : input.AimPoseL;

        // ---------------- acquire ----------------

        private void TryAcquire(int hand, VrInput input, Transform origin, HandVisuals hands)
        {
            GetWorld(origin, HandAimPose(hand, input), out Vector3 ao, out Quaternion ar);
            Vector3 dir = ar * Vector3.forward;
            // Pick the nearest CONTROL the ray passes through, not just the single closest collider. A plain
            // Physics.Raycast stops at the first collider on any layer — so an invisible trigger volume (or any
            // other non-control collider) in front of a dial/lever/switch silently blocks the grab. Scanning
            // all hits for the nearest grabbable control restores parity with the trigger; a closer pick-up
            // manual still yields to GrabManager's reposition grab.
            if (!RaycastControl(ao, dir, out RaycastHit hit, out Transform t))
                return;

            var dial = FindUp<DialInteractable>(t);
            if (dial != null)
            {
                if (CoopControls.IsRemotelyOwned(dial)) { input.Haptic(Config.DetentHapticAmplitude, 0.02f); return; }  // peer is using it
                EngageDial(hand, dial, hit.point, input, origin, hands); return;
            }
            var lever = FindUp<LinearSliderInteractable>(t);
            if (lever != null)
            {
                if (CoopControls.IsRemotelyOwned(lever)) { input.Haptic(Config.DetentHapticAmplitude, 0.02f); return; }
                EngageLever(hand, lever, hit.point, input, origin, hands); return;
            }

            // Click switch/button (LookAtTarget). Skip manuals (PickUpZoomTarget) — those are reposition
            // grabs owned by GrabManager, and their LookAtTarget click is the read-zoom we don't want here.
            if (Config.SwitchGrabEnabled && FindUp<PickUpZoomTarget>(t) == null)
            {
                var sw = FindUp<LookAtTarget>(t);
                if (sw != null) EngageSwitch(hand, sw, hit.point, input, origin, hands);
            }
        }

        private void EngageDial(int hand, DialInteractable dial, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _dial = dial;
            _kind = Kind.Dial;
            _hand = hand;
            StickTo(hand, dial.transform, hitPoint, input, origin);
            if (hand == 1) PinLeftCam(c => dial.raycastCamera = c);  // left drag follows the left controller from frame 0
            try { dial.BeginDialDrag(); } catch (Exception e) { Log.LogWarning("[manip] BeginDialDrag: " + e.Message); }
            hands.SetGrab(hand == 2, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed dial '{dial.name}' ({(hand == 2 ? "right" : "left")}, native drag).");
        }

        private void EngageLever(int hand, LinearSliderInteractable lever, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _lever = lever;
            _kind = Kind.Lever;
            _hand = hand;
            StickTo(hand, lever.transform, hitPoint, input, origin);
            if (hand == 1) PinLeftCam(c => lever.raycastCamera = c);  // left drag follows the left controller from frame 0
            try { lever.BeginSliderDrag(); } catch (Exception e) { Log.LogWarning("[manip] BeginSliderDrag: " + e.Message); }
            hands.SetGrab(hand == 2, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed lever '{lever.name}' ({(hand == 2 ? "right" : "left")}, native drag).");
        }

        private void EngageSwitch(int hand, LookAtTarget sw, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _switch = sw;
            _switchInteractable = sw.interactable;
            _kind = Kind.Switch;
            _hand = hand;
            GetWorld(origin, HandGripPose(hand, input), out Vector3 gp, out Quaternion gr);
            _switchGrabGripPos = gp;     // controller anchor — throw is measured from here
            _switchHandAnchor = hitPoint; // seed the hand on the switch; it rides the controller travel
            _switchLatched = false;
            _handPos = hitPoint;
            _handRot = gr;
            hands.SetGrab(hand == 2, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed switch '{sw.name}' ({(hand == 2 ? "right" : "left")}, throw {Config.SwitchThrowDistance * 100f:0.#}cm to activate).");
        }

        // Point a left-held control's raycast camera at the LEFT pointer cam (owned by CockpitInteractor).
        private void PinLeftCam(Action<Camera> set)
        {
            var cam = Cockpit != null ? Cockpit.LeftPointerCam : null;
            if (cam != null) set(cam);
        }

        // Record the grab pose relative to the control's transform, and seed the hand there now.
        private void StickTo(int hand, Transform ctrl, Vector3 hitPoint, VrInput input, Transform origin)
        {
            GetWorld(origin, HandGripPose(hand, input), out _, out Quaternion cr);
            _stickLocalPos = ctrl.InverseTransformPoint(hitPoint);
            _stickLocalRot = Quaternion.Inverse(ctrl.rotation) * cr;
            _handPos = hitPoint;
            _handRot = cr;
        }

        // ---------------- drive ----------------

        private void Drive(VrInput input, Transform origin, HandVisuals hands)
        {
            bool held = HandGrab(_hand, input) && HandGripValid(_hand, input);
            if (!held) { Release(input, hands, "released"); return; }

            if (_kind == Kind.Switch) { DriveSwitch(input, origin, hands); return; }

            Transform ctrl = _kind == Kind.Dial ? (_dial != null ? _dial.transform : null)
                           : _kind == Kind.Lever ? (_lever != null ? _lever.transform : null) : null;
            if (ctrl == null) { Release(input, hands, "control gone"); return; }

            // The game turns/slides the control from the controller pointer (native drag); we only keep
            // the hand stuck to the moving knob/handle.
            _handPos = ctrl.TransformPoint(_stickLocalPos);
            _handRot = ctrl.rotation * _stickLocalRot;
            hands.SetGrab(_hand == 2, _handPos, _handRot);
        }

        // Click switch: the game has no drag to ride, so the HAND tracks the controller's travel from the
        // grab point and we fire the click once the travel passes the throw threshold (re-arming when the
        // hand returns, so a back-and-forth toggles on then off within one grab).
        private void DriveSwitch(VrInput input, Transform origin, HandVisuals hands)
        {
            if (_switch == null) { Release(input, hands, "control gone"); return; }

            GetWorld(origin, HandGripPose(_hand, input), out Vector3 gp, out Quaternion gr);
            Vector3 delta = gp - _switchGrabGripPos;
            _handPos = _switchHandAnchor + delta; // hand follows the controller so it looks operated
            _handRot = gr;
            hands.SetGrab(_hand == 2, _handPos, _handRot);

            float travel = delta.magnitude;
            if (!_switchLatched && travel >= Config.SwitchThrowDistance)
            {
                Activate(_switch, _switchInteractable);
                _switchLatched = true;
                input.Haptic(Config.HapticAmplitude, Mathf.Max(Config.HapticSeconds, 0.04f));
            }
            else if (_switchLatched && travel <= Mathf.Min(Config.SwitchThrowReset, Config.SwitchThrowDistance * 0.5f))
            {
                _switchLatched = false; // back near the grab point — ready to flip again
            }
        }

        // Fire the switch's click exactly as the cursor manager does: tell it it's hovered, then press and
        // release against its own Interactable. Targeting the specific control means a drifting laser can't
        // send the click elsewhere. Falls back to the raw click hooks if it has no Interactable.
        private static void Activate(LookAtTarget sw, Interactable it)
        {
            try
            {
                if (it != null)
                {
                    try { sw.HandleHoverChangedFromManager(it); } catch { }
                    sw.HandleClickDownFromManager(it);
                    sw.HandleClickUpFromManager(it);
                }
                else
                {
                    sw.OnClickDown();
                    sw.OnClickUp();
                }
                CoopControls.LocalClick(sw);   // replicate the click to the co-op peer
                Log.LogInfo($"[manip] switch '{sw.name}' activated.");
            }
            catch (Exception e) { Log.LogWarning("[manip] switch activate: " + e.Message); }
        }

        // ---------------- release ----------------

        private void Release(VrInput input, HandVisuals hands, string why)
        {
            EndDrag();
            try { hands.ClearGrab(_hand == 2); } catch { }
            try { input.Haptic(Config.DetentHapticAmplitude, 0.03f); } catch { }
            Log.LogInfo($"[manip] released ({why}).");
            _kind = Kind.None; _hand = 0; _dial = null; _lever = null;
            _switch = null; _switchInteractable = null; _switchLatched = false;
        }

        // End the game's native drag on whatever we hold (mirrors Begin*Drag).
        private void EndDrag()
        {
            try { if (_kind == Kind.Dial && _dial != null) _dial.EndDialDrag(); } catch { }
            try { if (_kind == Kind.Lever && _lever != null) _lever.EndSliderDrag(); } catch { }
        }

        // ---------------- helpers ----------------

        // Walk up from a hit collider to the first transform carrying component T.
        private static T FindUp<T>(Transform t) where T : Component
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                var c = p.GetComponent<T>();
                if (c != null) return c;
            }
            return null;
        }

        // Nearest hit along the ray whose hierarchy carries a grabbable control (dial/lever, or — when
        // switch-grab is on — a click switch that isn't a pick-up manual). Intervening trigger volumes and
        // other non-control colliders are transparent (matching the trigger-click, which uses the game's
        // layer-filtered interaction raycast); a pick-up manual that's closer than any control wins, so it
        // stays GrabManager's reposition grab. Returns false if no control is reachable.
        private static bool RaycastControl(Vector3 ao, Vector3 dir, out RaycastHit hit, out Transform t)
        {
            hit = default; t = null;
            var hits = Physics.RaycastAll(ao, dir, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Collide);
            if (hits == null) return false;
            float ctrlDist = float.MaxValue;
            float manualDist = float.MaxValue;   // nearest pick-up manual — yields to GrabManager
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                var col = h.collider;
                if (col == null) continue;
                var ht = col.transform;
                if (ht == null) continue;
                bool isControl =
                    FindUp<DialInteractable>(ht) != null ||
                    FindUp<LinearSliderInteractable>(ht) != null ||
                    (Config.SwitchGrabEnabled && FindUp<PickUpZoomTarget>(ht) == null && FindUp<LookAtTarget>(ht) != null);
                if (isControl)
                {
                    if (h.distance < ctrlDist) { ctrlDist = h.distance; hit = h; t = ht; }
                }
                else if (FindUp<PickUpZoomTarget>(ht) != null && h.distance < manualDist)
                {
                    manualDist = h.distance;
                }
            }
            if (t == null || ctrlDist > manualDist) { t = null; return false; }
            return true;
        }

        private static void GetWorld(Transform origin, Posef pose, out Vector3 pos, out Quaternion rot)
        {
            var lp = new Vector3(pose.Position.X, pose.Position.Y, -pose.Position.Z);
            var lr = new Quaternion(-pose.Orientation.X, -pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
            pos = origin.TransformPoint(lp);
            rot = origin.rotation * lr;
        }

        public void Reset()
        {
            EndDrag();
            _kind = Kind.None; _hand = 0; _dial = null; _lever = null; _prevGrabR = _prevGrabL = false;
            _switch = null; _switchInteractable = null; _switchLatched = false;
        }
    }
}
