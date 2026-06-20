using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Procedural finger-curl rig for hand FBXs that ship without an Animator (the XR Hands sample meshes).
    /// This is the reusable form of the curl logic in <see cref="HandVisuals"/> — it's used to bend the
    /// REMOTE player's hand mesh (<see cref="RemoteAvatar"/>) from the curl amounts streamed over the pose
    /// channel, so a teammate's fingers visibly close around a dial/trigger instead of staying open.
    ///
    /// HandVisuals keeps its own (in-headset validated) copy for the local hands — this mirror is deliberately
    /// kept separate so the remote-hand work can't perturb the local-hand path. Both read the same
    /// <see cref="Config"/> curl tunables, so they stay in lock-step. Bones are matched by name and rotated off
    /// their bind pose; remote hands load the real left/right prefabs (not mirrored), so no sign flip is needed.
    /// </summary>
    internal static class FingerCurl
    {
        private static ManualLogSource Log => Plugin.Logger;

        public sealed class Joint
        {
            public Transform T;
            public Quaternion Bind;   // bone's local rotation as loaded
            public bool IsThumb;
            public bool IsIndex;
        }

        // Find the bendy finger-joint bones under a hand model. Matches finger {thumb/index/middle/ring/
        // little|pinky} × segment {proximal/intermediate/middle/distal}; skips metacarpal/tip/wrist. Mirror of
        // HandVisuals.CollectJoints (kept in sync). Names are read via Object.GetName() — the .name getter hits
        // a broken injected span path in this il2cpp build.
        public static List<Joint> Collect(GameObject model)
        {
            var list = new List<Joint>();
            if (model == null) return list;
            Transform[] all;
            try { all = model.GetComponentsInChildren<Transform>(true); } catch { return list; }
            int n = all != null ? all.Length : 0;
            for (int i = 0; i < n; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string nm = SafeName(t);
                if (nm == null) continue;
                string low = nm.ToLowerInvariant();

                bool thumb = low.Contains("thumb");
                bool index = low.Contains("index");
                bool middle = low.Contains("middle");
                bool ring = low.Contains("ring");
                bool little = low.Contains("little") || low.Contains("pinky");
                if (!(thumb || index || middle || ring || little)) continue;

                bool curlSeg = low.Contains("proximal") || low.Contains("intermediate")
                            || low.Contains("distal") || low.Contains("middle");
                // "middle" is both a finger AND a segment word; for the middle FINGER require an explicit segment.
                if (middle && !(low.Contains("proximal") || low.Contains("intermediate") || low.Contains("distal")))
                    curlSeg = false;
                if (!curlSeg) continue;
                if (low.Contains("metacarpal") || low.Contains("tip")) continue;

                list.Add(new Joint { T = t, Bind = t.localRotation, IsThumb = thumb, IsIndex = index });
            }
            return list;
        }

        // Bend the joints: the index follows curlIndex, every other finger + the thumb follow curlOther. Each
        // joint rotates off its bind pose about the configured local axis. signMul flips the fold direction for
        // a mirrored model (remote hands aren't mirrored, so the default 1 is correct).
        public static void Apply(List<Joint> joints, float curlIndex, float curlOther, float signMul = 1f)
        {
            if (!Config.FingerCurlEnabled || joints == null || joints.Count == 0) return;
            Vector3 axis = Config.FingerCurlAxis == 0 ? Vector3.right
                         : Config.FingerCurlAxis == 1 ? Vector3.up : Vector3.forward;
            float sign = Config.FingerCurlSign * signMul;
            float max = Config.FingerCurlMaxDeg;
            for (int i = 0; i < joints.Count; i++)
            {
                var j = joints[i];
                if (j.T == null) continue;
                float amt = j.IsIndex ? curlIndex : curlOther;
                float deg = sign * amt * max * (j.IsThumb ? 0.6f : 1f); // thumb folds less
                j.T.localRotation = j.Bind * Quaternion.AngleAxis(deg, axis);
            }
        }

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.GetName() : null; } catch { return null; }
        }
    }
}
