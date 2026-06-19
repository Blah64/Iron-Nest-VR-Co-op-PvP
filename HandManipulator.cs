using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// "Gravity glove" manipulation of cockpit dials/levers. The laser stays the targeting tool; when
    /// the RIGHT grip is squeezed while the laser is on a <c>DialInteractable</c> or
    /// <c>LinearSliderInteractable</c>, the hand model flies onto the control and rides it, and the
    /// control is driven directly from controller motion — even though your real hand is across the
    /// cockpit:
    ///  • <b>Dial</b>: the controller's twist about the dial's world spin axis (swing-twist decomposition,
    ///    accumulated per-frame so there's no wrap) maps to dial rotation via
    ///    <c>SetAccumulatedValueUnlimited</c>.
    ///  • <b>Lever</b>: the controller's travel along the lever's world axis (<c>GetAxisWorld</c>) maps to
    ///    handle distance via <c>SetSliderValue(MapDistanceToValue(d))</c>.
    ///
    /// We drive the control's own value API rather than the pointer-drag path, so the motion comes from
    /// the hand, not from sweeping the laser. Detents are suppressed while held (restored on release so
    /// it snaps), and auto-reset is disabled so the value sticks. Right-hand only (only the right aim
    /// ray exists); the left hand stays a cosmetic follower.
    /// </summary>
    internal sealed class HandManipulator
    {
        private static ManualLogSource Log => Plugin.Logger;

        private enum Kind { None, Dial, Lever }

        private Kind _kind;
        private DialInteractable _dial;
        private LinearSliderInteractable _lever;

        private bool _prevGrab;
        private Vector3 _axisW;            // world spin (dial) / travel (lever) axis
        private Vector3 _pivot;            // dial centre (for rotating the hand anchor)
        private Quaternion _prevCtrlRot;   // for per-frame twist deltas (dial)
        private Vector3 _startCtrlPos;     // controller pose at grab (lever)
        private float _startValue;         // dial accumulated angle / lever distance at grab
        private float _applied;            // accumulated angle delta (dial) — for detent haptic steps
        private float _lastDetentMark;

        private Vector3 _handPos;          // current hand anchor riding the control
        private Quaternion _handRot;

        // restored on release
        private bool _savedDetents, _savedReset;

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
                // Best-effort restore on a torn-down control, then reset.
                try { RestoreFlags(); } catch { }
                _kind = Kind.None; _dial = null; _lever = null;
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
            if (dial != null) { EngageDial(dial, hit.point, input, origin, hands); return; }
            var lever = FindUp<LinearSliderInteractable>(t);
            if (lever != null) EngageLever(lever, hit.point, input, origin, hands);
        }

        private void EngageDial(DialInteractable dial, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _dial = dial;
            _kind = Kind.Dial;
            _axisW = SafeNormalize(dial.transform.TransformDirection(dial.rotationAxis), dial.transform.up);
            _pivot = dial.transform.position;
            _startValue = dial.AccumulatedValue;
            _applied = 0f;
            _lastDetentMark = 0f;

            GetWorld(origin, input.GripPose, out _, out Quaternion cr);
            _prevCtrlRot = cr;
            _handPos = hitPoint;
            _handRot = cr;

            _savedDetents = dial.useDetents;
            _savedReset = dial.ResetToDefaultValueWithoutNoInput;
            if (Config.HandManipSuppressDetents) dial.useDetents = false;
            dial.ResetToDefaultValueWithoutNoInput = false;

            hands.SetGrab(true, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed dial '{dial.name}' (start angle {_startValue:0.0}).");
        }

        private void EngageLever(LinearSliderInteractable lever, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _lever = lever;
            _kind = Kind.Lever;
            _axisW = SafeNormalize(lever.GetAxisWorld(), lever.transform.up);
            _startValue = lever.currentDistance;

            GetWorld(origin, input.GripPose, out Vector3 cp, out Quaternion cr);
            _startCtrlPos = cp;
            _handPos = hitPoint;
            _handRot = cr;

            hands.SetGrab(true, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed lever '{lever.name}' (start dist {_startValue:0.000}).");
        }

        // ---------------- drive ----------------

        private void Drive(VrInput input, Transform origin, HandVisuals hands)
        {
            bool held = input.GrabR && input.GripValid;
            if (!held) { Release(input, hands, "released"); return; }

            if (_kind == Kind.Dial) DriveDial(input, origin, hands);
            else if (_kind == Kind.Lever) DriveLever(input, origin, hands);
        }

        private void DriveDial(VrInput input, Transform origin, HandVisuals hands)
        {
            if (_dial == null) { Release(input, hands, "dial gone"); return; }

            GetWorld(origin, input.GripPose, out _, out Quaternion cr);
            Quaternion inc = cr * Quaternion.Inverse(_prevCtrlRot);
            _prevCtrlRot = cr;

            float step = TwistDegrees(inc, _axisW) * Config.DialTwistSensitivity;
            _applied += step;

            float angle = _startValue + _applied;
            if (TryGetRange(_dial, out float lo, out float hi)) angle = Mathf.Clamp(angle, lo, hi);
            _dial.SetAccumulatedValueUnlimited(angle, true, false);

            // The hand rides the dial: rotate its anchor about the spin axis by the same step.
            _handPos = RotateAround(_handPos, _pivot, _axisW, step);
            _handRot = Quaternion.AngleAxis(step, _axisW) * _handRot;
            hands.SetGrab(true, _handPos, _handRot);

            DetentHaptic(input);
        }

        private void DriveLever(VrInput input, Transform origin, HandVisuals hands)
        {
            if (_lever == null) { Release(input, hands, "lever gone"); return; }

            GetWorld(origin, input.GripPose, out Vector3 cp, out _);
            float delta = Vector3.Dot(cp - _startCtrlPos, _axisW) * Config.LeverMoveSensitivity;
            float dist = _startValue + delta;
            float lo = _lever.minDistance, hi = _lever.maxDistance;
            if (hi > lo) dist = Mathf.Clamp(dist, lo, hi);

            _lever.SetSliderValue(_lever.MapDistanceToValue(dist));

            // The hand rides the handle along the travel axis.
            hands.SetGrab(true, _handPos + _axisW * (dist - _startValue), _handRot);
        }

        // Haptic tick each time the dial crosses a detent step while turning.
        private void DetentHaptic(VrInput input)
        {
            if (_dial == null) return;
            float stepSize;
            try { if (!_savedDetents || _dial.detentStepSize <= 0f) return; stepSize = _dial.detentStepSize; }
            catch { return; }
            if (Mathf.Abs(_applied - _lastDetentMark) >= stepSize)
            {
                _lastDetentMark = _applied;
                input.Haptic(Config.DetentHapticAmplitude, 0.02f);
            }
        }

        // ---------------- release ----------------

        private void Release(VrInput input, HandVisuals hands, string why)
        {
            RestoreFlags();
            try { hands.ClearGrab(true); } catch { }
            try { input.Haptic(Config.DetentHapticAmplitude, 0.03f); } catch { }
            Log.LogInfo($"[manip] released ({why}).");
            _kind = Kind.None; _dial = null; _lever = null;
        }

        private void RestoreFlags()
        {
            if (_kind == Kind.Dial && _dial != null)
            {
                try { _dial.useDetents = _savedDetents; } catch { }
                try { _dial.ResetToDefaultValueWithoutNoInput = _savedReset; } catch { }
            }
        }

        // ---------------- helpers ----------------

        // Prefer the dial's clamped range; fall back to its raw min/max; skip clamping if unset.
        private static bool TryGetRange(DialInteractable d, out float lo, out float hi)
        {
            lo = 0f; hi = 0f;
            try
            {
                lo = d.ClampedMinRotationAngle; hi = d.ClampedMaxRotationAngle;
                if (hi > lo) return true;
                lo = d.minRotationAngle; hi = d.maxRotationAngle;
                return hi > lo;
            }
            catch { return false; }
        }

        // Signed rotation (degrees) of quaternion dq about a unit axis — the "twist" of a swing-twist
        // decomposition. Stable for the small per-frame deltas we feed it.
        private static float TwistDegrees(Quaternion dq, Vector3 axis)
        {
            Vector3 ra = new Vector3(dq.x, dq.y, dq.z);
            Vector3 proj = Vector3.Project(ra, axis);
            float s = Vector3.Dot(proj, axis);                 // signed sin(theta/2) about axis
            float ang = 2f * Mathf.Atan2(s, dq.w) * Mathf.Rad2Deg;
            if (ang > 180f) ang -= 360f; else if (ang < -180f) ang += 360f;
            return ang;
        }

        private static Vector3 RotateAround(Vector3 point, Vector3 pivot, Vector3 axis, float deg)
            => pivot + Quaternion.AngleAxis(deg, axis) * (point - pivot);

        private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
            => v.sqrMagnitude > 1e-8f ? v.normalized : fallback.normalized;

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
            try { RestoreFlags(); } catch { }
            _kind = Kind.None; _dial = null; _lever = null; _prevGrab = false;
        }
    }
}
