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
        // Exact wire sizes so truncated packets are rejected instead of parsing stale buffer bytes.
        private const int HeadPacketLen = 2 + 12 + 16;        // msg + flag + headPos(3f) + headRot(4f) = 30
        private const int HandPacketLen = 2 + (12 + 16) * 3;  // + left + right = 86

        private static bool _inited;
        private static ulong _myId;
        private static Callback<P2PSessionRequest_t> _cbSession;
        private static Il2CppStructArray<byte> _sendArr;   // persistent, sized for max packet
        private static Il2CppStructArray<byte> _recvArr;
        private static readonly byte[] _f4 = new byte[4];

        public static CSteamID Peer;
        public static bool HasPeer;

        // Role: the lobby owner is the host. Used as the ownership tie-breaker in Phase 3 (control sync) and
        // for sim-gating in Phase 4. Recomputed each tick while in a lobby; false when solo / not in a lobby.
        public static bool IsHost;
        public static ulong MyId => _myId;

        // Latest remote pose (world space).
        public static bool RemoteValid;
        private static float _remoteAge;
        public static Vector3 HeadPos; public static Quaternion HeadRot = Quaternion.identity;
        public static bool HasHands;
        public static Vector3 LPos, RPos; public static Quaternion LRot = Quaternion.identity, RRot = Quaternion.identity;
        public static Vector3 LastSentHead;   // diag: the head world-pos we last transmitted

        public static bool SelfTest;   // F6: mirror local pose as a fake remote, to verify avatar rendering solo

        private static int _sent, _recvd;
        private static float _nextSend;   // transmit-rate cap (see Config.CoopSendHz)

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
            try
            {
                // Accepting a session NAT-punches and reveals our IP to the requester, so only accept from a
                // current lobby member. (Gate the accept on membership — looser than HasPeer==sender so we
                // don't reject the peer's first request before UpdatePeer has resolved them; consume is strict.)
                if (!IsCurrentLobbyMember(r.m_steamIDRemote))
                {
                    Log.LogWarning($"[p2p] ignoring P2P session from non-lobby sender {r.m_steamIDRemote.m_SteamID}");
                    return;
                }
                SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote);
                Log.LogInfo($"[p2p] accepted P2P session from {r.m_steamIDRemote.m_SteamID}");
            }
            catch (Exception e) { Log.LogWarning("[p2p] accept: " + e.Message); }
        }

        private static bool IsCurrentLobbyMember(CSteamID id)
        {
            if (!SteamNet.InLobby) return false;
            try
            {
                var lobby = SteamNet.CurrentLobby;
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < n; i++)
                    if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == id.m_SteamID) return true;
            }
            catch { }
            return false;
        }

        private static void CloseSession(CSteamID id)
        {
            try { SteamNetworking.CloseP2PSessionWithUser(id); Log.LogInfo($"[p2p] closed P2P session with {id.m_SteamID}"); }
            catch (Exception e) { Log.LogWarning("[p2p] close: " + e.Message); }
        }

        public static void Tick(float dt)
        {
            if (!SteamNet.Ready) return;
            Init();
            UpdatePeer();
            Receive();
            if (RemoteValid && !SelfTest) { _remoteAge += dt; if (_remoteAge > Config.RemoteStaleSeconds) { RemoteValid = false; Log.LogInfo($"[p2p] remote pose stale ({_remoteAge:F1}s) — hiding avatar"); } }
        }

        // One-line status for the co-op diagnostics hub. Position diag: myCam = where THIS player actually is
        // (ground truth); mySent = the head pose we transmit; remHead = where we DRAW the peer. Cross-machine,
        // one player's myCam should match the other player's remHead — if not, the two worlds aren't aligned.
        public static string Status()
        {
            Camera cam = null; try { cam = Camera.main; } catch { }
            string myCam = cam != null ? V(cam.transform.position) : "n/a";
            return $"net: {(SteamNet.InLobby ? "in-lobby" : "no-lobby")} peer={(HasPeer ? Peer.m_SteamID.ToString() : "none")} " +
                   $"role={(IsHost ? "HOST" : "client")} sent={_sent} recvd={_recvd} | avatar valid={RemoteValid} hands={HasHands} " +
                   $"remHead={V(HeadPos)} myCam={myCam} mySent={V(LastSentHead)}";
        }

        private static void UpdatePeer()
        {
            if (!SteamNet.InLobby)
            {
                if (HasPeer) { CloseSession(Peer); HasPeer = false; Log.LogInfo("[p2p] not in lobby — peer cleared"); }
                IsHost = false;
                if (!SelfTest) RemoteValid = false;
                return;
            }
            try
            {
                var lobby = SteamNet.CurrentLobby;
                try { IsHost = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID == _myId; } catch { }
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < n; i++)
                {
                    var m = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                    if (m.m_SteamID != _myId)
                    {
                        if (!HasPeer || Peer.m_SteamID != m.m_SteamID)
                        {
                            if (HasPeer && Peer.m_SteamID != m.m_SteamID) CloseSession(Peer);  // drop the old peer's session
                            Peer = m; HasPeer = true;
                            if (!SelfTest) RemoteValid = false;   // don't carry an old peer's avatar onto the new one
                            Log.LogInfo($"[p2p] peer = {m.m_SteamID}");
                        }
                        return;
                    }
                }
                if (HasPeer) { CloseSession(Peer); HasPeer = false; Log.LogInfo("[p2p] peer left lobby"); }
                if (!SelfTest) RemoteValid = false;
            }
            catch (Exception e) { Log.LogWarning("[p2p] peer: " + e.Message); }
        }

        public static void SendPose(Vector3 hp, Quaternion hr, bool hasHands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr)
        {
            LastSentHead = hp;   // record even if we don't send, so the diag shows what we WOULD transmit
            if (!_inited || !HasPeer) return;
            // Rate cap: skip this frame's send if we're ahead of the target Hz. Framerate below the cap always
            // passes (now >= _nextSend), so a slow peer keeps sending every frame.
            float now = Time.unscaledTime;
            if (Config.CoopSendHz > 0f)
            {
                if (now < _nextSend) return;
                _nextSend = now + 1f / Config.CoopSendHz;
            }
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

        // Generic send for non-pose channels (Phase 3 control sync). The caller owns its buffer + framing
        // (first byte = message type, distinct from MSG_POSE). Reliable for state-transition events
        // (grab/release), unreliable-no-delay for the continuous value/state stream. Shares the pose channel
        // and peer; safe because every packet is type-tagged and Receive() dispatches on the first byte.
        public static bool Send(Il2CppStructArray<byte> buf, int len, bool reliable)
        {
            if (!_inited || !HasPeer) return false;
            try
            {
                var mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
                if (SteamNetworking.SendP2PPacket(Peer, buf, (uint)len, mode, Channel)) { _sent++; return true; }
            }
            catch (Exception e) { Log.LogWarning("[p2p] send2: " + e.Message); }
            return false;
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
                    // Only the current lobby peer may drive our state — drop anything else (spoof / stale peer).
                    if (!HasPeer || from.m_SteamID != Peer.m_SteamID) continue;
                    if (read < 1) continue;

                    // Non-pose packets are Phase 3 control-sync — dispatch by type byte and move on.
                    if (_recvArr[0] != MSG_POSE)
                    {
                        try { CoopControls.OnPacket(_recvArr[0], _recvArr, (int)read); } catch (Exception ce) { Log.LogWarning("[ctrl] recv: " + ce.Message); }
                        _recvd++;
                        continue;
                    }
                    if (read < 2) continue;

                    byte flag = _recvArr[1];
                    if (flag > 1) continue;                                  // unknown flag (sender only writes 0/1)
                    bool hands = flag == 1;
                    if (read < (uint)(hands ? HandPacketLen : HeadPacketLen)) continue;  // truncated — no stale-byte parse

                    // Parse into locals, validate, THEN publish — so a bad packet never leaves torn pose state.
                    int o = 2;
                    Vector3 hp = GetV(ref o); Quaternion hr = GetQ(ref o);
                    Vector3 lp = Vector3.zero, rp = Vector3.zero; Quaternion lr = Quaternion.identity, rr = Quaternion.identity;
                    if (hands) { lp = GetV(ref o); lr = GetQ(ref o); rp = GetV(ref o); rr = GetQ(ref o); }

                    // NaN/Inf into a Unity transform poisons the hierarchy; a zero quaternion normalizes to NaN.
                    if (!Finite(hp) || !FiniteRot(hr)) continue;
                    if (hands && (!Finite(lp) || !Finite(rp) || !FiniteRot(lr) || !FiniteRot(rr))) continue;

                    HeadPos = hp; HeadRot = hr; HasHands = hands;
                    if (hands) { LPos = lp; LRot = lr; RPos = rp; RRot = rr; }
                    RemoteValid = true; _remoteAge = 0f; _recvd++;
                }
            }
            catch (Exception e) { Log.LogWarning("[p2p] recv: " + e.Message); }
        }

        // --- (de)serialization: write into the persistent Il2Cpp send array, read from the recv array ---
        private static int PutF(int o, float f) { var t = BitConverter.GetBytes(f); _sendArr[o] = t[0]; _sendArr[o + 1] = t[1]; _sendArr[o + 2] = t[2]; _sendArr[o + 3] = t[3]; return o + 4; }
        private static int PutV(int o, Vector3 v) { o = PutF(o, v.x); o = PutF(o, v.y); o = PutF(o, v.z); return o; }
        private static int PutQ(int o, Quaternion q) { o = PutF(o, q.x); o = PutF(o, q.y); o = PutF(o, q.z); o = PutF(o, q.w); return o; }

        private static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool FiniteRot(Quaternion q)
        {
            if (!Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) return false;
            return (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 0.0001f;   // reject degenerate/zero quaternion
        }

        private static float GetF(ref int o) { _f4[0] = _recvArr[o]; _f4[1] = _recvArr[o + 1]; _f4[2] = _recvArr[o + 2]; _f4[3] = _recvArr[o + 3]; o += 4; return BitConverter.ToSingle(_f4, 0); }
        private static Vector3 GetV(ref int o) { float x = GetF(ref o), y = GetF(ref o), z = GetF(ref o); return new Vector3(x, y, z); }
        private static Quaternion GetQ(ref int o) { float x = GetF(ref o), y = GetF(ref o), z = GetF(ref o), w = GetF(ref o); return new Quaternion(x, y, z, w); }
    }
}
