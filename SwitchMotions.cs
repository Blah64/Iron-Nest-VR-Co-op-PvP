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

        // Baked-in calibration (user-tuned, full pass 2026-06-25) for EVERY lever/slider/switch, so a fresh install or a deleted
        // cfg gives new players the correct motion direction out of the box — they can't accidentally throw a lever the wrong way
        // on the first pull. Uses the same line format as the cfg and goes through Apply, so it's seeded into the Map BEFORE
        // Config.Load reads the cfg — meaning a saved cfg line (the player's own later re-tuning) still overrides these. Call once
        // at the very start of Config.Load. To refresh: re-tune in-headset, then re-export the SwitchMotion= lines from the cfg.
        public static void SeedDefaults()
        {
            Apply("Universal Switch Button Variant@Power Lever@Power Lever Parent@Power Box|0|1 0 0|90|1|-0.02621307 -0.020732565 0.9994414|1|1");
            Apply("Universal Switch Button@.Check Switch@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|0|0.8146251 -0.24210104 -0.52704173|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.001@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|-0.6298143 0.68924737 -0.35815063|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.002@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.7263866 -0.54266137 -0.4217597|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.003@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.647286 -0.6134182 -0.45248073|1|1");
            Apply("Universal Switch Button Variant@.Check Switch.004@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.0014177166 -0.73192436 -0.6813844|1|1");
            Apply("Universal Button Move Cylinder@--Reloading Console@Gun System Right|0|0 1 0|90|1|-0.007311628 0.19845298 -0.9800831|1|1");
            Apply("Universal Button|1|0 0 1|0.3999999|1|0.22816 0.28576458 -0.9307425|1|1");
            Apply("Calculate Universal Button|0|1 0 0|25|1|0.36451685 -0.7213279 0.5889088|1|1");
            Apply("Universal Button Move Cylinder|0|1 0 0|25|0|0.99867576 -0.051444698 -0.0003775128|1|0");
            Apply("Universal Button Load shell Rammer|1|1 0 0|0.3999999|0|-0.1628543 -0.37299356 0.91343|1|0");
            Apply("Button Dispencer (1)|0|0 1 0|25|1|0.21541348 -0.035241306 0.9758869|1|0");
            Apply("Button Dispencer (2)|0|0 1 0|25|1|-0.08155644 0.0811569 0.993359|1|0");
            Apply("Button Dispencer (3)|0|0 1 0|25|1|-0.23370206 -0.017419327 0.9721522|1|0");
            Apply("Button Dispencer (4)|0|0 1 0|25|1|-0.23717906 0.15076959 0.95969504|1|0");
            Apply("Button Dispencer (5)|0|0 1 0|25|1|-0.08797458 0.4735802 0.876346|1|0");
            Apply("Button Dispencer (6)|0|0 1 0|25|1|-0.26464856 0.5090775 0.81902456|1|0");
            Apply("Universal Button Charge Rammer (1)|0|0 1 0|25|0|0.061300773 0.15419963 0.9861363|1|1");
            Apply("universal button|0|1 0 0|25|0|0.038201254 0.023993324 -0.998982|1|0");
            Apply("Universal Switch Button|0|1 0 0|25|0|0.94266254 -0.23930416 -0.23263903|1|0");
            Apply("Universal Button Arm Left|0|1 0 0|25|0|-0.21369077 0.24350819 0.94606555|1|0");
            Apply("Universal Toogle Switch Button Variant|0|1 0 0|25|0|-0.6139409 0.6234944 -0.48407787|1|0");
            Apply("Universal Switch Button Variant|0|1 0 0|90|1|0.70741665 0.24042559 -0.66464823|1|1");
            Apply("Universal Button@Floor Hatch Barbet Stars|0|1 0 0|25|0|0.17930134 -0.7503666 -0.63623965|1|0");
            Apply("Universal Button@Trigger Console|0|1 0 0|25|0|-0.6581829 0.053846445 -0.7509299|1|0");
            Apply("Universal Button@Requisition Console|1|1 0 0|0.3999999|1|-0.22520816 -0.00069617695 -0.97431034|1|0");
            Apply("Universal Switch Button@.Check Switch|0|1 0 0|25|0|0.3595588 0.09982921 -0.92776704|1|0");
            Apply("Universal Button@Floor Hatch Barbet Stars@Turret|0|1 0 0|25|0|-0.122811854 0.72200423 -0.68090177|1|0");
            Apply("universal button@War Horn@War Horn Parent|0|1 0 0|25|0|-0.040010385 0.031871893 -0.9986908|1|0");
            Apply("Calculate Universal Button@Artillery Computer Console|0|1 0 0|90|1|-0.25103912 -0.514366 0.8200042|1|1");
            Apply("Universal Button Move Cylinder@--Reloading Console@Gun System Left|0|0 1 0|90|1|0.95446575 -0.2863874 0.08353066|1|1");
            Apply("Universal Button Load shell Rammer@--Reloading Console@Gun System Left|1|1 0 0|0.3999999|0|0.51707006 -0.10338815 0.8496761|1|0");
            Apply("Universal Button Load shell Rammer@--Reloading Console@Gun System Right|1|1 0 0|0.3999999|0|-0.43526945 -0.2860667 0.853643|1|0");
            Apply("Button Dispencer (1)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.1546257 0.26916683 0.95059985|1|0");
            Apply("Button Dispencer (2)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|0.66218823 -0.725908 0.18591516|1|0");
            Apply("Button Dispencer (3)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.06284219 -0.19044623 0.9796842|1|0");
            Apply("Button Dispencer (4)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.16829199 -0.020881636 0.985516|1|0");
            Apply("Button Dispencer (5)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|0.03852901 -0.14287744 0.9889902|1|0");
            Apply("Button Dispencer (6)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.016494418 -0.05137145 0.9985433|1|0");
            Apply("Button Dispencer (1)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.117551416 0.447876 -0.8863344|1|0");
            Apply("Button Dispencer (2)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.06347864 -0.24202031 -0.9681924|1|0");
            Apply("Button Dispencer (3)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.23030023 -0.07861772 -0.9699387|1|0");
            Apply("Button Dispencer (4)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.30218944 -0.24596359 -0.9209687|1|0");
            Apply("Button Dispencer (5)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.10293663 -0.32261202 -0.9409174|1|0");
            Apply("Button Dispencer (6)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|-0.60922575 0.51687044 0.60140586|1|0");
            Apply("Universal Button Charge Rammer (1)@--Reloading Console@Gun System Right|0|0 1 0|25|0|0.32232627 0.47796977 0.8170989|1|1");
            Apply("Universal Button Charge Rammer (1)@--Reloading Console@Gun System Left|0|0 1 0|25|0|0.3520076 0.5276589 0.7730891|1|1");
            Apply("Universal Button Arm Left@.PowderRamLever.007@.ArmingLeverParent Left@.Trigger Core|0|1 0 0|25|0|-0.36389634 -0.032376274 0.9308766|1|0");
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
