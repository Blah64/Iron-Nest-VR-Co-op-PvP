using System;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// CPU-vs-GPU bound diagnostic for the VR frame loop — and the frame-time signal a future adaptive
    /// quality step would steer on.
    ///
    /// In <c>threadingMode=Direct</c> the entire frame runs on ONE thread, so where that thread spends its
    /// wall-clock IS the answer. We partition each frame's interval into FOUR non-overlapping buckets:
    ///   • <b>preLoopCpu</b> — our Update work BEFORE the VR loop: the co-op Tick()s, RemoteAvatar, scene
    ///     scan, etc. (measured top-of-Update → FrameBegin).
    ///   • <b>activeCpu</b>  — the VR loop's own work: PollEvents + input + sim + the immediate CopyResource.
    ///   • <b>xrWait</b>     — time BLOCKED inside xrWaitFrame / xrWaitSwapchainImage / xrEndFrame. When the
    ///     GPU/compositor is the wall, the thread parks here waiting for an image to free up.
    ///   • <b>render</b>     — frameInterval − Update = everything AFTER our Update returns: Unity's own
    ///     render phase (the scene GPU draw — mirror + 2 eyes), LateUpdate, present/vsync.
    /// They sum to the frame interval.
    ///
    /// Reading it:
    ///   preLoopCpu/activeCpu dominant → CPU-BOUND in OUR scripts (sim/submission; gfx-direct serializes it).
    ///   xrWait dominant               → GPU/compositor-BOUND (thread idle-waits on the GPU).
    ///   render dominant               → render-phase-bound (Unity scene render + present). If a RenderScale
    ///                                   sweep does NOT move fps, it's draw-call/geometry/camera-count or the
    ///                                   desktop mirror — NOT pixel fill — so dynamic-res won't help.
    ///
    /// Pure <see cref="Stopwatch"/> timing — no native GPU queries, no IL2CPP end-of-frame hooks. The VR
    /// loop never executes in flatscreen, so flatscreen parity is untouched regardless. Off = fully inert.
    /// </summary>
    internal static class PerfProbe
    {
        private static ManualLogSource Log => Plugin.Logger;

        public static bool Enabled => Config.PerfProbe;

        // All on the main thread (Direct mode), so plain statics are safe — no locking needed.
        private static readonly Stopwatch _update = new Stopwatch(); // whole Update body (started top-of-Update)
        private static readonly Stopwatch _loop = new Stopwatch();   // just the VR frame-loop body
        private static readonly Stopwatch _wait = new Stopwatch();   // accumulated blocking-wait time this frame

        private static int _frames;
        private static double _sumPre, _sumActive, _sumWait, _sumRender, _sumFrame;
        private static double _worstFrame;
        private static float _nextLog;

        /// <summary>Rolling-average frame interval (ms) over the last logged window. For adaptive quality.</summary>
        public static double AvgFrameMs { get; private set; }
        /// <summary>Rolling-average of all our CPU work (preLoop + active), ms. High vs frame ⇒ CPU-bound.</summary>
        public static double AvgCpuMs { get; private set; }
        /// <summary>Rolling-average of the GPU/compositor-attributable time (xrWait + render), ms.</summary>
        public static double AvgGpuBoundMs { get; private set; }

        // -------- Update-level bracket (top of VrManager.Update, every frame) --------

        public static void UpdateBegin()
        {
            if (!Enabled) return;
            _update.Restart();
        }

        // -------- VR-loop brackets --------

        public static void FrameBegin()
        {
            if (!Enabled) return;
            if (_nextLog == 0f) _nextLog = Time.unscaledTime + Config.PerfProbeIntervalSec;
            _wait.Reset();
            _loop.Restart();
        }

        public static void FrameEnd(float frameSec)
        {
            if (!Enabled) return;
            if (!_loop.IsRunning) return; // an early-returning frame skipped FrameEnd; next FrameBegin restarts
            _loop.Stop();

            double updateMs = _update.IsRunning ? _update.Elapsed.TotalMilliseconds : _loop.Elapsed.TotalMilliseconds;
            double loopMs = _loop.Elapsed.TotalMilliseconds;
            double waitMs = _wait.Elapsed.TotalMilliseconds;
            double frameMs = frameSec * 1000.0;

            double active = Math.Max(loopMs - waitMs, 0.0);
            double preLoop = Math.Max(updateMs - loopMs, 0.0);   // Update work before the VR loop (coop ticks etc.)
            double render = Math.Max(frameMs - updateMs, 0.0);   // after Update returns: Unity render phase + present

            _frames++;
            _sumPre += preLoop; _sumActive += active; _sumWait += waitMs; _sumRender += render; _sumFrame += frameMs;
            if (frameMs > _worstFrame) _worstFrame = frameMs;

            if (Time.unscaledTime >= _nextLog) Flush();
        }

        // -------- wait brackets (XrSession blocking calls) --------
        // Start/Stop accumulate into _wait (reset each FrameBegin). Calls are sequential, never nested, so
        // Stopwatch's "already running / not running" no-ops keep this safe even if Enabled flips mid-frame.

        public static void WaitStart() { if (Enabled) _wait.Start(); }
        public static void WaitStop() { if (Enabled) _wait.Stop(); }

        private static void Flush()
        {
            float span = Config.PerfProbeIntervalSec;
            int n = Math.Max(_frames, 1);
            double fps = _frames / Math.Max(span, 0.001f);
            double pre = _sumPre / n;
            double active = _sumActive / n;
            double wait = _sumWait / n;
            double render = _sumRender / n;
            double frame = _sumFrame / n;

            AvgFrameMs = frame;
            AvgCpuMs = pre + active;
            AvgGpuBoundMs = wait + render;

            string verdict;
            if (frame < 0.5)
                verdict = "?";
            else if (wait >= 0.40 * frame)
                verdict = "GPU/compositor-BOUND (thread parked in xr waits)";
            else if ((pre + active) >= 0.50 * frame)
                verdict = "CPU-BOUND in our scripts (sim/submission)";
            else if (render >= 0.50 * frame)
                verdict = "render-phase-bound (Unity scene render + present) — if RenderScale doesn't move fps, it's draws/geometry/mirror not fill";
            else
                verdict = "mixed";

            Log.LogInfo($"[probe] fps~{fps:F1} frame={frame:F1}ms = preLoopCpu {pre:F1} + activeCpu {active:F1} + xrWait {wait:F1} + render {render:F1}  " +
                        $"(worst frame {_worstFrame:F1})  ->  {verdict}");

            _frames = 0;
            _sumPre = _sumActive = _sumWait = _sumRender = _sumFrame = 0.0;
            _worstFrame = 0.0;
            _nextLog = Time.unscaledTime + span;
        }
    }
}
