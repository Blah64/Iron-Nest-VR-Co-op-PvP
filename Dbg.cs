using System;
using System.IO;
using System.Text;

namespace IronNestVR
{
    /// <summary>
    /// Crash-proof step tracer. Each write opens/writes/closes immediately, so the line reaches the OS
    /// file cache and survives a native access violation (unlike BepInEx's buffered log).
    ///
    /// Two channels, both written NEXT TO the BepInEx log (so a tester can just send the whole BepInEx
    /// folder):
    ///   • <see cref="Step"/>  — appended detail trace, gated by callers to the first frames; used to
    ///     pinpoint a BRING-UP crash (instance/session/swapchain/first render).
    ///   • <see cref="Beat"/>  — an always-on rolling ring of the last N breadcrumbs, rewritten every
    ///     call; used to pinpoint a DELAYED crash (minutes into a mission, long past the Step window).
    ///     After a crash, the heartbeat file holds the last steps the frame loop was executing.
    ///
    /// The output directory is resolved at load time from BepInEx; earlier builds hard-coded a dev path
    /// (C:\Users\blah6\dev\...), which silently produced nothing on tester machines.
    /// </summary>
    internal static class Dbg
    {
        public static bool Enabled = true;

        private static readonly string TracePath;
        private static readonly string BeatPath;
        private static readonly bool _ok;

        private const int RingSize = 48;
        private static readonly string[] _ring = new string[RingSize];
        private static int _ringCount;
        private static long _seq;
        private static readonly object _lock = new object();
        private static readonly StringBuilder _sb = new StringBuilder(4096);

        /// <summary>The most recent breadcrumb, also kept in memory for the startup env dump / logs.</summary>
        public static string Last { get; private set; } = "(none)";

        static Dbg()
        {
            try
            {
                string dir = ResolveDir();
                TracePath = Path.Combine(dir, "IronNestVR-trace.log");
                BeatPath = Path.Combine(dir, "IronNestVR-heartbeat.log");
                _ok = true;
            }
            catch { _ok = false; }
        }

        public static string Directory => _ok ? Path.GetDirectoryName(TracePath) : "(unavailable)";

        // Prefer the BepInEx folder (next to LogOutput.log); fall back to the game root, then Temp.
        private static string ResolveDir()
        {
            try { var p = BepInEx.Paths.BepInExRootPath; if (!string.IsNullOrEmpty(p)) return p; } catch { }
            try { var p = AppContext.BaseDirectory; if (!string.IsNullOrEmpty(p)) return p; } catch { }
            return Path.GetTempPath();
        }

        public static void Step(string s)
        {
            if (!Enabled || !_ok) return;
            try { File.AppendAllText(TracePath, $"{Environment.TickCount}  {s}\n"); } catch { }
        }

        /// <summary>
        /// Record a breadcrumb into the rolling ring and flush the whole ring to the heartbeat file.
        /// Cheap and crash-proof: call it at the frame-loop phase boundaries EVERY frame (ungated) so a
        /// crash minutes in still leaves the last <see cref="RingSize"/> steps on disk.
        /// </summary>
        public static void Beat(string s)
        {
            if (!Enabled || !_ok) return;
            try
            {
                lock (_lock)
                {
                    long n = _seq++;
                    Last = s;
                    _ring[(int)(n % RingSize)] = $"{Environment.TickCount}  #{n}  {s}";
                    _ringCount++;

                    _sb.Clear();
                    _sb.Append("--- IronNestVR heartbeat (most recent last) — last write tick ")
                       .Append(Environment.TickCount).Append(" ---\n");
                    int have = Math.Min(_ringCount, RingSize);
                    long start = n - have + 1;
                    for (long i = start; i <= n; i++)
                    {
                        var line = _ring[(int)(i % RingSize)];
                        if (line != null) _sb.Append(line).Append('\n');
                    }
                    File.WriteAllText(BeatPath, _sb.ToString());
                }
            }
            catch { }
        }

        public static void Reset()
        {
            if (!Enabled || !_ok) return;
            try
            {
                File.WriteAllText(TracePath, $"--- trace start {DateTime.Now:yyyy-MM-dd HH:mm:ss} (tick {Environment.TickCount}) ---\n");
            }
            catch { }
        }
    }
}
