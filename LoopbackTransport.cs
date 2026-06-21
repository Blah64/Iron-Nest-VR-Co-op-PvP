using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BepInEx.Logging;

namespace IronNestVR
{
    /// <summary>
    /// TEST-ONLY transport: a localhost TCP link between two game instances on the SAME machine, so co-op can
    /// be exercised without a second Steam account / PC (the recurring 2-player test blocker). It plugs in
    /// behind the <see cref="CoopP2P"/> Send/Receive seam — every packet (pose, control, click/fire, clipboard,
    /// map, entity, scene, orders) flows through here byte-for-byte identically to the Steam P2P path; only the
    /// wire swaps. Steam P2P stays the real shipping transport; this never opens a socket unless the operator
    /// explicitly presses a key, and the whole thing is gated behind <see cref="Config.CoopLoopback"/>.
    ///
    /// One side HOSTS (TcpListener) and is the co-op host/authority (mirrors the lobby owner); the other JOINS
    /// (TcpClient). Because TCP is a byte stream (not message-framed like SendP2PPacket), each packet is sent
    /// length-prefixed (u16 little-endian + payload). A background thread reads frames into a concurrent queue
    /// that <see cref="CoopP2P"/> drains on the Unity main thread (Unity objects must only be touched there).
    /// Reliable and unreliable both map to TCP (ordered + reliable) — fine for a local test, where a dropped
    /// "grab" can't happen anyway.
    ///
    /// Keys (Ctrl held, so they can't collide with the bare-F-key debug binds — F6 self-test, F7 lobby panel,
    /// F8 VR recenter, F9–F12 Steam lobby): **Ctrl+F2 CONNECT (auto host-or-join) — press in BOTH windows**,
    /// Ctrl+F3 stop. Auto-connect tries to grab the port first (→ host); if it's already taken by the other
    /// instance, it joins instead — so the same key in both windows can't be mis-paired. (Ctrl+F1 is avoided:
    /// some keyboards send Help/media on F1 via Fn-lock.) NOTE: the new Input System only delivers keys to the
    /// FOCUSED window — click a window before pressing. The link auto-reconnects on a drop (role kept for the
    /// session); for the unfocused instance to keep ticking, runInBackground is forced on (see Plugin.Load).
    /// </summary>
    internal static class LoopbackTransport
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Started (hosting or joining). While Active, CoopP2P routes through this transport instead of Steam.
        public static bool Active { get; private set; }
        // The two instances are actually linked (TCP handshake done). Drives CoopP2P.HasPeer / SteamNet.InLobby.
        public static bool Connected { get; private set; }
        // This instance hosts → it is the co-op HOST (authoritative), same role the Steam lobby owner gets.
        public static bool IsHost { get; private set; }

        // Local-test convenience: STICKY free-cursor toggle. When true (Ctrl+F5), the OS cursor is held
        // visible + unlocked so you can click between the two windows; mouselook is off while it's on. Defaults
        // OFF so the camera works normally — for a momentary cursor (e.g. to reach the other window) hold Left
        // Alt instead (see VrManager.LateUpdate). Honored only while CoopLoopback is on AND a link is Active, so
        // normal play / shipped builds are untouched.
        public static bool FreeCursor = false;

        private const int MaxFrame = 8192;   // sanity cap on a length-prefixed frame (largest real packet ~1.2KB)

        private static TcpListener _listener;
        private static TcpClient _client;
        private static NetworkStream _stream;
        private static Thread _thread;
        private static volatile bool _stop;
        private static readonly object _writeLock = new object();
        private static readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();

        // ---------------- lifecycle ----------------

        // Auto-role: try to OWN the port (→ host); if it's already bound by the other instance, JOIN it. The
        // same key in both windows always pairs correctly — whoever wins the bind hosts, the other joins. The
        // role is decided ONCE and kept for the session; the link auto-reconnects on a drop (see AutoLoop).
        public static void StartConnect(int port)
        {
            if (Active) { Log.LogWarning("[loop] already active — Ctrl+F3 to stop first"); return; }
            Active = true; _stop = false;
            _thread = new Thread(() => AutoLoop(port)) { IsBackground = true, Name = "INVR-loop" };
            _thread.Start();
            Log.LogInfo($"[loop] CONNECT on 127.0.0.1:{port} — deciding role (host if free, else join). Press Ctrl+F2 in the OTHER window too.");
        }

        public static void Stop()
        {
            if (!Active) return;
            _stop = true;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            _stream = null; _client = null; _listener = null;
            Active = false; Connected = false;
            while (_inbox.TryDequeue(out _)) { }
            Log.LogInfo("[loop] stopped");
        }

        // ---------------- background socket thread ----------------

