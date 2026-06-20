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
    /// HOW (phase-driven, not button-driven — robust to which control started it):
    ///   • HOST watches its own <c>MissionManager.CurrentPhase</c>. On →MissionActive it broadcasts
    ///     MISSION_START; on MissionActive→(BrowsingMap/MainMenu) it broadcasts MISSION_END.
    ///   • CLIENT applies them: MISSION_START → drive its own <c>OperationLoadRelay.StartAssignedOperation()</c>
    ///     (the same path a player-click takes), which loads the SAME mission scene. The 4a sim-gate keeps the
    ///     client's MissionGraph from bootstrapping, so it loads the scene but spawns nothing — the host's
    ///     entities arrive via <see cref="CoopEntities"/>. MISSION_END → <c>ReturnToMap()</c> /
    ///     <c>EndOperationAndReturnToMenu()</c> to follow the host back out.
    ///   • Once the client reaches MissionActive it sends MISSION_READY; the host then re-sends the entity
    ///     snapshot — covering spawns the host streamed before the client's scene finished loading.
    ///
    /// Only the HOST broadcasts phase changes (the client only receives + applies), so there's no feedback loop.
    /// Join-in-progress: if the host is ALREADY in a mission when the client connects, <see cref="SendSnapshot"/>
    /// (from the JIP coordinator) sends MISSION_START so the joiner loads in.
    ///
    /// NOT YET HARDENED (noted): a client independently clicking "start operation" would desync (only the host's
    /// lifecycle should drive) — a future pass can Harmony-gate client-initiated StartOperation/LoadMission like
    /// CoopSim gates the graph. For now, host-drives is correct as long as the client doesn't self-start.
    /// </summary>
    internal static class CoopScene
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_MISSION_START = 19;  // [t][scene str][missionId str]  reliable  host->client
        public const byte MSG_MISSION_END = 20;    // [t][targetPhase i32]           reliable  host->client
        public const byte MSG_MISSION_READY = 21;  // [t]                            reliable  client->host

        private static int _lastPhase = -1;        // host: detect our own phase transitions
        private static bool _readySent;            // client: send READY once per mission entry

        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];
        private static readonly byte[] _scratch = new byte[256];

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopSceneSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { _lastPhase = -1; _readySent = false; return; }
            try
            {
                int phase = CurrentPhase();
                if (phase < 0) return;

                if (CoopP2P.IsHost)
                {
                    if (_lastPhase < 0) { _lastPhase = phase; return; }   // first read: adopt, don't fire
                    if (phase != _lastPhase)
                    {
                        if (phase == (int)MissionManager.GamePhase.MissionActive) SendMissionStart();
                        else if (_lastPhase == (int)MissionManager.GamePhase.MissionActive) SendMissionEnd(phase);
                        _lastPhase = phase;
                    }
                }
                else
                {
                    // Client: announce readiness once we've actually entered the mission scene.
                    bool inMission = phase == (int)MissionManager.GamePhase.MissionActive;
                    if (inMission && !_readySent) { SendMissionReady(); _readySent = true; }
                    if (!inMission) _readySent = false;
                    _lastPhase = phase;
                }
            }
            catch (Exception e) { Log.LogWarning("[scene] tick: " + e.Message); }
        }

        // ---------------- join-in-progress ----------------

        // Host → new joiner: if we're already mid-mission, command the joiner to load in. Called from the JIP
        // coordinator BEFORE the entity snapshot (the joiner must load the scene before it can mirror entities;
        // entity packets are dropped until it's InMission, then re-sent when it signals MISSION_READY).
        public static void SendSnapshot()
        {
            if (!Config.CoopSceneSync || !CoopP2P.IsHost) return;
            if (CurrentPhase() == (int)MissionManager.GamePhase.MissionActive) { SendMissionStart(); Log.LogInfo("[scene] JIP: host already in mission — commanding joiner to load in"); }
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            int o = 1;
            switch (type)
            {
                case MSG_MISSION_START:
                {
                    if (CoopP2P.IsHost) return;   // host drives; never follows
                    string scene = GetStr(a, ref o, len);
                    string missionId = GetStr(a, ref o, len);
                    Log.LogInfo($"[scene] MISSION_START <- peer (scene='{scene}' mission='{missionId}')");
                    if (CurrentPhase() == (int)MissionManager.GamePhase.MissionActive) { Log.LogInfo("[scene] already in a mission — ignoring start"); return; }
                    StartClientMission();
                    break;
                }
                case MSG_MISSION_END:
                {
                    if (CoopP2P.IsHost) return;
                    if (len < 5) return;
                    int target = GetInt(a, ref o);
                    Log.LogInfo($"[scene] MISSION_END <- peer (target phase={target})");
                    if (CurrentPhase() != (int)MissionManager.GamePhase.MissionActive) { Log.LogInfo("[scene] not in a mission — ignoring end"); return; }
                    EndClientMission(target);
                    break;
                }
                case MSG_MISSION_READY:
                {
                    if (!CoopP2P.IsHost) return;   // only the host answers a joiner's readiness
                    Log.LogInfo("[scene] MISSION_READY <- peer — re-sending entity snapshot");
                    try { CoopEntities.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[scene] ready->snapshot: " + e.Message); }
                    break;
                }
            }
        }

        // ---------------- client drive ----------------

        // Drive the client into the mission the same way a player click does — via the scene's OperationLoadRelay
        // (it holds the OperationGraph asset; the client's copy is the same asset, same build). The 4a sim-gate
        // then suppresses the client's graph bootstrap so it loads the scene without spawning.
        private static void StartClientMission()
        {
            try
            {
                var relays = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<OperationLoadRelay>(), FindObjectsSortMode.None);
                if (relays != null && relays.Length > 0)
                {
                    var r = relays[0].TryCast<OperationLoadRelay>();
                    if (r != null) { r.StartAssignedOperation(); Log.LogInfo("[scene] client starting operation via OperationLoadRelay (host-commanded)"); return; }
                }
                Log.LogWarning("[scene] MISSION_START but no OperationLoadRelay in scene to drive the client load — client stays in hub");
            }
            catch (Exception e) { Log.LogWarning("[scene] start client mission: " + e.Message); }
        }

        private static void EndClientMission(int targetPhase)
        {
            try
            {
                var mm = MissionManager.Instance;
                if (mm == null) return;
                if (targetPhase == (int)MissionManager.GamePhase.MainMenu) { mm.EndOperationAndReturnToMenu(); Log.LogInfo("[scene] client ending operation -> menu (host-commanded)"); }
                else { mm.ReturnToMap(); Log.LogInfo("[scene] client returning to map (host-commanded)"); }
            }
            catch (Exception e) { Log.LogWarning("[scene] end client mission: " + e.Message); }
        }

        // ---------------- send ----------------

        private static void SendMissionStart()
        {
            if (!EnsureBuf()) return;
            string scene = "", mid = "";
            try { var mm = MissionManager.Instance; if (mm != null) { scene = mm.CurrentMissionSceneName ?? ""; var m = mm.CurrentMission; if (m != null) mid = m.MissionID ?? ""; } } catch { }
            int o = 0; _buf[o++] = MSG_MISSION_START; o = PutStr(o, scene); o = PutStr(o, mid);
            CoopP2P.Send(_buf, o, true);
            Log.LogInfo($"[scene] MISSION_START -> peer (scene='{scene}' mission='{mid}')");
        }

        private static void SendMissionEnd(int targetPhase)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_MISSION_END; o = PutInt(o, targetPhase);
            CoopP2P.Send(_buf, o, true);
            Log.LogInfo($"[scene] MISSION_END -> peer (target phase={targetPhase})");
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
            return $"scene: phase={phase} role={(CoopP2P.IsHost ? "HOST(drives)" : (CoopP2P.HasPeer ? "client(follows)" : "solo"))} readySent={_readySent}";
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

        private static int PutInt(int o, int v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }

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
