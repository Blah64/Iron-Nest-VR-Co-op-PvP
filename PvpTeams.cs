using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP TEAMS — team membership + roster for team PvP (up to 4 players, 2 teams ⇒ 2v2). A team IS a vehicle:
    /// teammates share the co-op stack (control sync, avatars, map) and a single turret; opponents are isolated and
    /// appear only as a map mirror to shell. This module owns ONLY the membership model; the team-scoped replication
    /// gate (Phase B) and per-team vehicle/combat (Phase C) consume <see cref="IsTeammate"/>/<see cref="MyTeam"/>.
    ///
    /// AUTHORITY: host-authoritative roster. The host auto-assigns each player to the SMALLER team on join (kept
    /// balanced), and broadcasts the full roster (<c>MSG_PVP_TEAM</c> kind=ROSTER) to everyone. A player may switch
    /// to an OPEN slot on the other team — a client sends a SWITCH request to the host, who validates (slot free,
    /// not mid-match) and re-broadcasts. Teams are mutable only in the lobby/pre-match; locked once MissionActive.
    ///
    /// Entirely inert unless <see cref="Config.PvpActive"/> (a PvP lobby) ⇒ co-op/solo untouched.
    /// </summary>
    internal static class PvpTeams
    {
        private static ManualLogSource Log => Plugin.Logger;

        public const byte MSG_PVP_TEAM = 44;   // [t][kind u8] (+payload). kind 0 = roster (host->all), 1 = switch req (client->host)
        private const byte KIND_ROSTER = 0;
        private const byte KIND_SWITCH = 1;

        public const int TeamCount = 2;
        public const int SlotsPerTeam = 2;     // 2v2

        private sealed class Entry { public ulong Id; public int Team; public string Name; }
        private static readonly Dictionary<ulong, Entry> _roster = new Dictionary<ulong, Entry>();

        private static readonly List<ulong> _players = new List<ulong>();   // scratch: all current players (me + peers)
        private static readonly List<ulong> _peerScratch = new List<ulong>();
        private static readonly List<ulong> _toRemove = new List<ulong>();

        private static Il2CppStructArray<byte> _buf;
        private static int _lastPeerCount = -1;
        private static float _nextHostSync;

        // ---------------- per-frame ----------------

        public static void Tick(float dt)
        {
            if (!Config.PvpActive || !SteamNet.InLobby)
            {
                if (_roster.Count > 0) { _roster.Clear(); _lastPeerCount = -1; }
                return;
            }
            if (!CoopP2P.IsHost) return;   // clients only mirror what the host sends

            // Rebuild + broadcast when the player set changes (join/leave), plus a slow heartbeat so a late joiner
            // or a dropped roster packet self-heals.
            float now = Time.unscaledTime;
            int pc = CoopP2P.PeerCount;
            if (pc != _lastPeerCount) { _lastPeerCount = pc; HostRebuild(); _nextHostSync = now + 3f; }
            else if (now >= _nextHostSync) { _nextHostSync = now + 3f; HostBroadcastRoster(); }
        }

        // ---------------- host: assignment ----------------

        // Make every current player have a team (auto-assign newcomers to the smaller team for balance), drop those
        // who left, then broadcast. Host-only.
        private static void HostRebuild()
        {
            BuildPlayerList(_players);

            _toRemove.Clear();
            foreach (var id in _roster.Keys) if (!_players.Contains(id)) _toRemove.Add(id);
            for (int i = 0; i < _toRemove.Count; i++) _roster.Remove(_toRemove[i]);

            for (int i = 0; i < _players.Count; i++)
            {
                ulong id = _players[i];
                if (_roster.TryGetValue(id, out var e)) { e.Name = NameOf(id); continue; }
                _roster[id] = new Entry { Id = id, Team = SmallerTeam(), Name = NameOf(id) };
            }

            HostBroadcastRoster();
            Log.LogInfo($"[pvp] team roster: {RosterSummary()}");
        }

        private static int SmallerTeam() => CountTeam(0) <= CountTeam(1) ? 0 : 1;

        // A client asked to switch (or the host switches itself locally). Honour it only if there's an open slot on
        // the target team and we're not mid-match. Host-only.
        private static void HostHandleSwitch(ulong who, int desiredTeam)
        {
            if (LockedForMatch()) return;
            if (desiredTeam < 0 || desiredTeam >= TeamCount) return;
            if (!_roster.TryGetValue(who, out var e) || e.Team == desiredTeam) return;
            if (CountTeam(desiredTeam) >= SlotsPerTeam) return;   // no open slot
            e.Team = desiredTeam;
            HostBroadcastRoster();
            Log.LogInfo($"[pvp] '{e.Name}' -> team {desiredTeam + 1}: {RosterSummary()}");
        }

        // ---------------- send ----------------

        private static void HostBroadcastRoster()
        {
            if (!CoopP2P.IsHost || _roster.Count == 0 || !EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_PVP_TEAM); w.Byte(KIND_ROSTER);
            w.Byte((byte)_roster.Count);
            foreach (var e in _roster.Values)
            {
                w.Int((int)(e.Id & 0xFFFFFFFFUL)); w.Int((int)(e.Id >> 32));   // CoopWire has no u64 reader -> pack as 2 ints
                w.Byte((byte)e.Team);
                w.Str(e.Name ?? "", 32);
            }
            if (w.Overflow) { Log.LogWarning("[pvp] team roster overflow"); return; }
            CoopP2P.Send(_buf, w.Length, true);
        }

        // Ask to move to the other team's open slot (called by the roster GUI). Host applies locally; a client asks
        // the host. No-op if the match has started.
        public static void RequestSwitch(int desiredTeam)
        {
            if (LockedForMatch()) return;
            if (CoopP2P.IsHost) { HostHandleSwitch(CoopP2P.MyId, desiredTeam); return; }
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_PVP_TEAM); w.Byte(KIND_SWITCH); w.Byte((byte)desiredTeam);
            if (w.Overflow) return;
            CoopP2P.Send(_buf, w.Length, true);
        }

        // ---------------- receive ----------------

        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            if (type != MSG_PVP_TEAM || !Config.PvpActive) return;
            var r = new CoopWire.Reader(a, len, 1);
            byte kind = r.Byte();
            if (r.Bad) return;

            if (kind == KIND_ROSTER)
            {
                if (origin != CoopP2P.HostSteamId) return;       // only the host defines the roster
                int count = r.Byte();
                var fresh = new Dictionary<ulong, Entry>();
                for (int i = 0; i < count; i++)
                {
                    ulong id = (uint)r.Int() | ((ulong)(uint)r.Int() << 32);
                    int team = r.Byte();
                    string nm = r.Str(32);
                    if (r.Bad) return;
                    fresh[id] = new Entry { Id = id, Team = team, Name = nm };
                }
                _roster.Clear();
                foreach (var kv in fresh) _roster[kv.Key] = kv.Value;
                Log.LogInfo($"[pvp] team roster (from host): {RosterSummary()}");
            }
            else if (kind == KIND_SWITCH)
            {
                if (!CoopP2P.IsHost) return;                      // only the host honours switch requests
                int desired = r.Byte();
                if (r.Bad) return;
                HostHandleSwitch(origin, desired);
            }
        }

        // ---------------- public predicates (Phase B/C) ----------------

        public static int GetTeam(ulong id) => _roster.TryGetValue(id, out var e) ? e.Team : -1;
        public static int MyTeam => GetTeam(CoopP2P.MyId);
        public static bool RosterKnown => _roster.Count > 0 && MyTeam >= 0;

        // Same team as me (self counts as my own teammate). UNKNOWN teams ⇒ NOT teammates (safe default: no cross-talk
        // until the roster is known).
        public static bool IsTeammate(ulong origin)
        {
            if (origin == CoopP2P.MyId) return true;
            int mine = MyTeam, theirs = GetTeam(origin);
            return mine >= 0 && theirs >= 0 && mine == theirs;
        }

        public static int CountTeam(int team)
        {
            int c = 0;
            foreach (var e in _roster.Values) if (e.Team == team) c++;
            return c;
        }

        // ---------------- team CAPTAIN (Phase C: one vehicle per team) ----------------
        // A team is ONE vehicle, so it needs ONE authoritative representative: the LOWEST SteamID on the team. Every
        // machine computes the same captain from the shared roster (no election protocol). The captain owns the team's
        // shared health and is the only member that broadcasts the team's map position — so opponents see exactly one
        // mirror per enemy team, not one per enemy player. If the captain drops, the next-lowest takes over seamlessly
        // (non-captains already track the shared health from the captain's keyframes). Returns 0 if the team is empty.
        public static ulong TeamCaptain(int team)
        {
            ulong cap = 0; bool has = false;
            foreach (var e in _roster.Values)
                if (e.Team == team && (!has || e.Id < cap)) { cap = e.Id; has = true; }
            return has ? cap : 0;
        }

        public static bool IsCaptain(ulong id) { int t = GetTeam(id); return t >= 0 && TeamCaptain(t) == id; }
        public static bool AmICaptain => IsCaptain(CoopP2P.MyId);

        // Fill ordered (id,name) for a team — for the roster GUI. Stable-ish ordering by lowest SteamID.
        public static void FillTeam(int team, List<ulong> ids, List<string> names)
        {
            ids.Clear(); names.Clear();
            foreach (var e in _roster.Values) if (e.Team == team) { ids.Add(e.Id); names.Add(e.Name ?? ""); }
            // simple insertion sort by id so slot order is stable across machines
            for (int i = 1; i < ids.Count; i++)
            {
                ulong id = ids[i]; string nm = names[i]; int j = i - 1;
                while (j >= 0 && ids[j] > id) { ids[j + 1] = ids[j]; names[j + 1] = names[j]; j--; }
                ids[j + 1] = id; names[j + 1] = nm;
            }
        }

        public static bool LockedForMatch()
        {
            try { var mm = MissionManager.Instance; return mm != null && mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        // ---------------- helpers ----------------

        private static void BuildPlayerList(List<ulong> dst)
        {
            dst.Clear();
            dst.Add(CoopP2P.MyId);
            CoopP2P.CopyPeerIds(_peerScratch);
            for (int i = 0; i < _peerScratch.Count; i++) dst.Add(_peerScratch[i]);
        }

        private static string NameOf(ulong id)
        {
            if (id == CoopP2P.MyId) { try { var n = SteamFriends.GetPersonaName(); if (!string.IsNullOrEmpty(n)) return n; } catch { } return "Me"; }
            var pn = CoopP2P.NameFor(id);
            return string.IsNullOrEmpty(pn) ? ("Player " + (id % 10000)) : pn;
        }

        private static string RosterSummary()
        {
            var sb = new System.Text.StringBuilder();
            for (int t = 0; t < TeamCount; t++)
            {
                if (t > 0) sb.Append(" | ");
                sb.Append("T").Append(t + 1).Append(":");
                bool any = false;
                foreach (var e in _roster.Values) if (e.Team == t) { if (any) sb.Append(','); sb.Append(e.Name); any = true; }
                if (!any) sb.Append("(empty)");
            }
            return sb.ToString();
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(256); return true; }
            catch (Exception e) { Log.LogWarning("[pvp] team buf: " + e.Message); return false; }
        }

        public static string Status() => $"pvpTeams: known={RosterKnown} myTeam={(MyTeam >= 0 ? (MyTeam + 1).ToString() : "-")} roster=[{RosterSummary()}]";

#if !PUBLIC_BUILD
        // ---------------- flatscreen roster GUI (dev/testing; the VR world-space panel is a follow-up) ----------------
        // Two team columns: filled names + open slots. Click an OPEN slot on the OTHER team to switch into it. IMGUI
        // button returns are dead under the new Input System (see LobbyGui), so we DRAW in OnGUI and DETECT CLICKS
        // ourselves in Update against the same rects — and only while the F7 panel has freed the cursor. Appears in
        // any PvP lobby (so Create PvP pulls it up); locked once the match starts.
        private static readonly List<ulong> _gIds = new List<ulong>();
        private static readonly List<string> _gNames = new List<string>();
        private static readonly List<(Rect r, int team)> _openSlots = new List<(Rect, int)>();
        private static Rect _launchRect;
        private static bool _launchShown;
        private static bool _launchEnabled;

        public static void DrawPanel() { try { BuildPanel(true); } catch { } }

        // Click detection (called from Update). Only acts while the cursor is freed by the F7 lobby panel.
        public static void HandleInput()
        {
            try
            {
                if (!LobbyGui.FlatInteractive || !Config.PvpActive || !SteamNet.InLobby) return;
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m == null || !m.leftButton.wasPressedThisFrame) return;
                float mx = m.position.x.ReadValue(), my = m.position.y.ReadValue();
                var gui = new Vector2(mx, Screen.height - my);   // mouse is Y-up; GUI is Y-down
                BuildPanel(false);                                // refresh _openSlots/_launchRect without drawing
                for (int i = 0; i < _openSlots.Count; i++)
                    if (_openSlots[i].r.Contains(gui)) { RequestSwitch(_openSlots[i].team); return; }
                if (_launchShown && _launchEnabled && _launchRect.Contains(gui)) { try { PvpMatch.LaunchArena(); } catch { } }
            }
            catch { }
        }

        // Shared layout for draw + click: positions the panel; when render, draws box/labels/slots + the host's Launch
        // button; always records clickable OPEN slots into _openSlots and the host Launch rect.
        private static void BuildPanel(bool render)
        {
            if (!Config.PvpActive || !SteamNet.InLobby) return;
            _openSlots.Clear();
            bool locked = LockedForMatch();

            const float colW = 170f, rowH = 24f, pad = 10f;
            float w = colW * 2 + pad * 3;
            float x = (Screen.width - w) * 0.5f, y = 8f;
            float colHeaderY = y + 30f;
            float slotsEnd = colHeaderY + rowH + SlotsPerTeam * rowH;   // bottom of the slot rows

            // The host launches the match from here (replaces the Ctrl+Shift+L dev key). Shown host-only, pre-match.
            _launchShown = CoopP2P.IsHost && !locked;
            float launchY = slotsEnd + 6f;
            float h = (_launchShown ? (launchY + rowH + 8f) : (slotsEnd + 10f)) - y;

            if (render) GUI.Box(new Rect(x, y, w, h), locked ? "PvP TEAMS  (locked — match in progress)" : "PvP TEAMS  (click an open slot to switch)");
            BuildColumn(0, "TEAM 1", x + pad, colHeaderY, colW, rowH, locked, render);
            BuildColumn(1, "TEAM 2", x + pad * 2 + colW, colHeaderY, colW, rowH, locked, render);

            if (_launchShown)
            {
                _launchRect = new Rect(x + pad, launchY, w - pad * 2f, rowH);
                // Require at least one player on EACH team (1v2 etc. is fine; an empty team is not).
                _launchEnabled = CountTeam(0) >= 1 && CountTeam(1) >= 1;
                if (render)
                {
                    bool pending = false; try { pending = PvpMatch.LaunchPending; } catch { }
                    var prev = GUI.contentColor; GUI.contentColor = pending ? Color.yellow : (_launchEnabled ? Color.green : Color.white);
                    GUI.enabled = _launchEnabled && !pending;
                    GUI.Button(_launchRect, pending ? "LAUNCHING…" : (_launchEnabled ? "LAUNCH MATCH" : "LAUNCH  (need 1+ on each team)"));
                    GUI.enabled = true;
                    GUI.contentColor = prev;
                }
            }
        }

        private static void BuildColumn(int team, string title, float x, float y, float colW, float rowH, bool locked, bool render)
        {
            FillTeam(team, _gIds, _gNames);
            if (render) GUI.Label(new Rect(x, y, colW, rowH), title + $"  ({_gIds.Count}/{SlotsPerTeam})");
            float ry = y + rowH;
            for (int s = 0; s < SlotsPerTeam; s++)
            {
                var rect = new Rect(x, ry, colW, rowH - 2f);
                if (s < _gIds.Count)
                {
                    if (render)
                    {
                        bool me = _gIds[s] == CoopP2P.MyId;
                        var prev = GUI.contentColor; if (me) GUI.contentColor = Color.green;
                        GUI.Label(rect, (me ? "» " : "  ") + _gNames[s]);
                        GUI.contentColor = prev;
                    }
                }
                else
                {
                    bool canJoin = !locked && team != MyTeam;   // only the OTHER team's open slot is joinable
                    if (canJoin) _openSlots.Add((rect, team));
                    if (render) { GUI.enabled = canJoin; GUI.Button(rect, canJoin ? "[ join ]" : "[ empty ]"); GUI.enabled = true; }
                }
                ry += rowH;
            }
        }
#endif
    }
}
