using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace IronNestVR
{
    /// <summary>
    /// OS-level key injection via Win32 <c>keybd_event</c>. Unlike <see cref="InputSynth"/> (which feeds
    /// only Unity's new Input System), a synthesized OS keystroke is seen by BOTH the new Input System
    /// and the legacy <c>UnityEngine.Input</c> path — which is how this game reads its [E] interact and
    /// Esc. The plugin runs on CoreCLR, so P/Invoke to user32 works normally.
    /// </summary>
    internal static class Win32Input
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte VK_E = 0x45;
        public const byte VK_ESCAPE = 0x1B;

        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public static void SendKey(byte vk, bool down)
        {
            try
            {
                byte scan = (byte)MapVirtualKey(vk, 0 /*MAPVK_VK_TO_VSC*/);
                keybd_event(vk, scan, down ? 0u : KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception e) { Log.LogWarning("[win32] keybd_event failed: " + e.Message); }
        }
    }
}
