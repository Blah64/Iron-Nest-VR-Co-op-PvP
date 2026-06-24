using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// How a single click switch/lever (<c>LookAtTarget</c>) physically MOVES when you grab and operate it.
    /// The motion is defined in the switch's own local space, so it's the same wherever the control sits in
    /// the world. Stored per switch (keyed by name) in <see cref="SwitchMotions"/>, auto-seeded on first grab,
    /// tunable in the VR menu, and persisted alongside the other settings.
    /// </summary>
    internal struct SwitchMotion
    {
        public bool Translate;     // false = rotate the switch about Axis; true = slide it along Axis
        public Vector3 Axis;       // local principal axis (unit) of the rotation hinge or slide direction
        public float Range;        // magnitude at full throw: degrees (rotate) or metres (slide)
        public bool Flip;          // reverse the motion direction
        public Vector3 PushLocal;  // local-space hand-push direction that activates it (captured from the throw)
        public bool HasPush;       // whether PushLocal has been captured yet
        public bool ManualAxis;    // player dialled Axis in the VR menu → the follow uses it instead of auto-inferring

        public static SwitchMotion Default => new SwitchMotion
        {
            Translate = false,
            Axis = new Vector3(1f, 0f, 0f),
            Range = 25f,
            Flip = false,
            PushLocal = Vector3.zero,
            HasPush = false,
            ManualAxis = false,
        };
    }

    /// <summary>
    /// Registry of per-switch <see cref="SwitchMotion"/>s, keyed by the control's name. <see cref="Get"/>
    /// returns the saved motion or a sensible default (so an unknown switch still moves on first grab); the
    /// in-VR tuning writes back with <see cref="Set"/>. Serialized to/from the settings file by
    /// <see cref="Config"/> via <see cref="Save"/>/<see cref="Apply"/>.
    /// </summary>
    internal static class SwitchMotions
    {
        private static readonly Dictionary<string, SwitchMotion> Map = new Dictionary<string, SwitchMotion>();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static SwitchMotion Get(string key)
        {
            if (key != null && Map.TryGetValue(key, out var m)) return m;
            return SwitchMotion.Default;
        }

        // Whether an explicit motion has been stored for this key (vs Get returning the shared Default).
        public static bool Has(string key) => key != null && Map.ContainsKey(key);

        // Like Get, but reports whether the key actually exists (Default is a value type, so Get can't say).
        public static bool TryGet(string key, out SwitchMotion m)
        {
            if (key != null && Map.TryGetValue(key, out m)) return true;
            m = SwitchMotion.Default; return false;
        }

        public static void Set(string key, SwitchMotion m) { if (!string.IsNullOrEmpty(key)) Map[key] = m; }
        public static void Remove(string key) { if (key != null) Map.Remove(key); }

        // Append one "SwitchMotion=<name>|<t>|<ax ay az>|<range>|<flip>|<px py pz>|<haspush>" line per entry.
        // The name comes first and never contains '|', so the rest parses unambiguously.
        public static void Save(StringBuilder sb)
        {
            foreach (var kv in Map)
            {
                var m = kv.Value;
                sb.Append("SwitchMotion=").Append(kv.Key).Append('|')
                  .Append(m.Translate ? '1' : '0').Append('|')
                  .Append(F(m.Axis.x)).Append(' ').Append(F(m.Axis.y)).Append(' ').Append(F(m.Axis.z)).Append('|')
                  .Append(F(m.Range)).Append('|')
                  .Append(m.Flip ? '1' : '0').Append('|')
                  .Append(F(m.PushLocal.x)).Append(' ').Append(F(m.PushLocal.y)).Append(' ').Append(F(m.PushLocal.z)).Append('|')
                  .Append(m.HasPush ? '1' : '0').Append('|')
                  .Append(m.ManualAxis ? '1' : '0').AppendLine();
            }
        }

        // Baked-in calibration (user-tuned 2026-06-24) for the review-console switches + power lever, so a fresh install
        // or a deleted cfg still gets their correct rotation axes / ranges out of the box. Uses the same line format as
        // the cfg and goes through Apply, so it's seeded into the Map BEFORE Config.Load reads the cfg — meaning a saved
        // cfg line (the player's own later re-tuning) overrides these. Call once at the very start of Config.Load.
        public static void SeedDefaults()
        {
            Apply("Universal Switch Button Variant@Power Lever@Power Lever Parent@Power Box|0|1 0 0|90|1|-0.02621307 -0.020732565 0.9994414|1|1");
            Apply("Universal Switch Button@.Check Switch@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|0|0.8146251 -0.24210104 -0.52704173|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.001@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|-0.6298143 0.68924737 -0.35815063|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.002@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.7263866 -0.54266137 -0.4217597|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.003@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.647286 -0.6134182 -0.45248073|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.004@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0 0 0|0|1");
            Apply("Universal Button Move Cylinder@--Reloading Console@Gun System Right|0|0 1 0|90|1|-0.007311628 0.19845298 -0.9800831|1|1");
        }

        // Parse one line's value (everything after "SwitchMotion=").
        public static void Apply(string value)
        {
            var p = value.Split('|');
            if (p.Length < 7) return;
            string key = p[0];
            if (key.Length == 0) return;
            Map[key] = new SwitchMotion
            {
                Translate = p[1] == "1",
                Axis = V(p[2], new Vector3(1f, 0f, 0f)),
                Range = PF(p[3], 25f),
                Flip = p[4] == "1",
                PushLocal = V(p[5], Vector3.zero),
                HasPush = p[6] == "1",
                ManualAxis = p.Length > 7 && p[7] == "1",   // older saves omit this field → defaults to auto-infer
            };
        }

        private static string F(float v) => v.ToString("R", Inv);
        private static float PF(string s, float d) => float.TryParse(s, NumberStyles.Float, Inv, out var r) ? r : d;
        private static Vector3 V(string s, Vector3 d)
        {
            var a = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length != 3) return d;
            return new Vector3(PF(a[0], d.x), PF(a[1], d.y), PF(a[2], d.z));
        }
    }
}
