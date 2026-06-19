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
        private static int _coopFpcId = -2;     // re-probe co-op layout when the player rig changes (new scene)
        private static bool _coopEntitiesDumped; // one-shot combat-entity dump per scene (fires when entities appear)
        private static float _next;

        public static void Tick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                // Co-op feasibility ground-truth: player rig / body mesh / spawn points / cockpit frame.
                // Independent of the HUD camera so it captures menu/map scenes too.
                CoopProbe();

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

        // ── Co-op feasibility probe ────────────────────────────────────────────────────────────────
        // Turns decompile inference into ground truth for the multiplayer scoping: the player rig
        // structure, whether a reusable body mesh exists (for a remote avatar), the tag-based spawn
        // points (player-2 placement), and the swaying cockpit frame (the local space a remote avatar
        // and any positional replicated state must be expressed relative to). Re-dumps whenever the
        // FirstPersonController instance changes (new scene / mission load). Pure logging.
        private static void CoopProbe()
        {
            try
            {
                var fpcArr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<FirstPersonController>(),
                                 FindObjectsInactive.Include, FindObjectsSortMode.None);
                int fn = fpcArr != null ? fpcArr.Length : 0;
                if (fn == 0) return;
                var fpc0 = fpcArr[0].TryCast<FirstPersonController>();
                if (fpc0 == null) return;
                int id = fpc0.GetInstanceID();
                bool newScene = id != _coopFpcId;
                if (newScene) { _coopFpcId = id; _coopEntitiesDumped = false; } // re-arm per scene

                // Entities appear mid-scene (combat starts after the intro), so this runs EVERY tick
                // until it catches the first non-empty set — independent of the once-per-scene rig dump.
                CoopEntityProbe();

                if (!newScene) return;               // rig/spawn/control already dumped for this scene

                Log.LogInfo($"=== DIAG CO-OP PROBE (FirstPersonController count={fn}) ===");

                // (1) Player rig + body-mesh check (does a reusable avatar mesh exist?).
                for (int i = 0; i < fn && i < 4; i++)
                {
                    var fpc = fpcArr[i].TryCast<FirstPersonController>();
                    if (fpc == null) continue;
                    var t = fpc.transform;
                    Log.LogInfo($"[coop] FPC[{i}] '{Path(t)}' active={fpc.gameObject.activeInHierarchy}");
                    Log.LogInfo($"[coop]   comps: {Comps(fpc.gameObject)}");
                    var cr = fpc.cameraRoot;
                    var amg = fpc.actualMainGameObject;
                    var mgt = fpc.mainGameObjectTransform;
                    var cc = fpc.controller;
                    Log.LogInfo($"[coop]   cameraRoot={(cr != null ? Path(cr) : "null")}");
                    Log.LogInfo($"[coop]   actualMainGameObject={(amg != null ? Path(amg.transform) : "null")}");
                    Log.LogInfo($"[coop]   mainGameObjectTransform={(mgt != null ? Path(mgt) : "null")}");
                    Log.LogInfo($"[coop]   controller={(cc != null ? Path(cc.transform) : "null")}");
                    DumpRenderers(t);   // search the WHOLE rig root (not just the camera leaf) for a body mesh
                    Log.LogInfo($"[coop]   cockpit-frame ancestor: {CockpitFrame(t)}");
                }

                // (2) Tag-based spawn points — how player 2 would be placed.
                DumpSpawns();

                // (3) Swaying cockpit-frame candidates — the avatar's local parent.
                DumpAll("MechSwayController", Il2CppType.Of<MechSwayController>());
                DumpAll("SwingController", Il2CppType.Of<SwingController>());
                DumpAll("TurretRotationFollowerRigidbody", Il2CppType.Of<TurretRotationFollowerRigidbody>());

                // (4) Turret + per-gun stations (per-gun ownership) and the turret's frame.
                var turr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TurretController>(), FindObjectsSortMode.None);
                if (turr != null && turr.Length > 0)
                {
                    var tc = turr[0].TryCast<TurretController>();
                    if (tc != null) Log.LogInfo($"[coop] TurretController '{Path(tc.transform)}' frame ancestor: {CockpitFrame(tc.transform)}");
                }
                var guns = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<GunController>(), FindObjectsSortMode.None);
                int gn = guns != null ? guns.Length : 0;
                Log.LogInfo($"[coop] GunController stations: {gn}");
                for (int i = 0; i < gn && i < 8; i++)
                {
                    var g = guns[i].TryCast<GunController>();
                    if (g != null) Log.LogInfo($"[coop]   - gun[{i}] '{Path(g.transform)}'");
                }

                // (5) Cockpit control-replication surface: every drag control + its lock-broker tag,
                // plus the click buttons. This is the set of state both machines must keep in sync, and
                // the broker tag groups which controls share a lock region.
                DumpControls("DialInteractable", Il2CppType.Of<DialInteractable>());
                DumpControls("LinearSliderInteractable", Il2CppType.Of<LinearSliderInteractable>());
                DumpControls("LookAtTarget(button)", Il2CppType.Of<LookAtTarget>());
                DumpAll("InteractionLockBroker", Il2CppType.Of<InteractionLockBroker>());

                // (6) Live target enumeration sanity (entities to replicate later).
                Log.LogInfo($"[coop] EntityLocation in scene: {Count(Il2CppType.Of<EntityLocation>())}");
            }
            catch (Exception e) { Log.LogWarning("[coop] " + e.Message); }
        }

        // Fires once per scene the moment live entities exist (combat starts after the intro), proving
        // the {ID, Position, Health, State} replication payload is readable at runtime from EntityLocation.
        private static void CoopEntityProbe()
        {
            if (_coopEntitiesDumped) return;
            var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(),
                          FindObjectsInactive.Include, FindObjectsSortMode.None);
            int n = arr != null ? arr.Length : 0;
            if (n == 0) return;
            _coopEntitiesDumped = true;
            Log.LogInfo($"=== DIAG CO-OP ENTITIES ({n}) — replication payload sample ===");
            for (int i = 0; i < n && i < 16; i++)
            {
                var el = arr[i].TryCast<EntityLocation>();
                if (el == null) continue;
                var e = el.Entity;
                if (e == null) { Log.LogInfo($"[coop]   - '{Path(el.transform)}' (no MapEntity)"); continue; }
                var p = e.Position;
                Log.LogInfo($"[coop]   - id='{e.ID}' name='{e.Name}' role={e.Role} state={e.State} " +
                            $"hp={e.Health}/{e.MaxHealth} alive={e.IsAlive} pos=({p.x:0.0},{p.y:0.0},{p.z:0.0}) '{Path(el.transform)}'");
            }
        }

        // Dial/Slider carry a broker drag-lock (lockBrokerTag) — the per-control ownership primitive co-op
        // makes network-aware. LookAtTarget buttons have none (instant click = fire-and-forget event).
        private static void DumpControls(string label, Il2CppSystem.Type t)
        {
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None);
            int n = arr != null ? arr.Length : 0;
            Log.LogInfo($"[coop] {label}: {n}");
            for (int i = 0; i < n && i < 40; i++)
            {
                var c = arr[i].TryCast<Component>();
                if (c == null) continue;
                string extra = "";
                try
                {
                    var dial = c.TryCast<DialInteractable>();
                    var slid = c.TryCast<LinearSliderInteractable>();
                    if (dial != null) extra = $"  brokerLock={dial.useBrokerLockWhileDragging} tag='{dial.lockBrokerTag}'";
                    else if (slid != null) extra = $"  brokerLock={slid.useBrokerLockWhileDragging} tag='{slid.lockBrokerTag}'";
                }
                catch { }
                Log.LogInfo($"[coop]   - '{Path(c.transform)}'{extra}");
            }
        }

        // Count + list every Renderer under the player rig. Zero => no body mesh => the remote avatar
        // must be built from scratch (reuse the HandVisuals AssetBundle + URP/Lit pipeline).
        private static void DumpRenderers(Transform root)
        {
            try
            {
                var rs = root.gameObject.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                int rn = rs != null ? rs.Length : 0;
                Log.LogInfo($"[coop]   renderers under rig: {rn}{(rn == 0 ? "  (NO body mesh -> avatar must be built)" : "")}");
                for (int i = 0; i < rn && i < 12; i++)
                {
                    var r = rs[i].TryCast<Renderer>();
                    if (r != null) Log.LogInfo($"[coop]     - {TypeName(r)} '{Path(r.transform)}'");
                }
            }
            catch (Exception e) { Log.LogWarning("[coop] renderers: " + e.Message); }
        }

        // Nearest ancestor carrying a cockpit-motion component — the swaying frame an avatar (and any
        // positional replicated state) must be expressed relative to, since the room pitches/rolls.
        private static string CockpitFrame(Transform t)
        {
            for (Transform a = t; a != null; a = a.parent)
            {
                var go = a.gameObject;
                if (go.GetComponent(Il2CppType.Of<MechSwayController>()) != null ||
                    go.GetComponent(Il2CppType.Of<SwingController>()) != null ||
                    go.GetComponent(Il2CppType.Of<TurretRotationFollowerRigidbody>()) != null)
                    return Path(a);
            }
            return "(none in ancestry — player NOT parented under the swaying frame)";
        }

        private static void DumpSpawns()
        {
            var sp = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerSpawnPoint>(),
                         FindObjectsInactive.Include, FindObjectsSortMode.None);
            int n = sp != null ? sp.Length : 0;
            Log.LogInfo($"[coop] PlayerSpawnPoint: {n}");
            for (int i = 0; i < n && i < 16; i++)
            {
                var p = sp[i].TryCast<PlayerSpawnPoint>();
                if (p == null) continue;
                Log.LogInfo($"[coop]   - '{Path(p.transform)}' playerTag='{p.playerTag}' mode={p.triggerMode} " +
                            $"applyYaw={p.applyYawRotation} active={p.gameObject.activeInHierarchy}");
            }
            var tr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<PlayerSpawnTrigger>(),
                         FindObjectsInactive.Include, FindObjectsSortMode.None);
            int tn = tr != null ? tr.Length : 0;
            Log.LogInfo($"[coop] PlayerSpawnTrigger: {tn}");
            for (int i = 0; i < tn && i < 16; i++)
            {
                var p = tr[i].TryCast<PlayerSpawnTrigger>();
                if (p == null) continue;
                Log.LogInfo($"[coop]   - '{Path(p.transform)}' spawnPointTag='{p.spawnPointTag}' triggerOnEnable={p.triggerOnEnable}");
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
