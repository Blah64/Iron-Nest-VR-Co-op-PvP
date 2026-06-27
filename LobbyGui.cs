using System;
using System.Collections.Generic;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Flatscreen (non-VR) lobby browser — the crossplay counterpart to the in-headset VR menu page.
    ///
    /// Two gotchas on this game drove the design:
    ///  1. World-space VR UI doesn't render to a flat screen and screen-space UI doesn't render into the
    ///     VR eye textures, so VR and flatscreen each need their own front-end over the shared SteamNet.
    ///  2. The game uses the NEW Input System (legacy UnityEngine.Input is off), so IMGUI's built-in click
    ///     handling never fires — GUI.Button draws but its return value is always false. So we DRAW with
    ///     OnGUI and DETECT CLICKS ourselves via UnityEngine.InputSystem.Mouse, hit-testing the same rects.
    ///
    /// A single Layout() pass feeds both Draw (render) and HandleInput (click) so the rects always match.
    /// HIDDEN by default: while shown on flatscreen it frees the cursor and freezes FPS look (so its
    /// buttons are clickable), which would otherwise stop a non-VR player moving their camera the moment
    /// the mod loads. Co-op is opt-in — F7 toggles the panel on/off. (VR never shows it; it uses the
    /// in-headset menu page.) This keeps flatscreen-without-a-headset playing exactly like unmodded.
    /// </summary>
    internal static class LobbyGui
    {
        public static bool Shown = false;         // opt-in via F7; never auto-grabs the flatscreen camera
        public static bool FlatInteractive;       // set by VrManager = Shown && not in VR -> free cursor + take clicks

        private struct Btn { public Rect R; public string Label; public Action Act; public int JoinIdx; }

        private static readonly List<(Rect r, string s)> _labels = new List<(Rect, string)>();
        private static readonly List<Btn> _buttons = new List<Btn>();

        private static void Layout(out Rect box, out List<(Rect r, string s)> labels, out List<Btn> buttons)
        {
            _labels.Clear();
            _buttons.Clear();
            labels = _labels;
            buttons = _buttons;

            const float x = 12f, w = 380f, rh = 26f, pad = 8f, gap = 4f;
            int count = SteamNet.Lobbies.Count;
            float listH = (count == 0 ? rh : count * (rh + gap));
            float h = 28f + rh + pad + rh + gap + rh + pad + rh + listH + pad;   // +1 button row (PvE/PvP)
            box = new Rect(x, 12f, w, h);

            float cy = 12f + 28f;
            labels.Add((new Rect(x + 10f, cy, w - 20f, rh), SteamNet.StatusLine()));
            cy += rh + pad;

            // Row 1: choose the lobby mode at creation. Row 2: refresh / leave.
            float bw = (w - 20f - 6f) / 2f;
            buttons.Add(new Btn { R = new Rect(x + 10f, cy, bw, rh), Label = "Create PvE", Act = () => SteamNet.CreateLobby(false), JoinIdx = -1 });
            buttons.Add(new Btn { R = new Rect(x + 10f + (bw + 6f), cy, bw, rh), Label = "Create PvP", Act = () => SteamNet.CreateLobby(true), JoinIdx = -1 });
            cy += rh + gap;
            buttons.Add(new Btn { R = new Rect(x + 10f, cy, bw, rh), Label = "Refresh", Act = SteamNet.RefreshLobbyList, JoinIdx = -1 });
            buttons.Add(new Btn { R = new Rect(x + 10f + (bw + 6f), cy, bw, rh), Label = "Leave", Act = SteamNet.Leave, JoinIdx = -1 });
            cy += rh + pad;

            labels.Add((new Rect(x + 10f, cy, w - 20f, rh), "Public lobbies:"));
            cy += rh;

            if (count == 0)
            {
                labels.Add((new Rect(x + 16f, cy, w - 26f, rh), SteamNet.Ready ? "(none — click Refresh)" : "(Steam connecting…)"));
                return;
            }
            for (int i = 0; i < count; i++)
            {
                labels.Add((new Rect(x + 16f, cy, w - 100f, rh), SteamNet.SlotLabel(i)));
                buttons.Add(new Btn { R = new Rect(x + w - 80f, cy, 70f, rh - 2f), Label = "Join", Act = null, JoinIdx = i });
                cy += rh + gap;
            }
        }

        // OnGUI: render only (see class note — built-in click handling is dead under the new Input System).
        public static void Draw()
        {
            if (!Shown) return;
            if (FlatInteractive) { try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { } }

            Layout(out var box, out var labels, out var buttons);
            GUI.Box(box, "IRON NEST  —  Lobbies  (PvE / PvP)   (F7 to hide)");
            for (int i = 0; i < labels.Count; i++) GUI.Label(labels[i].r, labels[i].s);
            for (int i = 0; i < buttons.Count; i++) GUI.Button(buttons[i].R, buttons[i].Label); // drawn; click handled below
        }

        // Update: detect a left-click through the NEW Input System and run the hit button's action.
        public static void HandleInput()
        {
            if (!Shown || !FlatInteractive) return;
            try
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m == null || !m.leftButton.wasPressedThisFrame) return;
                // Mouse.position is screen px, bottom-left origin (Y up); GUI is top-left origin (Y down).
                float mx = m.position.x.ReadValue();
                float my = m.position.y.ReadValue();
                var gui = new Vector2(mx, Screen.height - my);

                Layout(out _, out _, out var buttons);
                for (int i = 0; i < buttons.Count; i++)
                    if (buttons[i].R.Contains(gui)) { try { if (buttons[i].JoinIdx >= 0) SteamNet.JoinLobbyByIndex(buttons[i].JoinIdx); else buttons[i].Act(); } catch { } break; }
            }
            catch { }
        }
    }
}
