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
    ///   OPERATE them. The hand follows the controller's travel from the grab point, AND the switch mesh itself
    ///   moves with it — driven by a per-switch <see cref="SwitchMotion"/> (rotate/slide, local axis, range,
    ///   activation push direction) kept in <see cref="SwitchMotions"/>: auto-seeded on first grab, tunable in
    ///   the VR menu (operating on the last-grabbed switch), and persisted. While held we disable the control's
    ///   own Animator so our drive isn't overwritten (it runs after Update — same reason the watch/clipboard
    ///   disable theirs), restoring it on release so the game shows the new on/off state. Once the push passes
    ///   the throw threshold we fire the control's click the way the cursor manager does
    ///   (<c>HandleClickDown/UpFromManager</c>); pushing back re-arms it. (Skips <c>PickUpZoomTarget</c>
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

        // The switch mesh we physically move + its captured rest pose, the per-switch motion model, the control's
        // own Animator (disabled while we drive so it can't overwrite us; restored on release), and the latest
        // throw progress (0..1). _switchKey identifies the active grab in the registry; _selSwitchKey is the
        // last-grabbed switch and PERSISTS after release so the menu can tune it.
        private Transform _switchRef;   // frame the throw travel + push direction live in (animator root, else LAT node)
        private SwitchMotion _switchMotion;
        private Animator _switchAnimator;
        private bool _switchAnimatorWasEnabled;
        private float _switchProgress;
        private string _switchKey;
        private string _selSwitchKey;
        // Scrub-cache key. DISTINCT from _switchKey: many physical controls share a display name ('Universal Button' is
        // used by every floor hatch AND the requisition-console submit/raise levers), so keying the learn cache by name
        // alone cross-contaminates them — grabbing the console lever found the HATCH's cached drivers and never learned its
        // own geometry. We append the control's instance id so each physical control caches independently, while _switchKey
        // stays the bare name for SwitchMotions persistence + the menu (which ARE meant to share across same-named controls).
        private string _scrubKey;

        // SCRUB the game's OWN animation with the hand: we can't pre-read the authored pose (manual Animator.Update
        // writes nothing in this build), but the engine's normal animation phase DOES apply a Played state — so we
        // freeze the animator (speed=0), find the press/flip state, and Play it at normalizedTime = throw progress
        // each frame. The real handle then tracks the hand. On activation we restore speed and fire the real click.
        private bool _switchScrubDone;

        // LEARN-THEN-SCRUB. We can't replay the control's animation (neither AnimationClip.SampleAnimation nor a
        // manual/forced Animator.Play applies in this build — the cap is moved by a ParentConstraint whose source the
        // Animator drives), but the game DOES move real child nodes when the click fires. So on the FIRST grab of a
        // control we LEARN: leave the animator enabled, watch the rig through the real click, and snapshot EVERY moved
        // node's pressed local pose at the most-deflected frame. On LATER grabs we SCRUB: disable the animator + each
        // moved node's ParentConstraint and hand-drive all of them rest→pressed by throw progress (manual Transform
        // writes always apply once the constraint is off), re-enabling everything at activation. Cache is per control
        // name, session-lived. We capture the WHOLE moved set (not just the max-delta node) so the entire lever/bat
        // follows the hand, not only the tip light (which travels furthest from the pivot).
        // T = the LIVE transform captured at learn (cache is session-lived, so the object is still valid on later grabs —
        // direct refs eliminate name-resolution flakiness). Leaf/RestPos kept only as a fallback resolver if T dies.
        private struct ScrubNode { public Transform T; public string Leaf; public Vector3 RestPos, PressPos; public Quaternion RestRot, PressRot; }
        private static readonly System.Collections.Generic.Dictionary<string, ScrubNode[]> _scrubCache = new System.Collections.Generic.Dictionary<string, ScrubNode[]>();

        // SCRUB pass: the resolved nodes we hand-drive + their rest/pressed local poses + their (disabled) constraint.
        private struct ActiveMover { public Transform T; public Vector3 RestPos, PressPos; public Quaternion RestRot, PressRot; public UnityEngine.Animations.ParentConstraint Con; public bool ConWasEnabled; public Vector3 LastWrote; public float MaxDrift; public bool WroteOnce; public Vector3 StartLocal; public Quaternion StartLocalR; public float MoveMax; public float CachedGap; public Vector3 TgtPos; public Quaternion TgtRot; public Vector3 StartRL; public float WorldMove; }
        private System.Collections.Generic.List<ActiveMover> _activeMovers;
        // Every Animator that drives a mover (the LookAtTarget's AND any separate one, e.g. the hatch/lever's own) —
        // disabled while scrubbing so it can't re-pose the lever each frame, restored on activation/release.
        private struct AnimEntry { public Animator A; public bool WasEnabled; }
        private System.Collections.Generic.List<AnimEntry> _scrubAnimators;
        private bool _scrubManual;
        private const bool ScrubDebugOscillate = false;  // DEBUG: held switch cycles rest↔press on a timer, no activation
        private static readonly bool HeldStickFollow = true;  // point the held stick's '…Parent' hinge at the hand each frame
        private bool _scrubOpening;    // this grab drives toward PRESS (true) or back toward REST (false)
        private float _scrubProgMax;   // DIAG: peak throw progress reached during a scrub grab

        // HELD-LEVER FOLLOW: the captured lever node is usually rolled about its OWN long axis by the game's clip — on a
        // smooth cylinder that's invisible (origin stays put, world≈0), which is why "the lever stays frozen" while the
        // hatch/locks/lights (real translation) move. Replaying that roll can't help; it's invisible in-game too. So for
        // the one most-lever-like captured node we DON'T replay — we POINT IT AT THE HAND: rotate it about its pivot so
        // its arm (pivot→tip) tracks the hand, clamped to the mechanism's own throw angle. Driven by real hand position,
        // so it's guaranteed visible. Everything else keeps the captured replay (those already move correctly).
        private Transform _leverT;           // the held stick (grabbed node or its visible parent); null => no synthetic swing
        private Transform _leverParent;      // its parent — we work in parent-local so a turret rotation doesn't skew it
        private Vector3 _leverPivotLocal;    // stick localPosition at grab (its origin is the pivot we swing about)
        private Quaternion _leverRestLocalR; // stick localRotation at grab (the swing is measured off this)
        private Vector3 _leverArmParentDir;  // pivot→grabPoint direction in PARENT-local space (rebuilt to world each frame)
        private Vector3 _leverGrabLocal;     // the grab point in STICK-local space (a fixed point on the stick we aim at the hand)
        private float _leverMaxAngle;        // clamp the swing so the stick can't fold through itself
        // BURIED mechanism lever (driven by a captured lever-mover or the grabbed node itself, not a named ancestor hinge):
        // one-directional from rest (no bidirectional toggle) and clamped to its real captured throw, so it can't be pushed
        // the wrong way or past its stop the way the unbounded ±70° toggle hinges can.
        private bool _leverBuried;
        // ORB-STICK ARCHETYPE (punchcard 'Locking Lever', charge rammer 'BigLever.001'): the lever the user holds by an
        // orb-cap, found by tier 3a/4. These have NO captured clip motion (the game drives them natively), so the hinge
        // axis can't come from a capture — the cross-product inference snapped to the lever's LONG axis and merely TWISTED
        // the rod in place (only the offset orb appeared to move). For these we lock a STABLE axis perpendicular to the rod
        // and allow the swing BOTH ways (bidirectional), so the visible stick actually sweeps toward the hand.
        private bool _leverStickArchetype;
        // DIAG: paint the driven node red / rider green on grab so the user can confirm WHICH mesh we move. Flip off to ship.
        private const bool TintHeldStick = false;
        private struct TintEntry { public Renderer R; public Color C; }
        private System.Collections.Generic.List<TintEntry> _tints;
        // FORCE-ENABLE: the punchcard handle ('BigLever') is GPU-instanced — its per-object renderers are DISABLED (the
        // 'GPUGridInstancer_Animated' draws them), so co-moving its transform shows nothing. Enable its renderers for the
        // grab so the real mesh draws at our co-moved pose; restore on release. Saved (Renderer, wasEnabled) pairs.
        private struct RendEntry { public Renderer R; public bool En; }
        private System.Collections.Generic.List<RendEntry> _forcedRends;
        // FORCE-ACTIVATE: the punchcard handle subtree ('Locking Lever'/'BigLever') is an INACTIVE GameObject (active=False)
        // — a renderer-enable can't show it. SetActive(true) on it (+ inactive ancestors) for the grab so the real mesh
        // renders at our co-moved pose; restore on release. Saved (GameObject, wasActiveSelf) pairs.
        private struct ActEntry { public GameObject GO; public bool Was; }
        private System.Collections.Generic.List<ActEntry> _forcedActive;
        private bool _capSlideFollow;         // tier-4 charge-rammer cap: drive the cap 1:1 and sync the scrub movers to its travel
        private float _leverFollowFrac;       // 0..1 progress of the held stick's own swing/slide this frame (paces the rider + synced movers)
        // RIDER: a tip mesh that physically RIDES the swung stick but lives OUTSIDE its subtree (charge rammer's orb-light
        // 'Cylinder.001' is under the grabbed button, not under '.PowderRamLever'), so it won't move when we rotate the
        // stick. We RIGIDLY CO-MOVE it with the stick's exact transform delta this frame (rotate about the hinge / slide),
        // so it stays glued to the stick tip — driving it by its own captured travel made it decouple and fly off.
        private Transform _riderT, _riderParent;
        private Vector3 _riderRestLocal, _riderSlideAxisW; private float _riderTravel;
        private Vector3 _riderRestPosW; private Quaternion _riderRestRotW;   // orb world pose at grab — co-moved with the stick delta
        private UnityEngine.Animations.ParentConstraint _riderCon; private bool _riderConWasActive;
        // EXTRA RIDERS: the WHOLE handle is a small cluster of nearby meshes (stick + orb + connector), each constraint-
        // glued by the game and re-posed every frame, so co-moving only ONE leaves the others dead (console: stick swings
        // but its orb stays; punchcard: orb slides but its connector stays). Co-move EVERY nearby constraint-driven visible
        // handle part rigidly with the stick's delta. Captured world pose at grab; their constraints disabled, restored on release.
        private struct RiderX { public Transform T; public Vector3 RestPosW; public Quaternion RestRotW; public UnityEngine.Animations.ParentConstraint Con; public bool ConWasActive; }
        private System.Collections.Generic.List<RiderX> _riders;
        // The stick's transform delta THIS frame, world space, for DriveRider to apply to the rider: a rotation _leverSwingDqW
        // about _leverPivotW (rotate path), or a translation _leverSlideDeltaW (slide path).
        private Vector3 _leverPivotW; private Quaternion _leverSwingDqW = Quaternion.identity; private Vector3 _leverSlideDeltaW;
        private UnityEngine.Animations.ParentConstraint _leverCon; // a constraint pinning the stick (disabled while held), if any
        private bool _leverConWasActive;
        private Vector3 _leverTip0W;          // grab-point world position at grab (for the visible-travel readout)
        private float _leverTipTravel;        // DIAG: peak world travel of the grab point during the grab (confirms it moved)
        // SLIDE vs ROTATE: most levers rotate about their hinge, but some controls (War Horn pulley) TRANSLATE. Honour the
        // per-control Translate flag (VR menu, persisted), auto-defaulted from a name hint. Slide axis locks after the
        // first real shove (like PushLocal) so the handle slides along one line instead of drifting in 3D.
        private bool _leverSlide;
        private float _leverSlideMax;        // max slide distance (m): captured throw for a captured mover, else menu/auto
        private Vector3 _leverSlideAxisW; private bool _leverHasSlideAxis;
        // ROTATE: lock the swing PLANE from the first real movement so the lever swings about one fixed hinge axis,
        // instead of a free point-at-hand that tilts about the wrong axis when the hand drifts sideways.
        private Vector3 _leverRotAxisW; private bool _leverHasRotAxis;
        // FIRE-ON-RELEASE: while a stick is hand-followed, don't fire the click mid-pull (the game's animation would rip
        // it out of your hand) — arm it when you pull past the threshold and fire when you LET GO, so the whole pull is
        // hand-driven and the scripted animation only plays after the hand is gone.
        private bool _leverArmed;


        // LEARN pass: animator-root descendants + rest poses, and at the most-deflected (global-peak) frame the whole
        // rig's snapshot + per-node delta — so we cache every node that actually moved on the click.
        private Transform[] _scrubTs; private Vector3[] _scrubRestP; private Quaternion[] _scrubRestR;
        private float[] _scrubNodePeak; private Vector3[] _scrubNodePeakPos; private Quaternion[] _scrubNodePeakRot;
        private float _scrubGlobalPeak;
        // LEARN TAIL: the click fires mid-grab, but the triggered animation keeps playing AFTER you release. To capture
        // its FULL extent (not just however far it got before you let go), we keep sampling for a short tail post-release.
        private bool _learnTriggered;   // the learn grab actually fired the click → the animation played, worth keeping
        private bool _learnTail; private float _learnTailUntil; private string _learnTailKey;
        private const float LearnTailSeconds = 2.0f;
        private const float ScrubLearnMin = 0.02f;   // a node must move at least this to be REPORTED as a real mover
        private const float ScrubKeepMin = 0.003f;    // but cache the whole chain down to this — sub-threshold ANCESTORS
                                                      // still carry composition (a child rotates around them), so dropping
                                                      // them loses the visible swing. Direct refs make the wider set cheap.
        private const int ScrubRootClimb = 1;         // fallback parents to climb if no Animator ancestor is found
        // DIAG arrays, parallel to _scrubTs: root-local position at grab + peak root-local travel during the learn click,
        // so we can see each node's WORLD (in-assembly) displacement, not just its local-pose delta.
        private Vector3[] _scrubRestRL; private float[] _scrubWorldPeak; private Transform _scrubRoot;

        // AUTHORED motion: the actual transform(s) the control's animation clip moves, with their rest (grab-time)
        // and activated-end local poses. We reproduce that exact motion by lerping rest→on with the throw
        // progress, so the real handle/bat moves (not the LookAtTarget node, which often only carries lights).
        // Null => fall back to the manual rotate/slide model above. _selAuthored mirrors it for the menu.
        private struct SwMover { public Transform T; public Vector3 RestPos, OnPos; public Quaternion RestRot, OnRot; }
        private System.Collections.Generic.List<SwMover> _switchMovers;
        private bool _selAuthored;

        // One-time-per-name diagnostic dump of a grabbed switch's hierarchy + components, so we can see what its
        // moving part actually is (and what drives it) when the authored-motion read comes up empty.
        private readonly System.Collections.Generic.HashSet<string> _diagDumped = new System.Collections.Generic.HashSet<string>();

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
        private const float DialPointRadius = 0.1f;     // synthetic pointer radius on the dial plane (its ANGLE, not R, drives the dial)
        private const float DialCamBack = 0.25f;        // how far off the plane the camera sits, looking back along the axis at the point
        private const float DialMinGrabRadius = 0.03f;  // floor for the grab arm length, so a grab AT the pivot isn't hypersensitive

        // RIGID dial drive state. The grabbed point is reconstructed each frame from the controller's RIGID motion
        // at the captured grab radius (NOT the far laser-reach): rotate the grab arm by the controller's rotation
        // (TWIST about the axis, 1:1) and add its translation (CRANK tangentially, scaled by the grab radius). The
        // arm length = the grab point's distance from the dial pivot, so translation actually turns the dial in
        // proportion to how a real grab there would (the "orbit" that was missing when measured off the far grip),
        // and big-radius wheels crank gently while small knobs respond to small motions. We feed the native drag a
        // synthetic point at the accumulated angle, which it reads as deltas.
        private Vector3 _dialArmLocal;        // grab arm (pivot→grab point, on the plane), fixed in the controller frame at grab
        private Vector3 _dialGrabCtrlPos;     // controller position at grab — translation since then is the crank
        private float _dialSynthAngleDeg;     // accumulated synthetic pointer angle handed to the native drag
        private float _dialPrevAngleDeg;      // previous frame's reconstructed grab-point angle about the axis
        private bool _dialDriveInit;          // false until the first drive frame seeds the prev angle

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
                // LEARN TAIL: keep sampling a just-triggered animation after release until it has played out, then cache.
                if (_learnTail)
                {
                    SampleLearnFrame();
                    if (Time.time >= _learnTailUntil) FinalizeLearnTail();
                }

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
                ClearSwitchGrab();
                _kind = Kind.None; _hand = 0; _dial = null; _lever = null;
                try { hands.ClearGrab(true); } catch { }
                try { hands.ClearGrab(false); } catch { }
            }
        }

        // Re-assert the held STICK's pose in LateUpdate — AFTER the game's own Update/animator/interaction scripts have run
        // this frame — so the end-of-frame eye render sees OUR swing, not the game's. The held-prop holders do the same via
        // GrabManager.LateApply. This is why a confirmed-correct lever node ('.Hatch lever.002') moved in our Update log yet
        // showed "no change": the game re-poses the very nodes it animates (the lever meshes) after our Update. The working
        // '*Parent'-hinge controls don't need it (the game doesn't touch the parent), but re-applying their identical pose
        // here is harmless. Runs only while actively holding a click-switch lever; _handPos is this frame's cached grab.
        public void LateApply()
        {
            try
            {
                if (_kind != Kind.Switch || _switch == null || _leverT == null) return;
                DriveHeldStick();
            }
            catch (Exception e) { Log.LogWarning("[manip] LateApply " + e.Message); }
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
            {
                DumpGripMiss(ao, dir);   // grip pressed but no grabbable control on the ray — log what it DID hit
                return;
            }

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
            if (Config.SwitchGrabEnabled)
            {
                var pz = FindUp<PickUpZoomTarget>(t);
                if (pz != null)
                {
                    // DIAG: a control we hand off to GrabManager — these never enter the follow system, which is why the
                    // punchcard submit / raise-review-console lever don't track (the game repositions them). Log so we can
                    // confirm which deferred controls are PickUpZoomTarget manuals (the follow can't drive them as-is).
                    var lat0 = FindUp<LookAtTarget>(t);
                    Log.LogInfo($"[manip] grip over manual '{SafeName(pz.transform)}' (PickUpZoomTarget — GrabManager owns it, follow skipped; LAT={(lat0 != null ? SafeName(lat0.transform) : "—")}).");
                }
                else
                {
                    var sw = FindUp<LookAtTarget>(t);
                    if (sw != null) EngageSwitch(hand, sw, hit.point, input, origin, hands);
                }
            }
        }

        private void EngageDial(int hand, DialInteractable dial, Vector3 hitPoint, VrInput input, Transform origin, HandVisuals hands)
        {
            _dial = dial;
            _kind = Kind.Dial;
            _hand = hand;
            StickTo(hand, dial.transform, hitPoint, input, origin);
            // Drive the dial from a RIGID grab of the controller (twist about the axis + translation crank at the
            // grab radius), not the laser aim: aim the dial's own raycast camera through a synthetic pointer whose
            // angle we accumulate from the controller's motion. Capture the grab arm (pivot→grab point, on the
            // dial plane) in the controller's frame + the controller position, then pose the cam BEFORE
            // BeginDialDrag so the drag's start angle is captured here (no frame-0 jump). CockpitInteractor won't
            // repoint the held dial (HeldDialTransform).
            EnsureDialCam();
            GetWorld(origin, HandGripPose(hand, input), out Vector3 gp, out Quaternion gr);
            Vector3 axis0 = dial.transform.TransformDirection(dial.rotationAxis);
            if (axis0.sqrMagnitude < 1e-8f) axis0 = dial.transform.forward;
            axis0 = axis0.normalized;
            Vector3 arm0 = hitPoint - dial.transform.position;
            arm0 -= Vector3.Dot(arm0, axis0) * axis0;       // project the grab arm onto the dial plane
            if (arm0.sqrMagnitude < DialMinGrabRadius * DialMinGrabRadius)
            {
                PlaneBasis(axis0, out Vector3 ub, out _);    // grabbed at/near the pivot: floor the arm so it isn't hypersensitive
                Vector3 dir = arm0.sqrMagnitude > 1e-10f ? arm0.normalized : ub;
                arm0 = dir * DialMinGrabRadius;
            }
            _dialArmLocal = Quaternion.Inverse(gr) * arm0;  // grab arm rigidly fixed in the controller frame
            _dialGrabCtrlPos = gp;                           // controller position at grab (translation since = crank)
            _dialDriveInit = false;                          // first DriveDialCam seeds the prev angle
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

            // We do NOT hand-drive the switch mesh, and we LEAVE THE GAME'S ANIMATOR ENABLED. Reproducing the
            // authored pose was a dead end in this IL2CPP build (AnimationClip.SampleAnimation no-ops on Mecanim
            // clips; a manually-Updated Animator writes no transforms even forced AlwaysAnimate), and DISABLING the
            // animator to hand-drive a fallback was the actual "switch stays static" bug — it both moved the wrong
            // node (the LookAtTarget carries the lights) AND blocked the real click animation. Instead: the hand
            // follows the controller through the throw, and crossing the threshold fires the REAL click — the game
            // then plays the control's OWN authored depress/flip. We keep the SwitchMotion only for the activation
            // push DIRECTION (auto-seeded from the first shove) and remember the switch for the menu.
            // The GENERIC control names ('Universal Button', 'Universal Switch Button') are reused by DOZENS of unrelated
            // controls (every floor hatch, the punchcard, the raise console…). SwitchMotion is persisted BY NAME, so a slide
            // seeded on one generic 'Universal Button' poisoned ALL of them — the raise-console lever (correct node) got
            // dragged into SLIDE and barely moved (0.002 m vs 0.283 m rotating). Disambiguate ONLY the generic names by the
            // PARENT name (stable across sessions, unlike InstanceID), so each physical control owns its motion. Uniquely
            // named controls (shell rammer etc.) keep the bare name → their saved menu settings still load (no regression).
            // The base game reuses the same control NAMES across physically distinct controls: every mirrored
            // left/right cannon control shares a name ('Universal Button Move Cylinder', 'Button Dispencer (N)',
            // shell/charge rammers — all under 'Gun System Left' vs 'Gun System Right'), and 'Universal Switch Button
            // Variant' is BOTH the power lever AND every review-console switch. Keying the SwitchMotion by name alone
            // made those distinct controls share ONE entry: editing one's axis in the menu changed the others, and one
            // control's captured push direction (in ITS local frame) leaked onto its mirror, whose throw then projected
            // too small to ever reach the activation threshold (the right cannon "grabbed but never fired"). Build a key
            // that's UNIQUE per physical control yet STABLE across sessions: the control's name plus a few ancestor names
            // (names don't change between runs; the side container / parent switch differs per physical control).
            _switchKey = BuildSwitchKey(sw);
            if (!SwitchMotions.Has(_switchKey) && TryGetLegacyMotion(sw, out var legacy))
            {
                // First time we've seen this per-control key: migrate any tuning saved under the OLD name-only key so a
                // deliberately-set slide/axis isn't lost — but RESET the captured push so each physical control (incl.
                // each mirrored side) seeds its OWN activation direction instead of inheriting a sibling's.
                legacy.HasPush = false; legacy.PushLocal = Vector3.zero;
                SwitchMotions.Set(_switchKey, legacy);
            }
            _selSwitchKey = _switchKey;
            int swId = 0; try { swId = sw.GetInstanceID(); } catch { swId = 0; }
            _scrubKey = _switchKey + "#" + swId;   // per-instance so same-named controls don't share a learn cache
            _switchMotion = SwitchMotions.Get(_switchKey);
            _switchProgress = 0f;
            _switchMovers = null;   // no mesh driving — ApplySwitchPose is a no-op while the mover set is null
            _selAuthored = false;

            try { _switchAnimator = sw.animator; } catch { _switchAnimator = null; }
            // Reference frame the throw travel + push direction are measured in (animator root, else the LAT node).
            _switchRef = _switchAnimator != null ? _switchAnimator.transform : sw.transform;

            // PROBE (one-time per control): dump the local hierarchy around the grabbed node so we can identify which node
            // is the visible STICK the player holds (the grabbed LookAtTarget is usually a logical button carrying lights).
            DumpStickCandidates(sw.transform, hitPoint);
            _switchAnimatorWasEnabled = _switchAnimator != null && _switchAnimator.enabled;

            // Finalize any pending learn-tail BEFORE starting a new grab, so its cache is ready if this is a scrub.
            if (_learnTail) FinalizeLearnTail();

            _switchScrubDone = false; _scrubManual = false; _activeMovers = null; _scrubAnimators = null;
            _leverT = null; _leverParent = null; _leverCon = null; _leverTipTravel = 0f;
            _scrubGlobalPeak = 0f; _scrubTs = null; _scrubProgMax = 0f; _scrubOpening = true; _learnTriggered = false;

            // MOVE-EVERYTHING model: capture the whole triggered animation once (learn), then on later grabs DRIVE every
            // node that the trigger actually animates and is a DRIVER (no follow-constraint), hand-paced by the pull, while
            // FOLLOWERS (dome lights etc. — they carry a ParentConstraint) are left ALONE so they ride their driver via the
            // constraint instead of being flung around on their own. Never reads constraint sources (that API crashes here).
            if (_switchAnimator != null)
            {
                if (_scrubCache.TryGetValue(_scrubKey, out var nodes) && nodes != null && nodes.Length > 0)
                {
                    // SCRUB pass: drive the DRIVERS (no follow-constraint) hand-paced; LEAVE followers (constrained) alone
                    // so they ride their driver. Disable only the drivers' animators so our writes hold.
                    var root = ScrubRoot(_switchAnimator.transform);
                    _scrubRoot = root;
                    _activeMovers = new System.Collections.Generic.List<ActiveMover>();
                    Transform[] all = null;   // lazily built only if a direct ref died (then fall back to name resolve)
                    int direct = 0, byname = 0;
                    // Decide the TOGGLE DIRECTION from the control's actual current state: if the strongest mover currently
                    // sits nearer its PRESSED pose (control left activated/open), this grab should drive it back toward REST
                    // (closing); otherwise toward PRESS (opening). Picked once, applied to all movers so they stay coherent.
                    bool opening = true; float bestTravel = -1f;
                    for (int k = 0; k < nodes.Length; k++)
                    {
                        var t0 = nodes[k].T; if (t0 == null) continue;
                        float travel = Vector3.Distance(nodes[k].RestPos, nodes[k].PressPos) + Quaternion.Angle(nodes[k].RestRot, nodes[k].PressRot) * 0.01f;
                        if (travel <= bestTravel) continue;
                        bestTravel = travel;
                        float dRest = Vector3.Distance(t0.localPosition, nodes[k].RestPos) + Quaternion.Angle(t0.localRotation, nodes[k].RestRot) * 0.01f;
                        float dPress = Vector3.Distance(t0.localPosition, nodes[k].PressPos) + Quaternion.Angle(t0.localRotation, nodes[k].PressRot) * 0.01f;
                        opening = dRest <= dPress;   // nearer rest → open it; nearer press → close it
                    }
                    for (int k = 0; k < nodes.Length; k++)
                    {
                        // Prefer the LIVE ref captured at learn (same session) — no name ambiguity. Fall back to name only
                        // if the object died (shouldn't, within a session).
                        Transform t = nodes[k].T;
                        if (t != null) { direct++; }
                        else { if (all == null) all = root.GetComponentsInChildren<Transform>(true); t = FindByLeafNear(all, nodes[k].Leaf, nodes[k].RestPos); if (t != null) byname++; }
                        if (t == null) continue;
                        // FOLLOWER? If this node carries a follow-constraint, it's pinned to a driver — driving it directly
                        // (as before) flings it off / moves unrelated lights. Skip it entirely: leave the constraint ENABLED
                        // so it rides whichever driver we move. (We never read the constraint's source — that API crashes.)
                        UnityEngine.Animations.ParentConstraint con = null;
                        try { con = t.GetComponent<UnityEngine.Animations.ParentConstraint>(); } catch { con = null; }
                        if (con != null) continue;
                        // gap = how far the node currently sits from cached REST (diagnostic for stale state).
                        float gap = Vector3.Distance(t.localPosition, nodes[k].RestPos) + Quaternion.Angle(t.localRotation, nodes[k].RestRot) * 0.01f;
                        // Target this grab drives TOWARD; lerp begins at the node's ACTUAL current pose (no snap).
                        Vector3 tgtP = opening ? nodes[k].PressPos : nodes[k].RestPos;
                        Quaternion tgtR = opening ? nodes[k].PressRot : nodes[k].RestRot;
                        Vector3 startRL = Vector3.zero; try { startRL = root.InverseTransformPoint(t.position); } catch { }
                        _activeMovers.Add(new ActiveMover { T = t, RestPos = nodes[k].RestPos, RestRot = nodes[k].RestRot, PressPos = nodes[k].PressPos, PressRot = nodes[k].PressRot, Con = null, ConWasEnabled = false, StartLocal = t.localPosition, StartLocalR = t.localRotation, CachedGap = gap, TgtPos = tgtP, TgtRot = tgtR, StartRL = startRL });
                    }
                    if (_activeMovers.Count > 0)
                    {
                        _scrubManual = true;
                        _scrubOpening = opening;
                        // Disable every animator that drives a mover — the LAT's AND any separate one (e.g. the lever's
                        // own), found via GetComponentInParent — so none of them re-pose the lever over our writes.
                        _scrubAnimators = new System.Collections.Generic.List<AnimEntry>();
                        DisableScrubAnimator(_switchAnimator);
                        for (int k = 0; k < _activeMovers.Count; k++)
                        {
                            Animator a = null; try { a = _activeMovers[k].T.GetComponentInParent<Animator>(true); } catch { }
                            DisableScrubAnimator(a);
                        }

                        var dbg = new System.Text.StringBuilder();
                        int show = Mathf.Min(_activeMovers.Count, 8);
                        for (int k = 0; k < show; k++) dbg.Append($" [{SafeName(_activeMovers[k].T)} con={_activeMovers[k].Con != null} gap={_activeMovers[k].CachedGap:0.00}]");
                        Log.LogInfo($"[manip] scrub resolve '{_switchKey}': {_activeMovers.Count}/{nodes.Length} ({direct} ref, {byname} name; {_scrubAnimators.Count} anim; {(opening ? "OPEN" : "CLOSE")}) —{dbg}");
                    }
                }
                else
                {
                    // LEARN pass: leave the animator enabled and snapshot the rig so DriveSwitch can record which nodes
                    // the real click moves (and their pressed poses) to cache for next time. Root a parent UP from the
                    // LookAtTarget so a handle that's a sibling/ancestor (outside the LAT's own subtree) is also seen —
                    // local-pose measurement is immune to the turret's rotation, so a wider root is safe.
                    try
                    {
                        var root = ScrubRoot(_switchAnimator.transform);
                        _scrubRoot = root;
                        _scrubTs = root.GetComponentsInChildren<Transform>(true);
                        int m = _scrubTs != null ? _scrubTs.Length : 0;
                        Log.LogInfo($"[manip] learn root '{_switchKey}': '{SafeName(root)}' ({m} nodes)");
                        DumpAncestors(_switchAnimator.transform);   // PROBE: where the animators/renderers live above us
                        DumpAnimators(root);                        // PROBE: every animator in the captured subtree + params
                        _scrubRestP = new Vector3[m]; _scrubRestR = new Quaternion[m];
                        _scrubNodePeak = new float[m]; _scrubNodePeakPos = new Vector3[m]; _scrubNodePeakRot = new Quaternion[m];
                        _scrubRestRL = new Vector3[m]; _scrubWorldPeak = new float[m];
                        for (int i = 0; i < m; i++)
                        {
                            _scrubRestP[i] = _scrubTs[i].localPosition; _scrubRestR[i] = _scrubTs[i].localRotation;
                            _scrubNodePeakPos[i] = _scrubRestP[i]; _scrubNodePeakRot[i] = _scrubRestR[i];
                            _scrubRestRL[i] = root.InverseTransformPoint(_scrubTs[i].position);  // root-local rest position
                        }
                    }
                    catch { _scrubTs = null; }
                }
            }

            // HELD-STICK FOLLOW: point the stick's '…Parent' hinge at the hand so the thing in your hand visibly follows.
            // Runs on EVERY grab (learn or scrub) — it needs only the grab geometry, not the captured mechanism, so the
            // stick follows on the very first grab. The captured movers (hatch/cylinders) replay separately on scrubs.
            SelectHeldStick(sw.transform, hitPoint);

            // PLAIN-CLICK FALLBACK: if no genuine in-hand stick was resolved (tiers 1-4 all declined) but we set up a SCRUB
            // of captured movers, those movers are the REMOTE mechanism this control actuates — the review console raises
            // its own hatch/ceiling machinery; the charge rammer drives a deep ram rod. Hand-pacing them swings a big far
            // object around the cockpit, which reads as "the wrong thing follows my hand." Worse than the game's own click
            // animation. So restore the animators we disabled and drop the scrub → the control fires a clean click instead.
            if (_leverT == null && _scrubManual)
            {
                Log.LogInfo($"[manip] held-stick follow: '{sw.name}' has no in-hand stick — restoring animators, plain click (no remote-mechanism follow).");
                try { RestoreScrubDrivers(); } catch { }
                _activeMovers = null; _scrubManual = false; _scrubAnimators = null;
            }

            GetWorld(origin, HandGripPose(hand, input), out Vector3 gp, out Quaternion gr);
            _switchGrabGripPos = gp;     // controller anchor — throw is measured from here
            _switchHandAnchor = hitPoint; // seed the hand on the switch; it rides the controller travel
            _switchLatched = false;
            _handPos = hitPoint;
            _handRot = gr;
            hands.SetGrab(hand == 2, _handPos, _handRot);
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[manip] grabbed switch '{sw.name}' ({(hand == 2 ? "right" : "left")}; {(_scrubManual ? $"scrub {_activeMovers.Count} drivers" : "learn")}).");
        }

        // Pick the animator state to scrub for a grabbed switch: the press/flip clip (by name score), short enough to
        // be the control's own motion (not a multi-second mechanism), and addressable as a state on layer 0. State
        // names equal clip names on these controllers (verified: HasState(StringToHash(clipName)) is true).
        private bool FindPressState(Animator anim, out int hash)
        {
            hash = 0;
            try
            {
                var rac = anim.runtimeAnimatorController; if (rac == null) return false;
                var clips = rac.animationClips; if (clips == null || clips.Length == 0) return false;
                float best = float.NegativeInfinity; int bh = 0; bool found = false;
                for (int c = 0; c < clips.Length; c++)
                {
                    var clip = clips[c]; if (clip == null) continue;
                    string cn = SafeName(clip);
                    float len = 0f; try { len = clip.length; } catch { }
                    float score = ClipNameScore(cn);
                    if (score <= -1000f || len <= 0.02f || len > 1.5f) continue;
                    int h; try { h = Animator.StringToHash(cn); } catch { continue; }
                    bool hs; try { hs = anim.HasState(0, h); } catch { hs = false; }
                    if (!hs) continue;
                    if (score > best) { best = score; bh = h; found = true; }
                }
                hash = bh; return found;
            }
            catch { return false; }
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

        // Click switch: the hand AND the switch mesh follow the controller's travel from the grab point along
        // this switch's activation push direction; the mesh moves per its SwitchMotion (rotate/slide). Once the
        // push passes the throw threshold we fire the click (re-arming when the hand returns, so a back-and-forth
        // toggles on then off within one grab).
        private void DriveSwitch(VrInput input, Transform origin, HandVisuals hands)
        {
            if (_switch == null || _switchRef == null) { Release(input, hands, "control gone"); return; }

            GetWorld(origin, HandGripPose(_hand, input), out Vector3 gp, out Quaternion gr);
            Vector3 worldTravel = gp - _switchGrabGripPos;
            _handPos = _switchHandAnchor + worldTravel; // hand follows the controller so it looks operated
            _handRot = gr;
            hands.SetGrab(_hand == 2, _handPos, _handRot);

            // Hand travel in the reference PARENT-local frame — the frame the motion axis / push direction live in —
            // so the motion is defined relative to the switch wherever you stand.
            Transform pspace = _switchRef.parent;
            Vector3 localTravel = pspace != null ? pspace.InverseTransformVector(worldTravel) : worldTravel;

            // Auto-seed the activation push direction from the first real shove, then persist it.
            if (!_switchMotion.HasPush && localTravel.magnitude >= Config.SwitchThrowDistance * 0.5f)
            {
                _switchMotion.PushLocal = localTravel.normalized;
                _switchMotion.HasPush = true;
                SwitchMotions.Set(_switchKey, _switchMotion);
            }

            float along = _switchMotion.HasPush ? Vector3.Dot(localTravel, _switchMotion.PushLocal) : localTravel.magnitude;
            // Followed levers are bidirectional toggles: pulling EITHER way from rest counts, so a control left in its
            // 'on' state can be pulled back the other way to turn it off (and vice-versa). PushLocal is locked to the
            // first-ever direction, so without abs the return pull reads as negative progress and never arms.
            if (_leverT != null && !_leverBuried) along = Mathf.Abs(along);   // buried mechanism levers are one-way, not toggles
            float progress = Mathf.Clamp(along / Mathf.Max(0.01f, Config.SwitchThrowDistance), 0f, 1f);
            _switchProgress = progress;
            if (progress > _scrubProgMax) _scrubProgMax = progress;

            // The held stick follows the hand on EVERY grab (learn or scrub) — independent of the mechanism replay below.
            DriveHeldStick();

            // A FOLLOWED lever also activates by how far the LEVER ITSELF actually swung/slid (its tip travel / deflection),
            // not only by hand travel projected onto the captured push direction. That auto-seeded PushLocal can land along
            // the hinge axis — the ONE direction the lever doesn't move — so the projection stays ~0 no matter how far you
            // pull (the right cannon's reload lever: push captured as -Z while the lever rotates ABOUT Z, so it never
            // reached threshold). Tip travel reflects the real deflection, so a deliberate pull fires regardless of a bad
            // push capture. Big levers deflect far (this path fires them); tiny toggles barely deflect (so it stays dormant
            // for them and they keep using the push projection above). Computed AFTER DriveHeldStick so it sees this frame.
            if (_leverT != null)
            {
                float leverThrow = Mathf.Max(0.04f, Config.SwitchThrowDistance * 2f);
                float byDeflect = Mathf.Clamp01(_leverTipTravel / leverThrow);
                if (byDeflect > progress) { progress = byDeflect; _switchProgress = progress; if (progress > _scrubProgMax) _scrubProgMax = progress; }
            }

            if (_scrubManual)
            {
                // SCRUB pass: hand-drive every learned node rest→pressed by progress (animator + constraints off).
                if (!_switchScrubDone && _activeMovers != null)
                {
                    for (int k = 0; k < _activeMovers.Count; k++)
                    {
                        var m = _activeMovers[k];
                        if (m.T == null) continue;
                        // The held stick (and everything rigidly under it) is driven separately (point-at-hand) — never also
                        // replay it as a mechanism mover, or the scrub loop's local-pose writes fight the hand-drive and the
                        // lever "tracks once then stops" on the 2nd grab. Only movers OUTSIDE the lever subtree (drums,
                        // needles, gears that animate on their own) are scrub-driven.
                        if (_leverT != null && (m.T == _leverT || IsDescendantOf(m.T, _leverT))) continue;
                        // DIAG: how far did the node drift from what we wrote last frame? ~0 = our write holds (so if the
                        // visual still doesn't move, we've got the wrong node); large = something re-poses it after Update.
                        if (m.WroteOnce)
                        {
                            float drift = Vector3.Distance(m.T.localPosition, m.LastWrote);
                            if (drift > m.MaxDrift) m.MaxDrift = drift;
                        }
                        // DEBUG OSCILLATE: ignore the throw and cycle the FULL rest↔press range on a slow timer while held,
                        // so we can see — independent of push speed / activation — whether these writes render at all.
                        // CAP-FOLLOW (charge rammer): the in-hand cap is slid 1:1 by DriveHeldStick; pace these movers (the
                        // ram arm/gears) by the cap's OWN travel fraction so they stay attached to it, not by the throw
                        // progress (which is 10×+ faster and would race the arm ahead of the hand-held cap).
                        float drive = ScrubDebugOscillate ? Mathf.PingPong(Time.time * 0.6f, 1f)
                                    : _capSlideFollow ? Mathf.Clamp01(_leverTipTravel / Mathf.Max(_leverSlideMax, 1e-3f))
                                    : progress;
                        Vector3 fromP = ScrubDebugOscillate ? m.RestPos : m.StartLocal;
                        Quaternion fromR = ScrubDebugOscillate ? m.RestRot : m.StartLocalR;
                        Vector3 toP = ScrubDebugOscillate ? m.PressPos : m.TgtPos;
                        Quaternion toR = ScrubDebugOscillate ? m.PressRot : m.TgtRot;
                        // Lerp from the node's ACTUAL pose at grab toward this grab's target (open→press / close→rest), so
                        // there's no snap when the control was left mid-state, and a re-grab reverses it like a real toggle.
                        Vector3 wp = Vector3.Lerp(fromP, toP, drive);
                        m.T.localPosition = wp;
                        m.T.localRotation = Quaternion.Slerp(fromR, toR, drive);
                        m.LastWrote = wp; m.WroteOnce = true;
                        // DIAG: travel from grab pose, in the node's own local frame AND in root-local (visible) space.
                        float mv = Vector3.Distance(m.T.localPosition, m.StartLocal) + Quaternion.Angle(m.T.localRotation, m.StartLocalR) * 0.01f;
                        if (mv > m.MoveMax) m.MoveMax = mv;
                        if (_scrubRoot != null)
                        {
                            float wmv = Vector3.Distance(_scrubRoot.InverseTransformPoint(m.T.position), m.StartRL);
                            if (wmv > m.WorldMove) m.WorldMove = wmv;
                        }
                        _activeMovers[k] = m;
                    }
                }
            }
            else if (_scrubTs != null)
            {
                SampleLearnFrame();
            }

            // DEBUG OSCILLATE: never auto-activate while scrubbing, so the lever keeps cycling for as long as you hold it.
            if (ScrubDebugOscillate && _scrubManual) { /* held → keep oscillating; release ends it */ }
            else if (!_switchLatched && progress >= 0.85f)
            {
                if (_leverT != null)
                {
                    // FOLLOW control: DON'T fire mid-pull — the game's animation would rip the lever out of your hand.
                    // Arm it (a haptic tick marks the activation point); the click fires on RELEASE, after the hand is gone.
                    if (!_leverArmed) { _leverArmed = true; input.Haptic(Config.HapticAmplitude, Mathf.Max(Config.HapticSeconds, 0.04f)); }
                    _switchLatched = true;
                }
                else
                {
                    // On the scrub pass, hand the animator + constraints back to the game so it shows the post-click state.
                    if (_scrubManual && !_switchScrubDone)
                    {
                        RestoreScrubDrivers();
                        _switchScrubDone = true;
                    }
                    Activate(_switch, _switchInteractable);
                    _switchLatched = true;
                    if (!_scrubManual) _learnTriggered = true; // learn fired the click → capture its tail after release
                    input.Haptic(Config.HapticAmplitude, Mathf.Max(Config.HapticSeconds, 0.04f));
                }
            }
            else if (_switchLatched && progress <= 0.30f)
            {
                _switchLatched = false;          // pushed back — ready to flip again
                if (_leverT != null) _leverArmed = false;  // pulled back before release → don't fire on release
            }
        }

        // Retained mesh-poser (currently a no-op: we no longer hand-drive switch meshes — see EngageSwitch — so the
        // authored mover set is always null). Kept as the single drive point if a future build scrubs the game's
        // animator instead.
        private void ApplySwitchPose(float progress)
        {
            if (_switchMovers == null) return;
            for (int i = 0; i < _switchMovers.Count; i++)
            {
                var m = _switchMovers[i];
                if (m.T == null) continue;
                m.T.localPosition = Vector3.Lerp(m.RestPos, m.OnPos, progress);
                m.T.localRotation = Quaternion.Slerp(m.RestRot, m.OnRot, progress);
            }
        }

        // Discover the switch's real moving parts from its Animator and record the motion to reproduce. We sample
        // the best (most-moving) clip at its start and activated end — SampleAnimation is span-free here, so it's
        // safe in this IL2CPP build — diff every descendant of the animator root to find which transforms the
        // clip actually moves, and store each mover's CURRENT (rest) pose + the clip's end pose. Driving then
        // lerps rest→end with the throw progress, so the genuine handle/bat travels exactly as the game authored
        // it (no axis/range guessing). Returns false (→ manual fallback) if there's no animator/controller/clip
        // or nothing moves. Restores the sampled transforms to rest before returning (no visible flicker).
        private bool TryBuildAuthoredMotion(LookAtTarget sw)
        {
            Animator anim = null;
            try { anim = sw.animator; } catch { }
            if (anim == null) return false;
            var rac = anim.runtimeAnimatorController;
            if (rac == null) return false;
            var clips = rac.animationClips;
            if (clips == null || clips.Length == 0) return false;

            var go = anim.gameObject;
            if (go == null) return false;
            var ts = go.GetComponentsInChildren<Transform>(true);
            if (ts == null || ts.Length == 0) return false;
            int n = ts.Length;

            // Rest = the live pose at grab time. A press/flip clip RETURNS to rest at its end (rest→pressed→rest),
            // so comparing endpoints reads ~zero — we scan each clip across its length for the PEAK-deflection frame
            // and capture the pressed/flipped pose there.
            var restP = new Vector3[n]; var restR = new Quaternion[n];
            for (int i = 0; i < n; i++) { var t = ts[i]; restP[i] = t.localPosition; restR[i] = t.localRotation; }

            // Remember the live animator state so we can put it back after probing (we Play/Update foreign states).
            int st0Hash = 0; float st0Nt = 0f; bool haveSt0 = false;
            try { var si = anim.GetCurrentAnimatorStateInfo(0); st0Hash = si.fullPathHash; st0Nt = si.normalizedTime; haveSt0 = true; } catch { }

            // A manually-Updated Animator in CullUpdateTransforms/CullCompletely writes NO transforms when its
            // renderers are deemed invisible → our probe would read zero. Force AlwaysAnimate while probing, restore after.
            int cm0 = -1;
            try { cm0 = (int)anim.cullingMode; anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; } catch { }

            const int Steps = 12;
            const float LenCap = 1.5f;   // skip long mechanism cycles (the rammer's 6-7s clip) — drive the press, not the gun
            var diag = new System.Text.StringBuilder($"[manip] authored scan '{sw.name}' (root='{SafeName(go)}' n={n} en={anim.enabled} upd={(int)anim.updateMode} cull={cm0}): ");

            int bestC = -1, bestHash = 0; float bestRank = float.NegativeInfinity, bestPeakTime = 0f, bestPeakDev = 0f;
            for (int c = 0; c < clips.Length; c++)
            {
                var clip = clips[c];
                if (clip == null) continue;
                string cn = SafeName(clip);
                float len = 0f; try { len = clip.length; } catch { }

                float nameScore = ClipNameScore(cn);
                bool excluded = nameScore <= -1000f || len <= 0.02f || len > LenCap;

                // These are Mecanim (generic) clips: AnimationClip.SampleAnimation silently no-ops on them (it only
                // applies LEGACY clips). The supported runtime evaluation is to drive the Animator itself — Play the
                // clip's state at a normalized time and Update(0) to bake the pose onto the rig, then read it.
                int hash = 0; bool hasState = false;
                if (!excluded)
                {
                    try { hash = Animator.StringToHash(cn); hasState = anim.HasState(0, hash); } catch { hasState = false; }
                }

                float peakDev = 0f, peakTime = 0f; int peakIdx = -1; string err = null;
                if (hasState)
                {
                    try
                    {
                        for (int s = 1; s <= Steps; s++)
                        {
                            float nt = (float)s / Steps;
                            anim.Play(hash, 0, nt);
                            anim.Update(0f);
                            float dev = 0f; int mi = -1; float md = 0f;
                            for (int i = 0; i < n; i++)
                            {
                                float di = Vector3.Distance(ts[i].localPosition, restP[i]) + Quaternion.Angle(ts[i].localRotation, restR[i]) * 0.01f;
                                dev += di;
                                if (di > md) { md = di; mi = i; }
                            }
                            if (dev > peakDev) { peakDev = dev; peakTime = nt; peakIdx = mi; }
                        }
                    }
                    catch (Exception e) { peakDev = 0f; err = e.Message; }
                    for (int i = 0; i < n; i++) { ts[i].localPosition = restP[i]; ts[i].localRotation = restR[i]; } // restore
                }

                string tag = excluded ? "X" : !hasState ? "noState" : err != null ? "ERR:" + err
                           : "dev=" + peakDev.ToString("0.0000") + (peakIdx >= 0 ? "@" + SafeName(ts[peakIdx]) : "");
                diag.Append($"[{cn} {len:0.00}s {tag}] ");
                if (excluded || !hasState || peakDev < 1e-3f) continue;

                // Prefer the named press/flip clip; deflection magnitude only breaks ties between same-named clips.
                float rank = nameScore + Mathf.Min(peakDev, 1f);
                if (rank > bestRank) { bestRank = rank; bestC = c; bestHash = hash; bestPeakTime = peakTime; bestPeakDev = peakDev; }
            }

            if (bestC < 0) { RestoreAnimator(anim, ts, restP, restR, haveSt0, st0Hash, st0Nt, cm0); Log.LogInfo(diag.ToString() + "=> none"); return false; }

            // Bake the chosen clip's peak (pressed/flipped) pose as the activated end, read it, then restore.
            var bestClip = clips[bestC];
            var onP = new Vector3[n]; var onR = new Quaternion[n];
            try { anim.Play(bestHash, 0, bestPeakTime); anim.Update(0f); }
            catch { RestoreAnimator(anim, ts, restP, restR, haveSt0, st0Hash, st0Nt, cm0); return false; }
            for (int i = 0; i < n; i++) { onP[i] = ts[i].localPosition; onR[i] = ts[i].localRotation; }
            RestoreAnimator(anim, ts, restP, restR, haveSt0, st0Hash, st0Nt, cm0);

            var movers = new System.Collections.Generic.List<SwMover>();
            for (int i = 0; i < n; i++)
            {
                float d = Vector3.Distance(restP[i], onP[i]) + Quaternion.Angle(restR[i], onR[i]) * 0.01f;
                if (d <= 1e-4f) continue;
                movers.Add(new SwMover { T = ts[i], RestPos = restP[i], RestRot = restR[i], OnPos = onP[i], OnRot = onR[i] });
            }
            if (movers.Count == 0) { Log.LogInfo(diag.ToString() + "=> no movers"); return false; }
            _switchMovers = movers;
            Log.LogInfo(diag.ToString() + $"=> '{SafeName(bestClip)}' nt={bestPeakTime:0.00}, {movers.Count} mover(s).");
            return true;
        }

        // Put the rig back: restore the original animator state + culling mode (best-effort) and the rest local poses.
        private static void RestoreAnimator(Animator anim, Transform[] ts, Vector3[] restP, Quaternion[] restR,
                                            bool haveSt0, int st0Hash, float st0Nt, int cm0)
        {
            try { if (haveSt0) { anim.Play(st0Hash, 0, st0Nt); anim.Update(0f); } } catch { }
            try { if (cm0 >= 0) anim.cullingMode = (AnimatorCullingMode)cm0; } catch { }
            for (int i = 0; i < ts.Length; i++) { ts[i].localPosition = restP[i]; ts[i].localRotation = restR[i]; }
        }

        // Name-based preference for which clip is the control's "operate" motion. <= -1000 means exclude entirely.
        private static float ClipNameScore(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0f;
            string n = name.ToLowerInvariant();
            if (n.Contains("hover")) return -1000f;                          // hover highlight, not a motion
            float s = 0f;
            if (n.Contains("safty") || n.Contains("safety")) s -= 100f;      // safety-cover flip is a different part
            if (n.Contains("dead")) s -= 40f;                                // disabled-press variant
            if (n.Contains("unclick") || n.Contains("un click")) s -= 30f;   // reverse of the press
            else if (n.Contains("click")) s += 100f;                         // the press
            if (n.Contains("active")) s += 40f;                              // active/depressed pose
            if (n.Contains("switch")) s += 30f;
            if (n.Contains("arm") || n.Contains("lever")) s += 20f;
            return s;
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
            ClearSwitchGrab();
            try { hands.ClearGrab(_hand == 2); } catch { }
            try { input.Haptic(Config.DetentHapticAmplitude, 0.03f); } catch { }
            Log.LogInfo($"[manip] released ({why}).");
            _kind = Kind.None; _hand = 0; _dial = null; _lever = null;
        }

        // Hand a grabbed switch back: re-enable its animator + each disabled constraint (so the game owns the
        // post-click state again), and on a LEARN grab cache every node the click moved so later grabs scrub. MUST
        // run on every switch teardown path (release, exception, reset) or a scrub-grab's animator/constraints stay
        // disabled and that switch is frozen for the rest of the session. Keeps _selSwitchKey.
        private void ClearSwitchGrab()
        {
            try
            {
                // PROBE: per-mover scrub result — local move, root-local (visible) move, stale-rest gap, write drift.
                if (_scrubManual && _activeMovers != null && _switchKey != null)
                {
                    var dd = new System.Text.StringBuilder();
                    for (int k = 0; k < _activeMovers.Count; k++)
                        dd.Append($" [{SafeName(_activeMovers[k].T)} move={_activeMovers[k].MoveMax:0.00} world={_activeMovers[k].WorldMove:0.000} drift={_activeMovers[k].MaxDrift:0.000}]");
                    Log.LogInfo($"[manip] scrub motion '{_switchKey}' progMax={_scrubProgMax:0.00} {(_scrubOpening ? "OPEN" : "CLOSE")}:{dd}");
                }
                // Held-stick result on EVERY grab (learn or scrub): how far the grab point on the hinge actually swung.
                if (_leverT != null && _switchKey != null)
                {
                    string mode = _leverSlide
                        ? $"SLIDE axis=({_leverSlideAxisW.x:0.00},{_leverSlideAxisW.y:0.00},{_leverSlideAxisW.z:0.00}) locked={_leverHasSlideAxis}"
                        : $"ROTATE axis=({_leverRotAxisW.x:0.00},{_leverRotAxisW.y:0.00},{_leverRotAxisW.z:0.00}) locked={_leverHasRotAxis}";
                    // DIAG: any animator STILL enabled in the lever subtree re-poses the lever AFTER our write each frame —
                    // that would make "grab-point moved" report motion the user never sees (write applied, then overwritten).
                    int liveAnim = 0;
                    try { var aa = _leverT.GetComponentsInChildren<Animator>(true); if (aa != null) for (int i = 0; i < aa.Length; i++) if (aa[i] != null && aa[i].enabled) liveAnim++; } catch { }
                    Log.LogInfo($"[manip] held-stick result '{_switchKey}': hinge '{SafeName(_leverT)}' {mode} grab-point moved {_leverTipTravel:0.000} m. live-anim={liveAnim} arch={_leverStickArchetype}");
                }

                RestoreScrubDrivers();
                // Return the synthetic hinge to its rest pose so the lever doesn't stay stuck swung/slid after release.
                try { if (_leverT != null) { _leverT.localRotation = _leverRestLocalR; _leverT.localPosition = _leverPivotLocal; } } catch { }
                if (_switchAnimator != null) _switchAnimator.enabled = _switchAnimatorWasEnabled;

                // FOLLOW control fired on RELEASE: you pulled past the threshold, so fire the click NOW (hand already gone
                // → the scripted animation plays without yanking the lever). Set _learnTriggered so the tail still captures.
                if (_leverT != null && _leverArmed && _switch != null)
                {
                    Activate(_switch, _switchInteractable);
                    if (!_scrubManual) _learnTriggered = true;
                    Log.LogInfo($"[manip] held-stick '{_switchKey}': fired click on release (pulled past threshold).");
                }

                // LEARN: if the click fired, the animation is still playing — keep the rig snapshot alive and sample a
                // TAIL after release (in Tick) so the FULL animation is captured regardless of how fast you let go. The
                // cache is built when the tail completes. If the click never fired, there's nothing to capture.
                if (!_scrubManual && _scrubTs != null && _learnTriggered && _scrubKey != null)
                {
                    _learnTail = true; _learnTailKey = _scrubKey; _learnTailUntil = Time.time + LearnTailSeconds;
                }
            }
            catch { }
            _switch = null; _switchInteractable = null; _switchLatched = false;
            _switchScrubDone = false; _scrubManual = false; _activeMovers = null; _scrubAnimators = null;
            _leverT = null; _leverParent = null; _leverCon = null;
            _switchRef = null; _switchMovers = null; _switchAnimator = null; _switchKey = null; _scrubKey = null; _switchProgress = 0f;
            // Keep the learn arrays alive ONLY while a tail capture is pending; otherwise drop everything.
            if (!_learnTail)
            {
                _scrubTs = null; _scrubRoot = null; _scrubRestRL = null; _scrubWorldPeak = null;
                _scrubGlobalPeak = 0f; _learnTriggered = false;
            }
        }

        // Sample one frame of the learn capture: update each node's peak local deflection (+ pose at peak) and its peak
        // root-local (visible) travel. Runs both while holding the learn grab AND during the post-release tail.
        // One-time structural dump per control: reveal which node is the visible STICK the player holds (vs the grabbed
        // LookAtTarget, which is often a logical button carrying only lights/indicators). Lists self + ancestors, the
        // grabbed node's children, and its siblings — each with renderer/animator/constraint flags, geometric EXTENT
        // (farthest descendant = how long it is) and NEAR (closest its body comes to the grab point). The held stick is
        // the elongated renderer node whose body is nearest the grab. We never read constraint sources (that crashes).
        private static readonly System.Collections.Generic.HashSet<string> _stickDumped = new System.Collections.Generic.HashSet<string>();
        private void DumpStickCandidates(Transform grabbed, Vector3 grabPoint)
        {
            if (grabbed == null) return;
            // Key the once-per-control gate by INSTANCE, not bare name — many controls share 'Universal Button', and a
            // name-only gate meant only the FIRST instance ever dumped its geometry (the punchcard/raise-console levers,
            // grabbed after a floor hatch, were silently skipped). Per-instance → each physical control dumps once.
            int gid = 0; try { gid = grabbed.GetInstanceID(); } catch { }
            string key = SafeName(grabbed) + "#" + gid;
            if (_stickDumped.Contains(key)) return;
            _stickDumped.Add(key);
            try
            {
                Log.LogInfo($"[stick] === '{key}' grab@({grabPoint.x:0.00},{grabPoint.y:0.00},{grabPoint.z:0.00}) ===");
                Transform t = grabbed; int up = 0;
                while (t != null && up <= 3) { Log.LogInfo($"[stick] anc{up} {DescribeStickNode(t, grabPoint)}"); t = t.parent; up++; }
                DumpStickChildren(grabbed, grabPoint, "  kid", 2);
                Transform par = grabbed.parent;
                if (par != null)
                    for (int i = 0; i < par.childCount; i++)
                    {
                        Transform c = null; try { c = par.GetChild(i); } catch { }
                        if (c == null || c == grabbed) continue;
                        Log.LogInfo($"[stick] sib {DescribeStickNode(c, grabPoint)}");
                    }
                // Buried controls (Reload Cylinder, Shell Rammer, Punchcard) hinge on a node reached up-and-over through
                // the assembly — NOT an ancestor/sibling — so the lines above never show it. Scan the whole assembly for
                // lever-named nodes nearest the grab to reveal the real movable handle (origin-near small = a local hinge).
                DumpLeverCandidates(grabbed, grabPoint);
            }
            catch (Exception e) { Log.LogWarning("[stick] dump: " + e.Message); }
        }

        // PROBE: across the control's assembly, list every lever-named node and how near its PIVOT ORIGIN sits to the grab.
        // For a buried control whose hinge isn't a sibling, the real handle is the lever-named node with a small origin-near
        // and an extent that reaches toward the grab — that's what we drive next. One-time per control (gated upstream).
        private void DumpLeverCandidates(Transform grabbed, Vector3 grabPoint)
        {
            try
            {
                // Assembly root: highest ancestor whose subtree is still bounded — don't scan the whole turret (thousands).
                Transform root = grabbed, probe = grabbed; int climb = 0;
                while (probe != null && climb <= 6)
                {
                    int sz = int.MaxValue; try { sz = probe.GetComponentsInChildren<Transform>(true).Length; } catch { }
                    if (sz <= 1200) root = probe; else break;
                    probe = probe.parent; climb++;
                }
                Transform[] all = null; try { all = root.GetComponentsInChildren<Transform>(true); } catch { }
                if (all == null) return;
                int named = 0; for (int i = 0; i < all.Length; i++) if (all[i] != null && LeverNameHint(all[i])) named++;
                Log.LogInfo($"[stick] lever-candidates under '{SafeName(root)}' ({all.Length} nodes, {named} named):");
                int shown = 0;
                for (int i = 0; i < all.Length && shown < 40; i++)
                {
                    var t = all[i]; if (t == null || t == grabbed || !LeverNameHint(t)) continue;
                    Vector3 o = Vector3.zero; try { o = t.position; } catch { }
                    float oNear = Vector3.Distance(o, grabPoint);
                    Log.LogInfo($"[stick]   cand {DescribeStickNode(t, grabPoint)} origin-near={oNear:0.000} d{Depth(t, root)}");
                    shown++;
                }
            }
            catch (Exception e) { Log.LogWarning("[stick] lever-candidates: " + e.Message); }
        }

        // Name hint that a node is (or carries) a movable handle/hinge — used only to surface candidates in the dump.
        private static bool LeverNameHint(Transform t)
        {
            string s = SafeName(t); if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            return s.Contains("parent") || s.Contains("lever") || s.Contains("handle") || s.Contains("crank")
                || s.Contains("pull") || s.Contains("swing") || s.Contains("rotate") || s.Contains("ram")
                || s.Contains("valve") || s.Contains("wheel") || s.Contains("pump") || s.Contains("pivot")
                || s.Contains("hinge");
        }

        private void DumpStickChildren(Transform t, Vector3 grabPoint, string pre, int depth)
        {
            if (t == null || depth <= 0) return;
            int n = 0; try { n = t.childCount; } catch { return; }
            for (int i = 0; i < n; i++)
            {
                Transform c = null; try { c = t.GetChild(i); } catch { }
                if (c == null) continue;
                Log.LogInfo($"[stick]{pre} {DescribeStickNode(c, grabPoint)}");
                DumpStickChildren(c, grabPoint, pre + "  ", depth - 1);
            }
        }

        // One node line: name, renderer kind (+A animator, +C constraint), how long it is, how near its body is to the grab.
        private string DescribeStickNode(Transform t, Vector3 grabPoint)
        {
            string kind = RendererKind(t);
            bool anim = false; try { anim = t.GetComponent<Animator>() != null; } catch { }
            bool con = false; try { con = t.GetComponent<UnityEngine.Animations.ParentConstraint>() != null; } catch { }
            float extent = 0f, near = float.MaxValue;
            Vector3 o = Vector3.zero; try { o = t.position; } catch { }
            Transform[] kids = null; try { kids = t.GetComponentsInChildren<Transform>(true); } catch { }
            if (kids != null)
                for (int i = 0; i < kids.Length; i++)
                {
                    var k = kids[i]; if (k == null) continue;
                    Vector3 kp; try { kp = k.position; } catch { continue; }
                    float e = Vector3.Distance(kp, o); if (e > extent) extent = e;
                    float nr = Vector3.Distance(kp, grabPoint); if (nr < near) near = nr;
                }
            if (near == float.MaxValue) near = Vector3.Distance(o, grabPoint);
            return $"'{SafeName(t)}' {kind}{(anim ? "+A" : "")}{(con ? "+C" : "")} ext={extent:0.00} near={near:0.000}";
        }

        // Pick the stick the player is HOLDING (so we can point it at the hand). It's the grabbed node, OR its parent if
        // the parent gives a longer lever-arm from the grab point — that's the long stick whose base is its own origin
        // (e.g. the grabbed button sits on the end of 'Power Lever'). The captured movers are the actuated mechanism, NOT
        // the held stick, so we never pick from them. Seeded from the grab point; null => no synthetic swing this grab.
        private void SelectHeldStick(Transform grabbed, Vector3 grabPoint)
        {
            _leverT = null; _leverParent = null; _leverCon = null; _leverTipTravel = 0f; _leverArmed = false; _leverBuried = false; _capSlideFollow = false; _leverStickArchetype = false;
            _riderT = null; _riderParent = null; _riderCon = null; _riderTravel = 0f; _leverFollowFrac = 0f;
            if (_riders != null) _riders.Clear();   // their constraints were already re-enabled by RestoreScrubDrivers on the prior release
            if (_forcedRends != null) _forcedRends.Clear();   // renderer-enable already restored by RestoreScrubDrivers on the prior release
            if (_forcedActive != null) _forcedActive.Clear();   // SetActive already restored by RestoreScrubDrivers on the prior release
            if (!HeldStickFollow) return;   // structure-dump build: don't drive any node until the real stick is identified
            if (grabbed == null) return;

            // Resolve the node to drive, best first. The choice is PURELY GEOMETRIC (no learn-capture dependency) so
            // the SAME node is driven on grab #1 and every grab after — the earlier "tracks once, then stops" bug came
            // from grab #2+ switching to a captured mover that the scrub loop then fought.
            //  1) a named '<Name> Parent' hinge on an ancestor/sibling (hatch, power lever, war horn, arming) — TOGGLE;
            //  2) a named '*Parent' hinge nested in the assembly (reload '.RotateLeverParent', shell '.ShellRamLeverParent',
            //     Calculate '.Calculate Lever Parent', coffee) — buried;
            //  3) a named '*Lever' DESCENDANT the game animates when there's no '*Parent' (dispenser '.ChargeLever',
            //     charge rammer '.PowderRamLever') — drives ONLY the lever, not the lights/drums/gears under the button;
            //  3b) the grabbed control's own small per-control PARENT when it isn't named '*Parent' (review-console toggle
            //     '.Check Switch') — TOGGLE; the orb-cap/knob head rides it since they're under the pivot;
            //  4) a captured lever/cap outside the grabbed subtree (charge rammer) — last resort, needs a capture.
            // 2 & 3 are BURIED mechanism levers: ONE-DIRECTIONAL (rest→press), not the ±70° bidirectional toggle.
            // Pull-chain controls (War Horn) must slide the WHOLE assembly — cover + handle + chain — as one rigid unit, so
            // do NOT refine the high '<Name> Parent' hinge down to the buried internal lever (that left the cover/horn body
            // behind, moving only the trigger). The high node is reset to rest on release (ClearSwitchGrab), so the whole
            // horn returns cleanly and no per-mesh riders are needed. Gated to slide-hint controls → other levers unaffected.
            bool slideAssembly = NameSlideHintChain(grabbed);
            Transform pivot = FindPivotNode(grabbed, grabPoint);     // tier 1: ancestor/sibling '<Name> Parent' (toggle)
            if (pivot != null && !slideAssembly) pivot = RefinePivotToLeverChild(pivot, grabPoint);
            if (pivot == null)
            {
                pivot = FindAssemblyParentHinge(grabbed, grabPoint); // tier 2: '*Parent' nested in the assembly
                if (pivot != null && HingeGovernsForeignSwitch(pivot, grabbed)) pivot = null;
                if (pivot != null) { if (!slideAssembly) pivot = RefinePivotToLeverChild(pivot, grabPoint); _leverBuried = true; }
            }
            if (pivot == null)
            {
                pivot = FindLeverDescendant(grabbed, grabPoint);     // tier 3: a '*Lever' the game animates (no '*Parent')
                if (pivot != null) _leverBuried = true;
            }
            if (pivot == null)
            {
                // tier 3a-pre: a CAPTURED rotation lever the game itself animates (charge rammer '.PowderRamLever') — the
                // TRUE stick. PREFERRED over the name match below, which grabs the nearest lever-NAMED node by origin: for
                // the charge rammer that's 'BigLever.001' whose only child is a Bézier WIRING curve, not the rod (the user
                // saw "wiring move, not the stick"). The captured lever is the node the game's own clip swings, tip orb
                // rides it. Needs a capture → engages from the 2nd grab; the 1st (learn) grab falls through to the match.
                Transform capHinge = FindCapturedLeverHinge(grabbed, grabPoint);
                if (capHinge != null)
                {
                    pivot = capHinge; _leverBuried = true;
                    Vector3 rAxisW0; float rTravel0;
                    Transform rider0 = FindCapturedCapMover(grabbed, out rAxisW0, out rTravel0);
                    if (rider0 != null && rider0 != pivot && !IsDescendantOf(rider0, pivot))
                        SetRider(rider0, rAxisW0, rTravel0);
                }
            }
            if (pivot == null)
            {
                pivot = FindAssemblyLever(grabbed, grabPoint);       // tier 3a: a lever-named handle in a SIBLING subtree
                // PUNCHCARD: the visible handle ('Locking Lever') is GPU-instanced and only renders while the game's OWN
                // submit animation actively plays — it cannot be hand-followed (proven over passes 26-34: it ignores
                // transform writes AND a held/disabled animator; an enabled animator gets reverted to idle by the game every
                // frame). Per the user, leave this lever as a plain THRESHOLD TRIGGER with no custom visual movement: don't
                // drive a held stick (leave _leverT null) so DriveSwitch fires the click when the hand pulls a set distance
                // in the slide/push direction, exactly like the base control.
                if (pivot != null && !HasVisibleEnabledMesh(pivot))
                {
                    Log.LogInfo($"[manip] held-stick follow: '{SafeName(grabbed)}' handle '{SafeName(pivot)}' is GPU-instanced (renders only during the game's own animation) — activate-only threshold trigger, no visual follow.");
                    return;
                }
                if (pivot != null)
                {
                    _leverBuried = true; _leverStickArchetype = true;
                    // The user actually GRABS the orb-cap ('Cylinder.001'), a constraint-driven mesh child of the button
                    // that sits at the lever tip — NOT under the lever's subtree, so swinging the lever alone leaves the
                    // held orb dead in the air ("nothing tracks"). Glue it to the swing as a rigid rider. Found geometrically
                    // (no capture needed → works on the 1st grab). The Barbet/hatch levers don't need this — their orb rides
                    // the lever via the game's own constraint because we drive the very lever it's pinned to.
                    Transform orb = FindOrbCapGeometric(grabbed, pivot, grabPoint);
                    if (orb != null) SetRider(orb, Vector3.zero, 0f);
                }
            }
            if (pivot == null)
            {
                pivot = FindParentHinge(grabbed, grabPoint);         // tier 3b: a small per-control parent not named '*Parent' (toggle)
            }
            bool capSlideFollow = false;
            Vector3 capSlideAxisW = Vector3.zero; float capSlideTravel = 0f;
            if (pivot == null)
            {
                // tier 4: no '*Parent' hinge and no lever-named child IN the grabbed subtree. Use the captured motion:
                //  4a) a captured LEVER-named ROTATION mover whose arm reaches the grab — the STICK to swing (charge rammer
                //      '.PowderRamLever', a SIBLING whose hinge is ~1.2 m off but whose arm reaches your hand; the tip
                //      orb-light rides it). Rotate-follow it like reload — swings the whole stick, not just the tip light.
                //  4b) else the captured mesh that TRAVELS the most (a tip with no lever hinge) — slide-follow it 1:1.
                // Needs the capture, so it engages from the 2nd grab; the 1st learn grab can't follow (logged).
                Transform hinge = FindCapturedLeverHinge(grabbed, grabPoint);
                if (hinge != null)
                {
                    pivot = hinge; _leverBuried = true;
                    // The tip orb-light is a captured mover under the grabbed BUTTON (not under the stick), so swinging the
                    // stick won't move it. Set it up as a RIDER: capture its world pose now and RIGIDLY co-move it with the
                    // stick's exact transform delta each frame (DriveRider) so it stays glued to the swinging tip.
                    Vector3 rAxisW; float rTravel;
                    Transform rider = FindCapturedCapMover(grabbed, out rAxisW, out rTravel);
                    if (rider != null && rider != pivot && !IsDescendantOf(rider, pivot))
                        SetRider(rider, rAxisW, rTravel);
                }
                else
                {
                    Transform cap = FindCapturedCapMover(grabbed, out capSlideAxisW, out capSlideTravel);
                    if (cap == null)
                    {
                        // No capture yet (or the handle isn't a captured mover): the orb-cap IS the visible handle (punchcard
                        // slides it along its base). Find it geometrically so it follows on the FIRST grab — slide axis then
                        // auto-infers from the hand's pull (snapped to the cap's local cardinal).
                        cap = FindOrbCapGeometric(grabbed, null, grabPoint);
                        if (cap != null) Log.LogInfo($"[manip] held-stick follow: orb-cap '{SafeName(cap)}' slide-follow (geometric, no capture).");
                    }
                    if (cap != null) { pivot = cap; _leverBuried = true; capSlideFollow = true; }
                    else Log.LogInfo($"[manip] held-stick follow: '{SafeName(grabbed)}' has no hinge/lever and no capture yet (learn grab) — grab again to follow.");
                }
            }
            if (pivot == null)
            {
                Log.LogInfo($"[manip] held-stick follow: no hinge for '{SafeName(grabbed)}' — no swing this control.");
                return;
            }
            float arm = 0f; try { arm = Vector3.Distance(grabPoint, pivot.position); } catch { }
            if (arm < 0.03f)
            {
                Log.LogInfo($"[manip] held-stick follow: hinge '{SafeName(pivot)}' arm<3cm ({arm:0.000}) — no swing.");
                return;
            }
            _leverT = pivot; _leverParent = pivot.parent;
            _leverPivotLocal = pivot.localPosition; _leverRestLocalR = pivot.localRotation;
            Vector3 armW = grabPoint - pivot.position;
            _leverArmParentDir = _leverParent != null ? _leverParent.InverseTransformDirection(armW) : armW;
            try { _leverGrabLocal = pivot.InverseTransformPoint(grabPoint); } catch { _leverGrabLocal = Vector3.zero; }
            _leverTip0W = grabPoint; _leverTipTravel = 0f;
            _leverHasSlideAxis = false; _leverSlideAxisW = Vector3.zero;
            _leverHasRotAxis = false; _leverRotAxisW = Vector3.zero;
            _leverArmed = false;
            _capSlideFollow = capSlideFollow;   // remember for DriveSwitch (syncs the scrub movers to the cap's travel)

            // SLIDE: a captured-cap follow (tier 4) slides; else the menu Translate flag or a name hint (the hint
            // climbs the grabbed control's ancestors so the War Horn — grabbed node 'universal button', slide hint on
            // its 'War Horn' parent — is detected as a slide rather than rotating).
            _leverSlide = capSlideFollow || _switchMotion.Translate || NameSlideHint(pivot) || NameSlideHintChain(grabbed);

            // THROW LIMIT: both toggle and buried levers swing up to 70°, but a buried mechanism lever is ONE-DIRECTIONAL
            // (rest→press; enforced by swingLo in DriveHeldStick) while a named toggle hinge swings ±70°. Slide distance is
            // the captured travel for a cap-follow, the menu Range for a translate control, else a default.
            _leverMaxAngle = 70f;
            // Cap-slide throw = the captured travel; but a GEOMETRIC orb-cap (no capture, capSlideTravel==0) needs a sane
            // default (~0.3 m) so the punchcard handle can slide its full track instead of clamping to 5 cm.
            _leverSlideMax = capSlideFollow ? Mathf.Clamp(capSlideTravel > 0.01f ? capSlideTravel : 0.30f, 0.05f, 1.5f)
                           : (_switchMotion.Translate ? Mathf.Clamp(_switchMotion.Range, 0.03f, 0.4f) : 0.18f);

            // AXIS priority: (1) manual menu override; (2) the captured cap's real travel direction; (3) a buried
            // rotate-lever's FIXED axis from the game's captured motion (always the same way, not whichever way the
            // controller first moved); (4) auto-infer.
            Vector3 lockAxisLocal = Vector3.zero; Quaternion axisFrame = Quaternion.identity; bool haveLock = false;
            if (_switchMotion.ManualAxis)
            {
                lockAxisLocal = _switchMotion.Axis; if (_switchMotion.Flip) lockAxisLocal = -lockAxisLocal;
                axisFrame = _leverParent != null ? _leverParent.rotation * _leverRestLocalR : _leverRestLocalR;
                haveLock = true;
            }
            if (haveLock)
            {
                Vector3 axW = (axisFrame * lockAxisLocal).normalized;
                if (axW.sqrMagnitude > 1e-8f)
                {
                    if (_leverSlide) { _leverSlideAxisW = axW; _leverHasSlideAxis = true; }
                    else { _leverRotAxisW = axW; _leverHasRotAxis = true; }
                }
            }
            else if (capSlideFollow && capSlideAxisW.sqrMagnitude > 1e-8f)
            {
                _leverSlideAxisW = capSlideAxisW.normalized; _leverHasSlideAxis = true;
            }
            else if (_leverBuried && !_leverSlide && TryCapturedHingeAxisWorld(out Vector3 capAxW))
            {
                // FIXED direction from the game's own rest→press of the lever child (reload '.RotateLever' etc.). Pulling
                // the wrong way then reads as negative swing → clamped to 0 (DriveHeldStick), so the lever simply won't
                // move the wrong way. Needs a capture (present from the 2nd grab on); the 1st learn grab auto-infers.
                _leverRotAxisW = capAxW; _leverHasRotAxis = true;
                Log.LogInfo($"[manip] held-stick follow: fixed hinge axis from capture ({capAxW.x:0.00},{capAxW.y:0.00},{capAxW.z:0.00}).");
            }

            // ORB-STICK with no captured/manual axis (punchcard, charge rammer): lock a STABLE axis PERPENDICULAR to the rod
            // so the stick sweeps toward the hand instead of the cross-product inference snapping to the rod's LONG axis and
            // just twisting it (which moved only the offset orb). Bidirectional swing (see _leverStickArchetype in DriveHeldStick)
            // means the sign doesn't matter — pull either way and the visible stick follows.
            if (_leverStickArchetype && !_leverHasRotAxis && !_leverHasSlideAxis && !_leverSlide)
            {
                Vector3 ax = StableHingeAxisW(pivot, grabPoint);
                if (ax.sqrMagnitude > 1e-8f)
                {
                    _leverRotAxisW = ax; _leverHasRotAxis = true;
                    Log.LogInfo($"[manip] held-stick follow: stable perpendicular hinge axis ({ax.x:0.00},{ax.y:0.00},{ax.z:0.00}) for orb-stick '{SafeName(pivot)}'.");
                }
            }

            // If a ParentConstraint pins the hinge, our rotation would be overwritten — disable it for the grab and
            // restore on release. (Null-check only; we never read its sources — that API hard-crashes this runtime.)
            try { _leverCon = pivot.GetComponent<UnityEngine.Animations.ParentConstraint>(); } catch { _leverCon = null; }
            if (_leverCon != null) { try { _leverConWasActive = _leverCon.constraintActive; _leverCon.constraintActive = false; } catch { } }
            // Disable the animator(s) that would re-pose the hinge over our writes — on the hinge, on the grabbed control,
            // and the local-assembly rig ABOVE the hinge (the reload console / arming rig idles the lever back to rest,
            // which is why reload cylinder & arming lever computed a swing but never moved). Bounded so the turret is safe.
            try { DisableAssemblyAnimators(grabbed, pivot); } catch { }

            // DIAG: list the hinge's immediate children — confirms the visible lever (e.g. reload's '.RotateLever') is
            // actually UNDER the hinge we rotate. If it isn't, rotating the hinge can't move it (wrong node picked).
            string kidNames = "";
            try
            {
                int cc = pivot.childCount; var ksb = new System.Text.StringBuilder();
                for (int i = 0; i < cc && i < 6; i++) { var c = pivot.GetChild(i); ksb.Append(SafeName(c)); ksb.Append(i < cc - 1 && i < 5 ? "," : ""); }
                kidNames = $" kids[{cc}]: {ksb}";
            }
            catch { }
            Log.LogInfo($"[manip] held-stick follow: {(_leverSlide ? "sliding" : "swinging")} hinge '{SafeName(pivot)}' (arm={arm:0.000}m, con={_leverCon != null}).{kidNames}");

            // Co-move the REST of the handle cluster with the stick: the console's orb-light on the lever tip, the punchcard's
            // connector beside the orb-cap — separate constraint-driven meshes that otherwise stay put while the stick moves.
            // (The War Horn doesn't need this: its slide drives the whole assembly node, so the cover/handle/chain all ride it.)
            try { GatherExtraRiders(grabbed, grabPoint); } catch { }
        }

        // Unique-per-physical-control, session-stable SwitchMotion key: the control's own name plus a few ancestor
        // names. GameObject names don't change between runs, so this is stable; the differing ancestor (the per-side
        // 'Gun System Left/Right' container, or the distinct parent switch under '.Review Console Parent', or the
        // 'Power Lever' the power switch hangs on) makes every physical control resolve to its own entry — so menu
        // tuning and the captured push direction stay per control instead of bleeding across same-named siblings.
        private static string BuildSwitchKey(LookAtTarget sw)
        {
            var sb = new System.Text.StringBuilder(SafeName(sw.transform));
            Transform t = null; try { t = sw.transform.parent; } catch { }
            for (int i = 0; i < 3 && t != null; i++)
            {
                sb.Append('@').Append(SafeName(t));
                try { t = t.parent; } catch { t = null; }
            }
            return sb.ToString();
        }

        // Look up tuning saved under an OLDER key scheme so it can migrate to the new per-control key: builds keyed by
        // the bare name, or (for the two generic prefabs) by name@parent. Returns the first that exists.
        private static bool TryGetLegacyMotion(LookAtTarget sw, out SwitchMotion m)
        {
            string bare = SafeName(sw.transform);
            string par = null; try { if (sw.transform.parent != null) par = SafeName(sw.transform.parent); } catch { }
            if (!string.IsNullOrEmpty(par) && SwitchMotions.TryGet(bare + "@" + par, out m)) return true;
            return SwitchMotions.TryGet(bare, out m);
        }

        // Name hint that a control TRANSLATES rather than rotates (pulley/horn/chain). The menu Translate flag overrides.
        private static bool NameSlideHint(Transform t)
        {
            string s = SafeName(t); if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            return s.Contains("horn") || s.Contains("pulley") || s.Contains("chain") || s.Contains("rope") || s.Contains("cord");
        }

        // Some translate-type controls are named generically at the grab point and only an ANCESTOR carries the hint —
        // the War Horn's grabbed switch is 'universal button' but its parents are 'War Horn'/'War Horn Parent', so the
        // node-only NameSlideHint missed it and the horn rotated instead of sliding. Climb a few levels to catch it.
        private static bool NameSlideHintChain(Transform t)
        {
            for (int i = 0; i < 5 && t != null; i++)
            {
                if (NameSlideHint(t)) return true;
                try { t = t.parent; } catch { t = null; }
            }
            return false;
        }

        // If the chosen hinge carries BOTH a lever-named child AND other large meshes, drive only the LEVER child (about
        // that same hinge) so the siblings stay put. The trigger floor-hatch hinge '.Hatch Trigger Parent.001' carries the
        // hatch DOORS ('.Hatch 1.002', '.Hatch 2.001') alongside the lever '.Hatch lever.002'; rotating the hinge swung the
        // doors with the hand ("moves the hatch instead of the stick"). Only refines when there's a FOREIGN visible-mesh
        // child to protect — a hinge whose only mesh IS the lever (Barbet hatch, power, arming) is returned unchanged, so
        // those keep their exact current behavior. The lever child rotates about its own origin (the real lever hinge).
        private Transform RefinePivotToLeverChild(Transform pivot, Vector3 grabPoint)
        {
            if (pivot == null) return pivot;
            int cc = 0; try { cc = pivot.childCount; } catch { return pivot; }
            // FOREIGN-MESH gate stays on DIRECT children only: a hinge that holds nothing but its lever (Barbet hatch,
            // power, arming) has no foreign direct child, so this never fires for them — zero regression. The trigger
            // floor-hatch hinge '.Hatch Trigger Parent.001' has the two DOOR meshes as direct children → foreign.
            bool hasForeignMesh = false;
            for (int i = 0; i < cc; i++)
            {
                Transform k = null; try { k = pivot.GetChild(i); } catch { }
                if (k == null || !HasVisibleMesh(k)) continue;
                if (!LeverHandleName(k)) { hasForeignMesh = true; break; }   // a door/panel that would swing if we rotated the hinge
            }
            if (!hasForeignMesh) return pivot;
            // LEVER search spans DESCENDANTS, not just direct children: the trigger hatch's real lever '.Hatch lever.002'
            // is mounted UNDER a door (a grandchild), so a direct-children-only search missed it and we kept swinging the
            // doors. Pick the nearest-to-grab lever-named visible mesh anywhere in the subtree.
            Transform bestLever = null; float bestNear = float.MaxValue;
            Transform[] desc = null; try { desc = pivot.GetComponentsInChildren<Transform>(true); } catch { }
            if (desc != null)
                for (int i = 0; i < desc.Length; i++)
                {
                    Transform k = desc[i]; if (k == null || k == pivot) continue;
                    if (!LeverHandleName(k) || !HasVisibleMesh(k)) continue;
                    float nr = NearestApproach(k, grabPoint);
                    if (nr < bestNear) { bestNear = nr; bestLever = k; }
                }
            if (bestLever != null && bestNear <= 0.4f)
            {
                Log.LogInfo($"[manip] held-stick follow: refine hinge '{SafeName(pivot)}' → buried lever '{SafeName(bestLever)}' (swing only the lever, not the doors/siblings).");
                return bestLever;
            }
            // The lever may live OUTSIDE the hinge's own subtree: the trigger lever '.Hatch lever.002' is a SEPARATE node
            // under 'Trigger Console' (hinged at its base ~0.72 m down, but its body reaches up to ~0.19 m from the grab),
            // NOT under '.Hatch Trigger Parent.001'. The hinge is foreign-mesh (doors), so fall back to the nearest
            // lever-named visible mesh anywhere in the local assembly whose BODY reaches the grab (strict ≤0.3 m so we never
            // latch a neighbouring control's lever). Drive it about its own base = the real lever swing; doors stay put.
            Transform root = BoundedAssemblyRoot(pivot);
            if (root != null)
            {
                Transform[] all = null; try { all = root.GetComponentsInChildren<Transform>(true); } catch { }
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                    {
                        Transform k = all[i]; if (k == null || k == pivot) continue;
                        if (!LeverHandleName(k) || !HasVisibleMesh(k) || IsDescendantOf(k, pivot)) continue;
                        float nr = NearestApproach(k, grabPoint);
                        if (nr < bestNear) { bestNear = nr; bestLever = k; }
                    }
                if (bestLever != null && bestNear <= 0.3f)
                {
                    Log.LogInfo($"[manip] held-stick follow: refine hinge '{SafeName(pivot)}' → assembly lever '{SafeName(bestLever)}' (body {bestNear:0.000}m from grab; doors stay).");
                    return bestLever;
                }
            }
            return pivot;
        }

        // The lever's hinge: the nearest node named '*Parent' to the grab point — ancestors first (up to the assembly),
        // then the grabbed node's siblings. Ranked by how close the node's body comes to the grab. Null if none found.
        private Transform FindPivotNode(Transform grabbed, Vector3 grabPoint)
        {
            Transform best = null; float bestNear = float.MaxValue;
            Transform t = grabbed; int up = 0;
            while (t != null && up <= 4)
            {
                if (t != grabbed && NameHasParent(t))
                {
                    float n = NearestApproach(t, grabPoint);
                    if (n < bestNear) { bestNear = n; best = t; }
                }
                t = t.parent; up++;
            }
            Transform par = grabbed.parent;
            if (par != null)
            {
                int cc = 0; try { cc = par.childCount; } catch { }
                for (int i = 0; i < cc; i++)
                {
                    Transform c = null; try { c = par.GetChild(i); } catch { }
                    if (c == null || c == grabbed || !NameHasParent(c)) continue;
                    // A sibling '*Parent' is only a real lever hinge if it carries visible geometry. The requisition
                    // console's submit/raise '*Parent' sits beside the map's 'RTS cam Parent' (a camera rig, no mesh);
                    // without this guard the nearest-'*Parent' search latched onto the camera and swung it on grab.
                    if (!HasVisibleMesh(c))
                    {
                        Log.LogInfo($"[manip] held-stick follow: skip sibling hinge '{SafeName(c)}' (no visible mesh — not a lever).");
                        continue;
                    }
                    float n = NearestApproach(c, grabPoint);
                    if (n < bestNear) { bestNear = n; best = c; }
                }
            }
            // Reject a hinge that governs OTHER controls (a shared assembly/console parent). The Review Console switches
            // sit under '.Review Console Parent', which also carries every other switch — swinging it moved the WHOLE
            // console. A real per-control hinge carries only this control's own button.
            if (best != null && HingeGovernsForeignSwitch(best, grabbed))
            {
                Log.LogInfo($"[manip] held-stick follow: hinge '{SafeName(best)}' governs other controls (shared console) — no swing.");
                best = null;
            }
            return best;
        }

        // True if the hinge's subtree contains a LookAtTarget that ISN'T the grabbed control (nor a sub-part of it) —
        // i.e. the node is a shared console/assembly pivot, not this one control's hinge. Driving it would move siblings.
        private static bool HingeGovernsForeignSwitch(Transform hinge, Transform grabbed)
        {
            LookAtTarget[] lats = null; try { lats = hinge.GetComponentsInChildren<LookAtTarget>(true); } catch { }
            if (lats == null) return false;
            for (int i = 0; i < lats.Length; i++)
            {
                var lt = lats[i]; if (lt == null) continue;
                Transform t = null; try { t = lt.transform; } catch { }
                if (t == null || t == grabbed) continue;
                if (IsDescendantOf(t, grabbed)) continue;   // a sub-part of the grabbed control is fine
                return true;
            }
            return false;
        }

        private static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            for (Transform p = t; p != null; p = p.parent) if (p == ancestor) return true;
            return false;
        }


        // Fallback hinge finder: scan the control's bounded assembly for the '*Parent' node whose pivot ORIGIN sits
        // nearest the grab, accepting only a genuinely LOCAL hinge (origin within reach). This catches nephew hinges the
        // ancestor/sibling walk misses, while correctly rejecting far mechanism arms (the reload console's
        // '.Rammer Carrage Parent' is metres from the grab) so we never swing a remote part again.
        private Transform FindAssemblyParentHinge(Transform grabbed, Vector3 grabPoint)
        {
            Transform root = BoundedAssemblyRoot(grabbed);
            Transform[] all = null; try { all = root.GetComponentsInChildren<Transform>(true); } catch { }
            if (all == null) return null;
            // Require the hinge's BODY (not just its origin) to reach the grab — a real per-control hinge's lever passes
            // through your hand (Calculate's '.Calculate Lever Parent' body is 0.13 m away), while an internal mechanism
            // arm we must NOT grab (reload's '.RotateLeverParent' drives a rod separate from the handle) stays farther.
            Transform best = null; float bestNear = float.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                var n = all[i]; if (n == null || n == grabbed || !NameHasParent(n)) continue;
                float o; try { o = Vector3.Distance(n.position, grabPoint); } catch { continue; }
                if (o > 0.6f) continue;                       // cheap pre-filter: hinge origin must be local
                float nr = NearestApproach(n, grabPoint);     // does the hinge's body actually reach the grab?
                if (nr < bestNear) { bestNear = nr; best = n; }
            }
            if (best == null || bestNear > 0.2f) return null;   // no local hinge whose lever reaches the grab → no swing
            Log.LogInfo($"[manip] held-stick follow: assembly hinge '{SafeName(best)}' (body {bestNear:0.000}m from grab).");
            return best;
        }

        // A looser handle-name test than LeverMoverName: accepts 'lever'/'handle'/'crank'/'pull' but — unlike LeverMoverName
        // — does NOT reject on 'lock', because the punchcard submit's real handle is literally named 'Locking Lever'. Still
        // rejects the lock PIN ('LeverLock'/'lever lock'), which is hardware, not the handle.
        private static bool LeverHandleName(Transform t)
        {
            string s = SafeName(t); if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            if (s.Contains("leverlock") || s.Contains("lever lock")) return false;   // the lock pin, not a handle
            return s.Contains("lever") || s.Contains("handle") || s.Contains("crank") || s.Contains("pull");
        }

        private static bool IsWiringName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            return s.Contains("bezier") || s.Contains("bézier") || s.Contains("curve") || s.Contains("cable") || s.Contains("wire") || s.Contains("spline");
        }

        // True if a 'lever'-named node's ONLY visible mesh is a Bézier/cable/wire/spline curve — i.e. it's cosmetic WIRING
        // running along a stick, not the stick itself. The charge rammer's 'BigLever.001' has a single child 'BézierCurve.010';
        // we drove it and the user saw "the wiring move, not the lever stick." Reject so the real rod '.PowderRamLever' wins.
        private static bool IsWiringLever(Transform t)
        {
            // STRUCTURAL DECOY: a lever-named node whose every DIRECT CHILD is a wiring curve is a wiring bracket, not a rod —
            // even when its OWN renderer is a small enabled stub that the visible-mesh test below would keep. 'BigLever.001'
            // (sole child 'BézierCurve.010') is exactly this; the real rod '.PowderRamLever' has a 'Cube.030' rod child + LODs,
            // and every WORKING lever has a real mesh child (.RotateLever/.ShellRamLever/.ChargeLever/.LeverLock.005/...). So
            // this rejects the charge-rammer decoy on the FIRST grab (no learn capture needed) without touching any real lever.
            int cc = 0; try { cc = t.childCount; } catch { cc = 0; }
            if (cc > 0)
            {
                bool allWiring = true;
                for (int i = 0; i < cc; i++)
                {
                    Transform c = null; try { c = t.GetChild(i); } catch { }
                    if (c == null) continue;
                    if (!IsWiringName(SafeName(c))) { allWiring = false; break; }
                }
                if (allWiring) return true;
            }
            Renderer[] rs = null; try { rs = t.GetComponentsInChildren<Renderer>(true); } catch { }
            if (rs == null || rs.Length == 0) return false;
            bool sawEnabled = false;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i]; if (r == null) continue;
                bool en = false; try { en = r.enabled && r.gameObject.activeInHierarchy; } catch { }
                if (!en) continue;   // only meshes that actually RENDER count
                sawEnabled = true;
                string s = null; try { s = r.transform.name; } catch { }
                bool wiringName = IsWiringName(s);
                // 'BigLever.001's own renderer is enabled but its mesh is visually NEGLIGIBLE (a tiny stub) — so a name test
                // alone kept it. Also treat a renderer with tiny world bounds as non-stick: a real rod is ≥~0.1 m across.
                float size = 1f; try { size = r.bounds.size.magnitude; } catch { }
                bool tiny = size < 0.10f;
                if (!wiringName && !tiny) return false;   // a SUBSTANTIAL non-wiring mesh → it's a real stick, keep it
            }
            return sawEnabled;       // every visible mesh under it is wiring or a negligible stub
        }

        // Any renderer in t's subtree that ACTUALLY renders (enabled + active) — distinguishes a real visible stick from a
        // logical/hidden mechanism node. The punchcard's 'Locking Lever' subtree renders NOTHING (nothing turned red), so we
        // must not drive it; the visible handle is the orb-cap, which SLIDES.
        private static bool HasVisibleEnabledMesh(Transform t)
        {
            Renderer[] rs = null; try { rs = t == null ? null : t.GetComponentsInChildren<Renderer>(true); } catch { }
            if (rs == null) return false;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i]; if (r == null) continue;
                try { if (r.enabled && r.gameObject.activeInHierarchy) return true; } catch { }
            }
            return false;
        }

        // A LEVER-named, visible-mesh node ANYWHERE in the control's bounded assembly whose HINGE ORIGIN sits at the hand —
        // the real handle when it lives in a SIBLING's subtree (not an ancestor, sibling, or descendant of the grabbed
        // button, so tiers 1-3 all miss it). The punchcard submit's handle is 'Locking Lever' (→ 'Lever'/'Big Lever', with
        // an FMOD 'Lever Rotation' provider), a child of a sibling of the grabbed 'Universal Button', origin 0.29 m from the
        // grab. This is exactly what the [stick] dump surfaces as the smallest 'origin-near' lever. Rotating it about its own
        // origin swings the handle to the hand. Buried (one-directional, rest→press) like the other mechanism levers.
        private Transform FindAssemblyLever(Transform grabbed, Vector3 grabPoint)
        {
            Transform root = BoundedAssemblyRoot(grabbed);
            Transform[] all = null; try { all = root.GetComponentsInChildren<Transform>(true); } catch { }
            if (all == null) return null;
            Transform best = null; float bestOrigin = float.MaxValue; int bestDepth = int.MaxValue;
            Transform bestBody = null; float bestBodyNear = float.MaxValue;   // far-hinge fallback (charge rammer '.PowderRamLever')
            for (int i = 0; i < all.Length; i++)
            {
                var n = all[i]; if (n == null || n == grabbed) continue;
                if (!LeverHandleName(n) || !HasVisibleMesh(n)) continue;
                if (IsDescendantOf(grabbed, n)) continue;            // never an ancestor of the grab (that's the console)
                if (IsWiringLever(n)) continue;                      // skip a 'lever' whose only mesh is a Bézier/cable (charge rammer 'BigLever.001')
                float o; try { o = Vector3.Distance(n.position, grabPoint); } catch { continue; }
                float nr = NearestApproach(n, grabPoint);
                if (o <= 0.4f)                                       // PRIMARY: a lever hinged AT the hand (punchcard 'Locking Lever')
                {
                    int d = Depth(n, root);
                    // nearest origin wins; on a near-tie prefer the SHALLOWER node (the lever assembly, not a leaf mesh under it)
                    if (o < bestOrigin - 0.02f || (Mathf.Abs(o - bestOrigin) <= 0.02f && d < bestDepth))
                    { bestOrigin = o; best = n; bestDepth = d; }
                }
                else if (o <= 2.0f && nr <= 0.3f && nr < bestBodyNear)
                {
                    // FALLBACK: a LONG lever hinged far away (origin ≤2 m) but whose BODY reaches the grab — the charge
                    // rammer's '.PowderRamLever' hinges ~1.25 m down at the breech, the rod+orb you hold is its tip.
                    bestBodyNear = nr; bestBody = n;
                }
            }
            if (best == null && bestBody != null)
            {
                Log.LogInfo($"[manip] held-stick follow: assembly lever '{SafeName(bestBody)}' (long lever, body {bestBodyNear:0.000}m from grab; hinge far).");
                return bestBody;
            }
            if (best == null) return null;
            Log.LogInfo($"[manip] held-stick follow: assembly lever '{SafeName(best)}' (origin {bestOrigin:0.000}m from grab).");
            return best;
        }

        // The grabbed control's own ancestor used as the hinge WITHOUT the '<Name> Parent' naming convention. Some switches
        // hinge on a small per-control assembly node that simply isn't named '*Parent' — the review-console toggle
        // ('Universal Switch Button') sits under '.Check Switch', an 18 cm node carrying ONLY this one switch; rotating it
        // swings the toggle head (orb-cap + knob) about its base. FindPivotNode/FindAssemblyParentHinge miss it because they
        // require the literal 'Parent' name. Accept the nearest ancestor (up to 3) that carries visible mesh, is SMALL
        // (per-control, ext ≤ 0.6 m — never the whole '.Review Console Parent'/console), reaches the grab, and governs no
        // OTHER switch. Bidirectional toggle (not buried). Only reached after the named-'*Parent' tiers fail.
        private Transform FindParentHinge(Transform grabbed, Vector3 grabPoint)
        {
            Transform t = grabbed.parent; int up = 0;
            while (t != null && up < 3)
            {
                if (HasVisibleMesh(t) && !HingeGovernsForeignSwitch(t, grabbed))
                {
                    float ext = NodeExtent(t);
                    float near = NearestApproach(t, grabPoint);
                    if (ext <= 0.6f && near <= 0.2f)
                    {
                        Log.LogInfo($"[manip] held-stick follow: parent hinge '{SafeName(t)}' (ext {ext:0.00}m, body {near:0.000}m from grab).");
                        return t;
                    }
                }
                t = t.parent; up++;
            }
            return null;
        }

        // Geometric extent: how far the farthest descendant transform sits from this node's own origin. Distinguishes a
        // per-control assembly (small) from a shared console/turret root (huge). Same measure the [stick] dump prints as 'ext'.
        private static float NodeExtent(Transform t)
        {
            float extent = 0f;
            Vector3 o; try { o = t.position; } catch { return 0f; }
            Transform[] kids = null; try { kids = t.GetComponentsInChildren<Transform>(true); } catch { }
            if (kids != null)
                for (int i = 0; i < kids.Length; i++)
                {
                    var k = kids[i]; if (k == null) continue;
                    Vector3 kp; try { kp = k.position; } catch { continue; }
                    float e = Vector3.Distance(kp, o); if (e > extent) extent = e;
                }
            return extent;
        }

        // A named '*Lever'/handle DESCENDANT of the grabbed control whose body reaches the grab — the visible handle when
        // there's no '<Name> Parent' hinge to find (dispenser '.ChargeLever', charge rammer '.PowderRamLever'). Driving it
        // moves ONLY the lever, leaving the lights/drums/gears that share the grabbed button alone (those failed when we
        // rotated the whole grabbed node). Excludes gears/drums/lights/locks via LeverMoverName; picks the lever whose body
        // comes closest to the grab. Purely geometric — works on the very first grab, no learn capture needed.
        private Transform FindLeverDescendant(Transform grabbed, Vector3 grabPoint)
        {
            Transform[] all = null; try { all = grabbed.GetComponentsInChildren<Transform>(true); } catch { }
            if (all == null) return null;
            Transform best = null; float bestNear = float.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                var n = all[i]; if (n == null || n == grabbed) continue;
                if (!LeverMoverName(n)) continue;
                if (RendererKind(n) == "rNONE") continue;             // must have its own visible geometry
                float nr = NearestApproach(n, grabPoint);
                if (nr < bestNear) { bestNear = nr; best = n; }
            }
            if (best == null || bestNear > 0.35f) return null;        // no lever whose body reaches the grab → fall through
            Log.LogInfo($"[manip] held-stick follow: lever descendant '{SafeName(best)}' (body {bestNear:0.000}m from grab).");
            return best;
        }

        // A captured LEVER-named ROTATION mover whose body reaches the grab — the STICK to swing when there's no '*Parent'
        // hinge and the lever lives OUTSIDE the grabbed subtree (charge rammer '.PowderRamLever', a sibling whose hinge is
        // ~1.2 m from the grab but whose arm passes through your hand; the tip orb-light rides it via a constraint). Scans
        // the learn cache; requires rotation-dominant motion (a hinge, not a slider) and the arm to actually reach the
        // grab. Rotate-follow then uses TryCapturedHingeAxisWorld for the fixed axis/direction.
        private Transform FindCapturedLeverHinge(Transform grabbed, Vector3 grabPoint)
        {
            if (_scrubKey == null) return null;
            if (!_scrubCache.TryGetValue(_scrubKey, out var nodes) || nodes == null) return null;
            Transform best = null; float bestNear = float.MaxValue;
            for (int k = 0; k < nodes.Length; k++)
            {
                var T = nodes[k].T; if (T == null) continue;
                if (!LeverMoverName(T)) continue;
                float rotDeg = Quaternion.Angle(nodes[k].RestRot, nodes[k].PressRot);
                float trans = Vector3.Distance(nodes[k].RestPos, nodes[k].PressPos);
                if (rotDeg < 5f || rotDeg < trans * 60f) continue;   // rotation-dominant only (a hinge, not a slider)
                float nr = NearestApproach(T, grabPoint);
                if (nr < bestNear) { bestNear = nr; best = T; }
            }
            if (best == null || bestNear > 0.6f) return null;        // its body must actually reach the hand
            // NOTE: the hinge ORIGIN is allowed to be far (charge rammer '.PowderRamLever' hinges ~1.26 m away at the
            // breech and the orb-tip is what you hold) — it's a LONG lever, not a remote mechanism. The orb rides it via
            // co-rotation in DriveRider, so the whole stick+orb swings about the far hinge as the hand moves.
            Log.LogInfo($"[manip] held-stick follow: captured lever hinge '{SafeName(best)}' (body {bestNear:0.000}m from grab).");
            return best;
        }

        // The in-hand mesh that the game moves by a long captured TRANSLATION (the charge rammer cap 'Cylinder.001' rides
        // the ram lever ~1.2 m). Among the learned movers that sit UNDER the grabbed control, pick the one with the
        // largest captured WORLD travel and return it + that travel direction (world) + distance — so the follow can slide
        // the actual in-hand cap along its real arc instead of rotating a button that has no stick. The remaining movers
        // (the lever, gears) are siblings, so they keep scrub-replaying in step. Null until there's a capture (2nd grab).
        private Transform FindCapturedCapMover(Transform grabbed, out Vector3 slideAxisW, out float travelMax)
        {
            slideAxisW = Vector3.zero; travelMax = 0f;
            if (grabbed == null || _scrubKey == null) return null;
            // Scan the LEARN CACHE, not _activeMovers: the cap 'Cylinder.001' carries a ParentConstraint, so the scrub
            // resolve drops it from _activeMovers (it skips constraint-pinned followers) — but the cache keeps it. We
            // disable that constraint when we drive it (see _leverCon below), so our slide writes hold.
            if (!_scrubCache.TryGetValue(_scrubKey, out var nodes) || nodes == null) return null;
            Transform best = null; float bestTravel = 0f; Vector3 bestDirW = Vector3.zero;
            for (int k = 0; k < nodes.Length; k++)
            {
                var T = nodes[k].T;
                if (T == null) continue;
                if (T != grabbed && !IsDescendantOf(T, grabbed)) continue;   // only meshes under the grabbed control
                Transform par = T.parent; if (par == null) continue;
                Vector3 dW; try { dW = par.TransformVector(nodes[k].PressPos - nodes[k].RestPos); } catch { continue; }   // local Δ → world
                float tr = dW.magnitude;
                if (tr <= bestTravel) continue;
                bestTravel = tr; best = T; bestDirW = dW;
            }
            if (best == null || bestTravel < 0.10f) return null;   // need a real ≥10 cm travel to be the riding tip-cap
            slideAxisW = bestDirW.normalized; travelMax = bestTravel;
            Log.LogInfo($"[manip] held-stick follow: captured cap '{SafeName(best)}' slide-follow (travel {bestTravel:0.00}m).");
            return best;
        }

        // Set a mesh up as a RIDER: capture its world pose now and RIGIDLY co-move it with the swung stick's exact
        // transform delta each frame (DriveRider), so a held orb-cap that lives OUTSIDE the lever's subtree stays glued to
        // the swinging tip instead of hanging dead in the air. Its ParentConstraint is disabled so our writes hold;
        // RestoreScrubDrivers re-enables it on release. Pose is captured WHILE the constraint is still active (orb at its
        // correct spot), so frame 1 (identity swing) leaves it exactly where it was — no snap.
        private void SetRider(Transform rider, Vector3 slideAxisW, float travel)
        {
            if (rider == null) return;
            _riderT = rider; _riderParent = rider.parent; _riderRestLocal = rider.localPosition;
            _riderSlideAxisW = slideAxisW; _riderTravel = travel;
            try { _riderRestPosW = rider.position; _riderRestRotW = rider.rotation; } catch { }
            try { _riderCon = rider.GetComponent<UnityEngine.Animations.ParentConstraint>(); } catch { _riderCon = null; }
            if (_riderCon != null) { try { _riderConWasActive = _riderCon.constraintActive; _riderCon.constraintActive = false; } catch { } }
            Log.LogInfo($"[manip] held-stick follow: + rider '{SafeName(rider)}' co-moves with the stick (glued to the tip).");
        }

        // Register an extra handle part as a rigid rider: capture its world pose now and disable its constraint so our writes
        // hold. Skips the driven stick, the primary rider, and duplicates. RestoreScrubDrivers re-enables the constraint on release.
        private void AddRider(Transform t)
        {
            if (t == null || t == _leverT || t == _riderT) return;
            if (_riders == null) _riders = new System.Collections.Generic.List<RiderX>();
            for (int i = 0; i < _riders.Count; i++) if (_riders[i].T == t) return;
            var e = new RiderX { T = t };
            try { e.RestPosW = t.position; e.RestRotW = t.rotation; } catch { }
            try { e.Con = t.GetComponent<UnityEngine.Animations.ParentConstraint>(); } catch { e.Con = null; }
            if (e.Con != null) { try { e.ConWasActive = e.Con.constraintActive; e.Con.constraintActive = false; } catch { } }
            _riders.Add(e);
            Log.LogInfo($"[manip] held-stick follow: + extra rider '{SafeName(t)}' co-moves with the handle.");
        }

        // Gather the REST of the handle cluster so the WHOLE thing tracks the hand, not just the one node we drive. Two
        // passes, both tightly gated (small, visible, right at the grab, not under the driven stick, never an ancestor of the
        // grab = the console body): (1) GEOMETRIC — nearby constraint-driven meshes (orb-lights/caps the game glues to a
        // tip); works on the 1st grab. (2) CAPTURED — on a 2nd+ grab the learn cache lists every node the game moves; add the
        // visible small ones near the handle (a slider connector with no constraint of its own that our animator-disable would
        // otherwise freeze). Each becomes a rigid rider co-moved by the stick's delta in DriveRider.
        private void GatherExtraRiders(Transform grabbed, Vector3 grabPoint)
        {
            if (grabbed == null || _leverT == null) return;
            // CRITICAL: never sweep the broad assembly — the reloading console packs a 'Cylinder.001' orb on EVERY control,
            // so a radius search around the grab dragged ADJACENT controls' orbs (the pass-26 regression). Both passes below
            // are NEIGHBOUR-SAFE by construction: pass A is scoped to THIS control's own learn capture (keyed by control),
            // pass B to the grabbed control's OWN descendants — neither can reach a different control's handle.
            Vector3 leverPos; try { leverPos = _leverT.position; } catch { return; }

            // PASS A — CAPTURED, PER-CONTROL: a constraint-driven mover in THIS control's cache, mounted on the driven lever
            // (small + near its body). The console's orb 'Cylinder.001' (captured, world-moves 0.39m, glued by a
            // ParentConstraint) is held OFF the lever we drive so it sits dead — co-move it. The doors / rail-cart in the same
            // capture have NO constraint and are large → excluded (they keep moving via the scrub, paced by _leverFollowFrac).
            if (_scrubKey != null && _scrubCache.TryGetValue(_scrubKey, out var nodes) && nodes != null)
                for (int k = 0; k < nodes.Length; k++)
                {
                    Transform t = nodes[k].T;
                    if (!RiderCandidate(t, grabbed, 0.30f)) continue;
                    bool hasCon = false; try { hasCon = t.GetComponent<UnityEngine.Animations.ParentConstraint>() != null; } catch { }
                    if (!hasCon) continue;
                    // Measure to the GRAB POINT (the handle tip the orb sits at), NOT the lever's hinge ORIGIN — the console
                    // lever hinges ~0.78m down at its base, so a tip-origin gate wrongly excluded the orb. Generous radius is
                    // safe: this pass only sees THIS control's own captured movers, so it can never reach a neighbour's orb.
                    if (NearestApproach(t, grabPoint) > 0.40f) continue;
                    AddRider(t);
                }

            // PASS B — GRABBED SUBTREE ONLY: a visible part TOUCHING the driven node that the game does NOT animate (absent
            // from the capture) — a connector stick beside the sliding orb-cap. Scoped to the grabbed control's own
            // descendants and required to physically touch the driven node, so it can never reach an adjacent handle.
            Transform[] kids = null; try { kids = grabbed.GetComponentsInChildren<Transform>(true); } catch { }
            if (kids != null)
                for (int i = 0; i < kids.Length; i++)
                {
                    Transform t = kids[i];
                    if (!RiderCandidate(t, grabbed, 0.30f)) continue;
                    float toLever = NearestApproach(t, leverPos);
                    float toGrab; try { toGrab = Vector3.Distance(t.position, grabPoint); } catch { toGrab = float.MaxValue; }
                    if (toLever > 0.10f && toGrab > 0.10f) continue;      // must physically touch the driven node / grab
                    AddRider(t);
                }

        }

        // Enable every renderer in t's subtree for the grab (saving prior state), so a mesh the game draws via GPU instancing
        // (per-object renderer disabled) shows at the transform we co-move. RestoreScrubDrivers puts the enables back on release.
        private void ForceEnableRenderers(Transform t)
        {
            if (t == null) return;
            if (_forcedRends == null) _forcedRends = new System.Collections.Generic.List<RendEntry>();
            Renderer[] rs = null; try { rs = t.GetComponentsInChildren<Renderer>(true); } catch { }
            if (rs == null) return;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i]; if (r == null) continue;
                bool en = false; try { en = r.enabled; } catch { }
                _forcedRends.Add(new RendEntry { R = r, En = en });
                try { r.enabled = true; } catch { }
            }
        }

        // SetActive(true) on t's whole subtree AND every inactive ancestor up to (and including) stopRoot, so a switched-off
        // mesh becomes active-in-hierarchy and renders at the pose we co-move. Saved → RestoreScrubDrivers switches them back
        // on release. Logs the chain it had to turn on (reveals how deep the inactivity goes).
        private void ForceActivate(Transform t, Transform stopRoot)
        {
            if (t == null) return;
            if (_forcedActive == null) _forcedActive = new System.Collections.Generic.List<ActEntry>();
            int turnedOn = 0;
            Transform[] subs = null; try { subs = t.GetComponentsInChildren<Transform>(true); } catch { }
            if (subs != null)
                for (int i = 0; i < subs.Length; i++)
                {
                    var go = subs[i] == null ? null : subs[i].gameObject; if (go == null) continue;
                    bool a = true; try { a = go.activeSelf; } catch { }
                    if (!a) { _forcedActive.Add(new ActEntry { GO = go, Was = false }); try { go.SetActive(true); turnedOn++; } catch { } }
                }
            Transform p = t.parent; int guard = 0;
            while (p != null && guard++ < 32)
            {
                var go = p.gameObject;
                bool a = true; try { a = go.activeSelf; } catch { }
                if (!a) { _forcedActive.Add(new ActEntry { GO = go, Was = false }); try { go.SetActive(true); turnedOn++; } catch { } Log.LogInfo($"[stickC] force-activate ancestor '{SafeName(p)}'"); }
                if (p == stopRoot) break;
                p = p.parent;
            }
            Log.LogInfo($"[stickC] force-activate '{SafeName(t)}' turned on {turnedOn} GameObject(s)");
        }

        // True if any renderer exists ANYWHERE in t's subtree, enabled or not — distinguishes a real mesh node from a pure
        // logic/transform node, without requiring it to currently render (GPU-instanced handles read as not-enabled).
        private static bool HasAnyRenderer(Transform t)
        {
            Renderer[] rs = null; try { rs = t == null ? null : t.GetComponentsInChildren<Renderer>(true); } catch { }
            return rs != null && rs.Length > 0;
        }

        // A mesh qualifies as a co-moving handle part: not the stick/its subtree, not the grabbed control or an ancestor of
        // it (the console body), rendering, and no larger than maxExt (an orb/cap/connector or short handle, not a panel).
        // Proximity/scope are applied by the caller per pass.
        private bool RiderCandidate(Transform t, Transform grabbed, float maxExt)
        {
            if (t == null || t == _leverT || t == _riderT) return false;
            if (IsDescendantOf(t, _leverT)) return false;                 // under the driven stick → rides for free
            if (t == grabbed || IsDescendantOf(grabbed, t)) return false; // never an ancestor of the grab (the console body)
            if (!HasVisibleEnabledMesh(t)) return false;
            if (NodeExtent(t) > maxExt) return false;
            return true;
        }

        // The in-hand orb-cap ('Cylinder.001') the user actually holds: a mesh DESCENDANT of the grabbed button carrying a
        // ParentConstraint (the game glues it to a lever tip) that sits right at the grab point. Found GEOMETRICALLY (no
        // learn capture required, so it works on the very first grab) for the rider co-move when we swing a lever that the
        // orb is NOT under. Excludes the pivot and anything beneath it — those already ride the swing for free.
        private Transform FindOrbCapGeometric(Transform grabbed, Transform pivot, Vector3 grabPoint)
        {
            if (grabbed == null) return null;
            Transform[] kids = null; try { kids = grabbed.GetComponentsInChildren<Transform>(true); } catch { }
            if (kids == null) return null;
            Transform best = null; float bestNear = float.MaxValue;
            for (int i = 0; i < kids.Length; i++)
            {
                Transform t = kids[i]; if (t == null || t == grabbed) continue;
                if (pivot != null && (t == pivot || IsDescendantOf(t, pivot))) continue;   // under the stick → rides for free
                bool hasCon = false; try { hasCon = t.GetComponent<UnityEngine.Animations.ParentConstraint>() != null; } catch { }
                if (!hasCon || !HasVisibleMesh(t)) continue;                               // the orb-cap is a constraint-driven mesh
                float nr; try { nr = Vector3.Distance(t.position, grabPoint); } catch { continue; }
                if (nr < bestNear) { bestNear = nr; best = t; }
            }
            if (best == null || bestNear > 0.35f) return null;                             // must be the in-hand cap, at the grab
            return best;
        }

        // DIAG: tint every renderer under t to colour c, saving the originals to restore on release. material (not
        // sharedMaterial) gives a per-renderer instance so we never corrupt the shared asset; .color maps to URP Lit's
        // [MainColor] _BaseColor. Wrapped in try/catch per renderer so an exotic material can't break the grab.
        private void TintNode(Transform t, Color c)
        {
            if (t == null) return;
            if (_tints == null) _tints = new System.Collections.Generic.List<TintEntry>();
            Renderer[] rs = null; try { rs = t.GetComponentsInChildren<Renderer>(true); } catch { }
            if (rs == null) return;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i]; if (r == null) continue;
                try { var m = r.material; if (m == null) continue; _tints.Add(new TintEntry { R = r, C = m.color }); m.color = c; }
                catch { }
            }
        }

        private void RestoreTints()
        {
            if (_tints == null) return;
            for (int i = 0; i < _tints.Count; i++)
                try { var e = _tints[i]; if (e.R != null) { var m = e.R.material; if (m != null) m.color = e.C; } } catch { }
            _tints.Clear();
        }

        // A clean, STABLE hinge axis for an orb-stick lever that has no captured clip motion: the lever's local cardinal
        // (right/up/forward) MOST PERPENDICULAR to the grab arm. A real lever hinges ACROSS its length, so this is the
        // physical swing axis — and unlike the per-frame cross-product it never collapses onto the rod's long axis (which
        // merely twisted the stick in place, moving only the offset orb). Independent of hand jitter → same axis every grab.
        private Vector3 StableHingeAxisW(Transform pivot, Vector3 grabPoint)
        {
            if (pivot == null) return Vector3.zero;
            Vector3 armW; try { armW = grabPoint - pivot.position; } catch { return Vector3.zero; }
            if (armW.sqrMagnitude < 1e-8f) return Vector3.zero;
            Vector3 armDir = armW.normalized;
            Vector3[] axes; try { axes = new[] { pivot.right, pivot.up, pivot.forward }; } catch { return Vector3.zero; }
            Vector3 best = Vector3.zero; float bestPerp = -1f;
            for (int i = 0; i < axes.Length; i++)
            {
                if (axes[i].sqrMagnitude < 1e-8f) continue;
                float perp = 1f - Mathf.Abs(Vector3.Dot(axes[i].normalized, armDir));   // 1 = perpendicular to the rod
                if (perp > bestPerp) { bestPerp = perp; best = axes[i].normalized; }
            }
            return best;
        }

        // The hinge's FIXED axis + direction, taken from the game's own captured motion: among the learned movers (ready
        // from the 2nd grab), the one that is _leverT or rigidly under it (the lever mesh) rotates rest→press about the
        // true hinge axis. We return that axis in WORLD space, signed so a POSITIVE swing = rest→press (the activation
        // direction). DriveHeldStick then clamps swing to [0,max], so a wrong-way pull reads negative → clamps to 0 → the
        // lever can only move the correct way (fixes "locks onto whichever direction the controller first moved"). False
        // when there's no capture yet (the very first learn grab) → caller auto-infers from the first swing instead.
        private bool TryCapturedHingeAxisWorld(out Vector3 axisW)
        {
            axisW = Vector3.zero;
            if (_activeMovers == null || _leverT == null) return false;
            float bestAngle = 0f; Quaternion bestDq = Quaternion.identity; Transform bestParent = null;
            for (int k = 0; k < _activeMovers.Count; k++)
            {
                var m = _activeMovers[k];
                if (m.T == null) continue;
                if (m.T != _leverT && !IsDescendantOf(m.T, _leverT)) continue;   // only the lever itself / its rigid children
                float ang = Quaternion.Angle(m.RestRot, m.PressRot);
                if (ang <= bestAngle) continue;
                bestAngle = ang; bestDq = m.PressRot * Quaternion.Inverse(m.RestRot); bestParent = m.T.parent;
            }
            if (bestAngle < 3f || bestParent == null) return false;             // no meaningful captured rotation
            float a; Vector3 axLocal; bestDq.ToAngleAxis(out a, out axLocal);   // +a about axLocal does rest→press
            if (axLocal.sqrMagnitude < 1e-8f) return false;
            Vector3 w = bestParent.rotation * axLocal.normalized;               // mover-parent frame → world
            if (_switchMotion.Flip) w = -w;
            if (w.sqrMagnitude < 1e-8f) return false;
            axisW = w.normalized; return true;
        }

        // Name reads like a movable handle, and NOT a counter/indicator/cog that merely spins or displays.
        private static bool LeverMoverName(Transform t)
        {
            string s = SafeName(t); if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            bool handle = s.Contains("lever") || s.Contains("ram") || s.Contains("rotate") || s.Contains("handle")
                       || s.Contains("crank") || s.Contains("pull") || s.Contains("swing");
            bool notHandle = s.Contains("gear") || s.Contains("drum") || s.Contains("number") || s.Contains("wheel")
                          || s.Contains("cog") || s.Contains("light") || s.Contains("lamp") || s.Contains("lock");
            return handle && !notHandle;
        }

        // Highest ancestor of `from` whose whole subtree is still bounded (≤1200 nodes) — the control's local assembly,
        // never the whole turret. Used to scope hinge search and animator disabling so we never freeze the turret rig.
        private static Transform BoundedAssemblyRoot(Transform from)
        {
            Transform root = from, probe = from; int climb = 0;
            while (probe != null && climb <= 6)
            {
                int sz = int.MaxValue; try { sz = probe.GetComponentsInChildren<Transform>(true).Length; } catch { }
                if (sz <= 1200) root = probe; else break;
                probe = probe.parent; climb++;
            }
            return root;
        }

        // Disable every animator from the grabbed control AND from the hinge up to (and including) the local-assembly
        // root — the reloading-console / arming rigs idle their lever back to REST every frame, overwriting our swing, and
        // that animator sits ABOVE the hinge (not on it), so the on-hinge disable alone missed it. Bounded at the assembly
        // root so the turret rig is never touched. Recorded in _scrubAnimators → RestoreScrubDrivers re-enables on release.
        private void DisableAssemblyAnimators(Transform grabbed, Transform pivot)
        {
            if (_scrubAnimators == null) _scrubAnimators = new System.Collections.Generic.List<AnimEntry>();
            Transform stop = BoundedAssemblyRoot(grabbed);
            DisableAnimatorsUpTo(grabbed, stop);
            DisableAnimatorsUpTo(pivot, stop);
            // Also every animator INSIDE the hinge's subtree — the lever mesh's driver can sit on/below the hinge (the
            // reload console has 15 animators), which the upward climbs above don't reach.
            Animator[] kids = null; try { kids = pivot.GetComponentsInChildren<Animator>(true); } catch { }
            if (kids != null) for (int i = 0; i < kids.Length; i++) DisableScrubAnimator(kids[i]);
        }

        private void DisableAnimatorsUpTo(Transform from, Transform stop)
        {
            Transform t = from; int guard = 0;
            while (t != null && guard++ < 32)
            {
                Animator a = null; try { a = t.GetComponent<Animator>(); } catch { }
                if (a != null) DisableScrubAnimator(a);
                if (t == stop) break;
                t = t.parent;
            }
        }

        private static bool NameHasParent(Transform t)
        {
            string s = SafeName(t);
            return !string.IsNullOrEmpty(s) && s.IndexOf("parent", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Closest any part of t's body (itself + descendants) comes to point p — a hinge whose mesh is near the grab wins.
        private static float NearestApproach(Transform t, Vector3 p)
        {
            float near = float.MaxValue;
            Transform[] kids = null; try { kids = t.GetComponentsInChildren<Transform>(true); } catch { }
            if (kids != null)
                for (int i = 0; i < kids.Length; i++)
                {
                    var k = kids[i]; if (k == null) continue;
                    float d; try { d = Vector3.Distance(k.position, p); } catch { continue; }
                    if (d < near) near = d;
                }
            if (near == float.MaxValue) { try { near = Vector3.Distance(t.position, p); } catch { near = 0f; } }
            return near;
        }

        // Snap a world direction to the hinge's nearest LOCAL axis (±X/±Y/±Z in world), signed to match. Levers in this
        // rig hinge/slide about an authored local axis, so snapping turns the user's noisy first-shove into a clean,
        // rig-aligned axis (kills the skewed-diagonal swing and the random slide direction).
        private Vector3 SnapToPivotAxis(Vector3 dirW)
        {
            if (_leverT == null || dirW.sqrMagnitude < 1e-8f) return dirW;
            Vector3 best = _leverT.right; float bestDot = -1f;
            Vector3[] axes; try { axes = new[] { _leverT.right, _leverT.up, _leverT.forward }; } catch { return dirW; }
            for (int i = 0; i < axes.Length; i++)
            {
                if (axes[i].sqrMagnitude < 1e-8f) continue;
                float d = Mathf.Abs(Vector3.Dot(dirW, axes[i].normalized));
                if (d > bestDot) { bestDot = d; best = axes[i]; }
            }
            best = best.normalized;
            if (Vector3.Dot(dirW, best) < 0f) best = -best;   // sign so swing/slide direction matches the user's intent
            return best;
        }

        // Make the held stick follow the hand: ROTATE its hinge to point the grab arm at the hand (default), or SLIDE the
        // hinge along the pull axis for translate-type controls (e.g. the War Horn pulley). Real-hand driven → visible.
        private void DriveHeldStick()
        {
            if (_leverT == null) return;
            Vector3 pivotW = _leverParent != null ? _leverParent.TransformPoint(_leverPivotLocal) : _leverT.position;
            _leverPivotW = pivotW; _leverSwingDqW = Quaternion.identity; _leverSlideDeltaW = Vector3.zero;   // rider co-move delta (set below)

            if (_leverSlide)
            {
                // SLIDE: translate the hinge along ONE axis by the hand's travel (clamped). Work in the PARENT'S LOCAL
                // frame (localPosition + InverseTransformVector) so the handle RIDES its parent and only adds a bounded
                // local offset — writing world .position rode the parent's motion amplified and flung it metres away.
                // PIN rotation to rest so nothing can spin the hinge and sling its far body.
                try { _leverT.localRotation = _leverRestLocalR; } catch { }
                Vector3 travelW = _handPos - _leverTip0W;
                if (!_leverHasSlideAxis && travelW.magnitude >= 0.03f) { _leverSlideAxisW = SnapToPivotAxis(travelW.normalized); _leverHasSlideAxis = true; }
                if (_leverHasSlideAxis)
                {
                    // Bounded to the captured throw (captured slider) or the menu/auto reach, resolved at grab time.
                    float off = Mathf.Clamp(Vector3.Dot(travelW, _leverSlideAxisW), 0f, _leverSlideMax);
                    Vector3 localDelta = _leverParent != null ? _leverParent.InverseTransformVector(_leverSlideAxisW * off) : _leverSlideAxisW * off;
                    _leverT.localPosition = _leverPivotLocal + localDelta;
                    _leverSlideDeltaW = _leverSlideAxisW * off;          // rider slides by the same world delta
                    if (off > _leverTipTravel) _leverTipTravel = off;   // DIAG: actual slide distance (parent-ride excluded)
                    _leverFollowFrac = _leverSlideMax > 1e-3f ? Mathf.Clamp01(off / _leverSlideMax) : 0f;
                }
            }
            else
            {
                // ROTATE: PIN position to rest, then swing about a FIXED hinge axis. The axis is the rig's local axis nearest
                // the user's first swing (snapped) — a clean hinge axis, not the skewed free cross-product that tilted.
                try { _leverT.localPosition = _leverPivotLocal; } catch { }
                Vector3 armW = _leverParent != null ? _leverParent.TransformDirection(_leverArmParentDir) : _leverArmParentDir;
                Vector3 toHand = _handPos - pivotW;
                if (armW.sqrMagnitude > 1e-8f && toHand.sqrMagnitude > 1e-8f)
                {
                    if (!_leverHasRotAxis)
                    {
                        Vector3 a = Vector3.Cross(armW.normalized, toHand.normalized);
                        if (a.sqrMagnitude >= 2e-3f) { _leverRotAxisW = SnapToPivotAxis(a.normalized); _leverHasRotAxis = true; }
                    }
                    if (_leverHasRotAxis)
                    {
                        Vector3 ap = Vector3.ProjectOnPlane(armW, _leverRotAxisW);
                        Vector3 hp = Vector3.ProjectOnPlane(toHand, _leverRotAxisW);
                        if (ap.sqrMagnitude > 1e-8f && hp.sqrMagnitude > 1e-8f)
                        {
                            // Buried mechanism levers are one-directional (rest→press only); named toggle hinges swing both ways.
                            // Buried mechanism levers are one-directional, EXCEPT the orb-stick archetype (punchcard, charge
                            // rammer): its axis is a stable perpendicular guess with no fixed sign, so allow both ways.
                            float swingLo = (_leverBuried && !_leverStickArchetype) ? 0f : -_leverMaxAngle;
                            float swing = Mathf.Clamp(Vector3.SignedAngle(ap, hp, _leverRotAxisW), swingLo, _leverMaxAngle);
                            Quaternion restW = _leverParent != null ? _leverParent.rotation * _leverRestLocalR : _leverRestLocalR;
                            _leverSwingDqW = Quaternion.AngleAxis(swing, _leverRotAxisW);   // rider co-rotates by this about pivotW
                            _leverT.rotation = _leverSwingDqW * restW;
                            _leverFollowFrac = _leverMaxAngle > 0.01f ? Mathf.Clamp01(Mathf.Abs(swing) / _leverMaxAngle) : 0f;
                        }
                    }
                }
            }
            DriveRider();   // a tip mesh outside the stick's subtree (charge rammer orb-light) rides the swing, synced

            // DIAG (rotate only): world travel of the grabbed point. For slide we report the clamped slide distance above
            // instead — the TransformPoint readout is inflated by any parent ride, which is what made the horn read 10 m.
            if (!_leverSlide)
                try
                {
                    Vector3 grabW = _leverT.TransformPoint(_leverGrabLocal);
                    float td = Vector3.Distance(grabW, _leverTip0W);
                    if (td > _leverTipTravel) _leverTipTravel = td;
                }
                catch { }
        }

        // Drive the RIDER (charge rammer orb-light) by RIGIDLY co-moving it with the stick's exact transform delta this
        // frame — rotate it about the same hinge by the same angle (or translate it by the same slide) as the stick — so it
        // stays glued to the swinging tip instead of decoupling. Captured world pose at grab; constraint/animator disabled
        // so the write holds; on release they restore and it returns to the game's control.
        private void DriveRider()
        {
            if (_riderT != null)
            {
                try
                {
                    if (_leverSlide)
                        _riderT.position = _riderRestPosW + _leverSlideDeltaW;
                    else
                    {
                        _riderT.position = _leverPivotW + _leverSwingDqW * (_riderRestPosW - _leverPivotW);
                        _riderT.rotation = _leverSwingDqW * _riderRestRotW;
                    }
                }
                catch { }
            }
            // Every extra handle part co-moves by the SAME delta (slide translation or swing about the hinge) — drives the
            // console orb / punchcard connector even when there's no single primary rider (_riderT == null).
            if (_riders != null)
                for (int i = 0; i < _riders.Count; i++)
                {
                    var e = _riders[i]; if (e.T == null) continue;
                    try
                    {
                        if (_leverSlide)
                            e.T.position = e.RestPosW + _leverSlideDeltaW;
                        else
                        {
                            e.T.position = _leverPivotW + _leverSwingDqW * (e.RestPosW - _leverPivotW);
                            e.T.rotation = _leverSwingDqW * e.RestRotW;
                        }
                    }
                    catch { }
                }
        }

        private void SampleLearnFrame()
        {
            if (_scrubTs == null) return;
            for (int i = 0; i < _scrubTs.Length; i++)
            {
                if (_scrubTs[i] == null) continue;
                if (_scrubTs[i] == _leverT) continue;   // the hinge is OUR synthetic swing — never record it as a mechanism mover
                float di = Vector3.Distance(_scrubTs[i].localPosition, _scrubRestP[i]) + Quaternion.Angle(_scrubTs[i].localRotation, _scrubRestR[i]) * 0.01f;
                if (di > _scrubNodePeak[i])
                {
                    _scrubNodePeak[i] = di;
                    _scrubNodePeakPos[i] = _scrubTs[i].localPosition; _scrubNodePeakRot[i] = _scrubTs[i].localRotation;
                    if (di > _scrubGlobalPeak) _scrubGlobalPeak = di;
                }
                if (_scrubRoot != null && _scrubRestRL != null)
                {
                    float wd = Vector3.Distance(_scrubRoot.InverseTransformPoint(_scrubTs[i].position), _scrubRestRL[i]);
                    if (wd > _scrubWorldPeak[i]) _scrubWorldPeak[i] = wd;
                }
            }
        }

        // Build the cache from the captured peaks. Caches the whole moved chain down to ScrubKeepMin, BUT never anything
        // without its own visible geometry — cameras (driving one moves your VIEW), audio emitters and pure empties are
        // skipped so the scrub only ever moves meshes.
        private void FinalizeLearnCache(string key)
        {
            if (_scrubTs == null || key == null || _scrubGlobalPeak < ScrubLearnMin) return;
            var list = new System.Collections.Generic.List<ScrubNode>();
            var dbg = new System.Text.StringBuilder();
            int reported = 0, skipped = 0;
            for (int i = 0; i < _scrubTs.Length; i++)
            {
                if (_scrubTs[i] == null || _scrubNodePeak[i] < ScrubKeepMin) continue;
                // #2: never drive non-geometry. rNONE = no renderer anywhere (camera/empty/audio); also guard Cameras.
                if (RendererKind(_scrubTs[i]) == "rNONE") { skipped++; continue; }
                bool isCam = false; try { isCam = _scrubTs[i].GetComponent<Camera>() != null; } catch { }
                if (isCam) { skipped++; continue; }
                list.Add(new ScrubNode { T = _scrubTs[i], Leaf = SafeName(_scrubTs[i]), RestPos = _scrubRestP[i], RestRot = _scrubRestR[i], PressPos = _scrubNodePeakPos[i], PressRot = _scrubNodePeakRot[i] });
                if (_scrubNodePeak[i] >= ScrubLearnMin && reported < 12)
                {
                    reported++;
                    dbg.Append($" [{SafeName(_scrubTs[i])} loc={_scrubNodePeak[i]:0.00} world={_scrubWorldPeak[i]:0.000} {RendererKind(_scrubTs[i])} d{Depth(_scrubTs[i], _scrubRoot)}]");
                }
            }
            if (list.Count > 0)
            {
                _scrubCache[key] = list.ToArray();
                Log.LogInfo($"[manip] learned switch '{key}': {list.Count} kept ({reported} strong, {skipped} non-mesh skipped) —{dbg} — will scrub next grab.");
            }
        }

        // Complete the post-release tail: build the cache from the now-full capture, then drop the learn arrays.
        private void FinalizeLearnTail()
        {
            try { FinalizeLearnCache(_learnTailKey); } catch { }
            _learnTail = false; _learnTailKey = null; _learnTriggered = false;
            _scrubTs = null; _scrubRestP = null; _scrubRestR = null;
            _scrubNodePeak = null; _scrubNodePeakPos = null; _scrubNodePeakRot = null;
            _scrubRoot = null; _scrubRestRL = null; _scrubWorldPeak = null; _scrubGlobalPeak = 0f;
        }

        // DIAG: dump the control's animator — controller, culling, and every parameter (name/type/value). If there's a
        // float that ranges 0..1 (a press/blend), driving THAT each frame (animator left enabled) is how the game poses
        // the mesh incl. IK — the only path that can move an IK-driven lever, since hand-writing rig nodes can't.
        private void DumpRig(Animator a)
        {
            try
            {
                if (a == null) { Log.LogInfo("[manip] rig: animator null"); return; }
                var rac = a.runtimeAnimatorController;
                Log.LogInfo($"[manip] rig '{SafeName(a.transform)}' en={a.enabled} cull={(int)a.cullingMode} ctrl={(rac != null ? SafeName(rac) : "<null>")}");
                int pc = 0; try { pc = a.parameterCount; } catch { }
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < pc; i++)
                {
                    try
                    {
                        var p = a.GetParameter(i);
                        string val;
                        switch (p.type)
                        {
                            case AnimatorControllerParameterType.Float: val = a.GetFloat(p.nameHash).ToString("0.00"); break;
                            case AnimatorControllerParameterType.Bool: val = a.GetBool(p.nameHash).ToString(); break;
                            case AnimatorControllerParameterType.Int: val = a.GetInteger(p.nameHash).ToString(); break;
                            default: val = "trig"; break;
                        }
                        sb.Append($" [{p.name}:{p.type}={val}]");
                    }
                    catch { }
                }
                Log.LogInfo($"[manip] rig params ({pc}):{sb}");
                // Also dump current state on layer 0 — the state name hash tells us which clip is live at rest.
                try { var st = a.GetCurrentAnimatorStateInfo(0); Log.LogInfo($"[manip] rig state0 hash={st.fullPathHash} len={st.length:0.00} nt={st.normalizedTime:0.00}"); } catch { }
            }
            catch (Exception e) { Log.LogInfo($"[manip] rig dump err: {e.Message}"); }
        }

        // Root the learn/scrub at the TOPMOST Animator ancestor of the control. Every node any involved animator can
        // move is a descendant of some animator, so this subtree contains the WHOLE authored motion — including the
        // dominant ancestor pivot whose rotation is the visible lever swing (a leaf's local tweak alone barely moves).
        // Falls back to a single parent climb if the chain has no Animator.
        private const int ScrubRootMaxClimb = 8;   // don't root above this many levels (avoid scanning the whole vehicle)
        private static Transform ScrubRoot(Transform lat)
        {
            if (lat == null) return lat;
            Transform top = null, r = lat;
            for (int i = 0; r != null && i <= ScrubRootMaxClimb; i++)
            {
                try { if (r.GetComponent<Animator>() != null) top = r; } catch { }
                r = r.parent;
            }
            if (top != null) return top;
            var f = lat;
            for (int i = 0; i < ScrubRootClimb && f != null && f.parent != null; i++) f = f.parent;
            return f;
        }

        // PROBE: depth of `t` below `root` (0 = is root, -1 = not under it).
        private static int Depth(Transform t, Transform root)
        {
            if (t == null || root == null) return -1;
            int d = 0; var r = t;
            while (r != null) { if (r == root) return d; r = r.parent; d++; if (d > 128) break; }
            return -1;
        }

        // PROBE: walk the ancestor chain from the control up to the scene root, logging where Animators and Renderers
        // live — tells us how high the rig goes and which ancestor carries the press animation.
        private void DumpAncestors(Transform from)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var r = from; int d = 0;
                while (r != null && d < 24)
                {
                    bool anim = false, rend = false;
                    try { anim = r.GetComponent<Animator>() != null; } catch { }
                    try { rend = r.GetComponent<Renderer>() != null; } catch { }
                    sb.Append($" {d}:{SafeName(r)}{(anim ? "(A)" : "")}{(rend ? "(R)" : "")}");
                    r = r.parent; d++;
                }
                Log.LogInfo($"[manip] ancestors:{sb}");
            }
            catch { }
        }

        // PROBE: dump every Animator inside the captured subtree (root + descendants) with its controller + param names.
        private void DumpAnimators(Transform root)
        {
            try
            {
                if (root == null) return;
                var anims = root.GetComponentsInChildren<Animator>(true);
                int n = anims != null ? anims.Length : 0;
                Log.LogInfo($"[manip] subtree animators: {n}");
                for (int i = 0; i < n && i < 8; i++) DumpRig(anims[i]);
            }
            catch { }
        }

        // PROBE: what kind of renderer drives this node's pixels — a MeshRenderer follows the node's own transform (so a
        // hand-written rotation shows), a SkinnedMeshRenderer follows BONES (so writing this transform shows nothing).
        // True if t's subtree contains any visible mesh (mesh or skinned). A real lever hinge always carries visible
        // geometry; a logic/camera rig (the map's 'RTS cam Parent', child 'RTS Camera Controler') carries none — that's
        // how we reject it as a held-stick hinge so grabbing the requisition-console lever stops swinging the map camera.
        private static bool HasVisibleMesh(Transform t)
        {
            try
            {
                if (t == null) return false;
                if (t.GetComponentInChildren<MeshRenderer>(true) != null) return true;
                if (t.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) return true;
            }
            catch { }
            return false;
        }

        private static string RendererKind(Transform t)
        {
            try
            {
                if (t == null) return "r?";
                if (t.GetComponent<SkinnedMeshRenderer>() != null) return "rSKIN";
                if (t.GetComponent<MeshRenderer>() != null) return "rMESH";
                if (t.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) return "rSKINchild";
                if (t.GetComponentInChildren<MeshRenderer>(true) != null) return "rMESHchild";
                return "rNONE";
            }
            catch { return "r!"; }
        }

        // PROBE: enumerate every SkinnedMeshRenderer in the learn subtree with its bone count + root bone. If the lever is
        // skinned, the fix is to drive the BONES (or the game animator), not the rig node — this confirms which.
        private void DumpSkin()
        {
            try
            {
                if (_scrubRoot == null) return;
                var smrs = _scrubRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                int n = smrs != null ? smrs.Length : 0;
                Log.LogInfo($"[manip] skinned renderers in subtree: {n}");
                for (int i = 0; i < n && i < 8; i++)
                {
                    var s = smrs[i];
                    int bones = 0; string rb = "<null>";
                    try { var ba = s.bones; bones = ba != null ? ba.Length : 0; } catch { }
                    try { if (s.rootBone != null) rb = SafeName(s.rootBone); } catch { }
                    Log.LogInfo($"[manip]   smr '{SafeName(s.transform)}' bones={bones} root={rb}");
                }
            }
            catch { }
        }

        // PROBE: the largest root-local (in-assembly) movers across the WHOLE learn subtree — these are what the eye sees
        // move on the real click, regardless of where their local-pose delta sits. If the top world-mover isn't in the
        // cached set (or is an ancestor with tiny local delta), that's the node we must drive to reproduce the swing.
        private void DumpTopWorldMovers()
        {
            try
            {
                if (_scrubTs == null || _scrubWorldPeak == null) return;
                int n = _scrubTs.Length;
                var idx = new System.Collections.Generic.List<int>();
                for (int i = 0; i < n; i++) if (_scrubTs[i] != null && _scrubWorldPeak[i] > 0.002f) idx.Add(i);
                idx.Sort((a, b) => _scrubWorldPeak[b].CompareTo(_scrubWorldPeak[a]));
                var sb = new System.Text.StringBuilder();
                for (int j = 0; j < idx.Count && j < 10; j++)
                {
                    int i = idx[j];
                    sb.Append($" [{SafeName(_scrubTs[i])} world={_scrubWorldPeak[i]:0.000} loc={_scrubNodePeak[i]:0.00} d{Depth(_scrubTs[i], _scrubRoot)}]");
                }
                Log.LogInfo($"[manip] top world-movers:{sb}");
            }
            catch { }
        }

        // Disable an animator for the scrub (dedup by instance), recording its prior enabled state to restore later.
        private void DisableScrubAnimator(Animator a)
        {
            if (a == null || _scrubAnimators == null) return;
            for (int i = 0; i < _scrubAnimators.Count; i++)
                if (_scrubAnimators[i].A != null && _scrubAnimators[i].A.GetInstanceID() == a.GetInstanceID()) return;
            bool we = false; try { we = a.enabled; a.enabled = false; } catch { }
            _scrubAnimators.Add(new AnimEntry { A = a, WasEnabled = we });
        }

        // Re-enable the constraints + animators we disabled for a scrub (idempotent; safe at activation and on release).
        private void RestoreScrubDrivers()
        {
            if (_activeMovers != null)
                for (int k = 0; k < _activeMovers.Count; k++)
                {
                    var m = _activeMovers[k];
                    try { if (m.Con != null) m.Con.enabled = m.ConWasEnabled; } catch { }
                }
            if (_scrubAnimators != null)
                for (int i = 0; i < _scrubAnimators.Count; i++)
                {
                    var e = _scrubAnimators[i];
                    try { if (e.A != null) e.A.enabled = e.WasEnabled; } catch { }
                }
            // Re-enable the held stick's own constraint (if we disabled one to swing it).
            try { if (_leverCon != null) { _leverCon.constraintActive = _leverConWasActive; } } catch { }
            // Re-enable the rider's constraint (charge rammer orb-light) so it returns to the game's control.
            try { if (_riderCon != null) { _riderCon.constraintActive = _riderConWasActive; } } catch { }
            // Re-enable every extra handle-part rider's constraint too (the list is cleared at the next grab, mirroring _riderT).
            if (_riders != null)
                for (int i = 0; i < _riders.Count; i++)
                    try { if (_riders[i].Con != null) _riders[i].Con.constraintActive = _riders[i].ConWasActive; } catch { }
            // Put back the renderer-enable state we forced for a GPU-instanced handle (punchcard 'BigLever').
            if (_forcedRends != null)
                for (int i = 0; i < _forcedRends.Count; i++)
                    try { if (_forcedRends[i].R != null) _forcedRends[i].R.enabled = _forcedRends[i].En; } catch { }
            // Switch the handle subtree/ancestors we force-activated back OFF (reverse order: children before parents).
            if (_forcedActive != null)
                for (int i = _forcedActive.Count - 1; i >= 0; i--)
                    try { if (_forcedActive[i].GO != null) _forcedActive[i].GO.SetActive(_forcedActive[i].Was); } catch { }
            // DIAG: undo the held-stick tint.
            try { RestoreTints(); } catch { }
        }

        // Resolve a learned node on a later grab: among descendants named leaf (already enumerated in `all`), pick the
        // one whose current local position is closest to the learned rest — disambiguates duplicate names across
        // instances that can differ in child count.
        private static Transform FindByLeafNear(Transform[] all, string leaf, Vector3 restPos)
        {
            if (all == null || string.IsNullOrEmpty(leaf)) return null;
            Transform best = null; float bd = float.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || SafeName(all[i]) != leaf) continue;
                float d = Vector3.Distance(all[i].localPosition, restPos);
                if (d < bd) { bd = d; best = all[i]; }
            }
            return best;
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

        // Re-aim the dial-drive camera so the native drag reads the RIGID-grab angle. The grabbed point is
        // reconstructed from the controller's rigid motion at the captured grab radius: rotate the grab arm by the
        // controller's current rotation (so a roll about the axis turns the dial 1:1 — TWIST) and add the
        // controller's translation since grab (so moving the hand cranks it tangentially, scaled by the arm length
        // — CRANK). Its angle about the axis is accumulated (deltas, so wrap-safe) and fed to the native drag via a
        // synthetic point; the camera sits off the plane along +axis looking back along -axis at it, so its centre
        // ray ∩ plane = that point.
        private void DriveDialCam(DialInteractable dial, Vector3 ctrlPos, Quaternion ctrlRot)
        {
            if (_dialCam == null || dial == null) return;
            Transform t = dial.transform;
            if (t == null) return;
            Vector3 axis = t.TransformDirection(dial.rotationAxis);
            if (axis.sqrMagnitude < 1e-8f) axis = t.forward;
            axis = axis.normalized;
            Vector3 pivot = t.position;
            PlaneBasis(axis, out Vector3 u, out Vector3 v);

            // Grabbed point relative to the pivot = rotated arm (twist) + controller translation (crank).
            Vector3 grabVec = ctrlRot * _dialArmLocal + (ctrlPos - _dialGrabCtrlPos);
            float ang = AngleOnPlane(grabVec, axis, u, v);
            if (!float.IsNaN(ang))
            {
                if (!_dialDriveInit) { _dialPrevAngleDeg = ang; _dialDriveInit = true; }
                else { _dialSynthAngleDeg += WrapDeg(ang - _dialPrevAngleDeg); _dialPrevAngleDeg = ang; }
            }

            float a = _dialSynthAngleDeg * Mathf.Deg2Rad;
            Vector3 p = pivot + (Mathf.Cos(a) * u + Mathf.Sin(a) * v) * DialPointRadius;
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

        // ---------------- diagnostics ----------------

        // Dump a grabbed switch's hierarchy (3 parents up, full subtree) with each node's IL2CPP component types,
        // plus its Animator/controller/clip info. Once per switch name. Reveals the real moving part + its driver
        // so we can drive the right transform when the animator read finds nothing.
        private void DiagDumpSwitch(LookAtTarget sw)
        {
            try
            {
                string key = sw.name;
                if (!_diagDumped.Add(key)) return;
                Animator anim = null; try { anim = sw.animator; } catch { }
                var sb = new System.Text.StringBuilder();
                sb.Append($"[manip] === switch '{key}': LAT on '{SafeName(sw.transform)}', sw.animator={(anim != null)}");
                if (anim != null)
                {
                    var rac = anim.runtimeAnimatorController;
                    var clips = rac != null ? rac.animationClips : null;
                    sb.Append($", controller={(rac != null ? SafeName(rac) : "null")}, clips={(clips != null ? clips.Length : 0)}");
                    if (clips != null) for (int i = 0; i < clips.Length; i++) { var cl = clips[i]; if (cl != null) sb.Append($" [{SafeName(cl)} len={cl.length:0.00}]"); }
                }
                sb.Append(" ===\n");
                Transform root = sw.transform;
                for (int i = 0; i < 3 && root.parent != null; i++) root = root.parent;
                DumpNode(root, sw.transform, 0, sb);
                Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Log.LogWarning("[manip] diag: " + e.Message); }
        }

        private static void DumpNode(Transform t, Transform marker, int depth, System.Text.StringBuilder sb)
        {
            if (t == null || depth > 6) return;
            string indent = new string(' ', depth * 2);
            sb.Append($"{indent}{(t == marker ? "> " : "  ")}{SafeName(t)} [{CompList(t)}]\n");
            for (int i = 0; i < t.childCount; i++) DumpNode(t.GetChild(i), marker, depth + 1, sb);
        }

        // Comma-joined IL2CPP runtime type names of every component on a transform (so game script types show
        // through, not just the managed UnityEngine proxies).
        private static string CompList(Transform t)
        {
            var cs = t.GetComponents<Component>();
            if (cs == null) return "";
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < cs.Length; i++)
            {
                var c = cs[i];
                if (c == null) continue;
                // Real IL2CPP class name (the managed proxy collapses game scripts to MonoBehaviour/Object).
                try
                {
                    var klass = IL2CPP.il2cpp_object_get_class(c.Pointer);
                    names.Add(System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)));
                }
                catch { names.Add("?"); }
            }
            return string.Join(",", names);
        }

        // Dump the components on the moved node and each ancestor up to the animator root — so we can see WHAT drives
        // it (Animator vs a game script that re-poses it each frame, which would overwrite our manual scrub).
        private static void DumpDrivers(Transform node, Transform root)
        {
            try
            {
                var sb = new System.Text.StringBuilder("[manip] drivers '" + SafeName(node) + "': ");
                var cur = node; int guard = 0;
                while (cur != null && guard++ < 12)
                {
                    sb.Append(SafeName(cur) + "[" + CompList(cur) + "] < ");
                    if (cur == root) break;
                    cur = cur.parent;
                }
                Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Log.LogWarning("[manip] drivers dump: " + e.Message); }
        }

        // Span-free name read (UnityEngine.Object.name getter is broken in this IL2CPP build; GetName() isn't).
        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.GetName() : "null"; } catch { return "?"; }
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

        // PROBE: a grip was pressed with no grabbable control on the ray. Log every collider the ray passed through and
        // the nearest LookAtTarget/pickup/dial/slider above it (+ active/enabled state) — so a control that refuses to
        // grab (e.g. the right-cannon equivalents) reveals WHY: no collider there, an inactive node, or no LookAtTarget.
        private static float _lastGripMissT;
        private static void DumpGripMiss(Vector3 ao, Vector3 dir)
        {
            try
            {
                float now = Time.time;
                if (now - _lastGripMissT < 0.4f) return;   // collapse a held grip's frames into one report
                _lastGripMissT = now;
                var hits = Physics.RaycastAll(ao, dir, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Collide);
                int n = hits != null ? hits.Length : 0;
                Log.LogInfo($"[gripmiss] no control on ray ({n} hits) from ({ao.x:0.00},{ao.y:0.00},{ao.z:0.00}) dir ({dir.x:0.00},{dir.y:0.00},{dir.z:0.00}):");
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                        if (hits[j].distance < hits[i].distance) { var tmp = hits[i]; hits[i] = hits[j]; hits[j] = tmp; }
                int shown = 0;
                for (int i = 0; i < n && shown < 12; i++)
                {
                    var col = hits[i].collider; if (col == null) continue;
                    var ht = col.transform; if (ht == null) continue;
                    var lat = FindUp<LookAtTarget>(ht);
                    var pick = FindUp<PickUpZoomTarget>(ht);
                    var dial = FindUp<DialInteractable>(ht);
                    var slide = FindUp<LinearSliderInteractable>(ht);
                    string tag;
                    if (lat != null) { bool a = false, e2 = false; try { a = lat.gameObject.activeInHierarchy; } catch { } try { e2 = lat.enabled; } catch { } tag = $"LAT '{SafeName(lat.transform)}' active={a} en={e2}"; }
                    else if (pick != null) tag = "pickup-manual";
                    else if (dial != null) tag = "dial";
                    else if (slide != null) tag = "slider";
                    else tag = "—";
                    bool trig = false; try { trig = col.isTrigger; } catch { }
                    Log.LogInfo($"[gripmiss]   {hits[i].distance:0.00}m '{SafeName(ht)}'{(trig ? " (trigger)" : "")} -> {tag}");
                    shown++;
                }
                // PROXIMITY PROBE: the grabbable may sit just OFF the thin aim ray (you point/reach slightly off, so the
                // ray hits only the desk/wall behind it). Sweep a FAT sphere ALONG the ray (catches a control near the ray
                // line at any distance) AND a sphere at the controller (close reach). For each control found, flag whether a
                // PickUpZoomTarget sits above it — that's the reason the lever-follow currently skips it.
                var found = new System.Collections.Generic.List<Transform>();
                RaycastHit[] sc = null; try { sc = Physics.SphereCastAll(ao, 0.20f, dir, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Collide); } catch { }
                for (int i = 0; sc != null && i < sc.Length; i++) { var c = sc[i].collider; if (c != null && c.transform != null) found.Add(c.transform); }
                Collider[] near = null; try { near = Physics.OverlapSphere(ao, 0.6f, ~0, QueryTriggerInteraction.Collide); } catch { }
                for (int i = 0; near != null && i < near.Length; i++) { var c = near[i]; if (c != null && c.transform != null) found.Add(c.transform); }
                int pshown = 0;
                var seen = new System.Collections.Generic.HashSet<int>();
                for (int i = 0; i < found.Count && pshown < 14; i++)
                {
                    var ht = found[i]; if (ht == null) continue;
                    var lat = FindUp<LookAtTarget>(ht); var dial = FindUp<DialInteractable>(ht);
                    var slide = FindUp<LinearSliderInteractable>(ht); var pick = FindUp<PickUpZoomTarget>(ht);
                    if (lat == null && dial == null && slide == null && pick == null) continue;
                    Transform key = lat != null ? lat.transform : dial != null ? dial.transform : slide != null ? slide.transform : pick.transform;
                    int id; try { id = key.GetInstanceID(); } catch { id = i; }
                    if (!seen.Add(id)) continue;
                    string what = lat != null ? $"LAT '{SafeName(lat.transform)}'"
                                : dial != null ? $"dial '{SafeName(dial.transform)}'"
                                : slide != null ? $"slider '{SafeName(slide.transform)}'"
                                : $"pickup '{SafeName(pick.transform)}'";
                    string pz = (pick != null && (lat != null || slide != null || dial != null)) ? "  [+PickUpZoomTarget → follow SKIPS it]" : "";
                    float d = 0f; try { d = Vector3.Distance(ht.position, ao); } catch { }
                    Log.LogInfo($"[gripmiss]   NEAR {d:0.00}m '{SafeName(ht)}' -> {what}{pz}");
                    pshown++;
                }
                if (pshown == 0) Log.LogInfo("[gripmiss]   NEAR: no LAT/slider/dial/pickup near the ray or the controller.");
            }
            catch (Exception e) { Log.LogWarning("[gripmiss] " + e.Message); }
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
            ClearSwitchGrab();
            if (_dialCamGo != null) { try { UnityEngine.Object.Destroy(_dialCamGo); } catch { } _dialCamGo = null; _dialCam = null; }
            _kind = Kind.None; _hand = 0; _dial = null; _lever = null; _prevGrabR = _prevGrabL = false;
        }

        // ---------------- switch tuning (VR menu; operates on the last-grabbed switch) ----------------
        // The menu reads these to show the selected switch's current motion and calls the Switch* methods to
        // change it. Edits write straight to the registry (Set), so the next grab picks them up and the values
        // persist on Save. _selSwitchKey survives release, so you can grab a switch, open the menu, and tune it.

        public bool HasSwitchSelection => _selSwitchKey != null;
        // The key is a name@ancestor@… path (unique per physical control); show only the leaf control name in the menu.
        public string SelectedSwitchName
        {
            get
            {
                if (_selSwitchKey == null) return "—";
                int at = _selSwitchKey.IndexOf('@');
                return at > 0 ? _selSwitchKey.Substring(0, at) : _selSwitchKey;
            }
        }
        // "Auto" = the last grab reproduced the game's own animation, so the manual Axis/Range/Direction rows
        // below don't apply (they tune only switches with no usable animation).
        public string SwitchTypeText => !HasSwitchSelection ? "—"
            : _selAuthored ? "Auto" : (SwitchMotions.Get(_selSwitchKey).Translate ? "Slide" : "Rotate");
        public string SwitchAxisText => HasSwitchSelection ? AxisName(SwitchMotions.Get(_selSwitchKey).Axis) : "—";
        public string SwitchDirText => HasSwitchSelection ? (SwitchMotions.Get(_selSwitchKey).Flip ? "-" : "+") : "—";
        public string SwitchRangeText
        {
            get
            {
                if (!HasSwitchSelection) return "—";
                var m = SwitchMotions.Get(_selSwitchKey);
                return m.Translate ? Mathf.RoundToInt(m.Range * 100f) + " cm" : Mathf.RoundToInt(m.Range) + " deg";
            }
        }

        public void SwitchToggleType()
        {
            if (!HasSwitchSelection) return;
            var m = SwitchMotions.Get(_selSwitchKey);
            m.Translate = !m.Translate;
            m.Range = m.Translate ? 0.02f : 25f;   // sensible default magnitude for the new mode
            SwitchMotions.Set(_selSwitchKey, m);
        }

        public void SwitchCycleAxis(int dir)
        {
            if (!HasSwitchSelection) return;
            var m = SwitchMotions.Get(_selSwitchKey);
            m.Axis = NextAxis(m.Axis, dir);
            m.ManualAxis = true;   // dialling the axis pins it: the follow now uses this axis instead of auto-inferring
            SwitchMotions.Set(_selSwitchKey, m);
        }

        public void SwitchAdjustRange(int dir)
        {
            if (!HasSwitchSelection) return;
            var m = SwitchMotions.Get(_selSwitchKey);
            if (m.Translate) m.Range = Mathf.Clamp(m.Range + dir * 0.01f, 0.01f, 0.40f);   // slide up to 40 cm (shell rammer needs the reach)
            else m.Range = Mathf.Clamp(m.Range + dir * 2f, 2f, 90f);
            SwitchMotions.Set(_selSwitchKey, m);
        }

        public void SwitchFlipDir()
        {
            if (!HasSwitchSelection) return;
            var m = SwitchMotions.Get(_selSwitchKey);
            m.Flip = !m.Flip;
            SwitchMotions.Set(_selSwitchKey, m);
        }

        public void SwitchRecapturePush()
        {
            if (!HasSwitchSelection) return;
            var m = SwitchMotions.Get(_selSwitchKey);
            m.HasPush = false; m.PushLocal = Vector3.zero;
            SwitchMotions.Set(_selSwitchKey, m);
        }

        public void SwitchResetSelected()
        {
            if (HasSwitchSelection) SwitchMotions.Remove(_selSwitchKey);
        }

        // Name the dominant principal axis of a (near-principal) local vector.
        private static string AxisName(Vector3 a)
        {
            float ax = Mathf.Abs(a.x), ay = Mathf.Abs(a.y), az = Mathf.Abs(a.z);
            if (ax >= ay && ax >= az) return "X";
            return ay >= az ? "Y" : "Z";
        }

        // Cycle a unit principal axis X→Y→Z→X (dir>=0) or X→Z→Y→X (dir<0).
        private static Vector3 NextAxis(Vector3 a, int dir)
        {
            string cur = AxisName(a);
            if (dir >= 0) return cur == "X" ? Vector3.up : cur == "Y" ? Vector3.forward : Vector3.right;
            return cur == "X" ? Vector3.forward : cur == "Y" ? Vector3.right : Vector3.up;
        }
    }
}
