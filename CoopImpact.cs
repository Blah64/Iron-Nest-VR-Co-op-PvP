using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op (4c, second half): replicate the host's IMPACT RESULT so the client's tactical map shows
    /// HIT information.
    ///
    /// THE PROBLEM (tester 2026-06-21): "field report said HIT but the map did not show the enemy hit." Maps don't
    /// draw enemies (they're invisible data targets) — the on-map "hit" is an <c>ImpactIndicator</c> that lights a
    /// region when a shell impact within it hit a target. Each machine fires its OWN shell and runs its OWN impact
    /// adjudication (<c>SleepyNodes.State_ImpactStart.StartImpact</c> → returns the List&lt;MapEntity&gt; hit). On
    /// the client the target is often ALREADY host-destroyed (mirrored as Destroyed) by the time the client's shell
    /// lands, so the client adjudicates a MISS → its ImpactIndicators never fire → no map marker. (Same root as the
    /// HIT→MISS field-report flip fixed in CoopOrders.) The client's local FALL-OF-SHOT marker still works for its
    /// own shells; only the HIT result is wrong, so we only replicate HITS.
    ///
    /// CAPTURE (host): Harmony POSTFIX on <c>State_ImpactStart.StartImpact(state, shell, impactLocation)</c> reads
    /// the authoritative hit set (the returned MapEntities) + impact location + shell id, and — only when ≥1 entity
    /// was hit — broadcasts them. REPLAY (client): rebuild an <c>EventData_Impact{ImpactLocation, ImpactShell,
    /// ImpactEntities}</c> (resolving entity IDs against the live scene / <c>ImpactTracker.EntityLocations</c> and
    /// the shell id against the loaded ShellDefinitions) and hand it to every <c>ImpactIndicator.HandleLocalSpaceEvent</c>
    /// — exactly the dispatch the game's own impact event uses — so the indicators region-test it and light up.
    ///
    /// Only the HOST broadcasts (the client's postfix bails on !IsHost) → no loop. Scoped to MissionActive. The
    /// client's OWN local hit (rare — it usually misses the dead target) may also fire an indicator, but the
    /// indicator's own minSecondsBetweenInvokes throttle collapses the duplicate. Misses are NOT replicated, so the
    /// client's own fall-of-shot markers are untouched.
    /// </summary>
    internal static class CoopImpact
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_IMPACT = 30;   // [t][locX f][locY f][shellId str][hitCount i32][entId str]×N  reliable host->client

        private static Il2CppStructArray<byte> _buf;
        private static int _sent, _applied;

        // ShellId -> ShellDefinition, built once on the client (a handful of shell types). Rebuilt if a lookup misses.
        private static Dictionary<string, ShellDefinition> _shellCache;

        // ---------------- host capture (Harmony postfix on State_ImpactStart.StartImpact) ----------------

        public static void OnImpactAdjudicated(ShellDefinition shell, Vector2 impactLocation,
                                               Il2CppSystem.Collections.Generic.List<MapEntity> __result)
        {
            try
            {
                if (!Config.CoopImpactSync) return;
                if (!SteamNet.InLobby || !CoopP2P.HasPeer || !InMission()) return;

                int count = 0; try { if (__result != null) count = __result.Count; } catch { }
                string shellId = ""; try { if (shell != null) shellId = shell.ShellId ?? ""; } catch { }

                // This postfix runs when the shell LANDS - impactLocation is the map hit point (== the ShellVisual
                // crater, confirmed; artillery is 2D). Adjudication stays host-authoritative via the MSG_IMPACT
                // broadcast below; the visible flight is copied at fire time by CoopBallistics.
                Log.LogInfo($"[imp] {(CoopP2P.IsHost ? "HOST" : "client")} impact at ({impactLocation.x:0.0},{impactLocation.y:0.0}) shell='{shellId}' hits={count}");

                if (!CoopP2P.IsHost) return;          // only the host broadcasts the authoritative hit set
                if (__result == null || count <= 0) return;   // replicate HITS only — client keeps its own fall-of-shot for misses
                Broadcast(impactLocation, shellId, __result, count);
            }
            catch (Exception e) { Log.LogWarning("[imp] capture: " + e.Message); }
        }

        private static void Broadcast(Vector2 loc, string shellId, Il2CppSystem.Collections.Generic.List<MapEntity> hits, int count)
        {
            if (!EnsureBuf()) return;
            if (count > 16) count = 16;   // sanity cap; a single impact hits very few entities

            // Pre-collect valid IDs so the serialized hitCount matches what we actually write.
            var ids = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                MapEntity e = null; try { e = hits[i]; } catch { }
                if (e == null) continue;
                string id = null; try { id = e.ID; } catch { }
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            if (ids.Count == 0) return;

            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_IMPACT);
            w.Float(loc.x); w.Float(loc.y);
            w.Str(shellId, 200);
            w.Int(ids.Count);
            for (int i = 0; i < ids.Count; i++) w.Str(ids[i], 200);
            if (w.Overflow) { Log.LogWarning("[impact] packet too large for " + _buf.Length + "B - not sent"); return; }

            CoopP2P.Send(_buf, w.Length, true);
            _sent++;
            Log.LogInfo($"[imp] host impact at ({loc.x:0},{loc.y:0}) shell='{shellId}' hits={ids.Count} -> peer");
        }

        // ---------------- client replay ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_IMPACT) return;
            if (CoopP2P.IsHost) return;   // host authored it
            var r = new CoopWire.Reader(a, len, 1);
            float x = r.Float(), y = r.Float();
            string shellId = r.Str(200);
            if (r.Bad) return;
            int count = r.Int();
            if (r.Bad || count < 0 || count > 64) return;

            var ids = new List<string>(count);
            for (int i = 0; i < count; i++) { string s = r.Str(200); if (r.Bad) break; ids.Add(s); }

            Apply(new Vector2(x, y), shellId, ids);
        }

        private static void Apply(Vector2 loc, string shellId, List<string> ids)
        {
            try
            {
                var list = new Il2CppSystem.Collections.Generic.List<MapEntity>();
                int resolved = 0;
                foreach (var id in ids) { var e = ResolveEntity(id); if (e != null) { list.Add(e); resolved++; } }

                var ed = new SleepyNodes.EventData_Impact();
                try { ed.ImpactLocation = loc; } catch { }
                try { ed.ImpactShell = ResolveShell(shellId); } catch { }
                try { ed.ImpactEntities = list; } catch { }

                int fired = DispatchToIndicators(ed);
                _applied++;
                Log.LogInfo($"[imp] applied host impact ({loc.x:0},{loc.y:0}) shell='{shellId}' hits={resolved}/{ids.Count} -> {fired} indicator(s)");
            }
            catch (Exception e) { Log.LogWarning("[imp] apply: " + e.Message); }
        }

        private static int DispatchToIndicators(SleepyNodes.EventData_Impact ed)
        {
            int n = 0;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ImpactIndicator>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var ind = arr[i].TryCast<ImpactIndicator>(); if (ind == null) continue;
                    try { ind.HandleLocalSpaceEvent(ed); n++; } catch (Exception e) { Log.LogWarning("[imp] indicator: " + e.Message); }
                }
            }
            catch (Exception e) { Log.LogWarning("[imp] dispatch: " + e.Message); }
            return n;
        }

        // ---------------- resolution helpers ----------------

        // Resolve a mirrored entity by its MapEntity ID. Prefer ImpactTracker's live registry (O(1)); fall back to a
        // scene scan (the entity may be present-but-Destroyed — that's fine, it still carries the right Role).
        private static MapEntity ResolveEntity(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                var dict = ImpactTracker.EntityLocations;
                if (dict != null && dict.ContainsKey(id))
                {
                    var el = dict[id];
                    if (el != null) { try { return el.Entity; } catch { } }
                }
            }
            catch { }
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    MapEntity e = null; try { e = loc.Entity; } catch { }
                    if (e == null) continue;
                    string eid; try { eid = e.ID; } catch { continue; }
                    if (eid == id) return e;
                }
            }
            catch { }
            return null;
        }

        private static ShellDefinition ResolveShell(string shellId)
        {
            if (string.IsNullOrEmpty(shellId)) return null;
            try
            {
                if (_shellCache == null || !_shellCache.ContainsKey(shellId))
                {
                    _shellCache = new Dictionary<string, ShellDefinition>();
                    var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>());
                    if (arr != null) for (int i = 0; i < arr.Length; i++)
                    {
                        var s = arr[i].TryCast<ShellDefinition>(); if (s == null) continue;
                        string sid = null; try { sid = s.ShellId; } catch { }
                        if (!string.IsNullOrEmpty(sid) && !_shellCache.ContainsKey(sid)) _shellCache[sid] = s;
                    }
                }
                if (_shellCache.TryGetValue(shellId, out var sh)) return sh;
            }
            catch { }
            return null;
        }

        // ---------------- diagnostics ----------------

        public static string Status() => $"impact: sent={_sent} applied={_applied}";

        // ---------------- helpers ----------------

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(512); return true; }
            catch (Exception e) { Log.LogWarning("[imp] buf: " + e.Message); return false; }
        }

    }
}
