using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// On-demand tactical-map tool palette (pencil colours + compass) WITHOUT the focus-camera move.
    /// Pressing E on the map natively fires three bundled systems at once (CinemachineFocusTarget camera move +
    /// ClipboardToolSelector palette + ClipboardStateController raise); this reproduces only the palette +
    /// clipboard-raise half and leaves the focus camera / RTSMapCameraController untouched — so in VR you can pop
    /// the pencils/compass over the physical map you're already leaning over, no disorienting camera teleport.
    /// MapToolsProbe (F1) dumped the structure that proved this split; Shift+F1 calls the same Toggle().
    ///
    /// Trigger (VR): the <b>A</b> button on the RIGHT controller while the RIGHT hand holds the HUD clipboard, or
    /// the <b>X</b> button on the LEFT controller while the LEFT hand holds it. Gating on which hand holds the
    /// board both scopes the gesture and resolves the right-A vs [E]-interact overlap — CockpitInteractor
    /// suppresses its synthesized A->E while the right hand holds the clipboard.
    ///
    /// Mechanism = the proven Shift+F1 path: SetActive(true) on the ClipboardToolSelector GameObject (its OnEnable
    /// auto-selects a tool and shows the slots) + a raised+focused override on the ClipboardStateController
    /// (token-cancelled on hide). Refs are re-acquired if a scene change invalidated them.
    /// </summary>
    internal static class MapTools
    {
        private static ManualLogSource Log => Plugin.Logger;

        private static bool _shown;
        private static uint _tok;
        private static ClipboardToolSelector _sel;
        private static ClipboardStateController _ctrl;
        private static bool _prevR, _prevL;

        public static bool Shown => _shown;

        // Called each active VR frame AFTER GrabManager.Tick (so the hold state is current) and only while no
        // menu/popup owns input. Edge-detects the two triggers, gated by which hand holds the HUD clipboard.
        public static void Tick(VrInput input, GrabManager grab)
        {
            if (!Config.MapToolsOnDemand || input == null || grab == null) return;
            try
            {
                int hold = grab.HudClipboardHoldHand;   // 0 none, 1 left, 2 right
                bool r = input.InteractHeld;            // right A (also [E]; suppressed while right-holding)
                bool l = input.MapToolsLHeld;           // left X

                if (r && !_prevR && hold == 2) { Toggle(); Buzz(input); }
                else if (l && !_prevL && hold == 1) { Toggle(); Buzz(input); }

                _prevR = r; _prevL = l;
            }
            catch (Exception e) { Log.LogWarning("[maptools] tick: " + e.Message); }
        }

        // Shared by the VR triggers and MapToolsProbe's Shift+F1. Reversible; never touches the focus camera.
        public static void Toggle()
        {
            EnsureRefs();
            if (_sel == null) { Log.LogWarning("[maptools] toggle: no ClipboardToolSelector in scene"); return; }

            if (!_shown)
            {
                try { _sel.gameObject.SetActive(true); } catch (Exception e) { Log.LogWarning("[maptools] show: " + e.Message); }
                if (_ctrl != null) { try { _tok = _ctrl.PushOverrideState(true, true, false, 3600f); } catch (Exception e) { Log.LogWarning("[maptools] raise: " + e.Message); } }
                _shown = true;
                Log.LogInfo("[maptools] palette ON (clipboard raised+focused, camera NOT moved)");
            }
            else
            {
                if (_ctrl != null) { try { _ctrl.CancelOverride(_tok); } catch (Exception e) { Log.LogWarning("[maptools] cancel: " + e.Message); } }
                try { if (_sel != null) _sel.gameObject.SetActive(false); } catch (Exception e) { Log.LogWarning("[maptools] hide: " + e.Message); }
                _shown = false;
                Log.LogInfo("[maptools] palette OFF");
            }
        }

        private static void Buzz(VrInput input)
        { try { input.Haptic(Config.HapticAmplitude, Config.HapticSeconds); } catch { } }

        private static void EnsureRefs()
        {
            if (_sel == null) _sel = FirstSceneOf<ClipboardToolSelector>();
            if (_ctrl == null) _ctrl = FirstSceneOf<ClipboardStateController>();
        }

        // Resources.FindObjectsOfTypeAll catches the INACTIVE palette (FindObjectsByType would miss it), but it
        // also returns prefab assets — prefer a real scene instance, fall back to the first match otherwise.
        private static T FirstSceneOf<T>() where T : Component
        {
            T fallback = null;
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var c = arr[i].TryCast<T>();
                        if (c == null) continue;
                        if (fallback == null) fallback = c;
                        try { if (c.gameObject.scene.IsValid()) return c; } catch { }
                    }
            }
            catch (Exception e) { Log.LogWarning($"[maptools] find {typeof(T).Name}: {e.Message}"); }
            return fallback;
        }
    }
}
