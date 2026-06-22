using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;
using UnityEngine.Rendering;

namespace IronNestVR
{
    /// <summary>
    /// Grab-to-place for all the movable cockpit props: the HUD clipboard, the gun watch, and the
    /// world "operating manual" clipboards.
    ///
    /// Three follow modes:
    ///  • <b>WaistLocked</b> (HUD clipboard): rests at a waist "holster" anchor — body-yaw-relative to
    ///    the head — when not held; grip-grab it (either hand) to raise it and read, release and it
    ///    returns to the waist. Its readable face orientation is captured once so it faces you; a config
    ///    tilt angles it up toward your downward gaze.
    ///  • <b>WristLocked</b> (gun watch): rides the LEFT controller like a wristwatch at a fixed offset.
    ///    It is NOT grabbable — you turn your wrist to read it.
    ///  • <b>WorldLocked</b> (operating manuals): they stay where you drop them in the world and do NOT
    ///    follow you; only moved while grabbed.
    ///
    /// We drive each prop's transform by world pose (never re-parent — that breaks the clipboard's
    /// note-consolidation). The HUD clipboard is the one whose holder lives under Main Camera; world
    /// manuals are <c>PickUpZoomTarget</c>s. The watch is the Main-Camera child whose name contains
    /// "Watch". Waist/wrist offsets are tunable live on the menu's HUD tab (persisted to the cfg).
    /// </summary>
    internal sealed class GrabManager
    {
        private static ManualLogSource Log => Plugin.Logger;

        private enum Mode { WaistLocked, WristLocked, WorldLocked }

        private sealed class Item
        {
            public string Name;
            public Transform Move;     // transform whose world pose we set
            public Transform Prox;     // proximity target for grabbing (fallback)
            public Renderer Rend;      // visible mesh — grab by its bounds centre (parent pivot can be offset)
            public Mode Mode;
            public bool IsHudClip;     // the one we scale for VR
            public PickUpZoomTarget Zoom; // non-null for world "operating manual" props (the game's pick-up-to-read)
            // WaistLocked only: the prop's authored orientation in its anchor camera's local frame, captured
            // once (before we take over its transform) so the readable face keeps pointing at you. The waist
            // placement then = body-yaw * configTilt * ReadableRot, positioned at the configured waist offset.
            public Quaternion ReadableRot;
            public bool HasReadable;
            // WorldLocked only: the world pose captured when first tracked, used to put the prop back on release
            // IF it has no PickUpZoomTarget (manuals restore via the game's own original-state record instead).
            public Vector3 HomePos; public Quaternion HomeRot; public bool HasHome;
            // WorldLocked only: stable hierarchy-path key (captured at discovery) for the PER-MANUAL held pose
            // in Config.ManualHolds — manuals differ in size/orientation so each calibrates independently.
            public string Key;
            // HUD clipboard only: the game's ClipboardStateController drives an Animator that re-poses the
            // clip transform (which is Prox, the CHILD of Move) to raise/focus the board for its menu. We pin
            // that child to its idle local pose every frame so the game can't physically move the board — our
            // grab + waist placement is the ONLY movement. The Animator stays enabled, so the menu's content /
            // fade still animates; only the transform motion is cancelled. Captured at discovery (idle state).
            public bool PinChild;
            public Vector3 ChildRestPos; public Quaternion ChildRestRot;
            // Last world pose we drove this prop to (cached so the beginCameraRendering callback can re-assert
            // it right before the eye cameras render — after the game's Cinemachine brain has moved Main Camera,
            // which would otherwise drag the Main-Camera-child HUD props ahead of the world-fixed hand).
            public Vector3 LastPos; public Quaternion LastRot; public bool LastValid;
        }

        private readonly Dictionary<int, Item> _items = new Dictionary<int, Item>();
        private readonly HashSet<int> _seenScratch = new HashSet<int>();
        private readonly List<int> _deadScratch = new List<int>();
        private float _nextScan;

        private int _hand;             // 0 = none, 1 = left, 2 = right
        private Item _grabbed;
        private Vector3 _gPos;         // grabbed prop pose relative to the holding controller
        private Quaternion _gRot;

        private Item _hud;             // the HUD clipboard item (for scaling)
        private float _appliedScale = 1f;
        private Item _watch;           // the gun-watch item (for scaling)
        private float _appliedWatchScale = 1f;
        private bool _fadersOff;
        private int _lastManualCount = -1;

        // Calibration (menu-armed, like the hand-model Calibrate tool). All placement is done by GRIP-MOVING
        // the prop — no sliders.
        //  • Clip (waist rest): grip-grab the clipboard (either hand), place it at your waist, release →
        //    offset/tilt in the body-yaw frame.
        //  • Watch (wrist): the watch becomes grabbable; reach your RIGHT hand to your left wrist, grip &
        //    place, release → offset/orientation in the LEFT-grip frame.
        //  • ClipHold / ManualHold (place in the hand): hold the prop with one hand, then grip it with your
        //    OTHER hand and move/rotate to adjust how it sits in the holding hand; release to set → offset/
        //    orient in the holding controller's grip frame. (Two-hand, mirrors the hand-model Calibrate
        //    gesture.) ClipHold edits the HUD clipboard's held pose; ManualHold edits the world manuals'.
        // Stays armed until toggled off. Runs even with the menu open (VrManager calls Tick while armed).
        private enum CalibTarget { None, Clip, Watch, ClipHold, ManualHold }
        private CalibTarget _calib;
        public bool IsCalibrating => _calib != CalibTarget.None;
        public bool CalibratingClip => _calib == CalibTarget.Clip;
        public bool CalibratingWatch => _calib == CalibTarget.Watch;
        public bool CalibratingClipHold => _calib == CalibTarget.ClipHold;
        public bool CalibratingManualHold => _calib == CalibTarget.ManualHold;

