using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP PLAYER PRESENCE. Each player is represented on the OTHER player's tactical map as an enemy
    /// <see cref="MapEntity"/>, so they can be seen and shelled. Placement recipe: clone an EntityLocation under
    /// "Fire Mission Root", set transform.localPosition = (gridX,gridY,0), Init, RecalculateAndRegister,
    /// ImpactTracker.RegisterEntity.
    ///
    /// FLOW (symmetric, no host authority — each machine owns its own player position):
    ///   • Each machine BROADCASTS its own player's map grid position (<c>MSG_PVP_POS</c>, byte 42) periodically.
    ///   • On receipt it spawns/moves a MIRROR MapEntity(Role=Enemy) for that origin so the sender shows up as a
    ///     target on this machine's map. Keyed by the author's SteamID (origin), so N>2 each get their own mirror.
    ///
    /// Entirely gated on <see cref="Config.PvpActive"/> (a PvP lobby) + in-mission + a peer ⇒ co-op/solo untouched.
    /// </summary>
    internal static class PvpPlayers
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_PVP_POS = 42;   // [t][gridX f][gridY f][role i32][health f32]  — author's map position + HP; either player authors it
        public const byte MSG_PVP_SPAWN = 50; // [t][t0x f][t0y f][t1x f][t1y f]  reliable  HOST->all (global) — the two randomized team spawn grids
        // A nest dies in 4 HEALTH POINTS. Per-shell damage (PvpCombat.DamageForShell): AP 2, HE 1, HCHE 0.5, STAR 0
        // (STAR is recon only — it reveals enemies, see OnStarShell). So a nest takes 2 AP, 4 HE, or 8 HCHE to kill.
        public const float MaxHealth = 4f;

        private static float _teamHealth = MaxHealth;   // MY TEAM's shared health. The team CAPTAIN owns it authoritatively
                                                       // and broadcasts it on POS; teammates adopt it from the captain's
                                                       // keyframe. Victim-authoritative, one pool per team (vehicle).
        private static bool _eliminated;              // my team is at 0 hp

        // Match result, decided LOCALLY from the health state of a 2-team duel: Won = every enemy mirror destroyed,
        // Lost = my team eliminated. Latched so it's announced once; reset on leaving the mission. (Auto reset / return-
        // to-lobby is a follow-up; for now the result is declared and players leave the mission to reset.)
        private enum Result { None, Won, Lost }
        private static Result _result = Result.None;

        // Grid = the enemy's LIVE position (rides in on every POS keyframe) — hit adjudication (CollectHits) uses this so
        // you must shell where they ACTUALLY are. RevealedGrid = the position FROZEN at the moment a recon spotted them —
        // the on-map icon is drawn here and only re-snapshots when re-scouted (last-known-position intel, not a live track).
        private sealed class Mirror { public ulong Origin; public string ID; public GameObject Go; public EntityLocation Loc; public MapEntity Entity; public Vector2 Grid; public Vector2 RevealedGrid; public bool HasRevealedPos; public float Health; public CanvasGroup Vis; public float RevealUntil; }

        private const float ReconRevealSec = 30f;   // how long a recon photo keeps a spotted enemy visible before it re-fogs
                                                      // (generous — the player redeems at the console then walks to the map)
        private const float PhotoRadius = 4f;        // grid-units around the dialed recon target a scout photo can spot
        private static readonly Dictionary<ulong, Mirror> _mirrors = new Dictionary<ulong, Mirror>();
        private static readonly List<ulong> _toRemove = new List<ulong>();

        private static Il2CppStructArray<byte> _buf;
        private static float _nextSend;
        private const float SendIntervalSec = 0.5f;   // position is static in Phase 1 — a slow reliable keyframe is plenty

        // Per-TEAM spawn grids (FMR-local grid units; 1 unit == 1 km on the demo maps — the map is 20x10 km). The HOST
        // RANDOMIZES these at match start within the arena's REAL grid bounds, guaranteeing the two team turrets spawn at
        // least MinSpawnSepGrid km apart, then broadcasts them (MSG_PVP_SPAWN). Both teammates pin to the SAME team grid so
        // a team reads as ONE vehicle. Default to the old fixed placeholders until the host's assignment lands (and as a
        // solo/dev fallback). _spawnsAssigned latches once randomized (host) or received (client).
        private static readonly Vector2 Team0SpawnDefault = new Vector2(4f, 2f);
        private static readonly Vector2 Team1SpawnDefault = new Vector2(6f, 8f);
        private static Vector2 _team0Spawn = Team0SpawnDefault;
        private static Vector2 _team1Spawn = Team1SpawnDefault;
        private static bool _spawnsAssigned;
        private static float _spawnGenAt;       // host: when to generate (settle so FireMission's map bounds exist)
        private static float _nextSpawnBeat;    // host: re-broadcast heartbeat (JIP / lost-packet heal)
        private static System.Random _spawnRng;
        private const float SpawnBeatSec = 3f;
        private const float KmPerGridUnit = 1f;       // demo map scale: 1 grid unit = 1 km (map is 20 km x 10 km)
        private const float MinSpawnSepGrid = 3f;     // turrets >= 3 km apart (user requirement); grid == km so 3 units
        private const float SpawnEdgeInsetGrid = 1f;  // keep turrets >= 1 km off the map edge
        // Fallback grid bounds — used only if FireMission.GetGridBounds can't be read: the user-confirmed map size with a
        // bottom-left origin (matches the small positive demo coords, e.g. the scene-default turret at ~ (1.6,1.6)).
        private static readonly Vector2 FallbackGridMin = new Vector2(0f, 0f);
        private static readonly Vector2 FallbackGridMax = new Vector2(20f, 10f);

        private static int _sent, _spawned, _moved;
        private static bool _turretPlaced;   // pinned my turret to MyGrid this match?
        private static float _placeTurretAt;  // when to attempt placement (settle delay after entering the arena)

        // Teleprinter battery-position report ("tell each team their turret position on spawn + after a move card").
        // Local print only — in PvP, CoopOrders' replicate (OnSubmitLines) and blank (SuppressLocalPrint) both bail on
        // PvpActive, so each crew member's own printer states their own (shared-team) battery grid. Unified by settle-
        // detection: the turret lands on its spawn at deploy and traverses on a requisition move card; each time it
        // goes stationary at a NEW grid we print (DEPLOYED the first time, RELOCATED after).
        private static Vector2 _lastTurretGrid = new Vector2(-9999f, -9999f);   // last sampled live grid (move detector)
        private static Vector2 _announcedGrid = new Vector2(-9999f, -9999f);    // last grid we printed to the teleprinter
        private static float _turretStableSince;     // when the turret last went stationary
        private static int _posAnnounceCount;        // 0 => next print is DEPLOYED, else RELOCATED
        private const float TurretMoveEps = 0.05f;       // grid delta (km) above which the turret counts as moving
        private const float TurretSettleSec = 1.0f;      // must be stationary this long after a move before we print
        private const float AnnounceMinDeltaGrid = 0.2f; // ignore a "move" smaller than this (no spurious re-print)

        // SCOUT-PHOTO FOOTPRINT (production, both flavors). A scout plane's photo "develops" LATE — a few seconds after the
        // flyover the game registers a reveal REGION (MapReconClearer.Register(handle)); that handle's fog-tile children ARE
        // the exact photographed area. We arm a window when the plane launches, capture the region (event-driven via the
        // Register hook, plus a polling backup), and reveal only the enemy mirrors INSIDE that region. The dialed circle is
        // a fallback used only if no region ever lands. Tied to the scout window so a forward observer never reveals.
        private const float ScoutWindowSec = 30f;        // how long after launch we accept the late photo region
        private const float FallbackAfterSec = 20f;      // if no region landed by now, reveal the dialed circle instead (backstop)
        private const float HandleSettleSec = 0.75f;     // let RegisterChild populate the region's children before reading
        private const float FootprintMargin = 1.0f;      // photo strip HALF-WIDTH: a mirror within this of the strip segment is "on the photo"
        private const float HandleScanInterval = 0.25f;  // how often we re-scan for newly-appeared recon regions
        private static float _scoutWindowUntil;
        private static float _lastScoutLaunch;
        private static Vector2 _scoutFallbackCenter;
        private static bool _scoutFallbackArmed;
        private static float _scoutFallbackAt;
        private static bool _scoutHandled;               // a photo region resolved this scout → suppress the circle fallback
        private static float _nextHandleScan;
        private static bool _handlesInit;                // first scan done? (baselines scene handles so they aren't seen as photos)
        private static readonly HashSet<int> _knownHandles = new HashSet<int>();          // every recon handle seen this mission (PERSISTENT, no per-pass race)
        private static readonly HashSet<int> _scoutHandlesCaptured = new HashSet<int>();  // handles already turned into a reveal
        private sealed class PendingRegion { public MapReconClearHandle H; public float At; }
        private static readonly List<PendingRegion> _pendingRegions = new List<PendingRegion>();

