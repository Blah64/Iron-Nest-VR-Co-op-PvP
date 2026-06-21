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
        // Pose flag-byte bits: hands present, and (implies hands) finger-curl present. Older builds sent the
        // flag as a plain 0/1 bool — the curl bit is additive, so a peer that doesn't send curl just leaves it 0.
        private const byte FLAG_HANDS = 1;
        private const byte FLAG_CURL = 2;
        // Exact wire sizes so truncated packets are rejected instead of parsing stale buffer bytes.
        private const int HeadPacketLen = 2 + 12 + 16;          // msg + flag + headPos(3f) + headRot(4f) = 30
        private const int HandPacketLen = 2 + (12 + 16) * 3;    // + left + right hand = 86
        private const int HandCurlPacketLen = HandPacketLen + 16; // + Lindex,Lother,Rindex,Rother (4f) = 102

        private static bool _inited;
        private static ulong _myId;
        private static Callback<P2PSessionRequest_t> _cbSession;
        private static Il2CppStructArray<byte> _sendArr;   // persistent, sized for max packet
        private static Il2CppStructArray<byte> _recvArr;
        private static readonly byte[] _f4 = new byte[4];
        private static byte[] _loopScratch;                // managed copy of an outbound packet for the loopback wire
        private static bool _wasLoopback;                  // so we can clear peer state when loopback ends

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
        // Remote finger curl (0..1): index from the trigger, "other" from the grip. RemoteAvatar bends the hand
        // mesh from these when HasCurl. Only set when the peer streams it (FLAG_CURL).
        public static bool HasCurl;
        public static float LCurlIndex, LCurlOther, RCurlIndex, RCurlOther;
        public static Vector3 LastSentHead;   // diag: the head world-pos we last transmitted

        // The peer's Steam persona name (for the join toast + the avatar name tag); "" when no peer.
        public static string PeerName = "";

        public static bool SelfTest;   // F6: mirror local pose as a fake remote, to verify avatar rendering solo

        private static int _sent, _recvd;
        private static float _nextSend;   // transmit-rate cap (see Config.CoopSendHz)

        // Join-in-progress: when the host detects a new peer it schedules a one-time full-world snapshot for
        // CoopSnapshotDelaySec later (0 = none pending). The delay lets both sides resolve the peer + bring the
        // session up so the snapshot isn't dropped by the receive-side peer gate. Cleared if the peer leaves.
        private static float _snapAt;

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
            // Same-machine TEST mode: route everything over the localhost link instead of Steam (see
            // LoopbackTransport). Mutually exclusive with the Steam path — we never touch SteamAPI here, so
            // the second instance doesn't even need Steam initialised.
            if (LoopbackTransport.Active) { _wasLoopback = true; TickLoopback(dt); return; }
            if (_wasLoopback)   // loopback was just stopped — drop the synthetic peer so subsystems clear
            {
                _wasLoopback = false;
                HasPeer = false; IsHost = false; PeerName = ""; _snapAt = 0f;
                if (!SelfTest) RemoteValid = false;
                Log.LogInfo("[p2p] loopback ended — peer cleared");
            }

            if (!SteamNet.Ready) return;
            Init();
            UpdatePeer();
            Receive();
            CoopNetSim.Pump(Time.unscaledTime, DispatchManaged);   // release any NetSim-delayed packets (test aid)
            if (_snapAt > 0f && Time.unscaledTime >= _snapAt) { _snapAt = 0f; SendJoinSnapshot(); }
            if (RemoteValid && !SelfTest) { _remoteAge += dt; if (_remoteAge > Config.RemoteStaleSeconds) { RemoteValid = false; Log.LogInfo($"[p2p] remote pose stale ({_remoteAge:F1}s) — hiding avatar"); } }
        }

        // Loopback (same-machine test) counterpart of the Steam Init+UpdatePeer+Receive trio. Derives peer +
        // role from the TCP link, gives the two sides synthetic deterministic ids (host=1, client=2) so the
        // host-priority ownership tie-break works without real SteamIDs, then drains the link through the SAME
        // DispatchPacket path the Steam receive uses. The join-in-progress snapshot rides along for free.
        private static void TickLoopback(float dt)
        {
            EnsureArrays();
            bool was = HasPeer;
            IsHost = LoopbackTransport.IsHost;
            HasPeer = LoopbackTransport.Connected;
            _myId = IsHost ? 1UL : 2UL;

            if (HasPeer && !was)
            {
                PeerName = IsHost ? "Loopback client" : "Loopback host";
                Log.LogInfo($"[p2p] loopback link up — role={(IsHost ? "HOST" : "client")}");
                Notify.PeerJoined(PeerName, IsHost);
                if (IsHost) { _snapAt = Time.unscaledTime + Config.CoopSnapshotDelaySec; Log.LogInfo("[p2p] host: loopback join snapshot scheduled"); }
                else _snapAt = 0f;
            }
            else if (!HasPeer && was)
            {
                Notify.PeerLeft(PeerName); PeerName = ""; _snapAt = 0f;
                if (!SelfTest) RemoteValid = false;
                Log.LogInfo("[p2p] loopback link down");
            }

            ReceiveLoopback();
            CoopNetSim.Pump(Time.unscaledTime, DispatchManaged);   // release any NetSim-delayed packets (test aid)
            if (_snapAt > 0f && Time.unscaledTime >= _snapAt) { _snapAt = 0f; SendJoinSnapshot(); }
            if (RemoteValid && !SelfTest) { _remoteAge += dt; if (_remoteAge > Config.RemoteStaleSeconds) { RemoteValid = false; Log.LogInfo($"[p2p] remote pose stale ({_remoteAge:F1}s) — hiding avatar"); } }
        }

        // Allocate the persistent Il2Cpp packet buffers (+ the managed loopback scratch). Init() does this for
        // the Steam path; loopback skips Init() entirely (no SteamAPI), so it allocates here instead.
        private static void EnsureArrays()
        {
            if (_sendArr == null) _sendArr = new Il2CppStructArray<byte>(128);
            if (_recvArr == null) _recvArr = new Il2CppStructArray<byte>(1200);
            if (_loopScratch == null) _loopScratch = new byte[1200];
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
                if (HasPeer) { CloseSession(Peer); HasPeer = false; PeerName = ""; Log.LogInfo("[p2p] not in lobby — peer cleared"); }
                IsHost = false;
                _snapAt = 0f;
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
                            try { PeerName = SteamFriends.GetFriendPersonaName(m); } catch { PeerName = ""; }
                            if (!SelfTest) RemoteValid = false;   // don't carry an old peer's avatar onto the new one
                            Log.LogInfo($"[p2p] peer = {m.m_SteamID} ('{PeerName}')");
                            Notify.PeerJoined(PeerName, IsHost);   // non-blocking toast (flat + VR)
                            // Host pushes a full-world snapshot to the joiner once the session settles.
                            if (IsHost) { _snapAt = Time.unscaledTime + Config.CoopSnapshotDelaySec; Log.LogInfo($"[p2p] host: join-in-progress snapshot scheduled in {Config.CoopSnapshotDelaySec:0.0}s"); }
                            else _snapAt = 0f;
                        }
                        return;
                    }
                }
                if (HasPeer) { Notify.PeerLeft(PeerName); CloseSession(Peer); HasPeer = false; PeerName = ""; Log.LogInfo("[p2p] peer left lobby"); }
                _snapAt = 0f;
                if (!SelfTest) RemoteValid = false;
            }
            catch (Exception e) { Log.LogWarning("[p2p] peer: " + e.Message); }
        }

        // Host-only: push a one-time authoritative snapshot of the whole shared world to a freshly-joined peer
        // so it converges to the host's state instead of its stale default. Each subsystem re-sends its current
        // state over its own reliable packets (turret/gun, clipboard text, map tokens); the joiner applies them
        // idempotently through the same paths the live stream uses. Fired from Tick once _snapAt elapses.
        private static void SendJoinSnapshot()
        {
            if (!HasPeer || !IsHost) return;
            Log.LogInfo("[p2p] host: sending join-in-progress snapshot to new peer");
            try { CoopControls.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot ctrl: " + e.Message); }
            try { CoopClipboard.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot clip: " + e.Message); }
            try { CoopMap.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot map: " + e.Message); }
            try { CoopScene.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot scene: " + e.Message); }   // mission-load command first
            try { CoopEntities.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot ent: " + e.Message); }
        }

        public static void SendPose(Vector3 hp, Quaternion hr, bool hasHands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr,
                                    bool hasCurl = false, float lCurlIdx = 0f, float lCurlOth = 0f, float rCurlIdx = 0f, float rCurlOth = 0f)
        {
            LastSentHead = hp;   // record even if we don't send, so the diag shows what we WOULD transmit
            if (!HasPeer) return;
            if (!LoopbackTransport.Active && !_inited) return;   // Steam path needs Init(); loopback doesn't
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
                bool curl = hasHands && hasCurl && Config.CoopFingerCurlSync;
                byte flag = 0;
                if (hasHands) flag |= FLAG_HANDS;
                if (curl) flag |= FLAG_CURL;

                int o = 0;
                _sendArr[o++] = MSG_POSE;
                _sendArr[o++] = flag;
                o = PutV(o, hp); o = PutQ(o, hr);
                if (hasHands) { o = PutV(o, lp); o = PutQ(o, lr); o = PutV(o, rp); o = PutQ(o, rr); }
                if (curl) { o = PutF(o, lCurlIdx); o = PutF(o, lCurlOth); o = PutF(o, rCurlIdx); o = PutF(o, rCurlOth); }
                if (LoopbackTransport.Active) { if (LoopSend(_sendArr, o)) _sent++; }
                else if (SteamNetworking.SendP2PPacket(Peer, _sendArr, (uint)o, EP2PSend.k_EP2PSendUnreliableNoDelay, Channel)) _sent++;
            }
            catch (Exception e) { Log.LogWarning("[p2p] send: " + e.Message); }
        }

        // Generic send for non-pose channels (Phase 3 control sync). The caller owns its buffer + framing
        // (first byte = message type, distinct from MSG_POSE). Reliable for state-transition events
        // (grab/release), unreliable-no-delay for the continuous value/state stream. Shares the pose channel
        // and peer; safe because every packet is type-tagged and Receive() dispatches on the first byte.
        public static bool Send(Il2CppStructArray<byte> buf, int len, bool reliable)
        {
            // Loopback maps both reliability classes to TCP (ordered + reliable) — fine for a local test.
            if (LoopbackTransport.Active) { if (LoopSend(buf, len)) { _sent++; return true; } return false; }
            if (!_inited || !HasPeer) return false;
            try
            {
                var mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
                if (SteamNetworking.SendP2PPacket(Peer, buf, (uint)len, mode, Channel)) { _sent++; return true; }
            }
            catch (Exception e) { Log.LogWarning("[p2p] send2: " + e.Message); }
            return false;
        }

        // Copy an Il2Cpp packet buffer into the managed scratch and hand it to the localhost link. Keeps the
        // subsystems' Il2Cpp send buffers untouched (they don't know about the transport).
        private static bool LoopSend(Il2CppStructArray<byte> buf, int len)
        {
            if (!LoopbackTransport.Connected) return false;
            if (_loopScratch == null || _loopScratch.Length < len) _loopScratch = new byte[Math.Max(1200, len)];
            for (int i = 0; i < len; i++) _loopScratch[i] = buf[i];
            return LoopbackTransport.Send(_loopScratch, len);
        }

        // Fake-remote injection for the F6 solo render test.
        public static void InjectRemote(Vector3 hp, Quaternion hr, bool hasHands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr,
                                        bool hasCurl = false, float lCurlIdx = 0f, float lCurlOth = 0f, float rCurlIdx = 0f, float rCurlOth = 0f)
        {
            HeadPos = hp; HeadRot = hr; HasHands = hasHands; LPos = lp; LRot = lr; RPos = rp; RRot = rr;
            HasCurl = hasHands && hasCurl;
            if (HasCurl) { LCurlIndex = lCurlIdx; LCurlOther = lCurlOth; RCurlIndex = rCurlIdx; RCurlOther = rCurlOth; }
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
                    if (CoopNetSim.Active)
                    {
                        int n = (int)read; var copy = new byte[n];
                        for (int i = 0; i < n; i++) copy[i] = _recvArr[i];
                        CoopNetSim.Ingest(copy, n);
                    }
                    else DispatchPacket(read);
                }
            }
            catch (Exception e) { Log.LogWarning("[p2p] recv: " + e.Message); }
        }

        // Drain the localhost test link into the SAME parse path as Steam: copy each length-framed packet into
        // the persistent Il2Cpp recv array, then dispatch identically. Only the wire differs from Receive().
        private static void ReceiveLoopback()
        {
            try
            {
                int guard = 0;
                while (guard++ < 128 && LoopbackTransport.TryReceive(out var frame))
                {
                    int read = frame.Length;
                    if (read < 1 || read > _recvArr.Length) continue;
                    if (CoopNetSim.Active)
                    {
                        var copy = new byte[read]; Array.Copy(frame, copy, read);
                        CoopNetSim.Ingest(copy, read);
                    }
                    else
                    {
                        for (int i = 0; i < read; i++) _recvArr[i] = frame[i];
                        DispatchPacket((uint)read);
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[p2p] recv-loop: " + e.Message); }
        }

        // NetSim release path (test aid): copy a delayed/managed packet back into the Il2Cpp recv array and run it
        // through the SAME parser. Only used while CoopNetSim is active; otherwise the receive paths dispatch direct.
        private static void DispatchManaged(byte[] data, int len)
        {
            if (data == null || len < 1 || len > _recvArr.Length) return;
            for (int i = 0; i < len; i++) _recvArr[i] = data[i];
            DispatchPacket((uint)len);
        }

        // Parse one packet already sitting in _recvArr (length = read). Transport-agnostic — shared by the
        // Steam and loopback receive paths. Pose packets populate the remote-avatar fields; every other type
        // byte fans out to the co-op subsystems via CoopControls.OnPacket (exactly as the Steam path always did).
        private static void DispatchPacket(uint read)
        {
            if (read < 1) return;

            // Non-pose packets are Phase 3+ subsystem traffic — dispatch by type byte and move on.
            if (_recvArr[0] != MSG_POSE)
            {
                try { CoopControls.OnPacket(_recvArr[0], _recvArr, (int)read); } catch (Exception ce) { Log.LogWarning("[ctrl] recv: " + ce.Message); }
                _recvd++;
                return;
            }
            if (read < 2) return;

            byte flag = _recvArr[1];
            if ((flag & ~(FLAG_HANDS | FLAG_CURL)) != 0) return;   // unknown flag bits
            bool hands = (flag & FLAG_HANDS) != 0;
            bool curl = (flag & FLAG_CURL) != 0;
            if (curl && !hands) return;                            // curl implies hands
            int need = curl ? HandCurlPacketLen : (hands ? HandPacketLen : HeadPacketLen);
            if (read < (uint)need) return;                         // truncated — no stale-byte parse

            // Parse into locals, validate, THEN publish — so a bad packet never leaves torn pose state.
            int o = 2;
            Vector3 hp = GetV(ref o); Quaternion hr = GetQ(ref o);
            Vector3 lp = Vector3.zero, rp = Vector3.zero; Quaternion lr = Quaternion.identity, rr = Quaternion.identity;
            if (hands) { lp = GetV(ref o); lr = GetQ(ref o); rp = GetV(ref o); rr = GetQ(ref o); }
            float lci = 0f, lco = 0f, rci = 0f, rco = 0f;
            if (curl) { lci = GetF(ref o); lco = GetF(ref o); rci = GetF(ref o); rco = GetF(ref o); }

            // NaN/Inf into a Unity transform poisons the hierarchy; a zero quaternion normalizes to NaN.
            if (!Finite(hp) || !FiniteRot(hr)) return;
            if (hands && (!Finite(lp) || !Finite(rp) || !FiniteRot(lr) || !FiniteRot(rr))) return;
            if (curl && (!Finite(lci) || !Finite(lco) || !Finite(rci) || !Finite(rco))) return;

            HeadPos = hp; HeadRot = hr; HasHands = hands;
            if (hands) { LPos = lp; LRot = lr; RPos = rp; RRot = rr; }
            HasCurl = curl;
            if (curl)
            {
                LCurlIndex = Mathf.Clamp01(lci); LCurlOther = Mathf.Clamp01(lco);
                RCurlIndex = Mathf.Clamp01(rci); RCurlOther = Mathf.Clamp01(rco);
            }
            RemoteValid = true; _remoteAge = 0f; _recvd++;
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