        // ClipHold gesture state: the opposite hand's grip drags the board relative to the holding hand.
        private bool _holdAdjust, _prevHoldGrip;
        private Vector3 _holdOppPos, _holdClipPos;
        private Quaternion _holdOppRot, _holdClipRot;

        // Which hand is currently holding a clipboard-posed prop (the HUD clipboard OR a world manual)
        // (0 = none, 1 = left, 2 = right). The hand-model renderer reads this to pose that hand as holding a
        // flat object. See HandVisuals.SetClipboardHold. (The watch during calibration is excluded.)
        public int ClipboardHoldHand =>
            (_grabbed != null && (_grabbed.Mode == Mode.WaistLocked || _grabbed.Mode == Mode.WorldLocked)) ? _hand : 0;

        // Which hand holds the HUD clipboard SPECIFICALLY (0 = none, 1 = left, 2 = right) — excludes the world
        // "operating manual" props (WorldLocked). Gates the on-demand map-tools palette toggle (MapTools): A on
        // the right while the right hand holds it, X on the left while the left hand holds it.
        public int HudClipboardHoldHand =>
            (_grabbed != null && _grabbed.Mode == Mode.WaistLocked) ? _hand : 0;

        // Which hand is currently grip-holding ANY prop (clipboard, world manual, or the watch during
        // calibration) — 0 none, 1 left, 2 right. CockpitInteractor reads this to hide that hand's pointing
        // laser while it's busy holding something.
        public int GrabbingHand => _grabbed != null ? _hand : 0;

        public void ToggleCalibrateClip() => SetCalib(CalibTarget.Clip);
        public void ToggleCalibrateWatch() => SetCalib(CalibTarget.Watch);
        public void ToggleCalibrateClipHold() => SetCalib(CalibTarget.ClipHold);
        public void ToggleCalibrateManualHold() => SetCalib(CalibTarget.ManualHold);

        private void SetCalib(CalibTarget want)
        {
            _calib = (_calib == want) ? CalibTarget.None : want;
            _holdAdjust = false;
            _prevHoldGrip = true; // swallow an opposite-grip that's already held as we enter
            if (_calib == CalibTarget.None) { try { Config.Save(); } catch { } } // persist on finish
            Log.LogInfo("[grab] calibrate " + (_calib == CalibTarget.None ? "off."
                : _calib == CalibTarget.Watch ? "WATCH — reach your RIGHT hand to your left wrist, grip & place."
                : _calib == CalibTarget.ClipHold ? "CLIP HOLD — hold the clipboard, then grip it with your OTHER hand and move it."
                : _calib == CalibTarget.ManualHold ? "MANUAL HOLD — grab a manual, then grip it with your OTHER hand and move it."
                : "CLIPBOARD — grip it, place it at your waist, release."));
        }

        // ---------------- per-frame ----------------

        public void Tick(VrInput input, CameraRig rig, bool active)
        {
            try
            {
                if (!Config.ClipboardGrabEnabled || !active) { _hand = 0; return; }
                HookRenderCallback();   // idempotent; ensures the end-of-frame HUD re-assert is live
                Scan();
                ReparentHudOnce();      // lift the clipboard holder out of Main Camera so the notes can't drift
                var origin = rig.OriginTransform;
                if (origin == null) return;

                // Grab / drag / release. BOTH hands: RAY-grab first (so a laser on a world manual grabs THAT,
                // even out of reach), then fall back to a proximity grab (the waist clipboard / a floated manual
                // within reach). Each hand uses its own aim ray + grip.
                if (_grabbed == null)
                {
                    if (input.GrabR && input.GripValid)
                    {
                        bool got = input.AimValid && TryRayGrab(2, origin, input.AimPose, input.GripPose, input);
                        if (!got) TryGrab(2, origin, input.GripPose, input);
                    }
                    else if (input.GrabL && input.GripValidL)
                    {
                        bool got = input.AimValidL && TryRayGrab(1, origin, input.AimPoseL, input.GripPoseL, input);
                        if (!got) TryGrab(1, origin, input.GripPoseL, input);
                    }
                }
                else
                {
                    bool held = _hand == 2 ? (input.GrabR && input.GripValid) : (input.GrabL && input.GripValidL);
                    if (!held || !Grabbable(_grabbed)) Release(rig, input); // drop if grip released, destroyed, or a manual started zooming
                    else
                    {
                        Posef p = _hand == 2 ? input.GripPose : input.GripPoseL;
                        GetWorld(origin, p, out var cp, out var cr);
                        if (_grabbed.Mode == Mode.WaistLocked || _grabbed.Mode == Mode.WorldLocked)
                        {
                            bool manual = _grabbed.Mode == Mode.WorldLocked;
                            // Two-hand hold calibration: the OTHER hand drags the held offset before we apply it
                            // (ClipHold edits the clipboard's, ManualHold edits the held manual's own entry).
                            if ((_calib == CalibTarget.ClipHold && !manual) || (_calib == CalibTarget.ManualHold && manual))
                                HeldPoseAdjust(input, origin, cp, cr);
                            // Snap into the fixed held pose in the grip frame (per holding hand, since left/right
                            // grips mirror) — clipboard uses ClipHeld*, each manual uses its own per-manual entry.
                            GetHeld(_grabbed, _hand == 2, out var off, out var eul);
                            Place(_grabbed, cp + cr * off, cr * Quaternion.Euler(eul));
                            PinChild(_grabbed); // keep the game's menu Animator from shoving the board out of the grip
                        }
                        else
                            // Watch during calibration: keep the relative pose captured at grab time.
                            Place(_grabbed, cp + cr * _gPos, cr * _gRot);
                    }
                }

                DriveFollowers(rig, input);
            }
            catch (Exception e)
            {
                Log.LogWarning("[grab] " + e.Message);
                _items.Clear(); _hud = null; _grabbed = null; _hand = 0;
            }
        }

