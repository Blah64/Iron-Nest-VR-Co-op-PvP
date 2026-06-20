using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 3 co-op: replicate TACTICAL-MAP item placements — the physical 3D tokens/cards (DraggableItem,
    /// a FIXED set of ~41 incl. the record disk, no spawn/destroy lifecycle). Same transient per-item
    /// ownership as cockpit controls: poll each token's own <c>IsBeingDragged</c> (true whether the VR player
    /// dragged it with the trigger or the flatscreen player used the native mouse — both drive the game's
    /// cursor drag), claim it, stream its position, and the peer mirrors it.
    ///
    /// COORDINATE FRAME: the tactical map sits INSIDE the Barbet (the rotating, swaying turret frame), so a
    /// token's WORLD position differs between machines the instant turret aim/sway diverge — the same reason
    /// avatars are synced Barbet-relative. So positions are sent relative to the <c>MapPiece3D</c> board
    /// transform (InverseTransformPoint on send, TransformPoint on apply). That frame is also immune to each
    /// player panning the board independently (the board and its tokens move together).
    ///
    /// While a peer drags a token we set <c>_externallyControlled=true</c> so the local input loop won't fight
    /// the applied transform; on release we make the placement STICK by calling <c>ItemSlot.PlaceItem</c> when
    /// it landed in a slot (otherwise the game would snap it back to where its own state thinks it is).
    ///
    /// ALSO REPLICATES the dynamically-drawn bearing/range MARKERS (<c>MapMarkerLineUI</c>) — these DO have a
    /// create/destroy lifecycle (unlike the fixed token set). They carry no stable id, so each side assigns its
    /// own collision-free netId to the markers it creates; their geometry is two endpoints in <c>mapRect</c>-
    /// LOCAL space (already frame-stable cross-machine — no Barbet transform needed). We replicate the
    /// FINALIZED set: detect a local finalize (a new GameObject appears in <c>MapMarkerPlacer.placedMarkers</c>)
    /// → send ADD; the peer instantiates the same prefab and replays Initialize/UpdateLine/FinalizePlacement;
    /// either side deleting a marker (it leaves placedMarkers) → send DEL; the peer destroys its mirror. The
    /// live drag-preview is deferred polish. The mission-spawned MapEntity targets are host-authoritative
    /// (Phase 4).
    /// </summary>
    internal static class CoopMap
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_GRAB = 10;        // [t][itemId i32]                          reliable
        public const byte MSG_POS = 11;         // [t][itemId i32][x,y,z f32]  (board-local) unreliable
        public const byte MSG_PLACE = 12;       // [t][itemId i32][x,y,z f32][slotId i32]   reliable
        public const byte MSG_MARKER_ADD = 14;  // [t][netId i32][prefabName str][oX,oY,tX,tY f32] reliable  (13 = ctrl JIP snap)
        public const byte MSG_MARKER_DEL = 15;  // [t][netId i32]                           reliable

        private const float StaleSec = 2f;

        private sealed class Item
        {
            public int NetId;
            public DraggableItem Token;
            public Transform T;
            public bool LocalOwned;
            public bool PrevDragging;
            public bool RemoteOwned;
            public float RemoteUntil;
            public bool HasRemotePos;
            public Vector3 RemoteLocal;     // board-local target position
        }

        private static readonly Dictionary<int, Item> _items = new Dictionary<int, Item>();
        private static readonly Dictionary<int, ItemSlot> _slots = new Dictionary<int, ItemSlot>();
        private static Transform _ref;       // MapPiece3D board (the shared coordinate frame)
        private static int _refIid = -1;
        private static float _nextScan, _nextSend;

        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];
        private static readonly byte[] _scratch = new byte[128];   // string (de)serialization for marker prefab names

        // --- bearing/range markers (MapMarkerLineUI) ---
        private sealed class Marker
        {
            public int NetId;
            public GameObject Go;
            public MapMarkerLineUI Ui;
            public int InstanceId;     // Go.GetInstanceID() — diffs against MapMarkerPlacer.placedMarkers
            public string PrefabName;  // the marker prefab's name (encodes COLOR/style) — resolved by name on the peer
            public bool IsLocal;       // we created it via local game input (vs mirrored from the peer)
        }

        private static readonly Dictionary<int, Marker> _markers = new Dictionary<int, Marker>();   // netId -> marker
        private static readonly Dictionary<int, int> _byInstance = new Dictionary<int, int>();        // GO instanceId -> netId
        private static readonly HashSet<int> _seen = new HashSet<int>();                              // placedMarkers instanceIds this tick
        private static readonly List<int> _toRemove = new List<int>();
        private static MapMarkerPlacer _placer;
        private static int _placerIid = -1;
        private static float _nextPlacerScan;
        private static int _markerSeq;
        private static readonly Dictionary<string, GameObject> _prefabByName = new Dictionary<string, GameObject>();   // resolved marker prefabs (by color/style name)

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopMapSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { if (_items.Count > 0 || _markers.Count > 0) ClearOwnership(); return; }
            try
            {
                EnsureRegistry();   // resolves the token board AND the marker placer

                float now = Time.unscaledTime;
                bool sendNow = Config.CoopSendHz <= 0f || now >= _nextSend;
                if (sendNow && Config.CoopSendHz > 0f) _nextSend = now + 1f / Config.CoopSendHz;

                DetectLocalMarkers();   // bearing/range markers — independent of the token board

                if (_ref == null || _items.Count == 0) return;

                foreach (var it in _items.Values)
                {
                    if (it.Token == null || it.T == null) continue;
                    bool dragging = false;
                    try { dragging = it.Token.IsBeingDragged; } catch { }

                    if (dragging && !it.PrevDragging)
                    {
                        if (!(it.RemoteOwned && now < it.RemoteUntil)) { it.LocalOwned = true; SendGrab(it.NetId); Log.LogInfo($"[map] grabbed '{it.T.name}' -> owner"); }
                    }
                    else if (!dragging && it.PrevDragging && it.LocalOwned)
                    {
                        it.LocalOwned = false;
                        SendPlace(it);
                    }
                    it.PrevDragging = dragging;

                    if (it.LocalOwned && sendNow) SendPos(it);
                    if (it.RemoteOwned && now >= it.RemoteUntil) { it.RemoteOwned = false; ClearExternal(it); }
                }
            }
            catch (Exception e) { Log.LogWarning("[map] tick: " + e.Message); }
        }

        // Apply remote-owned token positions after the game's Update so our placement wins the frame.
        public static void LateApply()
        {
            if (!Config.CoopMapSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer || _ref == null) return;
            float now = Time.unscaledTime;
            foreach (var it in _items.Values)
            {
                if (it.LocalOwned || !it.RemoteOwned || now >= it.RemoteUntil || !it.HasRemotePos || it.Token == null) continue;
                try
                {
                    if (!it.Token._externallyControlled) it.Token._externallyControlled = true;
                    it.T.position = _ref.TransformPoint(it.RemoteLocal);
                }
                catch { }
            }
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            float now = Time.unscaledTime;
            int o = 1;
            switch (type)
            {
                case MSG_GRAB:
                {
                    if (len < 5) return;
                    int id = GetInt(a, ref o);
                    if (_items.TryGetValue(id, out var it))
                    {
                        if (it.LocalOwned) { if (CoopP2P.IsHost) return; it.LocalOwned = false; }
                        it.RemoteOwned = true; it.RemoteUntil = now + StaleSec;
                        Log.LogInfo($"[map] remote grabbed '{it.T.name}' <- peer");
                    }
                    break;
                }
                case MSG_POS:
                {
                    if (len < 17) return;
                    int id = GetInt(a, ref o);
                    Vector3 p = GetV(a, ref o);
                    if (_items.TryGetValue(id, out var it) && Finite(p)) { it.RemoteLocal = p; it.HasRemotePos = true; it.RemoteOwned = true; it.RemoteUntil = now + StaleSec; }
                    break;
                }
                case MSG_PLACE:
                {
                    if (len < 21) return;
                    int id = GetInt(a, ref o);
                    Vector3 p = GetV(a, ref o);
                    int slotId = GetInt(a, ref o);
                    if (_items.TryGetValue(id, out var it) && it.Token != null)
                    {
                        try
                        {
                            if (Finite(p) && _ref != null) it.T.position = _ref.TransformPoint(p);
                            if (slotId != 0 && _slots.TryGetValue(slotId, out var slot) && slot != null)
                                slot.PlaceItem(it.Token);        // make a slot placement stick
                        }
                        catch (Exception e) { Log.LogWarning("[map] place: " + e.Message); }
                        it.RemoteOwned = false; it.HasRemotePos = false;
                        ClearExternal(it);
                        Log.LogInfo($"[map] applied remote place '{it.T.name}' (slot={(slotId != 0)}) <- peer");
                    }
                    break;
                }
                case MSG_MARKER_ADD:
                {
                    if (len < 9) return;
                    int netId = GetInt(a, ref o);
                    string prefabName = GetStr(a, ref o, len);
                    if (o + 16 > len) return;
                    Vector2 origin = new Vector2(GetF(a, ref o), GetF(a, ref o));
                    Vector2 tip = new Vector2(GetF(a, ref o), GetF(a, ref o));
                    if (_markers.ContainsKey(netId)) return;   // already mirrored (dup / JIP re-send)
                    SpawnMirror(netId, prefabName ?? "", origin, tip);
                    break;
                }
                case MSG_MARKER_DEL:
                {
                    if (len < 5) return;
                    int netId = GetInt(a, ref o);
                    if (_markers.TryGetValue(netId, out var m))
                    {
                        try { var pm = _placer != null ? _placer.placedMarkers : null; if (pm != null && m.Go != null) pm.Remove(m.Go); } catch { }
                        try { if (m.Go != null) UnityEngine.Object.Destroy(m.Go); } catch { }
                        _byInstance.Remove(m.InstanceId);
                        _markers.Remove(netId);
                        Log.LogInfo($"[map] removed remote marker <- peer (id={netId})");
                    }
                    break;
                }
            }
        }

        // ---------------- send ----------------

        private static void SendGrab(int id)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_GRAB; o = PutInt(o, id);
            CoopP2P.Send(_buf, o, true);
        }

        private static void SendPos(Item it)
        {
            if (!EnsureBuf() || _ref == null) return;
            Vector3 local;
            try { local = _ref.InverseTransformPoint(it.T.position); } catch { return; }
            if (!Finite(local)) return;
            int o = 0; _buf[o++] = MSG_POS; o = PutInt(o, it.NetId); o = PutV(o, local);
            CoopP2P.Send(_buf, o, false);
        }

        private static void SendPlace(Item it, bool log = true)
        {
            if (!EnsureBuf() || _ref == null) return;
            Vector3 local;
            try { local = _ref.InverseTransformPoint(it.T.position); } catch { return; }
            int slotId = 0;
            try
            {
                if (it.Token.CurrentLocation == DraggableItem.ItemLocation.Slot)
                {
                    var s = it.Token.SlotRef;
                    if (s != null) slotId = Fnv(PathOf(s.transform));
                }
            }
            catch { }
            int o = 0; _buf[o++] = MSG_PLACE; o = PutInt(o, it.NetId); o = PutV(o, local); o = PutInt(o, slotId);
            CoopP2P.Send(_buf, o, true);
            if (log) Log.LogInfo($"[map] placed '{it.T.name}' (slot={(slotId != 0)}) -> peer");
        }

        // ---------------- join-in-progress snapshot ----------------

        // Host → new joiner: re-send EVERY token's current board-local position + slot so the joiner adopts the
        // host's map layout instead of its default. Reuses MSG_PLACE (the receiver applies position and sticks
        // slot placements, then clears ownership — exactly snapshot semantics). Usually a no-op in scenes
        // without a map board (combat hub only); logs a one-line summary rather than per-token chatter.
        // Called by CoopP2P.SendJoinSnapshot (host only).
        public static void SendSnapshot()
        {
            if (!Config.CoopMapSync) return;
            try
            {
                EnsureRegistry();
                int n = 0;
                if (_ref != null)
                    foreach (var it in _items.Values)
                    {
                        if (it.Token == null || it.T == null) continue;
                        SendPlace(it, false);
                        n++;
                    }
                int mk = 0;
                foreach (var m in _markers.Values)
                {
                    if (m.Ui == null || m.Go == null) continue;
                    SendMarkerAdd(m);
                    mk++;
                }
                if (n == 0 && mk == 0) Log.LogInfo("[map] JIP snapshot: nothing to send (no tokens or markers present)");
                else Log.LogInfo($"[map] sent JIP snapshot -> peer ({n} tokens, {mk} markers)");
            }
            catch (Exception e) { Log.LogWarning("[map] snapshot: " + e.Message); }
        }

        // ---------------- markers: local detection + spawn/send ----------------

        // Detect the LOCAL player creating or deleting a bearing/range marker by diffing the placer's
        // placedMarkers list against what we already track. A GameObject we don't recognise = the local player
        // just finalized a new marker (send ADD); a tracked marker that has left the list = it was deleted here
        // (send DEL). Markers we MIRRORED from the peer are registered with their instanceId, so they're never
        // mistaken for new local placements. Either player may create or delete; deletes propagate both ways.
        private static void DetectLocalMarkers()
        {
            if (_placer == null) return;
            Il2CppSystem.Collections.Generic.List<GameObject> placed = null;
            try { placed = _placer.placedMarkers; } catch { return; }
            if (placed == null) return;

            // 1) New locally-finalized markers.
            _seen.Clear();
            int count;
            try { count = placed.Count; } catch { return; }
            for (int i = 0; i < count; i++)
            {
                GameObject go = null;
                try { go = placed[i]; } catch { continue; }
                if (go == null) continue;
                int iid = go.GetInstanceID();
                _seen.Add(iid);
                if (_byInstance.ContainsKey(iid)) continue;   // already tracked (local or mirror)

                MapMarkerLineUI ui = null;
                try { ui = go.GetComponent<MapMarkerLineUI>(); } catch { }
                if (ui == null) continue;
                int netId = NextMarkerId();
                string pname = MarkerPrefabName(go);
                var m = new Marker { NetId = netId, Go = go, Ui = ui, InstanceId = iid, PrefabName = pname, IsLocal = true };
                _markers[netId] = m; _byInstance[iid] = netId;
                SendMarkerAdd(m);
                Log.LogInfo($"[map] local marker placed -> peer (id={netId} prefab='{pname}' dist={SafeF(ui, true):0.0} ang={SafeF(ui, false):0.0})");
            }

            // 2) Deletions: any tracked marker whose GameObject left placedMarkers (or was destroyed).
            _toRemove.Clear();
            foreach (var kv in _markers)
            {
                var m = kv.Value;
                if (m.Go == null || !_seen.Contains(m.InstanceId)) _toRemove.Add(kv.Key);
            }
            for (int i = 0; i < _toRemove.Count; i++)
            {
                int netId = _toRemove[i];
                if (!_markers.TryGetValue(netId, out var m)) continue;
                _byInstance.Remove(m.InstanceId);
                _markers.Remove(netId);
                SendMarkerDel(netId);
                Log.LogInfo($"[map] local marker deleted -> peer (id={netId})");
            }
        }

        // Peer → mirror a finalized marker: instantiate the same prefab, replay the geometry, finalize, and add
        // it to placedMarkers so it's a first-class marker locally (hoverable/deletable, and that delete
        // propagates back). Registered with its instanceId so our own detection won't re-broadcast it.
        private static void SpawnMirror(int netId, string prefabName, Vector2 origin, Vector2 tip)
        {
            if (_placer == null) { Log.LogWarning($"[map] marker add (id={netId}) but no MapMarkerPlacer in scene"); return; }
            try
            {
                GameObject prefab = ResolveMarkerPrefab(prefabName);   // by NAME → correct COLOR (placer list is empty; each side's activeMarkerPrefab differs)
                if (prefab == null) { Log.LogWarning($"[map] marker add (id={netId}): no prefab named '{prefabName}'"); return; }
                RectTransform mapRect = null;
                try { mapRect = _placer.mapRect; } catch { }
                if (mapRect == null) { Log.LogWarning($"[map] marker add (id={netId}): placer has no mapRect"); return; }

                var inst = UnityEngine.Object.Instantiate(prefab).TryCast<GameObject>();
                if (inst == null) { Log.LogWarning($"[map] marker add (id={netId}): instantiate failed"); return; }
                try { inst.transform.SetParent(mapRect.transform, false); } catch { }
                MapMarkerLineUI ui = null;
                try { ui = inst.GetComponent<MapMarkerLineUI>(); } catch { }
                if (ui == null) { Log.LogWarning($"[map] marker add (id={netId}): prefab has no MapMarkerLineUI"); try { UnityEngine.Object.Destroy(inst); } catch { } return; }

                try { ui.Initialize(origin, mapRect); } catch (Exception e) { Log.LogWarning("[map] marker Initialize: " + e.Message); }
                try { ui.UpdateLine(origin, tip, mapRect); } catch (Exception e) { Log.LogWarning("[map] marker UpdateLine: " + e.Message); }
                try { ui.FinalizePlacement(); } catch (Exception e) { Log.LogWarning("[map] marker FinalizePlacement: " + e.Message); }
                try { var pm = _placer.placedMarkers; if (pm != null) pm.Add(inst); } catch { }

                int iid = inst.GetInstanceID();
                _markers[netId] = new Marker { NetId = netId, Go = inst, Ui = ui, InstanceId = iid, PrefabName = prefabName, IsLocal = false };
                _byInstance[iid] = netId;
                Log.LogInfo($"[map] mirrored remote marker <- peer (id={netId} prefab='{prefabName}' dist={SafeF(ui, true):0.0} ang={SafeF(ui, false):0.0})");
            }
            catch (Exception e) { Log.LogWarning("[map] spawn mirror: " + e.Message); }
        }

        private static void SendMarkerAdd(Marker m)
        {
            if (!EnsureBuf() || m.Ui == null) return;
            Vector2 origin = Vector2.zero, tip = Vector2.zero;
            try { origin = m.Ui.OriginLocal; var t = m.Ui.TipLocalPosition; tip = new Vector2(t.x, t.y); } catch { }
            if (!Finite(origin.x) || !Finite(origin.y) || !Finite(tip.x) || !Finite(tip.y)) return;
            int o = 0; _buf[o++] = MSG_MARKER_ADD; o = PutInt(o, m.NetId); o = PutStr(o, m.PrefabName);
            o = PutF(o, origin.x); o = PutF(o, origin.y); o = PutF(o, tip.x); o = PutF(o, tip.y);
            CoopP2P.Send(_buf, o, true);
        }

        private static void SendMarkerDel(int netId)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_MARKER_DEL; o = PutInt(o, netId);
            CoopP2P.Send(_buf, o, true);
        }

        // The marker prefab's NAME (which encodes its color/style — e.g. "RED"/"Yellow"/"White"). Unity names an
        // instantiated marker "<Prefab>(Clone)", so the instance name minus the suffix IS the prefab name it was
        // drawn from — robust regardless of which prefab is currently "active". Falls back to the placer's active
        // prefab name. Method borrowed from the reference iron-nest-coop mod (DrawLines.cs), which keys color by
        // prefab name instead of by index (our index broke because MapMarkerPlacer.markerPrefabs is empty).
        private static string MarkerPrefabName(GameObject go)
        {
            string n = null;
            try { n = go != null ? go.name : null; } catch { }
            if (!string.IsNullOrEmpty(n))
            {
                int idx = n.IndexOf("(Clone)", StringComparison.Ordinal);
                if (idx >= 0) n = n.Substring(0, idx);
                n = n.Trim();
                if (n.Length > 0) return n;
            }
            try { var ap = _placer != null ? _placer.activeMarkerPrefab : null; if (ap != null) return ap.name; } catch { }
            return "";
        }

        // Resolve a marker prefab by name so the mirror gets the SAME color the sender drew. The placer's own
        // markerPrefabs list is empty in-scene, so we search ALL loaded GameObjects for an exact name match
        // (excludes "(Clone)" scene instances) — the same global lookup the reference mod uses. Cached per name.
        private static GameObject ResolveMarkerPrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) { try { return _placer != null ? _placer.activeMarkerPrefab : null; } catch { return null; } }
            if (_prefabByName.TryGetValue(name, out var cached) && cached != null) return cached;

            try { var ps = _placer != null ? _placer.markerPrefabs : null; if (ps != null) for (int i = 0; i < ps.Count; i++) { var p = ps[i]; if (p != null && p.name == name) { _prefabByName[name] = p; return p; } } } catch { }

            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
                if (all != null) for (int i = 0; i < all.Length; i++)
                {
                    var g = all[i].TryCast<GameObject>(); if (g == null) continue;
                    string gn; try { gn = g.name; } catch { continue; }
                    if (gn == name) { _prefabByName[name] = g; return g; }
                }
            }
            catch (Exception e) { Log.LogWarning("[map] resolve prefab '" + name + "': " + e.Message); }

            try { return _placer != null ? _placer.activeMarkerPrefab : null; } catch { return null; }   // last resort
        }

        // Collision-free across the two players: FNV of (mySteamId : sequence). Avoid 0 (used as a "none" id).
        private static int NextMarkerId()
        {
            int id = Fnv(CoopP2P.MyId.ToString() + ":" + (_markerSeq++));
            if (id == 0) id = Fnv(CoopP2P.MyId.ToString() + ":" + (_markerSeq++) + "x");
            return id;
        }

        private static float SafeF(MapMarkerLineUI ui, bool dist)
        {
            try { return dist ? ui.DistanceValue : ui.AngleValue; } catch { return 0f; }
        }

        private static void ClearMarkers(bool destroyMirrors)
        {
            if (destroyMirrors)
                foreach (var m in _markers.Values)
                {
                    if (m.IsLocal || m.Go == null) continue;
                    try { var pm = _placer != null ? _placer.placedMarkers : null; if (pm != null) pm.Remove(m.Go); } catch { }
                    try { UnityEngine.Object.Destroy(m.Go); } catch { }
                }
            _markers.Clear(); _byInstance.Clear();
        }

        // ---------------- registry ----------------

        private static void EnsureRegistry()
        {
            // Marker placer — resolved on its own light timer, independent of the token board (a scene can have
            // the bearing-marker map without the 3D token board, e.g. the hub). On a placer change (new scene)
            // drop the marker tracking (the old GameObjects died with the scene).
            if (Time.unscaledTime >= _nextPlacerScan)
            {
                _nextPlacerScan = Time.unscaledTime + 2f;
                try
                {
                    var pls = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapMarkerPlacer>(), FindObjectsSortMode.None);
                    var p = (pls != null && pls.Length > 0) ? pls[0].TryCast<MapMarkerPlacer>() : null;
                    int piid = p != null ? p.GetInstanceID() : -1;
                    if (piid != _placerIid) { ClearMarkers(false); _prefabByName.Clear(); _placerIid = piid; if (p != null) Log.LogInfo($"[map] marker placer found ({(p.markerPrefabs != null ? p.markerPrefabs.Count : 0)} prefabs, host={CoopP2P.IsHost})"); }
                    _placer = p;
                }
                catch { _placer = null; }
            }

            Transform reference = null;
            try
            {
                var boards = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapPiece3D>(), FindObjectsSortMode.None);
                if (boards != null && boards.Length > 0) { var b = boards[0].TryCast<MapPiece3D>(); if (b != null) reference = b.transform; }
            }
            catch { }
            if (reference == null) { if (_items.Count > 0) { _items.Clear(); _slots.Clear(); _ref = null; _refIid = -1; } return; }

            int iid = reference.GetInstanceID();
            bool rescan = _refIid != iid || _items.Count == 0 || Time.unscaledTime >= _nextScan;
            _ref = reference;
            if (!rescan) return;
            _nextScan = Time.unscaledTime + 3f;
            if (_refIid != iid) { _items.Clear(); _slots.Clear(); }
            _refIid = iid;

            int addedItems = 0, addedSlots = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<DraggableItem>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var d = arr[i].TryCast<DraggableItem>(); if (d == null) continue;
                    var tr = d.transform; if (tr == null) continue;
                    int id = Fnv(PathOf(tr));
                    if (_items.ContainsKey(id)) continue;
                    _items[id] = new Item { NetId = id, Token = d, T = tr };
                    addedItems++;
                }
            }
            catch (Exception e) { Log.LogWarning("[map] scan items: " + e.Message); }
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ItemSlot>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var s = arr[i].TryCast<ItemSlot>(); if (s == null) continue;
                    var tr = s.transform; if (tr == null) continue;
                    int id = Fnv(PathOf(tr));
                    if (_slots.ContainsKey(id)) continue;
                    _slots[id] = s;
                    addedSlots++;
                }
            }
            catch (Exception e) { Log.LogWarning("[map] scan slots: " + e.Message); }

            if (addedItems + addedSlots > 0) Log.LogInfo($"[map] registry: {_items.Count} tokens, {_slots.Count} slots (host={CoopP2P.IsHost})");
        }

        private static void ClearExternal(Item it)
        {
            try { if (it.Token != null && it.Token._externallyControlled) it.Token._externallyControlled = false; } catch { }
        }

        private static void ClearOwnership()
        {
            foreach (var it in _items.Values) { it.LocalOwned = false; it.RemoteOwned = false; it.PrevDragging = false; it.HasRemotePos = false; ClearExternal(it); }
            ClearMarkers(true);   // co-op ended → drop the peer's mirrored markers; locals re-detect on reconnect
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            int local = 0, remote = 0;
            foreach (var it in _items.Values) { if (it.LocalOwned) local++; if (it.RemoteOwned) remote++; }
            int mLocal = 0, mRemote = 0;
            foreach (var m in _markers.Values) { if (m.IsLocal) mLocal++; else mRemote++; }
            return $"map: {_items.Count} tokens, {_slots.Count} slots, boardRef={_ref != null}, owned local={local} remote={remote} | " +
                   $"markers: placer={_placer != null}, {_markers.Count} tracked (local={mLocal} mirror={mRemote})";
        }

        public static void Dump()
        {
            Log.LogInfo("[map] " + Status());
            foreach (var it in _items.Values)
            {
                if (it.LocalOwned) Log.LogInfo($"[map]   LOCAL-owned: '{it.T.name}'");
                else if (it.RemoteOwned) Log.LogInfo($"[map]   remote-owned: '{it.T.name}' localPos={it.RemoteLocal}");
            }
            foreach (var m in _markers.Values)
                Log.LogInfo($"[map]   marker id={m.NetId} {(m.IsLocal ? "local" : "mirror")} prefab='{m.PrefabName}' dist={SafeF(m.Ui, true):0.0} ang={SafeF(m.Ui, false):0.0}");
        }

        // ---------------- helpers ----------------

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) { sb.Insert(0, '/'); sb.Insert(0, p.name); }
            return sb.ToString();
        }

        private static int Fnv(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= 16777619u; }
            return unchecked((int)h);
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(128); return true; }   // room for a marker prefab name + endpoints
            catch (Exception e) { Log.LogWarning("[map] buf: " + e.Message); return false; }
        }

        private static int PutInt(int o, int v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }
        private static int PutF(int o, float v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }
        private static int PutV(int o, Vector3 v) { o = PutF(o, v.x); o = PutF(o, v.y); o = PutF(o, v.z); return o; }

        private static int PutStr(int o, string s)
        {
            s ??= "";
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = bytes.Length; if (n > 64) n = 64;   // marker prefab names are short; cap guards the buffer
            o = PutInt(o, n);
            for (int i = 0; i < n; i++) _buf[o + i] = bytes[i];
            return o + n;
        }

        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_f4, 0); }
        private static float GetF(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToSingle(_f4, 0); }
        private static Vector3 GetV(Il2CppStructArray<byte> a, ref int o) { float x = GetF(a, ref o), y = GetF(a, ref o), z = GetF(a, ref o); return new Vector3(x, y, z); }

        private static string GetStr(Il2CppStructArray<byte> a, ref int o, int len)
        {
            if (o + 4 > len) return null;
            int n = GetInt(a, ref o);
            if (n < 0 || o + n > len || n > _scratch.Length) return null;
            for (int i = 0; i < n; i++) _scratch[i] = a[o + i];
            o += n;
            return Encoding.UTF8.GetString(_scratch, 0, n);
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    }
}