#if !PUBLIC_BUILD
        // RECON FOOTPRINT TELEMETRY (dev only) — after a scout-plane run, capture WHERE the photos actually land so the
        // reveal can match the real footprint instead of a guessed circle. Logs, in grid space: fog tiles that get
        // cleared, the plane clone's flight path, and recon handle/clearer counts. Pure observation; doesn't reveal.
        private static float _reconWatchUntil;
        private static float _nextReconLog;
        private static Transform _reconFmr;
        private static string _planeName;
        private static readonly Dictionary<int, Vector2> _fogSnap = new Dictionary<int, Vector2>();   // fog tile id -> grid at run start
        private static readonly HashSet<int> _fogCleared = new HashSet<int>();                          // tiles gone since start
        private static readonly HashSet<int> _handleSnap = new HashSet<int>();                          // recon handle ids present at run start
        private static readonly HashSet<int> _handleSeen = new HashSet<int>();                          // NEW handles already logged this run
#endif

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Active())
            {
                // Leaving a PvP mission ends the match: clear mirrors and reset team health/result for the next one.
                if (_mirrors.Count > 0) { ClearMirrors(); Log.LogInfo("[pvp] left PvP mission — cleared player mirrors"); }
                _teamHealth = MaxHealth; _eliminated = false; _result = Result.None;
                _turretPlaced = false; _placeTurretAt = 0f;   // re-pin the turret next match
                _lastTurretGrid = _announcedGrid = new Vector2(-9999f, -9999f); _turretStableSince = 0f; _posAnnounceCount = 0;   // re-announce battery grid next match
                _spawnsAssigned = false; _spawnGenAt = 0f; _nextSpawnBeat = 0f;   // re-randomize spawns next match
                _team0Spawn = Team0SpawnDefault; _team1Spawn = Team1SpawnDefault;
                _scoutWindowUntil = 0f; _scoutFallbackArmed = false; _scoutHandled = false; _pendingRegions.Clear();
                _knownHandles.Clear(); _scoutHandlesCaptured.Clear(); _handlesInit = false; _nextHandleScan = 0f;
                return;
            }
            try
            {
                float now = Time.unscaledTime;

                // HOST assigns the two team spawn grids ONCE per match — randomized, within the arena's real grid bounds,
                // and >= MinSpawnSepGrid km apart — then broadcasts them (clients adopt via MSG_PVP_SPAWN). A short settle
                // lets FireMission (the map-bounds source) awake first. A slow heartbeat heals a late joiner / lost packet.
                if (CoopP2P.IsHost)
                {
                    if (!_spawnsAssigned)
                    {
                        if (_spawnGenAt <= 0f) _spawnGenAt = now + 2.5f;
                        else if (now >= _spawnGenAt) GenerateAndBroadcastSpawns();
                    }
                    else if (now >= _nextSpawnBeat) { _nextSpawnBeat = now + SpawnBeatSec; BroadcastSpawns(); }
                }

                // Pin MY turret to MY assigned team grid once spawns are known, a moment after they settle, so the firing
                // origin is deterministic for the match and a learned solution repeats. Gated on _spawnsAssigned so we
                // never place at the pre-assignment default and then have to re-place when the real grid arrives. Retries
                // each tick until the turret exists. The mission's own turret-move nodes are suppressed (CoopSim).
                if (!_turretPlaced && _spawnsAssigned)
                {
                    if (_placeTurretAt <= 0f) _placeTurretAt = now + 2f;
                    else if (now >= _placeTurretAt && PlaceMyTurret()) _turretPlaced = true;
                }

                // Report MY battery's grid on the teleprinter — once on deploy, and again after a move card relocates it.
                TickTurretPositionReport(now);

                // Only the TEAM CAPTAIN broadcasts the team's keyframe → opponents see ONE mirror per enemy team
                // (the vehicle), not one per player. Fall back to broadcasting if the roster isn't resolved yet so a
                // 1v1 still works before the first roster packet lands.
                if (now >= _nextSend)
                {
                    _nextSend = now + SendIntervalSec;
                    bool roster = false; try { roster = PvpTeams.RosterKnown; } catch { }
                    if (SafeAmICaptain() || !roster) BroadcastMyPosition();
                }
                PruneNonCaptainMirrors();   // reap a non-captain's stale mirror once the roster is known
                CheckMatchEnd();            // declare win/lose from the team-health state
                ApplyMirrorVisibility();    // ACQUISITION: enemy is fog-hidden until a recon card reveals it (OnReconReveal)
                ScanHandles(now);           // baseline scene recon regions + detect a developed scout photo region
                if (_scoutWindowUntil > 0f) ProcessScoutFootprint(now);   // resolve a scout photo into a footprint reveal
#if !PUBLIC_BUILD
                if (_reconWatchUntil > 0f) ReconWatchTick(now);   // dev telemetry: fog/plane diff after a scout run
#endif
            }
            catch (Exception e) { Log.LogWarning("[pvp] tick: " + e.Message); }
        }

        private static void BroadcastMyPosition()
        {
            if (!EnsureBuf()) return;
            Vector2 g = MyTurretGrid();   // LIVE turret position — so a card-driven relocate moves our blip on the enemy's map
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_PVP_POS);
            w.Float(g.x); w.Float(g.y); w.Int((int)EntityRoles.Enemy);
            w.Float(_teamHealth);
            if (w.Overflow) { Log.LogWarning("[pvp] pos packet overflow"); return; }
            CoopP2P.Send(_buf, w.Length, true);
            _sent++;
        }

        // ---------------- randomized team spawns (host-authoritative) ----------------

        // HOST: pick the two team spawn grids for this match — random, within the arena's REAL grid bounds, and at least
        // MinSpawnSepGrid (km) apart — then latch + broadcast. Bounds come from FireMission.GetGridBounds (the map's own
        // grid extent) so the points are on-map regardless of the coord origin; a user-confirmed 20x10 fallback covers
        // the rare case the bounds can't be read. Latched per match by _spawnsAssigned.
        private static void GenerateAndBroadcastSpawns()
        {
            Vector2 mn, mx; bool fromMap = TryGetMapGridBounds(out mn, out mx);
            if (!fromMap) { mn = FallbackGridMin; mx = FallbackGridMax; }
            mn.x += SpawnEdgeInsetGrid; mn.y += SpawnEdgeInsetGrid; mx.x -= SpawnEdgeInsetGrid; mx.y -= SpawnEdgeInsetGrid;
            if (mx.x <= mn.x || mx.y <= mn.y) { mn = FallbackGridMin; mx = FallbackGridMax; }   // degenerate inset -> safe box

            if (_spawnRng == null) _spawnRng = new System.Random();
            Vector2 p0 = RandGrid(mn, mx), p1 = RandGrid(mn, mx);
            bool ok = false;
            for (int i = 0; i < 200; i++)
            {
                p0 = RandGrid(mn, mx); p1 = RandGrid(mn, mx);
                if ((p0 - p1).magnitude >= MinSpawnSepGrid) { ok = true; break; }
            }
            if (!ok) { p0 = new Vector2(mn.x, mn.y); p1 = new Vector2(mx.x, mx.y); }   // box too small -> opposite corners (max sep)

            _team0Spawn = p0; _team1Spawn = p1;
            _spawnsAssigned = true;
            _nextSpawnBeat = now2() + SpawnBeatSec;
            float sepKm = (p0 - p1).magnitude * KmPerGridUnit;
            Log.LogInfo($"[pvp] randomized spawns: team0 ({p0.x:0.0},{p0.y:0.0}) team1 ({p1.x:0.0},{p1.y:0.0}) = {sepKm:0.00} km apart (bounds [{mn.x:0.0},{mn.y:0.0}]..[{mx.x:0.0},{mx.y:0.0}] {(fromMap ? "from FireMission" : "fallback 20x10")})");
            BroadcastSpawns();
        }

        // Random grid point in [mn,mx], snapped to the map's 0.1 km sub-grid lines.
        private static Vector2 RandGrid(Vector2 mn, Vector2 mx)
        {
            float x = mn.x + (float)_spawnRng.NextDouble() * (mx.x - mn.x);
            float y = mn.y + (float)_spawnRng.NextDouble() * (mx.y - mn.y);
            x = Mathf.Round(x * 10f) / 10f; y = Mathf.Round(y * 10f) / 10f;   // 0.1 grid == 0.1 km (the sub-grid lines)
            return new Vector2(x, y);
        }

        // The arena's grid bounds in FMR-local grid space (== the space PlaceMyTurret/MyGrid use), from the game's own
        // FireMission.GetGridBounds() (world-space corners) converted through Fire Mission Root. Returns false if the map
        // isn't up yet or the result doesn't look like the ~20x10 demo map (then the caller uses the fallback box).
        private static bool TryGetMapGridBounds(out Vector2 min, out Vector2 max)
        {
            min = Vector2.zero; max = Vector2.zero;
            try
            {
                var fm = FireMission.Instance; if (fm == null) return false;
                var corners = fm.GetGridBounds(); if (corners == null || corners.Length == 0) return false;
                var fmr = ResolveParent(); if (fmr == null) return false;
                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
                for (int i = 0; i < corners.Length; i++)
                {
                    Vector2 g = WorldToGrid(fmr, corners[i]);
                    if (g.x < mn.x) mn.x = g.x; if (g.y < mn.y) mn.y = g.y;
                    if (g.x > mx.x) mx.x = g.x; if (g.y > mx.y) mx.y = g.y;
                }
                float w = mx.x - mn.x, h = mx.y - mn.y;
                if (w < 4f || w > 60f || h < 2f || h > 40f) return false;   // not the expected ~20x10 grid -> fallback
                min = mn; max = mx;
                return true;
            }
            catch { return false; }
        }

        // HOST: send the two randomized team spawn grids to everyone (global type -> crosses teams). Reliable.
        private static void BroadcastSpawns()
        {
            if (!CoopP2P.IsHost) return;
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_PVP_SPAWN);
            w.Float(_team0Spawn.x); w.Float(_team0Spawn.y);
            w.Float(_team1Spawn.x); w.Float(_team1Spawn.y);
            if (w.Overflow) { Log.LogWarning("[pvp] spawn packet overflow"); return; }
            CoopP2P.Send(_buf, w.Length, true);
        }

        // CLIENT: adopt the host's randomized team spawn grids. Only the host sends MSG_PVP_SPAWN, so any received one is
        // authoritative; the host sets its own in GenerateAndBroadcastSpawns and ignores echoes here.
        private static void OnSpawnPacket(ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (origin == CoopP2P.MyId || CoopP2P.IsHost) return;   // host is the author — never overwrite from a packet
            var r = new CoopWire.Reader(a, len, 1);
            float t0x = r.Float(), t0y = r.Float(), t1x = r.Float(), t1y = r.Float();
            if (r.Bad) return;
            bool first = !_spawnsAssigned;
            _team0Spawn = new Vector2(t0x, t0y);
            _team1Spawn = new Vector2(t1x, t1y);
            _spawnsAssigned = true;
            if (first) Log.LogInfo($"[pvp] received team spawns: team0 ({t0x:0.0},{t0y:0.0}) team1 ({t1x:0.0},{t1y:0.0}) <- host");
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (type == MSG_PVP_SPAWN) { OnSpawnPacket(origin, a, len); return; }   // accept regardless of Active() so it's ready by placement time
            if (type != MSG_PVP_POS) return;
            if (!Active()) return;                 // only mirror while we're in a PvP mission ourselves
            if (origin == CoopP2P.MyId) return;    // never mirror ourselves
            var r = new CoopWire.Reader(a, len, 1);
            float x = r.Float(), y = r.Float();
            int role = r.Int();
            float health = r.Float();
            if (r.Bad) return;

            // A TEAMMATE's keyframe is my CAPTAIN announcing the shared team health (allies are co-op avatars, never a
            // map target). Adopt the team health if I'm not the captain; never spawn an ally mirror. (POS is a GLOBAL
            // type so it still reaches us from teammates — we filter here.)
            if (PvpTeams.IsTeammate(origin))
            {
                if (!SafeAmICaptain()) AdoptTeamHealth(health);
                OnPeerLeft(origin);
                return;
            }

            // An ENEMY: mirror ONLY the enemy team's CAPTAIN (one vehicle per team). Reap a non-captain's stale mirror
            // once the roster is known; before it resolves, mirror anyone (1v1 pre-roster fallback).
            bool roster = false; try { roster = PvpTeams.RosterKnown; } catch { }
            if (roster && !PvpTeams.IsCaptain(origin)) { OnPeerLeft(origin); return; }
            UpsertRemote(origin, new Vector2(x, y), role, health);
        }

        private static void UpsertRemote(ulong origin, Vector2 grid, int role, float health)
        {
            if (_mirrors.TryGetValue(origin, out var m)) { MoveMirror(m, grid); ApplyMirrorHealth(m, health); return; }
            SpawnMirror(origin, grid, role, health);
        }

        // ---------------- spawn / move (the Phase 0 locked recipe) ----------------

        private static void SpawnMirror(ulong origin, Vector2 grid, int role, float health)
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
                e.Health = HpInt(health); e.MaxHealth = HpInt(MaxHealth); e.Armour = 0; e.Stars = 0; e.Scale = 1;
            }
            catch (Exception ex) { Log.LogWarning("[pvp] build MapEntity: " + ex.Message); try { UnityEngine.Object.Destroy(go); } catch { } return; }

            try { loc.Init(e); } catch (Exception ex) { Log.LogWarning("[pvp] Init: " + ex.Message); }
            try { go.transform.localPosition = new Vector3(grid.x, grid.y, 0f); } catch { }
            try { loc.RecalculateAndRegister(true); } catch (Exception ex) { Log.LogWarning("[pvp] register: " + ex.Message); }
            try { ImpactTracker.RegisterEntity(loc); } catch (Exception ex) { Log.LogWarning("[pvp] RegisterEntity: " + ex.Message); }

            CanvasGroup vis = null; try { vis = loc.VisibilityGroup; } catch { }
            _mirrors[origin] = new Mirror { Origin = origin, ID = id, Go = go, Loc = loc, Entity = e, Grid = grid, RevealedGrid = grid, HasRevealedPos = false, Health = health, Vis = vis };
            _spawned++;
            Log.LogInfo($"[pvp] spawned player mirror '{id}' at grid ({grid.x:0.0},{grid.y:0.0}) hp={health:0.#} <- peer {origin}");
        }

        private static void MoveMirror(Mirror m, Vector2 grid)
        {
            if ((m.Grid - grid).sqrMagnitude < 0.0001f) return;   // unchanged
            m.Grid = grid;   // LIVE position only — drives hit adjudication. The on-map ICON is NOT moved here: it stays
                             // frozen at the last scouted position (RevealedGrid) until a recon re-snapshots it. Re-scout
                             // to see where they went. (SnapshotRevealPos is the only place the icon transform moves.)
            _moved++;
        }

        // Freeze the on-map icon at the enemy's CURRENT live position — called when a recon reveal spots this mirror, so
        // the marker shows where they were AT SCOUT TIME and holds there (it doesn't track their live movement). Re-
        // scouting calls this again to refresh the snapshot to their new position.
        private static void SnapshotRevealPos(Mirror m)
        {
            if (m == null) return;
            m.RevealedGrid = m.Grid; m.HasRevealedPos = true;
            try { if (m.Entity != null) m.Entity.Position = new Vector3(m.Grid.x, m.Grid.y, 0f); } catch { }
            try { if (m.Go != null) m.Go.transform.localPosition = new Vector3(m.Grid.x, m.Grid.y, 0f); } catch { }
            try { if (m.Loc != null) m.Loc.RecalculateAndRegister(false); } catch { }
        }

        // Reflect the victim's authoritative health (rode in on their POS keyframe) onto their mirror on THIS map.
        private static void ApplyMirrorHealth(Mirror m, float health)
        {
            if (m == null) return;
            health = Clamp(health);
            if (m.Health == health) return;
            float before = m.Health;
            m.Health = health;
            try { if (m.Entity != null) { m.Entity.Health = HpInt(health); m.Entity.State = StateForHealth(health); } } catch { }
            try { if (m.Loc != null) m.Loc.RecalculateAndRegister(false); } catch { }
            if (health < before) { try { ReportEnemyHit(health); } catch { } }   // our team just landed a hit on this enemy
        }

        // ACQUISITION (recon-card spotting). The
        // enemy turret mirror is fog-hidden by DEFAULT (RevealUntil=0) and only shown while a recon sweep has it spotted
        // (OnReconReveal pushes RevealUntil forward). We OWN the mirror's on-map visibility outright rather than relying
        // on the native fog touching a mod-spawned clone: force its icon Image on while revealed, off otherwise. The
        // entity's own Update keeps re-evaluating fog, so we re-assert every frame. The reveal lever is Image_Icon.enabled
        // (a render-dump showed the fog HIDES by disabling that Image, with the CanvasGroup alpha already 1); we drive the
        // CanvasGroup too as a belt. Runs in BOTH build flavors — this is the shipping mechanic now, not a test override.
        private static void ApplyMirrorVisibility()
        {
            float now = Time.unscaledTime;
            foreach (var kv in _mirrors)
            {
                var m = kv.Value; if (m == null || m.Loc == null) continue;
                bool show = now < m.RevealUntil;
                try { var img = m.Loc.Image_Icon; if (img != null && img.enabled != show) img.enabled = show; } catch { }
                var v = m.Vis;
                if (v == null) { try { v = m.Vis = m.Loc.VisibilityGroup; } catch { } }
                if (v != null) { try { v.alpha = show ? 1f : 0f; v.interactable = show; v.blocksRaycasts = show; } catch { } }
            }
        }

        // Harmony POSTFIX (CoopSim) on State_SpawnScoutPlane.OnEnter — a SCOUT-PLANE recon card just launched its flyover.
        // The photo doesn't land yet; it "develops" a few seconds later as a registered reveal REGION whose fog-tile
        // children are the exact footprint (see OnReconRegionRegistered). So here we just ARM a window to accept that
        // region, and stash the dialed coordinate as a fallback (used only if no region ever registers). Gated to a PvP
        // card window so a mission-scripted plane can't trigger it. The FORWARD OBSERVER uses a DIFFERENT node
        // (State_SpawnMapEntity) and never flies a plane, so it never arms this → it correctly never reveals the map.
        public static void OnScoutPlanePhoto(SleepyNodes.State_SpawnScoutPlane __instance)
        {
            try
            {
                if (!Active() || _mirrors.Count == 0) return;
                bool card = false; try { card = PvpMatch.CardGraphActive(); } catch { }
                if (!card) return;   // mission-scripted plane, not a player recon card — ignore

                float now = Time.unscaledTime;
                _scoutWindowUntil = now + ScoutWindowSec;
                _scoutHandled = false;
                _lastScoutLaunch = now;
                // NOTE: handle tracking is PERSISTENT (ScanHandles / _knownHandles), not re-baselined per pass — so a photo
                // that develops late is still seen as new even if another pass launched meanwhile. Don't clear it here.

                // Fallback target: the dialed recon coordinate (or the node's inline grid). Only used if no region lands.
                bool have = CoopPunchcards.TryGetReconTarget(out Vector2 center);
                if (!have || center == Vector2.zero) { if (TryGetNodeGrid(__instance, out Vector2 ng)) { center = ng; have = true; } }
                _scoutFallbackArmed = have;
                _scoutFallbackCenter = center;
                _scoutFallbackAt = now + FallbackAfterSec;

                Diagnostics.V($"[pvp] scout plane launched — waiting for the photo to develop into a reveal region (fallback {(have ? $"circle @ ({center.x:0.0},{center.y:0.0}) r={PhotoRadius:0.0} in {FallbackAfterSec:0}s" : "none")})");

#if !PUBLIC_BUILD
                try { ArmReconWatch(__instance); } catch (Exception e) { Log.LogWarning("[pvp-recon] arm: " + e.Message); }
#endif
            }
            catch (Exception e) { Log.LogWarning("[pvp] scout photo: " + e.Message); }
        }

        // Harmony POSTFIX (CoopSim) on MapReconClearer.Register — fires the instant the developed photo's reveal region is
        // registered. If it lands inside an armed scout window, that handle IS the photographed footprint: capture it (its
        // children populate over the next frames, so defer the read by HandleSettleSec). Ignored outside a scout window
        // (scene/forward-observer recon) so only a player scout plane reveals the enemy.
        public static void OnReconRegionRegistered(MapReconClearHandle handle)
        {
            try
            {
                if (!Config.PvpActive || handle == null) return;
                if (Time.unscaledTime >= _scoutWindowUntil) return;   // not a player scout photo
                CaptureScoutHandle(handle);
            }
            catch (Exception e) { Log.LogWarning("[pvp] recon region: " + e.Message); }
        }

        private static void CaptureScoutHandle(MapReconClearHandle handle)
        {
            if (handle == null) return;
            int id; try { var go = handle.gameObject; if (go == null) return; id = go.GetInstanceID(); } catch { return; }
            if (_scoutHandlesCaptured.Contains(id)) return;
            _scoutHandlesCaptured.Add(id);
            _pendingRegions.Add(new PendingRegion { H = handle, At = Time.unscaledTime + HandleSettleSec });
        }

        // PERSISTENT recon-region detection — runs every tick while in a PvP mission (throttled). The first scan baselines
        // the handles already in the scene; after that, any NEWLY-appeared handle is a developed photo. If it shows up while
        // a scout window is open, it's a player's scout photo → capture it (its children = the footprint). Tracking is
        // mission-global (not re-baselined per pass), so a photo that develops late is still caught even after another pass.
        private static void ScanHandles(float now)
        {
            if (now < _nextHandleScan) return;
            _nextHandleScan = now + HandleScanInterval;
            try
            {
                var hs = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapReconClearHandle>(), FindObjectsSortMode.None);
                if (hs == null) return;
                bool windowActive = now < _scoutWindowUntil;
                for (int i = 0; i < hs.Length; i++)
                {
                    var h = hs[i].TryCast<MapReconClearHandle>(); if (h == null) continue;
                    var go = h.gameObject; if (go == null) continue;
                    int id = go.GetInstanceID();
                    if (_knownHandles.Contains(id)) continue;
                    _knownHandles.Add(id);
                    if (!_handlesInit) continue;   // first scan: baseline existing scene handles, never a photo
                    if (windowActive)
                    {
                        Diagnostics.V($"[pvp] scout photo region detected (+{now - _lastScoutLaunch:0.0}s after launch) — resolving footprint");
                        CaptureScoutHandle(h);
                    }
#if !PUBLIC_BUILD
                    else Log.LogInfo($"[pvp-recon] new recon handle id={id} but no scout window active — ignored");
#endif
                }
                _handlesInit = true;
            }
            catch (Exception e) { Log.LogWarning("[pvp] handle scan: " + e.Message); }
        }

        // Per-frame: resolve captured photo regions into reveals once their children populate; if nothing landed in time,
        // fall back to the dialed circle as a backstop. (Detection itself is in ScanHandles, which runs every tick.)
        private static void ProcessScoutFootprint(float now)
        {
            // Resolve settled regions into a footprint reveal.
            for (int i = _pendingRegions.Count - 1; i >= 0; i--)
            {
                var pr = _pendingRegions[i];
                if (pr == null) { _pendingRegions.RemoveAt(i); continue; }
                if (now < pr.At) continue;
                _pendingRegions.RemoveAt(i);
                RevealMirrorsInHandle(pr.H);
                _scoutHandled = true;            // a photo resolved — do NOT also fire the circle (even if it revealed 0)
                _scoutFallbackArmed = false;
            }

            // Fallback: no region ever landed — give the player the dialed-circle reveal so they still get intel.
            if (_scoutFallbackArmed && !_scoutHandled && now >= _scoutFallbackAt)
            {
                _scoutFallbackArmed = false;
                int n = RevealMirrorsNear(_scoutFallbackCenter, PhotoRadius);
                Diagnostics.V($"[pvp] scout photo never registered a region — fallback circle @ ({_scoutFallbackCenter.x:0.0},{_scoutFallbackCenter.y:0.0}) r={PhotoRadius:0.0} -> spotted {n} unit(s)");
            }

            if (now >= _scoutWindowUntil && _pendingRegions.Count == 0) _scoutWindowUntil = 0f;   // close the window
        }

        private static bool SaneGrid(Vector2 g) { return Mathf.Abs(g.x) < 50f && Mathf.Abs(g.y) < 50f; }

        // Perpendicular distance from point p to the infinite LINE through a-b (NOT clamped to the segment). The scout
        // plane photographs the full traverse along the bearing, so the strip runs the whole line a->b direction across
        // the map, not just the short a->b marker span — clamping to the segment wrongly drops units past the markers.
        private static float DistToLine(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return Vector2.Distance(p, a);   // degenerate (no direction known) -> point
            float cross = (p.x - a.x) * ab.y - (p.y - a.y) * ab.x;
            return Mathf.Abs(cross) / Mathf.Sqrt(len2);
        }

        // Reveal only the enemy mirrors that fall on the photographed STRIP — an ORIENTED line along the dialed bearing,
        // NOT a circle or an axis-aligned box. The handle (P0) and its on-map child 'Parent' (P1) give the strip's
        // DIRECTION; the plane photographs the full traverse along it, so we use perpendicular distance to the LINE
        // through P0->P1 (not the short P0->P1 segment — the strip continues past the markers). Children that resolve to
        // canvas-pixel space (off-map UI 'Plane Flyover', e.g. (-530,968)) are filtered. A mirror is "on the photo" if
        // it's within FootprintMargin (the strip half-width) of that line. Each spotted mirror's icon is snapshotted.
        private static int RevealMirrorsInHandle(MapReconClearHandle h)
        {
            if (h == null) return 0;
            Transform fmr = ResolveParent();

            // Collect sane on-map points: the handle (p0) and its children. The strip runs from the handle to the
            // child farthest from it (the far end of the photo run along the bearing).
            Vector2 p0; bool haveP0 = false;
            Vector2 hg = WorldToGrid(fmr, h.transform.position);
            if (SaneGrid(hg)) { p0 = hg; haveP0 = true; } else p0 = hg;
            Vector2 p1 = p0; float far = 0f;
            try
            {
                var ch = h._allChildren;
                if (ch != null) for (int i = 0; i < ch.Count; i++)
                {
                    var c = ch[i]; if (c == null) continue;
                    Vector2 g; try { g = WorldToGrid(fmr, c.transform.position); } catch { continue; }
#if !PUBLIC_BUILD
                    float w = 0f, ht = 0f; bool hasR = false; try { var rt = c.GetComponent<RectTransform>(); if (rt != null) { hasR = true; var rc = rt.rect; w = rc.width; ht = rc.height; } } catch { }
                    string nm = null; try { nm = c.name; } catch { }
                    Log.LogInfo($"[pvp-recon]   child '{nm}' grid ({g.x:0.0},{g.y:0.0}) sane={SaneGrid(g)} rect={hasR}({w:0.0}x{ht:0.0})");
#endif
                    if (!SaneGrid(g)) continue;
                    if (!haveP0) { p0 = g; p1 = g; haveP0 = true; continue; }
                    float d = Vector2.Distance(g, p0); if (d > far) { far = d; p1 = g; }
                }
            }
            catch { }
            if (!haveP0) return 0;

            float until = now2() + ReconRevealSec; int revealed = 0;
            foreach (var kv in _mirrors)
            {
                var m = kv.Value; if (m == null) continue;
                float d = DistToLine(m.Grid, p0, p1);
                bool on = d <= FootprintMargin;
#if !PUBLIC_BUILD
                Log.LogInfo($"[pvp]   footprint vs mirror '{m.ID}' grid ({m.Grid.x:0.0},{m.Grid.y:0.0}) strip-line ({p0.x:0.0},{p0.y:0.0})->({p1.x:0.0},{p1.y:0.0}) perp={d:0.0} w={FootprintMargin:0.0} -> {(on ? "SPOTTED" : "not on photo")}");
#endif
                if (on) { RevealOne(m, until); revealed++; }
            }
            Diagnostics.V($"[pvp] scout photo strip-line ({p0.x:0.0},{p0.y:0.0})->({p1.x:0.0},{p1.y:0.0}) w={FootprintMargin:0.0} -> spotted {revealed} unit(s) for {ReconRevealSec:0}s");
            return revealed;
        }

        private static float now2() { try { return Time.unscaledTime; } catch { return 0f; } }

        // World position -> map grid (FMR-local), the same space mirror grids use. Parent-agnostic, so it works for any
        // map GameObject (fog tiles, recon-region children) regardless of where they're parented.
        private static Vector2 WorldToGrid(Transform fmr, Vector3 world)
        {
            try { if (fmr != null) { var l = fmr.InverseTransformPoint(world); return new Vector2(l.x, l.y); } } catch { }
            return new Vector2(world.x, world.y);
        }

        // Fallback recon target: read the scout-plane node's own LocationToSpawn. When it's an INLINE grid we use it
        // directly; a CONTEXT location is logged but can't be resolved without the live mission graph (the dialed
        // PunchcardVariable is the right source in that case). Best-effort + diagnostic; wrapped so any interop quirk
        // just disables the fallback rather than throwing.
        private static bool TryGetNodeGrid(SleepyNodes.State_SpawnScoutPlane node, out Vector2 grid)
        {
            grid = Vector2.zero;
            try
            {
                var loc = node != null ? node.LocationToSpawn : null;
                if (loc == null) return false;
                int lt = (int)loc.LocationType;   // 0 = GridLocation, 1 = ContextLocation
#if !PUBLIC_BUILD
                Log.LogInfo($"[pvp]   node LocationToSpawn type={lt}");
#endif
                if (lt == 0)
                {
                    var gl = loc.GridLocation;
                    if (gl != null)
                    {
                        int sel = (int)gl.SelectionType;   // 0 = Inline, 1 = Context
                        if (sel == 0)
                        {
                            var v = gl.Value;
                            if (v != null) { grid = new Vector2(v.X, v.Y);
#if !PUBLIC_BUILD
                                Log.LogInfo($"[pvp]   node inline grid ({grid.x:0.#},{grid.y:0.#})");
#endif
                                return grid != Vector2.zero; }
                        }
#if !PUBLIC_BUILD
                        else { string ck = null; try { ck = gl.ContextKey; } catch { } Log.LogInfo($"[pvp]   node GridLocation is a Context key='{ck}' (resolve via the dialed PunchcardVariable instead)"); }
#endif
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[pvp] node grid: " + e.Message); }
            return false;
        }

        // Spot one mirror until `until`: arm its reveal AND freeze its on-map icon at the current live position (the
        // scouted snapshot). The single place a reveal is applied, so every path snapshots consistently.
        private static void RevealOne(Mirror m, float until)
        {
            if (m == null) return;
            if (until > m.RevealUntil) m.RevealUntil = until;
            SnapshotRevealPos(m);
        }

        // Reveal enemy mirrors within `radius` grid-units of `center` (the photo footprint) for ReconRevealSec; returns
        // how many were in the photo. Mirrors outside it stay fogged (the scout plane only spots what it photographed).
        private static int RevealMirrorsNear(Vector2 center, float radius)
        {
            float until = Time.unscaledTime + ReconRevealSec;
            float r2 = radius * radius; int n = 0;
            foreach (var kv in _mirrors) { var m = kv.Value; if (m == null) continue; if ((m.Grid - center).sqrMagnitude <= r2) { RevealOne(m, until); n++; } }
            return n;
        }

        // STAR (illumination) shell burst — reveal enemy mirrors within StarRevealRadius of the impact (user spec:
        // "STAR shells reveal enemies within 1 radius"). STAR deals NO damage; revealing is its whole purpose. Local
        // to the firing machine, like the recon-card reveal, reusing the recon reveal duration + icon snapshot path.
        // Called from PvpCombat on every STAR impact (both build flavors — this is shipping behaviour, not a dev tool).
        public const float StarRevealRadius = 1f;   // grid units (== km): a star shell lights up enemies within 1 km
        public static int OnStarShell(Vector2 impact)
        {
            try
            {
                if (!Active() || _mirrors.Count == 0) return 0;
                int n = RevealMirrorsNear(impact, StarRevealRadius);
                Diagnostics.V($"[pvp] STAR shell @ ({impact.x:0.0},{impact.y:0.0}) r={StarRevealRadius:0.0} -> spotted {n} enemy unit(s) for {ReconRevealSec:0}s");
                return n;
            }
            catch (Exception e) { Log.LogWarning("[pvp] star reveal: " + e.Message); return 0; }
        }

#if !PUBLIC_BUILD
        // ---------------- recon footprint telemetry (dev only) ----------------

        // Snapshot the fog/recon state at the moment a scout plane launches, so ReconWatchTick can diff against it and
        // report WHERE the photos actually clear/land (in grid space) over the plane's flight. Observation only.
        private static void ArmReconWatch(SleepyNodes.State_SpawnScoutPlane node)
        {
            _reconFmr = ResolveParent();
            _fogSnap.Clear(); _fogCleared.Clear(); _planeName = null;
            try { var pf = node != null ? node.PlanePrefab : null; if (pf != null) _planeName = pf.name; } catch { }

            int tiles = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapReconClearChild>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i].TryCast<MapReconClearChild>(); if (c == null) continue;
                    var go = c.gameObject; if (go == null) continue;
                    _fogSnap[go.GetInstanceID()] = ReconToGrid(c.transform.position); tiles++;
                }
            }
            catch { }

            int handles = 0, active = 0;
            _handleSnap.Clear(); _handleSeen.Clear();
            try
            {
                var hs = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapReconClearHandle>(), FindObjectsSortMode.None);
                if (hs != null) { handles = hs.Length; for (int i = 0; i < hs.Length; i++) { var h = hs[i].TryCast<MapReconClearHandle>(); if (h == null) continue; var go = h.gameObject; if (go != null) _handleSnap.Add(go.GetInstanceID()); } }
            }
            catch { }
            try { var cl = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapReconClearer>(), FindObjectsSortMode.None); if (cl != null) for (int i = 0; i < cl.Length; i++) { var c = cl[i].TryCast<MapReconClearer>(); if (c != null) { try { active += c.ActiveCount; } catch { } } } } catch { }

            _reconWatchUntil = Time.unscaledTime + 8f;
            _nextReconLog = 0f;
            Log.LogInfo($"[pvp-recon] watch armed: plane='{_planeName}' fogTiles={tiles} reconHandles={handles} clearerActive={active} fmr={(_reconFmr != null)}");
            foreach (var kv in _mirrors) { var m = kv.Value; if (m != null) Log.LogInfo($"[pvp-recon]   enemy mirror '{m.ID}' at grid ({m.Grid.x:0.0},{m.Grid.y:0.0})"); }
        }

        // Dump a recon handle's footprint in grid space: its own transform position + each managed child (the cells the
        // photo covers). The NEW handle created by a scout run IS the photographed region — its children's grids are the
        // exact footprint we want the reveal to match.
        private static void DumpHandle(MapReconClearHandle h, string tag)
        {
            if (h == null) return;
            Vector2 hg = Vector2.zero; try { hg = ReconToGrid(h.transform.position); } catch { }
            int cc = 0; string pts = "";
            try
            {
                var ch = h._allChildren;
                if (ch != null)
                {
                    cc = ch.Count;
                    for (int i = 0; i < ch.Count && i < 16; i++)
                    {
                        var c = ch[i]; if (c == null) continue;
                        Vector2 g = Vector2.zero; try { g = ReconToGrid(c.transform.position); } catch { }
                        pts += $" ({g.x:0.0},{g.y:0.0})";
                    }
                }
            }
            catch { }
            Log.LogInfo($"[pvp-recon]   {tag} handle @ grid ({hg.x:0.0},{hg.y:0.0}) children={cc}:{pts}");
        }

        // Each poll during the watch: report fog tiles that have disappeared (cleared = photographed) with their grid,
        // the plane clone's path, and live handle/clearer counts. The union of cleared-tile grids is the real footprint.
        private static void ReconWatchTick(float now)
        {
            if (now >= _reconWatchUntil) { _reconWatchUntil = 0f; Log.LogInfo($"[pvp-recon] watch done: {_fogCleared.Count} fog tile(s) cleared total"); return; }
            if (now < _nextReconLog) return;
            _nextReconLog = now + 0.5f;

            var present = new HashSet<int>();
            int tilesNow = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapReconClearChild>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i].TryCast<MapReconClearChild>(); if (c == null) continue;
                    var go = c.gameObject; if (go == null) continue;
                    present.Add(go.GetInstanceID()); tilesNow++;
                }
            }
            catch { }

            int newly = 0;
            foreach (var kv in _fogSnap)
            {
                if (!present.Contains(kv.Key) && !_fogCleared.Contains(kv.Key))
                {
                    _fogCleared.Add(kv.Key); newly++;
                    Log.LogInfo($"[pvp-recon]   fog CLEARED @ grid ({kv.Value.x:0.0},{kv.Value.y:0.0})");
                }
            }

            // NEW recon handle(s) = the region(s) this scout run revealed. Dump each once — its children's grids are the
            // exact photo footprint the reveal should match (fog tiles don't get destroyed, so this is the real signal).
            try
            {
                var hs = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<MapReconClearHandle>(), FindObjectsSortMode.None);
                if (hs != null) for (int i = 0; i < hs.Length; i++)
                {
                    var h = hs[i].TryCast<MapReconClearHandle>(); if (h == null) continue;
                    var go = h.gameObject; if (go == null) continue;
                    int id = go.GetInstanceID();
                    if (_handleSnap.Contains(id) || _handleSeen.Contains(id)) continue;
                    _handleSeen.Add(id);
                    DumpHandle(h, "NEW");
                }
            }
            catch { }

            // plane clone flight path (best-effort name match — there's no dedicated scout-plane component)
            if (!string.IsNullOrEmpty(_planeName))
            {
                try
                {
                    var clone = GameObject.Find(_planeName + "(Clone)");
                    if (clone == null) clone = GameObject.Find(_planeName);
                    if (clone != null) { var g = ReconToGrid(clone.transform.position); Log.LogInfo($"[pvp-recon]   plane @ grid ({g.x:0.0},{g.y:0.0})"); }
                }
                catch { }
            }

            if (newly > 0 || tilesNow != _fogSnap.Count)
                Log.LogInfo($"[pvp-recon] tilesNow={tilesNow} (snap={_fogSnap.Count}) clearedTotal={_fogCleared.Count}");
        }

        private static Vector2 ReconToGrid(Vector3 world)
        {
            try { if (_reconFmr != null) { var l = _reconFmr.InverseTransformPoint(world); return new Vector2(l.x, l.y); } } catch { }
            return new Vector2(world.x, world.y);
        }

        // DEV force-spot ALL enemy mirrors (Ctrl+Shift+R) — exercise the move/hit lane without flying a real recon photo.
        public static void OnReconReveal()
        {
            try
            {
                if (!Active() || _mirrors.Count == 0) return;
                float until = Time.unscaledTime + ReconRevealSec;
                foreach (var kv in _mirrors) { var m = kv.Value; if (m != null) { m.RevealUntil = until; SnapshotRevealPos(m); } }
                Log.LogInfo($"[pvp] DEV reveal-all — {_mirrors.Count} mirror(s) spotted for {ReconRevealSec:0}s");
            }
            catch (Exception e) { Log.LogWarning("[pvp] dev reveal: " + e.Message); }
        }
