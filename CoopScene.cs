using System;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op (increment 4b-keystone): replicate the MISSION / SCENE TRANSITION so both players are
    /// co-located. Sim-gating (4a) + entity sync (4b) assume both are already in the same MissionActive scene —
    /// but nothing got them there: when the host starts a mission, the client stayed in the hub. This closes
    /// that gap, host-authoritatively.
    ///
    /// HOW (phase-driven, not button-driven — robust to which control started it). The game has THREE phases —
    /// MainMenu → BrowsingMap (the operations map, where the per-mission OperationLoadRelays live) → MissionActive
    /// — and the client must follow the host through ALL of them (an OperationLoadRelay only exists in the
    /// BrowsingMap scene, so a client still at MainMenu literally has nothing to start — the bug the first 2-player
    /// test hit).
    ///   • HOST watches its own <c>MissionManager.CurrentPhase</c> and replicates EVERY transition: →MissionActive
    ///     broadcasts MISSION_START (with the OperationID); →BrowsingMap / →MainMenu broadcast GO_TO_PHASE.
    ///   • CLIENT applies them: GO_TO_PHASE → <c>EnterBrowsingMap()</c> / <c>ReturnToMap()</c> / <c>LoadMainMenu()</c>
    ///     / <c>EndOperationAndReturnToMenu()</c> (picked by where it currently is). MISSION_START → start the
    ///     OperationLoadRelay whose <c>operation.OperationID</c> matches the host's. The client's mission graph
    ///     bootstraps and runs normally now; the 4a NARROW gate only suppresses its enemy SPAWN node, so it
    ///     authors no enemies of its own — the host's entities arrive via <see cref="CoopEntities"/>.
    ///   • Once the client reaches MissionActive it sends MISSION_READY; the host then re-sends the entity
    ///     snapshot — covering spawns the host streamed before the client's scene finished loading.
    ///
    /// Only the HOST broadcasts phase changes (the client only receives + applies), so there's no feedback loop.
    /// Join-in-progress: <see cref="SendSnapshot"/> tells a fresh joiner the host's CURRENT phase (enter the map;
    /// and if mid-mission, the map command + MISSION_START), so a peer that joined at the MainMenu catches up.
    ///
    /// HARDENED (2026-06-21): a client clicking "start operation" itself can no longer desync — a Harmony prefix
    /// (<c>GateClientStart</c>, registered by CoopSim) blocks <c>MissionManager.StartOperation</c> on a co-op
    /// client unless <c>ApplyingRemoteStart</c> is set (the host-commanded path). The host and solo play are
    /// never blocked. Only host lifecycle drives missions.
    /// </summary>
    internal static class CoopScene
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_MISSION_START = 19;  // [t][scene str][missionId str][operationId str]  reliable  host->client
        public const byte MSG_MISSION_END = 20;    // [t][targetPhase i32]  reliable host->client — "go to non-mission phase" (BrowsingMap/MainMenu)
        public const byte MSG_MISSION_READY = 21;  // [t]                            reliable  client->host

        private static int _lastPhase = -1;        // host: detect our own phase transitions
        private static bool _readySent;            // client: send READY once per mission entry

        // Client: a received MISSION_START is retried until the scene's OperationLoadRelay is actually available
        // (REVIEW-fix P1 — the relay may not exist on the receive frame during a scene/hub transition). We keep
        // trying for a bounded window rather than dropping the command on the first miss.
        private static float _pendingStartUntil;   // 0 = no pending start; else deadline (unscaledTime)
        private static float _nextStartTry;        // next retry time
        private static bool _startInvoked;         // StartAssignedOperation already called → wait for the load
        private static string _pendingScene, _pendingMission, _pendingOperation;

        // Set true ONLY around the host-commanded StartOperation so the client self-start guard (GateClientStart,
        // a Harmony prefix on MissionManager.StartOperation) lets that one call through while blocking any other
        // client-initiated start.
        internal static bool ApplyingRemoteStart;

        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];
        private static readonly byte[] _scratch = new byte[256];

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopSceneSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { _lastPhase = -1; _readySent = false; _pendingStartUntil = 0f; _startInvoked = false; return; }
            try
            {
                int phase = CurrentPhase();
                if (phase < 0) return;

                if (CoopP2P.IsHost)
                {
                    if (_lastPhase < 0) { _lastPhase = phase; return; }   // first read: adopt, don't fire
                    if (phase != _lastPhase)
                    {
                        // Replicate EVERY transition so the client follows the host through the whole chain
                        // (MainMenu→BrowsingMap→Mission). Previously only the Mission edges were sent, so a client
                        // at the MainMenu never reached the BrowsingMap and had no relay to start.
                        if (phase == (int)MissionManager.GamePhase.MissionActive) SendMissionStart();
                        else SendGoToPhase(phase);
                        _lastPhase = phase;
                    }
                }
                else
                {
                    // Client: announce readiness once we've actually entered the mission scene.
                    bool inMission = phase == (int)MissionManager.GamePhase.MissionActive;
                    if (inMission)
                    {
                        if (_pendingStartUntil > 0f) { _pendingStartUntil = 0f; _startInvoked = false; Log.LogInfo("[scene] client reached mission scene (host-commanded start complete)"); }
                        if (!_readySent) { SendMissionReady(); _readySent = true; }
                    }
                    else
                    {
                        _readySent = false;
                        // Retry the host-commanded load until the relay shows up or we time out.
                        if (_pendingStartUntil > 0f)
                        {
                            float now = Time.unscaledTime;
                            if (now >= _pendingStartUntil) { _pendingStartUntil = 0f; _startInvoked = false; Log.LogWarning($"[scene] gave up waiting for OperationLoadRelay — client did not load host mission '{_pendingMission}'"); }
                            else if (!_startInvoked && now >= _nextStartTry) { _nextStartTry = now + 1f; if (StartClientMission(_pendingOperation, _pendingMission)) _startInvoked = true; }
                        }
                    }
                    _lastPhase = phase;
                }
            }
            catch (Exception e) { Log.LogWarning("[scene] tick: " + e.Message); }
        }

        // ---------------- join-in-progress ----------------

        // Host → new joiner: catch the joiner up to the host's CURRENT phase. A peer that connected at the
        // MainMenu must be told to enter the operations map (where the relays live); if the host is already
        // mid-mission, send the map command AND the mission-start (the joiner traverses the map, then the pending
        // MISSION_START retries until the map's relays load). Called from the JIP coordinator before the entity
        // snapshot (the joiner must reach the mission scene before it can mirror entities).
        public static void SendSnapshot()
        {
            if (!Config.CoopSceneSync || !CoopP2P.IsHost) return;
            int phase = CurrentPhase();
            if (phase == (int)MissionManager.GamePhase.MissionActive)
            {
                SendGoToPhase((int)MissionManager.GamePhase.BrowsingMap);
                SendMissionStart();
                Log.LogInfo("[scene] JIP: host in mission — sent map + mission-start so the joiner loads in");
            }
            else if (phase == (int)MissionManager.GamePhase.BrowsingMap)
            {
                SendGoToPhase((int)MissionManager.GamePhase.BrowsingMap);
                Log.LogInfo("[scene] JIP: host in operations map — commanding joiner to follow");
            }
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            int o = 1;
            switch (type)
            {
                case MSG_MISSION_START:
                {
                    if (CoopP2P.IsHost) return;   // host drives; never follows
                    string scene = GetStr(a, ref o, len);
                    string missionId = GetStr(a, ref o, len);
                    string operationId = GetStr(a, ref o, len) ?? "";
                    Log.LogInfo($"[scene] MISSION_START <- peer (scene='{scene}' mission='{missionId}' op='{operationId}')");
                    if (CurrentPhase() == (int)MissionManager.GamePhase.MissionActive) { Log.LogInfo("[scene] already in a mission — ignoring start"); return; }
                    // Arm a bounded retry: try now, and keep trying in Tick until the relay exists or we time out.
                    _pendingScene = scene; _pendingMission = missionId; _pendingOperation = operationId;
                    _pendingStartUntil = Time.unscaledTime + 12f;
                    _nextStartTry = Time.unscaledTime + 1f;
                    _startInvoked = StartClientMission(operationId, missionId);
                    if (!_startInvoked) Log.LogInfo("[scene] operation/mission graphs not loaded yet — will retry until the map scene is ready");
                    break;
                }
                case MSG_MISSION_END:   // generalized: "go to this non-mission phase" from ANY current phase
                {
                    if (CoopP2P.IsHost) return;
                    if (len < 5) return;
                    int target = GetInt(a, ref o);
                    Log.LogInfo($"[scene] GO_TO_PHASE <- peer (target phase={target})");
                    ApplyGoToPhase(target);
                    break;
                }
                case MSG_MISSION_READY:
                {
                    if (!CoopP2P.IsHost) return;   // only the host answers a joiner's readiness
                    Log.LogInfo($"[scene] MISSION_READY <- {origin} — re-sending entity snapshot to that peer");
                    // Target the resync to the joiner that asked — don't re-burst the entity snapshot onto every
                    // existing client each time someone reports ready (REVIEW-fix P2a).
                    try { CoopP2P.SendSnapshotTo(origin, () => CoopEntities.SendSnapshot()); } catch (Exception e) { Log.LogWarning("[scene] ready->snapshot: " + e.Message); }
                    break;
                }
            }
        }

        // ---------------- client drive ----------------

        // Drive the client into the SAME mission the host picked, by RESOLVING THE GRAPH ASSETS and calling
        // MissionManager.StartOperation directly — NOT via OperationLoadRelay.
        //
        // Why not the relay: each briefing's Play button is wired to its own OperationLoadRelay, but that relay
        // lives on the briefing panel and is INACTIVE until the player opens that briefing. The client never opens
        // a briefing (host-authoritative), so FindObjectsByType (active-only) finds no relay — the exact failure
        // the live test hit (client in BrowsingMap, "no OperationLoadRelay … gave up"). And StartAssignedOperation
        // starts a coroutine, which can't run on an inactive GameObject anyway.
        //
        // OperationGraph/MissionGraph are ScriptableObjects (StateGraph→NodeGraph→ScriptableObject), so we look
        // them up by their stable OperationID/MissionID via Resources.FindObjectsOfTypeAll (which includes inactive
        // + asset objects) and call MissionManager.StartOperation(op, mission) — the same entry the relay reaches.
        //
        // Returns true if it invoked StartOperation; false if the graph assets aren't loaded yet (caller retries
        // while the BrowsingMap scene is still spinning up).
        private static bool StartClientMission(string operationId, string missionId)
        {
            try
            {
                var mm = MissionManager.Instance; if (mm == null) return false;
                var op = FindOperationById(operationId);
                var mission = FindMissionById(missionId);
                if (op == null || mission == null) return false;   // assets not loaded yet — retry

                ApplyingRemoteStart = true;
                try { mm.StartOperation(op, mission); }
                finally { ApplyingRemoteStart = false; }
                Log.LogInfo($"[scene] client StartOperation(op='{operationId}', mission='{missionId}') (host-commanded)");
                return true;
            }
            catch (Exception e) { Log.LogWarning("[scene] start client mission: " + e.Message); return false; }
        }

        private static SleepyNodes.OperationGraph FindOperationById(string operationId)
        {
            if (string.IsNullOrEmpty(operationId)) return null;
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SleepyNodes.OperationGraph>());
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var op = arr[i].TryCast<SleepyNodes.OperationGraph>(); if (op == null) continue;
                    try { if (op.OperationID == operationId) return op; } catch { }
                }
            }
            catch (Exception e) { Log.LogWarning("[scene] find op: " + e.Message); }
            return null;
        }

        private static SleepyNodes.MissionGraph FindMissionById(string missionId)
        {
            if (string.IsNullOrEmpty(missionId)) return null;
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SleepyNodes.MissionGraph>());
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var m = arr[i].TryCast<SleepyNodes.MissionGraph>(); if (m == null) continue;
                    try { if (m.MissionID == missionId) return m; } catch { }
                }
            }
            catch (Exception e) { Log.LogWarning("[scene] find mission: " + e.Message); }
            return null;
        }

        // Harmony prefix on MissionManager.StartOperation (registered by CoopSim.ApplyPatches). Blocks a co-op
        // CLIENT from starting an operation on its own — e.g. clicking a briefing's Play button — so it can't
        // diverge into a different mission than the host. Only the host's lifecycle starts missions, replicated
        // via MISSION_START; that host-commanded start runs through StartClientMission, which sets
        // ApplyingRemoteStart so this guard lets it pass. Never blocks the host or solo play. Returns false to
        // skip the original. MUST NOT throw (a throw would break mission-start for everyone) → any error allows it.
        public static bool GateClientStart()
        {
            try
            {
                if (!Config.CoopSceneSync) return true;                  // feature off
                if (!SteamNet.InLobby || !CoopP2P.HasPeer) return true;  // solo
                if (CoopP2P.IsHost) return true;                         // host drives
                if (ApplyingRemoteStart) return true;                    // this IS the host-commanded start
                Log.LogInfo("[scene] blocked client-initiated StartOperation (host drives missions in co-op)");
                return false;                                            // client self-start — suppress
            }
            catch { return true; }
        }

        // Drive the client to a non-mission target phase, picking the right MissionManager call for where it
        // currently is (EnterBrowsingMap from the menu vs ReturnToMap from a mission; LoadMainMenu from the map vs
        // EndOperationAndReturnToMenu from a mission). Idempotent: no-op if already in the target phase.
        private static void ApplyGoToPhase(int targetPhase)
        {
            try
            {
                var mm = MissionManager.Instance; if (mm == null) return;
                int cur = (int)mm.CurrentPhase;
                if (cur == targetPhase) { Log.LogInfo($"[scene] already in phase {targetPhase} — nothing to do"); return; }
                _pendingStartUntil = 0f; _startInvoked = false;   // reaching a non-mission phase cancels any pending load

                if (targetPhase == (int)MissionManager.GamePhase.BrowsingMap)
                {
                    if (cur == (int)MissionManager.GamePhase.MissionActive) { mm.ReturnToMap(); Log.LogInfo("[scene] client returning to operations map (host-commanded)"); }
                    else { mm.EnterBrowsingMap(); Log.LogInfo("[scene] client entering operations map (host-commanded)"); }
                }
                else if (targetPhase == (int)MissionManager.GamePhase.MainMenu)
                {
                    if (cur == (int)MissionManager.GamePhase.MissionActive) { mm.EndOperationAndReturnToMenu(); Log.LogInfo("[scene] client ending operation -> menu (host-commanded)"); }
                    else { mm.LoadMainMenu(); Log.LogInfo("[scene] client -> main menu (host-commanded)"); }
                }
            }
            catch (Exception e) { Log.LogWarning("[scene] go-to-phase: " + e.Message); }
        }

        // ---------------- send ----------------

        private static void SendMissionStart()
        {
            if (!EnsureBuf()) return;
            string scene = "", mid = "", oid = "";
            try
            {
                var mm = MissionManager.Instance;
                if (mm != null)
                {
                    scene = mm.CurrentMissionSceneName ?? "";
                    var m = mm.CurrentMission; if (m != null) mid = m.MissionID ?? "";
                    var op = mm.CurrentOperation; if (op != null) oid = op.OperationID ?? "";
                }
            }
            catch { }
            int o = 0; _buf[o++] = MSG_MISSION_START; o = PutStr(o, scene); o = PutStr(o, mid); o = PutStr(o, oid);
            CoopP2P.Send(_buf, o, true);
            Log.LogInfo($"[scene] MISSION_START -> peer (scene='{scene}' mission='{mid}' op='{oid}')");
        }

        private static void SendGoToPhase(int targetPhase)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_MISSION_END; o = PutInt(o, targetPhase);
            CoopP2P.Send(_buf, o, true);
            Log.LogInfo($"[scene] GO_TO_PHASE -> peer (target phase={targetPhase})");
        }

        private static void SendMissionReady()
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_MISSION_READY;
            CoopP2P.Send(_buf, o, true);
            Log.LogInfo("[scene] MISSION_READY -> peer (client loaded the mission)");
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            string phase = "n/a";
            try { var mm = MissionManager.Instance; if (mm != null) phase = mm.CurrentPhase.ToString(); } catch { }
            string pend = _pendingStartUntil > 0f ? $" pendingStart='{_pendingMission}'(invoked={_startInvoked})" : "";
            return $"scene: phase={phase} role={(CoopP2P.IsHost ? "HOST(drives)" : (CoopP2P.HasPeer ? "client(follows)" : "solo"))} readySent={_readySent}{pend}";
        }

        // ---------------- helpers ----------------

        private static int CurrentPhase()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return -1; return (int)mm.CurrentPhase; }
            catch { return -1; }
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(256); return true; }
            catch (Exception e) { Log.LogWarning("[scene] buf: " + e.Message); return false; }
        }

        private static int PutInt(int o, int v) { _buf[o] = (byte)v; _buf[o + 1] = (byte)(v >> 8); _buf[o + 2] = (byte)(v >> 16); _buf[o + 3] = (byte)(v >> 24); return o + 4; }

        private static int PutStr(int o, string s)
        {
            s ??= "";
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = bytes.Length; if (n > 100) n = 100;
            o = PutInt(o, n);
            for (int i = 0; i < n; i++) _buf[o + i] = bytes[i];
            return o + n;
        }

        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_f4, 0); }

        private static string GetStr(Il2CppStructArray<byte> a, ref int o, int len)
        {
            if (o + 4 > len) return null;
            int n = GetInt(a, ref o);
            if (n < 0 || o + n > len || n > _scratch.Length) return null;
            for (int i = 0; i < n; i++) _scratch[i] = a[o + i];
            o += n;
            return Encoding.UTF8.GetString(_scratch, 0, n);
        }
    }
}
