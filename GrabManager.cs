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
    /// tutorial clipboards. Squeeze the grip on either hand near a prop to grab it; move/rotate your
    /// hand to position it; release to drop it.
    ///
    /// Two follow modes:
    ///  • <b>HeadLocked</b> (HUD clipboard + watch): after release they ride the VR head at the
    ///    grab-set offset — they rotate WITH the camera and stay in the same spot in your view.
    ///  • <b>WorldLocked</b> (tutorial clipboards): they stay where you drop them in the world and do
    ///    NOT follow the head.
    ///
    /// We drive each prop's transform by world pose (never re-parent — that breaks the clipboard's
    /// note-consolidation). The HUD clipboard is the one whose holder lives under Main Camera; tutorial
    /// clipboards are <c>ClipboardStateController</c>s that aren't. The watch is the Main-Camera child
    /// whose name contains "Watch".
    /// </summary>
    internal sealed class GrabManager
    {
        private static ManualLogSource Log => Plugin.Logger;

        private enum Mode { HeadLocked, WorldLocked }

        private sealed class Item
        {
            public string Name;
            public Transform Move;     // transform whose world pose we set
            public Transform Prox;     // proximity target for grabbing (fallback)
            public Renderer Rend;      // visible mesh — grab by its bounds centre (parent pivot can be offset)
            public Mode Mode;
            public bool IsHudClip;     // the one we scale for VR
            public PickUpZoomTarget Zoom; // non-null for world "operating manual" props (the game's pick-up-to-read)
            // Resting offset captured (on find / grab-release) in two frames so the
            // "rotate with camera" toggle can switch at runtime: head-relative (rotates with view)
            // and rig-origin-relative (follows position, fixed orientation).
            public Vector3 HeadOffPos; public Quaternion HeadOffRot;
            public Vector3 OriginOffPos; public Quaternion OriginOffRot;
            public bool HasOffset;
        }

        private readonly Dictionary<int, Item> _items = new Dictionary<int, Item>();
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
                    if (!held || !Grabbable(_grabbed)) Release(rig); // drop if grip released, destroyed, or a manual started zooming
                    else
                    {
                        Posef p = _hand == 2 ? input.GripPose : input.GripPoseL;
                        GetWorld(origin, p, out var cp, out var cr);
                        _grabbed.Move.SetPositionAndRotation(cp + cr * _gPos, cr * _gRot);
                    }
                }

                DriveFollowers(rig);
            }
            catch (Exception e)
            {
                Log.LogWarning("[grab] " + e.Message);
                _items.Clear(); _hud = null; _grabbed = null; _hand = 0;
            }
        }

        // Head-locked props follow the VR head at their resting offset (except the one being grabbed).
        // Config.HudRotateWithCamera picks the frame: head (rotates with view) or rig origin (follows
        // position only, fixed orientation — you turn to look at it).
        private void DriveFollowers(CameraRig rig)
        {
            if (!rig.TryGetHeadPose(out var hp, out var hr)) return;
            var origin = rig.OriginTransform;
            if (origin == null) return;
            bool rotate = Config.HudRotateWithCamera;
            var cam = Camera.main;
            foreach (var it in _items.Values)
            {
                if (it == _grabbed || it.Mode != Mode.HeadLocked || it.Move == null) continue;
                if (!it.HasOffset) CaptureInitial(it, hp, hr, origin, cam); // start it in view, in front of you
                if (rotate)
                    it.Move.SetPositionAndRotation(hp + hr * it.HeadOffPos, hr * it.HeadOffRot);
                else
                    it.Move.SetPositionAndRotation(origin.TransformPoint(it.OriginOffPos), origin.rotation * it.OriginOffRot);
            }
        }

        // Initial placement: a HUD prop is authored in front of MAIN CAMERA, but Main Camera doesn't
        // track the VR head — so its current world pose can be off-screen/behind in VR. Transplant its
        // Main-Camera-relative (authored) pose onto the VR head so it starts in front of you, in view.
        private static void CaptureInitial(Item it, Vector3 hp, Quaternion hr, Transform origin, Camera cam)
        {
            Vector3 camLocalPos;
            Quaternion camLocalRot;
            if (cam != null)
            {
                var ct = cam.transform;
                camLocalPos = ct.InverseTransformPoint(it.Move.position);
                camLocalRot = Quaternion.Inverse(ct.rotation) * it.Move.rotation;
            }
            else
            {
                var invH = Quaternion.Inverse(hr);
                camLocalPos = invH * (it.Move.position - hp);
                camLocalRot = invH * it.Move.rotation;
            }

            it.HeadOffPos = camLocalPos;
            it.HeadOffRot = camLocalRot;

            // Same world pose, expressed in the rig-origin frame too (for the no-rotate follow mode).
            Vector3 wpos = hp + hr * camLocalPos;
            Quaternion wrot = hr * camLocalRot;
            it.OriginOffPos = origin.InverseTransformPoint(wpos);
            it.OriginOffRot = Quaternion.Inverse(origin.rotation) * wrot;
            it.HasOffset = true;
        }

        private static void CaptureOffset(Item it, Vector3 hp, Quaternion hr, Transform origin)
        {
            Vector3 wp = it.Move.position;
            Quaternion wr = it.Move.rotation;
            var invH = Quaternion.Inverse(hr);
            it.HeadOffPos = invH * (wp - hp);
            it.HeadOffRot = invH * wr;
            it.OriginOffPos = origin.InverseTransformPoint(wp);
            it.OriginOffRot = Quaternion.Inverse(origin.rotation) * wr;
            it.HasOffset = true;
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
            if (!Physics.Raycast(ao, dir, out RaycastHit hit, Config.LaserMaxDistance, ~0, QueryTriggerInteraction.Collide))
                return false;
            var col = hit.collider;
            var it = FindItemForCollider(col != null ? col.transform : null);
            if (it == null || it.Zoom == null || !Grabbable(it))
            {
                if (Time.unscaledTime >= _nextRayLog)
                {
                    _nextRayLog = Time.unscaledTime + 0.5f;
                    Log.LogInfo($"[grab] ray hit '{(col != null ? col.name : "null")}' @ {hit.distance:0.0}m — not a grabbable manual.");
                }
                return false;
            }
            GetWorld(origin, gripPose, out var cp, out var cr);
            Attach(it, hand, cp, cr, input, "ray@" + hit.distance.ToString("0.0") + "m");
            return true;
        }

        // Map a hit collider back to a tracked item by walking up to a transform we know.
        private Item FindItemForCollider(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
                if (_items.TryGetValue(p.GetInstanceID(), out var it)) return it;
            return null;
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

        // A prop is grabbable unless it's a world manual that's inactive or mid pick-up/put-back ANIMATION
        // (the game's coroutine owns the transform then — grabbing would fight the lerp). We DO allow
        // grabbing while it's floated-up-to-read (isHeld && settled): PickUpZoomTarget has no Update, so
        // once the move coroutine finishes nothing re-poses it — this is the state the user grabs it in.
        private static bool Grabbable(Item it)
        {
            if (it == null || it.Move == null) return false;
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

        private void Release(CameraRig rig)
        {
            var origin = rig.OriginTransform;
            if (_grabbed != null && _grabbed.Mode == Mode.HeadLocked && _grabbed.Move != null
                && origin != null && rig.TryGetHeadPose(out var hp, out var hr))
            {
                CaptureOffset(_grabbed, hp, hr, origin); // remember the new spot in both frames
                PersistPlacement(_grabbed);              // and save it to disk so it survives the session
            }
            if (_grabbed != null) Log.LogInfo($"[grab] released '{_grabbed.Name}'.");
            _grabbed = null;
            _hand = 0;
        }

        // ---------------- placement persistence (HUD clipboard + watch) ----------------

        // Apply a previously-saved drag position to a freshly-discovered HUD prop so it re-appears where the
        // player last left it (instead of the game's authored default). Both follow frames are restored;
        // HasOffset=true makes DriveFollowers use them straight away and skip CaptureInitial.
        private static void RestorePlacement(Item it, bool isClip)
        {
            if (it == null) return;
            bool saved = isClip ? Config.ClipPlacementSaved : Config.WatchPlacementSaved;
            if (!saved) return;
            if (isClip)
            {
                it.HeadOffPos = Config.ClipHeadOffPos; it.HeadOffRot = Quaternion.Euler(Config.ClipHeadOffEul);
                it.OriginOffPos = Config.ClipOriginOffPos; it.OriginOffRot = Quaternion.Euler(Config.ClipOriginOffEul);
            }
            else
            {
                it.HeadOffPos = Config.WatchHeadOffPos; it.HeadOffRot = Quaternion.Euler(Config.WatchHeadOffEul);
                it.OriginOffPos = Config.WatchOriginOffPos; it.OriginOffRot = Quaternion.Euler(Config.WatchOriginOffEul);
            }
            it.HasOffset = true;
            Log.LogInfo($"[grab] restored saved placement for {it.Name}.");
        }

        // Mirror the just-captured offset into Config and write the settings file immediately (releases are
        // user-paced, so a disk write here is fine). Only the two head-locked HUD props are persisted.
        private void PersistPlacement(Item it)
        {
            bool isClip = it == _hud;
            bool isWatch = it == _watch;
            if (!isClip && !isWatch) return;
            if (isClip)
            {
                Config.ClipHeadOffPos = it.HeadOffPos; Config.ClipHeadOffEul = it.HeadOffRot.eulerAngles;
                Config.ClipOriginOffPos = it.OriginOffPos; Config.ClipOriginOffEul = it.OriginOffRot.eulerAngles;
                Config.ClipPlacementSaved = true;
            }
            else
            {
                Config.WatchHeadOffPos = it.HeadOffPos; Config.WatchHeadOffEul = it.HeadOffRot.eulerAngles;
                Config.WatchOriginOffPos = it.OriginOffPos; Config.WatchOriginOffEul = it.OriginOffRot.eulerAngles;
                Config.WatchPlacementSaved = true;
            }
            try { Config.Save(); } catch (Exception e) { Log.LogWarning("[grab] save placement: " + e.Message); }
            Log.LogInfo($"[grab] saved placement for {it.Name}.");
        }

        // Forget the saved HUD placements: clears the persisted flags and re-runs the authored default placement
        // on the next frame. Exposed for a menu "Reset HUD Positions" action. NOTE: re-default works because the
        // game re-creates the clipboard/watch at their authored pose on a scene/mission reload — so for an
        // immediate reset in the current scene, drop & re-acquire (or reload) to pick the authored pose back up.
        public void ResetHudPlacement()
        {
            Config.ClipPlacementSaved = false;
            Config.WatchPlacementSaved = false;
            try { Config.Save(); } catch { }
            Log.LogInfo("[grab] HUD placements reset (clipboard + watch will use authored default on next (re)spawn).");
        }

        // ---------------- discovery ----------------

        private void Scan()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            var seen = new HashSet<int>();
            var cam = Camera.main;
            Transform camT = cam != null ? cam.transform : null;

            // Clipboards: HUD (holder under Main Camera) vs tutorial (everything else).
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
                            Mode = hud ? Mode.HeadLocked : Mode.WorldLocked,
                            IsHudClip = hud
                        };
                        _items[id] = it;
                        if (hud)
                        {
                            _hud = it;
                            _appliedScale = 1f;
                            if (!_fadersOff) { DisablePositionFaders(); _fadersOff = true; }
                            ReconcileScale();
                            RestorePlacement(it, true);   // re-appear where you last dragged it (if saved)
                        }
                        Log.LogInfo($"[grab] tracking {it.Name} ({it.Mode}).");
                    }
                }
            }

            // Watch: the Main-Camera child whose name contains "Watch".
            if (camT != null)
            {
                var watch = FindByNameContains(camT, "watch");
                if (watch != null)
                {
                    int id = watch.GetInstanceID();
                    seen.Add(id);
                    if (!_items.ContainsKey(id))
                    {
                        var it = new Item { Name = "watch '" + watch.name + "'", Move = watch, Prox = watch, Rend = watch.GetComponentInChildren<Renderer>(true), Mode = Mode.HeadLocked };
                        _items[id] = it;
                        _watch = it;
                        _appliedWatchScale = 1f;
                        DisableWatchAnimators(watch); // stop the wrist-raise Animator re-posing it each frame
                        ReconcileScale();
                        RestorePlacement(it, false);  // re-appear where you last dragged it (if saved)
                        Log.LogInfo($"[grab] tracking watch '{watch.name}' (HeadLocked).");
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
                var dead = new List<int>();
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

        public void Reset()
        {
            _items.Clear();
            _hud = null; _watch = null; _grabbed = null; _hand = 0;
            _appliedScale = 1f; _appliedWatchScale = 1f; _fadersOff = false;
            _lastManualCount = -1;
            _nextScan = 0f;
        }
    }
}
