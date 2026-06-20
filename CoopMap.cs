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
    /// The dynamically-spawned bearing/range markers (MapMarkerLineUI) have a create/destroy lifecycle and are
    /// a separate follow-up; the mission-spawned MapEntity targets are host-authoritative (Phase 4).
    /// </summary>
    internal static class CoopMap
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_GRAB = 10;   // [t][itemId i32]                          reliable
        public const byte MSG_POS = 11;    // [t][itemId i32][x,y,z f32]  (board-local) unreliable
        public const byte MSG_PLACE = 12;  // [t][itemId i32][x,y,z f32][slotId i32]   reliable

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

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopMapSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { if (_items.Count > 0) ClearOwnership(); return; }
            try
            {
                EnsureRegistry();
                if (_ref == null || _items.Count == 0) return;

                float now = Time.unscaledTime;
                bool sendNow = Config.CoopSendHz <= 0f || now >= _nextSend;
                if (sendNow && Config.CoopSendHz > 0f) _nextSend = now + 1f / Config.CoopSendHz;

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
                if (_ref == null || _items.Count == 0) { Log.LogInfo("[map] JIP snapshot: no map board present — nothing to send"); return; }
                int n = 0;
                foreach (var it in _items.Values)
                {
                    if (it.Token == null || it.T == null) continue;
                    SendPlace(it, false);
                    n++;
                }
                Log.LogInfo($"[map] sent JIP snapshot -> peer ({n} tokens)");
            }
            catch (Exception e) { Log.LogWarning("[map] snapshot: " + e.Message); }
        }

        // ---------------- registry ----------------

        private static void EnsureRegistry()
        {
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
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            int local = 0, remote = 0;
            foreach (var it in _items.Values) { if (it.LocalOwned) local++; if (it.RemoteOwned) remote++; }
            return $"map: {_items.Count} tokens, {_slots.Count} slots, boardRef={_ref != null}, owned local={local} remote={remote}";
        }

        public static void Dump()
        {
            Log.LogInfo("[map] " + Status());
            foreach (var it in _items.Values)
            {
                if (it.LocalOwned) Log.LogInfo($"[map]   LOCAL-owned: '{it.T.name}'");
                else if (it.RemoteOwned) Log.LogInfo($"[map]   remote-owned: '{it.T.name}' localPos={it.RemoteLocal}");
            }
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
            try { _buf = new Il2CppStructArray<byte>(32); return true; }
            catch (Exception e) { Log.LogWarning("[map] buf: " + e.Message); return false; }
        }

        private static int PutInt(int o, int v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }
        private static int PutF(int o, float v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }
        private static int PutV(int o, Vector3 v) { o = PutF(o, v.x); o = PutF(o, v.y); o = PutF(o, v.z); return o; }

        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_f4, 0); }
        private static float GetF(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToSingle(_f4, 0); }
        private static Vector3 GetV(Il2CppStructArray<byte> a, ref int o) { float x = GetF(a, ref o), y = GetF(a, ref o), z = GetF(a, ref o); return new Vector3(x, y, z); }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    }
}
