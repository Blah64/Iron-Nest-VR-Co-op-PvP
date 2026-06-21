using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op: host-authoritative SCORE / mission-outcome replication so the client's win/lose result and
    /// requisition currency match the host. Two pieces, split by risk:
    ///
    /// (1) MISSION OUTCOME (the result screen) — a Harmony postfix on <c>MissionManager.MarkMissionComplete</c> /
    /// <c>MarkMissionFailed</c> (registered by CoopSim). The HOST in a mission broadcasts the outcome; the client
    /// replays the same call so it shows the same success/failure result. Host-only broadcast → no loop (the
    /// client's replay re-hits the postfix but bails on !IsHost). The client's own narrow-gated graph may not reach
    /// completion on its own (its objectives track host-authored kills), so the host telling it is what guarantees
    /// the screens match. Idempotent.
    ///
    /// (2) REQUISITION / POWDER (the persistent progression numbers) — <c>OperationState.RequisitionPoints</c> +
    /// <c>PowderCharges</c>. The only setter the game exposes is <c>LoadOperationState</c>, which does heavy work
    /// (reloads cards/punchcards), so we NEVER apply it mid-mission. The host broadcasts the two scalars only while
    /// OUT of a mission (BrowsingMap/menu, where they're finalized and about to be spent); the client patches them
    /// onto its OWN <c>SaveOperationState()</c> snapshot and <c>LoadOperationState</c>s that — overlaying the host's
    /// numbers without wiping the client's own structure, at the safe between-missions moment.
    ///
    /// LIMITATION (noted): per-mission MEDALS (the <c>MissionState.Medals</c> dict) are NOT round-tripped here yet —
    /// only the two scalar numbers + completion (which rides the outcome replay). Medals are a small follow-up if
    /// the demo surfaces them. The in-mission running point counter may differ between machines; it finalizes to the
    /// host's value on return to the map.
    /// </summary>
    internal static class CoopScore
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_OUTCOME = 28;   // [t][kind u8]  (0=complete, 1=failed)  reliable  host->client
        public const byte MSG_OPSTATE = 29;   // [t][reqPoints i32][powderCharges i32]  reliable host->client (out-of-mission only)

        private static int _lastReq = int.MinValue, _lastPow = int.MinValue;
        private static float _nextSend;
        private static int _outcomes, _opstates;

        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];

        // ---------------- outcome capture (Harmony postfix, registered by CoopSim) ----------------

        public static void OnMissionComplete() => OnOutcome(0);
        public static void OnMissionFailed() => OnOutcome(1);

        private static void OnOutcome(byte kind)
        {
            try
            {
                if (!Config.CoopScoreSync || !CoopP2P.IsHost) return;
                if (!SteamNet.InLobby || !CoopP2P.HasPeer) return;
                if (!EnsureBuf()) return;
                int o = 0; _buf[o++] = MSG_OUTCOME; _buf[o++] = kind;
                CoopP2P.Send(_buf, o, true);
                _outcomes++;
                Log.LogInfo($"[score] mission {(kind == 0 ? "COMPLETE" : "FAILED")} -> peer");
            }
            catch (Exception e) { Log.LogWarning("[score] outcome: " + e.Message); }
        }

        // ---------------- per-frame (host sends requisition; both keep counters) ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopScoreSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { _lastReq = int.MinValue; _lastPow = int.MinValue; return; }
            if (!CoopP2P.IsHost) return;            // client only receives
            if (InMission()) return;                // never round-trip OperationState mid-mission
            float now = Time.unscaledTime;
            if (now < _nextSend) return;
            _nextSend = now + 0.5f;
            try
            {
                var mm = MissionManager.Instance; if (mm == null) return;
                OperationState st = null; try { st = mm.SaveOperationState(); } catch { }
                if (st == null) return;
                string opId = null; try { opId = st.OperationID; } catch { }
                if (string.IsNullOrEmpty(opId)) return;   // no active operation (e.g. main menu) — nothing to sync
                int req, pow; try { req = st.RequisitionPoints; pow = st.PowderCharges; } catch { return; }
                if (req == _lastReq && pow == _lastPow) return;
                _lastReq = req; _lastPow = pow;
                if (!EnsureBuf()) return;
                int o = 0; _buf[o++] = MSG_OPSTATE; o = PutInt(o, req); o = PutInt(o, pow);
                CoopP2P.Send(_buf, o, true);
                _opstates++;
                Log.LogInfo($"[score] requisition -> peer (points={req} powder={pow})");
            }
            catch (Exception e) { Log.LogWarning("[score] tick: " + e.Message); }
        }

        // ---------------- receive (client) ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            if (CoopP2P.IsHost) return;   // host is authoritative; never applies
            int o = 1;
            switch (type)
            {
                case MSG_OUTCOME:
                {
                    if (len < 2) return;
                    byte kind = a[o++];
                    try
                    {
                        var mm = MissionManager.Instance; if (mm == null) return;
                        if (kind == 0) mm.MarkMissionComplete(); else mm.MarkMissionFailed();
                        Log.LogInfo($"[score] applied mission {(kind == 0 ? "COMPLETE" : "FAILED")} <- peer");
                    }
                    catch (Exception e) { Log.LogWarning("[score] apply outcome: " + e.Message); }
                    break;
                }
                case MSG_OPSTATE:
                {
                    if (len < 1 + 4 + 4) return;
                    int req = GetInt(a, ref o);
                    int pow = GetInt(a, ref o);
                    try
                    {
                        var mm = MissionManager.Instance; if (mm == null) return;
                        if (InMission()) return;   // safety: only apply the heavy LoadOperationState out of a mission
                        var st = mm.SaveOperationState(); if (st == null) return;
                        int curReq, curPow; try { curReq = st.RequisitionPoints; curPow = st.PowderCharges; } catch { return; }
                        if (curReq == req && curPow == pow) return;   // already matches — don't churn LoadOperationState
                        try { st.RequisitionPoints = req; st.PowderCharges = pow; } catch { return; }
                        mm.LoadOperationState(st);                    // overlay host's numbers onto our own state
                        Log.LogInfo($"[score] applied requisition <- peer (points={req} powder={pow})");
                    }
                    catch (Exception e) { Log.LogWarning("[score] apply opstate: " + e.Message); }
                    break;
                }
            }
        }

        // ---------------- diagnostics ----------------

        public static string Status() => $"score: req={(_lastReq == int.MinValue ? 0 : _lastReq)} powder={(_lastPow == int.MinValue ? 0 : _lastPow)} outcomes={_outcomes} opstates={_opstates}";

        // ---------------- helpers ----------------

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(16); return true; }
            catch (Exception e) { Log.LogWarning("[score] buf: " + e.Message); return false; }
        }

        private static int PutInt(int o, int v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }
        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_f4, 0); }
    }
}
