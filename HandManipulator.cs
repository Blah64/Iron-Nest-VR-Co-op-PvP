using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// "Gravity glove" grab of cockpit controls. The laser stays the targeting tool; squeezing the RIGHT
    /// grip while the laser is on a control flies the hand model onto it and operates it. Two families:
    ///
    /// • DRAG controls (<c>DialInteractable</c>/<c>LinearSliderInteractable</c>): start the GAME'S OWN drag
    ///   (<c>BeginDialDrag</c>/<c>BeginSliderDrag</c>) — the exact path the trigger uses, so the control
    ///   actually turns/slides and the turret reacts (the prior value-API drive rotated the knob but
    ///   bypassed the drag/broker state, so it never registered). While held the control follows the
    ///   controller pointer (the <see cref="CockpitInteractor"/> keeps its raycast camera pinned down the
    ///   controller every frame) and the hand is stuck RIGIDLY to the control's transform.
    ///
    /// • CLICK switches/buttons (<c>LookAtTarget</c> — the simple click-to-activate controls): there is no
    ///   drag to ride, so instead we let you PHYSICALLY OPERATE them — the hand follows your controller's
    ///   travel from the grab point, and once it moves past <see cref="Config.SwitchThrowDistance"/> we fire
    ///   the control's click the same way the cursor manager does (<c>HandleClickDown/UpFromManager</c>).
    ///   Moving back re-arms it, so one grab can flip a toggle on then off. (Skips <c>PickUpZoomTarget</c>
    ///   manuals — those stay reposition-grabs owned by <see cref="GrabManager"/>.)
    ///
    /// Right-hand only (only the right aim ray exists); the left hand stays a cosmetic follower.
    /// </summary>
    internal sealed class HandManipulator
    {
        private static ManualLogSource Log => Plugin.Logger;

        private enum Kind { None, Dial, Lever, Switch }

        private Kind _kind;
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

        private bool _prevGrab;

        // The hand's grab pose expressed in the control's LOCAL space, reapplied each frame so the hand
        // rides the actual knob/handle as the game moves it.
        private Vector3 _stickLocalPos;
        private Quaternion _stickLocalRot;
        private Vector3 _handPos;
        private Quaternion _handRot;

        public bool Active => _kind != Kind.None;

        public void Tick(VrInput input, CameraRig rig, HandVisuals hands, bool active)
        {
            try
            {
                if (!Config.HandManipEnabled || !active)
                {
                    if (Active) Release(input, hands, "inactive");
                    _prevGrab = false;
                    return;
                }
                var origin = rig.OriginTransform;
                if (origin == null) return;

                if (Active) Drive(input, origin, hands);
                else if (input.GrabR && !_prevGrab && input.AimValid && input.GripValid)
                    TryAcquire(input, origin, hands);

                _prevGrab = input.GrabR;
            }
            catch (Exception e)
            {
                Log.LogWarning("[manip] " + e.Message);
                EndDrag();
                _kind = Kind.None; _dial = null; _lever = null;
                _switch = null; _switchInteractable = null; _switchLatched = false;
                try { hands.ClearGrab(true); } catch { }
            }
        }

        // ---------------- acquire ----------------

        private void TryAcquire(VrInput input, Transform origin, HandVisuals hands)
        {
            GetWorld(origin, input.AimPose, out Vector3 ao, out Quaternion ar);
            Vector3 dir = ar * Vector3.forward;
            if (!Physics.Raycast(ao, dir, out RaycastHit hit, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Collide))
                return;
            Transform t = hit.collider != null ? hit.collider.transform : null;
            if (t == null) return;

            var dial = FindUp<DialInteractable>(t);
            if (dial != null)
            {
                if (CoopControls.IsRemotelyOwned(dial)) { input.Haptic(Config.DetentHapticAmplitude, 0.02f); return; }  // peer is using it
                EngageDial(dial, hit.point, input, origin, hands); return;
            }
            var lever = FindUp<LinearSliderInteractable>(t);
            if (lever != null)
            {
                if (CoopControls.IsRemotelyOwned(lever)) { input.Haptic(Config.DetentHapticAmplitude, 0.02f); return; }
                EngageLever(lever, hit.point, input, origin, hands); return;
            }

            // Click switch/button (LookAtTarget). Skip manuals (PickUpZoomTarget) — those are reposition
            // grabs owned by GrabManager, and their LookAtTarget click is the read-zoom we don't want here.
            if (Config.SwitchGrabEnabled && FindUp<PickUpZoomTarget>(t) == null)
            {
                var sw = FindUp<LookAtTarget>(t);
                if (sw != null) EngageSwitch(sw, hit.point, input, origin, hands);
            }
        }

        private void EngageDial(DialInteractable dial, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _dial = dial;
            _kind = Kind.Dial;
            StickTo(dial.transform, hitPoint, input, origin);
            try { dial.BeginDialDrag(); } catch (Exception e) { Log.LogWarning("[manip] BeginDialDrag: " + e.Message); }
            hands.SetGrab(true, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed dial '{dial.name}' (native drag).");
        }

        private void EngageLever(LinearSliderInteractable lever, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _lever = lever;
            _kind = Kind.Lever;
            StickTo(lever.transform, hitPoint, input, origin);
            try { lever.BeginSliderDrag(); } catch (Exception e) { Log.LogWarning("[manip] BeginSliderDrag: " + e.Message); }
            hands.SetGrab(true, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed lever '{lever.name}' (native drag).");
        }

        private void EngageSwitch(LookAtTarget sw, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _switch = sw;
            _switchInteractable = sw.interactable;
            _kind = Kind.Switch;
            GetWorld(origin, input.GripPose, out Vector3 gp, out Quaternion gr);
            _switchGrabGripPos = gp;     // controller anchor — throw is measured from here
            _switchHandAnchor = hitPoint; // seed the hand on the switch; it rides the controller travel
            _switchLatched = false;
            _handPos = hitPoint;
            _handRot = gr;
            hands.SetGrab(true, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed switch '{sw.name}' (throw {Config.SwitchThrowDistance * 100f:0.#}cm to activate).");
        }

        // Record the grab pose relative to the control's transform, and seed the hand there now.
        private void StickTo(Transform ctrl, Vector3 hitPoint, VrInput input, Transform origin)
        {
            GetWorld(origin, input.GripPose, out _, out Quaternion cr);
            _stickLocalPos = ctrl.InverseTransformPoint(hitPoint);
            _stickLocalRot = Quaternion.Inverse(ctrl.rotation) * cr;
            _handPos = hitPoint;
            _handRot = cr;
        }

        // ---------------- drive ----------------

        private void Drive(VrInput input, Transform origin, HandVisuals hands)
        {
            bool held = input.GrabR && input.GripValid;
            if (!held) { Release(input, hands, "released"); return; }

            if (_kind == Kind.Switch) { DriveSwitch(input, origin, hands); return; }

            Transform ctrl = _kind == Kind.Dial ? (_dial != null ? _dial.transform : null)
                           : _kind == Kind.Lever ? (_lever != null ? _lever.transform : null) : null;
            if (ctrl == null) { Release(input, hands, "control gone"); return; }

            // The game turns/slides the control from the controller pointer (native drag); we only keep
            // the hand stuck to the moving knob/handle.
            _handPos = ctrl.TransformPoint(_stickLocalPos);
            _handRot = ctrl.rotation * _stickLocalRot;
            hands.SetGrab(true, _handPos, _handRot);
        }

        // Click switch: the game has no drag to ride, so the HAND tracks the controller's travel from the
        // grab point and we fire the click once the travel passes the throw threshold (re-arming when the
        // hand returns, so a back-and-forth toggles on then off within one grab).
        private void DriveSwitch(VrInput input, Transform origin, HandVisuals hands)
        {
            if (_switch == null) { Release(input, hands, "control gone"); return; }

            GetWorld(origin, input.GripPose, out Vector3 gp, out Quaternion gr);
            Vector3 delta = gp - _switchGrabGripPos;
            _handPos = _switchHandAnchor + delta; // hand follows the controller so it looks operated
            _handRot = gr;
            hands.SetGrab(true, _handPos, _handRot);

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
            try { hands.ClearGrab(true); } catch { }
            try { input.Haptic(Config.DetentHapticAmplitude, 0.03f); } catch { }
            Log.LogInfo($"[manip] released ({why}).");
            _kind = Kind.None; _dial = null; _lever = null;
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
            _kind = Kind.None; _dial = null; _lever = null; _prevGrab = false;
            _switch = null; _switchInteractable = null; _switchLatched = false;
        }
    }
}
