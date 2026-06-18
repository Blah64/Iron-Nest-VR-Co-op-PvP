using System;
using BepInEx.Logging;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace IronNestVR
{
    /// <summary>
    /// Feeds synthetic events into Unity's new Input System so the game's ENTIRE input pipeline sees
    /// real input: <c>primaryClickActions</c> (button/dial clicks), the cursor manager's separate
    /// grab/drag path (draggable clipboards/items), and the keyboard interact ("[E]") action.
    ///
    /// We use full-state events (<c>QueueStateEvent&lt;MouseState&gt;/&lt;KeyboardState&gt;</c>) rather than
    /// per-control delta events: those two instantiations are guaranteed to exist in the il2cpp build
    /// (the Mouse/Keyboard devices queue them every frame), whereas a value-type generic the game never
    /// compiled would throw under Il2CppInterop. The mouse button is re-queued every held frame so a
    /// drag persists; the real desktop mouse is untouched unless we're actively pressing.
    /// </summary>
    internal static class InputSynth
    {
        private static ManualLogSource Log => Plugin.Logger;

        private static bool _probed;
        private static bool _ok;

        public static bool Supported
        {
            get { Probe(); return _ok; }
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var m = Mouse.current;
                if (m == null) { _ok = false; return; }
                // No-op state event (button up) to confirm the generic call path works on this build.
                var ms = new MouseState { position = m.position.ReadValue() };
                InputSystem.QueueStateEvent(m, ms);
                _ok = true;
                Log.LogInfo("[synth] input synthesis available (mouse/keyboard).");
            }
            catch (Exception e)
            {
                _ok = false;
                Log.LogWarning("[synth] input synthesis unavailable, falling back to click events: " + e.Message);
            }
        }

        /// <summary>Queue a full mouse state with the left button held/released. Re-call every held frame.</summary>
        public static bool SetMouseLeft(bool down)
        {
            if (!Supported) return false;
            try
            {
                var m = Mouse.current;
                if (m == null) return false;
                var ms = new MouseState { position = m.position.ReadValue() };
                ms = ms.WithButton(MouseButton.Left, down);
                InputSystem.QueueStateEvent(m, ms);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Queue a keyboard state with a single key held/released (call on edges only).</summary>
        public static bool SetKey(Key key, bool down)
        {
            if (!Supported) return false;
            try
            {
                var k = Keyboard.current;
                if (k == null) return false;
                var ks = new KeyboardState();
                ks.Set(key, down);
                InputSystem.QueueStateEvent(k, ks);
                return true;
            }
            catch { return false; }
        }
    }
}
