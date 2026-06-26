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

        // Per-field consecutive-divergence counters (reset when a field is back in tolerance) + an "already logged
        // this episode" flag so we warn on the crossing and note the recovery, not every single digest.
        private static int _dRot, _dElevL, _dElevR, _dPowder, _dEnt, _dMarker;
        private static bool _flRot, _flElevL, _flElevR, _flPowder, _flEnt, _flMarker;

        public static void Tick(float dt)
        {
            if (!Config.CoopDesyncDetect) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { ResetState(); return; }
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

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_DIGEST || !Config.CoopDesyncDetect) return;
            try
            {
                var r = new CoopWire.Reader(a, len, 1);
                byte flags = r.Byte();
                bool remHasT = (flags & FLAG_HAS_TURRET) != 0;
                bool remBusy = (flags & FLAG_BUSY) != 0;
                float rRot = r.Float(), rEL = r.Float(), rER = r.Float();
                int rPL = r.Int(), rPR = r.Int();
                int rEnt = r.Int(), rMarker = r.Int();
                if (r.Bad) return;

                // Our own current digest to compare against the peer's.
                bool locHasT = CoopControls.TryGetTurretDigest(out float lRot, out float lEL, out float lER, out int lPL, out int lPR);
                bool locBusy = false; try { locBusy = CoopControls.AnyLocalOwnership; } catch { }
                int lEnt = SafeEntityCount(), lMarker = SafeMarkerCount();

                float tol = Mathf.Max(0.25f, Config.CoopDesyncAngleTolDeg);
                bool compareTurret = remHasT && locHasT && !remBusy && !locBusy;   // skip while anyone is slewing

                if (compareTurret)
                {
                    Check(ref _dRot, ref _flRot, Mathf.Abs(Mathf.DeltaAngle(lRot, rRot)) > tol, "turret rotation", lRot, rRot);
                    Check(ref _dElevL, ref _flElevL, Mathf.Abs(lEL - rEL) > tol, "gun-L elevation", lEL, rEL);
                    Check(ref _dElevR, ref _flElevR, Mathf.Abs(lER - rER) > tol, "gun-R elevation", lER, rER);
                    Check(ref _dPowder, ref _flPowder, lPL != rPL || lPR != rPR, "powder", lPL + lPR, rPL + rPR);
                }
                Check(ref _dEnt, ref _flEnt, lEnt != rEnt, "entity count", lEnt, rEnt);
                Check(ref _dMarker, ref _flMarker, lMarker != rMarker, "marker count", lMarker, rMarker);
            }
            catch (Exception e) { Log.LogWarning("[diag] recv digest: " + e.Message); }
        }

        // Track one field: bump its divergence counter while out of tolerance; warn once it persists (and again
        // every CoopDesyncPersist digests so a stuck desync keeps a heartbeat), and note recovery. local/remote
        // are only for the log line.
        private static void Check(ref int counter, ref bool flagged, bool divergent, string label, float local, float remote)
        {
            int persist = Mathf.Max(1, Config.CoopDesyncPersist);
            if (divergent)
            {
                counter++;
                if (counter >= persist && (!flagged || counter % persist == 0))
                {
                    flagged = true;
                    float secs = counter * Mathf.Max(0.25f, Config.CoopDesyncIntervalSec);
                    Log.LogWarning($"[diag] DESYNC {label}: local={local:0.0} peer={remote:0.0} (persisted {counter} digests ~{secs:0.0}s)");
                }
            }
            else
            {
                if (flagged) Log.LogInfo($"[diag] {label} re-converged (local={local:0.0} peer={remote:0.0})");
                counter = 0; flagged = false;
            }
        }

        public static string Status()
        {
            int open = (_flRot ? 1 : 0) + (_flElevL ? 1 : 0) + (_flElevR ? 1 : 0) + (_flPowder ? 1 : 0) + (_flEnt ? 1 : 0) + (_flMarker ? 1 : 0);
            return $"desync-detector: {(Config.CoopDesyncDetect ? "on" : "off")} open-divergences={open}";
        }

        private static int SafeEntityCount() { try { return CoopEntities.LocalEntityCount; } catch { return 0; } }
        private static int SafeMarkerCount() { try { return CoopMap.MarkerCount; } catch { return 0; } }

        private static void ResetState()
        {
            _dRot = _dElevL = _dElevR = _dPowder = _dEnt = _dMarker = 0;
            _flRot = _flElevL = _flElevR = _flPowder = _flEnt = _flMarker = false;
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
