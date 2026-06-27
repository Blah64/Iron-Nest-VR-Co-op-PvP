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

        public const byte MSG_PVP_POS = 42;   // [t][gridX f][gridY f][role i32][health i32]  — author's map position + HP; either player authors it
        public const int MaxHealth = 100;

        private static int _myHealth = MaxHealth;   // MY authoritative health (victim-authoritative model); broadcast on POS
        private static bool _eliminated;

        private sealed class Mirror { public ulong Origin; public string ID; public GameObject Go; public EntityLocation Loc; public MapEntity Entity; public Vector2 Grid; public int Health; public CanvasGroup Vis; }
        private static readonly Dictionary<ulong, Mirror> _mirrors = new Dictionary<ulong, Mirror>();
        private static readonly List<ulong> _toRemove = new List<ulong>();

        private static Il2CppStructArray<byte> _buf;
        private static float _nextSend;
        private const float SendIntervalSec = 0.5f;   // position is static in Phase 1 — a slow reliable keyframe is plenty

        // Per-TEAM spawn grids (within the coord range Phase 0 observed on the demo maps). Both teammates pin their
        // local turret to the SAME team grid so a team reads as ONE vehicle. Phase C derives these from a match-start
        // placement; the values match the old host/client placeholders so a 1v1 is unchanged (host=team0, client=team1).
        private static readonly Vector2 Team0Spawn = new Vector2(4f, 2f);
        private static readonly Vector2 Team1Spawn = new Vector2(6f, 8f);

        private static int _sent, _spawned, _moved;
        private static bool _turretPlaced;   // pinned my turret to MyGrid this match?
        private static float _placeTurretAt;  // when to attempt placement (settle delay after entering the arena)

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Active())
            {
                // Leaving a PvP mission ends the match: clear mirrors and reset MY health for the next one.
                if (_mirrors.Count > 0) { ClearMirrors(); _myHealth = MaxHealth; _eliminated = false; Log.LogInfo("[pvp] left PvP mission — cleared player mirrors"); }
                _turretPlaced = false; _placeTurretAt = 0f;   // re-pin the turret next match
                return;
            }
            try
            {
                float now = Time.unscaledTime;

                // Pin MY turret to MY deterministic grid once, a moment after the arena settles, so the firing origin
                // is identical every match and a learned firing solution repeats. Retries each tick until the turret
                // exists. The mission's own turret-move nodes are suppressed (CoopSim) so nothing fights this.
                if (!_turretPlaced)
                {
                    if (_placeTurretAt <= 0f) _placeTurretAt = now + 2f;
                    else if (now >= _placeTurretAt && PlaceMyTurret()) _turretPlaced = true;
                }

                if (now >= _nextSend) { _nextSend = now + SendIntervalSec; BroadcastMyPosition(); }
#if !PUBLIC_BUILD
                RevealMirrors();   // TESTING: keep opponents visible through fog (real recon/teleprinter acquisition TBD)
#endif
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
            w.Int(_myHealth);
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
            // Never mirror a TEAMMATE as an enemy target — they share my vehicle and show as a co-op avatar, not a
            // map marker to shell. (POS is a GLOBAL type so it still reaches us from teammates; we filter here.) The
            // roster is known by the time we're in-mission, so this is reliable. Reap a stale teammate mirror if a
            // late roster flips someone from opponent to ally mid-flight.
            if (PvpTeams.IsTeammate(origin)) { OnPeerLeft(origin); return; }
            var r = new CoopWire.Reader(a, len, 1);
            float x = r.Float(), y = r.Float();
            int role = r.Int();
            int health = r.Int();
            if (r.Bad) return;
            UpsertRemote(origin, new Vector2(x, y), role, health);
        }

        private static void UpsertRemote(ulong origin, Vector2 grid, int role, int health)
        {
            if (_mirrors.TryGetValue(origin, out var m)) { MoveMirror(m, grid); ApplyMirrorHealth(m, health); return; }
            SpawnMirror(origin, grid, role, health);
        }

        // ---------------- spawn / move (the Phase 0 locked recipe) ----------------

        private static void SpawnMirror(ulong origin, Vector2 grid, int role, int health)
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

            health = Clamp(health);
            ResolveIcon();
            string id = "PVPPLAYER_" + origin;
            MapEntity e;
            try
            {
                e = new MapEntity();
                e.ID = id; e.Name = "Enemy turret"; e.Icon = _iconId;
                try { if (_iconSprite != null) e.IconRaw = _iconSprite; } catch { }   // the RESOLVED sprite — the map marker is BLANK without it
                e.Role = EntityRoles.Enemy;
                e.Position = new Vector3(grid.x, grid.y, 0f);
                e.State = StateForHealth(health);
                e.Health = health; e.MaxHealth = MaxHealth; e.Armour = 0; e.Stars = 0; e.Scale = 1;
            }
            catch (Exception ex) { Log.LogWarning("[pvp] build MapEntity: " + ex.Message); try { UnityEngine.Object.Destroy(go); } catch { } return; }

            try { loc.Init(e); } catch (Exception ex) { Log.LogWarning("[pvp] Init: " + ex.Message); }
            try { go.transform.localPosition = new Vector3(grid.x, grid.y, 0f); } catch { }
            try { loc.RecalculateAndRegister(true); } catch (Exception ex) { Log.LogWarning("[pvp] register: " + ex.Message); }
            try { ImpactTracker.RegisterEntity(loc); } catch (Exception ex) { Log.LogWarning("[pvp] RegisterEntity: " + ex.Message); }

            CanvasGroup vis = null; try { vis = loc.VisibilityGroup; } catch { }
            _mirrors[origin] = new Mirror { Origin = origin, ID = id, Go = go, Loc = loc, Entity = e, Grid = grid, Health = health, Vis = vis };
            _spawned++;
            // (render-state dump retired — marker rendering is confirmed; RevealMirrors keeps it through fog)
            Log.LogInfo($"[pvp] spawned player mirror '{id}' at grid ({grid.x:0.0},{grid.y:0.0}) hp={health} <- peer {origin}");
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

        // Reflect the victim's authoritative health (rode in on their POS keyframe) onto their mirror on THIS map.
        private static void ApplyMirrorHealth(Mirror m, int health)
        {
            if (m == null) return;
            health = Clamp(health);
            if (m.Health == health) return;
            m.Health = health;
            try { if (m.Entity != null) { m.Entity.Health = health; m.Entity.State = StateForHealth(health); } } catch { }
            try { if (m.Loc != null) m.Loc.RecalculateAndRegister(false); } catch { }
        }