        // Re-assert the HUD clipboard + watch poses from LateUpdate. CRITICAL: the eye cameras auto-render at
        // END of frame (Config.UseEnabledCameras), which is AFTER the game's menu Animator runs (it re-poses
        // the clip child → "pulled out of hand for the menu") and AFTER Main Camera moves (the clipboard/watch
        // holders are children of Main Camera, so locomotion drags them → "disconnects from hand when moving").
        // Our Update-phase placement is therefore stale by render time. Re-applying here — after the animation
        // phase and the game's camera motion — makes the eye render see them where WE put them. The origin is
        // unchanged since Update, so the grip-derived board pose lands exactly on the (Update-placed) hand. World
        // manuals don't need this: their parent isn't Main Camera and they carry no Animator. Mirrors
        // CoopControls/CoopMap.LateApply (the established "win the frame in LateUpdate" pattern).
        public void LateApply(CameraRig rig, VrInput input)
        {
            if (!Config.ClipboardGrabEnabled || rig == null) return;
            try
            {
                var origin = rig.OriginTransform;
                if (origin == null) return;
                // Grabbed HUD clipboard → re-stick it into the holding hand (same grip-frame held pose as Tick).
                if (_grabbed != null && _grabbed == _hud && _grabbed.Move != null)
                {
                    Posef p = _hand == 2 ? input.GripPose : input.GripPoseL;
                    GetWorld(origin, p, out var cp, out var cr);
                    GetHeld(_grabbed, _hand == 2, out var off, out var eul);
                    Place(_grabbed, cp + cr * off, cr * Quaternion.Euler(eul));
                    PinChild(_grabbed);
                }
                // Waist clipboard (when not grabbed) + wrist watch.
                DriveFollowers(rig, input);
            }
            catch (Exception e) { Log.LogWarning("[grab] lateapply: " + e.Message); }
        }

        // Re-anchor the always-on HUD props each frame (except the one being grabbed):
        //  • WaistLocked clipboard → a body-yaw "holster" anchor below/in front of the head, tilted up.
        //  • WristLocked watch     → the LEFT controller, at a fixed wristband offset.
        private void DriveFollowers(CameraRig rig, VrInput input)
        {
            if (!rig.TryGetHeadPose(out var hp, out var hr)) return;
            var origin = rig.OriginTransform;
            if (origin == null) return;
            var cam = Camera.main;
            Quaternion body = BodyYaw(hr);

            // Left controller world pose (for the wrist watch), if it's tracked this frame.
            bool hasLeft = input.GripValidL;
            Vector3 lwp = Vector3.zero; Quaternion lwr = Quaternion.identity;
            if (hasLeft) GetWorld(origin, input.GripPoseL, out lwp, out lwr);

            foreach (var it in _items.Values)
            {
                if (it == _grabbed || it.Move == null) continue;
                if (it.Mode == Mode.WaistLocked)
                {
                    if (!it.HasReadable) CaptureReadable(it, cam); // lock in the readable facing before we take over
                    Vector3 pos = hp + body * Config.ClipWaistOffset;
                    Quaternion rot = body * Quaternion.Euler(Config.ClipWaistEuler) * it.ReadableRot;
                    Place(it, pos, rot);
                    PinChild(it); // cancel the game's raise/focus Animator so the board stays at the waist
                }
                else if (it.Mode == Mode.WristLocked)
                {
                    if (!hasLeft) continue; // left controller dropped tracking: leave the watch put
                    Vector3 pos = lwp + lwr * Config.WatchWristOffset;
                    Quaternion rot = lwr * Quaternion.Euler(Config.WatchWristEuler);
                    Place(it, pos, rot);
                }
            }
        }

        // Yaw-only "body" frame from the head rotation (head forward flattened to horizontal). Used to
        // anchor the waist holster so the clipboard follows where the body faces but not head pitch/roll.
        private static Quaternion BodyYaw(Quaternion hr)
        {
            Vector3 fwd = hr * Vector3.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) { fwd = hr * Vector3.up; fwd.y = 0f; } // looking straight up/down
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            return Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }

        // Capture the prop's authored orientation relative to its (Main) camera, ONCE, before we start
        // driving its transform — afterwards camera-relative is meaningless (we own its world rotation).
        // This is the "readable face toward the viewer" base the waist tilt is applied on top of.
        private static void CaptureReadable(Item it, Camera cam)
        {
            Quaternion camRot = cam != null ? cam.transform.rotation : Quaternion.identity;
            it.ReadableRot = Quaternion.Inverse(camRot) * it.Move.rotation;
            it.HasReadable = true;
        }

        // Re-pin the HUD clipboard's child (the ClipboardStateController transform = Prox, under the Move
        // parent we drive) to its idle local pose. The game's menu Animator otherwise re-poses this child to
        // raise/focus the board; cancelling it here — in Update, BEFORE the eye render — means the headset only
        // ever sees the board where WE place it (waist or in-hand). No-op for props that don't pin (manuals,
        // or a clip with no parent).
        private static void PinChild(Item it)
        {
            if (it == null || !it.PinChild || it.Prox == null) return;
            it.Prox.localPosition = it.ChildRestPos;
            it.Prox.localRotation = it.ChildRestRot;
        }

        // Drive a prop to a world pose AND cache it, so the render callback can re-assert it after the game's
        // late camera motion. Use this everywhere we place a tracked prop.
        private static void Place(Item it, Vector3 pos, Quaternion rot)
        {
            it.Move.SetPositionAndRotation(pos, rot);
            it.LastPos = pos; it.LastRot = rot; it.LastValid = true;
        }

