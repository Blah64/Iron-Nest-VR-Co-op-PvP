using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Co-op transport: a host-relay STAR over Steam P2P. Every client holds a single session with the host;
    /// the host fans out (broadcast / unicast / relay) to all clients. Streams each player's head + hand poses
    /// plus subsystem traffic, distinguished by the first packet byte.
    ///
    /// ORIGIN MODEL: the host derives a packet's origin from the Steam `from` (never trusts a
    /// client-supplied field). On the host→client leg an 8-byte sender SteamID is appended as a TRAILER; the
    /// receiving client strips it and dispatches the inner packet at offset 0 with the reduced length, so every
    /// parser sees the exact byte offsets it did in the 2-player build. client→host packets carry no trailer.
    ///
    /// The lobby cap is Config.CoopMaxPlayers (currently 4). With a single peer the fan-out/relay collapses to one
    /// unicast (broadcast == "send to the peer", relay is a no-op); with more peers the host fans out to every peer
    /// and relays each client-authored packet to the OTHERS, stamped with the sender's host-derived origin.
    ///
    /// Poses are sent in WORLD space (both instances load the same scene at the same world coords). If the
    /// rotating cockpit (Barbet) makes avatars drift once turret aim is desynced, switch to Barbet-local later.
    /// </summary>
    internal static class CoopP2P
    {
        private static ManualLogSource Log => Plugin.Logger;

        private const int Channel = 0;
        private const byte MSG_POSE = 1;
        // Pose flag-byte bits: hands present, and (implies hands) finger-curl present.
        private const byte FLAG_HANDS = 1;
        private const byte FLAG_CURL = 2;
        // Exact wire sizes so truncated packets are rejected instead of parsing stale buffer bytes.
        private const int HeadPacketLen = 2 + 12 + 16;          // msg + flag + headPos(3f) + headRot(4f) = 30
        private const int HandPacketLen = 2 + (12 + 16) * 3;    // + left + right hand = 86
        private const int HandCurlPacketLen = HandPacketLen + 16; // + Lindex,Lother,Rindex,Rother (4f) = 102

        // Host→client origin trailer width. Appended AFTER the inner packet; the client reads it
        // from the last 8 bytes and dispatches the inner packet with the reduced length, so offsets never move.
        private const int OriginTrailerLen = 8;

        // Largest INNER packet any subsystem may put on the wire. The receive buffer is sized to
        // this + the host→client origin trailer, so a peer can always read the biggest packet a sender can build.
        // Every subsystem that allocates its own scratch buffer (e.g. CoopPunchcards._buf) caps itself to this and
        // refuses to send anything larger, rather than silently producing a packet the receiver would truncate.
        public const int MaxInnerPayload = 2048;

        private static bool _inited;
        private static ulong _myId;
        private static Callback<P2PSessionRequest_t> _cbSession;
        private static Il2CppStructArray<byte> _sendArr;   // pose serialization
        private static Il2CppStructArray<byte> _outArr;    // outbound staging (inner packet + origin trailer)
        private static Il2CppStructArray<byte> _recvArr;
        private static readonly byte[] _f4 = new byte[4];
        private static byte[] _loopScratch;                // managed copy of an outbound packet for the loopback wire
        private static bool _wasLoopback;                  // so we can clear peer state when loopback ends

        // Current co-op peers = every lobby member except me. Single source of truth for fan-out. Real SteamIDs
        // over Steam; one synthetic id over loopback. Maintained by UpdatePeer / TickLoopback.
        private static readonly List<CSteamID> _peers = new List<CSteamID>();
        private static readonly List<CSteamID> _scratch = new List<CSteamID>();
        private static CSteamID _hostId;   // lobby owner; the address a client sends to

        public static bool HasPeer => _peers.Count > 0;
        public static int PeerCount => _peers.Count;

        // Role: the lobby owner is the host. Used by ownership tie-break + sim-gating.
        public static bool IsHost;
        public static ulong MyId => _myId;
        public static ulong HostSteamId => _hostId.m_SteamID;   // lobby owner; used by the grab priority tie-break

        // Per-peer remote pose (world space), keyed by the authoring SteamID. RemoteAvatar pools one
        // rig per entry, so >2 avatars render. The F6 solo render test injects
        // under SelfTestId. Finger curl (0..1): index from the trigger, "other" from the grip; only set when the
        // peer streams it (FLAG_CURL).
        public struct RemotePose
        {
            public Vector3 HeadPos; public Quaternion HeadRot;
            public bool HasHands; public Vector3 LPos, RPos; public Quaternion LRot, RRot;
            public bool HasCurl; public float LCurlIndex, LCurlOther, RCurlIndex, RCurlOther;
            public string Name;   // persona name for the avatar's name tag
            public float Age;     // seconds since last update; > RemoteStaleSeconds ⇒ expired + removed
        }
        private static readonly Dictionary<ulong, RemotePose> _poses = new Dictionary<ulong, RemotePose>();
        private static readonly Dictionary<ulong, string> _peerNames = new Dictionary<ulong, string>();
        private static readonly List<ulong> _poseKeys = new List<ulong>();   // scratch for safe expiry iteration
        public const ulong SelfTestId = 0xFEEDFACEUL;   // synthetic origin for the F6 solo render test

        // Read-only view for RemoteAvatar to pool/iterate per peer. Do not mutate.
        public static IReadOnlyDictionary<ulong, RemotePose> RemotePoses => _poses;
        public static bool AnyRemote => _poses.Count > 0;

        public static Vector3 LastSentHead;   // diag: the head world-pos we last transmitted

        // The peer's Steam persona name (for the join toast + the avatar name tag); "" when no peer.
        public static string PeerName = "";

        // All OTHER players' SteamIDs (the live peer set; excludes me). Used by PvpTeams so the host can build the
        // full roster (me + peers). Copies into the caller's list to avoid exposing the internal list.
        public static void CopyPeerIds(System.Collections.Generic.List<ulong> dst)
        {
            dst.Clear();
            for (int i = 0; i < _peers.Count; i++) dst.Add(_peers[i].m_SteamID);
        }

        // A peer's Steam persona name (or "" if unknown). For self, callers use SteamFriends.GetPersonaName().
        public static string NameFor(ulong id) => _peerNames.TryGetValue(id, out var n) ? n : "";

        public static bool SelfTest;   // F6: mirror local pose as a fake remote, to verify avatar rendering solo

        private static int _sent, _recvd;
        private static float _nextSend;   // transmit-rate cap (see Config.CoopSendHz)

        // Join-in-progress: when the host detects a new peer it schedules a one-time full-world snapshot for
        // CoopSnapshotDelaySec later. The delay lets both sides resolve the peer + bring the session up so the
        // snapshot isn't dropped by the receive-side peer gate. Each pending snapshot is tracked PER PEER so two
        // joiners arriving within the delay window each get their own snapshot (a single pending target would be
        // overwritten by the second joiner, so the first would never receive the full-world snapshot).
        private struct PendingSnap { public CSteamID Peer; public float DueAt; }
        private static readonly List<PendingSnap> _pendingSnaps = new List<PendingSnap>();
        private static CSteamID _snapTarget;   // the peer the IN-FLIGHT snapshot is currently being unicast to
        private static bool _snapActive;       // while true, host Send unicasts to _snapTarget (JIP), not broadcast

        // Queue (or refresh) a join-in-progress snapshot for one peer. De-dupes a re-detected peer.
        private static void ScheduleSnapshot(CSteamID peer, float now)
        {
            for (int i = _pendingSnaps.Count - 1; i >= 0; i--)
                if (_pendingSnaps[i].Peer.m_SteamID == peer.m_SteamID) _pendingSnaps.RemoveAt(i);
            _pendingSnaps.Add(new PendingSnap { Peer = peer, DueAt = now + Config.CoopSnapshotDelaySec });
        }

        // Drop any pending snapshot for a peer that left before its snapshot fired.
        private static void DropSnapshot(ulong peerId)
        {
            for (int i = _pendingSnaps.Count - 1; i >= 0; i--)
                if (_pendingSnaps[i].Peer.m_SteamID == peerId) _pendingSnaps.RemoveAt(i);
        }

        // Fire every pending snapshot whose delay has elapsed (host only). Each targets its own joiner.
        private static void ProcessPendingSnaps(float now)
        {
            for (int i = _pendingSnaps.Count - 1; i >= 0; i--)
            {
                if (now < _pendingSnaps[i].DueAt) continue;
                var peer = _pendingSnaps[i].Peer;
                _pendingSnaps.RemoveAt(i);
                if (!IsHost || !ContainsId(_peers, peer.m_SteamID)) continue;   // peer left before the snapshot fired
                _snapTarget = peer;
                SendJoinSnapshot();
            }
        }

        public static void Init()
        {
            if (_inited) return;
            try
            {
                _myId = SteamUser.GetSteamID().m_SteamID;
                _sendArr = new Il2CppStructArray<byte>(128);
                _outArr = new Il2CppStructArray<byte>(MaxInnerPayload + OriginTrailerLen);
                _recvArr = new Il2CppStructArray<byte>(MaxInnerPayload + OriginTrailerLen);
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
                // current lobby member.
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
            // LoopbackTransport). Mutually exclusive with the Steam path.
            if (LoopbackTransport.Active) { _wasLoopback = true; TickLoopback(dt); return; }
            if (_wasLoopback)   // loopback was just stopped — drop the synthetic peer so subsystems clear
            {
                _wasLoopback = false;
                ClearPeers(); IsHost = false; PeerName = ""; _pendingSnaps.Clear();
                ClearRealPoses();
                Log.LogInfo("[p2p] loopback ended — peers cleared");
            }

            if (!SteamNet.Ready) return;
            Init();
            UpdatePeer();
            Receive();
            CoopNetSim.Pump(Time.unscaledTime, DispatchManaged);   // release any NetSim-delayed packets (test aid)
            ProcessPendingSnaps(Time.unscaledTime);
            AgeRemotePoses(dt);
        }

        // Loopback (same-machine test) counterpart of the Steam Init+UpdatePeer+Receive trio. Derives role from
        // the TCP link, gives the two sides synthetic deterministic ids (host=1, client=2) so host-priority
        // ownership works without real SteamIDs, then drains the link through the SAME deliver path the Steam
        // receive uses. A single synthetic peer is kept in _peers; loopback is single-peer.
        private static void TickLoopback(float dt)
        {
            EnsureArrays();
            IsHost = LoopbackTransport.IsHost;
            _myId = IsHost ? 1UL : 2UL;
            _hostId = new CSteamID { m_SteamID = 1UL };
            ulong peerSyn = IsHost ? 2UL : 1UL;
            bool connected = LoopbackTransport.Connected;

            if (connected && _peers.Count == 0)
            {
                _peers.Add(new CSteamID { m_SteamID = peerSyn });
                PeerName = IsHost ? "Loopback client" : "Loopback host";
                _peerNames[peerSyn] = PeerName;
                Log.LogInfo($"[p2p] loopback link up — role={(IsHost ? "HOST" : "client")}");
                Notify.PeerJoined(PeerName, IsHost);
                if (IsHost) { ScheduleSnapshot(new CSteamID { m_SteamID = peerSyn }, Time.unscaledTime); Log.LogInfo("[p2p] host: loopback join snapshot scheduled"); }
                else _pendingSnaps.Clear();
            }
            else if (!connected && _peers.Count > 0)
            {
                _peers.Clear(); _peerNames.Clear(); Notify.PeerLeft(PeerName); PeerName = ""; _pendingSnaps.Clear();
                ClearRealPoses();
                Log.LogInfo("[p2p] loopback link down");
            }

            ReceiveLoopback();
            CoopNetSim.Pump(Time.unscaledTime, DispatchManaged);   // release any NetSim-delayed packets (test aid)
            ProcessPendingSnaps(Time.unscaledTime);
            AgeRemotePoses(dt);
        }

        // Allocate the persistent Il2Cpp packet buffers (+ the managed loopback scratch). Init() does this for
        // the Steam path; loopback skips Init() entirely (no SteamAPI), so it allocates here instead.
        private static void EnsureArrays()
        {
            // Same sizing policy as Init(): the loopback test transport must read/stage packets as
            // large as the Steam path can, or same-machine testing would reject packets real Steam would accept.
            int wire = MaxInnerPayload + OriginTrailerLen;
            if (_sendArr == null) _sendArr = new Il2CppStructArray<byte>(128);
            if (_outArr == null) _outArr = new Il2CppStructArray<byte>(wire);
            if (_recvArr == null) _recvArr = new Il2CppStructArray<byte>(wire);
            if (_loopScratch == null) _loopScratch = new byte[wire];
        }

        // Size the outbound staging buffer to the fixed wire max. With the Send-side max-payload guard no caller can
        // hand us more than MaxInnerPayload + trailer, so this never grows unbounded.
        private static void EnsureOut(int need)
        {
            int max = MaxInnerPayload + OriginTrailerLen;
            if (_outArr == null || _outArr.Length < max) _outArr = new Il2CppStructArray<byte>(max);
        }

        private static void ClearPeers()
        {
            if (!LoopbackTransport.Active)
                foreach (var p in _peers) CloseSession(p);
            _peers.Clear();
            _peerNames.Clear();
        }

        // Age every remote pose by dt and drop the ones that haven't refreshed within RemoteStaleSeconds (lost
        // peer / link). The self-test pose persists while SelfTest is on (re-injected each frame). Keys are copied
        // out first so we can Remove during the pass without invalidating the dictionary enumerator.
        private static void AgeRemotePoses(float dt)
        {
            if (_poses.Count == 0) return;
            _poseKeys.Clear();
            foreach (var k in _poses.Keys) _poseKeys.Add(k);
            for (int i = 0; i < _poseKeys.Count; i++)
            {
                ulong k = _poseKeys[i];
                if (k == SelfTestId && SelfTest) continue;
                var p = _poses[k];
                p.Age += dt;
                if (p.Age > Config.RemoteStaleSeconds) { _poses.Remove(k); Log.LogInfo($"[p2p] remote pose stale ({p.Age:F1}s) — hiding avatar {k}"); }
                else _poses[k] = p;
            }
        }

        // Drop all real remote poses (lobby left / peer gone / loopback ended). Keeps the self-test entry so F6
        // mirroring survives a teardown — matching the old "if (!SelfTest) RemoteValid = false" behavior.
        private static void ClearRealPoses()
        {
            if (_poses.Count == 0) return;
            _poseKeys.Clear();
            foreach (var k in _poses.Keys) _poseKeys.Add(k);
            for (int i = 0; i < _poseKeys.Count; i++) if (_poseKeys[i] != SelfTestId) _poses.Remove(_poseKeys[i]);
        }

        // One-line status for the co-op diagnostics hub.
        public static string Status()
        {
            Camera cam = null; try { cam = Camera.main; } catch { }
            string myCam = cam != null ? V(cam.transform.position) : "n/a";
            return $"net: {(SteamNet.InLobby ? "in-lobby" : "no-lobby")} peers={_peers.Count} " +
                   $"role={(IsHost ? "HOST" : "client")} sent={_sent} recvd={_recvd} | avatars={_poses.Count} " +
                   $"myCam={myCam} mySent={V(LastSentHead)}";
        }

        // Rebuild _peers from the lobby roster (everyone except me), opening/closing sessions and firing
        // join/leave toasts on the delta. Host = lobby owner; a client sends to _hostId.
        private static void UpdatePeer()
        {
            if (!SteamNet.InLobby)
            {
                if (_peers.Count > 0) { Notify.PeerLeft(PeerName); ClearPeers(); PeerName = ""; Log.LogInfo("[p2p] not in lobby — peers cleared"); }
                IsHost = false;
                _pendingSnaps.Clear();
                ClearRealPoses();
                return;
            }
            try
            {
                var lobby = SteamNet.CurrentLobby;
                try { _hostId = SteamMatchmaking.GetLobbyOwner(lobby); IsHost = _hostId.m_SteamID == _myId; } catch { }

                // Snapshot the current roster (minus me).
                _scratch.Clear();
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (int i = 0; i < n; i++)
                {
                    var m = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                    if (m.m_SteamID != _myId) _scratch.Add(m);
                }

                // Additions: members in the roster we don't have yet.
                for (int i = 0; i < _scratch.Count; i++)
                {
                    var m = _scratch[i];
                    if (ContainsId(_peers, m.m_SteamID)) continue;
                    _peers.Add(m);
                    string nm = ""; try { nm = SteamFriends.GetFriendPersonaName(m); } catch { }
                    PeerName = nm;
                    _peerNames[m.m_SteamID] = nm;
                    _poses.Remove(m.m_SteamID);   // don't carry a stale avatar onto a (re)joining peer
                    Log.LogInfo($"[p2p] peer + {m.m_SteamID} ('{nm}')  (now {_peers.Count} peer(s))");
                    Notify.PeerJoined(nm, IsHost);
                    // Host pushes a full-world snapshot to the joiner once the session settles — one per joiner.
                    if (IsHost) { ScheduleSnapshot(m, Time.unscaledTime); Log.LogInfo($"[p2p] host: join-in-progress snapshot scheduled in {Config.CoopSnapshotDelaySec:0.0}s"); }
                }

                // Removals: peers no longer in the roster.
                for (int i = _peers.Count - 1; i >= 0; i--)
                {
                    if (ContainsId(_scratch, _peers[i].m_SteamID)) continue;
                    var gone = _peers[i];
                    CloseSession(gone);
                    _peers.RemoveAt(i);
                    _poses.Remove(gone.m_SteamID);
                    _peerNames.Remove(gone.m_SteamID);
                    DropSnapshot(gone.m_SteamID);   // peer left before its pending snapshot fired — discard it
                    Log.LogInfo($"[p2p] peer - {gone.m_SteamID} left  (now {_peers.Count} peer(s))");
                    Notify.PeerLeft(PeerName);
                }

                if (_peers.Count == 0) { _pendingSnaps.Clear(); ClearRealPoses(); }
            }
            catch (Exception e) { Log.LogWarning("[p2p] peer: " + e.Message); }
        }

        private static bool ContainsId(List<CSteamID> list, ulong id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].m_SteamID == id) return true;
            return false;
        }

        // Host-only: push a one-time authoritative snapshot of the whole shared world to a freshly-joined peer.
        // Each subsystem re-sends its current state; the joiner applies it idempotently.
        private static void SendJoinSnapshot()
        {
            if (_peers.Count == 0 || !IsHost) return;
            Log.LogInfo($"[p2p] host: sending join-in-progress snapshot to {_snapTarget.m_SteamID}");
            // Redirect every subsystem snapshot send to the JOINER only — a late-join catch-up must not replay
            // onto the existing clients and snap their turret/clipboard/mission state. The
            // subsystems still call CoopP2P.Send; while _snapActive the host unicasts it to _snapTarget.
            _snapActive = true;
            try
            {
                // Scene/load command FIRST so the joiner starts traversing toward the host's phase. The cockpit-
                // dependent snapshots below (controls/map/punchcards) can't apply until the joiner has loaded the
                // mission scene, so they're ALSO re-sent on the joiner's MISSION_READY ack (CoopScene)
                // (map/punchcard layout arriving before the joiner has the scene objects is dropped).
                try { CoopScene.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot scene: " + e.Message); }   // mission-load command first
                try { CoopControls.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot ctrl: " + e.Message); }
                try { CoopClipboard.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot clip: " + e.Message); }
                try { CoopMap.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot map: " + e.Message); }
                try { CoopEntities.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot ent: " + e.Message); }
                try { CoopPunchcards.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot punch: " + e.Message); }
                try { CoopScore.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot score: " + e.Message); }
                try { CoopPressure.SendSnapshot(); } catch (Exception e) { Log.LogWarning("[p2p] snapshot valve: " + e.Message); }   // re-sent on MISSION_READY too (valves are scene objects)
            }
            finally { _snapActive = false; }
        }

        // Host-only: run a subsystem snapshot send targeted to ONE peer instead of broadcast, by reusing the JIP
        // unicast latch. A MISSION_READY resync must reach only the joiner that asked, not re-burst every existing
        // client. Re-entrancy-safe via save/restore.
        public static void SendSnapshotTo(ulong peer, Action sendAction)
        {
            if (!IsHost || sendAction == null) return;
            var prevTarget = _snapTarget; bool prevActive = _snapActive;
            _snapTarget = new CSteamID { m_SteamID = peer };
            _snapActive = true;
            try { sendAction(); }
            catch (Exception e) { Log.LogWarning("[p2p] targeted snapshot: " + e.Message); }
            finally { _snapActive = prevActive; _snapTarget = prevTarget; }
        }

        public static void SendPose(Vector3 hp, Quaternion hr, bool hasHands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr,
                                    bool hasCurl = false, float lCurlIdx = 0f, float lCurlOth = 0f, float rCurlIdx = 0f, float rCurlOth = 0f)
        {
            LastSentHead = hp;   // record even if we don't send, so the diag shows what we WOULD transmit
            if (!HasPeer) return;
            if (!LoopbackTransport.Active && !_inited) return;   // Steam path needs Init(); loopback doesn't
            // Rate cap: skip this frame's send if we're ahead of the target Hz.
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
                Send(_sendArr, o, false);   // unreliable-no-delay; host appends the origin trailer per-peer
            }
            catch (Exception e) { Log.LogWarning("[p2p] send: " + e.Message); }
        }

        // Generic outbound for every channel. Host → broadcast to all peers (each with an origin trailer of our
        // own id); client → send to the host (no trailer; the host derives our origin from the Steam `from`).
        // Reliable for state-transition events; unreliable-no-delay for the continuous value/state/pose stream.
        public static bool Send(Il2CppStructArray<byte> buf, int len, bool reliable)
        {
            if (LoopbackTransport.Active)
            {
                if (_peers.Count == 0) return false;
                bool okl = IsHost ? RawSendTrailer(default, buf, len, reliable, _myId) : RawSendPlain(default, buf, len, reliable);
                if (okl) _sent++;
                return okl;
            }
            if (!_inited || _peers.Count == 0) return false;
            try
            {
                if (IsHost)
                {
                    if (_snapActive) { bool oks = RawSendTrailer(_snapTarget, buf, len, reliable, _myId); if (oks) _sent++; return oks; }   // JIP: unicast to the joiner
                    bool any = false;
                    foreach (var p in _peers) any |= RawSendTrailer(p, buf, len, reliable, _myId);
                    if (any) _sent++;
                    return any;
                }
                bool ok = RawSendPlain(_hostId, buf, len, reliable);
                if (ok) _sent++;
                return ok;
            }
            catch (Exception e) { Log.LogWarning("[p2p] send2: " + e.Message); }
            return false;
        }

        // Host-only targeted unicast (join-in-progress snapshots to a single joiner). Carries our origin trailer.
        public static bool SendTo(CSteamID peer, Il2CppStructArray<byte> buf, int len, bool reliable)
        {
            if (!IsHost) return false;
            if (LoopbackTransport.Active) { bool okl = RawSendTrailer(default, buf, len, reliable, _myId); if (okl) _sent++; return okl; }
            if (!_inited) return false;
            bool ok = RawSendTrailer(peer, buf, len, reliable, _myId); if (ok) _sent++; return ok;
        }

        // Host relay: re-broadcast a client-authored packet (sitting in _recvArr[0..len]) to every OTHER client,
        // stamped with the original sender's id. Fired from Deliver (= at NetSim release), never at raw receive,
        // so the host's local apply and its relay are ordered from the same event. The high-rate streams (pose / dial value / turret
        // group / map drag / entity move) relay UNRELIABLE so they don't head-of-line-block; everything else
        // (grab/release/click/fire/snapshots/edits) relays reliable.
        private static void RelayInner(ulong origin, int innerLen)
        {
            byte type = _recvArr[0];
            bool reliable;
            if (type == MSG_POSE) reliable = false;
            // Mixed live/final types carry their own reliability in a flag byte at payload index 9: relay the
            // final/release edge reliably, the live stream unreliably (type-only classification
            // downgraded the reliable final and a dropped final left the piece/dial stuck off on other clients).
            else if (CoopControls.IsMixedFinalStream(type)) reliable = innerLen > 9 && (_recvArr[9] & 1) != 0;
            else reliable = !CoopControls.IsUnreliableStream(type);
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i].m_SteamID == origin) continue;
                RawSendTrailer(_peers[i], _recvArr, innerLen, reliable, origin);
            }
        }

        // Transport-boundary enforcement of the shared max-payload invariant. EVERY send funnels
        // through RawSendTrailer or RawSendPlain, so guarding both makes it IMPOSSIBLE for any subsystem to put an
        // inner packet larger than MaxInnerPayload (or empty) on the wire — the receiver's _recvArr is sized to
        // exactly that + the trailer. Rejected sends log a throttled warning carrying the packet type and length.
        private static float _nextOversizeLog;
        private static bool ValidOut(Il2CppStructArray<byte> src, int len)
        {
            if (len >= 1 && len <= MaxInnerPayload) return true;
            float now = Time.unscaledTime;
            if (now >= _nextOversizeLog)
            {
                _nextOversizeLog = now + 1f;
                byte type = (src != null && len >= 1 && src.Length > 0) ? src[0] : (byte)0;
                Log.LogWarning($"[p2p] refusing out-of-bounds packet (type={type} len={len}, max={MaxInnerPayload}) — sender bug");
            }
            return false;
        }

        // Stage [inner | origin(8)] into _outArr and transmit (host→client leg).
        private static bool RawSendTrailer(CSteamID dest, Il2CppStructArray<byte> src, int len, bool reliable, ulong origin)
        {
            if (!ValidOut(src, len)) return false;
            EnsureOut(len + OriginTrailerLen);
            for (int i = 0; i < len; i++) _outArr[i] = src[i];
            PutU64(_outArr, len, origin);
            return WireSend(dest, _outArr, len + OriginTrailerLen, reliable);
        }

        // Transmit the buffer verbatim (client→host leg — no trailer).
        private static bool RawSendPlain(CSteamID dest, Il2CppStructArray<byte> src, int len, bool reliable)
        {
            if (!ValidOut(src, len)) return false;
            return WireSend(dest, src, len, reliable);
        }

        private static bool WireSend(CSteamID dest, Il2CppStructArray<byte> buf, int len, bool reliable)
        {
            if (LoopbackTransport.Active) return LoopSend(buf, len);   // single stream; dest implicit
            var mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            return SteamNetworking.SendP2PPacket(dest, buf, (uint)len, mode, Channel);
        }

        // Copy an Il2Cpp packet buffer into the managed scratch and hand it to the localhost link.
        private static bool LoopSend(Il2CppStructArray<byte> buf, int len)
        {
            if (!LoopbackTransport.Connected) return false;
            if (_loopScratch == null || _loopScratch.Length < len) _loopScratch = new byte[Math.Max(MaxInnerPayload + OriginTrailerLen, len)];
            for (int i = 0; i < len; i++) _loopScratch[i] = buf[i];
            return LoopbackTransport.Send(_loopScratch, len);
        }

        // Fake-remote injection for the F6 solo render test (writes a self-test pose entry directly; no wire).
        public static void InjectRemote(Vector3 hp, Quaternion hr, bool hasHands, Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr,
                                        bool hasCurl = false, float lCurlIdx = 0f, float lCurlOth = 0f, float rCurlIdx = 0f, float rCurlOth = 0f)
        {
            bool curl = hasHands && hasCurl;
            _poses[SelfTestId] = new RemotePose
            {
                HeadPos = hp, HeadRot = hr, HasHands = hasHands,
                LPos = lp, LRot = lr, RPos = rp, RRot = rr,
                HasCurl = curl,
                LCurlIndex = curl ? lCurlIdx : 0f, LCurlOther = curl ? lCurlOth : 0f,
                RCurlIndex = curl ? rCurlIdx : 0f, RCurlOther = curl ? rCurlOth : 0f,
                Name = "(self-test)", Age = 0f,
            };
        }

        private static void Receive()
        {
            try
            {
                int guard = 0;
                while (SteamNetworking.IsP2PPacketAvailable(out _, Channel) && guard++ < 128)
                {
                    if (!SteamNetworking.ReadP2PPacket(_recvArr, (uint)_recvArr.Length, out uint read, out CSteamID from, Channel)) break;
                    // Only current lobby members may drive our state — drop anything else (spoof / stale peer).
                    if (!IsCurrentLobbyMember(from)) continue;
                    if (CoopNetSim.Active)
                    {
                        int nb = (int)read; var copy = new byte[nb];
                        for (int i = 0; i < nb; i++) copy[i] = _recvArr[i];
                        CoopNetSim.Ingest(copy, nb, from.m_SteamID);   // thread the REAL sender origin (correct at >2)
                    }
                    else DeliverFromWire((int)read, from.m_SteamID);
                }
            }
            catch (Exception e) { Log.LogWarning("[p2p] recv: " + e.Message); }
        }

        // Drain the localhost test link into the SAME deliver path as Steam.
        private static void ReceiveLoopback()
        {
            try
            {
                int guard = 0;
                while (guard++ < 256 && LoopbackTransport.TryReceive(out var frame))
                {
                    int read = frame.Length;
                    if (read < 1 || read > _recvArr.Length) continue;
                    if (CoopNetSim.Active)
                    {
                        var copy = new byte[read]; Array.Copy(frame, copy, read);
                        CoopNetSim.Ingest(copy, read, PrimaryRemoteOrigin());   // loopback is single-peer by construction
                    }
                    else
                    {
                        for (int i = 0; i < read; i++) _recvArr[i] = frame[i];
                        DeliverFromWire(read, PrimaryRemoteOrigin());
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[p2p] recv-loop: " + e.Message); }
        }

        // NetSim release path (test aid): copy a delayed managed packet back into _recvArr and run it through the
        // SAME deliver path. The packet's REAL origin (captured at ingest from the Steam `from`) is threaded through
        // CoopNetSim, so a delayed/reordered packet is attributed to its true sender even with more than one peer.
        private static void DispatchManaged(byte[] data, int len, ulong origin)
        {
            if (data == null || len < 1 || len > _recvArr.Length) return;
            for (int i = 0; i < len; i++) _recvArr[i] = data[i];
            DeliverFromWire(len, origin);
        }

        // The single remote peer's id (host: the one client; client: the host). Used only by the same-machine
        // loopback test link, which is single-peer by construction; the real Steam + NetSim paths thread the
        // true per-sender origin, so this is no longer on any multi-peer attribution path.
        private static ulong PrimaryRemoteOrigin()
            => IsHost ? (_peers.Count > 0 ? _peers[0].m_SteamID : 0UL) : _hostId.m_SteamID;

        // Deterministic grab-conflict winner shared by every per-control / per-token owner (CoopControls, CoopMap):
        // the host beats any client; among the same class the lower SteamID wins. All machines compute the same
        // result from (id, host id), so simultaneous grabs converge with no host grant/deny round-trip. Reduces to
        // "host keeps, client yields" at the 2-player cap.
        public static bool GrabBeats(ulong a, ulong b)
        {
            if (a == b) return false;
            ulong host = _hostId.m_SteamID;
            bool aHost = a == host, bHost = b == host;
            if (aHost != bHost) return aHost;
            return a < b;
        }

        // Resolve a raw wire packet now sitting in _recvArr: derive origin + inner length, then Deliver. Host
        // ingress (client→host) has no trailer and origin = the Steam `from` (hostSideOrigin). Client ingress
        // (host→client) carries an 8-byte origin trailer we strip; hostSideOrigin is unused there.
        private static void DeliverFromWire(int read, ulong hostSideOrigin)
        {
            ulong origin; int innerLen;
            if (IsHost) { origin = hostSideOrigin; innerLen = read; }
            else
            {
                // In the star topology only the host sends host→client packets (carrying the origin trailer). Drop
                // anything from another lobby member so a buggy/malicious peer can't inject a host-formatted packet
                // with a forged origin trailer. Loopback/NetSim pass _hostId here, so they pass.
                if (hostSideOrigin != _hostId.m_SteamID) return;
                if (read < OriginTrailerLen) return;       // malformed — must carry the origin trailer
                innerLen = read - OriginTrailerLen;
                origin = GetU64(_recvArr, innerLen);
            }
            Deliver(origin, innerLen);
        }

        // Apply one inner packet (already at _recvArr offset 0, length innerLen). On the host, relay client-
        // authored types to the other clients FIRST (so relay + local apply fire from the same released event),
        // then dispatch locally. Clients never relay.
        private static void Deliver(ulong origin, int innerLen)
        {
            if (innerLen < 1) return;
            byte type = _recvArr[0];
            if (IsHost && (type == MSG_POSE || CoopControls.IsClientAuthored(type))) RelayInner(origin, innerLen);
            DispatchPacket(origin, (uint)innerLen);
        }

        // Parse one inner packet in _recvArr (length = read). Pose populates the per-origin remote-avatar block;
        // every other type byte fans out via CoopControls.OnPacket.
        private static void DispatchPacket(ulong origin, uint read)
        {
            if (read < 1) return;

            if (_recvArr[0] != MSG_POSE)
            {
                try { CoopControls.OnPacket(_recvArr[0], origin, _recvArr, (int)read); } catch (Exception ce) { Log.LogWarning("[ctrl] recv: " + ce.Message); }
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

            string nm = _peerNames.TryGetValue(origin, out var pn) ? pn : "";
            _poses[origin] = new RemotePose
            {
                HeadPos = hp, HeadRot = hr, HasHands = hands,
                LPos = hands ? lp : Vector3.zero, LRot = hands ? lr : Quaternion.identity,
                RPos = hands ? rp : Vector3.zero, RRot = hands ? rr : Quaternion.identity,
                HasCurl = curl,
                LCurlIndex = curl ? Mathf.Clamp01(lci) : 0f, LCurlOther = curl ? Mathf.Clamp01(lco) : 0f,
                RCurlIndex = curl ? Mathf.Clamp01(rci) : 0f, RCurlOther = curl ? Mathf.Clamp01(rco) : 0f,
                Name = nm, Age = 0f,
            };
            _recvd++;
        }

        // --- (de)serialization: write into a send array, read from the recv array ---
        private static int PutF(int o, float f) { int __b = BitConverter.SingleToInt32Bits(f); _sendArr[o] = (byte)__b; _sendArr[o + 1] = (byte)(__b >> 8); _sendArr[o + 2] = (byte)(__b >> 16); _sendArr[o + 3] = (byte)(__b >> 24); return o + 4; }
        private static int PutV(int o, Vector3 v) { o = PutF(o, v.x); o = PutF(o, v.y); o = PutF(o, v.z); return o; }
        private static int PutQ(int o, Quaternion q) { o = PutF(o, q.x); o = PutF(o, q.y); o = PutF(o, q.z); o = PutF(o, q.w); return o; }

        private static void PutU64(Il2CppStructArray<byte> a, int o, ulong v) { for (int i = 0; i < 8; i++) a[o + i] = (byte)(v >> (8 * i)); }
        private static ulong GetU64(Il2CppStructArray<byte> a, int o) { ulong v = 0; for (int i = 0; i < 8; i++) v |= ((ulong)a[o + i]) << (8 * i); return v; }

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