#if !PUBLIC_BUILD
        // TESTING override: planning-map entities are fog-gated via EntityLocation.VisibilityGroup (a CanvasGroup the
        // recon system fades to 0 until an area is revealed). Force our opponent mirrors' alpha to 1 every frame so
        // they're always visible to aim at. The entity's own Update re-evaluates fog, hence per-frame re-application.
        // Replace with the chosen acquisition mechanic (teleprinter fire mission / scout recon) before any public PvP.
        private static void RevealMirrors()
        {
            foreach (var kv in _mirrors)
            {
                var m = kv.Value; if (m == null || m.Loc == null) continue;
                // The recon/fog system HIDES an entity by DISABLING its Image_Icon component (dump showed en=False
                // while the CanvasGroup alpha was already 1). Force the Image on — that's the actual reveal lever.
                try { var img = m.Loc.Image_Icon; if (img != null) img.enabled = true; } catch { }
                var v = m.Vis;
                if (v == null) { try { v = m.Vis = m.Loc.VisibilityGroup; } catch { } }
                if (v != null) { try { v.alpha = 1f; v.interactable = true; v.blocksRaycasts = true; } catch { } }
            }
        }
#endif

        // ---------------- damage (Phase 2 shot lane, victim-authoritative) ----------------

        // Called by PvpCombat when a peer reports their shell hit MY player. I own my own health: decrement it and
        // push an immediate keyframe so the attacker's mirror of me updates without waiting for the next tick.
        public static void ApplyDamageToSelf(int dmg, ulong from)
        {
            if (dmg <= 0 || _eliminated) return;
            int before = _myHealth;
            _myHealth = Clamp(_myHealth - dmg);
            Log.LogInfo($"[pvp] took {dmg} damage from peer {from} ({before} -> {_myHealth})");
            if (_myHealth <= 0) { _eliminated = true; Log.LogWarning("[pvp] === YOU WERE ELIMINATED ==="); }
            try { BroadcastMyPosition(); } catch { }   // immediate health keyframe to the attacker
        }

        // True if the given MapEntity ID is one of our opponent mirrors; returns that opponent's SteamID.
        public static bool TryGetMirrorOrigin(string entityId, out ulong origin)
        {
            origin = 0;
            if (string.IsNullOrEmpty(entityId)) return false;
            foreach (var kv in _mirrors) { var m = kv.Value; if (m != null && m.ID == entityId) { origin = kv.Key; return true; } }
            return false;
        }

        // PvP HIT ADJUDICATION (distance-based, victim-authoritative). We PLACED each opponent mirror, so we know its
        // map grid, and a shell's impactLocation is in that SAME map space (mirror (6,8) vs impact (6.33,7.96) in the
        // first runs). So we adjudicate a hit by PROXIMITY rather than depending on the engine's StartImpact hit set
        // returning a programmatically-spawned entity — which it does NOT (ImpactTracker adjudicates off its own
        // registry/spatial query; see PvpProbe RUN notes). Returns the SteamIDs of every mirror within `radius`.
        public static int CollectHits(Vector2 impact, float radius, List<ulong> outVictims)
        {
            outVictims.Clear();
            if (radius <= 0f) return 0;
            float r2 = radius * radius;
            foreach (var kv in _mirrors)
            {
                var m = kv.Value; if (m == null) continue;
                if ((m.Grid - impact).sqrMagnitude <= r2) outVictims.Add(kv.Key);
            }
            return outVictims.Count;
        }

