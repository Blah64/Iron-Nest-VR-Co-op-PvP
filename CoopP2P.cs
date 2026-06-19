using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 2 co-op transport: a Steam P2P data channel between the two lobby members, streaming each
    /// player's head + hand poses so the other sees an avatar. Uses the byte-array SteamNetworking P2P API
    /// (SendP2PPacket/ReadP2PPacket) — simpler than SteamNetworkingMessages and avoids the message-struct
    /// pointer unpacking. The peer is the other member of the current <see cref="SteamNet"/> lobby.
    ///
    /// Poses are sent in WORLD space (both instances load the same scene at the same world coords, so the
    /// remote head lands where that player actually is). If the rotating cockpit (Barbet) makes avatars
    /// drift once turret aim is desynced, switch to Barbet-local in a later pass.
    /// </summary>
    internal static class CoopP2P
    {
        private static ManualLogSource Log => Plugin.Logger;

        private const int Channel = 0;
        private const byte MSG_POSE = 1;

        private static bool _inited;
        private static ulong _myId;
        private static Callback<P2PSessionRequest_t> _cbSession;
        private static Il2CppStructArray<byte> _sendArr;   // persistent, sized for max packet
        private static Il2CppStructArray<byte> _recvArr;
        private static readonly byte[] _f4 = new byte[4];

        public static CSteamID Peer;
        public static bool HasPeer;

        // Latest remote pose (world space).
        public static bool RemoteValid;
        private static float _remoteAge;
        public static Vector3 HeadPos; public static Quaternion HeadRot = Quaternion.identity;
        public static bool HasHands;
        public static Vector3 LPos, RPos; public static Quaternion LRot = Quaternion.identity, RRot = Quaternion.identity;

        public static bool SelfTest;   // F6: mirror local pose as a fake remote, to verify avatar rendering solo

        private static int _sent, _recvd;
        private static float _nextStat;

        public static void Init()
        {
            if (_inited) return;
            try
            {
                _myId = SteamUser.GetSteamID().m_SteamID;
                _sendArr = new Il2CppStructArray<byte>(128);
                _recvArr = new Il2CppStructArray<byte>(1200);
                _cbSession = Callback<P2PSessionRequest_t>.Create((Action<P2PSessionRequest_t>)OnSessionRequest);
                _inited = true;
                Log.LogInfo("[p2p] init — P2P session callback registered");
            }
            catch (Exception e) { Log.LogError("[p2p] init failed: " + e); }
        }

        private static void OnSessionRequest(P2PSessionRequest_t r)
        {
            try { SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote); Log.LogInfo($"[p2p] accepted P2P session from {r.m_steamIDRemote.m_SteamID}"); }
            catch (Exception e) { Log.LogWarning("[p2p] accept: " + e.Message); }
        }

        public static void Tick(float dt)
        {
            if (!SteamNet.Ready) return;
            Init();
            UpdatePeer();
            Receive();
            if (RemoteValid && !SelfTest) { _remoteAge += dt; if (_remoteAge > 2f) { RemoteValid = false; Log.LogInfo("[p2p] remote pose stale — hiding avatar"); } }
            if (Time.unscaledTime >= _nextStat)
            {
                _nextStat = Time.unscaledTime + 5f;
                if (HasPeer || _sent > 0 || _recvd > 0) Log.LogInfo($"[p2p] peer={(HasPeer ? Peer.m_SteamID.ToString() : "none")} sent={_sent} recvd={_recvd} remoteValid={RemoteValid}");
            }
        }

        private static void UpdatePeer()
        {
            if (!SteamNet.InLobby)
            {
                if (HasPeer) { HasPeer = false; Log.LogInfo("[p2p] not in lobby — peer cleared"); }
                if (!SelfTest) RemoteValid = false;
                return;
            }
            try
            {
                var lobby = SteamNet.CurrentLobby;
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < n; i++)
                {
                    var m = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                    if (m.m_SteamID != _myId)
                    {
                        if (!HasPeer || Peer.m_SteamID != m.m_SteamID) { Peer = m; HasPeer = true; Log.LogInfo($"[p2p] peer = {m.m_SteamID}"); }
                        return;
                    }
                }
                if (HasPeer) { HasPeer = false; Log.LogInfo("[p2p] peer left lobby"); }
                if (!SelfTest) RemoteValid = false;
            }
            catch (Exception e) { Log.LogWarning("[p2p] peer: " + e.Message); }
        }

        public static void SendPose(Vector3 hp, Quaternion hr, bool hasHands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr)
        {
            if (!_inited || !HasPeer) return;
            try
            {
                int o = 0;
                _sendArr[o++] = MSG_POSE;
                _sendArr[o++] = (byte)(hasHands ? 1 : 0);
                o = PutV(o, hp); o = PutQ(o, hr);
                if (hasHands) { o = PutV(o, lp); o = PutQ(o, lr); o = PutV(o, rp); o = PutQ(o, rr); }
                if (SteamNetworking.SendP2PPacket(Peer, _sendArr, (uint)o, EP2PSend.k_EP2PSendUnreliableNoDelay, Channel)) _sent++;
            }
            catch (Exception e) { Log.LogWarning("[p2p] send: " + e.Message); }
        }

        // Fake-remote injection for the F6 solo render test.
        public static void InjectRemote(Vector3 hp, Quaternion hr, bool hasHands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr)
        {
            HeadPos = hp; HeadRot = hr; HasHands = hasHands; LPos = lp; LRot = lr; RPos = rp; RRot = rr;
            RemoteValid = true; _remoteAge = 0f;
        }

        private static void Receive()
        {
            try
            {
                int guard = 0;
                while (SteamNetworking.IsP2PPacketAvailable(out _, Channel) && guard++ < 64)
                {
                    if (!SteamNetworking.ReadP2PPacket(_recvArr, (uint)_recvArr.Length, out uint read, out CSteamID from, Channel)) break;
                    if (read < 2 || _recvArr[0] != MSG_POSE) continue;
                    bool hands = _recvArr[1] != 0;
                    int o = 2;
                    HeadPos = GetV(ref o); HeadRot = GetQ(ref o);
                    HasHands = hands;
                    if (hands) { LPos = GetV(ref o); LRot = GetQ(ref o); RPos = GetV(ref o); RRot = GetQ(ref o); }
                    RemoteValid = true; _remoteAge = 0f; _recvd++;
                }
            }
            catch (Exception e) { Log.LogWarning("[p2p] recv: " + e.Message); }
        }

        // --- (de)serialization: write into the persistent Il2Cpp send array, read from the recv array ---
        private static int PutF(int o, float f) { var t = BitConverter.GetBytes(f); _sendArr[o] = t[0]; _sendArr[o + 1] = t[1]; _sendArr[o + 2] = t[2]; _sendArr[o + 3] = t[3]; return o + 4; }
        private static int PutV(int o, Vector3 v) { o = PutF(o, v.x); o = PutF(o, v.y); o = PutF(o, v.z); return o; }
        private static int PutQ(int o, Quaternion q) { o = PutF(o, q.x); o = PutF(o, q.y); o = PutF(o, q.z); o = PutF(o, q.w); return o; }

        private static float GetF(ref int o) { _f4[0] = _recvArr[o]; _f4[1] = _recvArr[o + 1]; _f4[2] = _recvArr[o + 2]; _f4[3] = _recvArr[o + 3]; o += 4; return BitConverter.ToSingle(_f4, 0); }
        private static Vector3 GetV(ref int o) { float x = GetF(ref o), y = GetF(ref o), z = GetF(ref o); return new Vector3(x, y, z); }
        private static Quaternion GetQ(ref int o) { float x = GetF(ref o), y = GetF(ref o), z = GetF(ref o), w = GetF(ref o); return new Quaternion(x, y, z, w); }
    }
}