        // --- end-of-frame re-assert (URP render callback) ---
        // The gun-watch holder is a child of Main Camera (the clipboard holder used to be too, but we now lift it
        // out — see ReparentHudOnce). The game's Cinemachine brain moves Main Camera AFTER our Update/LateUpdate
        // placement, so a Main-Camera-child prop gets dragged "ahead of the hand in the direction of movement."
        // We re-assert its cached world pose in beginCameraRendering — right before each of OUR eye cameras renders,
        // after all the game's camera motion — so the headset only ever sees the watch where WE put it. The watch is
        // mesh-based (drawn with its live transform), so this render-time fix is sufficient for it. World manuals
        // aren't Main-Camera children, so they're skipped (LastValid is only set for the always-on HUD props).
        private bool _cbHooked;
        private bool _cbTried;
        private Il2CppSystem.Action<ScriptableRenderContext, Camera> _beginCamCb;

        // HUD clipboard reparent state. The notes are a WORLD-SPACE Canvas that bakes its geometry in PostLateUpdate,
        // after the game's Cinemachine camera move and after every render hook we can reach — so while the holder is a
        // child of Main Camera the baked notes always render dragged "ahead of the hand" (the board MESH could be
        // fixed at render time, but the canvas could not). Lifting the holder out from under Main Camera (it's a scene
        // object, so to the active-scene root) means locomotion can't drag it; we drive its world pose every frame and
        // it stays put through the bake. Original parent saved for restore on Reset.
        private bool _hudReparented;
        private Transform _hudOrigParent;

