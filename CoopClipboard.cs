using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 3 co-op: replicate the HUD clipboard CONTENTS between the two lobby members. All displayed text
    /// lives in <c>NotepadSection</c> components keyed by a designer-assigned <c>UnityTag</c> (stable across
    /// both machines on the same build); the loggers (markers/odometer/espresso) and any mission-graph
    /// briefing text all just call <c>NotepadSection.Write(...)</c>, so syncing the section TEXT captures
    /// everything regardless of what produced it.
    ///
    /// STRATEGY = state-based diff (NOT event replay): each side watches every tagged section's current text
    /// and, when it changes, broadcasts the full text; the peer applies it with Write(Replace, Instant). This
    /// is robust to dropped packets and join-in-progress (the next diff re-sends the truth), and avoids having
    /// to reproduce each logger's local instrument snapshot on the remote machine. The active clipboard tool
    /// (ClipboardToolSelector) is synced the same way, by slot index.
    ///
    /// The clipboard's PHYSICAL pose (raised/focused/hidden) is deliberately NOT synced — each player has
    /// their own head-locked board, so pose is per-player ergonomics.
    ///
    /// Rides the same Steam P2P channel as poses/controls; packets are type-tagged (8/9) and routed here from
    /// <see cref="CoopControls.OnPacket"/>. Section/tool updates go reliable (a missed one would leave the
    /// peer's clipboard stale until the next local edit).
    /// </summary>
    internal static class CoopClipboard
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_SECTION = 8;   // [t][tagLen i32][tag utf8][textLen i32][text utf8]  reliable
        public const byte MSG_TOOL = 9;      // [t][index i32]                                      reliable

        private const int MaxText = 1000;    // wire cap per section (notepads are short; guards the buffer)
        private const float ScanHz = 5f;     // content changes are infrequent — diff a few times a second

        private static Il2CppStructArray<byte> _buf;     // send (lazily created on the Unity thread)
        private static readonly byte[] _scratch = new byte[1400];
        private static readonly byte[] _i4 = new byte[4];

        private static readonly List<NotepadSection> _sections = new List<NotepadSection>();
        private static ClipboardToolSelector _selector;
        private static float _nextScan, _nextRebuild;

        // Last text we sent/applied per tag — diffing against this detects local edits; updating it on apply
        // stops our own diff from bouncing a remote change straight back.
        private static readonly Dictionary<string, string> _last = new Dictionary<string, string>();
        // Brief per-tag suppression right after applying a remote write, in case Write normalises the string.
        private static readonly Dictionary<string, float> _echoUntil = new Dictionary<string, float>();
        private static int _lastTool = -1;

        public static void Tick(float dt)
        {
            if (!Config.CoopClipboardSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { if (_last.Count > 0) { _last.Clear(); _echoUntil.Clear(); _lastTool = -1; } return; }

            float now = Time.unscaledTime;
            if (now < _nextScan) return;
            _nextScan = now + 1f / ScanHz;

            try
            {
                Rebuild(now);

                foreach (var s in _sections)
                {
                    if (s == null) continue;
                    string tag; string text;
                    try { tag = s.UnityTag; } catch { continue; }
                    if (string.IsNullOrEmpty(tag)) continue;
                    try { var tt = s.TargetText; text = tt != null ? tt.text : null; } catch { continue; }
                    text ??= "";

                    if (_echoUntil.TryGetValue(tag, out var u) && now < u) { _last[tag] = text; continue; }
                    if (_last.TryGetValue(tag, out var prev) && prev == text) continue;

                    _last[tag] = text;
                    SendSection(tag, text);
                    Log.LogInfo($"[clip] section '{tag}' ({text.Length} chars) -> peer");
                }

                int tool = CurrentToolIndex();
                if (tool != _lastTool) { _lastTool = tool; if (tool >= 0) SendTool(tool); }
            }
            catch (Exception e) { Log.LogWarning("[clip] tick: " + e.Message); }
        }

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
        {
            float now = Time.unscaledTime;
            int o = 1;
            switch (type)
            {
                case MSG_SECTION:
                {
                    string tag = GetStr(a, ref o, len);
                    string text = GetStr(a, ref o, len);
                    if (tag == null) return;
                    try
                    {
                        var s = NotepadSection.ResolveByTag(tag);
                        if (s != null)
                        {
                            s.Write(text ?? "", NotepadSection.WriteMode.Replace, NotepadSection.AddPosition.Bottom,
                                    0f, NotepadSection.TextRevealMode.Instant, 0f);
                            string applied; try { applied = s.TargetText != null ? s.TargetText.text : text; } catch { applied = text; }
                            _last[tag] = applied ?? "";
                            _echoUntil[tag] = now + 0.4f;
                        }
                    }
                    catch (Exception e) { Log.LogWarning("[clip] apply section: " + e.Message); }
                    break;
                }
                case MSG_TOOL:
                {
                    if (len < 5) return;
                    int idx = GetInt(a, ref o);
                    try
                    {
                        var sel = _selector;
                        if (sel != null && idx >= 0)
                        {
                            var slots = sel.slots;
                            if (slots != null && idx < slots.Count) { sel.SelectTool(slots[idx]); _lastTool = idx; }
                        }
                    }
                    catch (Exception e) { Log.LogWarning("[clip] apply tool: " + e.Message); }
                    break;
                }
            }
        }

        // ---------------- registry ----------------

        private static void Rebuild(float now)
        {
            if (_sections.Count > 0 && now < _nextRebuild) return;
            _nextRebuild = now + 3f;
            _sections.Clear();
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<NotepadSection>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++) { var s = arr[i].TryCast<NotepadSection>(); if (s != null) _sections.Add(s); }
            }
            catch { }
            try
            {
                var sel = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ClipboardToolSelector>(), FindObjectsSortMode.None);
                _selector = (sel != null && sel.Length > 0) ? sel[0].TryCast<ClipboardToolSelector>() : null;
            }
            catch { _selector = null; }
        }

        private static int CurrentToolIndex()
        {
            try
            {
                var sel = _selector; if (sel == null) return -1;
                var cur = sel.CurrentSelected; if (cur == null) return -1;
                var slots = sel.slots; if (slots == null) return -1;
                for (int i = 0; i < slots.Count; i++) if ((object)slots[i] == cur) return i;
            }
            catch { }
            return -1;
        }

        // ---------------- send ----------------

        private static void SendSection(string tag, string text)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_SECTION;
            o = PutStr(o, tag); o = PutStr(o, text);
            CoopP2P.Send(_buf, o, true);
        }

        private static void SendTool(int idx)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_TOOL; o = PutInt(o, idx);
            CoopP2P.Send(_buf, o, true);
        }

        // ---------------- (de)serialization ----------------

        private static int PutInt(int o, int v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }

        private static int PutStr(int o, string s)
        {
            s ??= "";
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = bytes.Length; if (n > MaxText) n = MaxText;   // truncate over-long notes (rare)
            o = PutInt(o, n);
            for (int i = 0; i < n; i++) _buf[o + i] = bytes[i];
            return o + n;
        }

        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _i4[0] = a[o]; _i4[1] = a[o + 1]; _i4[2] = a[o + 2]; _i4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_i4, 0); }

        private static string GetStr(Il2CppStructArray<byte> a, ref int o, int len)
        {
            if (o + 4 > len) return null;
            int n = GetInt(a, ref o);
            if (n < 0 || o + n > len || n > _scratch.Length) return null;
            for (int i = 0; i < n; i++) _scratch[i] = a[o + i];
            o += n;
            return Encoding.UTF8.GetString(_scratch, 0, n);
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(1200); return true; }
            catch (Exception e) { Log.LogWarning("[clip] buf: " + e.Message); return false; }
        }
    }
}
