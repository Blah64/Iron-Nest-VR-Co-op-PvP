using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Co-op DESYNC DETECTOR (diagnostic only — never changes gameplay). Same-machine loopback hides the silent
    /// state divergence that real-WAN latency/loss cause; this surfaces it. Each side, ~CoopDesyncIntervalSec,
    /// sends a quantized DIGEST of state both machines should agree on (turret CurrentAngle + gun elevations +
    /// powder, plus mirrored entity and marker COUNTS). On receiving the peer's digest we compare it to our OWN
    /// current digest and, when a field stays out of tolerance for CoopDesyncPersist consecutive digests, log a
    /// warning (and a note when it recovers). Persistence-gating ignores the transient divergence that's normal
    /// while the turret slews or a packet is in flight — only a STUCK desync trips it.
    ///
    /// Turret-angle fields are skipped while EITHER side is actively operating a control (the digest carries a
    /// "busy" bit): a turret mid-slew legitimately reads different on the two machines across a laggy link. Counts
    /// are always compared — a persistent entity/marker-count mismatch is a real lost-spawn / lost-line. The digest
    /// rides the same Steam P2P channel (type byte MSG_DIGEST), reliable, ~1/s. Gate with Config.CoopDesyncDetect.
    /// Host-as-reference model: only clients send digests; the host compares each client's digest against its own
    /// state, keyed per origin (steam id), so 3+ player lobbies get per-client divergence tracking.
    /// </summary>
    internal static class CoopNetDiag
    {
        private static ManualLogSource Log => Plugin.Logger;

        // [t][flags u8][rot f][elevL f][elevR f][powderL i32][powderR i32][entCount i32][markerCount i32]  reliable
        public const byte MSG_DIGEST = 26;
        private const byte FLAG_HAS_TURRET = 1;
        private const byte FLAG_BUSY = 2;

        private static Il2CppStructArray<byte> _buf;
        private static float _nextSend;

        // Per-origin consecutive-divergence counters (keyed by sender steam id). Each Counters instance tracks one
        // client's per-field divergence counters + "already logged this episode" flags.
        private sealed class Counters { public int dRot, dElevL, dElevR, dPowder, dEnt, dMarker; public bool flRot, flElevL, flElevR, flPowder, flEnt, flMarker; }
        private static readonly System.Collections.Generic.Dictionary<ulong, Counters> _byOrigin = new System.Collections.Generic.Dictionary<ulong, Counters>();

        public static void Tick(float dt)
        {
            if (!Config.CoopDesyncDetect) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { ResetState(); return; }
            if (CoopP2P.IsHost) return;   // host-as-reference: clients send, host compares on receive
            float now = Time.unscaledTime;
            if (now < _nextSend) return;
            _nextSend = now + Mathf.Max(0.25f, Config.CoopDesyncIntervalSec);
            SendDigest();
        }

        private static void SendDigest()
        {
            if (!EnsureBuf()) return;
            try
            {
                bool hasT = CoopControls.TryGetTurretDigest(out float rot, out float eL, out float eR, out int pL, out int pR);
                bool busy = false; try { busy = CoopControls.AnyLocalOwnership; } catch { }
                byte flags = 0;
                if (hasT) flags |= FLAG_HAS_TURRET;
                if (busy) flags |= FLAG_BUSY;

                var w = new CoopWire.Writer(_buf);
                w.Byte(MSG_DIGEST); w.Byte(flags);
                w.Float(hasT ? rot : 0f);
                w.Float(hasT ? eL : 0f);
                w.Float(hasT ? eR : 0f);
                w.Int(hasT ? pL : 0);
                w.Int(hasT ? pR : 0);
                w.Int(SafeEntityCount());
                w.Int(SafeMarkerCount());
                if (w.Overflow) { Log.LogWarning("[netdiag] packet too large - not sent"); return; }
                CoopP2P.Send(_buf, w.Length, true);
            }
            catch (Exception e) { Log.LogWarning("[diag] send digest: " + e.Message); }
        }

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_DIGEST || !Config.CoopDesyncDetect) return;
            if (!CoopP2P.IsHost) return;   // only the host (reference) compares
            try
            {
                if (!_byOrigin.TryGetValue(origin, out var c)) { c = new Counters(); _byOrigin[origin] = c; }

                var r = new CoopWire.Reader(a, len, 1);
                byte flags = r.Byte();
                bool remHasT = (flags & FLAG_HAS_TURRET) != 0;
                bool remBusy = (flags & FLAG_BUSY) != 0;
                float rRot = r.Float(), rEL = r.Float(), rER = r.Float();
                int rPL = r.Int(), rPR = r.Int();
                int rEnt = r.Int(), rMarker = r.Int();
                if (r.Bad) return;

                // Host's own current digest is the reference to compare each client against.
                bool locHasT = CoopControls.TryGetTurretDigest(out float lRot, out float lEL, out float lER, out int lPL, out int lPR);
                bool locBusy = false; try { locBusy = CoopControls.AnyLocalOwnership; } catch { }
                int lEnt = SafeEntityCount(), lMarker = SafeMarkerCount();

                float tol = Mathf.Max(0.25f, Config.CoopDesyncAngleTolDeg);
                bool compareTurret = remHasT && locHasT && !remBusy && !locBusy;   // skip while anyone is slewing

                if (compareTurret)
                {
                    Check(ref c.dRot, ref c.flRot, Mathf.Abs(Mathf.DeltaAngle(lRot, rRot)) > tol, "turret rotation", lRot, rRot, origin);
                    Check(ref c.dElevL, ref c.flElevL, Mathf.Abs(lEL - rEL) > tol, "gun-L elevation", lEL, rEL, origin);
                    Check(ref c.dElevR, ref c.flElevR, Mathf.Abs(lER - rER) > tol, "gun-R elevation", lER, rER, origin);
                    Check(ref c.dPowder, ref c.flPowder, lPL != rPL || lPR != rPR, "powder", lPL + lPR, rPL + rPR, origin);
                }
                Check(ref c.dEnt, ref c.flEnt, lEnt != rEnt, "entity count", lEnt, rEnt, origin);
                Check(ref c.dMarker, ref c.flMarker, lMarker != rMarker, "marker count", lMarker, rMarker, origin);
            }
            catch (Exception e) { Log.LogWarning("[diag] recv digest: " + e.Message); }
        }

        // Track one field: bump its divergence counter while out of tolerance; warn once it persists (and again
        // every CoopDesyncPersist digests so a stuck desync keeps a heartbeat), and note recovery. local/remote
        // are only for the log line. origin identifies which client diverged (for 3+ player host logs).
        private static void Check(ref int counter, ref bool flagged, bool divergent, string label, float local, float remote, ulong origin)
        {
            int persist = Mathf.Max(1, Config.CoopDesyncPersist);
            if (divergent)
            {
                counter++;
                if (counter >= persist && (!flagged || counter % persist == 0))
                {
                    flagged = true;
                    float secs = counter * Mathf.Max(0.25f, Config.CoopDesyncIntervalSec);
                    Log.LogWarning($"[diag] DESYNC {label} (client {origin}): local={local:0.0} peer={remote:0.0} (persisted {counter} digests ~{secs:0.0}s)");
                }
            }
            else
            {
                if (flagged) Log.LogInfo($"[diag] {label} re-converged (client {origin}, local={local:0.0} peer={remote:0.0})");
                counter = 0; flagged = false;
            }
        }

        public static string Status()
        {
            int open = 0;
            foreach (var c in _byOrigin.Values)
                open += (c.flRot ? 1 : 0) + (c.flElevL ? 1 : 0) + (c.flElevR ? 1 : 0) + (c.flPowder ? 1 : 0) + (c.flEnt ? 1 : 0) + (c.flMarker ? 1 : 0);
            return $"desync-detector: {(Config.CoopDesyncDetect ? "on" : "off")} clients={_byOrigin.Count} open-divergences={open}";
        }

        private static int SafeEntityCount() { try { return CoopEntities.LocalEntityCount; } catch { return 0; } }
        private static int SafeMarkerCount() { try { return CoopMap.MarkerCount; } catch { return 0; } }

        private static void ResetState()
        {
            _byOrigin.Clear();
            _nextSend = 0f;
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(64); return true; }
            catch (Exception e) { Log.LogWarning("[diag] buf: " + e.Message); return false; }
        }

    }
}