        // Decide the role once (bind the port → host, else client), then keep a link up for the whole session:
        // on a drop (scene-load hitch, transient socket error, the peer briefly gone) the host re-accepts and
        // the client re-connects, so sync RESUMES instead of dying permanently. The host keeps its listener for
        // the session; TickLoopback re-fires the join-in-progress snapshot on each reconnect so state re-syncs.
        private static void AutoLoop(int port)
        {
            try
            {
                try { _listener = new TcpListener(IPAddress.Loopback, port); _listener.Start(); }
                catch { _listener = null; }   // port already taken by the other instance → we're the client
                IsHost = _listener != null;
                Log.LogInfo(IsHost
                    ? $"[loop] auto: port free → HOSTING on 127.0.0.1:{port} — waiting for the other instance…"
                    : $"[loop] auto: port busy → JOINING 127.0.0.1:{port}…");

                while (!_stop)
                {
                    _client = AcquireConnection(port);   // blocks until linked (or stopped)
                    if (_stop || _client == null) break;
                    Setup(_client);
                    ReadLoop();                          // returns when the link drops
                    Connected = false;
                    try { _stream?.Close(); } catch { }
                    try { _client?.Close(); } catch { }
                    _stream = null; _client = null;
                    if (!_stop) Log.LogInfo("[loop] link dropped — re-establishing…");
                }
                try { _listener?.Stop(); } catch { }
            }
            catch (Exception e) { if (!_stop) Log.LogError("[loop] auto: " + e.Message); }
        }

        // Host: poll-accept the next joiner. Client: connect, retrying until the host is up. Either blocks until
        // a connection exists (or Stop() flips _stop). Returns null if stopped before connecting.
        private static TcpClient AcquireConnection(int port)
        {
            if (IsHost)
            {
                while (!_stop)
                {
                    if (_listener.Pending()) { try { return _listener.AcceptTcpClient(); } catch { return null; } }
                    Thread.Sleep(50);
                }
                return null;
            }
            while (!_stop)
            {
                try { var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return c; }
                catch { Thread.Sleep(300); }   // host not up yet — retry
            }
            return null;
        }

        private static void Setup(TcpClient c)
        {
            try { c.NoDelay = true; } catch { }
            _stream = c.GetStream();
            Connected = true;
            Log.LogInfo($"[loop] CONNECTED ({(IsHost ? "host" : "client")}) — co-op link up");
        }

        private static void ReadLoop()
        {
            var hdr = new byte[2];
            while (!_stop)
            {
                if (!ReadFull(hdr, 2)) break;
                int len = hdr[0] | (hdr[1] << 8);
                if (len <= 0 || len > MaxFrame) break;   // garbage framing — bail rather than desync the stream
                var payload = new byte[len];
                if (!ReadFull(payload, len)) break;
                _inbox.Enqueue(payload);
            }
            Connected = false;
        }

        private static bool ReadFull(byte[] buf, int len)
        {
            var s = _stream;
            if (s == null) return false;
            int got = 0;
            try
            {
                while (got < len)
                {
                    int n = s.Read(buf, got, len - got);
                    if (n <= 0) return false;   // peer closed
                    got += n;
                }
                return true;
            }
            catch { return false; }
        }

        // ---------------- main-thread I/O surface (called by CoopP2P) ----------------

        // Send one length-prefixed frame. Called on the Unity main thread (all CoopP2P sends are).
        public static bool Send(byte[] data, int len)
        {
            var s = _stream;
            if (s == null || !Connected) return false;
            try
            {
                lock (_writeLock)
                {
                    s.WriteByte((byte)(len & 0xFF));
                    s.WriteByte((byte)((len >> 8) & 0xFF));
                    s.Write(data, 0, len);
                }
                return true;
            }
            catch (Exception e) { Log.LogWarning("[loop] send: " + e.Message); Connected = false; return false; }
        }

        // Dequeue the next received frame (managed bytes); CoopP2P copies it into its Il2Cpp recv array.
        public static bool TryReceive(out byte[] frame) => _inbox.TryDequeue(out frame);

        // ---------------- keys ----------------

        private static bool _loggedLive;
        public static void PollKeys()
        {
            if (!Config.CoopLoopback) return;
            UnityEngine.InputSystem.Keyboard kb;
            try { kb = UnityEngine.InputSystem.Keyboard.current; } catch { return; }
            if (kb == null) return;
            if (!_loggedLive) { _loggedLive = true; Log.LogInfo("[loop] key handler live (Ctrl+F2 connect · Ctrl+F3 stop · hold Alt or Ctrl+F5 = test cursor)"); }
            bool ctrl;
            try { ctrl = kb[UnityEngine.InputSystem.Key.LeftCtrl].isPressed || kb[UnityEngine.InputSystem.Key.RightCtrl].isPressed; }
            catch { return; }
            if (!ctrl) return;
            // Diagnostic: confirm the chord is seen even if the action then no-ops, so a silent log means the
            // key truly isn't arriving (laptop Fn-lock, wrong window focus) rather than a logic bug.
            if (Pressed(kb, UnityEngine.InputSystem.Key.F2)) { Log.LogInfo("[loop] Ctrl+F2 detected"); StartConnect(Config.CoopLoopbackPort); }   // auto: press in BOTH windows
            if (Pressed(kb, UnityEngine.InputSystem.Key.F3)) { Log.LogInfo("[loop] Ctrl+F3 detected"); Stop(); }
            if (Pressed(kb, UnityEngine.InputSystem.Key.F5)) { FreeCursor = !FreeCursor; Log.LogInfo("[loop] test cursor (sticky) " + (FreeCursor ? "ON — cursor visible, mouselook off (click between windows)" : "OFF — mouselook on, cursor hidden; hold Alt for a momentary cursor")); }
        }

        private static bool Pressed(UnityEngine.InputSystem.Keyboard kb, UnityEngine.InputSystem.Key k)
        { try { return kb[k].wasPressedThisFrame; } catch { return false; } }
    }
}
