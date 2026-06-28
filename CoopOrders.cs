using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op (increment 4d): replicate the TELEPRINTER "typing machine" ORDERS.
    ///
    /// ACTIVE even under the NARROW gate (Config.CoopOrdersSync = true). The narrow gate only suppresses the
    /// client's enemy SPAWN node — but tested 2026-06-20 the client's order text still came out EMPTY, because
    /// the gated <c>State_SpawnMapEntity</c> is what stashes the target in a graph context variable that the
    /// order node later resolves {grid}/{bearing} against. A client that skips the spawn has no target → blank
    /// order. So the resolved text MUST come from the host.
    ///
    /// CLIENT-LOCAL SUPPRESSION (2026-06-21): the client's own teleprinter node DOES run (we don't gate the node,
    /// to avoid stalling its <c>WaitUntilComplete</c>) — but its text is not just empty, it can be WRONG: the
    /// client independently adjudicates its own shell (<c>State_ImpactStart</c>) and, finding the target already
    /// host-destroyed, prints "MISS" over the host's replayed "HIT" (the tester's HIT→MISS flip). So a Harmony
    /// PREFIX (<see cref="SuppressLocalPrint"/>) blanks the lines of every client-local in-mission submit, making
    /// the HOST the sole source of real field-report text. The blank (not skipped) submit keeps a real, completing
    /// <c>PrintJob</c> flowing through the node so the graph never stalls. The replay path sets
    /// <see cref="ApplyingRemote"/> so the host's authoritative text passes through unblanked.
    ///
    /// Mission orders are emitted host-side by the SleepyNodes graph node <c>State_TeleprinterText</c>, which
    /// resolves {bearing}/{grid} tokens against live MapEntity/RNG state and calls <c>Teleprinter.SubmitLines</c>.
    /// We replicate the host's RESOLVED text (the client cannot re-resolve it — no target context, RNG-sensitive).
    ///
    /// CAPTURE: a Harmony POSTFIX on <c>Teleprinter.SubmitLines</c> (the single funnel for all teleprinter text)
    /// reads the returned <c>PrintJob</c> {sourceId, lines} + the printer's <c>TeleprinterType</c> and, on the
    /// HOST inside a mission, broadcasts them. REPLAY: the client calls <c>SubmitLines</c> + <c>TryStart</c> on
    /// its matching teleprinter, so the same order types out there too.
    ///
    /// Only the HOST broadcasts (the client's replay also hits the postfix but is a no-op there) → no loop.
    /// SCOPED to <c>GamePhase.MissionActive</c>: in the hub both instances run their own teleprinter logic
    /// locally, so syncing there would DOUBLE-print; in a mission the client's is gated, so the host is the
    /// only source. LIMITATION (noted): orders printed before a mid-mission joiner arrives are not back-filled
    /// (no JIP snapshot of teleprinter state) — a joiner sees orders from the point they join onward.
    /// </summary>
    internal static class CoopOrders
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_ORDER = 22;   // [t][printerType u8][sourceId str][lineCount i32][line str]×N  reliable

        private static Il2CppStructArray<byte> _buf;
        private static int _sent, _applied, _suppressed;

        // Set true around the client's REPLAY submit so the local-print suppressor (SuppressLocalPrint) lets the
        // host's authoritative text through instead of blanking it like the client's own graph submits.
        public static bool ApplyingRemote;

        // ---------------- host capture (Harmony postfix on Teleprinter.SubmitLines) ----------------

        // Patched in by CoopSim.ApplyPatches. Runs after every SubmitLines on every instance; only the host in a
        // mission actually broadcasts.
        public static void OnSubmitLines(Teleprinter __instance, PrintJob __result)
        {
            try
            {
                if (Config.PvpActive) return;   // PvP: teleprinters are per-player (recon is local) — never replicate/host-author
                if (!Config.CoopOrdersSync || !CoopP2P.IsHost) return;
                if (!SteamNet.InLobby || !CoopP2P.HasPeer) return;
                if (!InMission()) return;
                if (__instance == null || __result == null) return;

                byte ptype; string sourceId; Il2CppSystem.Collections.Generic.List<string> lines;
                try { ptype = (byte)__instance.TeleprinterType; sourceId = __result.sourceId; lines = __result.lines; }
                catch { return; }
                if (lines == null) return;

                Broadcast(ptype, sourceId, lines);
            }
            catch (Exception e) { Log.LogWarning("[ord] capture: " + e.Message); }
        }

        // Harmony PREFIX on Teleprinter.SubmitLines (registered by CoopSim). On a co-op CLIENT inside a mission,
        // BLANK the lines of its OWN locally-generated teleprinter text. The client runs the full mission graph
        // (narrow gate), so it independently adjudicates its own shell via State_ImpactStart and — finding the
        // target already host-destroyed — prints "MISS", which fought the host's authoritative "HIT" replayed via
        // the order path below (the tester's "field report said HIT then immediately MISS"). Now the host is the
        // ONLY source of real field-report text on the client. We replace the content with a single empty line
        // rather than skipping the call, so the State_TeleprinterText node still receives a real, completing
        // PrintJob and never stalls on its WaitUntilComplete (the queued blank job fires onJobCompleted normally).
        // The HOST (it authors) and the REPLAY path (ApplyingRemote) pass through untouched.
        public static void SuppressLocalPrint(ref Il2CppSystem.Collections.Generic.IEnumerable<string> lines)
        {
            try
            {
                if (ApplyingRemote) return;                                   // host's replayed text — keep it
                if (Config.PvpActive) return;                                 // PvP: each player prints their own recon/orders locally — NEVER blank
                if (!Config.CoopOrdersSync || CoopP2P.IsHost) return;         // feature off / host authors
                if (!SteamNet.InLobby || !CoopP2P.HasPeer || !InMission()) return;  // solo / hub: local is correct
                var blank = new Il2CppSystem.Collections.Generic.List<string>();
                blank.Add("");
                lines = blank.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();
                _suppressed++;
            }
            catch { }   // never break the teleprinter / mission graph
        }

        private static void Broadcast(byte ptype, string sourceId, Il2CppSystem.Collections.Generic.List<string> lines)
        {
            if (!EnsureBuf()) return;
            int count; try { count = lines.Count; } catch { return; }
            if (count > 64) count = 64;   // sanity cap

            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_ORDER); w.Byte(ptype);
            w.Str(sourceId, 200);
            int countAt = w.Mark();   // back-patched below with the count we actually serialize
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                string line = null; try { line = lines[i]; } catch { }
                line ??= "";
                // reserveTail:4 reproduces the old "o + 4 + n > _buf.Length - 4" capacity check exactly; TryStr is
                // transactional, so a line that won't fit stops the loop without dropping the whole packet.
                if (!w.TryStr(line, 240, 4)) break;   // out of room — send what fits
                written++;
            }
            w.Patch(countAt, written);   // record the actual line count we serialized
            if (w.Overflow) { Log.LogWarning("[ord] packet too large for " + _buf.Length + "B — not sent"); return; }

            CoopP2P.Send(_buf, w.Length, true);
            _sent++;
            // DIAG (teleprinter "client prints nothing" hunt): log the actual text we captured/broadcast, so a
            // tester log proves whether the source order has real content or is already blank at the host.
            DescribeLines(lines, out int dbgChars, out string dbg0);
            Log.LogInfo($"[ord] teleprinter {(Teleprinter.Teleprinters)ptype} '{sourceId}' ({written} lines, {dbgChars} chars, line0='{dbg0}') -> peer");
        }

        // ---------------- client replay ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_ORDER) return;
            if (Config.PvpActive) return; // PvP: no cross-player order replication (teleprinters are per-player)
            if (CoopP2P.IsHost) return;   // host authored it; never replays
            var r = new CoopWire.Reader(a, len, 1);
            byte ptype = r.Byte();
            string sourceId = r.Str(200);
            int count = r.Int();
            if (r.Bad || count < 0 || count > 64) return;

            var lines = new Il2CppSystem.Collections.Generic.List<string>();
            for (int i = 0; i < count; i++)
            {
                string s = r.Str(240);
                if (r.Bad) break;
                lines.Add(s);
            }

            try
            {
                var tp = FindTeleprinter(ptype);
                if (tp == null) { Log.LogWarning($"[ord] no {(Teleprinter.Teleprinters)ptype} teleprinter to replay '{sourceId}' (not in mission scene yet?)"); return; }
                // Mark this as the authoritative remote text so SuppressLocalPrint doesn't blank it.
                ApplyingRemote = true;
                try
                {
                    tp.SubmitLines(sourceId ?? "", lines.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>(), null, false);
                    tp.TryStart(false);
                }
                finally { ApplyingRemote = false; }
                _applied++;
                // DIAG (teleprinter "client prints nothing" hunt): log the applied text content + which teleprinter
                // received it (name + active-in-hierarchy), so the next test says empty-lines (serialize/capture) vs
                // real-text-but-not-rendering (wrong/inactive teleprinter or the print never started).
                DescribeLines(lines, out int dbgChars, out string dbg0);
                bool tpActive = false; string tpName = "?";
                try { tpName = tp.name; var go = tp.gameObject; tpActive = go != null && go.activeInHierarchy; } catch { }
                Log.LogInfo($"[ord] applied teleprinter {(Teleprinter.Teleprinters)ptype} '{sourceId}' ({lines.Count} lines, {dbgChars} chars, line0='{dbg0}') <- peer (tp='{tpName}' active={tpActive})");
            }
            catch (Exception e) { Log.LogWarning("[ord] replay: " + e.Message); }
        }

        private static Teleprinter FindTeleprinter(byte ptype)
        {
            try
            {
                Teleprinter.Teleprinters want = (Teleprinter.Teleprinters)ptype;
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Teleprinter>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var tp = arr[i].TryCast<Teleprinter>(); if (tp == null) continue;
                    try { if (tp.TeleprinterType == want) return tp; } catch { }
                }
                // Fallback: if only one teleprinter exists, use it regardless of type.
                if (arr != null && arr.Length == 1) return arr[0].TryCast<Teleprinter>();
            }
            catch { }
            return null;
        }

        // ---------------- diagnostics ----------------

        public static string Status() => $"orders: sent={_sent} applied={_applied} localSuppressed={_suppressed}";

        // ---------------- helpers ----------------

        private static bool InMission()
        {
            try { var mm = MissionManager.Instance; if (mm == null) return false; return mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(1200); return true; }
            catch (Exception e) { Log.LogWarning("[ord] buf: " + e.Message); return false; }
        }

        // DIAG helper: summarize a line list for the [ord] logs (total char count + a truncated preview of line 0),
        // so the broadcast + replay diagnostics print the same shape. Shared by both (teleprinter "prints nothing"
        // hunt). Remove with the two DIAG log blocks once that regression is resolved.
        private static void DescribeLines(Il2CppSystem.Collections.Generic.List<string> lines, out int chars, out string first)
        {
            chars = 0; first = "";
            try
            {
                int n = lines.Count;
                for (int i = 0; i < n; i++)
                {
                    string ln = null; try { ln = lines[i]; } catch { }
                    if (ln == null) continue;
                    chars += ln.Length;
                    if (i == 0) first = ln.Length > 48 ? ln.Substring(0, 48) : ln;
                }
            }
            catch { }
        }
    }
}