        private void HookRenderCallback()
        {
            if (_cbHooked || _cbTried) return;
            _cbTried = true;
            try
            {
                _beginCamCb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    (Action<ScriptableRenderContext, Camera>)OnBeginCameraRendering);
                RenderPipelineManager.add_beginCameraRendering(_beginCamCb);
                _cbHooked = true;
                Log.LogInfo("[grab] hooked beginCameraRendering for end-of-frame HUD re-assert.");
            }
            catch (Exception e) { Log.LogWarning("[grab] render hook failed (HUD may lag in motion): " + e.Message); }
        }

        private void UnhookRenderCallback()
        {
            if (_cbHooked) { try { RenderPipelineManager.remove_beginCameraRendering(_beginCamCb); } catch { } }
            _cbHooked = false; _cbTried = false; _beginCamCb = null;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                if (cam == null) return;
                string n = cam.name;
                if (n == null || !n.StartsWith("IronNestVR_Eye", StringComparison.Ordinal)) return; // only our eye cams
                ReassertHud();
            }
            catch { }
        }

        // Re-apply each always-on HUD prop's cached world pose + re-pin the clipboard child, right before our eye
        // cameras render (after the game's late camera motion). Mainly keeps the Main-Camera-child watch on the wrist;
        // the reparented clipboard is already frame-stable, so re-asserting it here is just belt-and-suspenders.
        private void ReassertHud()
        {
            foreach (var it in _items.Values)
            {
                if (it == null || it.Move == null || !it.LastValid) continue;
                if (it.Mode == Mode.WaistLocked || it.Mode == Mode.WristLocked)
                {
                    it.Move.SetPositionAndRotation(it.LastPos, it.LastRot);
                    PinChild(it);
                }
            }
        }

        // Lift the HUD clipboard holder out from under Main Camera, exactly once, preserving its world transform.
        // See the _hudReparented field note for why. A no-op until the HUD clipboard has been discovered.
        private void ReparentHudOnce()
        {
            if (_hudReparented || _hud == null || _hud.Move == null) return;
            _hudReparented = true; // set regardless so a failure doesn't retry every frame
            try
            {
                _hudOrigParent = _hud.Move.parent;
                _hud.Move.SetParent(null, true); // active-scene root, world pose preserved
                Log.LogInfo("[grab] lifted HUD clipboard holder out of Main Camera (notes anti-drift via reparent).");
            }
            catch (Exception e) { Log.LogWarning("[grab] HUD reparent failed: " + e.Message); }
        }

        private bool TryGrab(int hand, Transform origin, Posef pose, VrInput input)
        {
            GetWorld(origin, pose, out var cp, out var cr);
            Item best = null;
            float bd = Config.GrabRadius;
            foreach (var it in _items.Values)
            {
                if (!Grabbable(it)) continue;
                Vector3 pp = ProxPoint(it);
                float d = Vector3.Distance(cp, pp);
                if (d <= bd) { bd = d; best = it; }
            }
            if (best == null) return false;
            Attach(best, hand, cp, cr, input, "grip");
            return true;
        }

        // Ray/distance grab: grab whatever the laser is actually touching, if it's a world manual — even
        // out of arm's reach. Uses the CLOSEST hit (matches the visible laser endpoint) and includes
        // triggers (the manuals' colliders may be triggers). Manuals only (Zoom != null). The manual then
        // follows the controller at the distance it was grabbed.
        private float _nextRayLog;
        private bool TryRayGrab(int hand, Transform origin, Posef aimPose, Posef gripPose, VrInput input)
        {
            GetWorld(origin, aimPose, out var ao, out var ar);
            Vector3 dir = ar * Vector3.forward;
            // Grab the nearest grabbable manual the ray passes through — not just the single closest collider.
            // A plain Physics.Raycast stops at the first collider on any layer, so a trigger volume (or other
            // non-manual prop) in front of a manual silently blocks the grab even though the laser visually
            // lands on the manual. Scanning all hits sees through those and grabs the closest actual manual.
            var it = RayGrabTarget(ao, dir, out float hitDist);
            if (it == null)
            {
                if (Time.unscaledTime >= _nextRayLog)
                {
                    _nextRayLog = Time.unscaledTime + 0.5f;
                    Log.LogInfo("[grab] ray found no grabbable manual under the laser.");
                }
                return false;
            }
            GetWorld(origin, gripPose, out var cp, out var cr);
            Attach(it, hand, cp, cr, input, "ray@" + hitDist.ToString("0.0") + "m");
            return true;
        }

        // Map a hit collider back to a tracked item by walking up to a transform we know.
        private Item FindItemForCollider(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
                if (_items.TryGetValue(p.GetInstanceID(), out var it)) return it;
            return null;
        }

        // Nearest tracked, grabbable manual under the ray. Like the trigger-click and the laser visual, it
        // sees through intervening trigger volumes / non-manual colliders that a single Physics.Raycast would
        // stop at. Triggers are included in the query because a manual's own collider may be a trigger.
        private Item RayGrabTarget(Vector3 ao, Vector3 dir, out float dist)
        {
            dist = 0f;
            var hits = Physics.RaycastAll(ao, dir, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Collide);
            if (hits == null) return null;
            Item best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                var col = h.collider;
                if (col == null) continue;
                var item = FindItemForCollider(col.transform);
                if (item == null || item.Zoom == null || !Grabbable(item)) continue;
                if (h.distance < bestDist) { bestDist = h.distance; best = item; dist = h.distance; }
            }
            return best;
        }

        private void Attach(Item it, int hand, Vector3 cp, Quaternion cr, VrInput input, string how)
        {
            _grabbed = it;
            _hand = hand;
            var inv = Quaternion.Inverse(cr);
            _gPos = inv * (it.Move.position - cp);
            _gRot = inv * it.Move.rotation;
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[grab] {how}-grabbed '{it.Name}' ({(hand == 2 ? "right" : "left")}, {it.Mode}).");
        }

        // A prop is grabbable unless it's the wrist watch (fixed to the wrist by design — except while you're
        // calibrating it) or a world manual that's inactive or mid pick-up/put-back ANIMATION (the game's
        // coroutine owns the transform then — grabbing would fight the lerp). We DO allow grabbing a manual
        // while it's floated-up-to-read (isHeld && settled): PickUpZoomTarget has no Update, so once the move
        // coroutine finishes nothing re-poses it — this is the state the user grabs it in.
        private bool Grabbable(Item it)
        {
            if (it == null || it.Move == null) return false;
            if (it.Mode == Mode.WristLocked && _calib != CalibTarget.Watch) return false; // watch only grabbable while calibrating it
            if (it.Zoom != null)
            {
                if (!it.Move.gameObject.activeInHierarchy) return false;
                try { if (it.Zoom.IsMoving) return false; } catch { }
            }
            return true;
        }

        // Where the prop visibly is, for grab proximity: the renderer bounds centre when available
        // (the watch's pivot is offset from its visible face), else the proximity transform.
        private static Vector3 ProxPoint(Item it)
        {
            if (it.Rend != null) return it.Rend.bounds.center;
            if (it.Prox != null) return it.Prox.position;
            return it.Move != null ? it.Move.position : Vector3.zero;
        }

        // Drop whatever's held. If we're calibrating the released prop, the spot it was placed becomes its new
        // resting offset (saved to disk). Otherwise the waist clipboard returns to its holster next frame
        // (DriveFollowers) and a world manual snaps back to where it was originally placed in the world.
        private void Release(CameraRig rig, VrInput input)
        {
            if (_grabbed != null)
            {
                if (_calib == CalibTarget.Clip && _grabbed == _hud) CaptureClipCalibration(rig);
                else if (_calib == CalibTarget.Watch && _grabbed == _watch) CaptureWatchCalibration(rig, input);
                else if (_grabbed.Mode == Mode.WorldLocked) ReturnManualHome(_grabbed);
                Log.LogInfo($"[grab] released '{_grabbed.Name}'.");
            }
            _grabbed = null;
            _hand = 0;
        }

        // Put a world manual back where it was originally placed. Manuals use the game's own original-state
        // record (ResetToOriginalImmediate / Release) so they return under their original parent — correct
        // even for props mounted on the rotating turret. Non-zoom world props fall back to our captured Home.
        private static void ReturnManualHome(Item it)
        {
            try
            {
                if (it.Zoom != null)
                {
                    if (it.Zoom.isHeld) it.Zoom.Release();        // was floated-up to read: proper close + state reset
                    else it.Zoom.ResetToOriginalImmediate();      // wasn't zoomed: snap straight back to its spot
                }
                else if (it.HasHome && it.Move != null)
                {
                    it.Move.SetPositionAndRotation(it.HomePos, it.HomeRot);
                }
            }
            catch (Exception e) { Log.LogWarning("[grab] return manual home: " + e.Message); }
        }

        // Recompute the waist offset + tilt from where the clipboard was just placed, expressed in the
        // body-yaw anchor frame so it rests there relative to your body. The tilt is stored on top of the
        // captured readable facing (rot = bodyYaw * Euler(tilt) * ReadableRot), inverting that relation.
        private void CaptureClipCalibration(CameraRig rig)
        {
            if (_hud == null || _hud.Move == null || !rig.TryGetHeadPose(out var hp, out var hr)) return;
            if (!_hud.HasReadable) CaptureReadable(_hud, Camera.main);
            Quaternion invBody = Quaternion.Inverse(BodyYaw(hr));
            Config.ClipWaistOffset = invBody * (_hud.Move.position - hp);
            Config.ClipWaistEuler = (invBody * _hud.Move.rotation * Quaternion.Inverse(_hud.ReadableRot)).eulerAngles;
            try { Config.Save(); } catch { }
            Log.LogInfo($"[grab] clipboard calibrated: offset={Config.ClipWaistOffset}, tilt={Config.ClipWaistEuler}.");
        }

        // Hold calibration (clipboard OR manual): while the prop is held by one hand (hPos/hRot = that grip's
        // world pose), the OPPOSITE hand's grip rigidly drags the prop; we re-express its pose in the holding-
        // grip frame and store it as the holding hand's held offset/euler (so it sticks once released). For a
        // manual that writes its OWN per-manual entry. Mirrors HandVisuals.CalibrateTick.
        private void HeldPoseAdjust(VrInput input, Transform origin, Vector3 hPos, Quaternion hRot)
        {
            if (_grabbed == null) return;
            bool holdRight = _hand == 2;  // which hand HOLDS the prop → which per-hand offset we edit
            bool oppIsRight = !holdRight; // the opposite (adjusting) hand
            bool oppValid = oppIsRight ? input.GripValid : input.GripValidL;
            bool oppGrip = oppIsRight ? input.GrabR : input.GrabL;
            if (!oppValid) { _prevHoldGrip = oppGrip; return; }
            GetWorld(origin, oppIsRight ? input.GripPose : input.GripPoseL, out var oPos, out var oRot);

            GetHeld(_grabbed, holdRight, out Vector3 curOff, out Vector3 curEul);
            Vector3 clipPos = hPos + hRot * curOff;
            Quaternion clipRot = hRot * Quaternion.Euler(curEul);

            if (oppGrip && !_prevHoldGrip)
            {
                _holdOppPos = oPos; _holdOppRot = oRot;
                _holdClipPos = clipPos; _holdClipRot = clipRot;
                _holdAdjust = true;
                input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            }
            else if (!oppGrip && _prevHoldGrip && _holdAdjust)
            {
                _holdAdjust = false;
                try { Config.Save(); } catch { }
                input.Haptic(Config.DetentHapticAmplitude, 0.03f);
                bool manual = _grabbed.Mode == Mode.WorldLocked;
                GetHeld(_grabbed, holdRight, out Vector3 o, out Vector3 e);
                Log.LogInfo($"[grab] {(manual ? "manual" : "clip")}-hold calibrated ({(holdRight ? "right" : "left")} hand, key='{(manual ? _grabbed.Key : "clip")}'): offset={o}, euler={e}.");
            }

            if (_holdAdjust)
            {
                Quaternion dq = oRot * Quaternion.Inverse(_holdOppRot);
                Vector3 newPos = oPos + dq * (_holdClipPos - _holdOppPos);
                Quaternion newRot = dq * _holdClipRot;
                Quaternion invH = Quaternion.Inverse(hRot);
                SetHeld(_grabbed, holdRight, invH * (newPos - hPos), (invH * newRot).eulerAngles);
            }
            _prevHoldGrip = oppGrip;
        }

        // The held pose for a grabbed prop, per holding hand. The HUD clipboard uses the shared ClipHeld*; a
        // world manual uses its OWN entry in Config.ManualHolds (keyed by Item.Key), falling back to the shared
        // ManualHeld* default until that specific manual has been calibrated.
        private static void GetHeld(Item it, bool right, out Vector3 off, out Vector3 eul)
        {
            if (it.Mode == Mode.WorldLocked)
            {
                if (it.Key != null && Config.ManualHolds.TryGetValue(it.Key, out var h))
                { off = right ? h.OffR : h.OffL; eul = right ? h.EulR : h.EulL; return; }
                off = right ? Config.ManualHeldOffsetR : Config.ManualHeldOffsetL;
                eul = right ? Config.ManualHeldEulerR : Config.ManualHeldEulerL;
            }
            else { off = right ? Config.ClipHeldOffsetR : Config.ClipHeldOffsetL; eul = right ? Config.ClipHeldEulerR : Config.ClipHeldEulerL; }
        }

        private static void SetHeld(Item it, bool right, Vector3 off, Vector3 eul)
        {
            if (it.Mode == Mode.WorldLocked)
            {
                if (it.Key == null) return;
                // Seed a fresh entry from the shared default so calibrating ONE hand doesn't zero the other.
                if (!Config.ManualHolds.TryGetValue(it.Key, out var h))
                {
                    h.OffR = Config.ManualHeldOffsetR; h.EulR = Config.ManualHeldEulerR;
                    h.OffL = Config.ManualHeldOffsetL; h.EulL = Config.ManualHeldEulerL;
                }
                if (right) { h.OffR = off; h.EulR = eul; } else { h.OffL = off; h.EulL = eul; }
                Config.ManualHolds[it.Key] = h;
            }
            else { if (right) { Config.ClipHeldOffsetR = off; Config.ClipHeldEulerR = eul; } else { Config.ClipHeldOffsetL = off; Config.ClipHeldEulerL = eul; } }
        }

        // Stable hierarchy-path key for a manual (name#siblingIndex per segment, deterministic across sessions),
        // captured at discovery before the game's zoom can reparent it. Matches CoopControls.PathOf's scheme.
        private static string ManualKey(Transform t)
        {
            var sb = new System.Text.StringBuilder();
            for (Transform p = t; p != null; p = p.parent)
            {
                string n; try { n = p.name; } catch { n = "?"; }
                int idx; try { idx = p.GetSiblingIndex(); } catch { idx = 0; }
                if (sb.Length > 0) sb.Insert(0, "/");
                sb.Insert(0, (string.IsNullOrEmpty(n) ? "?" : n) + "#" + idx);
            }
            return sb.ToString().Replace('=', '_').Replace('|', '_').Replace('\n', '_').Replace('\r', '_');
        }

        // Recompute the watch offset + orientation from where it was placed, expressed in the LEFT grip
        // (wrist) frame, so it rides the wrist there. Needs the left controller tracked at release.
        private void CaptureWatchCalibration(CameraRig rig, VrInput input)
        {
            if (_watch == null || _watch.Move == null) return;
            var origin = rig.OriginTransform;
            if (origin == null) return;
            if (!input.GripValidL) { Log.LogWarning("[grab] watch calibrate skipped: left controller not tracked at release."); return; }
            GetWorld(origin, input.GripPoseL, out var lwp, out var lwr);
            Quaternion invL = Quaternion.Inverse(lwr);
            Config.WatchWristOffset = invL * (_watch.Move.position - lwp);
            Config.WatchWristEuler = (invL * _watch.Move.rotation).eulerAngles;
            try { Config.Save(); } catch { }
            Log.LogInfo($"[grab] watch calibrated: offset={Config.WatchWristOffset}, euler={Config.WatchWristEuler}.");
        }

        // ---------------- discovery ----------------

        private void Scan()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            var seen = _seenScratch; seen.Clear();
            var cam = Camera.main;
            Transform camT = cam != null ? cam.transform : null;

            // Clipboards: HUD (holder under Main Camera → waist holster) vs world manuals (everything else).
            var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ClipboardStateController>(), FindObjectsSortMode.None);
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i].TryCast<ClipboardStateController>();
                    if (c == null) continue;
                    Transform clip = c.transform;
                    if (clip == null) continue;
                    // Already tracking this as the HUD clipboard? Once we've lifted its holder out of Main Camera,
                    // IsUnder() below would misclassify it as a world manual and re-register it — keep the existing
                    // item and just mark it seen.
                    if (_hud != null && _hud.Prox == clip && _hud.Move != null) { seen.Add(_hud.Move.GetInstanceID()); continue; }
                    bool hud = IsUnder(clip, camT);
                    Transform move = hud ? (clip.parent != null ? clip.parent : clip) : clip;
                    int id = move.GetInstanceID();
                    seen.Add(id);
                    if (!_items.ContainsKey(id))
                    {
                        var it = new Item
                        {
                            Name = hud ? "HUD clipboard" : ("tutorial clipboard '" + clip.name + "'"),
                            Move = move, Prox = clip,
                            Rend = move.GetComponentInChildren<Renderer>(true),
                            Mode = hud ? Mode.WaistLocked : Mode.WorldLocked,
                            IsHudClip = hud,
                            HomePos = move.position, HomeRot = move.rotation, HasHome = !hud,  // world props return here on release
                            Key = hud ? null : ManualKey(move),
                            // HUD clip: pin its child (clip) to its idle local pose so the game's menu Animator
                            // can't move the board. Only when clip actually has a parent we drive (move != clip).
                            PinChild = hud && move != clip,
                            ChildRestPos = clip.localPosition, ChildRestRot = clip.localRotation
                        };
                        _items[id] = it;
                        if (hud)
                        {
                            _hud = it;
                            _appliedScale = 1f;
                            if (!_fadersOff) { DisablePositionFaders(); _fadersOff = true; }
                            ReconcileScale();
                        }
                        Log.LogInfo($"[grab] tracking {it.Name} ({it.Mode}).");
                    }
                }
            }

            // Watch: the Main-Camera child whose name contains "Watch" → rides the LEFT wrist (not grabbable).
            if (camT != null)
            {
                var watch = FindByNameContains(camT, "watch");
                if (watch != null)
                {
                    int id = watch.GetInstanceID();
                    seen.Add(id);
                    if (!_items.ContainsKey(id))
                    {
                        var it = new Item { Name = "watch '" + watch.name + "'", Move = watch, Prox = watch, Rend = watch.GetComponentInChildren<Renderer>(true), Mode = Mode.WristLocked };
                        _items[id] = it;
                        _watch = it;
                        _appliedWatchScale = 1f;
                        DisableWatchAnimators(watch); // stop the wrist-raise Animator re-posing it each frame
                        ReconcileScale();
                        Log.LogInfo($"[grab] tracking watch '{watch.name}' (WristLocked).");
                    }
                }
            }

            // Operating-manual / instruction clipboards scattered in the world (PickUpZoomTarget props):
            // grip-grab brings one into your hand in the SAME held clipboard pose; releasing snaps it back to
            // where it was originally placed (via the game's own original-state record). Trigger still does the
            // game's click-to-zoom-and-read. Tracked even while inactive (no add/remove churn); only grabbable
            // while resting/floated + visible.
            if (Config.ManualGrabEnabled)
            {
                var marr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PickUpZoomTarget>(), FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (marr != null)
                {
                    for (int i = 0; i < marr.Length; i++)
                    {
                        var pz = marr[i].TryCast<PickUpZoomTarget>();
                        if (pz == null) continue;
                        Transform t = pz.transform;
                        if (t == null) continue;
                        int id = t.GetInstanceID();
                        seen.Add(id);
                        if (!_items.ContainsKey(id))
                        {
                            _items[id] = new Item
                            {
                                Name = "manual '" + t.name + "'",
                                Move = t, Prox = t,
                                Rend = t.GetComponentInChildren<Renderer>(true),
                                Mode = Mode.WorldLocked,
                                Zoom = pz,
                                HomePos = t.position, HomeRot = t.rotation, HasHome = true,  // fallback if the game's reset fails
                                Key = ManualKey(t)   // per-manual held-pose key
                            };
                        }
                    }
                }
            }

            // Drop anything that vanished (scene change / destroyed).
            if (_items.Count > 0)
            {
                var dead = _deadScratch; dead.Clear();
                foreach (var kv in _items) if (!seen.Contains(kv.Key) || kv.Value.Move == null) dead.Add(kv.Key);
                foreach (var id in dead)
                {
                    if (_grabbed != null && _items[id] == _grabbed) { _grabbed = null; _hand = 0; }
                    if (_hud != null && _items[id] == _hud) { _hud = null; _hudReparented = false; _hudOrigParent = null; }
                    if (_watch != null && _items[id] == _watch) { _watch = null; }
                    _items.Remove(id);
                }
            }

            // One-shot confirmation that the world manuals registered (debug for ray-grab).
            int manuals = 0;
            foreach (var kv in _items) if (kv.Value.Zoom != null) manuals++;
            if (manuals != _lastManualCount) { _lastManualCount = manuals; Log.LogInfo($"[grab] tracking {manuals} world manual(s) for grab."); }
        }

        private static bool IsUnder(Transform t, Transform ancestor)
        {
            if (ancestor == null) return false;
            int aid = ancestor.GetInstanceID();
            for (Transform p = t; p != null; p = p.parent)
                if (p.GetInstanceID() == aid) return true;
            return false;
        }

        private static Transform FindByNameContains(Transform root, string sub)
        {
            int n = root.childCount;
            for (int i = 0; i < n; i++)
            {
                var c = root.GetChild(i);
                if (c.name.ToLowerInvariant().Contains(sub)) return c;
                var deep = FindByNameContains(c, sub);
                if (deep != null) return deep;
            }
            return null;
        }

        // ---------------- clipboard VR scale ----------------

        public void ReconcileScale()
        {
            ApplyScale(_hud, Config.ClipboardScale, ref _appliedScale, "clipboard");
            ApplyScale(_watch, Config.WatchScale, ref _appliedWatchScale, "watch");
        }

        // Scale a prop by multiplying only the delta since last applied, so a live settings slider
        // resizes it without compounding. Drive-followers set position/rotation (not scale), so scale sticks.
        private static void ApplyScale(Item it, float cfg, ref float applied, string label)
        {
            if (it == null || it.Move == null) return;
            float target = cfg > 0f ? cfg : 1f;
            if (Mathf.Abs(target - applied) <= 0.001f) return;
            try
            {
                it.Move.localScale = it.Move.localScale * (target / applied);
                applied = target;
                Log.LogInfo($"[grab] {label} rescaled x{target:0.##}.");
            }
            catch (Exception e) { Log.LogWarning($"[grab] {label} rescale: " + e.Message); }
        }

        // The clipboards' rest position is re-applied each frame by ClipboardAspectRatioOffsetFader
        // (a flat-screen feature) which fights our placement. Disable them so grabs/head-lock stick.
        private void DisablePositionFaders()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ClipboardAspectRatioOffsetFader>(), FindObjectsSortMode.None);
                if (arr == null) return;
                int n = 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    var f = arr[i].TryCast<ClipboardAspectRatioOffsetFader>();
                    if (f != null && f.enabled) { f.enabled = false; n++; }
                }
                if (n > 0) Log.LogInfo($"[grab] disabled {n} clipboard position-fader(s).");
            }
            catch (Exception e) { Log.LogWarning("[grab] fader disable: " + e.Message); }
        }

        // The watch ("Gun Watch Parent") carries an Animator (+ AnimatorBoolTogglers) that animates its
        // local pose each frame (wrist raise / show-hide) — it overwrites our grab/head-lock placement,
        // so the watch was unmovable AND followed the flat camera regardless of the rotate toggle. The
        // needles are driven by the GunStopwatch SCRIPT (not the Animator), so disabling the Animator
        // keeps the hands ticking; it just stops the transform from being re-posed. Same idea as the
        // clipboard's position-fader. (The watch then stays visible, which is what we want in VR.)
        private static void DisableWatchAnimators(Transform watch)
        {
            try
            {
                var arr = watch.GetComponentsInChildren<Animator>(true);
                if (arr == null) return;
                int n = 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    var a = arr[i];
                    if (a != null && a.enabled) { a.enabled = false; n++; }
                }
                if (n > 0) Log.LogInfo($"[grab] disabled {n} watch animator(s).");
            }
            catch (Exception e) { Log.LogWarning("[grab] watch animator disable: " + e.Message); }
        }

        private static void GetWorld(Transform origin, Posef pose, out Vector3 pos, out Quaternion rot)
        {
            var lp = new Vector3(pose.Position.X, pose.Position.Y, -pose.Position.Z);
            var lr = new Quaternion(-pose.Orientation.X, -pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
            pos = origin.TransformPoint(lp);
            rot = origin.rotation * lr;
        }

        // Disarm calibration without changing offsets (called when the settings menu closes, like the hand
        // Calibrate tool). The placed offset was already captured + saved on release.
        public void CancelCalibrate()
        {
            if (_calib == CalibTarget.None) return;
            _calib = CalibTarget.None;
            _holdAdjust = false;
            try { Config.Save(); } catch { }
        }

        public void Reset()
        {
            UnhookRenderCallback();
            // Put the clipboard holder back under its original parent (it was lifted out of Main Camera) before we
            // forget it — so a VR-exit / scene teardown leaves the hierarchy as we found it. No-op if destroyed.
            if (_hudReparented && _hud != null && _hud.Move != null)
            {
                try { _hud.Move.SetParent(_hudOrigParent, true); } catch { }
            }
            _hudReparented = false; _hudOrigParent = null;
            _items.Clear();
            _hud = null; _watch = null; _grabbed = null; _hand = 0;
            _appliedScale = 1f; _appliedWatchScale = 1f; _fadersOff = false;
            _lastManualCount = -1;
            _nextScan = 0f;
            _calib = CalibTarget.None;
            _holdAdjust = false; _prevHoldGrip = false;
        }
    }
}
