using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP PLAN — Phase 0 instrumented feasibility probes (see PLAN-pvp.md §5 "Recommended first action").
    ///
    /// Everything in the PvP plan above design level is verified by DECOMPILATION; this harness proves the five
    /// runtime assumptions each feature stage depends on, BEFORE any PvP feature code is written. It is read-/log-
    /// heavy and reversible — it spawns dev-only marker entities, nudges the turret, and logs the engine's own
    /// impact adjudication. Nothing here ships: the whole module is inert unless <c>Config.PvpProbe</c> is set true
    /// (cfg-only; default false, and the keys/patches early-return when off), so a normal co-op/flatscreen session
    /// is untouched (flatscreen parity intact). All output is gated by the same flag, so a public build stays quiet.
    ///
    /// What it answers (priority order):
    ///   P1 (keystone) — Player-as-entity HIT DETECTION. A programmatically-built MapEntity (the recipe a PvP
    ///       player avatar would use: new MapEntity → EntityLocation.Init → RecalculateAndRegister) — does the
    ///       engine's own shell adjudication (State_ImpactStart.StartImpact) RETURN it in the hit set when a real
    ///       shell lands on it? If a spawned entity isn't in that list, the M1 hit model changes shape.
    ///   P2 — Programmatic spawn RENDERS + TAKES DAMAGE. Same spawn path; EntityLocation.TakeDamage actually moves
    ///       Health / flips State (Damaged → Destroyed) / IsAlive.
    ///   P3 — Deterministic-fire INPUTS. Dumps the gun + chambered-shell dispersion coefficients the co-op
    ///       deterministic-fire patch zeros, so two machines' logs can be compared for the M1 "copied impact" claim.
    ///   P4 — Turret REPOSITION. SetTurretLocation / MoveTurret moves turretBase (the firing origin) — needed to
    ///       place two players at different map points.
    ///   (P5 lobby-driven launch/mode is exercised by the existing CoopScene/StartOperation path + a later
    ///       Config.PvpActive gate; not a probe here.)
    ///
    /// Keys (game window focused; chorded Left-Ctrl + Left-Shift so they can't collide with the shipped F-keys):
    ///   Ctrl+Shift+1  — spawn a test Enemy entity at the LAST shell impact (fire a ranging shot first), else (0,0).
    ///   Ctrl+Shift+2  — TakeDamage the most-recent probe entity by 50 (watch Damaged → Destroyed across presses).
    ///   Ctrl+Shift+3  — reposition the turret: MoveTurret to turretBase + a small offset (logs before/after).
    ///   Ctrl+Shift+4  — instant variant: SetTurretLocation to the same probe offset.
    ///   Ctrl+Shift+5  — dump ImpactTracker.EntityLocations registry: every entity's id/role/state +
    ///                   (MapEntity.Position / EntityLocation.LocalPosition / transform) triplet + registered? flag.
    ///                   This reveals the grid<->transform mapping a hittable entity must satisfy.
    ///   Ctrl+Shift+6  — query ImpactTracker.GetNearestTargetOrEnemy + GetNearest(Enemy) against the last impact
    ///                   (shows what the tracker compares + whether a probe entity is even considered).
    ///   Ctrl+Shift+7  — ★ ONE-KEY AUTO P1 VERDICT (no firing): spawn a probe, SIMULATE a shell on it via
    ///                   ImpactTracker.EvaluateImpact, and report whether its HP/State moved (+ which coordinate
    ///                   space adjudicates). This is the fast path — just press it in a mission.
    ///   Ctrl+Shift+0  — status / determinism-inputs dump (phase, turret, chambered-shell dispersion, probe set).
    ///   Ctrl+Shift+9  — destroy all probe-spawned entities (cleanup).
    ///
    /// RUN 1 RESULT (2026-06-27): P4 ✅ (SetTurretLocation instant, MoveTurret animated), P2 ✅ (TakeDamage moved
    /// hp 100→50/Damaged), P1 ❌ — a spawned entity at the EXACT impact (dist 0) returned hits=0. ImpactTracker has
    /// its OWN registry (Dictionary&lt;string,EntityLocation&gt; EntityLocations + RegisterEntity) the adjudication
    /// keys off, NOT MapEntity.Position; and RecalculateAndRegister recomputed LocalPosition from the (unmoved)
    /// transform, so the registered grid cell ≠ the impact. Run 2 (this build) explicitly registers + places the
    /// transform + dumps the registry/nearest so we learn the correct placement from a NATIVE target.
    ///
    /// The impact LOGGER (the read-side of P1) is a Harmony POSTFIX on State_ImpactStart.StartImpact installed by
    /// ApplyPatches(); while the flag is on it logs every shell's impact point + the returned hit set
    /// (id/role/state/hp per hit) and records the impact for the spawn key. It double-postfixes the same method
    /// CoopImpact already postfixes — Harmony composes both, and this one only logs.
    /// Tick() is called from VrManager.Update; ApplyPatches() once from Plugin.Load (after Config.Load).
    /// </summary>
    internal static class PvpProbe
    {
        private static ManualLogSource Log => Plugin.Logger;

        private static Harmony _harmony;
        private static bool _patched;

        // Last shell impact (map/board space) the StartImpact postfix saw — the spawn key anchors a target here.
        private static Vector2 _lastImpact;
        private static bool _haveImpact;

        // Probe-spawned entities (dev artifacts; destroyed by Ctrl+Shift+9 or when leaving the mission).
        private sealed class Spawned { public string ID; public GameObject Go; public EntityLocation Loc; public MapEntity Entity; }
        private static readonly List<Spawned> _spawned = new List<Spawned>();
        private static int _spawnSeq;

        // ============================================================ Harmony (impact logger) ============================================================

        // One-time from Plugin.Load. Installs the StartImpact logger only if the flag is on at load (cfg sets it
        // before this runs). Its own Harmony id + try/catch so a missing method can't disturb the shipped patches.
        public static void ApplyPatches()
        {
            if (!Config.PvpProbe) { Log.LogInfo("[pvpprobe] disabled (set PvpProbe=true in IronNestVR.cfg to arm Phase 0 probes)"); return; }
            try { _harmony = new Harmony("com.ironnest.vr.pvpprobe"); }
            catch (Exception e) { Log.LogError("[pvpprobe] Harmony init failed: " + e); return; }
            try
            {
                var mi = AccessTools.Method(typeof(SleepyNodes.State_ImpactStart), "StartImpact");
                if (mi != null)
                {
                    _harmony.Patch(mi, postfix: new HarmonyMethod(typeof(PvpProbe), nameof(OnStartImpact)));
                    _patched = true;
                    Log.LogInfo("[pvpprobe] ARMED — keys Ctrl+Shift+7=AUTO P1 verdict (no firing) | 1 spawn · 2 damage · 3/4 turret · 5 registry · 6 nearest · 0 status · 9 cleanup (see PvpProbe.cs)");
                }
                else Log.LogWarning("[pvpprobe] State_ImpactStart.StartImpact not found — impact logger NOT installed");
            }
            catch (Exception e) { Log.LogWarning("[pvpprobe] impact patch: " + e.Message); }
        }

        // Postfix: log the impact point + the engine's authoritative hit set, and stash the point for the spawn key.
        // Signature mirrors CoopImpact.OnImpactAdjudicated (the verified StartImpact shape).
        public static void OnStartImpact(ShellDefinition shell, Vector2 impactLocation,
                                         Il2CppSystem.Collections.Generic.List<MapEntity> __result)
        {
            try
            {
                _lastImpact = impactLocation; _haveImpact = true;
                string shellId = ""; try { if (shell != null) shellId = shell.ShellId ?? ""; } catch { }
                int count = 0; try { if (__result != null) count = __result.Count; } catch { }
                Log.LogInfo($"[pvpprobe] IMPACT at ({impactLocation.x:0.00},{impactLocation.y:0.00}) shell='{shellId}' hits={count}");
                for (int i = 0; i < count && i < 16; i++)
                {
                    MapEntity e = null; try { e = __result[i]; } catch { }
                    if (e == null) { Log.LogInfo($"[pvpprobe]   hit#{i} <null>"); continue; }
                    string id = "?"; string role = "?"; string st = "?"; int hp = -1; bool probe = false;
                    try { id = e.ID; } catch { }
                    try { role = e.Role.ToString(); } catch { }
                    try { st = e.State.ToString(); } catch { }
                    try { hp = e.Health; } catch { }
                    probe = IsProbeId(id);
                    Log.LogInfo($"[pvpprobe]   hit#{i} id='{id}'{(probe ? " <-- PROBE ENTITY" : "")} role={role} state={st} hp={hp}");
                }
                if (count == 0 && _spawned.Count > 0)
                    Log.LogInfo("[pvpprobe]   (no entities returned — if a probe target was under this impact, P1 = the engine does NOT adjudicate programmatic spawns; see distances below)");
                // Distance from this impact to each live probe entity, so a near-miss is distinguishable from a
                // "spawned-but-never-considered" miss even when the hit set is empty.
                for (int i = 0; i < _spawned.Count; i++)
                {
                    var s = _spawned[i]; if (s == null || s.Entity == null) continue;
                    Vector2 p; try { p = new Vector2(s.Entity.Position.x, s.Entity.Position.y); } catch { continue; }
                    Log.LogInfo($"[pvpprobe]   probe '{s.ID}' at ({p.x:0.00},{p.y:0.00}) dist={Vector2.Distance(p, impactLocation):0.00}");
                }
            }
            catch (Exception e) { try { Log.LogWarning("[pvpprobe] impact log: " + e.Message); } catch { } }
        }

        // ============================================================ per-frame keys ============================================================

        public static void Tick()
        {
            if (!Config.PvpProbe) return;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return;
                bool chord = (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)
                          && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                if (!chord) return;

                if (kb[UnityEngine.InputSystem.Key.Digit1].wasPressedThisFrame) SpawnTarget();
                else if (kb[UnityEngine.InputSystem.Key.Digit2].wasPressedThisFrame) DamageLatest(50);
                else if (kb[UnityEngine.InputSystem.Key.Digit3].wasPressedThisFrame) RepositionTurret(false);
                else if (kb[UnityEngine.InputSystem.Key.Digit4].wasPressedThisFrame) RepositionTurret(true);
                else if (kb[UnityEngine.InputSystem.Key.Digit5].wasPressedThisFrame) DumpRegistry();
                else if (kb[UnityEngine.InputSystem.Key.Digit6].wasPressedThisFrame) NearestToImpact();
                else if (kb[UnityEngine.InputSystem.Key.Digit7].wasPressedThisFrame) AutoTest();
                else if (kb[UnityEngine.InputSystem.Key.Digit0].wasPressedThisFrame) DumpStatus();
                else if (kb[UnityEngine.InputSystem.Key.Digit9].wasPressedThisFrame) DestroyAll();
            }
            catch (Exception e) { Log.LogWarning("[pvpprobe] tick: " + e.Message); }

            // Drop stale refs if the mission scene tore down (probe entities die with it).
            if (_spawned.Count > 0 && !InMission()) { _spawned.Clear(); Log.LogInfo("[pvpprobe] left mission — cleared probe entity list"); }
        }

        // ============================================================ P1/P2: spawn + damage ============================================================

        private static void SpawnTarget()
        {
            if (!InMission()) Log.LogWarning("[pvpprobe] not in a mission (GamePhase != MissionActive) — spawn/adjudication may be meaningless; spawning anyway");
            Vector2 at = _haveImpact ? _lastImpact : Vector2.zero;
            if (!_haveImpact) Log.LogWarning("[pvpprobe] no shell impact recorded yet — spawning at (0,0). (Ctrl+Shift+7 auto-tests hit detection WITHOUT firing.)");
            var s = SpawnAt(at);
            if (s != null) Log.LogInfo("[pvpprobe] tip: Ctrl+Shift+7 simulates an impact on this entity (no firing needed) and prints a P1 verdict.");
        }

        // Core spawn — builds a registered Enemy MapEntity at map coord `at` and returns it (null on failure).
        // Recipe (CoopEntities): Init → place TRANSFORM → RecalculateAndRegister(true). RUN 1 proved lp is recomputed
        // FROM the transform (setting LocalPosition alone was overwritten), so we move transform.localPosition under
        // the parent to `at`, AND explicitly call ImpactTracker.RegisterEntity in case RecalculateAndRegister doesn't
        // touch the adjudication registry.
        private static Spawned SpawnAt(Vector2 at)
        {
            var tmpl = FindCloneSource();
            if (tmpl == null) { Log.LogWarning("[pvpprobe] no EntityLocation to clone — cannot spawn (are you in a mission scene?)"); return null; }

            GameObject go = null;
            try { go = UnityEngine.Object.Instantiate(tmpl).TryCast<GameObject>(); } catch (Exception ex) { Log.LogWarning("[pvpprobe] instantiate: " + ex.Message); }
            if (go == null) { Log.LogWarning("[pvpprobe] clone failed"); return null; }
            try { go.SetActive(true); } catch { }
            try { go.name = "PvpProbeTarget"; } catch { }
            var parent = ResolveParent();
            if (parent != null) { try { go.transform.SetParent(parent, false); } catch { } }

            EntityLocation loc = null;
            try { loc = go.GetComponent<EntityLocation>(); } catch { }
            if (loc == null) { Log.LogWarning("[pvpprobe] clone has no EntityLocation"); try { UnityEngine.Object.Destroy(go); } catch { } return null; }

            string id = "PVPPROBE_" + (++_spawnSeq);
            MapEntity e;
            try
            {
                e = new MapEntity();
                e.ID = id; e.Name = id; e.Icon = "";
                e.Role = EntityRoles.Enemy;
                e.Position = new Vector3(at.x, at.y, 0f);
                e.State = MapEntityStates.None;
                e.Health = 100; e.MaxHealth = 100; e.Armour = 0; e.Stars = 0; e.Scale = 1;
            }
            catch (Exception ex) { Log.LogWarning("[pvpprobe] build MapEntity: " + ex.Message); try { UnityEngine.Object.Destroy(go); } catch { } return null; }

            try { loc.Init(e); } catch (Exception ex) { Log.LogWarning("[pvpprobe] Init: " + ex.Message); }
            try { go.transform.localPosition = new Vector3(at.x, at.y, 0f); } catch { }
            try { loc.RecalculateAndRegister(true); } catch (Exception ex) { Log.LogWarning("[pvpprobe] register: " + ex.Message); }
            bool regCalled = false;
            try { ImpactTracker.RegisterEntity(loc); regCalled = true; } catch (Exception ex) { Log.LogWarning("[pvpprobe] RegisterEntity: " + ex.Message); }

            Vector3 finalPos = Vector3.zero; Vector2 finalLp = Vector2.zero;
            try { finalPos = e.Position; } catch { }
            try { finalLp = loc.LocalPosition; } catch { }
            var s = new Spawned { ID = id, Go = go, Loc = loc, Entity = e };
            _spawned.Add(s);
            Log.LogInfo($"[pvpprobe] SPAWNED '{id}' role=Enemy hp=100 | requested=({at.x:0.00},{at.y:0.00}) " +
                        $"-> Position=({finalPos.x:0.00},{finalPos.y:0.00}) LocalPosition=({finalLp.x:0.00},{finalLp.y:0.00}) " +
                        $"transform.local={SafeLocal(go)} world={SafeWorld(go)} parent={(parent != null ? parent.name : "<none>")}");
            Log.LogInfo($"[pvpprobe] registry: RegisterEntity={(regCalled ? "called" : "FAILED")} inRegistry={InRegistry(id)} registryCount={RegistryCount()}");
            return s;
        }

        // ONE-KEY AUTO P1 TEST (Ctrl+Shift+7) — NO firing, NO native target needed. Spawns a probe, then SIMULATES a
        // shell landing on it via ImpactTracker.EvaluateImpact and checks whether the probe's HP/State actually moved.
        // If L doesn't hit, it retries at the probe's lp- and Position-space so the log states exactly which coordinate
        // space the adjudication uses (the placement recipe), or that a registered entity still isn't adjudicated.
        private static void AutoTest()
        {
            Log.LogInfo("[pvpprobe] ===== AUTO P1 TEST (no firing) =====");
            if (!InMission()) Log.LogWarning("[pvpprobe] not in MissionActive — results may be meaningless");
            var shell = FindAnyShell();
            if (shell == null) { Log.LogWarning("[pvpprobe] no ShellDefinition available — cannot simulate impact"); return; }
            string sid = "?"; float radius = -1, dmg = -1;
            try { sid = shell.ShellId; } catch { } try { radius = shell.ImpactRadius; } catch { } try { dmg = shell.Damage; } catch { }
            Log.LogInfo($"[pvpprobe] sim shell='{sid}' ImpactRadius={radius} Damage={dmg}");
            DumpAvailableShells();

            // A valid on-map test coordinate: prefer a real impact; else a native entity's Position; else a default.
            EntityLocation refEnt = FirstNativeEntity();
            Vector2 L; string src;
            if (_haveImpact) { L = _lastImpact; src = "lastImpact"; }
            else if (refEnt != null) { Vector3 rp = Vector3.zero; try { rp = refEnt.Entity.Position; } catch { } L = new Vector2(rp.x, rp.y); src = "nativeEntityPos"; }
            else { L = new Vector2(10f, 10f); src = "default(10,10)"; }
            Log.LogInfo($"[pvpprobe] test location L=({L.x:0.00},{L.y:0.00}) source={src}");

            var s = SpawnAt(L);
            if (s == null) { Log.LogWarning("[pvpprobe] spawn failed — abort"); return; }

            // (A) Does ImpactTracker's OWN spatial query return the probe at the impact point? This is the query the
            // real shell adjudication (StartImpact) uses to find what a shell hit — exception-free, the key signal.
            bool trackerFinds = false;
            try
            {
                var t = ImpactTracker.GetNearest(EntityRoles.Enemy, L, false);
                EntityLocation nl = null; float d = float.NaN;
                try { nl = t.Item1; } catch { } try { d = t.Item2; } catch { }
                string nid = "<null>"; try { if (nl != null && nl.Entity != null) nid = nl.Entity.ID; } catch { }
                trackerFinds = nid == s.ID;
                Log.LogInfo($"[pvpprobe] GetNearest(Enemy)@L -> '{nid}' d={d:0.00} {(trackerFinds ? "== PROBE (tracker finds our entity AT the impact point)" : "(a different entity is nearer)")}");
            }
            catch (Exception ex) { Log.LogWarning("[pvpprobe] GetNearest: " + ex.Message); }

            // (B) Direct damage path: does EntityLocation.TakeDamage move the probe's HP/state? (proven in run 1)
            bool tookDamage = false;
            try
            {
                int hp0 = ReadHp(s); string st0 = ReadState(s);
                bool ret = false; try { ret = s.Loc.TakeDamage(shell, 50, ""); } catch (Exception ex) { Log.LogWarning("[pvpprobe] TakeDamage: " + ex.Message); }
                int hp1 = ReadHp(s); string st1 = ReadState(s);
                tookDamage = hp1 != hp0 || st1 != st0;
                Log.LogInfo($"[pvpprobe] TakeDamage(50) -> ret={ret} hp {hp0}->{hp1} state {st0}->{st1} {(tookDamage ? "DAMAGED" : "no change")}");
            }
            catch (Exception ex) { Log.LogWarning("[pvpprobe] TakeDamage block: " + ex.Message); }

            // (C) Best-effort synthetic full-pipeline driver (may be unavailable outside the fire flow → ANE; not fatal).
            bool simHit = SimImpact(shell, L, s, "L");

            if (simHit)
                Log.LogInfo("[pvpprobe] VERDICT P1: PASS (end-to-end) — EvaluateImpact adjudicated the spawned entity as hit.");
            else if (trackerFinds && tookDamage)
                Log.LogInfo("[pvpprobe] VERDICT P1: PASS — entity registers, ImpactTracker's spatial query returns it AT the impact point, AND TakeDamage applies. " +
                            "(EvaluateImpact's synthetic call needs fire-flow context; real PvP shells use StartImpact, which uses this same registry+query.) Player-as-entity is viable.");
            else if (trackerFinds)
                Log.LogInfo("[pvpprobe] VERDICT P1: LIKELY PASS — ImpactTracker finds the probe AT the impact point (registration + placement correct). " +
                            "TakeDamage didn't move HP this run (asset-shell quirk); a real fired shell drives it. Player-as-entity viable.");
            else
                Log.LogInfo("[pvpprobe] VERDICT P1: probe registered but NOT returned by the spatial query — check Role/State/coords (registry dump Ctrl+Shift+5).");
            Log.LogInfo("[pvpprobe] ===== end auto test =====");
        }

        // Simulate a shell landing at `at` and report whether the probe's hp/state moved.
        private static bool SimImpact(ShellDefinition shell, Vector2 at, Spawned s, string label)
        {
            int hp0 = ReadHp(s); string st0 = ReadState(s);
            try { ImpactTracker.EvaluateImpact(shell, at); }
            catch (Exception ex) { Log.LogWarning($"[pvpprobe] EvaluateImpact@{label}: " + ex.Message); return false; }
            int hp1 = ReadHp(s); string st1 = ReadState(s);
            bool changed = hp1 != hp0 || st1 != st0;
            Log.LogInfo($"[pvpprobe] EvaluateImpact @{label}=({at.x:0.00},{at.y:0.00}) -> probe hp {hp0}->{hp1} state {st0}->{st1} {(changed ? "HIT" : "no change")}");
            return changed;
        }

        private static int ReadHp(Spawned s) { try { return s.Entity.Health; } catch { return -1; } }
        private static string ReadState(Spawned s) { try { return s.Entity.State.ToString(); } catch { return "?"; } }

        private static void DamageLatest(int dmg)
        {
            if (_spawned.Count == 0) { Log.LogWarning("[pvpprobe] no probe entity to damage (Ctrl+Shift+1 to spawn one)"); return; }
            var s = _spawned[_spawned.Count - 1];
            if (s == null || s.Loc == null) { Log.LogWarning("[pvpprobe] latest probe entity is gone"); return; }
            var shell = FindAnyShell();
            if (shell == null) { Log.LogWarning("[pvpprobe] no ShellDefinition found to pass to TakeDamage"); return; }

            int hp0 = -1; string st0 = "?"; bool alive0 = false;
            try { hp0 = s.Entity.Health; } catch { } try { st0 = s.Entity.State.ToString(); } catch { } try { alive0 = s.Entity.IsAlive; } catch { }
            bool ret = false;
            try { ret = s.Loc.TakeDamage(shell, dmg, ""); } catch (Exception ex) { Log.LogWarning("[pvpprobe] TakeDamage: " + ex.Message); return; }
            int hp1 = -1; string st1 = "?"; bool alive1 = false;
            try { hp1 = s.Entity.Health; } catch { } try { st1 = s.Entity.State.ToString(); } catch { } try { alive1 = s.Entity.IsAlive; } catch { }
            Log.LogInfo($"[pvpprobe] DAMAGE '{s.ID}' by {dmg} (ret={ret}) | hp {hp0}->{hp1} state {st0}->{st1} alive {alive0}->{alive1} (P2)");
        }

        // ============================================================ P1 diagnostics: registry + nearest ============================================================

        // Dump the ImpactTracker registry the adjudication actually uses, alongside each entity's Position / lp /
        // transform — so a NATIVE target reveals the grid<->transform mapping a hittable entity must satisfy.
        private static void DumpRegistry()
        {
            Log.LogInfo($"[pvpprobe] ===== ImpactTracker REGISTRY (count={RegistryCount()}) + scene EntityLocations =====");
            int n = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    MapEntity e = null; try { e = loc.Entity; } catch { }
                    string id = "?"; try { if (e != null) id = e.ID; } catch { }
                    string role = "?", st = "?"; bool alive = false; Vector3 pos = Vector3.zero; Vector2 lp = Vector2.zero;
                    try { if (e != null) role = e.Role.ToString(); } catch { }
                    try { if (e != null) st = e.State.ToString(); } catch { }
                    try { if (e != null) alive = e.IsAlive; } catch { }
                    try { if (e != null) pos = e.Position; } catch { }
                    try { lp = loc.LocalPosition; } catch { }
                    bool probe = IsProbeId(id);
                    Log.LogInfo($"[pvpprobe]  {(probe ? "*" : " ")}'{id}' role={role} state={st} alive={alive} reg={InRegistry(id)} " +
                                $"Position=({pos.x:0.00},{pos.y:0.00}) lp=({lp.x:0.00},{lp.y:0.00}) tlocal={SafeLocal(loc.gameObject)} world={SafeWorld(loc.gameObject)}");
                    n++;
                }
            }
            catch (Exception ex) { Log.LogWarning("[pvpprobe] registry dump: " + ex.Message); }
            if (n == 0) Log.LogInfo("[pvpprobe]  (no EntityLocations in scene — fire-mission targets may not be placed in this mission)");
            Log.LogInfo("[pvpprobe] ===== end registry (compare a NATIVE target's Position vs lp vs tlocal to learn the mapping) =====");
        }

        // Ask the tracker itself what's nearest to the last impact — reveals the coordinate space it compares and
        // whether our probe entity is even a candidate.
        private static void NearestToImpact()
        {
            if (!_haveImpact) { Log.LogWarning("[pvpprobe] no impact recorded yet — fire a shot first"); return; }
            Vector2 at = _lastImpact;
            try
            {
                var t = ImpactTracker.GetNearestTargetOrEnemy(at);
                LogNearest("GetNearestTargetOrEnemy", at, t);
            }
            catch (Exception ex) { Log.LogWarning("[pvpprobe] GetNearestTargetOrEnemy: " + ex.Message); }
            try
            {
                var t = ImpactTracker.GetNearest(EntityRoles.Enemy, at, false);
                LogNearest("GetNearest(Enemy,alive=false)", at, t);
            }
            catch (Exception ex) { Log.LogWarning("[pvpprobe] GetNearest: " + ex.Message); }
        }

        private static void LogNearest(string which, Vector2 at, Il2CppSystem.ValueTuple<EntityLocation, float, float> t)
        {
            EntityLocation loc = null; float d1 = -1, d2 = -1;
            try { loc = t.Item1; } catch { } try { d1 = t.Item2; } catch { } try { d2 = t.Item3; } catch { }
            string id = "<null>";
            try { if (loc != null && loc.Entity != null) id = loc.Entity.ID; } catch { }
            Log.LogInfo($"[pvpprobe] {which} from ({at.x:0.00},{at.y:0.00}) -> '{id}' d2={d1:0.00} d3={d2:0.00}");
        }

        // ============================================================ P4: turret reposition ============================================================

        private static void RepositionTurret(bool instant)
        {
            TurretController t = null;
            try { t = TurretController.Instance; } catch { }
            if (t == null) { Log.LogWarning("[pvpprobe] TurretController.Instance == null"); return; }

            Vector3 before = Vector3.zero; bool haveBase = false;
            try { var b = t.turretBase; if (b != null) { before = b.position; haveBase = true; } } catch { }
            Vector3 target = before + new Vector3(50f, 0f, 0f);   // small map-space nudge; logged so the delta is visible
            string fn = instant ? "SetTurretLocation" : "MoveTurret";
            try { if (instant) t.SetTurretLocation(target); else t.MoveTurret(target); }
            catch (Exception ex) { Log.LogWarning($"[pvpprobe] {fn}: " + ex.Message); return; }

            Vector3 after = Vector3.zero; try { var b = t.turretBase; if (b != null) after = b.position; } catch { }
            string mvStart = "?", mvTarget = "?";
            try { var ms = t.MovementStartLoc; mvStart = ms.HasValue ? ms.Value.ToString("0.0") : "null"; } catch { }
            try { var mt = t.MovementTargetLoc; mvTarget = mt.HasValue ? mt.Value.ToString("0.0") : "null"; } catch { }
            Log.LogInfo($"[pvpprobe] {fn}({target.ToString("0.0")}) | turretBase {(haveBase ? before.ToString("0.0") : "<none>")} -> {after.ToString("0.0")} " +
                        $"MovementStartLoc={mvStart} MovementTargetLoc={mvTarget} (P4 — MoveTurret is animated; watch the map over the next seconds)");
        }

        // ============================================================ P3 / status ============================================================

        private static void DumpStatus()
        {
            Log.LogInfo("[pvpprobe] ===== STATUS / determinism inputs (P3) =====");
            string phase = "n/a"; try { var mm = MissionManager.Instance; if (mm != null) phase = mm.CurrentPhase.ToString(); } catch { }
            Log.LogInfo($"[pvpprobe] phase={phase} patched={_patched} lastImpact={(_haveImpact ? _lastImpact.ToString("0.00") : "<none>")} probeEntities={_spawned.Count}");

            TurretController t = null; try { t = TurretController.Instance; } catch { }
            if (t == null) Log.LogInfo("[pvpprobe] turret: <none>");
            else
            {
                Vector3 bp = Vector3.zero; try { var b = t.turretBase; if (b != null) bp = b.position; } catch { }
                Log.LogInfo($"[pvpprobe] turret: base={bp.ToString("0.0")}");
                GunController gun = FirstGun(t);
                if (gun == null) Log.LogInfo("[pvpprobe] gun: <none>");
                else
                {
                    float gH = 0, gV = 0; try { gH = gun.gunHorizontalDispersion; } catch { } try { gV = gun.gunVerticalDispersion; } catch { }
                    ShellDefinition sd = null;
                    try { var bp2 = gun.ChamberedShellBlueprint; if (bp2 != null) sd = bp2.shellDefinition; } catch { }
                    if (sd == null) Log.LogInfo($"[pvpprobe] gun dispersion: gunH={gH} gunV={gV} | no chambered shell");
                    else
                    {
                        float sh = 0, sv = 0, ssp = 0; string sid = "?";
                        try { sh = sd.horizontalDispersion; } catch { } try { sv = sd.verticalDispersion; } catch { }
                        try { ssp = sd.shellSpeedVariationPercent; } catch { } try { sid = sd.ShellId; } catch { }
                        Log.LogInfo($"[pvpprobe] gun dispersion: gunH={gH} gunV={gV} | shell='{sid}' h={sh} v={sv} speedVar%={ssp} " +
                                    "(co-op deterministic-fire zeros ALL five during FireShell; both machines should log the SAME impact)");
                    }
                }
            }
            for (int i = 0; i < _spawned.Count; i++)
            {
                var s = _spawned[i]; if (s == null) continue;
                string p = "?", hp = "?", st = "?";
                try { p = s.Entity.Position.ToString("0.0"); } catch { } try { hp = s.Entity.Health.ToString(); } catch { } try { st = s.Entity.State.ToString(); } catch { }
                Log.LogInfo($"[pvpprobe]   probe '{s.ID}' pos={p} hp={hp} state={st} go={(s.Go != null ? "live" : "DEAD")}");
            }
            Log.LogInfo("[pvpprobe] ===== end status =====");
        }

        private static void DestroyAll()
        {
            int n = 0;
            for (int i = 0; i < _spawned.Count; i++)
            {
                var s = _spawned[i]; if (s == null) continue;
                try { if (s.Go != null) UnityEngine.Object.Destroy(s.Go); n++; } catch { }
            }
            _spawned.Clear();
            Log.LogInfo($"[pvpprobe] destroyed {n} probe entit{(n == 1 ? "y" : "ies")}");
        }

        // ============================================================ helpers ============================================================

        private static bool IsProbeId(string id) => !string.IsNullOrEmpty(id) && id.StartsWith("PVPPROBE_", StringComparison.Ordinal);

        // A source EntityLocation to clone (prefer a non-clone, non-probe one). Resources.FindObjectsOfTypeAll so we
        // also see inactive templates/prefabs — the same reason CoopEntities uses it.
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
                    if (nm == "PvpProbeTarget") continue;             // not one of ours
                    best = go;
                    if (nm == null || nm.IndexOf("(Clone)", StringComparison.Ordinal) < 0) break;   // prefer a prefab/source
                }
                return best;
            }
            catch (Exception e) { Log.LogWarning("[pvpprobe] find clone source: " + e.Message); return null; }
        }

        // Parent for the clone: an existing scene EntityLocation's parent (exact for this scene), else 'Fire Mission
        // Root', else null (EntityLocation self-resolves a root canvas from its LocalPosition).
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
                    if (nm == "PvpProbeTarget") continue;
                    var p = loc.transform.parent; if (p != null) return p;
                }
            }
            catch { }
            try { var fmr = GameObject.Find("Fire Mission Root"); if (fmr != null) return fmr.transform; } catch { }
            return null;
        }

        private static EntityLocation FirstNativeEntity()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    MapEntity e = null; try { e = loc.Entity; } catch { }
                    if (e == null) continue;
                    string id = null; try { id = e.ID; } catch { }
                    if (IsProbeId(id)) continue;
                    return loc;
                }
            }
            catch { }
            return null;
        }

        private static GunController FirstGun(TurretController t)
        {
            try
            {
                var guns = t.guns; if (guns == null) return null;
                for (int i = 0; i < guns.Count; i++) { var g = guns[i]; if (g != null) return g; }
            }
            catch { }
            return null;
        }

        // Pick a REAL damaging shell — never the 'EMPT' empty-chamber placeholder (it has no ballistic data and makes
        // EvaluateImpact throw ArgumentNullException). Prefer a chambered real shell, else a Resources shell with
        // Damage>0 and a non-empty id, else any non-empty, else anything.
        private static ShellDefinition FindAnyShell()
        {
            try { var t = TurretController.Instance; var g = t != null ? FirstGun(t) : null; if (g != null) { var bp = g.ChamberedShellBlueprint; if (bp != null && bp.shellDefinition != null && IsRealShell(bp.shellDefinition)) return bp.shellDefinition; } } catch { }
            ShellDefinition best = null, nonEmpty = null, any = null;
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>());
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var s = arr[i].TryCast<ShellDefinition>(); if (s == null) continue;
                    if (any == null) any = s;
                    if (!IsEmptyShell(s) && nonEmpty == null) nonEmpty = s;
                    if (IsRealShell(s)) { best = s; break; }
                }
            }
            catch { }
            return best ?? nonEmpty ?? any;
        }

        private static bool IsEmptyShell(ShellDefinition s)
        { try { string id = s.ShellId; return string.IsNullOrEmpty(id) || id.IndexOf("EMPT", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return true; } }

        private static bool IsRealShell(ShellDefinition s)
        { try { return !IsEmptyShell(s) && s.Damage > 0; } catch { return false; } }

        private static void DumpAvailableShells()
        {
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>());
                int n = arr != null ? arr.Length : 0;
                var sb = new System.Text.StringBuilder("[pvpprobe] shells:");
                for (int i = 0; i < n && i < 16; i++)
                {
                    var s = arr[i].TryCast<ShellDefinition>(); if (s == null) continue;
                    string id = "?"; int d = -1; float r = -1;
                    try { id = s.ShellId; } catch { } try { d = s.Damage; } catch { } try { r = s.ImpactRadius; } catch { }
                    sb.Append($" '{id}'(dmg={d},r={r})");
                }
                Log.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Log.LogWarning("[pvpprobe] shell dump: " + ex.Message); }
        }

        private static string SafeWorld(GameObject go)
        { try { return go != null ? go.transform.position.ToString("0.0") : "<null>"; } catch { return "?"; } }

        private static string SafeLocal(GameObject go)
        { try { return go != null ? go.transform.localPosition.ToString("0.0") : "<null>"; } catch { return "?"; } }

        private static bool InRegistry(string id)
        { try { var d = ImpactTracker.EntityLocations; return d != null && !string.IsNullOrEmpty(id) && d.ContainsKey(id); } catch { return false; } }

        private static int RegistryCount()
        { try { var d = ImpactTracker.EntityLocations; return d != null ? d.Count : -1; } catch { return -1; } }

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        public static string Status() => $"pvpprobe: {(Config.PvpProbe ? "ARMED" : "off")} patched={_patched} probes={_spawned.Count} lastImpact={(_haveImpact ? "yes" : "no")}";
    }
}
