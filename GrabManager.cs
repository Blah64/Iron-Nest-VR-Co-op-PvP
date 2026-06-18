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
            public Transform Prox;     // proximity target for grabbing
            public Mode Mode;
            public bool IsHudClip;     // the one we scale for VR
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
        private bool _fadersOff;

        // ---------------- per-frame ----------------

        public void Tick(VrInput input, CameraRig rig, bool active)
        {
            try
            {
                if (!Config.ClipboardGrabEnabled || !active) { _hand = 0; return; }
                Scan();
                var origin = rig.OriginTransform;
                if (origin == null) return;

                // Grab / drag / release.
                if (_grabbed == null)
                {
                    if (input.GrabR && input.GripValid) TryGrab(2, origin, input.GripPose, input);
                    else if (input.GrabL && input.GripValidL) TryGrab(1, origin, input.GripPoseL, input);
                }
                else
                {
                    bool held = _hand == 2 ? (input.GrabR && input.GripValid) : (input.GrabL && input.GripValidL);
                    if (!held || _grabbed.Move == null) Release(rig);
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

        private void TryGrab(int hand, Transform origin, Posef pose, VrInput input)
        {
            GetWorld(origin, pose, out var cp, out var cr);
            Item best = null;
            float bd = Config.GrabRadius;
            foreach (var it in _items.Values)
            {
                if (it.Prox == null) continue;
                float d = Vector3.Distance(cp, it.Prox.position);
                if (d <= bd) { bd = d; best = it; }
            }
            if (best == null) return;

            _grabbed = best;
            _hand = hand;
            var inv = Quaternion.Inverse(cr);
            _gPos = inv * (best.Move.position - cp);
            _gRot = inv * best.Move.rotation;
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[grab] grabbed '{best.Name}' ({(hand == 2 ? "right" : "left")} grip, {best.Mode}).");
        }

        private void Release(CameraRig rig)
        {
            var origin = rig.OriginTransform;
            if (_grabbed != null && _grabbed.Mode == Mode.HeadLocked && _grabbed.Move != null
                && origin != null && rig.TryGetHeadPose(out var hp, out var hr))
            {
                CaptureOffset(_grabbed, hp, hr, origin); // remember the new spot in both frames
            }
            if (_grabbed != null) Log.LogInfo($"[grab] released '{_grabbed.Name}'.");
            _grabbed = null;
            _hand = 0;
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
                        _items[id] = new Item { Name = "watch '" + watch.name + "'", Move = watch, Prox = watch, Mode = Mode.HeadLocked };
                        Log.LogInfo($"[grab] tracking watch '{watch.name}' (HeadLocked).");
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
                    _items.Remove(id);
                }
            }
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
            if (_hud == null || _hud.Move == null) return;
            float target = Config.ClipboardScale > 0f ? Config.ClipboardScale : 1f;
            if (Mathf.Abs(target - _appliedScale) <= 0.001f) return;
            try
            {
                _hud.Move.localScale = _hud.Move.localScale * (target / _appliedScale);
                _appliedScale = target;
                Log.LogInfo($"[grab] clipboard rescaled x{target:0.##}.");
            }
            catch (Exception e) { Log.LogWarning("[grab] rescale: " + e.Message); }
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
            _hud = null; _grabbed = null; _hand = 0;
            _appliedScale = 1f; _fadersOff = false;
            _nextScan = 0f;
        }
    }
}
