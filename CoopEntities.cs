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
    /// Phase 4 co-op (increment 4b): replicate the host's mission ENTITIES (enemies / targets) to the client.
    ///
    /// Pairs with <see cref="CoopSim"/> (4a): the client's enemy/target SPAWN node is gated OFF (narrow gate),
    /// so it authors no enemies locally and instead MIRRORS the host's. (The rest of the client's mission sim
    /// runs normally now.) Enemies are data records (<c>MapEntity</c>), each mirrored
    /// in-scene by an <c>EntityLocation</c> MonoBehaviour. There is NO entity registry/factory in the demo, so:
    ///   • the HOST enumerates <c>FindObjectsByType&lt;EntityLocation&gt;()</c> → <c>.Entity</c>, diffs by a
    ///     stable hash of the MapEntity's string <c>ID</c>, and broadcasts SPAWN (full record) / UPDATE
    ///     (position + state + health) / DESPAWN. Diff sends mean a static entity costs one SPAWN then silence.
    ///   • the CLIENT applies them: for an ID it doesn't have it ADOPTS a same-ID scene entity if one already
    ///     exists (shared pre-placed fire-mission targets live in both scenes), otherwise CLONES a cached
    ///     <c>EntityLocation</c> template and binds a fresh MapEntity via the verified recipe
    ///     <c>Init(e)</c> → <c>RecalculateAndRegister(true)</c> (see ironnest-phase4-entities memory).
    ///
    /// SCOPED TO <c>GamePhase.MissionActive</c> on BOTH sides, so the validated hub co-op is never touched and
    /// no entity traffic flows outside an actual mission. Host-authoritative: the client never authors entities.
    ///
    /// UNVERIFIED until a 2-player mission test (logged heavily so the first run tells us the truth):
    ///   (1) a gated client in a mission scene having an EntityLocation to clone — we cache a template from any
    ///       scene that has one (e.g. the hub's ~14) and keep it across loads, but the CLONE PARENT must be
    ///       resolved in the live scene (EntityLocation positions itself in a root canvas).
    ///   (2) the exact reposition call (we set MapEntity.Position + EntityLocation.LocalPosition + re-register).
    ///   (3) damage is mirrored as raw Health/State (no ShellDefinition replay yet — that's a 4c refinement).
    /// Also UNSOLVED companion piece: getting both players INTO the same mission scene (mission-start / scene
    /// transition replication) — entity sync assumes they're already co-located in a MissionActive scene.
    /// </summary>
    internal static class CoopEntities
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_SPAWN = 16;    // [t][key i32][id str][name str][icon str][role i32][pos 3f][state i32][hp i32][maxHp i32][armour i32][stars i32][scale i32]  reliable
        public const byte MSG_UPDATE = 17;   // [t][key i32][seq i32][pos 3f][state i32][hp i32]  RELIABLE — discrete state/hp change OR periodic position keyframe (REVIEW-fix P2)
        public const byte MSG_DESPAWN = 18;  // [t][key i32]                              reliable
        public const byte MSG_MOVE = 23;     // [t][key i32][seq i32][pos 3f]            UNRELIABLE — position stream; per-entity seq drops late/reordered moves (REVIEW-fix P2)
        // REVIEW-fix (P1): movement is a SEPARATE position-only packet so a reordered/stale unreliable move can
        // never carry old state/hp and roll back a newer reliable damage/death. Discrete state/hp travels ONLY
        // on the reliable+ordered MSG_UPDATE; MSG_MOVE touches position alone (self-correcting next frame).

        private sealed class SentState
        {
            public string ID;
            public Vector3 Pos;
            public int State;
            public int Health;
            public int Seq;            // REVIEW-fix (P2): per-entity monotonic seq across move+update (position ordering)
            public float LastKeyframe; // last time we sent a reliable position keyframe for this entity
        }

        private sealed class Mirror
        {
            public int Key;
            public string ID;
            public GameObject Go;
            public EntityLocation Loc;
            public MapEntity Entity;
            public bool IsClone;          // we instantiated it (destroy on despawn) vs adopted a scene entity
            public int LastState;
            public int LastSeq;           // REVIEW-fix (P2): highest position seq applied (drops late/reordered moves)
        }

        // Host: last broadcast state per entity (diff source). Client: live mirrors.
        private static readonly Dictionary<int, SentState> _sent = new Dictionary<int, SentState>();
        private static readonly Dictionary<int, Mirror> _mirrors = new Dictionary<int, Mirror>();
        private static readonly List<int> _toRemove = new List<int>();
        private static readonly HashSet<int> _seen = new HashSet<int>();

        // Desync detector: entities this side currently tracks (host = broadcast set, client = mirrors); should match.
        public static int LocalEntityCount => CoopP2P.IsHost ? _sent.Count : _mirrors.Count;

        // Spawns that arrived before a clone template was available (a gated client just entered the mission scene
        // and hasn't found anything to clone yet). The host diff-sends each entity exactly ONCE, so a dropped SPAWN
        // never comes back on its own — we queue them and ClientTick replays them the moment a template appears.
        private sealed class PendingSpawn { public int Key; public string ID, Name, Icon; public int Role, State, Hp, MaxHp, Armour, Stars, Scale; public Vector3 Pos; }
        private static readonly List<PendingSpawn> _pendingSpawns = new List<PendingSpawn>();

        // Client clone template (a disabled EntityLocation copy kept across scene loads, since a gated client
        // may enter a mission scene with no EntityLocation of its own to clone).
        private static GameObject _template;
        private static float _nextTemplateTry;

        private static float _nextSend;
        private const float MoveEpsilonSq = 0.0001f;   // ~1cm map-units; ignore sub-pixel jitter

        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];
        private static readonly byte[] _scratch = new byte[512];

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopEntitySync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { if (_sent.Count > 0 || _mirrors.Count > 0) ClearAll(); return; }
            try
            {
                if (CoopP2P.IsHost) HostTick();
                else ClientTick();
            }
            catch (Exception e) { Log.LogWarning("[ent] tick: " + e.Message); }
        }

        // ---------------- host: enumerate + diff + broadcast ----------------

        private static void HostTick()
        {
            float now = Time.unscaledTime;
            bool sendNow = Config.CoopSendHz <= 0f || now >= _nextSend;
            if (!sendNow) return;
            if (Config.CoopSendHz > 0f) _nextSend = now + 1f / Config.CoopSendHz;

            if (!InMission())
            {
                if (_sent.Count > 0) { foreach (var k in _sent.Keys) SendDespawn(k); _sent.Clear(); Log.LogInfo("[ent] left mission — despawned all replicated entities"); }
                return;
            }

            Il2CppArrayBase<UnityEngine.Object> arr = null;
            try { arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None); } catch { return; }
            if (arr == null) return;

            _seen.Clear();
            for (int i = 0; i < arr.Length; i++)
            {
                EntityLocation loc = null;
                try { loc = arr[i].TryCast<EntityLocation>(); } catch { }
                if (loc == null) continue;
                MapEntity e = null;
                try { e = loc.Entity; } catch { }
                if (e == null) continue;
                string id; try { id = e.ID; } catch { continue; }
                if (string.IsNullOrEmpty(id)) continue;

                int key = Fnv(id);
                _seen.Add(key);

                Vector3 pos; int state, hp;
                try { pos = e.Position; state = (int)e.State; hp = e.Health; } catch { continue; }

                if (!_sent.TryGetValue(key, out var s))
                {
                    SendSpawn(key, e);
                    _sent[key] = new SentState { ID = id, Pos = pos, State = state, Health = hp, Seq = 0, LastKeyframe = now };
                }
                else
                {
                    bool discrete = s.State != state || s.Health != hp;
                    bool moved = (s.Pos - pos).sqrMagnitude > MoveEpsilonSq;
                    // REVIEW-fix (P2): a reliable position keyframe every CoopEntityKeyframeSec bounds drift after a
                    // lost unreliable move; a discrete state/hp change is itself a reliable keyframe (carries pos+seq),
                    // so fold them together. Plain moves stay unreliable in between. Every send bumps the shared seq.
                    bool keyframe = Config.CoopEntityKeyframeSec > 0f && now - s.LastKeyframe >= Config.CoopEntityKeyframeSec;
                    if (discrete || keyframe)
                    {
                        s.Seq++; SendUpdate(key, pos, state, hp, s.Seq);
                        if (discrete) Log.LogInfo($"[ent] '{s.ID}' state/hp -> peer (state={state} hp={hp})");
                        s.State = state; s.Health = hp; s.Pos = pos; s.LastKeyframe = now;
                    }
                    else if (moved) { s.Seq++; SendMove(key, pos, s.Seq); s.Pos = pos; }
                }
            }

            _toRemove.Clear();
            foreach (var kv in _sent) if (!_seen.Contains(kv.Key)) _toRemove.Add(kv.Key);
            for (int i = 0; i < _toRemove.Count; i++)
            {
                int k = _toRemove[i];
                string id = _sent.TryGetValue(k, out var ss) ? ss.ID : "?";
                _sent.Remove(k);
                SendDespawn(k);
                Log.LogInfo($"[ent] '{id}' despawned -> peer");
            }
        }

        // ---------------- client: maintain template + cleanup ----------------

        private static void ClientTick()
        {
            // Drop cloned mirrors when we leave the mission (host stops sending; the scene tore down anyway).
            if (!InMission())
            {
                if (_mirrors.Count > 0 || _pendingSpawns.Count > 0) { ClearMirrors(); _pendingSpawns.Clear(); Log.LogInfo("[ent] left mission — cleared mirrored entities"); }
                return;
            }
            // Opportunistically cache a clone template from any EntityLocation we can see (kept across scenes).
            if (_template == null && Time.unscaledTime >= _nextTemplateTry)
            {
                _nextTemplateTry = Time.unscaledTime + 2f;
                TryCacheTemplate();
            }
            // Once a template exists, replay any spawns that arrived before it (else the first host burst is lost).
            if (_template != null && _pendingSpawns.Count > 0) DrainPendingSpawns();
        }

        private static void DrainPendingSpawns()
        {
            var pend = _pendingSpawns.ToArray();
            _pendingSpawns.Clear();
            int n = 0;
            foreach (var p in pend)
            {
                if (_mirrors.ContainsKey(p.Key)) continue;
                AdoptOrClone(p.Key, p.ID, p.Name, p.Icon, p.Role, p.Pos, p.State, p.Hp, p.MaxHp, p.Armour, p.Stars, p.Scale);
                n++;
            }
            if (n > 0) Log.LogInfo($"[ent] replayed {n} queued spawn(s) now that a clone template is available");
        }

        // ---------------- receive (client) ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            if (CoopP2P.IsHost) return;   // host is authoritative; never mirrors
            // Drop entity traffic until our own scene reaches the mission — otherwise we'd clone into the wrong
            // scene. CoopScene re-requests a full snapshot (via MISSION_READY) once we've actually loaded in.
            if (!InMission()) return;
            int o = 1;
            switch (type)
            {
                case MSG_SPAWN:
                {
                    int key = GetInt(a, ref o);
                    string id = GetStr(a, ref o, len);
                    string name = GetStr(a, ref o, len);
                    string icon = GetStr(a, ref o, len);
                    if (o + 4 + 12 + 12 > len) return;
                    int role = GetInt(a, ref o);
                    Vector3 pos = GetV(a, ref o);
                    int state = GetInt(a, ref o);
                    int hp = GetInt(a, ref o);
                    int maxHp = GetInt(a, ref o);
                    int armour = GetInt(a, ref o);
                    int stars = GetInt(a, ref o);
                    int scale = GetInt(a, ref o);
                    if (id == null) return;
                    if (_mirrors.TryGetValue(key, out var ex)) { ApplyUpdate(ex, pos, state, hp); return; }   // JIP/dup re-spawn
                    AdoptOrClone(key, id, name, icon, role, pos, state, hp, maxHp, armour, stars, scale);
                    break;
                }
                case MSG_UPDATE:
                {
                    if (len < 1 + 4 + 4 + 12 + 4 + 4) return;
                    int key = GetInt(a, ref o);
                    int seq = GetInt(a, ref o);
                    Vector3 pos = GetV(a, ref o);
                    int state = GetInt(a, ref o);
                    int hp = GetInt(a, ref o);
                    // Reliable+ordered: always apply state/hp/pos, and advance the position seq so an older unreliable
                    // move delivered late is dropped instead of yanking the entity back (REVIEW-fix P2).
                    if (_mirrors.TryGetValue(key, out var m)) { ApplyUpdate(m, pos, state, hp); if (seq > m.LastSeq) m.LastSeq = seq; }
                    break;
                }
                case MSG_MOVE:
                {
                    if (len < 1 + 4 + 4 + 12) return;
                    int key = GetInt(a, ref o);
                    int seq = GetInt(a, ref o);
                    Vector3 pos = GetV(a, ref o);
                    // Drop a stale/reordered move so a late unreliable packet can't settle the entity at an old
                    // position (REVIEW-fix P2). Position-only either way — never touches state/hp.
                    if (_mirrors.TryGetValue(key, out var m) && seq > m.LastSeq) { ApplyMove(m, pos); m.LastSeq = seq; }
                    break;
                }
                case MSG_DESPAWN:
                {
                    if (len < 5) return;
                    int key = GetInt(a, ref o);
                    if (_mirrors.TryGetValue(key, out var m))
                    {
                        if (m.IsClone) { try { if (m.Go != null) UnityEngine.Object.Destroy(m.Go); } catch { } }
                        else { try { if (m.Go != null) UnityEngine.Object.Destroy(m.Go); } catch { } }   // adopted scene entity also dies when the host says so
                        _mirrors.Remove(key);
                        Log.LogInfo($"[ent] despawned '{m.ID}' <- peer");
                    }
                    break;
                }
            }
        }

        private static void AdoptOrClone(int key, string id, string name, string icon, int role, Vector3 pos, int state, int hp, int maxHp, int armour, int stars, int scale)
        {
            // Already present locally as a shared/pre-placed scene entity? Adopt it (don't duplicate).
            var existing = FindLocalEntityById(id);
            if (existing != null)
            {
                MapEntity le = null; try { le = existing.Entity; } catch { }
                var am = new Mirror { Key = key, ID = id, Go = existing.gameObject, Loc = existing, Entity = le, IsClone = false, LastState = state };
                _mirrors[key] = am;
                ApplyUpdate(am, pos, state, hp);
                Log.LogInfo($"[ent] adopted local entity '{id}' <- peer (role={role})");
                return;
            }

            // Otherwise clone the cached template.
            var tmpl = _template;
            if (tmpl == null) { TryCacheTemplate(); tmpl = _template; }
            if (tmpl == null)
            {
                if (!PendingContains(key))
                    _pendingSpawns.Add(new PendingSpawn { Key = key, ID = id, Name = name, Icon = icon, Role = role, Pos = pos, State = state, Hp = hp, MaxHp = maxHp, Armour = armour, Stars = stars, Scale = scale });
                Log.LogInfo($"[ent] no clone template yet — queued '{id}' ({_pendingSpawns.Count} pending; replays when a source appears)");
                return;
            }

            GameObject go = null;
            try { go = UnityEngine.Object.Instantiate(tmpl).TryCast<GameObject>(); } catch (Exception ex) { Log.LogWarning("[ent] instantiate: " + ex.Message); }
            if (go == null) { Log.LogWarning($"[ent] clone failed for '{id}'"); return; }
            try { go.SetActive(true); } catch { }
            var parent = ResolveEntityParent();
            if (parent != null) { try { go.transform.SetParent(parent, false); } catch { } }

            EntityLocation loc = null;
            try { loc = go.GetComponent<EntityLocation>(); } catch { }
            if (loc == null) { Log.LogWarning($"[ent] clone of '{id}' has no EntityLocation"); try { UnityEngine.Object.Destroy(go); } catch { } return; }

            MapEntity e;
            try
            {
                e = new MapEntity();
                e.ID = id; e.Name = name ?? id; e.Icon = icon ?? "";
                e.Role = (EntityRoles)role; e.Position = pos; e.State = (MapEntityStates)state;
                e.Health = hp; e.MaxHealth = maxHp; e.Armour = armour; e.Stars = stars; e.Scale = scale;
            }
            catch (Exception ex) { Log.LogWarning("[ent] build MapEntity: " + ex.Message); try { UnityEngine.Object.Destroy(go); } catch { } return; }

            try { loc.Init(e); } catch (Exception ex) { Log.LogWarning("[ent] Init: " + ex.Message); }
            try { loc.RecalculateAndRegister(true); } catch (Exception ex) { Log.LogWarning("[ent] register: " + ex.Message); }
            try { loc.LocalPosition = new Vector2(pos.x, pos.y); } catch { }

            _mirrors[key] = new Mirror { Key = key, ID = id, Go = go, Loc = loc, Entity = e, IsClone = true, LastState = state };
            Log.LogInfo($"[ent] cloned remote entity '{id}' <- peer (role={role} hp={hp}/{maxHp} parent={(parent != null ? parent.name : "<none>")})");
        }

        // Position-only apply for the unreliable MSG_MOVE stream. Deliberately does NOT read or write state/hp,
        // so a late/reordered move can only nudge position (corrected by the next move), never revert authoritative
        // damage/death that arrived on the reliable MSG_UPDATE channel.
        private static void ApplyMove(Mirror m, Vector3 pos)
        {
            if (m.Loc == null && m.Entity == null) return;
            try
            {
                var e = m.Entity; if (e == null && m.Loc != null) { try { e = m.Loc.Entity; m.Entity = e; } catch { } }
                if (e != null) { try { e.Position = pos; } catch { } }
                try { if (m.Loc != null) m.Loc.LocalPosition = new Vector2(pos.x, pos.y); } catch { }
                try { if (m.Loc != null) m.Loc.RecalculateAndRegister(false); } catch { }
            }
            catch (Exception ex) { Log.LogWarning("[ent] applyMove: " + ex.Message); }
        }

        private static void ApplyUpdate(Mirror m, Vector3 pos, int state, int hp)
        {
            if (m.Loc == null && m.Entity == null) return;
            try
            {
                var e = m.Entity; if (e == null && m.Loc != null) { try { e = m.Loc.Entity; m.Entity = e; } catch { } }
                if (e != null)
                {
                    try { e.Position = pos; } catch { }
                    try { e.Health = hp; } catch { }
                    if (m.LastState != state)
                    {
                        var old = (MapEntityStates)m.LastState; var nw = (MapEntityStates)state;
                        try { e.State = nw; } catch { }
                        try { if (m.Loc != null) m.Loc.OnEntityStateChanged(old, nw); } catch (Exception ex) { Log.LogWarning("[ent] stateChanged: " + ex.Message); }
                        m.LastState = state;
                    }
                }
                try { if (m.Loc != null) m.Loc.LocalPosition = new Vector2(pos.x, pos.y); } catch { }
                try { if (m.Loc != null) m.Loc.RecalculateAndRegister(false); } catch { }
            }
            catch (Exception ex) { Log.LogWarning("[ent] applyUpdate: " + ex.Message); }
        }

        // ---------------- join-in-progress ----------------

        // Host → new joiner: re-broadcast every current entity as a SPAWN so a mid-mission joiner sees the live
        // battlefield. Reuses the diff record so the client adopts-or-clones idempotently.
        public static void SendSnapshot()
        {
            if (!Config.CoopEntitySync || !CoopP2P.IsHost) return;
            if (!InMission()) { Log.LogInfo("[ent] JIP snapshot: not in a mission — no entities to send"); return; }
            int n = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    MapEntity e = null; try { e = loc.Entity; } catch { }
                    if (e == null) continue;
                    string id; try { id = e.ID; } catch { continue; }
                    if (string.IsNullOrEmpty(id)) continue;
                    int key = Fnv(id);
                    SendSpawn(key, e);
                    if (!_sent.ContainsKey(key)) { try { _sent[key] = new SentState { ID = id, Pos = e.Position, State = (int)e.State, Health = e.Health }; } catch { } }
                    n++;
                }
            }
            catch (Exception ex) { Log.LogWarning("[ent] snapshot: " + ex.Message); }
            Log.LogInfo($"[ent] sent JIP snapshot -> peer ({n} entities)");
        }

        // ---------------- send ----------------

        private static void SendSpawn(int key, MapEntity e)
        {
            if (!EnsureBuf()) return;
            string id, name, icon; int role, state, hp, maxHp, armour, stars, scale; Vector3 pos;
            try
            {
                id = e.ID; name = e.Name; icon = e.Icon; role = (int)e.Role; pos = e.Position;
                state = (int)e.State; hp = e.Health; maxHp = e.MaxHealth; armour = e.Armour; stars = e.Stars; scale = e.Scale;
            }
            catch { return; }
            int o = 0; _buf[o++] = MSG_SPAWN; o = PutInt(o, key);
            o = PutStr(o, id); o = PutStr(o, name); o = PutStr(o, icon);
            o = PutInt(o, role); o = PutV(o, pos); o = PutInt(o, state); o = PutInt(o, hp);
            o = PutInt(o, maxHp); o = PutInt(o, armour); o = PutInt(o, stars); o = PutInt(o, scale);
            CoopP2P.Send(_buf, o, true);
            Log.LogInfo($"[ent] spawn '{id}' -> peer (role={role} hp={hp}/{maxHp} state={state})");
        }

        // Discrete state/hp change OR periodic position keyframe — ALWAYS reliable (and Steam-ordered), so it can't
        // be overtaken by a stale move. Carries the shared position seq so the client can order it against moves.
        private static void SendUpdate(int key, Vector3 pos, int state, int hp, int seq)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_UPDATE; o = PutInt(o, key); o = PutInt(o, seq); o = PutV(o, pos); o = PutInt(o, state); o = PutInt(o, hp);
            CoopP2P.Send(_buf, o, true);
        }

        // High-rate position stream — unreliable, position only (no state/hp). Per-entity seq lets the receiver
        // drop a late/reordered move (REVIEW-fix P1/P2).
        private static void SendMove(int key, Vector3 pos, int seq)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_MOVE; o = PutInt(o, key); o = PutInt(o, seq); o = PutV(o, pos);
            CoopP2P.Send(_buf, o, false);
        }

        private static void SendDespawn(int key)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_DESPAWN; o = PutInt(o, key);
            CoopP2P.Send(_buf, o, true);
        }

        // ---------------- template / parent / lookup helpers ----------------

        private static void TryCacheTemplate()
        {
            try
            {
                // Resources.FindObjectsOfTypeAll (NOT FindObjectsByType) so we also see INACTIVE EntityLocations and
                // PREFAB assets. A gated client's mission scene spawns no active entities of its own, so the only
                // clone source is the spawn prefab / an inactive template — which the active-only scan always missed
                // (the "cannot mirror … no template" failure in the first 2-player mission test).
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<EntityLocation>());
                if (arr == null) return;
                GameObject best = null;
                for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    var go = loc.gameObject; if (go == null) continue;
                    if (IsMirrorGo(go)) continue;                       // don't clone one of our own clones
                    string nm = null; try { nm = go.name; } catch { }
                    if (nm != null && nm.Contains("CoopEntityTemplate")) continue;   // skip our own cached template
                    best = go;
                    if (nm == null || nm.IndexOf("(Clone)", StringComparison.Ordinal) < 0) break;   // prefer a prefab/source over a live clone
                }
                if (best == null) return;
                var tmpl = UnityEngine.Object.Instantiate(best).TryCast<GameObject>();
                if (tmpl == null) return;
                try { tmpl.SetActive(false); } catch { }
                try { tmpl.name = "CoopEntityTemplate"; } catch { }
                UnityEngine.Object.DontDestroyOnLoad(tmpl);
                _template = tmpl;
                Log.LogInfo($"[ent] cached entity template from '{best.name}' (Resources scan; kept across scenes)");
            }
            catch (Exception e) { Log.LogWarning("[ent] cache template: " + e.Message); }
        }

        private static bool PendingContains(int key)
        {
            for (int i = 0; i < _pendingSpawns.Count; i++) if (_pendingSpawns[i].Key == key) return true;
            return false;
        }

        // Parent for a cloned mirror: the canvas the game places EntityLocations under. Prefer an existing
        // scene EntityLocation's parent (exact match for the live scene); else null and let EntityLocation
        // self-resolve its root canvas (ResolveRootCanvasRect).
        private static Transform ResolveEntityParent()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    var go = loc.gameObject; if (go == null || IsMirrorGo(go)) continue;
                    var p = loc.transform.parent; if (p != null) return p;
                }
            }
            catch { }
            return null;
        }

        private static EntityLocation FindLocalEntityById(string id)
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    if (IsMirrorGo(loc.gameObject)) continue;
                    MapEntity e = null; try { e = loc.Entity; } catch { }
                    if (e == null) continue;
                    string eid; try { eid = e.ID; } catch { continue; }
                    if (eid == id) return loc;
                }
            }
            catch { }
            return null;
        }

        private static bool IsMirrorGo(GameObject go)
        {
            if (go == null) return false;
            foreach (var m in _mirrors.Values) if (m.IsClone && (object)m.Go == (object)go) return true;
            return false;
        }

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        // ---------------- cleanup ----------------

        private static void ClearMirrors()
        {
            foreach (var m in _mirrors.Values) if (m.IsClone && m.Go != null) { try { UnityEngine.Object.Destroy(m.Go); } catch { } }
            _mirrors.Clear();
        }

        private static void ClearAll()
        {
            _sent.Clear();
            ClearMirrors();
            _pendingSpawns.Clear();
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            int clones = 0, adopted = 0;
            foreach (var m in _mirrors.Values) { if (m.IsClone) clones++; else adopted++; }
            return $"ent: inMission={InMission()} | host-sent={_sent.Count} | client-mirrors={_mirrors.Count} (clone={clones} adopt={adopted}) template={_template != null} pending={_pendingSpawns.Count}";
        }

        public static void Dump()
        {
            Log.LogInfo("[ent] " + Status());
            foreach (var kv in _sent) Log.LogInfo($"[ent]   sent '{kv.Value.ID}' pos={kv.Value.Pos} state={kv.Value.State} hp={kv.Value.Health}");
            foreach (var m in _mirrors.Values) Log.LogInfo($"[ent]   mirror '{m.ID}' {(m.IsClone ? "clone" : "adopt")} state={m.LastState}");
        }

        // ---------------- (de)serialization ----------------

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(512); return true; }
            catch (Exception e) { Log.LogWarning("[ent] buf: " + e.Message); return false; }
        }

        private static int PutInt(int o, int v) { _buf[o] = (byte)v; _buf[o + 1] = (byte)(v >> 8); _buf[o + 2] = (byte)(v >> 16); _buf[o + 3] = (byte)(v >> 24); return o + 4; }
        private static int PutF(int o, float v) { int __b = BitConverter.SingleToInt32Bits(v); _buf[o] = (byte)__b; _buf[o + 1] = (byte)(__b >> 8); _buf[o + 2] = (byte)(__b >> 16); _buf[o + 3] = (byte)(__b >> 24); return o + 4; }
        private static int PutV(int o, Vector3 v) { o = PutF(o, v.x); o = PutF(o, v.y); o = PutF(o, v.z); return o; }

        private static int PutStr(int o, string s)
        {
            s ??= "";
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = bytes.Length; if (n > 200) n = 200;   // IDs/names are short; cap guards the buffer
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

        private static int Fnv(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= 16777619u; }
            return unchecked((int)h);
        }
    }
}