#if !PUBLIC_BUILD
        // Per-impact proximity trace (one line per mirror) so the real ImpactRadius + the hit/miss margin is visible.
        public static void LogImpactProximity(Vector2 impact, float radius)
        {
            foreach (var kv in _mirrors)
            {
                var m = kv.Value; if (m == null) continue;
                float d = Vector2.Distance(m.Grid, impact);
                Log.LogInfo($"[pvp]   vs mirror '{m.ID}' grid ({m.Grid.x:0.0},{m.Grid.y:0.0}) dist={d:0.00} radius={radius:0.00} -> {(d <= radius ? "HIT" : "miss")}");
            }
        }
#endif

        private static int Clamp(int h) => h < 0 ? 0 : (h > MaxHealth ? MaxHealth : h);

        // The map marker needs BOTH a valid Icon KEY (in EntityLocation.PossibleMapIcons) AND the resolved Sprite
        // (MapEntity.IconRaw) — the key alone draws a blank (the Phase-1 invisible-marker bug). PossibleMapIcons maps
        // key -> MapEntityIcon{ID, Sprite Icon}, so we pull both from it once. Prefer an artillery / fire-direction /
        // enemy icon (this is an artillery opponent), else the first. Logs the key list + whether the sprite resolved.
        private static string _iconId;
        private static Sprite _iconSprite;
        private static void ResolveIcon()
        {
            if (_iconId != null) return;
            _iconId = ""; _iconSprite = null;
            try
            {
                var dict = EntityLocation.PossibleMapIcons;
                if (dict == null) { Log.LogWarning("[pvp] EntityLocation.PossibleMapIcons null — mirror may be invisible on the map"); return; }
                string first = null, pArtillery = null, pFdc = null, pEnemy = null; var all = new System.Text.StringBuilder();
                foreach (var k in dict.Keys)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    if (all.Length > 0) all.Append(", "); all.Append(k);
                    if (first == null) first = k;
                    string kl = k.ToLowerInvariant();
                    if (pArtillery == null && kl.Contains("artillery")) pArtillery = k;
                    if (pFdc == null && kl.Contains("fire direction")) pFdc = k;
                    if (pEnemy == null && kl.Contains("enemy")) pEnemy = k;
                }
                _iconId = pArtillery ?? pFdc ?? pEnemy ?? first ?? "";
                if (!string.IsNullOrEmpty(_iconId)) { try { var mei = dict[_iconId]; if (mei != null) _iconSprite = mei.Icon; } catch { } }
                Log.LogInfo($"[pvp] map icons: [{all}] -> mirrors use '{_iconId}' (sprite={_iconSprite != null})");
            }
            catch (Exception e) { Log.LogWarning("[pvp] resolve icon: " + e.Message); }
        }

        private static MapEntityStates StateForHealth(int health)
        {
            if (health <= 0) return MapEntityStates.Destroyed;
            if (health < MaxHealth / 2) return MapEntityStates.Damaged;
            return MapEntityStates.None;
        }

        // ---------------- helpers ----------------

        // My own map grid = MY TEAM's spawn (teammates coincide → one shared vehicle position). Falls back to
        // host/client role until the roster is known (the first frames after join), which keeps a 1v1 identical to
        // the old per-side placeholders. Phase C derives this from a real match-start placement.
        private static Vector2 MyGrid()
        {
            int team = -1; try { team = PvpTeams.MyTeam; } catch { }
            if (team == 0) return Team0Spawn;
            if (team == 1) return Team1Spawn;
            return CoopP2P.IsHost ? Team0Spawn : Team1Spawn;   // roster not resolved yet — fall back to role
        }

        // Pin THIS player's turret to their deterministic grid (MyGrid) so the firing origin — and therefore the
        // azimuth/range/elevation solution to the fixed enemy marker — is identical every match. The turret moves in
        // WORLD space (SetTurretLocation takes a world position and snaps Y to the map surface; probe RUN 1 showed
        // X/Z preserved, Y resolved), so convert grid->world through Fire Mission Root — the SAME transform the map
        // markers use (entity FMR-local (X,Y,0) == grid (X,Y)). Returns true once placed; retried until the turret
        // exists. The mission's State_MoveTurret/State_SetTurretLocation nodes are suppressed so nothing overrides it.
        private static bool PlaceMyTurret()
        {
            try
            {
                var t = TurretController.Instance; if (t == null) return false;
                var bas = t.turretBase; if (bas == null) return false;
                var fmr = ResolveParent(); if (fmr == null) return false;
                Vector2 g = MyGrid();
                Vector3 worldTarget = fmr.TransformPoint(new Vector3(g.x, g.y, 0f));
#if !PUBLIC_BUILD
                Vector2 gridBefore = Vector2.zero; Vector3 worldBefore = Vector3.zero;
                try { gridBefore = bas.anchoredPosition; } catch { }
                try { worldBefore = bas.position; } catch { }
#endif
                t.SetTurretLocation(worldTarget);
#if !PUBLIC_BUILD
                Vector2 gridAfter = Vector2.zero; Vector3 worldAfter = Vector3.zero;
                try { gridAfter = bas.anchoredPosition; } catch { }
                try { worldAfter = bas.position; } catch { }
                Log.LogInfo($"[pvp] placed my turret -> grid target ({g.x:0.0},{g.y:0.0}) worldTarget={worldTarget.ToString("0.0")} | turretBase anchored {gridBefore.ToString("0.0")}->{gridAfter.ToString("0.0")} world {worldBefore.ToString("0.0")}->{worldAfter.ToString("0.0")}");
#else
                Log.LogInfo($"[pvp] placed my turret at grid ({g.x:0.0},{g.y:0.0})");
#endif
                return true;
            }
            catch (Exception e) { Log.LogWarning("[pvp] place turret: " + e.Message); return false; }
        }

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

        // ---------------- HUD accessors ----------------

        public static int MyHealth => _myHealth;
        public static bool Eliminated => _eliminated;
        public static Vector2 MyGridPublic => MyGrid();
        public static int MirrorCount => _mirrors.Count;

        // First opponent mirror (1v1 = the only one). Returns its map grid + current health.
        public static bool TryGetFirstEnemy(out Vector2 grid, out int health)
        {
            grid = Vector2.zero; health = 0;
            foreach (var kv in _mirrors) { var m = kv.Value; if (m != null) { grid = m.Grid; health = m.Health; return true; } }
            return false;
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

        public static string Status() => $"pvpPlayers: active={Active()} hp={_myHealth}{(_eliminated ? " ELIM" : "")} mirrors={_mirrors.Count} sent={_sent} spawned={_spawned} moved={_moved}";
    }
}
