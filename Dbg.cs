using System;
using System.IO;

namespace IronNestVR
{
    /// <summary>
    /// Crash-proof step tracer: each call opens/writes/closes the file, so the line reaches the OS
    /// file cache immediately and survives a native access violation (unlike BepInEx's buffered log).
    /// Used only for diagnosing the render-path crash; cheap to leave compiled out via Enabled.
    /// </summary>
    internal static class Dbg
    {
        public static bool Enabled = true;
        private static readonly string Path = @"C:\Users\blah6\dev\IronNestVR\trace.log";

        public static void Step(string s)
        {
            if (!Enabled) return;
            try { File.AppendAllText(Path, $"{Environment.TickCount}  {s}\n"); } catch { }
        }

        public static void Reset()
        {
            if (!Enabled) return;
            try { File.WriteAllText(Path, $"--- trace start {Environment.TickCount} ---\n"); } catch { }
        }
    }
}
