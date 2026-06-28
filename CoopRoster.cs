using System;
using System.Collections.Generic;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// CO-OP player list — the top-centre roster that opens/closes with the flatscreen lobby browser (F7), the
    /// counterpart to PvP's team panel but a SINGLE list (everyone's on the same crew). Shows who's in the lobby and
    /// lets the HOST click a name to kick, plus a host Lock toggle. The VR side reaches the same model through the
    /// settings menu's Lobbies tab (player slots + Lock row) — both front-ends drive the shared SteamNet (see
    /// FillMembers / Kick / ToggleLock), so VR and flatscreen see and do the same things.
    ///
    /// Drawn screen-space with OnGUI; clicks come through the new Input System (GUI.Button returns are dead — see
    /// LobbyGui), hit-tested against the same rects, and ONLY while the cursor is actually free (FlatInteractive,
    /// which VrManager sets to the same condition as the lobby panel). Inert in VR and when the F7 panel is hidden,
    /// so a no-headset player who never opens the browser plays exactly like unmodded.
    /// </summary>
    internal static class CoopRoster
    {
        public static bool FlatInteractive;   // set by VrManager: the cursor is free (F7 panel open) so clicks register

        private static readonly List<SteamNet.Member> _members = new List<SteamNet.Member>();
        private static readonly List<(Rect r, ulong id)> _kickRects = new List<(Rect, ulong)>();
        private static Rect _lockRect;
        private static bool _lockShown;

        // Co-op only: opens with the F7 lobby browser while in a lobby. A PvP lobby uses PvpTeams' 2-column panel.
        private static bool ShouldShow => LobbyGui.Shown && SteamNet.InLobby && !Config.PvpActive;

        public static void DrawPanel() { if (!ShouldShow) return; try { Build(true); } catch { } }

        // Click detection (called from Update). Only acts while the cursor is freed by the F7 lobby panel.
        public static void HandleInput()
        {
            if (!ShouldShow || !FlatInteractive) return;
            try
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m == null || !m.leftButton.wasPressedThisFrame) return;
                var gui = new Vector2(m.position.x.ReadValue(), Screen.height - m.position.y.ReadValue());   // mouse Y-up; GUI Y-down
                Build(false);   // refresh rects without drawing
                if (_lockShown && _lockRect.Contains(gui)) { SteamNet.ToggleLock(); return; }
                for (int i = 0; i < _kickRects.Count; i++)
                    if (_kickRects[i].r.Contains(gui)) { SteamNet.Kick(_kickRects[i].id); return; }
            }
            catch { }
        }

        // Shared layout for draw + click: top-centre panel listing every member; host gets a kick button per OTHER
        // player and a Lock toggle. Always records the clickable rects; only draws when render.
        private static void Build(bool render)
        {
            SteamNet.FillMembers(_members);
            _kickRects.Clear();
            bool host = CoopP2P.IsHost;

            const float w = 300f, rowH = 22f, headerH = 26f, pad = 8f;
            int rows = Mathf.Max(1, _members.Count);
            float lockH = host ? rowH + 6f : 0f;
            float h = headerH + rows * rowH + lockH + pad;
            float x = (Screen.width - w) * 0.5f, y = 8f;
            float lx = x + 10f, lw = w - 20f;

            if (render)
            {
                string title = $"CO-OP CREW  ({_members.Count}/{SteamNet.MaxMembers})" + (SteamNet.IsLocked ? "   [LOCKED]" : "");
                GUI.Box(new Rect(x, y, w, h), title);
            }

            float cy = y + headerH;
            if (_members.Count == 0)
            {
                if (render) GUI.Label(new Rect(lx, cy, lw, rowH), "(connecting…)");
                cy += rowH;
            }
            for (int i = 0; i < _members.Count; i++)
            {
                var mem = _members[i];
                bool canKick = host && !mem.IsMe;
                float nameW = canKick ? lw - 64f : lw;
                if (render)
                {
                    var prev = GUI.contentColor; if (mem.IsMe) GUI.contentColor = Color.green;
                    GUI.Label(new Rect(lx, cy, nameW, rowH), (mem.IsMe ? "» " : "  ") + mem.Name + (mem.IsHost ? "  (host)" : ""));
                    GUI.contentColor = prev;
                }
                if (canKick)
                {
                    var kr = new Rect(lx + lw - 60f, cy, 58f, rowH - 2f);
                    _kickRects.Add((kr, mem.Id));
                    if (render) GUI.Button(kr, "kick");
                }
                cy += rowH;
            }

            _lockShown = host;
            if (host)
            {
                _lockRect = new Rect(lx, cy + 4f, lw, rowH);
                if (render)
                {
                    var prev = GUI.contentColor; GUI.contentColor = SteamNet.IsLocked ? Color.yellow : Color.white;
                    string label = !SteamNet.IsLocked ? "Lock lobby  (block new players)"
                                 : SteamNet.ManualLock ? "Locked — click to unlock"
                                 : "Locked (mission in progress)";
                    GUI.Button(_lockRect, label);
                    GUI.contentColor = prev;
                }
            }
        }
    }
}
