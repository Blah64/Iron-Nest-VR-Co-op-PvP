using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;

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

        // Calibration (menu-armed, like the hand-model Calibrate tool): physically grab the prop and
        // release — the resting pose is recomputed in the prop's own anchor frame and saved to the cfg.
        //  • Clipboard: grip-grab it (either hand), place it at your waist, release → waist offset/tilt.
        //  • Watch: it becomes grabbable; reach your RIGHT hand to your left wrist, grip & place, release
        //    → offset/orientation in the LEFT-grip (wrist) frame.
        // Stays armed until toggled off. Runs even with the menu open (VrManager calls Tick while armed).
        private enum CalibTarget { None, Clip, Watch }
        private CalibTarget _calib;
        public bool IsCalibrating => _calib != CalibTarget.None;
        public bool CalibratingClip => _calib == CalibTarget.Clip;
        public bool CalibratingWatch => _calib == CalibTarget.Watch;

        public void ToggleCalibrate(bool watch)
        {
            var want = watch ? CalibTarget.Watch : CalibTarget.Clip;
            _calib = (_calib == want) ? CalibTarget.None : want;
            if (_calib == CalibTarget.None) { try { Config.Save(); } catch { } } // persist on finish
            Log.LogInfo("[grab] calibrate " + (_calib == CalibTarget.None ? "off."
                : (watch ? "WATCH — reach your RIGHT hand to your left wrist, grip & place, release."
                         : "CLIPBOARD — grip it, place it at your waist, release.")));
        }

        // ---------------- per-frame ----------------

        public void Tick(VrInput input, CameraRig rig, bool active)
        {
            try
            {
                if (!Config.ClipboardGrabEnabled || !active) { _hand = 0; return; }
                Scan();
                var origin = rig.OriginTransform;
                if (origin == null) return;

                // Grab / drag / release. Right hand: RAY-grab first, because the head-locked watch/clipboard
                // are always within proximity range and would otherwise hijack every squeeze — so if the
                // laser is touching a world manual, grab THAT; only fall back to a proximity grab otherwise.
                // Left hand: proximity only (there's no left aim ray).
                if (_grabbed == null)
                {
                    if (input.GrabR && input.GripValid)
                    {
                        bool got = input.AimValid && TryRayGrab(2, origin, input.AimPose, input.GripPose, input);
                        if (!got) TryGrab(2, origin, input.GripPose, input);
                    }
                    else if (input.GrabL && input.GripValidL)
                    {
                        TryGrab(1, origin, input.GripPoseL, input);
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
                        _grabbed.Move.SetPositionAndRotation(cp + cr * _gPos, cr * _gRot);
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
                    it.Move.SetPositionAndRotation(pos, rot);
                }
                else if (it.Mode == Mode.WristLocked)
                {
                    if (!hasLeft) continue; // left controller dropped tracking: leave the watch put
                    Vector3 pos = lwp + lwr * Config.WatchWristOffset;
                    Quaternion rot = lwr * Quaternion.Euler(Config.WatchWristEuler);
                    it.Move.SetPositionAndRotation(pos, rot);
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
        // (DriveFollowers) and a world manual stays at the world pose it was released in.
        private void Release(CameraRig rig, VrInput input)
        {
            if (_grabbed != null)
            {
                if (_calib == CalibTarget.Clip && _grabbed == _hud) CaptureClipCalibration(rig);
                else if (_calib == CalibTarget.Watch && _grabbed == _watch) CaptureWatchCalibration(rig, input);
                Log.LogInfo($"[grab] released '{_grabbed.Name}'.");
            }
            _grabbed = null;
            _hand = 0;
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
                            IsHudClip = hud
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
            // grip-grab to reposition them in 3D, world-locked (no camera rotate). Tracked even while
            // inactive (so there's no add/remove churn) but only grabbable while resting + visible.
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
                                Zoom = pz
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
                    if (_hud != null && _items[id] == _hud) { _hud = null; }
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
            try { Config.Save(); } catch { }
        }

        public void Reset()
        {
            _items.Clear();
            _hud = null; _watch = null; _grabbed = null; _hand = 0;
            _appliedScale = 1f; _appliedWatchScale = 1f; _fadersOff = false;
            _lastManualCount = -1;
            _nextScan = 0f;
            _calib = CalibTarget.None;
        }
    }
}
