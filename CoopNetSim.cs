using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Co-op NETWORK SIMULATOR (TEST AID, off by default). Sits at the INGRESS chokepoint: CoopP2P hands every
    /// received packet here instead of dispatching it directly, and a per-frame Pump releases packets after a
    /// simulated delay (or never, if dropped). This is the only realistic way to stress the netcode without two
    /// distant machines, because the legacy Steam P2P API ignores Steam's built-in fake-lag knobs and same-machine
    /// loopback never touches a shapeable socket.
    ///
    /// Models: base LATENCY + uniform JITTER; LOSS, DUPLICATION and REORDER (all UNRELIABLE-only — Steam would
    /// retransmit a lost reliable packet, so dropping one in the sim is unrealistic; reliable packets are delayed
    /// but never dropped); a BANDWIDTH cap (serializes packets over a virtual link → bufferbloat under load); and a
    /// periodic LINK-DROP that blacks out all ingress for a window (exercises reconnect/resync). Shapes ONE side's
    /// ingress (Config.CoopNetSimSide) so the link is lopsided like a real one, and seeds a deterministic RNG so a
    /// failing run reproduces.
    ///
    /// Reliability per packet is inferred from the type byte (the protocol's reliable/unreliable split is fixed).
    /// MSG_PIECE_MOVE rides BOTH classes, so we read its final/live flag to classify the individual packet.
    /// </summary>
    internal static class CoopNetSim
    {
        private static ManualLogSource Log => Plugin.Logger;

        private struct Pkt { public byte[] Data; public int Len; public float ReleaseAt; public uint Seq; }
        private static readonly List<Pkt> _q = new List<Pkt>();
        private static System.Random _rng;
        private static int _rngSeed = int.MinValue;
        private static uint _seqCtr;          // tie-break for stable ordering among equal ReleaseAt
        private static float _wireFreeAt;     // bandwidth: when the virtual link is next free
        private static float _nextDropAt;     // session-drop schedule
        private static float _dropUntil;      // ingress-blackout end
        private static int _dropped, _duped, _delivered;
        private static float _lastNow;

        public static bool Active => Config.CoopNetSim && CoopP2P.HasPeer && SideApplies();

        private static bool SideApplies()
        {
            switch (Config.CoopNetSimSide)
            {
                case 1: return CoopP2P.IsHost;     // shape the host's ingress only
                case 2: return !CoopP2P.IsHost;    // shape the client's ingress only (default)
                default: return true;              // both
            }
        }

        private static System.Random Rng()
        {
            if (_rng == null || _rngSeed != Config.CoopNetSimSeed)
            {
                _rngSeed = Config.CoopNetSimSeed;
                _rng = new System.Random(_rngSeed == 0 ? 1 : _rngSeed);
            }
            return _rng;
        }

        // Called from CoopP2P's receive paths for each incoming packet (a private managed copy we now own).
        public static void Ingest(byte[] data, int len)
        {
            if (data == null || len < 1) return;
            float now = Time.unscaledTime;
            _lastNow = now;
            var rng = Rng();

            // Link-drop blackout: the link is "down", drop everything inbound until it comes back.
            if (now < _dropUntil) { _dropped++; return; }

            bool reliable = IsReliable(data, len);

            // Loss / dup / reorder apply to UNRELIABLE packets only.
            if (!reliable && Roll(rng, Config.CoopNetSimLossPct)) { _dropped++; return; }

            float baseDelay = Mathf.Max(0, Config.CoopNetSimLatencyMs) / 1000f;
            float jitter = Mathf.Max(0, Config.CoopNetSimJitterMs) / 1000f;
            float delay = baseDelay + (jitter > 0f ? (float)rng.NextDouble() * jitter : 0f);

            // Reorder (unreliable only): occasionally add a big extra delay so this packet falls behind later ones.
            if (!reliable && Roll(rng, Config.CoopNetSimReorderPct))
                delay += baseDelay + (float)rng.NextDouble() * Mathf.Max(0.02f, baseDelay);

            float release = now + delay;

            // Bandwidth: serialize over a virtual link of N bytes/sec (queueing causes bufferbloat under load —
            // reliable packets queue, they don't drop).
            int bps = Config.CoopNetSimBandwidthBps;
            if (bps > 0)
            {
                float serial = (float)len / bps;
                float wireAt = Mathf.Max(now, _wireFreeAt) + serial;
                _wireFreeAt = wireAt;
                if (wireAt > release) release = wireAt;
            }

            Enqueue(data, len, release);

            // Dup (unreliable only): a second copy a hair later, to exercise echo-suppression / idempotency.
            if (!reliable && Roll(rng, Config.CoopNetSimDupPct))
            {
                var copy = new byte[len]; Array.Copy(data, copy, len);
                Enqueue(copy, len, release + (float)rng.NextDouble() * 0.01f);
                _duped++;
            }
        }

        private static void Enqueue(byte[] data, int len, float release)
        {
            _q.Add(new Pkt { Data = data, Len = len, ReleaseAt = release, Seq = _seqCtr++ });
        }

        // Release all due packets (ReleaseAt, then arrival order) into the dispatcher. Called every frame by CoopP2P.
        public static void Pump(float now, Action<byte[], int> dispatch)
        {
            _lastNow = now;
            MaybeScheduleDrop(now);
            if (_q.Count == 0) return;

            List<Pkt> due = null;
            for (int i = 0; i < _q.Count; i++)
                if (_q[i].ReleaseAt <= now) (due ??= new List<Pkt>()).Add(_q[i]);
            if (due == null) return;

            _q.RemoveAll(p => p.ReleaseAt <= now);
            due.Sort((x, y) => x.ReleaseAt != y.ReleaseAt ? x.ReleaseAt.CompareTo(y.ReleaseAt) : x.Seq.CompareTo(y.Seq));

            for (int i = 0; i < due.Count; i++)
            {
                try { dispatch(due[i].Data, due[i].Len); _delivered++; }
                catch (Exception e) { Log.LogWarning("[netsim] dispatch: " + e.Message); }
            }
        }

        private static void MaybeScheduleDrop(float now)
        {
            float every = Config.CoopNetSimDropEverySec;
            if (every <= 0f) { _dropUntil = 0f; _nextDropAt = 0f; return; }
            if (_nextDropAt <= 0f) { _nextDropAt = now + every; return; }
            if (now >= _nextDropAt && now >= _dropUntil)
            {
                _dropUntil = now + Mathf.Max(0.1f, Config.CoopNetSimDropForSec);
                _nextDropAt = _dropUntil + every;
                int inflight = _q.Count; _q.Clear(); _dropped += inflight;   // in-flight packets are lost when the link drops
                Log.LogWarning($"[netsim] simulated link drop for {Config.CoopNetSimDropForSec:0.0}s (ingress blackout; {inflight} in-flight lost)");
            }
        }

        private static bool Roll(System.Random rng, float pct)
        {
            if (pct <= 0f) return false;
            if (pct >= 100f) return true;
            return rng.NextDouble() * 100.0 < pct;
        }

        // Which packet types are sent RELIABLE (must not be dropped/reordered — Steam delivers them). The few
        // unreliable streams are listed explicitly; everything else defaults to reliable. MSG_PIECE_MOVE (24) rides
        // both classes, so we read its final/live flag byte. Type bytes mirror the per-subsystem MSG_* constants.
        private static bool IsReliable(byte[] d, int len)
        {
            switch (d[0])
            {
                case 1:   // CoopP2P MSG_POSE
                case 4:   // CoopControls MSG_VALUE
                case 5:   // CoopControls MSG_GROUP
                case 11:  // CoopMap MSG_POS
                case 23:  // CoopEntities MSG_MOVE
                    return false;
                case 24:  // CoopMap MSG_PIECE_MOVE — flags byte at offset 9: final(&1)=reliable, live=unreliable
                    return len < 10 || (d[9] & 1) != 0;
                default:
                    return true;
            }
        }

        public static string Status()
        {
            if (!Config.CoopNetSim) return "netsim: off";
            return $"netsim: on side={Config.CoopNetSimSide} lat={Config.CoopNetSimLatencyMs}±{Config.CoopNetSimJitterMs}ms " +
                   $"loss={Config.CoopNetSimLossPct}% dup={Config.CoopNetSimDupPct}% reorder={Config.CoopNetSimReorderPct}% bw={Config.CoopNetSimBandwidthBps}B/s | " +
                   $"q={_q.Count} delivered={_delivered} dropped={_dropped} duped={_duped}" + (_lastNow < _dropUntil ? " [LINK DOWN]" : "");
        }
    }
}
