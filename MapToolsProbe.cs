using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// One-shot structural probe for decoupling the map-tools palette (pencil colours + compass) from the
    /// map FOCUS camera move. Pressing E on the tactical map fires THREE independent systems at once:
    ///   (1) camera-above-map  = FocusSystem.CinemachineFocusTarget -> CinemachineFocusService.RequestFocus
    ///   (2) the tool palette   = ClipboardToolSelector (appears on its GameObject being ENABLED)
    ///   (3) clipboard raised   = ClipboardStateController (Raise/Focus, via an override stack / ClipboardStateRelay)
    /// The goal is a button that does (2)+(3) WITHOUT (1). This probe answers the open questions before we build it:
    ///   • which exact GameObject toggles the palette (the selector itself, or a parent that also carries a
    ///     ClipboardStateRelay that would raise the clipboard for free on SetActive(true));
    ///   • what the map focus target's onFocusGrabbed/onFocusReleased UnityEvents are actually wired to
    ///     (the inspector-serialized persistent listeners reveal the real bundling);
    ///   • the current clipboard state + the tool slots (pencil colours + compass) present.
    ///
    /// Keys (game window focused):
    ///   F1        -> read-only structural dump (no behaviour change).
    ///   Shift+F1  -> LIVE decouple test: enable the first ClipboardToolSelector's GameObject + push a
    ///                raised+focused clipboard override (camera left untouched). Press again to reverse.
    ///                Fully reversible; this is exactly the mechanism the real feature will use.
    ///
    /// Enumerates with Resources.FindObjectsOfTypeAll (NOT FindObjectsByType) because the palette is INACTIVE
    /// when the map isn't focused, so an active-only search would miss it. Tick() is called from VrManager.Update.
    /// </summary>
    internal static class MapToolsProbe
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Live-toggle state (Shift+F1).
        private static bool _shown;
        private static uint _tok;
        private static ClipboardToolSelector _sel;
        private static ClipboardStateController _ctrl;

        public static void Tick()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return;
                if (!kb[UnityEngine.InputSystem.Key.F1].wasPressedThisFrame) return;

                bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                if (shift) ToggleLive();
                else Probe();
            }
            catch (Exception e) { Log.LogWarning("[maptools] tick: " + e.Message); }
        }

        // ------------------------------------------------------------------ read-only structural dump (F1)
        private static void Probe()
        {
            Log.LogInfo("[maptools] ===== MAP TOOLS PROBE (F1) =====");

            // (2) The tool palette. The slots ARE the pencil colours + compass; the GameObject's active state is
            // what the game toggles to show/hide it. Walk the parents so we see which ancestor is the real unit.
            var selectors = AllOf<ClipboardToolSelector>();
            Log.LogInfo($"[maptools] --- ClipboardToolSelector ({selectors.Count} incl inactive) ---");
            for (int i = 0; i < selectors.Count; i++)
            {
                var s = selectors[i];
                try
                {
                    var go = s.gameObject;
                    Log.LogInfo($"[maptools]  #{i} path='{PathOf(s.transform)}' activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} enabled={SafeEnabled(s)} scene='{SceneOf(go)}'");
                    DumpSlots(s);
                    string sel = "<none>"; try { if (s.CurrentSelected != null) sel = s.CurrentSelected.gameObject.name; } catch { }
                    string placer = "<none>"; try { if (s.mapMarkerPlacer != null) placer = s.mapMarkerPlacer.gameObject.name; } catch { }
                    string curOv = "<none>"; try { if (s.mapCursorOverride != null) curOv = s.mapCursorOverride.ToString(); } catch { }
                    string aSel = "?"; int aIdx = -1;
                    try { aSel = s.autoSelectOnFirstEnable.ToString(); } catch { }
                    try { aIdx = s.autoSelectIndex; } catch { }
                    Log.LogInfo($"[maptools]     currentSelected='{sel}' autoSelectOnFirstEnable={aSel} autoSelectIndex={aIdx} mapMarkerPlacer='{placer}' mapCursorOverride='{curOv}'");
                    DumpAncestors(s.transform);
                }
                catch (Exception e) { Log.LogWarning($"[maptools]  #{i} selector: {e.Message}"); }
            }

            // (3) The clipboard state machine + any relays (a relay with beginOverrideOnEnable would raise the
            // clipboard automatically when the palette GO is enabled — that would collapse the feature to one toggle).
            var relays = AllOf<ClipboardStateRelay>();
            Log.LogInfo($"[maptools] --- ClipboardStateRelay ({relays.Count} incl inactive) ---");
            for (int i = 0; i < relays.Count; i++)
            {
                var r = relays[i];
                try
                {
                    string tag = "?"; try { tag = r.controllerTag; } catch { }
                    bool tr = false, tf = false, th = false, boe = false, eod = false;
                    try { tr = r.targetRaised; } catch { } try { tf = r.targetFocused; } catch { } try { th = r.targetHidden; } catch { }
                    try { boe = r.beginOverrideOnEnable; } catch { } try { eod = r.endOverrideOnDisable; } catch { }
                    string mode = "?", style = "?";
                    try { mode = r.overrideMode.ToString(); } catch { } try { style = r.overrideStyle.ToString(); } catch { }
                    string ctrl = "<none>"; try { var c = r.GetController(); if (c != null) ctrl = c.gameObject.name; } catch { }
                    Log.LogInfo($"[maptools]  #{i} path='{PathOf(r.transform)}' active={r.gameObject.activeInHierarchy} controllerTag='{tag}' target(raised={tr},focused={tf},hidden={th}) beginOnEnable={boe} endOnDisable={eod} mode={mode} style={style} ctrl='{ctrl}'");
                }
                catch (Exception e) { Log.LogWarning($"[maptools]  relay #{i}: {e.Message}"); }
            }

            var ctrls = AllOf<ClipboardStateController>();
            Log.LogInfo($"[maptools] --- ClipboardStateController ({ctrls.Count} incl inactive) ---");
            for (int i = 0; i < ctrls.Count; i++)
            {
                var c = ctrls[i];
                try
                {
                    bool ra = false, fo = false, hi = false;
                    try { ra = c.IsRaised; } catch { } try { fo = c.IsFocused; } catch { } try { hi = c.IsHidden; } catch { }
                    Log.LogInfo($"[maptools]  #{i} path='{PathOf(c.transform)}' active={c.gameObject.activeInHierarchy} IsRaised={ra} IsFocused={fo} IsHidden={hi}");
                }
                catch (Exception e) { Log.LogWarning($"[maptools]  ctrl #{i}: {e.Message}"); }
            }

            // (1) The map focus target(s). The persistent listeners on onFocusGrabbed / onFocusReleased are the
            // gold: they show EXACTLY which objects/methods the map focus enables (palette, relay, etc.).
            var targets = AllOf<FocusSystem.CinemachineFocusTarget>();
            Log.LogInfo($"[maptools] --- CinemachineFocusTarget ({targets.Count} incl inactive) ---");
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                try
                {
                    string key = "?"; bool tog = false; int prio = -1;
                    try { key = t.key; } catch { } try { tog = t.ToggleOn; } catch { } try { prio = t.priority; } catch { }
                    Log.LogInfo($"[maptools]  #{i} path='{PathOf(t.transform)}' key='{key}' toggleOn={tog} priority={prio}");
                    DumpUnityEvent("onFocusGrabbed", t.onFocusGrabbed);
                    DumpUnityEvent("onFocusReleased", t.onFocusReleased);
                }
                catch (Exception e) { Log.LogWarning($"[maptools]  target #{i}: {e.Message}"); }
            }

            // Sanity: the camera systems we must NOT touch.
            var cams = AllOf<RTSMapCameraController>();
            Log.LogInfo($"[maptools] --- RTSMapCameraController ({cams.Count}) ---");
            for (int i = 0; i < cams.Count; i++)
            {
                try { Log.LogInfo($"[maptools]  #{i} path='{PathOf(cams[i].transform)}' active={cams[i].gameObject.activeInHierarchy} isActive={SafeIsActive(cams[i])}"); }
                catch (Exception e) { Log.LogWarning($"[maptools]  cam #{i}: {e.Message}"); }
            }
            try { Log.LogInfo($"[maptools] CinemachineFocusService.HasInstance={FocusSystem.CinemachineFocusService.HasInstance}"); } catch { }

            Log.LogInfo("[maptools] ===== end probe (now press Shift+F1 to live-test palette WITHOUT the camera move) =====");
        }

        // ------------------------------------------------------------------ live decouple test (Shift+F1)
        private static void ToggleLive()
        {
            EnsureRefs();
            if (_sel == null) { Log.LogWarning("[maptools] live: no ClipboardToolSelector in memory — run F1 in the map scene first"); return; }

            if (!_shown)
            {
                // Enable the selector's own GameObject. NOTE: if the probe shows the palette is toggled by a PARENT
                // (inactive ancestor), enabling this child alone won't make it activeInHierarchy — switch this to the
                // ancestor the F1 dump identifies. Big duration + cancel-by-token => persists until we reverse it.
                try { _sel.gameObject.SetActive(true); } catch (Exception e) { Log.LogWarning("[maptools] live show: " + e.Message); }
                if (_ctrl != null) { try { _tok = _ctrl.PushOverrideState(true, true, false, 3600f); } catch (Exception e) { Log.LogWarning("[maptools] live raise: " + e.Message); } }
                _shown = true;
                Log.LogInfo($"[maptools] LIVE ON: selector '{PathOf(_sel.transform)}' active={_sel.gameObject.activeInHierarchy}, clipboard raised+focused (token={_tok}) — camera NOT requested. If the palette didn't appear, the toggle target is a parent (see F1 'ancestors').");
            }
            else
            {
                if (_ctrl != null) { try { _ctrl.CancelOverride(_tok); } catch (Exception e) { Log.LogWarning("[maptools] live cancel: " + e.Message); } }
                try { _sel.gameObject.SetActive(false); } catch (Exception e) { Log.LogWarning("[maptools] live hide: " + e.Message); }
                _shown = false;
                Log.LogInfo("[maptools] LIVE OFF: override cancelled, selector GameObject inactive again.");
            }
        }

        private static void EnsureRefs()
        {
            try { if (_sel == null) { var s = AllOf<ClipboardToolSelector>(); if (s.Count > 0) _sel = s[0]; } } catch { }
            try { if (_ctrl == null) { var c = AllOf<ClipboardStateController>(); if (c.Count > 0) _ctrl = c[0]; } } catch { }
        }

        // ------------------------------------------------------------------ helpers
        private static void DumpSlots(ClipboardToolSelector s)
        {
            try
            {
                var slots = s.slots;
                int n = slots != null ? slots.Count : 0;
                var parts = new List<string>();
                for (int i = 0; i < n && i < 16; i++)
                {
                    var slot = slots[i];
                    if (slot == null) { parts.Add("<null>"); continue; }
                    string nm = "?", mk = "<none>";
                    try { nm = slot.gameObject.name; } catch { }
                    try { if (slot.markerPrefab != null) mk = slot.markerPrefab.name; } catch { }
                    parts.Add($"'{nm}'(marker='{mk}')");
                }
                Log.LogInfo($"[maptools]     slots={n}: {string.Join(" ", parts.ToArray())}");
            }
            catch (Exception e) { Log.LogWarning("[maptools]     slots: " + e.Message); }
        }

        // Reveal the inspector-wired calls — this is what tells us how the game bundles the palette + clipboard
        // raise onto the focus grab, i.e. exactly what our button must replicate.
        private static void DumpUnityEvent(string label, UnityEngine.Events.UnityEvent ev)
        {
            try
            {
                if (ev == null) { Log.LogInfo($"[maptools]     {label}: <null event>"); return; }
                int cnt = ev.GetPersistentEventCount();
                if (cnt == 0) { Log.LogInfo($"[maptools]     {label}: 0 persistent listeners (wired in code, not inspector)"); return; }
                for (int i = 0; i < cnt; i++)
                {
                    string tgt = "?", method = "?";
                    try { var t = ev.GetPersistentTarget(i); tgt = t != null ? t.name + " (" + t.ToString() + ")" : "null"; } catch { }
                    try { method = ev.GetPersistentMethodName(i); } catch { }
                    Log.LogInfo($"[maptools]     {label}[{i}] target='{tgt}' method='{method}'");
                }
            }
            catch (Exception e) { Log.LogWarning($"[maptools]     {label}: {e.Message}"); }
        }

        // Walk up to 5 ancestors, flagging the components that matter so we can see which level is the toggle unit.
        private static void DumpAncestors(Transform t)
        {
            try
            {
                var sb = new System.Text.StringBuilder("[maptools]     ancestors:");
                Transform p = t;
                for (int depth = 0; p != null && depth < 5; depth++, p = p.parent)
                {
                    string flags = "";
                    if (Has<ClipboardStateRelay>(p)) flags += "+Relay";
                    if (Has<FocusSystem.CinemachineFocusTarget>(p)) flags += "+FocusTarget";
                    if (Has<RTSMapCameraController>(p)) flags += "+RTSMapCam";
                    if (Has<ClipboardStateController>(p)) flags += "+StateCtrl";
                    sb.Append($" '{p.name}'(self={p.gameObject.activeSelf}{flags})");
                    if (p.parent != null) sb.Append(" ->");
                }
                Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Log.LogWarning("[maptools]     ancestors: " + e.Message); }
        }

        private static bool Has<T>(Transform t) where T : Component
        { try { return t.GetComponent<T>() != null; } catch { return false; } }

        private static bool SafeEnabled(Behaviour b) { try { return b.enabled; } catch { return false; } }
        private static bool SafeIsActive(RTSMapCameraController c) { try { return c.isActive; } catch { return false; } }
        private static string SceneOf(GameObject go) { try { return go.scene.name; } catch { return "?"; } }

        private static List<T> AllOf<T>() where T : UnityEngine.Object
        {
            var list = new List<T>();
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var c = arr[i].TryCast<T>();
                        if (c != null) list.Add(c);
                    }
            }
            catch (Exception e) { Log.LogWarning($"[maptools] enum {typeof(T).Name}: {e.Message}"); }
            return list;
        }

        private static string PathOf(Transform t)
        {
            try
            {
                var sb = new System.Text.StringBuilder(t.name);
                for (Transform p = t.parent; p != null; p = p.parent)
                    sb.Insert(0, p.name + "/");
                return sb.ToString();
            }
            catch { return t != null ? t.name : "<null>"; }
        }
    }
}
