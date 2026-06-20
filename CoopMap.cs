using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Co-op tactical-map replication. Three map paradigms span the game's scenes; handled together:
    ///
    /// (A) OPERATIONS-MAP TOKENS (<c>DraggableItem</c>) — a FIXED set of ~41 3D tokens/cards on a board that
    /// sits INSIDE the Barbet (the rotating, swaying turret frame). Synced board-RELATIVE
    /// (InverseTransformPoint/TransformPoint against the <c>MapPiece3D</c> board, <c>_ref</c>) so a token's
    /// coords are frame-stable cross-machine and immune to each player panning the board independently.
    /// Transient per-token ownership: poll <c>IsBeingDragged</c>, claim, stream MSG_POS, peer mirrors; on
    /// release MSG_PLACE makes a slot landing stick (<c>ItemSlot.PlaceItem</c>). While a peer owns a token we
    /// set <c>_externallyControlled=true</c> so the local input loop won't fight the applied transform.
    ///
    /// (B) FIRE-MISSION PIECES (<c>MapPiece3D</c>) — the draggable tokens/screws on the fire-direction map.
    /// These already exist on BOTH machines at startup (no spawn lifecycle), so we sync their WORLD transform —
    /// frame drift and slot snapping are not factors on this map. We hook the game's own drag methods:
    /// <c>BeginDragFromManager</c> starts LIVE streaming (unreliable, so the peer sees the token slide rather
    /// than teleport), <c>EndDragFromManager</c> sends the authoritative final (reliable). Keyed by
    /// FNV(hierarchy-path), applied via SetPositionAndRotation. (Matches the working reference co-op mod, plus
    /// the live motion it lacked.)
    ///
    /// (C) BEARING/RANGE LINES (<c>MapMarkerLineUI</c>) — create/destroy lifecycle, no stable id, so each side
    /// assigns its own collision-free netId. Geometry is two endpoints in <c>mapRect</c>-LOCAL space (already
    /// frame-stable). ADD is caught by hooking <c>FinalizePlacement</c> (reliable; replaces the old
    /// placedMarkers poll that missed finalizes); the peer instantiates the same by-NAME prefab (name encodes
    /// color) and replays Initialize/UpdateLine/FinalizePlacement under an echo guard. DELETE is caught by
    /// polling <c>placedMarkers</c> for a tracked marker that has left the list → MSG_MARKER_DEL; the peer
    /// destroys its mirror. Either side may add or delete; both propagate. Mission-spawned MapEntity targets
    /// are host-authoritative (Phase 4 / CoopEntities).
    /// </summary>
    internal static class CoopMap
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_GRAB = 10;        // [t][itemId i32]                          reliable
        public const byte MSG_POS = 11;         // [t][itemId i32][x,y,z f32]  (board-local) unreliable
        public const byte MSG_PLACE = 12;       // [t][itemId i32][x,y,z f32][slotId i32]   reliable
        public const byte MSG_MARKER_ADD = 14;  // [t][netId i32][prefabName str][oX,oY,tX,tY f32] reliable  (13 = ctrl JIP snap) — (tX,tY)=ABSOLUTE target = origin+tip
        public const byte MSG_MARKER_DEL = 15;  // [t][netId i32]                           reliable
        public const byte MSG_PIECE_MOVE = 24;  // [t][pieceId i32][pos 3f][rot 4f]         reliable  — MapPiece3D world transform on drag-end (reference-style)

        // Harmony: fire-mission MAP PIECES (MapPiece3D) and LINES (MapMarkerLineUI) are driven by the game's own
        // drag/finalize methods, not a pollable list — so we hook them (matching the working reference co-op mod)
        // rather than diffing. Set up in Init().
        private static Harmony _harmony;
        private static bool _applyingNetworkLine;   // echo guard: true while we instantiate a mirrored line/piece
        private static readonly Dictionary<int, MapPiece3D> _pieces = new Dictionary<int, MapPiece3D>();   // FNV(path) -> piece
        private static readonly HashSet<int> _draggingLocal = new HashSet<int>();   // MapPiece3D ids the LOCAL player is dragging (live-streamed)
        private static float _nextPieceScan;
        private static float _nextDelScan;   // throttle for marker delete-detection poll

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
            public bool Confirmed;     // has been seen in placedMarkers at least once (guards the finalize→list-add race)
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

        // ---------------- harmony setup (called once from Plugin.Load) ----------------

        public static void Init()
        {
            try { _harmony = new Harmony("com.ironnest.vr.map"); }
            catch (Exception e) { Log.LogError("[map] Harmony init failed: " + e); return; }

            int n = 0;
            n += Patch(typeof(MapPiece3D), "BeginDragFromManager", nameof(OnPieceDragBegin)); // 3D piece: start live-stream
            n += Patch(typeof(MapPiece3D), "EndDragFromManager", nameof(OnPieceDragEnd));      // 3D piece: authoritative drop
            n += Patch(typeof(MapMarkerLineUI), "FinalizePlacement", nameof(OnLineFinalized));  // bearing/range lines
            Log.LogInfo($"[map] map-object hooks: {n}/3 patched (MapPiece3D begin+end drag + MapMarkerLineUI finalize)");
        }

        private static int Patch(Type t, string method, string postfix)
        {
            try
            {
                var mi = AccessTools.Method(t, method);
                if (mi == null) { Log.LogWarning($"[map] hook target not found: {t.Name}.{method}"); return 0; }
                _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(CoopMap), postfix));
                return 1;
            }
            catch (Exception e) { Log.LogWarning($"[map] patch {t.Name}.{method}: " + e.Message); return 0; }
        }

        // ---------------- harmony postfixes: local capture ----------------

        // The local player just GRABBED a 3D map piece → start live-streaming its transform (Tick) so the peer sees
        // it slide. Marks it locally-owned so we ignore the peer's echo for it until release.
        public static void OnPieceDragBegin(MapPiece3D __instance)
        {
            try
            {
                if (!Config.CoopMapSync || !SteamNet.InLobby || !CoopP2P.HasPeer || __instance == null) return;
                var tr = __instance.transform; if (tr == null) return;
                int id = Fnv(PathOf(tr));
                _pieces[id] = __instance;
                _draggingLocal.Add(id);
            }
            catch (Exception e) { Log.LogWarning("[map] piece drag-begin: " + e.Message); }
        }

        // The local player just DROPPED a 3D map piece (token/screw) → broadcast its final world transform RELIABLY
        // (the streamed updates were unreliable). Keyed by the FNV path-hash (cross-process stable, like our other ids).
        public static void OnPieceDragEnd(MapPiece3D __instance)
        {
            try
            {
                if (!Config.CoopMapSync || !SteamNet.InLobby || !CoopP2P.HasPeer || __instance == null) return;
                var tr = __instance.transform; if (tr == null) return;
                int id = Fnv(PathOf(tr));
                if (!_pieces.ContainsKey(id)) _pieces[id] = __instance;
                _draggingLocal.Remove(id);
                SendPieceMove(id, tr.position, tr.rotation, true);   // authoritative final — reliable
                Log.LogInfo($"[map] piece '{tr.name}' dropped -> peer");
            }
            catch (Exception e) { Log.LogWarning("[map] piece drag-end: " + e.Message); }
        }

        // A bearing/range line was just finalized locally → broadcast it. Skips lines WE instantiated as mirrors
        // (echo guard). Sends the ABSOLUTE target (origin+tip), matching the reference's apply path.
        public static void OnLineFinalized(MapMarkerLineUI __instance)
        {
            try
            {
                if (_applyingNetworkLine) return;   // this is our own mirror being placed — don't bounce it back
                if (!Config.CoopMapSync || !SteamNet.InLobby || !CoopP2P.HasPeer || __instance == null) return;
                var go = __instance.gameObject; if (go == null) return;
                int iid = go.GetInstanceID();
                if (_byInstance.ContainsKey(iid)) return;   // already tracked (shouldn't happen for a fresh local line)

                Vector2 origin, target;
                try { origin = __instance.OriginLocal; var t = __instance.TipLocalPosition; target = origin + new Vector2(t.x, t.y); }
                catch { return; }
                if (!Finite(origin.x) || !Finite(origin.y) || !Finite(target.x) || !Finite(target.y)) return;

                int netId = NextMarkerId();
                string pname = MarkerPrefabName(go);
                var m = new Marker { NetId = netId, Go = go, Ui = __instance, InstanceId = iid, PrefabName = pname, IsLocal = true };
                _markers[netId] = m; _byInstance[iid] = netId;
                SendMarkerAdd(netId, pname, origin, target);
                Log.LogInfo($"[map] local marker finalized -> peer (id={netId} prefab='{pname}' origin={origin} target={target})");
            }
            catch (Exception e) { Log.LogWarning("[map] line finalized: " + e.Message); }
        }

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

                ScanPieces(false);   // keep the MapPiece3D registry fresh so we can apply remote piece moves

                // Live-stream the fire-mission pieces the local player is dragging (unreliable; the reliable
                // drop-end packet is the authoritative final). Independent of the DraggableItem token board, so
                // it must run BEFORE the _ref/items early-return — the fire-mission map has no such board.
                if (_draggingLocal.Count > 0 && sendNow)
                    foreach (var id in _draggingLocal)
                    {
                        if (!_pieces.TryGetValue(id, out var p) || p == null) continue;
                        var tr = p.transform; if (tr == null) continue;
                        SendPieceMove(id, tr.position, tr.rotation, false);
                    }

                DetectMarkerDeletes();   // propagate a locally-deleted bearing/range line to the peer

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
                    Vector2 target = new Vector2(GetF(a, ref o), GetF(a, ref o));   // ABSOLUTE endpoint = origin+tip
                    if (_markers.ContainsKey(netId)) return;   // already mirrored (dup / JIP re-send)
                    SpawnMirror(netId, prefabName ?? "", origin, target);
                    break;
                }
                case MSG_PIECE_MOVE:
                {
                    if (len < 1 + 4 + 12 + 16) return;
                    int id = GetInt(a, ref o);
                    Vector3 pos = GetV(a, ref o);
                    Quaternion rot = new Quaternion(GetF(a, ref o), GetF(a, ref o), GetF(a, ref o), GetF(a, ref o));
                    ApplyPieceMove(id, pos, rot);
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
                int pc = 0;
                ScanPieces(true);
                foreach (var kv in _pieces)
                {
                    var p = kv.Value; if (p == null) continue;
                    var tr = p.transform; if (tr == null) continue;
                    SendPieceMove(kv.Key, tr.position, tr.rotation, true);
                    pc++;
                }
                if (n == 0 && mk == 0 && pc == 0) Log.LogInfo("[map] JIP snapshot: nothing to send (no tokens/markers/pieces present)");
                else Log.LogInfo($"[map] sent JIP snapshot -> peer ({n} tokens, {mk} markers, {pc} pieces)");
            }
            catch (Exception e) { Log.LogWarning("[map] snapshot: " + e.Message); }
        }

        // ---------------- markers: local detection + spawn/send ----------------

        // Detect the LOCAL player creating or deleting a bearing/range marker by diffing the placer's
        // placedMarkers list against what we already track. A GameObject we don't recognise = the local player
        // just finalized a new marker (send ADD); a tracked marker that has left the list = it was deleted here
        // (send DEL). Markers we MIRRORED from the peer are registered with their instanceId, so they're never
        // mistaken for new local placements. Either player may create or delete; deletes propagate both ways.
        // Keep a FNV(path) -> MapPiece3D registry fresh so a received MSG_PIECE_MOVE can find the piece to move.
        // Both sides scan (the receiver didn't drag the piece, so it must discover them independently).
        private static void ScanPieces(bool force)
        {
            if (!force && Time.unscaledTime < _nextPieceScan) return;
            _nextPieceScan = Time.unscaledTime + 3f;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapPiece3D>(), FindObjectsSortMode.None);
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                {
                    var p = arr[i].TryCast<MapPiece3D>(); if (p == null) continue;
                    var tr = p.transform; if (tr == null) continue;
                    _pieces[Fnv(PathOf(tr))] = p;
                }
            }
            catch (Exception e) { Log.LogWarning("[map] scan pieces: " + e.Message); }
        }

        private static void ApplyPieceMove(int id, Vector3 pos, Quaternion rot)
        {
            try
            {
                if (!Finite(pos)) return;
                if (_draggingLocal.Contains(id)) return;   // we're dragging this piece locally — local input wins, ignore the peer
                if (!_pieces.TryGetValue(id, out var p) || p == null) { ScanPieces(true); _pieces.TryGetValue(id, out p); }
                if (p == null) { Log.LogWarning($"[map] piece move for unknown id={id} — not in this scene (graph-spawned + gated away on the client?)"); return; }
                var tr = p.transform; if (tr == null) return;
                tr.SetPositionAndRotation(pos, rot);
                Log.LogInfo($"[map] applied remote piece move '{tr.name}' <- peer");
            }
            catch (Exception e) { Log.LogWarning("[map] apply piece move: " + e.Message); }
        }

        private static void SendPieceMove(int id, Vector3 pos, Quaternion rot, bool reliable)
        {
            if (!EnsureBuf() || !Finite(pos)) return;
            int o = 0; _buf[o++] = MSG_PIECE_MOVE; o = PutInt(o, id); o = PutV(o, pos);
            o = PutF(o, rot.x); o = PutF(o, rot.y); o = PutF(o, rot.z); o = PutF(o, rot.w);
            CoopP2P.Send(_buf, o, reliable);
        }

        // Detect a bearing/range line deleted locally (e.g. the map's reset/clear, or an individual delete) by
        // diffing our tracked markers against the placer's live placedMarkers list, and push MSG_MARKER_DEL so the
        // peer drops its mirror. ADDs come from the FinalizePlacement hook; this poll covers the destroy side, which
        // the game exposes no convenient hook for. Either side may delete (locals OR mirrors): when we receive a DEL
        // we untrack the marker BEFORE its GameObject leaves placedMarkers, so this never bounces the delete back.
        // The Confirmed gate avoids a spurious DEL in the gap between FinalizePlacement firing and the game adding
        // the new marker to placedMarkers.
        private static void DetectMarkerDeletes()
        {
            if (_placer == null || _markers.Count == 0) return;
            if (Time.unscaledTime < _nextDelScan) return;
            _nextDelScan = Time.unscaledTime + 0.3f;
            try
            {
                _seen.Clear();
                Il2CppSystem.Collections.Generic.List<GameObject> pm = null;
                try { pm = _placer.placedMarkers; } catch { }
                if (pm != null)
                    for (int i = 0; i < pm.Count; i++)
                    {
                        var go = pm[i]; if (go == null) continue;
                        try { _seen.Add(go.GetInstanceID()); } catch { }
                    }

                _toRemove.Clear();
                foreach (var kv in _markers)
                {
                    var m = kv.Value;
                    if (_seen.Contains(m.InstanceId)) { m.Confirmed = true; continue; }
                    bool gone = (m.Go == null) || m.Confirmed;   // destroyed, or it WAS in the list and now isn't
                    if (gone) _toRemove.Add(kv.Key);
                }

                for (int i = 0; i < _toRemove.Count; i++)
                {
                    int netId = _toRemove[i];
                    if (!_markers.TryGetValue(netId, out var m)) continue;
                    SendMarkerDel(netId);
                    _byInstance.Remove(m.InstanceId);
                    _markers.Remove(netId);
                    Log.LogInfo($"[map] local marker deleted -> peer (id={netId})");
                }
            }
            catch (Exception e) { Log.LogWarning("[map] detect deletes: " + e.Message); }
        }

        // Peer → mirror a finalized marker: instantiate the same prefab, replay the geometry, finalize, and add
        // it to placedMarkers so it's a first-class marker locally (hoverable/deletable, and that delete
        // propagates back). Registered with its instanceId so our own detection won't re-broadcast it.
        // Mirror a remote line. Mirrors the reference's ApplyDrawLine exactly: instantiate the by-NAME prefab UNDER
        // the placer's mapRect, set the new line's own rect anchoredPosition = origin, then Initialize/UpdateLine/
        // FinalizePlacement on that own rect with the ABSOLUTE target. The _applyingNetworkLine guard stops our own
        // FinalizePlacement from bouncing back through the finalize hook.
        private static void SpawnMirror(int netId, string prefabName, Vector2 origin, Vector2 target)
        {
            if (_placer == null) { Log.LogWarning($"[map] marker add (id={netId}) but no MapMarkerPlacer in scene"); return; }
            try
            {
                GameObject prefab = ResolveMarkerPrefab(prefabName);   // by NAME → correct COLOR
                if (prefab == null) { Log.LogWarning($"[map] marker add (id={netId}): no prefab named '{prefabName}'"); return; }
                RectTransform mapRect = null;
                try { mapRect = _placer.mapRect; } catch { }
                if (mapRect == null) { Log.LogWarning($"[map] marker add (id={netId}): placer has no mapRect"); return; }

                _applyingNetworkLine = true;
                try
                {
                    var inst = UnityEngine.Object.Instantiate(prefab, mapRect).TryCast<GameObject>();
                    if (inst == null) { Log.LogWarning($"[map] marker add (id={netId}): instantiate failed"); return; }
                    try { inst.SetActive(true); } catch { }
                    MapMarkerLineUI ui = null;
                    try { ui = inst.GetComponent<MapMarkerLineUI>(); } catch { }
                    if (ui == null) { Log.LogWarning($"[map] marker add (id={netId}): prefab has no MapMarkerLineUI"); try { UnityEngine.Object.Destroy(inst); } catch { } return; }
                    RectTransform rect = null; try { rect = inst.GetComponent<RectTransform>(); } catch { }
                    try { if (rect != null) { rect.anchoredPosition = origin; rect.localRotation = Quaternion.identity; } } catch { }

                    try { ui.Initialize(origin, rect); } catch (Exception e) { Log.LogWarning("[map] marker Initialize: " + e.Message); }
                    try { ui.UpdateLine(origin, target, rect); } catch (Exception e) { Log.LogWarning("[map] marker UpdateLine: " + e.Message); }
                    try { ui.FinalizePlacement(); } catch (Exception e) { Log.LogWarning("[map] marker FinalizePlacement: " + e.Message); }
                    try { var pm = _placer.placedMarkers; if (pm != null) pm.Add(inst); } catch { }

                    int iid = inst.GetInstanceID();
                    _markers[netId] = new Marker { NetId = netId, Go = inst, Ui = ui, InstanceId = iid, PrefabName = prefabName, IsLocal = false };
                    _byInstance[iid] = netId;
                    Log.LogInfo($"[map] mirrored remote marker <- peer (id={netId} prefab='{prefabName}' origin={origin} target={target})");
                }
                finally { _applyingNetworkLine = false; }
            }
            catch (Exception e) { Log.LogWarning("[map] spawn mirror: " + e.Message); }
        }

        private static void SendMarkerAdd(int netId, string prefabName, Vector2 origin, Vector2 target)
        {
            if (!EnsureBuf()) return;
            if (!Finite(origin.x) || !Finite(origin.y) || !Finite(target.x) || !Finite(target.y)) return;
            int o = 0; _buf[o++] = MSG_MARKER_ADD; o = PutInt(o, netId); o = PutStr(o, prefabName);
            o = PutF(o, origin.x); o = PutF(o, origin.y); o = PutF(o, target.x); o = PutF(o, target.y);
            CoopP2P.Send(_buf, o, true);
        }

        // JIP re-send: recompute the absolute target from the live marker.
        private static void SendMarkerAdd(Marker m)
        {
            if (m == null || m.Ui == null) return;
            Vector2 origin, target;
            try { origin = m.Ui.OriginLocal; var t = m.Ui.TipLocalPosition; target = origin + new Vector2(t.x, t.y); } catch { return; }
            SendMarkerAdd(m.NetId, m.PrefabName, origin, target);
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
            _draggingLocal.Clear();
            ClearMarkers(true);   // co-op ended → drop the peer's mirrored markers; locals re-detect on reconnect
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            int local = 0, remote = 0;
            foreach (var it in _items.Values) { if (it.LocalOwned) local++; if (it.RemoteOwned) remote++; }
            int mLocal = 0, mRemote = 0;
            foreach (var m in _markers.Values) { if (m.IsLocal) mLocal++; else mRemote++; }
            return $"map: {_items.Count} tokens, {_slots.Count} slots, {_pieces.Count} pieces, boardRef={_ref != null}, owned local={local} remote={remote} | " +
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
            DumpSceneObjects();
        }

        // One-shot structural probe (F4): enumerate every map-related object actually live in the CURRENT scene so
        // we can see how the fire-mission tactical map is built (vs the operations map this module was written for).
        // Run on BOTH host and client and diff: tells us which token/screw/line types exist, whether they're active
        // or graph-spawned-and-absent, and what their parent frame is — the data needed to sync them correctly.
        public static void DumpSceneObjects()
        {
            Log.LogInfo("[map-probe] ===== map-object probe (current scene) =====");
            Probe("MapPiece3D", Il2CppType.Of<MapPiece3D>(), true);
            Probe("DraggableItem", Il2CppType.Of<DraggableItem>(), true);
            Probe("SurfaceHandoffDraggable3D", Il2CppType.Of<SurfaceHandoffDraggable3D>(), true);
            Probe("MapMarkerLineUI", Il2CppType.Of<MapMarkerLineUI>(), false);
            try
            {
                var pls = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapMarkerPlacer>(), FindObjectsSortMode.None);
                int n = pls != null ? pls.Length : 0;
                Log.LogInfo($"[map-probe] MapMarkerPlacer: {n} active-in-scene");
                if (pls != null) for (int i = 0; i < n; i++)
                {
                    var p = pls[i].TryCast<MapMarkerPlacer>(); if (p == null) continue;
                    int pm = 0; bool hasRect = false; string ap = "?";
                    try { pm = p.placedMarkers != null ? p.placedMarkers.Count : -1; } catch { }
                    try { hasRect = p.mapRect != null; } catch { }
                    try { ap = p.activeMarkerPrefab != null ? p.activeMarkerPrefab.name : "<none>"; } catch { }
                    Log.LogInfo($"[map-probe]    placer '{p.gameObject.name}' placedMarkers={pm} mapRect={hasRect} activePrefab='{ap}'");
                }
            }
            catch (Exception e) { Log.LogWarning("[map-probe] placer: " + e.Message); }
            Log.LogInfo("[map-probe] ===== end probe =====");
        }

        private static void Probe(string label, Il2CppSystem.Type t, bool withPos)
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
                int total = 0; try { var all = Resources.FindObjectsOfTypeAll(t); total = all != null ? all.Length : 0; } catch { }
                int active = arr != null ? arr.Length : 0;
                Log.LogInfo($"[map-probe] {label}: {active} active-in-scene, {total} loaded(incl inactive/prefab)");
                if (arr != null) for (int i = 0; i < arr.Length && i < 25; i++)
                {
                    var comp = arr[i].TryCast<Component>(); if (comp == null) continue;
                    string nm = "?", par = "?", pos = "";
                    try { nm = comp.gameObject.name; } catch { }
                    try { var p = comp.transform.parent; par = p != null ? p.name : "<root>"; } catch { }
                    bool act = false; try { act = comp.gameObject.activeInHierarchy; } catch { }
                    if (withPos) { try { var v = comp.transform.position; pos = $" pos=({v.x:0.0},{v.y:0.0},{v.z:0.0})"; } catch { } }
                    Log.LogInfo($"[map-probe]    '{nm}' active={act} parent='{par}'{pos}");
                }
            }
            catch (Exception e) { Log.LogWarning($"[map-probe] {label}: " + e.Message); }
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
