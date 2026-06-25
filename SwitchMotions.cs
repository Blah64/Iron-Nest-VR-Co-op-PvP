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
        // PER-HANDLE GRIP POSE: where/how each HAND sits when you grab this handle, captured in the VR menu's grip
        // calibration (two-hand grab+move) and expressed in the control's grip-reference transform (the swinging
        // hinge _leverT when there is one, else the stable _switchRef) so the hand rides the handle as it swings.
        // Re-applied on every grab instead of snapping the hand to the raw laser hit. Stored PER HAND because the
        // left hand model is X-mirrored — one pose used for both would put the off hand upside-down. HasGrip*=false
        // for a hand → that hand falls back to the laser hit point.
        public Vector3 GripPosR, GripEulR;   // right hand: anchor + orientation (euler), local to the grip-reference
        public bool HasGripR;
        public Vector3 GripPosL, GripEulL;   // left hand
        public bool HasGripL;
        // FREE-TWIST: optionally let the hand rotate about ONE handle-local axis (like a fist sliding round a
        // cylinder) tracking the controller's roll, instead of being rigidly locked to the calibrated orientation.
        // GripTwistAxis is a principal axis in the grip-reference frame; GripTwistFree gates it. Off by default.
        public Vector3 GripTwistAxis;
        public bool GripTwistFree;

        public static SwitchMotion Default => new SwitchMotion
        {
            Translate = false,
            Axis = new Vector3(1f, 0f, 0f),
            Range = 25f,
            Flip = false,
            PushLocal = Vector3.zero,
            HasPush = false,
            ManualAxis = false,
            GripPosR = Vector3.zero, GripEulR = Vector3.zero, HasGripR = false,
            GripPosL = Vector3.zero, GripEulL = Vector3.zero, HasGripL = false,
            GripTwistAxis = Vector3.zero, GripTwistFree = false,
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

        // Append one line per entry. Fields after <manualaxis> are the optional per-handle grip pose (older saves
        // omit them; Apply tolerates their absence). The name comes first and never contains '|', so it parses
        // unambiguously. Layout:
        //   SwitchMotion=<name>|<t>|<ax ay az>|<range>|<flip>|<px py pz>|<haspush>|<manualaxis>
        //     |<grx gry grz>|<erx ery erz>|<hasgripR>|<glx gly glz>|<elx ely elz>|<hasgripL>|<tax tay taz>|<twistfree>
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
                  .Append(m.ManualAxis ? '1' : '0').Append('|')
                  .Append(F(m.GripPosR.x)).Append(' ').Append(F(m.GripPosR.y)).Append(' ').Append(F(m.GripPosR.z)).Append('|')
                  .Append(F(m.GripEulR.x)).Append(' ').Append(F(m.GripEulR.y)).Append(' ').Append(F(m.GripEulR.z)).Append('|')
                  .Append(m.HasGripR ? '1' : '0').Append('|')
                  .Append(F(m.GripPosL.x)).Append(' ').Append(F(m.GripPosL.y)).Append(' ').Append(F(m.GripPosL.z)).Append('|')
                  .Append(F(m.GripEulL.x)).Append(' ').Append(F(m.GripEulL.y)).Append(' ').Append(F(m.GripEulL.z)).Append('|')
                  .Append(m.HasGripL ? '1' : '0').Append('|')
                  .Append(F(m.GripTwistAxis.x)).Append(' ').Append(F(m.GripTwistAxis.y)).Append(' ').Append(F(m.GripTwistAxis.z)).Append('|')
                  .Append(m.GripTwistFree ? '1' : '0').AppendLine();
            }
        }

        // Baked-in calibration (user-tuned, full pass 2026-06-25 incl. per-hand GRIP POSES + grip rotation/twist) for EVERY
        // lever/slider/switch, so a fresh install or a deleted cfg gives new players the correct motion direction AND a clean
        // hand placement on each handle out of the box — no wrong-way throws, no hand snapping to the raw laser hit. Uses the
        // same line format as the cfg and goes through Apply, so it's seeded into the Map BEFORE Config.Load reads the cfg —
        // meaning a saved cfg line (the player's own later re-tuning) still overrides these. Call once at the very start of
        // Config.Load. To refresh: re-tune in-headset, then re-export the SwitchMotion= lines from the cfg into here.
        public static void SeedDefaults()
        {
            Apply("Universal Switch Button Variant@Power Lever@Power Lever Parent@Power Box|0|1 0 0|90|1|-0.02621307 -0.020732565 0.9994414|1|1|-0.2198497 -0.39211044 -0.00879398|340.4174 211.0204 208.05525|1|-0.24888432 -0.37521118 -0.021867543|352.8874 122.3557 118.66279|1|1 0 0|1");
            Apply("Universal Switch Button@.Check Switch@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|0|0.8146251 -0.24210104 -0.52704173|1|1|-0.024781922 -0.0081007965 -0.026294656|317.29343 62.653572 308.74835|1|-0.020963319 0.014913367 -0.043750234|33.14523 33.15378 191.6948|1|0 0 0|0");
            Apply("Universal Switch Button Variant@.Check Switch.001@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|-0.6298143 0.68924737 -0.35815063|1|1|-0.02535753 -0.0049487203 -0.029226605|328.83173 64.03547 303.46097|1|-0.01789628 -0.0018973611 0.0022221124|59.856274 79.86199 250.42995|1|0 0 0|0");
            Apply("Universal Switch Button Variant@.Check Switch.002@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.7263866 -0.54266137 -0.4217597|1|1|-0.032544337 -0.00087311864 -0.0297697|318.23535 49.603836 329.48386|1|-0.0034543397 0.021061085 -0.014986247|75.07775 354.45187 149.81201|1|0 0 0|0");
            Apply("Universal Switch Button Variant@.Check Switch.003@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.647286 -0.6134182 -0.45248073|1|1|-0.017876878 -0.006946413 -0.021911109|316.4805 63.128227 314.90518|1|-0.01942366 0.01633713 -0.017357586|67.029076 52.600807 210.17143|1|0 0 0|0");
            Apply("Universal Switch Button Variant@.Check Switch.004@.Review Console Parent@.Trigger Console Floor|0|0 0 1|90|1|0.0014177166 -0.73192436 -0.6813844|1|1|-0.0265055 -0.015739337 -0.04290242|321.65228 52.59923 328.11258|1|-0.021806393 0.012600856 -0.017354362|79.19622 358.9365 155.86539|1|0 0 0|0");
            Apply("Universal Button Move Cylinder@--Reloading Console@Gun System Right|0|0 1 0|90|1|-0.007311628 0.19845298 -0.9800831|1|1|0.01499795 0.0070524216 -0.44251296|46.14608 251.7255 146.89786|1|-0.007365945 -0.030158043 -0.44313636|296.84998 279.57022 3.448493|1|0 1 0|1");
            Apply("Universal Button|1|0 0 1|0.3999999|1|0.22816 0.28576458 -0.9307425|1|1|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Calculate Universal Button|0|1 0 0|25|1|0.36451685 -0.7213279 0.5889088|1|1|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Button Move Cylinder|0|1 0 0|25|0|0.99867576 -0.051444698 -0.0003775128|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Button Load shell Rammer|1|1 0 0|0.3999999|0|-0.1628543 -0.37299356 0.91343|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Button Dispencer (1)|0|0 1 0|25|1|0.21541348 -0.035241306 0.9758869|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Button Dispencer (2)|0|0 1 0|25|1|-0.08155644 0.0811569 0.993359|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Button Dispencer (3)|0|0 1 0|25|1|-0.23370206 -0.017419327 0.9721522|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Button Dispencer (4)|0|0 1 0|25|1|-0.23717906 0.15076959 0.95969504|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Button Dispencer (5)|0|0 1 0|25|1|-0.08797458 0.4735802 0.876346|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Button Dispencer (6)|0|0 1 0|25|1|-0.26464856 0.5090775 0.81902456|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Button Charge Rammer (1)|0|0 1 0|25|0|0.061300773 0.15419963 0.9861363|1|1|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("universal button|0|1 0 0|25|0|0.038201254 0.023993324 -0.998982|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Switch Button|0|1 0 0|25|0|0.94266254 -0.23930416 -0.23263903|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Button Arm Left|0|1 0 0|25|0|-0.21369077 0.24350819 0.94606555|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Toogle Switch Button Variant|0|1 0 0|25|0|-0.6139409 0.6234944 -0.48407787|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Switch Button Variant|0|1 0 0|90|1|0.70741665 0.24042559 -0.66464823|1|1|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Button@Floor Hatch Barbet Stars|0|1 0 0|25|0|0.17930134 -0.7503666 -0.63623965|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Button@Trigger Console|0|1 0 0|25|0|-0.6581829 0.053846445 -0.7509299|1|0|0.021789055 0.014310598 -0.7728168|331.59546 230.28159 90.40052|1|0.003776525 -0.01330483 -0.760449|338.64685 231.89574 103.64488|1|0 0 1|1");
            Apply("Universal Button@Requisition Console|1|1 0 0|0.3999999|1|-0.22520816 -0.00069617695 -0.97431034|1|0|0.10972591 -0.0046087936 -0.021338582|357.78607 319.13074 67.15922|1|0.077507414 -0.004932857 0.01863189|4.3850193 63.555866 284.14044|1|1 0 0|1");
            Apply("Universal Switch Button@.Check Switch|0|1 0 0|25|0|0.3595588 0.09982921 -0.92776704|1|0|0 0 0|0 0 0|0|0 0 0|0 0 0|0|0 0 0|0");
            Apply("Universal Button@Floor Hatch Barbet Stars@Turret|0|1 0 0|25|0|-0.122811854 0.72200423 -0.68090177|1|0|-0.9371969 0.0023870468 0.07754707|315.36362 102.84949 93.97703|1|-0.95394033 -0.010399342 0.04580307|335.068 111.89266 166.51529|1|1 0 0|1");
            Apply("universal button@War Horn@War Horn Parent|0|1 0 0|25|0|-0.040010385 0.031871893 -0.9986908|1|0|-0.0146974055 0.05383937 0.0044677434|349.31583 27.181902 44.417133|1|0.024857225 -0.011974945 -0.0048184926|15.486364 16.057621 65.38879|1|0 0 1|1");
            Apply("Calculate Universal Button@Artillery Computer Console|0|1 0 0|90|1|-0.25103912 -0.514366 0.8200042|1|1|-0.020181527 -0.04545466 0.10649128|47.23226 43.78787 38.68429|1|-0.038487792 -0.057916462 0.12683102|5.1438174 199.15636 32.385128|1|0 0 1|1");
            Apply("Universal Button Move Cylinder@--Reloading Console@Gun System Left|0|0 1 0|90|1|0.95446575 -0.2863874 0.08353066|1|1|0.0056672106 0.013810098 -0.45347655|49.430115 187.20638 137.73051|1|-0.004907276 -0.009607434 -0.45037535|309.98218 236.08496 358.294|1|0 1 0|1");
            Apply("Universal Button Load shell Rammer@--Reloading Console@Gun System Left|1|1 0 0|0.3999999|0|0.51707006 -0.10338815 0.8496761|1|0|0.012222292 -0.016327873 -0.13492692|325.34485 155.01447 174.4664|1|0.018054008 0.005584702 -0.123033166|319.90393 180.07033 190.33562|1|0 0 1|1");
            Apply("Universal Button Load shell Rammer@--Reloading Console@Gun System Right|1|1 0 0|0.3999999|0|-0.43526945 -0.2860667 0.853643|1|0|0.013381958 -8.536875E-05 -0.12591946|26.856672 205.30928 4.7510257|1|0.014699936 -0.005179897 -0.10387921|35.213955 193.43326 17.397745|1|0 0 1|1");
            Apply("Button Dispencer (1)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.1546257 0.26916683 0.95059985|1|0|-0.0039683105 -0.0011307972 -0.5576324|325.73257 205.2447 94.21294|1|-0.008778166 -0.0025641667 -0.5493018|347.4296 221.08876 120.19822|1|0 0 1|1");
            Apply("Button Dispencer (2)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|0.66218823 -0.725908 0.18591516|1|0|0.0021218392 -0.0010643424 -0.56986284|331.61697 202.37868 75.327774|1|-0.011466119 0.0051936707 -0.5628055|1.2773443 216.54028 100.109886|1|0 0 1|1");
            Apply("Button Dispencer (3)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.06284219 -0.19044623 0.9796842|1|0|-0.004017466 0.01048996 -0.56497747|337.62292 211.63693 73.10612|1|-0.018944366 -0.0008672896 -0.5671625|4.4132876 213.53683 102.040436|1|0 0 1|1");
            Apply("Button Dispencer (4)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.16829199 -0.020881636 0.985516|1|0|0.0076058987 -0.001507324 -0.5706851|335.28882 212.21808 69.144264|1|-0.0147901885 -0.0018402137 -0.56110847|5.793513 215.17848 103.7129|1|0 0 1|1");
            Apply("Button Dispencer (5)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|0.03852901 -0.14287744 0.9889902|1|0|-0.000295793 -0.0078475885 -0.5679064|335.0844 210.57661 78.49422|1|-0.015693355 0.0048991656 -0.5582228|6.701637 217.80573 109.557655|1|0 0 1|1");
            Apply("Button Dispencer (6)@PowderChargeController@--Reloading Console@Gun System Left|0|0 1 0|25|1|-0.016494418 -0.05137145 0.9985433|1|0|0.0073665013 -0.006337201 -0.5540835|326.271 211.14038 76.004776|1|-0.014126456 -0.006677609 -0.5644506|4.07821 217.67334 106.53552|1|0 0 1|1");
            Apply("Button Dispencer (1)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.117551416 0.447876 -0.8863344|1|0|0.007102145 -0.0074443026 -0.566257|346.81824 214.67122 67.680954|1|-0.0105107855 0.0018683309 -0.5538828|12.045992 218.06537 95.722435|1|0 0 1|1");
            Apply("Button Dispencer (2)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.06347864 -0.24202031 -0.9681924|1|0|0.00724759 0.00021596691 -0.5667372|347.03937 213.55144 73.25894|1|-0.011654716 0.007041414 -0.5648445|4.97806 215.29373 99.16661|1|0 0 1|1");
            Apply("Button Dispencer (3)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.23030023 -0.07861772 -0.9699387|1|0|0.009351894 -0.0029622696 -0.5605129|344.90558 218.17723 68.60115|1|-0.012331806 0.0036381672 -0.5646381|7.348021 214.645 94.5945|1|0 0 1|1");
            Apply("Button Dispencer (4)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.30218944 -0.24596359 -0.9209687|1|0|0.013962921 0.00064463797 -0.5820762|343.64108 219.92305 68.991295|1|-0.009838867 0.00016590016 -0.56115866|358.43414 217.06226 91.70062|1|0 0 1|1");
            Apply("Button Dispencer (5)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|0.10293663 -0.32261202 -0.9409174|1|0|0.012976866 -0.00052533386 -0.561827|346.45245 215.39217 74.89371|1|-0.008263846 0.0042315936 -0.5588916|4.276232 216.7322 97.73222|1|0 0 1|1");
            Apply("Button Dispencer (6)@PowderChargeController@--Reloading Console@Gun System Right|0|0 1 0|25|1|-0.60922575 0.51687044 0.60140586|1|0|0.0043089245 -0.0016761797 -0.55889636|347.49692 218.32751 68.78404|1|-0.016987583 0.0046037636 -0.5616643|6.1669693 216.43262 92.97741|1|0 0 1|1");
            Apply("Universal Button Charge Rammer (1)@--Reloading Console@Gun System Right|0|0 1 0|25|0|0.32232627 0.47796977 0.8170989|1|1|-1.0533606 0.24663615 -0.61037785|37.837257 346.68527 151.76753|1|-1.0471716 0.27472818 -0.5876619|295.12637 12.58132 357.42612|1|0 1 0|1");
            Apply("Universal Button Charge Rammer (1)@--Reloading Console@Gun System Left|0|0 1 0|25|0|0.3520076 0.5276589 0.7730891|1|1|-1.0637035 0.32989216 -0.598968|55.73672 325.34454 128.4832|1|-1.046552 0.2952249 -0.60112953|321.23343 0.3569569 5.6612954|1|0 1 0|1");
            Apply("Universal Button Arm Left@.PowderRamLever.007@.ArmingLeverParent Left@.Trigger Core|0|1 0 0|25|0|-0.36389634 -0.032376274 0.9308766|1|0|-0.64116955 0.17277616 0.047865927|305.37012 120.92458 308.31653|1|-0.61839354 0.21940821 0.031712443|52.251553 102.544304 201.07303|1|0 1 0|1");
            Apply("Universal Toogle Switch Button Variant@.Brew Lever@.Brew Lever Parent@Espresso Machine|0|0 0 1|25|0|-0.61675954 -0.61643153 0.4895099|1|1|0.12344564 -0.0011969879 -0.011980149|28.486963 210.6578 6.2698045|1|-0.07764775 0.08531208 -0.03621772|30.391691 209.083 63.24635|1|0 0 1|1");
            Apply("Universal Button Arm Right@.PowderRamLever.008@.ArmingLeverParent Right@.Trigger Core|0|1 0 0|25|0|0.34066272 -0.68836695 -0.6403905|1|0|-0.6341852 -0.22663015 0.026502341|318.74384 114.82734 322.62543|1|-0.6196528 -0.18717414 0.03795764|61.533176 94.769455 191.36606|1|0 1 0|1");
            Apply("Universal Button@Floor Hatch@EngineControls|1|0 0 1|0.3999999|1|0 0 0|0|1|0.016248107 -0.013778844 -2.137434|27.275343 353.47366 261.85522|1|-0.0007443437 0.005666572 -2.1205387|355.55072 352.72403 283.04413|1|0 0 1|1");
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
                // Per-handle grip pose (optional; older saves / SeedDefaults lines omit these → HasGrip*=false).
                GripPosR = p.Length > 8 ? V(p[8], Vector3.zero) : Vector3.zero,
                GripEulR = p.Length > 9 ? V(p[9], Vector3.zero) : Vector3.zero,
                HasGripR = p.Length > 10 && p[10] == "1",
                GripPosL = p.Length > 11 ? V(p[11], Vector3.zero) : Vector3.zero,
                GripEulL = p.Length > 12 ? V(p[12], Vector3.zero) : Vector3.zero,
                HasGripL = p.Length > 13 && p[13] == "1",
                GripTwistAxis = p.Length > 14 ? V(p[14], Vector3.zero) : Vector3.zero,
                GripTwistFree = p.Length > 15 && p[15] == "1",
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
