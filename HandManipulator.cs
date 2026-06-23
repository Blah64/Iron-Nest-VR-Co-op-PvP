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
    ///   actually turns/slides and the turret reacts, and the hand is stuck RIGIDLY to the control's transform.
    ///   A LEVER follows the controller pointer (<see cref="CockpitInteractor"/> keeps its raycast camera pinned
    ///   down the holding controller — the right pointer cam for a right-hand grab, the LEFT for a left-hand one).
    ///   A DIAL instead follows the hand's POSITION orbiting the dial centre: we own a dedicated raycast camera
    ///   (<c>_dialCam</c>) aimed so the native drag's pointer-on-plane point tracks where the controller is around
    ///   the axis — so the dial turns like a grabbed knob, not from where the laser points. CockpitInteractor skips
    ///   repointing the held dial (<see cref="HeldDialTransform"/>) so that aim sticks.
    ///
    /// • CLICK switches/buttons (<c>LookAtTarget</c>): there is no drag to ride, so we let you PHYSICALLY
    ///   OPERATE them — the hand follows the controller's travel from the grab point, and once it moves past
    ///   <see cref="Config.SwitchThrowDistance"/> we fire the control's click the same way the cursor manager
    ///   does (<c>HandleClickDown/UpFromManager</c>). Moving back re-arms it. (Skips <c>PickUpZoomTarget</c>
    ///   manuals — those stay reposition-grabs owned by <see cref="GrabManager"/>.)
    ///
    /// EITHER hand can grab (both controllers have an aim ray). One control at a time; the right hand wins a
    /// tie. For a left-hand LEVER drag the held lever's raycast camera must follow the LEFT controller — see
    /// <see cref="LeftHeldControlTransform"/>, which CockpitInteractor reads to pin that lever to the left cam.
    /// A grabbed DIAL (either hand) drives itself from its own <c>_dialCam</c> instead, so it isn't reported there.
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

        // Dedicated raycast camera for a grabbed DIAL. The native dial drag computes the dial's angle from the
        // pointer point on the dial plane (GetPointerWorldPointOnDialPlane = this camera's centre ray ∩ plane).
        // We re-aim it every frame so that point's ANGLE around the axis tracks a RIGID grab of the controller —
        // the dial turns like a grabbed knob, not from the laser aim. We own it; CockpitInteractor excludes the
        // held dial from its periodic raycast-camera repoint (HeldDialTransform) so this assignment stays put.
        private GameObject _dialCamGo;
        private Camera _dialCam;
        private const float DialPointRadius = 0.1f; // synthetic pointer radius on the dial plane (its ANGLE, not R, drives the dial)
        private const float DialCamBack = 0.25f;     // how far off the plane the camera sits, looking back along the axis at the point

        // RIGID dial drive state. We accumulate a synthetic pointer angle from TWO contributions, both measured
        // about the dial axis in a fixed plane basis: (1) TWIST — the controller's roll about the axis, tracked
        // via a controller-local reference direction captured ⊥-axis at grab (distance-independent, so a far
        // laser-grab doesn't amplify it); (2) ORBIT — the controller position's angle around the dial centre.
        // Summing them = "both twisting and moving the hand turn the dial," the rigid-grab feel. Fed to the
        // native drag as a point at DialPointRadius and DialPointRadius·angle, which it reads as deltas.
        private Vector3 _dialRefLocal;        // controller-local ⊥-axis reference dir (captured at grab) → twist
        private float _dialSynthAngleDeg;     // accumulated synthetic pointer angle handed to the native drag
        private float _dialPrevTwistDeg;      // previous frame's twist reference angle about the axis
        private float _dialPrevOrbitDeg;      // previous frame's controller-position orbit angle about the axis
        private bool _dialDriveInit;          // false until the first drive frame seeds the prev angles

        public bool Active => _kind != Kind.None;

        // Which hand currently holds a control (dial/lever/switch) — 0 none, 1 left, 2 right. CockpitInteractor
        // reads this to hide that hand's pointing laser while it's operating something.
        public int HeldHand => _kind != Kind.None ? _hand : 0;

        // The LEVER currently held by the LEFT hand (else null). CockpitInteractor pins this control's own
        // raycast camera to the LEFT pointer cam each frame so its native drag follows the left controller
        // (the periodic repoint otherwise pins every control to the right cam). Switches don't need it (their
        // activation is camera-independent); a DIAL is NOT reported here because we drive it from its own
        // dedicated <c>_dialCam</c> (orbit follow) instead — see <see cref="HeldDialTransform"/>.
        public Transform LeftHeldControlTransform =>
            (_hand == 1 && _kind == Kind.Lever && _lever != null) ? _lever.transform : null;

        // The dial currently held by EITHER hand (else null). CockpitInteractor reads this to SKIP its periodic
        // raycast-camera repoint on that dial, so our dedicated orbit-drive camera (_dialCam) stays assigned.
        internal Transform HeldDialTransform => (_kind == Kind.Dial && _dial != null) ? _dial.transform : null;

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
            // Drive the dial from a RIGID grab of the controller (twist about the axis + orbit of the hand),
            // not the laser aim: aim the dial's own raycast camera through a synthetic pointer whose angle we
            // accumulate from the controller's motion. Capture the ⊥-axis twist reference in the controller's
            // frame, then pose the cam BEFORE BeginDialDrag so the drag's start angle is captured here (no
            // frame-0 jump). CockpitInteractor won't repoint the held dial (HeldDialTransform).
            EnsureDialCam();
            GetWorld(origin, HandGripPose(hand, input), out Vector3 gp, out Quaternion gr);
            Vector3 axis0 = dial.transform.TransformDirection(dial.rotationAxis);
            if (axis0.sqrMagnitude < 1e-8f) axis0 = dial.transform.forward;
            PlaneBasis(axis0.normalized, out Vector3 u0, out _);
            _dialRefLocal = Quaternion.Inverse(gr) * u0;   // controller-local ⊥-axis reference for twist tracking
            _dialDriveInit = false;                         // first DriveDialCam seeds the prev angles
            _dialSynthAngleDeg = 0f;
            DriveDialCam(dial, gp, gr);
            if (_dialCam != null) dial.raycastCamera = _dialCam;
            try { dial.BeginDialDrag(); } catch (Exception e) { Log.LogWarning("[manip] BeginDialDrag: " + e.Message); }
            hands.SetGrab(hand == 2, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed dial '{dial.name}' ({(hand == 2 ? "right" : "left")}, rigid drag).");
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

            // DIAL: re-aim its dedicated raycast camera each frame so the native drag's pointer angle tracks the
            // rigid grab (controller twist about the axis + hand orbit) — the dial follows the controller's
            // motion, not where the laser points. (LEVER keeps the controller-pointer drive via CockpitInteractor.)
            if (_kind == Kind.Dial && _dial != null)
            {
                GetWorld(origin, HandGripPose(_hand, input), out Vector3 gp, out Quaternion gr);
                DriveDialCam(_dial, gp, gr);
                if (_dialCam != null && _dial.raycastCamera != _dialCam) _dial.raycastCamera = _dialCam;
            }

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
            if (_kind == Kind.Dial && _dial != null)
            {
                try { _dial.EndDialDrag(); } catch { }
                // Hand the dial's raycast camera back to the cursor pointer cam so the laser/hover work again.
                // (CockpitInteractor's periodic repoint would also restore it within ~0.5s once it's no longer held.)
                try { var c = Cockpit != null ? Cockpit.ActiveCursorCam : null; if (c != null) _dial.raycastCamera = c; } catch { }
            }
            try { if (_kind == Kind.Lever && _lever != null) _lever.EndSliderDrag(); } catch { }
        }

        // ---------------- dial orbit-drive camera ----------------

        // Lazily create the dedicated camera that drives a grabbed dial. Renders nothing (cullingMask 0,
        // clearFlags Nothing) but stays enabled so ScreenPointToRay has a valid viewport — same setup as the
        // CockpitInteractor pointer cams. Unique depth (eye cams ≈ main-10, pointer cams -100/-101, scope -97.5):
        // the game's Ultimate LOD System spams a per-frame LogError when two active cameras share a depth.
        private void EnsureDialCam()
        {
            if (_dialCamGo != null) return;
            _dialCamGo = new GameObject("IronNestVR_DialCam");
            UnityEngine.Object.DontDestroyOnLoad(_dialCamGo);
            _dialCamGo.hideFlags = HideFlags.HideAndDontSave;
            _dialCam = _dialCamGo.AddComponent<Camera>();
            _dialCam.clearFlags = CameraClearFlags.Nothing;
            _dialCam.cullingMask = 0;
            _dialCam.depth = -102f;
            _dialCam.nearClipPlane = 0.01f;
            _dialCam.fieldOfView = 60f;
        }

        // Re-aim the dial-drive camera so the native drag reads the RIGID-grab angle. We accumulate a synthetic
        // pointer angle from the controller's TWIST about the axis (its roll, via the ⊥-axis reference captured
        // at grab — distance-independent) plus its ORBIT (the controller position's angle around the dial centre),
        // both measured in a fixed plane basis and summed as per-frame deltas. The camera is then placed off the
        // plane along +axis, looking back along -axis at the synthetic point, so its centre ray ∩ plane = that
        // point — and the native drag turns the dial by that point's angle.
        private void DriveDialCam(DialInteractable dial, Vector3 ctrlPos, Quaternion ctrlRot)
        {
            if (_dialCam == null || dial == null) return;
            Transform t = dial.transform;
            if (t == null) return;
            Vector3 axis = t.TransformDirection(dial.rotationAxis);
            if (axis.sqrMagnitude < 1e-8f) axis = t.forward;
            axis = axis.normalized;
            Vector3 center = t.position;
            PlaneBasis(axis, out Vector3 u, out Vector3 v);

            float twist = AngleOnPlane(ctrlRot * _dialRefLocal, axis, u, v);   // controller roll about the axis
            float orbit = AngleOnPlane(ctrlPos - center, axis, u, v);          // controller position around the centre

            if (!_dialDriveInit)
            {
                _dialPrevTwistDeg = twist; _dialPrevOrbitDeg = orbit; _dialDriveInit = true;
            }
            else
            {
                if (!float.IsNaN(twist) && !float.IsNaN(_dialPrevTwistDeg)) _dialSynthAngleDeg += WrapDeg(twist - _dialPrevTwistDeg);
                if (!float.IsNaN(orbit) && !float.IsNaN(_dialPrevOrbitDeg)) _dialSynthAngleDeg += WrapDeg(orbit - _dialPrevOrbitDeg);
                if (!float.IsNaN(twist)) _dialPrevTwistDeg = twist;
                if (!float.IsNaN(orbit)) _dialPrevOrbitDeg = orbit;
            }

            float a = _dialSynthAngleDeg * Mathf.Deg2Rad;
            Vector3 p = center + (Mathf.Cos(a) * u + Mathf.Sin(a) * v) * DialPointRadius;
            Vector3 camPos = p + axis * DialCamBack;
            Vector3 up = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            _dialCam.transform.SetPositionAndRotation(camPos, Quaternion.LookRotation(-axis, up));
        }

        // A stable orthonormal basis (u, v) spanning the plane ⊥ to <paramref name="axis"/>. Deterministic from
        // axis, so it's consistent frame-to-frame (the axis world direction is invariant under the dial's own
        // rotation about it), which keeps the accumulated angle deltas meaningful.
        private static void PlaneBasis(Vector3 axis, out Vector3 u, out Vector3 v)
        {
            u = Vector3.Cross(axis, Vector3.up);
            if (u.sqrMagnitude < 1e-6f) u = Vector3.Cross(axis, Vector3.right);
            u = u.normalized;
            v = Vector3.Cross(axis, u);   // unit (axis ⟂ u, both normalized)
        }

        // Signed angle (deg) of a world vector around <paramref name="axis"/>, measured in the (u, v) basis.
        // Returns NaN if the vector is (near) parallel to the axis — caller skips that contribution this frame.
        private static float AngleOnPlane(Vector3 w, Vector3 axis, Vector3 u, Vector3 v)
        {
            Vector3 p = w - Vector3.Dot(w, axis) * axis;
            if (p.sqrMagnitude < 1e-10f) return float.NaN;
            return Mathf.Atan2(Vector3.Dot(p, v), Vector3.Dot(p, u)) * Mathf.Rad2Deg;
        }

        // Wrap an angle delta to (-180, 180] so a frame's accumulation never jumps a full turn.
        private static float WrapDeg(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            else if (a < -180f) a += 360f;
            return a;
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
            if (_dialCamGo != null) { try { UnityEngine.Object.Destroy(_dialCamGo); } catch { } _dialCamGo = null; _dialCam = null; }
            _kind = Kind.None; _hand = 0; _dial = null; _lever = null; _prevGrabR = _prevGrabL = false;
            _switch = null; _switchInteractable = null; _switchLatched = false;
        }
    }
}