#endif

        // ---------------- damage (Phase 2 shot lane, victim-authoritative) ----------------

        // Called by PvpCombat when a peer reports their shell hit MY team. The hit is addressed to the team CAPTAIN (the
        // only member with a mirror), so this runs on the captain, who owns the shared team health: decrement it and
        // push an immediate keyframe so the attacker's mirror AND my teammates update without waiting for the next tick.
        public static void ApplyTeamDamage(float dmg, ulong from)
        {
            if (dmg <= 0f || _eliminated) return;
            float before = _teamHealth;
            _teamHealth = Clamp(_teamHealth - dmg);
            Diagnostics.V($"[pvp] team took {dmg:0.#} damage from peer {from} ({before:0.#} -> {_teamHealth:0.#})");
            try { PvpEffects.OnTeamHit(before - _teamHealth, _teamHealth); } catch { }   // captain's own hit cue
            try { ReportTeamHit(_teamHealth); } catch { }   // teleprinter: we are hit + remaining strength (captain)
            if (_teamHealth <= 0f) { _eliminated = true; Log.LogWarning("[pvp] === YOUR TEAM WAS ELIMINATED ==="); }
            try { BroadcastMyPosition(); } catch { }   // immediate health keyframe (captain) → attacker + teammates
        }

        // Non-captain: adopt the shared team health announced by my captain's keyframe (HUD + elimination). The captain
        // is authoritative — I never broadcast it myself.
        private static void AdoptTeamHealth(float health)
        {
            health = Clamp(health);
            float drop = _teamHealth - health;
            _teamHealth = health;
            _eliminated = health <= 0f;
            if (drop > 0f) { try { PvpEffects.OnTeamHit(drop, health); } catch { } try { ReportTeamHit(health); } catch { } }   // teammate feels the captain's hit
        }

        private static bool SafeAmICaptain() { try { return PvpTeams.AmICaptain; } catch { return false; } }

        // Reap any mirror whose origin is no longer the enemy team's captain — a non-captain's mirror lingering from the
        // pre-roster fallback, or one left after a captain hand-off. One vehicle per enemy team. No-op until roster known.
        private static void PruneNonCaptainMirrors()
        {
            bool roster = false; try { roster = PvpTeams.RosterKnown; } catch { }
            if (!roster || _mirrors.Count == 0) return;
            _toRemove.Clear();
            foreach (var kv in _mirrors) { try { if (!PvpTeams.IsCaptain(kv.Key)) _toRemove.Add(kv.Key); } catch { } }
            for (int i = 0; i < _toRemove.Count; i++) OnPeerLeft(_toRemove[i]);
        }

        // Declare the match result locally from the team-health state (2-team duel): my team at 0 = Lost; every enemy
        // mirror destroyed = Won. Latched + logged once; the HUD reads MatchOver/Won.
        private static void CheckMatchEnd()
        {
            if (_result != Result.None) return;
            if (_eliminated) { _result = Result.Lost; Log.LogWarning("[pvp] === MATCH OVER — YOUR TEAM LOST ==="); try { PvpEffects.OnMatchResult(false); } catch { } return; }
            if (_mirrors.Count == 0) return;
            foreach (var kv in _mirrors) { var m = kv.Value; if (m != null && m.Health > 0f) return; }   // an enemy still alive
            _result = Result.Won; Log.LogWarning("[pvp] === MATCH OVER — YOUR TEAM WON ==="); try { PvpEffects.OnMatchResult(true); } catch { }
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

        private static float Clamp(float h) => h < 0f ? 0f : (h > MaxHealth ? MaxHealth : h);

        // MapEntity.Health / MaxHealth are ints; round the float HP UP for the on-map marker so a nest with a
        // fractional point left (e.g. 3.5 after an HCHE hit) still reads as alive (>=1), never a false "destroyed".
        private static int HpInt(float h) => Mathf.Max(0, Mathf.CeilToInt(h));

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
                Diagnostics.V($"[pvp] map icons: [{all}] -> mirrors use '{_iconId}' (sprite={_iconSprite != null})");
            }
            catch (Exception e) { Log.LogWarning("[pvp] resolve icon: " + e.Message); }
        }

        private static MapEntityStates StateForHealth(float health)
        {
            if (health <= 0f) return MapEntityStates.Destroyed;
            if (health < MaxHealth / 2f) return MapEntityStates.Damaged;
            return MapEntityStates.None;
        }

        // ---------------- helpers ----------------

        // My own map grid = MY TEAM's (randomized, host-assigned) spawn — teammates coincide → one shared vehicle
        // position. Falls back to host/client role for the team mapping until the roster is known (the first frames after
        // join); _team0Spawn/_team1Spawn hold the random values once assigned, else the fixed defaults.
        private static Vector2 MyGrid()
        {
            int team = -1; try { team = PvpTeams.MyTeam; } catch { }
            if (team == 0) return _team0Spawn;
            if (team == 1) return _team1Spawn;
            return CoopP2P.IsHost ? _team0Spawn : _team1Spawn;   // roster not resolved yet — fall back to role
        }

        // The grid we BROADCAST as our turret's position. Once our turret is pinned (PlaceMyTurret), read the LIVE
        // turretBase.anchoredPosition — which IS map-grid space (the placement log confirmed anchored ≈ the target grid),
        // so when the player RELOCATES the turret with a requisition card the enemy's mirror of us follows the real turret
        // and their learned firing solution goes stale (they must recon + re-aim). Before placement settles, broadcast the
        // intended team grid so a frame of the scene-default origin (≈1.6,1.6) never leaks to the enemy.
        private static Vector2 MyTurretGrid()
        {
            if (_turretPlaced)
            {
                try { var t = TurretController.Instance; var bas = t != null ? t.turretBase : null; if (bas != null) return bas.anchoredPosition; }
                catch { }
            }
            return MyGrid();
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

        // ---------------- teleprinter battery-position report ----------------

        // Announce MY battery's grid on the teleprinter — once when it deploys, and again after a requisition move card
        // relocates it. Settle-detection unifies both: the turret jumps to its spawn on placement (DEPLOYED) and
        // traverses on a move card (RELOCATED); each time it goes stationary at a NEW grid we print. (In a bare PvP
        // arena only a requisition move card can move the turret — the mission's own move nodes are suppressed.)
        private static void TickTurretPositionReport(float now)
        {
            if (!_turretPlaced) return;
            Vector2 g = MyTurretGrid();
            if ((g - _lastTurretGrid).magnitude > TurretMoveEps) { _lastTurretGrid = g; _turretStableSince = now; return; }  // still moving — reset settle clock
            if (now - _turretStableSince < TurretSettleSec) return;             // not stationary long enough yet
            if ((g - _announcedGrid).magnitude < AnnounceMinDeltaGrid) return;  // already announced this position
            _announcedGrid = g;
            bool deployed = _posAnnounceCount == 0;
            _posAnnounceCount++;
            AnnounceTurretPosition(g, deployed);
        }

        // Print a short field message to the (local) teleprinter stating our battery's map grid. PvP teleprinters are
        // per-player (CoopOrders never replicates/suppresses while PvpActive), so this prints only on this machine —
        // exactly what we want: each crew member's printer reports their own (shared-team) battery position.
        private static void AnnounceTurretPosition(Vector2 grid, bool deployed)
        {
            string label = GridLabel(grid);
            PrintTeleprinter("PVP_BATTERY", deployed ? "FRIENDLY BATTERY DEPLOYED" : "FRIENDLY BATTERY RELOCATED", $"OWN POSITION  GRID {label}");
            Diagnostics.V($"[pvp] teleprinter: own battery {(deployed ? "DEPLOYED" : "RELOCATED")} at GRID {label} ({grid.x:0.0},{grid.y:0.0} km)");
        }

        // Our shell landed on an enemy battery (its shared-team HP just dropped) — confirm the hit + the enemy's
        // remaining strength on the (local) teleprinter. Fires on every member of our team (each holds the enemy mirror)
        // and regardless of fog: it's hit confirmation, NOT a position reveal (the enemy grid is only on the map once
        // recon/STAR spots it). Driven from ApplyMirrorHealth on a health DROP.
        private static void ReportEnemyHit(float health)
        {
            if (health <= 0f) PrintTeleprinter("PVP_REPORT", "ENEMY BATTERY DESTROYED", "TARGET NEUTRALISED");
            else PrintTeleprinter("PVP_REPORT", "ENEMY BATTERY HIT", $"ENEMY STRENGTH  {health:0.#}/{MaxHealth:0.#}");
        }

        // An enemy shell hit US — report it + our remaining strength on the (local) teleprinter. Fires on every member
        // of our team: the captain from ApplyTeamDamage, teammates from AdoptTeamHealth (the captain's keyframe).
        private static void ReportTeamHit(float newHealth)
        {
            if (newHealth <= 0f) PrintTeleprinter("PVP_REPORT", "FRIENDLY BATTERY ELIMINATED", "WE ARE OUT OF ACTION");
            else PrintTeleprinter("PVP_REPORT", "INCOMING - WE ARE HIT", $"FRIENDLY STRENGTH  {newHealth:0.#}/{MaxHealth:0.#}");
        }

        // Submit 1-2 field-report lines to the (local) Primary teleprinter, shared by every PvP report. PvP teleprinters
        // are per-player (CoopOrders' replicate + blank both bail on PvpActive), so this prints only on this machine.
        private static void PrintTeleprinter(string sourceId, string line1, string line2)
        {
            try
            {
                Teleprinter tp = null;
                try { tp = Teleprinter.GetTeleprinter(Teleprinter.Teleprinters.Primary); } catch { }
                if (tp == null)
                {
                    try { var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Teleprinter>(), FindObjectsSortMode.None); if (arr != null && arr.Length > 0) tp = arr[0].TryCast<Teleprinter>(); } catch { }
                }
                if (tp == null) { Log.LogWarning("[pvp] no teleprinter for report '" + line1 + "'"); return; }
                var lines = new Il2CppSystem.Collections.Generic.List<string>();
                lines.Add(line1);
                if (!string.IsNullOrEmpty(line2)) lines.Add(line2);
                tp.SubmitLines(sourceId, lines.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>(), null, false);
                tp.TryStart(false);
            }
            catch (Exception e) { Log.LogWarning("[pvp] teleprinter print: " + e.Message); }
        }

        // Format an FMR-local grid position (km; 1 unit = 1 major cell) as the game's grid reference. Convention
        // (user-confirmed 2026-06-29): major columns A-T LEFT->RIGHT (A at the grid's left edge), major rows 1-10
        // BOTTOM->TOP (1 at the bottom); inside each major square the sub-grid runs 1-10 LEFT->RIGHT (sub X) and
        // 1-10 BOTTOM->TOP (sub Y), each sub-cell 0.1 km. Origin is taken from the arena's real grid min (FireMission
        // bounds) so an offset map space still labels correctly; falls back to (0,0) = the user-confirmed 20x10 box.
        private static string GridLabel(Vector2 grid)
        {
            Vector2 mn = FallbackGridMin;
            try { if (TryGetMapGridBounds(out Vector2 bmn, out Vector2 _)) mn = bmn; } catch { }
            float lx = grid.x - mn.x, ly = grid.y - mn.y;                       // position from the grid's bottom-left
            int col = Mathf.Clamp(Mathf.FloorToInt(lx), 0, 25);                 // 0 => 'A'
            int row = Mathf.FloorToInt(ly) + 1;                                 // 0 => row 1 (bottom)
            int subX = Mathf.Clamp(Mathf.FloorToInt((lx - Mathf.Floor(lx)) * 10f + 1e-4f) + 1, 1, 10);  // 1..10 left->right
            int subY = Mathf.Clamp(Mathf.FloorToInt((ly - Mathf.Floor(ly)) * 10f + 1e-4f) + 1, 1, 10);  // 1..10 bottom->top
            char letter = (char)('A' + col);
            return $"{letter}{row} ({subX},{subY})";
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

        public static float MyHealth => _teamHealth;   // my TEAM's shared health (named for HUD compat)
        public static bool Eliminated => _eliminated;
        public static bool MatchOver => _result != Result.None;
        public static bool Won => _result == Result.Won;
        public static Vector2 MyGridPublic => MyTurretGrid();   // live turret grid (tracks a card relocate)
        public static int MirrorCount => _mirrors.Count;

        // True while at least one enemy mirror is currently spotted (a recon sweep is in effect). For the HUD cue.
        public static bool EnemyAcquired
        {
            get { float now = Time.unscaledTime; foreach (var kv in _mirrors) { var m = kv.Value; if (m != null && now < m.RevealUntil) return true; } return false; }
        }

        // First opponent mirror (1v1 = the only one). Returns its map grid + current health.
        public static bool TryGetFirstEnemy(out Vector2 grid, out float health)
        {
            grid = Vector2.zero; health = 0f;
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

        public static string Status() => $"pvpPlayers: active={Active()} teamHp={_teamHealth:0.#}{(_eliminated ? " ELIM" : "")}{(_result != Result.None ? " " + _result : "")} cap={SafeAmICaptain()} mirrors={_mirrors.Count} spawn={(_spawnsAssigned ? $"t0({_team0Spawn.x:0.0},{_team0Spawn.y:0.0})/t1({_team1Spawn.x:0.0},{_team1Spawn.y:0.0})" : "pending")} sent={_sent} spawned={_spawned} moved={_moved}";
    }
}
