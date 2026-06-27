using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP Phase 1 — PLAYER PRESENCE. Each player is represented on the OTHER player's tactical map as an enemy
    /// <see cref="MapEntity"/>, so they can be seen and (Phase 2) shelled. Uses the placement recipe proven in
    /// Phase 0 (PvpProbe / PLAN-pvp.md §3.2): clone an EntityLocation under "Fire Mission Root", set
    /// transform.localPosition = (gridX,gridY,0), Init, RecalculateAndRegister, ImpactTracker.RegisterEntity.
    ///
    /// FLOW (symmetric, no host authority — each machine owns its own player position):
    ///   • Each machine BROADCASTS its own player's map grid position (<c>MSG_PVP_POS</c>, byte 42) periodically.
    ///   • On receipt it spawns/moves a MIRROR MapEntity(Role=Enemy) for that origin so the sender shows up as a
    ///     target on this machine's map. Keyed by the author's SteamID (origin), so N>2 each get their own mirror.
    ///
    /// PHASE 1 SCOPE: presence + position only. NO damage / shot adjudication yet (that's Phase 2 PvpCombat — a
    /// StartImpact postfix that, when MY shell's hit set contains an opponent mirror, reports the hit to that peer
    /// who applies it to their own health). For now each player's grid is a fixed per-side placeholder spawn.
    ///
    /// Entirely gated on <see cref="Config.PvpActive"/> (a PvP lobby) + in-mission + a peer ⇒ co-op/solo untouched.
    /// </summary>
    internal static class PvpPlayers
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_PVP_POS = 42;   // [t][gridX f][gridY f][role i32]  — author's map position; either player authors it

        private sealed class Mirror { public ulong Origin; public string ID; public GameObject Go; public EntityLocation Loc; public MapEntity Entity; public Vector2 Grid; }
        private static readonly Dictionary<ulong, Mirror> _mirrors = new Dictionary<ulong, Mirror>();
        private static readonly List<ulong> _toRemove = new List<ulong>();

        private static Il2CppStructArray<byte> _buf;
        private static float _nextSend;
        private const float SendIntervalSec = 0.5f;   // position is static in Phase 1 — a slow reliable keyframe is plenty

        // Placeholder per-side spawn grids (within the coord range Phase 0 observed on the demo maps). Phase 2 will
        // derive these from each player's actual turret map position / a match-start placement.
        private static readonly Vector2 HostSpawn = new Vector2(4f, 2f);
        private static readonly Vector2 ClientSpawn = new Vector2(6f, 8f);

        private static int _sent, _spawned, _moved;

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Active())
            {
                if (_mirrors.Count > 0) { ClearMirrors(); Log.LogInfo("[pvp] left PvP mission — cleared player mirrors"); }
                return;
            }
            try
            {
                float now = Time.unscaledTime;
                if (now >= _nextSend) { _nextSend = now + SendIntervalSec; BroadcastMyPosition(); }
            }
            catch (Exception e) { Log.LogWarning("[pvp] tick: " + e.Message); }
        }

        private static void BroadcastMyPosition()
        {
            if (!EnsureBuf()) return;
            Vector2 g = MyGrid();
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_PVP_POS);
            w.Float(g.x); w.Float(g.y); w.Int((int)EntityRoles.Enemy);
            if (w.Overflow) { Log.LogWarning("[pvp] pos packet overflow"); return; }
            CoopP2P.Send(_buf, w.Length, true);
            _sent++;
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_PVP_POS) return;
            if (!Active()) return;                 // only mirror while we're in a PvP mission ourselves
            if (origin == CoopP2P.MyId) return;    // never mirror ourselves
            var r = new CoopWire.Reader(a, len, 1);
            float x = r.Float(), y = r.Float();
            int role = r.Int();
            if (r.Bad) return;
            UpsertRemote(origin, new Vector2(x, y), role);
        }

        private static void UpsertRemote(ulong origin, Vector2 grid, int role)
        {
            if (_mirrors.TryGetValue(origin, out var m)) { MoveMirror(m, grid); return; }
            SpawnMirror(origin, grid, role);
        }

        // ---------------- spawn / move (the Phase 0 locked recipe) ----------------

        private static void SpawnMirror(ulong origin, Vector2 grid, int role)
        {
            var tmpl = FindCloneSource();
            if (tmpl == null) { Log.LogWarning("[pvp] no EntityLocation to clone — can't spawn player mirror yet"); return; }

            GameObject go = null;
            try { go = UnityEngine.Object.Instantiate(tmpl).TryCast<GameObject>(); } catch (Exception ex) { Log.LogWarning("[pvp] instantiate: " + ex.Message); }
            if (go == null) return;
            try { go.SetActive(true); } catch { }
            try { go.name = "PvpPlayer_" + origin; } catch { }
            var parent = ResolveParent();
            if (parent != null) { try { go.transform.SetParent(parent, false); } catch { } }

            EntityLocation loc = null;
            try { loc = go.GetComponent<EntityLocation>(); } catch { }
            if (loc == null) { try { UnityEngine.Object.Destroy(go); } catch { } Log.LogWarning("[pvp] clone has no EntityLocation"); return; }

            string id = "PVPPLAYER_" + origin;
            MapEntity e;
            try
            {
                e = new MapEntity();
                e.ID = id; e.Name = "Enemy turret"; e.Icon = "";
                e.Role = EntityRoles.Enemy;
                e.Position = new Vector3(grid.x, grid.y, 0f);
                e.State = MapEntityStates.None;
                e.Health = 100; e.MaxHealth = 100; e.Armour = 0; e.Stars = 0; e.Scale = 1;
            }
            catch (Exception ex) { Log.LogWarning("[pvp] build MapEntity: " + ex.Message); try { UnityEngine.Object.Destroy(go); } catch { } return; }

            try { loc.Init(e); } catch (Exception ex) { Log.LogWarning("[pvp] Init: " + ex.Message); }
            try { go.transform.localPosition = new Vector3(grid.x, grid.y, 0f); } catch { }
            try { loc.RecalculateAndRegister(true); } catch (Exception ex) { Log.LogWarning("[pvp] register: " + ex.Message); }
            try { ImpactTracker.RegisterEntity(loc); } catch (Exception ex) { Log.LogWarning("[pvp] RegisterEntity: " + ex.Message); }

            _mirrors[origin] = new Mirror { Origin = origin, ID = id, Go = go, Loc = loc, Entity = e, Grid = grid };
            _spawned++;
            Log.LogInfo($"[pvp] spawned player mirror '{id}' at grid ({grid.x:0.0},{grid.y:0.0}) <- peer {origin}");
        }

        private static void MoveMirror(Mirror m, Vector2 grid)
        {
            if ((m.Grid - grid).sqrMagnitude < 0.0001f) return;   // unchanged
            m.Grid = grid;
            try { if (m.Entity != null) m.Entity.Position = new Vector3(grid.x, grid.y, 0f); } catch { }
            try { if (m.Go != null) m.Go.transform.localPosition = new Vector3(grid.x, grid.y, 0f); } catch { }
            try { if (m.Loc != null) m.Loc.RecalculateAndRegister(false); } catch { }
            _moved++;
        }

        // ---------------- helpers ----------------

        // My own map grid. PHASE 1: a fixed per-side placeholder (host vs client). Phase 2 derives it from the
        // player's real turret position / match-start placement.
        private static Vector2 MyGrid() => CoopP2P.IsHost ? HostSpawn : ClientSpawn;

        private static bool Active()
        {
            if (!Config.PvpActive) return false;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) return false;
            return InMission();
        }

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        // A source EntityLocation to clone (prefer a non-clone, non-PvP one). Resources scan sees inactive templates.
        private static GameObject FindCloneSource()
        {
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<EntityLocation>());
                if (arr == null) return null;
                GameObject best = null;
                for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    var go = loc.gameObject; if (go == null) continue;
                    string nm = null; try { nm = go.name; } catch { }
                    if (nm != null && nm.StartsWith("PvpPlayer_", StringComparison.Ordinal)) continue;   // not one of ours
                    best = go;
                    if (nm == null || nm.IndexOf("(Clone)", StringComparison.Ordinal) < 0) break;        // prefer a prefab/source
                }
                return best;
            }
            catch (Exception e) { Log.LogWarning("[pvp] find clone source: " + e.Message); return null; }
        }

        private static Transform ResolveParent()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    var go = loc.gameObject; if (go == null) continue;
                    string nm = null; try { nm = go.name; } catch { }
                    if (nm != null && nm.StartsWith("PvpPlayer_", StringComparison.Ordinal)) continue;
                    var p = loc.transform.parent; if (p != null) return p;
                }
            }
            catch { }
            try { var fmr = GameObject.Find("Fire Mission Root"); if (fmr != null) return fmr.transform; } catch { }
            return null;
        }

        // ---------------- cleanup ----------------

        private static void ClearMirrors()
        {
            foreach (var m in _mirrors.Values) { try { if (m.Go != null) UnityEngine.Object.Destroy(m.Go); } catch { } }
            _mirrors.Clear();
        }

        // Reap a mirror for a peer that left (called by the lobby/peer-leave path if wired; safe to call anytime).
        public static void OnPeerLeft(ulong origin)
        {
            if (_mirrors.TryGetValue(origin, out var m)) { try { if (m.Go != null) UnityEngine.Object.Destroy(m.Go); } catch { } _mirrors.Remove(origin); }
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(64); return true; }
            catch (Exception e) { Log.LogWarning("[pvp] buf: " + e.Message); return false; }
        }

        // ---------------- diagnostics ----------------

        public static string Status() => $"pvpPlayers: active={Active()} mirrors={_mirrors.Count} sent={_sent} spawned={_spawned} moved={_moved}";
    }
}
