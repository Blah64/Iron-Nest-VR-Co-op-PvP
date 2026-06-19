using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// One-shot scene dump to identify objects we can't classify from decompilation alone: the watch's
    /// component/child layout (for grab + look-at), and the tutorial clipboards (DraggableItem/DragSurface).
    /// Fires once, a moment after a HUD-bearing scene is up. Pure logging — no behaviour change.
    /// </summary>
    internal static class Diagnostics
    {
        private static ManualLogSource Log => Plugin.Logger;
        private static bool _watchDumped;
        private static bool _dragDumped;
        private static bool _manualDumped;
        private static int _lastCanvasCount = -1;
        private static float _next;

        public static void Tick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                var cam = Camera.main;
                if (cam == null) return;

                // How do the menus render in VR? renderMode + event camera decides the aim-fix approach.
                // Re-dump whenever the canvas count changes, so tutorial instruction clipboards (spawned
                // at runtime) and menu canvases (activated on open) are captured, not just startup ones.
                {
                    var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Canvas>(),
                                  FindObjectsInactive.Include, FindObjectsSortMode.None);
                    int n = arr != null ? arr.Length : 0;
                    if (n > 0 && n != _lastCanvasCount)
                    {
                        _lastCanvasCount = n;
                        Log.LogInfo($"=== DIAG CANVASES ({n}) ===");
                        for (int i = 0; i < n && i < 32; i++)
                        {
                            var cv = arr[i].TryCast<Canvas>();
                            if (cv == null || !cv.isRootCanvas) continue;
                            string wc = cv.worldCamera != null ? cv.worldCamera.name : "null";
                            Log.LogInfo($"[diag] canvas '{cv.name}' mode={cv.renderMode} cam={wc} " +
                                        $"order={cv.sortingOrder} active={cv.gameObject.activeInHierarchy} layer={cv.gameObject.layer}");
                        }
                    }
                }

                if (!_watchDumped)
                {
                    var watch = FindContains(cam.transform, "watch");
                    if (watch != null)
                    {
                        Log.LogInfo($"=== DIAG WATCH '{Path(watch)}' ===");
                        Log.LogInfo($"[diag]   comps: {Comps(watch.gameObject)}");
                        DumpChildren(watch, 2);
                        _watchDumped = true;
                    }
                }

                // Tutorial clipboards live in the tutorial scene — dump these the moment they appear,
                // independently of the watch (which may have been dumped in an earlier mission).
                if (!_dragDumped)
                {
                    int d = Count(Il2CppType.Of<DraggableItem>());
                    int s = Count(Il2CppType.Of<DragSurface>());
                    if (d > 0 || s > 0)
                    {
                        Log.LogInfo("=== DIAG TUTORIAL PROPS ===");
                        DumpAll("DraggableItem", Il2CppType.Of<DraggableItem>());
                        DumpAll("DragSurface", Il2CppType.Of<DragSurface>());
                        DumpAll("ClipboardStateController", Il2CppType.Of<ClipboardStateController>());
                        _dragDumped = true;
                    }
                }

                // The world "operating manual / instruction clipboard" props: identify by name so we can
                // grab them. Dump every Interactable whose name hints at a readable doc, with full
                // component lists + ancestor path + one level of children, plus all ClipboardPickUpHandlers.
                if (!_manualDumped) DumpManuals();
            }
            catch (Exception e) { Log.LogWarning("[diag] " + e.Message); }
        }

        private static int Count(Il2CppSystem.Type t)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            return arr != null ? arr.Length : 0;
        }

        private static readonly string[] ManualHints =
            { "manual", "operating", "instruction", "leaflet", "booklet", "document", "guide", "tutorial", "note", "paper", "diagram" };

        private static void DumpManuals()
        {
            try
            {
                bool found = false;
                var ints = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Interactable>(),
                               FindObjectsInactive.Include, FindObjectsSortMode.None);
                int n = ints != null ? ints.Length : 0;
                for (int i = 0; i < n; i++)
                {
                    var it = ints[i].TryCast<Interactable>();
                    if (it == null) continue;
                    string nm = it.gameObject.name;
                    if (nm == null) continue;
                    string low = nm.ToLowerInvariant();
                    bool hit = false;
                    for (int h = 0; h < ManualHints.Length; h++) if (low.Contains(ManualHints[h])) { hit = true; break; }
                    if (!hit) continue;

                    if (!found) { Log.LogInfo("=== DIAG MANUALS (interactables matching doc-name hints) ==="); found = true; }
                    var t = it.transform;
                    Log.LogInfo($"[diag] '{Path(t)}' active={it.gameObject.activeInHierarchy}");
                    Log.LogInfo($"[diag]   self:   {Comps(it.gameObject)}");
                    if (t.parent != null) Log.LogInfo($"[diag]   parent '{t.parent.name}': {Comps(t.parent.gameObject)}");
                    DumpChildren(t, 1);
                }

                // ClipboardPickUpHandler(s) — the slide/pick-up controller, wherever it lives.
                var hand = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ClipboardPickUpHandler>(),
                               FindObjectsInactive.Include, FindObjectsSortMode.None);
                int hn = hand != null ? hand.Length : 0;
                if (hn > 0)
                {
                    if (!found) { Log.LogInfo("=== DIAG MANUALS ==="); found = true; }
                    Log.LogInfo($"[diag] ClipboardPickUpHandler: {hn}");
                    for (int i = 0; i < hn && i < 6; i++)
                    {
                        var c = hand[i].TryCast<Component>();
                        if (c != null) Log.LogInfo($"[diag]   - {Path(c.transform)}  comps: {Comps(c.gameObject)}");
                    }
                }

                if (found) _manualDumped = true;
            }
            catch (Exception e) { Log.LogWarning("[diag] manuals: " + e.Message); }
        }

        private static void DumpAll(string label, Il2CppSystem.Type t)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            int n = arr != null ? arr.Length : 0;
            Log.LogInfo($"[diag] {label}: {n} found");
            for (int i = 0; i < n && i < 10; i++)
            {
                var c = arr[i].TryCast<Component>();
                if (c == null) continue;
                Log.LogInfo($"[diag]   - {Path(c.transform)}  comps: {Comps(c.gameObject)}");
            }
        }

        private static string Comps(GameObject go)
        {
            try
            {
                var cs = go.GetComponents<Component>();
                string s = "";
                for (int i = 0; i < cs.Length; i++)
                {
                    var c = cs[i];
                    if (c == null) continue;
                    s += (i > 0 ? ", " : "") + TypeName(c);
                }
                return s;
            }
            catch { return "?"; }
        }

        // Real IL2CPP runtime class name (GetType().Name returns the base "Component" for game types
        // whose wrapper isn't resolved). Goes via the il2cpp class pointer.
        private static string TypeName(Component c)
        {
            try
            {
                IntPtr klass = IL2CPP.il2cpp_object_get_class(c.Pointer);
                IntPtr namePtr = IL2CPP.il2cpp_class_get_name(klass);
                string n = Marshal.PtrToStringAnsi(namePtr);
                return string.IsNullOrEmpty(n) ? c.GetType().Name : n;
            }
            catch { return c.GetType().Name; }
        }

        private static void DumpChildren(Transform t, int depth, int indent = 0)
        {
            if (depth < 0) return;
            int n = t.childCount;
            for (int i = 0; i < n && i < 20; i++)
            {
                var c = t.GetChild(i);
                Log.LogInfo($"[diag]   {new string(' ', (indent + 1) * 2)}{c.name}  [{Comps(c.gameObject)}]");
                DumpChildren(c, depth - 1, indent + 1);
            }
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (Transform a = t.parent; a != null; a = a.parent) p = a.name + "/" + p;
            return p;
        }

        private static Transform FindContains(Transform root, string sub)
        {
            int n = root.childCount;
            for (int i = 0; i < n; i++)
            {
                var c = root.GetChild(i);
                if (c.name.ToLowerInvariant().Contains(sub)) return c;
                var d = FindContains(c, sub);
                if (d != null) return d;
            }
            return null;
        }
    }
}
