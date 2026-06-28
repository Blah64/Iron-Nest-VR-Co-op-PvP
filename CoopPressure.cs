using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Co-op sync for the cockpit STEAM/PRESSURE chain (PLAN-valve.md / memory ironnest-steam-pressure).
    ///
    /// (1) VALVES — per-valve <c>ValveController.currentDamage01</c> (0 fine … 1 broken). The probe proved damage
    /// is dial-coupled (<c>currentDamage01 == NormalizeDamage(CurrentDialValue)</c>) AND that <c>SetDamage01(x)</c>
    /// drives all three layers in one call — the hidden float, the visual dial, and the manager's derived
    /// <c>Health01</c> aggregation — with no next-frame snap-back. So the receive path is simply
    /// <c>v.SetDamage01(dmg)</c>; <c>Health01</c> derives and is never sent. Valves are keyed by their repair
    /// dial's FNV-1a path-hash (the SAME PathOf/Fnv CoopControls uses) because the game's own <c>systemId</c> is
    /// non-unique and mislabeled (two managers share 'ChargeRammerLeft').
    ///
    /// REPAIRS are symmetric (either crew member turns a valve dial → damage drops); BREAKS are host-authoritative
    /// (RNG via <c>ValveAutoAddDamageOnEnable</c>; if the client ran its own roll it would break a DIFFERENT valve
    /// than the host = union desync). We can't gate the break component with Harmony — patching it (especially its
    /// <c>BurstRoutine</c> coroutine) HARD-CRASHES this IL2CPP build (rev3). So the gate is RECONCILE-not-prevent,
    /// implemented in pure data with zero Harmony:
    ///   - The CLIENT authors only DECREASES (a dial-drag repair). It NEVER sends an increase — a local RNG break
    ///     is left unsent, so it can't propagate to the host.
    ///   - The HOST is the canonical source: it edge-sends every change (break or repair) AND re-asserts the FULL
    ///     valve set on a 2s heartbeat. That heartbeat overwrites any spurious client-local break back to the host's
    ///     truth within ≤2s (the only artifact is a brief wrong-valve flicker on the client). Either side's repair
    ///     still rides through: a client decrease reaches the host, the host adopts it and re-asserts the new value.
    /// MSG_VALVE is reliable + symmetric, so it must be on the host RELAY allowlist (CoopControls.IsClientAuthored)
    /// or a client's repair never reaches the OTHER clients at N>2. Echo-guarded (_echoUntil + Prev-adopt) exactly
    /// like the powder/aim edge-replicators.
    ///
    /// (2) ENGINE (Phase 2 — DORMANT until tested) — host-authoritative <c>DieselEngineController.EnginesRunning</c>
    /// via <c>ForceStart()</c>/<c>ForceStop()</c>. <c>EnginePowerController.Power</c> is deterministic smoothing off
    /// the running state → never synced (it converges on its own). Gated behind <c>Config.CoopEngineSync</c>, which
    /// defaults OFF: PLAN-valve §5 wants a 2-player test FIRST (the client's engine may already track the host purely
    /// from the synced fuel/timing dials, since the controller polls them). Flip the flag on only if it diverges.
    ///
    /// The valve repair dials are EXCLUDED from CoopControls (it would mark the turret group remotely-owned and its
    /// dial-visual stream wouldn't update the hidden damage anyway — see CoopControls.Scan + PLAN-valve §2/§4.1).
    /// </summary>
    internal static class CoopPressure
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_VALVE  = 48;   // [t][netId i32][damage01 f32]   reliable, SYMMETRIC (either->peer; client sends repairs only)
        public const byte MSG_ENGINE = 49;   // [t][running u8]                reliable, HOST->client (host-authoritative; Phase 2, dormant)

        private const float ValveEps = 0.01f;          // damage delta below which we don't bother replicating (matches dial coupling noise)
        private const float ValveHeartbeatSec = 2f;     // host re-asserts the full valve set this often (self-heal + clear client spurious breaks)
        private const float EngineHeartbeatSec = 2f;
        private const float EchoSec = 0.3f;             // suppress echoing a value we just applied (same as powder/aim)
        private const float RescanSec = 3f;             // registry refresh cadence (mirrors CoopControls)

        private sealed class VState { public float Prev = float.NaN; }

        private static readonly Dictionary<int, ValveController> _byId = new Dictionary<int, ValveController>();
        private static readonly Dictionary<int, VState> _state = new Dictionary<int, VState>();
        private static readonly Dictionary<int, float> _echoUntil = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _pending = new Dictionary<int, float>();   // values that arrived before their valve registered
        private static int _lastValveCount = -1;
        private static float _nextRescan;
        private static float _nextValveBeat;

        private static DieselEngineController _engine;
        private static bool _enginePrev;
        private static float _nextEngineBeat;

        private static int _applied, _sent;

        private static Il2CppStructArray<byte> _buf;
        private static readonly StringBuilder _sb = new StringBuilder(128);

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopValveSync && !Config.CoopEngineSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer)
            {
                if (_byId.Count > 0 || _state.Count > 0) { _byId.Clear(); _state.Clear(); _echoUntil.Clear(); _pending.Clear(); _engine = null; _lastValveCount = -1; }
                return;
            }

            float now = Time.unscaledTime;
            EnsureRegistry(now);

            if (Config.CoopValveSync) TickValves(now);
            if (Config.CoopEngineSync) TickEngine(now);
        }

        private static void TickValves(float now)
        {
            bool host = CoopP2P.IsHost;

            foreach (var kv in _byId)
            {
                int id = kv.Key; var v = kv.Value;
                if (v == null) continue;
                float cur; try { cur = v.currentDamage01; } catch { continue; }
                if (!CoopWire.Finite(cur)) continue;

                var st = State(id);
                bool suppressed = _echoUntil.TryGetValue(id, out var u) && now < u;
                // Prev==NaN: first sample after (re)connect/scene — seed the baseline silently (join state rides the
                // JIP snapshot). suppressed: we just applied a peer value, don't bounce it straight back.
                if (CoopWire.Finite(st.Prev) && Mathf.Abs(cur - st.Prev) > ValveEps && !suppressed)
                {
                    bool increased = cur > st.Prev;
                    // RECONCILE GATE: the client never authors a break (increase) — that's the host's RNG. It only
                    // authors a repair (decrease, a dial drag). The host authors everything (break + repair).
                    if (host || !increased)
                    {
                        SendValve(id, cur);
                        Diagnostics.V($"[valve] {(increased ? "break" : "repair")} id=0x{(uint)id:X8} dmg={cur:0.000} -> peer");
                    }
                }
                st.Prev = cur;
            }

            // HOST heartbeat: re-assert the FULL valve set so (a) a value lost in a blackout re-converges, and (b) a
            // spurious client-local RNG break (which the client refused to send) is overwritten back to the host's
            // truth within ≤2s. The client does NOT heartbeat — the host owns the canonical state.
            if (host && now >= _nextValveBeat)
            {
                _nextValveBeat = now + ValveHeartbeatSec;
                foreach (var kv in _byId)
                {
                    var v = kv.Value; if (v == null) continue;
                    float cur; try { cur = v.currentDamage01; } catch { continue; }
                    if (CoopWire.Finite(cur)) SendValve(kv.Key, cur);
                }
            }
        }

        private static void SendValve(int id, float dmg)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_VALVE); w.Int(id); w.Float(dmg);
            if (w.Overflow) return;
            if (CoopP2P.Send(_buf, w.Length, true)) _sent++;   // reliable: a discrete repair/break edge must not be lost
        }

        // ---------------- engine (Phase 2 — host->client, dormant unless Config.CoopEngineSync) ----------------

        private static void TickEngine(float now)
        {
            if (!CoopP2P.IsHost || _engine == null) return;
            bool run; try { run = _engine.EnginesRunning; } catch { return; }
            if (run != _enginePrev || now >= _nextEngineBeat)
            {
                _nextEngineBeat = now + EngineHeartbeatSec;
                _enginePrev = run;
                if (!EnsureBuf()) return;
                var w = new CoopWire.Writer(_buf); w.Byte(MSG_ENGINE); w.Bool(run);
                if (w.Overflow) return;
                if (CoopP2P.Send(_buf, w.Length, true)) _sent++;
            }
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            float now = Time.unscaledTime;
            switch (type)
            {
                case MSG_VALVE:
                {
                    if (len < 9) return;   // t + netId i32 + damage f32
                    var r = new CoopWire.Reader(a, len, 1);
                    int id = r.Int(); float dmg = r.Float();
                    if (r.Bad || !CoopWire.Finite(dmg)) return;
                    if (_byId.TryGetValue(id, out var v) && v != null)
                    {
                        float cur; try { cur = v.currentDamage01; } catch { cur = float.NaN; }
                        // AUTHORITY BOUNDARY (REVIEW-valve P2): the host accepts only REPAIRS (decreases) from a
                        // client — a client must NEVER author a break (increase); that's the host's RNG. The reconcile
                        // gate is enforced HERE, not just trusted in the client's sender, so a stale/buggy/unexpected
                        // client can't push a break into the host (which would then re-broadcast it as truth on the 2s
                        // heartbeat). On reject, immediately re-assert the host's true value so the offending sender
                        // converges now instead of waiting for the heartbeat. (The transport relay can't value-inspect,
                        // so a relayed break still reaches other clients for ≤1 round-trip; this + the heartbeat heal it.)
                        if (CoopP2P.IsHost && CoopWire.Finite(cur) && dmg > cur + ValveEps)
                        {
                            SendValve(id, cur);
                            Diagnostics.V($"[valve] REJECT client break id=0x{(uint)id:X8} dmg={dmg:0.000} > host {cur:0.000} (from {origin}) — re-asserting");
                            break;
                        }
                        bool changed = !CoopWire.Finite(cur) || Mathf.Abs(cur - dmg) > ValveEps;
                        if (changed)
                        {
                            try { v.SetDamage01(dmg); _applied++; Diagnostics.V($"[valve] applied id=0x{(uint)id:X8} dmg={dmg:0.000} <- peer"); }
                            catch (Exception e) { Log.LogWarning("[valve] apply: " + e.Message); }
                        }
                        // Adopt as our baseline + suppress the echo so TickValves doesn't bounce it back. Adopting the
                        // peer's value yields authorship: the peer re-asserts this valve, not us (no flap).
                        State(id).Prev = dmg;
                        _echoUntil[id] = now + EchoSec;
                    }
                    else
                    {
                        // Valve not registered yet (joiner's cockpit still resolving) — stash and apply on registry build.
                        _pending[id] = dmg;
                    }
                    break;
                }
                case MSG_ENGINE:
                {
                    if (CoopP2P.IsHost) return;   // host is authoritative; never applies
                    if (len < 2) return;          // t + running u8
                    var r = new CoopWire.Reader(a, len, 1);
                    bool run = r.Bool();
                    if (r.Bad || _engine == null) return;
                    try
                    {
                        bool cur = _engine.EnginesRunning;
                        if (run && !cur) _engine.ForceStart();
                        else if (!run && cur) _engine.ForceStop();
                        _applied++;
                        Diagnostics.V($"[engine] applied running={run} <- peer");
                    }
                    catch (Exception e) { Log.LogWarning("[engine] apply: " + e.Message); }
                    break;
                }
            }
        }

        // ---------------- join-in-progress ----------------

        // Host -> joiner: send EVERY valve's current damage (not just the broken ones) so the joiner both ADOPTS the
        // broken set AND CLEARS any stale/default local damage. Wired into CoopP2P.SendJoinSnapshot AND the targeted
        // MISSION_READY resync (CoopScene) — valves are mission-scene objects, so the connect-time burst can land
        // before the joiner's cockpit exists and be dropped (same as map/punchcards); MISSION_READY re-sends once the
        // joiner reports its scene objects are present. Runs inside the JIP/targeted unicast latch.
        public static void SendSnapshot()
        {
            if (!Config.CoopValveSync || !CoopP2P.IsHost) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) return;
            try
            {
                EnsureRegistry(Time.unscaledTime);
                int n = 0;
                foreach (var kv in _byId)
                {
                    var v = kv.Value; if (v == null) continue;
                    float cur; try { cur = v.currentDamage01; } catch { continue; }
                    if (CoopWire.Finite(cur)) { SendValve(kv.Key, cur); n++; }
                }
                Log.LogInfo($"[valve] sent JIP snapshot -> peer ({n} valves)");
            }
            catch (Exception e) { Log.LogWarning("[valve] snapshot: " + e.Message); }
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            int broken = 0;
            foreach (var kv in _byId) { var v = kv.Value; if (v == null) continue; try { if (v.currentDamage01 > ValveEps) broken++; } catch { } }
            string eng = Config.CoopEngineSync
                ? (_engine != null ? ("running=" + SafeRunning() + " host-auth") : "no-engine")
                : "off(phase2)";
            return $"valve: sync={(Config.CoopValveSync ? "on" : "off")} valves={_byId.Count} broken={broken} sent={_sent} applied={_applied} | engine: {eng}";
        }

        private static string SafeRunning() { try { return _engine.EnginesRunning.ToString(); } catch { return "?"; } }

        // ---------------- registry ----------------

        // Rebuild netId -> ValveController on a valve-count change or every RescanSec (cheap; mirrors CoopControls).
        // The dial path + FNV-1a are byte-identical to CoopControls.PathOf/Fnv, so a valve's id is deterministic and
        // identical on both machines (== the probe's printed netId). Never clears live _state/_echoUntil.
        private static void EnsureRegistry(float now)
        {
            var valves = All<ValveController>(Il2CppType.Of<ValveController>());
            bool changed = valves.Count != _lastValveCount || (_byId.Count == 0 && valves.Count > 0);
            if (!changed && now < _nextRescan) return;
            _nextRescan = now + RescanSec;
            _lastValveCount = valves.Count;

            _byId.Clear();
            for (int i = 0; i < valves.Count; i++)
            {
                var v = valves[i];
                Transform dt = null; try { var d = v.dial; dt = d != null ? d.transform : null; } catch { }
                if (dt == null) continue;
                int id = Fnv(PathOf(dt));
                if (!_byId.ContainsKey(id)) _byId[id] = v;
                // Apply anything that arrived for this valve before it existed (JIP race).
                if (_pending.TryGetValue(id, out var pd))
                {
                    try { if (Mathf.Abs(v.currentDamage01 - pd) > ValveEps) v.SetDamage01(pd); } catch { }
                    State(id).Prev = pd; _echoUntil[id] = now + EchoSec; _pending.Remove(id);
                    Diagnostics.V($"[valve] applied pending id=0x{(uint)id:X8} dmg={pd:0.000}");
                }
            }

            if (Config.CoopEngineSync && _engine == null)
            {
                var engines = All<DieselEngineController>(Il2CppType.Of<DieselEngineController>());
                if (engines.Count > 0) { _engine = engines[0]; try { _enginePrev = _engine.EnginesRunning; } catch { } }
            }
        }

        private static VState State(int id)
        {
            if (!_state.TryGetValue(id, out var s)) { s = new VState(); _state[id] = s; }
            return s;
        }

        // ---------------- helpers ----------------

        private static List<T> All<T>(Il2CppSystem.Type t) where T : Il2CppObjectBase
        {
            var list = new List<T>();
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    { try { var c = arr[i].TryCast<T>(); if (c != null) list.Add(c); } catch { } }
            }
            catch { }
            return list;
        }

        // FULL hierarchy path, each segment disambiguated by its SIBLING INDEX — byte-identical to
        // CoopControls.PathOf so a valve dial's id matches across machines (and equals the probe's netId).
        private static string PathOf(Transform t)
        {
            _sb.Length = 0;
            _sb.Append(t.name).Append('#').Append(Sib(t));
            for (var p = t.parent; p != null; p = p.parent) _sb.Insert(0, p.name + "#" + Sib(p) + "/");
            return _sb.ToString();
        }
        private static int Sib(Transform t) { try { return t.GetSiblingIndex(); } catch { return 0; } }

        // FNV-1a 32-bit — deterministic across processes (same as CoopControls.Fnv).
        private static int Fnv(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= 16777619u; }
            return unchecked((int)h);
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(16); return true; }
            catch (Exception e) { Log.LogWarning("[valve] buf: " + e.Message); return false; }
        }
    }
}
