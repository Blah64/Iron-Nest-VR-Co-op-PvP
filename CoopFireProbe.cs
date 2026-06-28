#if !PUBLIC_BUILD
using System;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// DEV-ONLY fire-state probe: read/log-only hooks on GunController.RequestFire / GunController.FireShell /
    /// ShellVisual.Initialize. Answers four questions about the fire state machine before a per-side intent queue
    /// is built (PLAN-host §6.0). Completely absent from the public build (#if !PUBLIC_BUILD). Zero overhead unless
    /// Config.FireProbe = true in IronNestVR.cfg (the flag gates both installation AND every hook body).
    ///
    /// Q1 — 1:1 ordering: every RequestFire that proceeds yields exactly one FireShell then one ShellVisual.Initialize.
    /// Q2 — synchronous no-op detection: when !CanFire / IsReloading / pendingReload, does RequestFire no-op?
    /// Q3 — reload spacing: FireShell inter-shot spacing vs fireDelay (two same-gun in-flight shots from one source).
    /// Q4 — call source: is the MSG_CLICK-replay lane actually reaching RequestFire?
    ///
    /// CoopControls.ReplayClick sets InClickReplay=true on entry / false in finally.
    /// Plugin.cs calls ApplyPatches() under #if !PUBLIC_BUILD.
    /// </summary>
    internal static class CoopFireProbe
    {
        // Integration surface — CoopControls.ReplayClick sets this.
        public static bool InClickReplay;

        private static ManualLogSource Log => Plugin.Logger;
        private static Harmony _harmony;

        // Q1: per-side sequence counters (index 0=Left, 1=Right).
        private static readonly int[] _reqSeq  = new int[2];
        private static readonly int[] _fireSeq = new int[2];
        private static int _visSeqTotal;

        // Q2: per-side "did FireShell fire during THIS RequestFire call?".
        private static readonly bool[] _fireSeenDuringReq = new bool[2];

        // Q3: per-side time of previous FireShell (unscaledTime). Init to -1 = not yet seen.
        private static readonly float[] _lastFireT = new float[] { -1f, -1f };

        // ============================= ApplyPatches =============================

        public static void ApplyPatches()
        {
            if (!Config.FireProbe) return;   // when off, install NOTHING — zero overhead

            try { _harmony = new Harmony("com.ironnest.vr.fireprobe"); }
            catch (Exception e) { Log.LogError("[fireprobe] Harmony init failed: " + e); return; }

            // GunController.RequestFire — prefix + postfix
            try
            {
                var mi = AccessTools.Method(typeof(GunController), "RequestFire");
                if (mi != null)
                {
                    _harmony.Patch(mi,
                        prefix:  new HarmonyMethod(typeof(CoopFireProbe), nameof(RequestFirePre)),
                        postfix: new HarmonyMethod(typeof(CoopFireProbe), nameof(RequestFirePost)));
                    Log.LogInfo("[fireprobe] GunController.RequestFire patched (prefix+postfix)");
                }
                else Log.LogWarning("[fireprobe] GunController.RequestFire not found — Q1/Q2/Q4 hooks NOT installed");
            }
            catch (Exception e) { Log.LogWarning("[fireprobe] RequestFire patch: " + e.Message); }

            // GunController.FireShell — prefix
            try
            {
                var mi = AccessTools.Method(typeof(GunController), "FireShell");
                if (mi != null)
                {
                    _harmony.Patch(mi, prefix: new HarmonyMethod(typeof(CoopFireProbe), nameof(FireShellPre)));
                    Log.LogInfo("[fireprobe] GunController.FireShell patched (prefix)");
                }
                else Log.LogWarning("[fireprobe] GunController.FireShell not found — Q1/Q3 hooks NOT installed");
            }
            catch (Exception e) { Log.LogWarning("[fireprobe] FireShell patch: " + e.Message); }

            // ShellVisual.Initialize — postfix
            try
            {
                var mi = AccessTools.Method(typeof(ShellVisual), "Initialize");
                if (mi != null)
                {
                    _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(CoopFireProbe), nameof(ShellVisualPost)));
                    Log.LogInfo("[fireprobe] ShellVisual.Initialize patched (postfix)");
                }
                else Log.LogWarning("[fireprobe] ShellVisual.Initialize not found — Q1 visual-seq hook NOT installed");
            }
            catch (Exception e) { Log.LogWarning("[fireprobe] ShellVisual patch: " + e.Message); }

            Log.LogInfo("[fireprobe] armed — hooking RequestFire/FireShell/ShellVisual.Initialize (answers Q1-Q4)");
        }

        // ============================= Hooks =============================

        // PREFIX on GunController.RequestFire.
        // Q1: bump _reqSeq[side].
        // Q2: snapshot gun state fields; arm _fireSeenDuringReq[side]=false.
        // Q4: classify call source.
        // Returns void — observe-only, never skips the original.
        public static void RequestFirePre(GunController __instance)
        {
            if (!Config.FireProbe) return;
            try
            {
                int side = SideOf(__instance);
                float t = Time.unscaledTime;

                // Q1
                int req = ++_reqSeq[side];

                // Q2 — arm per-side flag
                _fireSeenDuringReq[side] = false;

                // Q4 — call source
                string src;
                try
                {
                    if (CoopBallistics.IsApplyingRemoteImpact) src = "MSG_FIRE-replay";
                    else if (InClickReplay)                     src = "MSG_CLICK-replay(fire-button)";
                    else                                        src = "local-trigger";
                }
                catch { src = "n/a"; }

                // Q2 — snapshot gun state (each field in its own try/catch)
                string canFire     = "n/a"; try { canFire     = __instance.CanFire.ToString();     } catch { }
                string isReloading = "n/a"; try { isReloading = __instance.IsReloading.ToString(); } catch { }
                string pendRel     = "n/a"; try { pendRel     = __instance.pendingReload.ToString();} catch { }
                string hasFiredPre = "n/a"; try { hasFiredPre = __instance.hasFired.ToString();    } catch { }
                string fdStr       = "n/a"; try { fdStr       = __instance.fireDelay.ToString("0.000"); } catch { }

                string nm = "?"; try { if (__instance != null) nm = __instance.name ?? "?"; } catch { }

                Log.LogInfo($"[fireprobe] RequestFire.PRE t={t:0.000} gun={nm} side={(side == 0 ? "L" : "R")} reqSeq={req} " +
                            $"src={src} CanFire={canFire} IsReloading={isReloading} pendingReload={pendRel} hasFired={hasFiredPre} fireDelay={fdStr}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] RequestFirePre: " + e.Message); } catch { }
            }
        }

        // POSTFIX on GunController.RequestFire.
        // Q2: read hasFired again; log flip + whether FireShell was seen during the call.
        // Returns void — observe-only.
        public static void RequestFirePost(GunController __instance)
        {
            if (!Config.FireProbe) return;
            try
            {
                int side = SideOf(__instance);
                float t = Time.unscaledTime;

                string hasFiredPost = "n/a"; try { hasFiredPost = __instance.hasFired.ToString(); } catch { }
                bool fireSeen = _fireSeenDuringReq[side];
                string nm = "?"; try { if (__instance != null) nm = __instance.name ?? "?"; } catch { }

                Log.LogInfo($"[fireprobe] RequestFire.POST t={t:0.000} gun={nm} side={(side == 0 ? "L" : "R")} " +
                            $"hasFired={hasFiredPost} fireShellDuringCall={fireSeen}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] RequestFirePost: " + e.Message); } catch { }
            }
        }

        // PREFIX on GunController.FireShell.
        // Q1: bump _fireSeq[side]; log reqSeq vs fireSeq so a reader can confirm 1:1 tracking.
        // Q2: set _fireSeenDuringReq[side]=true.
        // Q3: log spacing since last FireShell on this side vs fireDelay.
        // Returns void — observe-only, never skips the original.
        public static void FireShellPre(GunController __instance)
        {
            if (!Config.FireProbe) return;
            try
            {
                int side = SideOf(__instance);
                float t = Time.unscaledTime;

                // Q1
                int fire = ++_fireSeq[side];
                int req  = _reqSeq[side];

                // Q2
                _fireSeenDuringReq[side] = true;

                // Q3
                float last = _lastFireT[side];
                string spacing = last < 0f ? "first" : (t - last).ToString("0.000");
                _lastFireT[side] = t;

                string fdStr = "n/a"; try { fdStr = __instance.fireDelay.ToString("0.000"); } catch { }
                string nm = "?"; try { if (__instance != null) nm = __instance.name ?? "?"; } catch { }

                Log.LogInfo($"[fireprobe] FireShell.PRE t={t:0.000} gun={nm} side={(side == 0 ? "L" : "R")} " +
                            $"reqSeq={req} fireSeq={fire} spacingSinceLastShot={spacing} fireDelay={fdStr}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] FireShellPre: " + e.Message); } catch { }
            }
        }

        // POSTFIX on ShellVisual.Initialize.
        // Q1: bump global visual counter and log. No gun ref available; correlation to a side is best-effort/none.
        // Returns void — observe-only.
        public static void ShellVisualPost()
        {
            if (!Config.FireProbe) return;
            try
            {
                int vis = ++_visSeqTotal;
                float t = Time.unscaledTime;
                Log.LogInfo($"[fireprobe] ShellVisual.Initialize.POST t={t:0.000} visSeqTotal={vis}");
            }
            catch (Exception e)
            {
                try { Log.LogWarning("[fireprobe] ShellVisualPost: " + e.Message); } catch { }
            }
        }

        // ============================= Helpers =============================

        // Map a GunController to side index (0=Left, 1=Right). Defaults to 0 on any failure or ambiguity.
        // Mirrors the name-based disambiguation used by CoopControls.ScanGuns.
        private static int SideOf(GunController g)
        {
            try
            {
                if (g == null) return 0;
                string nm = g.name;
                if (nm == null) return 0;
                string lo = nm.ToLowerInvariant();
                if (lo.Contains("right")) return 1;
                // "left" or anything else -> 0
                return 0;
            }
            catch { return 0; }
        }
    }
}
#endif
