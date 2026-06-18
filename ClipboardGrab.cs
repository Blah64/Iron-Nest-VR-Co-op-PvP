using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Grab-to-place for the held clipboard. Squeeze the grip on either hand while your hand is near the
    /// clipboard to grab it; move/rotate your hand to position it; release to lock it there. It then stays
    /// put relative to you (it's a child of Main Camera, so it follows you as you move but doesn't spin
    /// with your head). Trigger still clicks its buttons.
    ///
    /// We drive the clipboard's STATIC HOLDER ("Static notepad Parent", the Main Camera child) by setting
    /// its world pose — we do NOT re-parent it, so the clipboard's raise/lower animation and the tutorial
    /// "move note onto clipboard" consolidation keep working.
    /// </summary>
    internal sealed class ClipboardGrab
    {
        private static ManualLogSource Log => Plugin.Logger;

        private ClipboardStateController _cb;
        private Transform _clip;   // the clipboard itself ("Notepad Parent") — proximity target
        private Transform _holder; // its parent ("Static notepad Parent") — what we move
        private float _nextFind;

        private int _hand;         // 0 = none, 1 = left, 2 = right
        private Vector3 _offPos;   // clipboard pose relative to the grabbing controller
        private Quaternion _offRot;

        public void Tick(VrInput input, CameraRig rig, bool active)
        {
            try
            {
                if (!Config.ClipboardGrabEnabled || !active) { _hand = 0; return; }
                EnsureFound();
                if (_holder == null) return;
                var origin = rig.OriginTransform;
                if (origin == null) return;

                if (_hand == 0)
                {
                    if (input.GrabR && input.GripValid && Near(origin, input.GripPose)) Start(2, origin, input.GripPose, input);
                    else if (input.GrabL && input.GripValidL && Near(origin, input.GripPoseL)) Start(1, origin, input.GripPoseL, input);
                }
                else
                {
                    bool held = _hand == 2 ? (input.GrabR && input.GripValid) : (input.GrabL && input.GripValidL);
                    if (!held) { _hand = 0; return; }
                    Posef p = _hand == 2 ? input.GripPose : input.GripPoseL;
                    GetWorld(origin, p, out var cp, out var cr);
                    _holder.SetPositionAndRotation(cp + cr * _offPos, cr * _offRot);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[grab] " + e.Message);
                _cb = null; _clip = null; _holder = null; _hand = 0;
            }
        }

        private void Start(int hand, Transform origin, Posef p, VrInput input)
        {
            GetWorld(origin, p, out var cp, out var cr);
            var inv = Quaternion.Inverse(cr);
            _offPos = inv * (_holder.position - cp);
            _offRot = inv * _holder.rotation;
            _hand = hand;
            input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            Log.LogInfo($"[grab] clipboard grabbed ({(hand == 2 ? "right" : "left")} grip)");
        }

        private bool Near(Transform origin, Posef gripPose)
        {
            GetWorld(origin, gripPose, out var cp, out _);
            Vector3 target = _clip != null ? _clip.position : _holder.position;
            return Vector3.Distance(cp, target) <= Config.GrabRadius;
        }

        private static void GetWorld(Transform origin, Posef pose, out Vector3 pos, out Quaternion rot)
        {
            var lp = new Vector3(pose.Position.X, pose.Position.Y, -pose.Position.Z);
            var lr = new Quaternion(-pose.Orientation.X, -pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);
            pos = origin.TransformPoint(lp);
            rot = origin.rotation * lr;
        }

        private void EnsureFound()
        {
            if (_holder != null) return;
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;

            var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ClipboardStateController>(), FindObjectsSortMode.None);
            if (arr != null && arr.Length > 0)
            {
                _cb = arr[0].TryCast<ClipboardStateController>();
                _clip = _cb != null ? _cb.transform : null;
                _holder = _clip != null ? _clip.parent : null;
                if (_holder != null)
                {
                    DisablePositionConstrainers();
                    if (Config.ClipboardScale > 0f && Mathf.Abs(Config.ClipboardScale - 1f) > 0.001f)
                    {
                        _holder.localScale = _holder.localScale * Config.ClipboardScale;
                        Log.LogInfo($"[grab] clipboard scaled x{Config.ClipboardScale:0.##} for VR.");
                    }
                    Log.LogInfo($"[grab] clipboard ready (holder '{_holder.name}', grip-squeeze near it to move).");
                }
            }
        }

        // The clipboard's resting position is re-applied every frame by ClipboardAspectRatioOffsetFader
        // (a flat-screen-only feature). That overwrote our grip move, pinning the clipboard to a fixed
        // distance (orbit). Disable it so the grabbed position actually sticks.
        private void DisablePositionConstrainers()
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
                if (n > 0) Log.LogInfo($"[grab] disabled {n} clipboard position-fader(s) for free placement.");
            }
            catch (Exception e) { Log.LogWarning("[grab] fader disable: " + e.Message); }
        }

        public void Reset() { _cb = null; _clip = null; _holder = null; _hand = 0; }
    }
}
