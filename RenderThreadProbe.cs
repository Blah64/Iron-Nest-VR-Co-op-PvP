using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace IronNestVR
{
    /// <summary>
    /// One-shot feasibility probe for PLANXR open question #2 (the potential veto for the whole
    /// render-thread swapchain-copy plan): under BepInEx IL2CPP / .NET 6, can we register a native
    /// render-thread callback fired via <c>CommandBuffer.IssuePluginEvent</c> and have Unity actually call
    /// it without crashing — AND does it run on a SEPARATE thread when gfx-direct is off?
    ///
    /// Uses a <see cref="UnmanagedFunctionPointerAttribute"/> delegate + Marshal.GetFunctionPointerForDelegate
    /// (rather than [UnmanagedCallersOnly], which collides with IL2CPP's own copy of that attribute in
    /// UnityEngine.CoreModule). Same capability, broader compatibility.
    ///
    /// Run it in BOTH launch configs and compare the [rtprobe] RESULT line:
    ///   • WITH  -force-gfx-direct  (threadingMode=Direct)         → callback fires on the MAIN thread
    ///     (no render thread exists). Proves the mechanism works at all.
    ///   • WITHOUT -force-gfx-direct (threadingMode=LegacyJobified) → callback fires on a DIFFERENT OS
    ///     thread (the render thread). Proves we can run release/endframe there.
    ///
    /// Self-terminating: issues the event for ~120 frames, then logs one RESULT line and goes idle. Gated
    /// behind Config.RenderThreadProbeTest. Pure Unity (no OpenXR/VR needed — runs at the menu too).
    /// </summary>
    internal static class RenderThreadProbe
    {
        // Unity's UnityRenderingEvent is __stdcall (UNITY_INTERFACE_API) on Windows. On x64 stdcall/cdecl
        // collapse to one ABI so it can't actually mismatch, but we declare StdCall to match the contract.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RenderEvent(int eventId);

        private static volatile int _fireCount;
        private static volatile int _cbThreadId;
        private static int _mainThreadId;

        // Kept alive for the app lifetime: if this delegate were GC'd the native function pointer would
        // dangle and Unity's call from the render thread would crash.
        private static readonly RenderEvent _del = OnRenderEvent;

        private static CommandBuffer _cmd;
        private static bool _started;
        private static bool _done;
        private static int _frames;

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        // Runs on whatever thread Unity replays the plugin event on. MUST stay minimal — no managed
        // allocation, no IL2CPP calls — the render thread is a native thread the runtime attaches on entry.
        private static void OnRenderEvent(int eventId)
        {
            _cbThreadId = GetCurrentThreadId();
            _fireCount++;
        }

        public static void Tick()
        {
            if (_done || !Config.RenderThreadProbeTest) return;
            try
            {
                if (!_started)
                {
                    _started = true;
                    _mainThreadId = GetCurrentThreadId();
                    IntPtr cb = Marshal.GetFunctionPointerForDelegate(_del);
                    _cmd = new CommandBuffer { name = "IronNestVR_RTProbe" };
                    _cmd.IssuePluginEvent(cb, 0x4952); // 'IR'
                    Plugin.Logger.LogInfo($"[rtprobe] start: mainThreadId={_mainThreadId} cbPtr=0x{cb.ToInt64():X} " +
                                          $"threadingMode={SystemInfo.renderingThreadingMode}");
                }

                Graphics.ExecuteCommandBuffer(_cmd); // replays the recorded plugin event on the render thread

                if (++_frames >= 120)
                {
                    _done = true;
                    bool fired = _fireCount > 0;
                    bool sameThread = _cbThreadId == _mainThreadId;
                    string verdict = !fired
                        ? "CALLBACK NEVER FIRED — IssuePluginEvent path unavailable; render-thread plan needs a different mechanism"
                        : sameThread
                            ? "WORKS — callback ran on the MAIN thread (expected in Direct mode; re-run without -force-gfx-direct to see the render thread)"
                            : "WORKS — callback ran on a SEPARATE render thread (green light for render-thread copy/release/endframe)";
                    Plugin.Logger.LogInfo($"[rtprobe] RESULT: fired={fired} fireCount={_fireCount} " +
                                          $"cbThreadId={_cbThreadId} mainThreadId={_mainThreadId} sameThread={sameThread} " +
                                          $"mode={SystemInfo.renderingThreadingMode} -> {verdict}");
                    try { _cmd?.Dispose(); } catch { }
                    _cmd = null;
                }
            }
            catch (Exception e)
            {
                _done = true;
                Plugin.Logger.LogError("[rtprobe] EXCEPTION (mechanism unavailable as written): " + e);
            }
        }
    }
}
