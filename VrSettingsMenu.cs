using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using TMPro;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// A world-space VR settings panel, opened by clicking BOTH thumbsticks at once. It floats in front
    /// of the player (placed once on open, then world-locked so you can look around it) and is operated
    /// with the laser + trigger: point at a row and pull the trigger to toggle/activate it, or — for
    /// numeric rows — point at the right half to increase and the left half to decrease.
    ///
    /// It's a plain managed object (not an injected MonoBehaviour): it builds Unity primitives + 3D
    /// TextMeshPro text (which renders into the VR eye cameras, unlike the game's screen-space UI) and
    /// does its own laser raycast against the row colliders. Changes write straight to <see cref="Config"/>
    /// and apply live (clipboard size, turn mode, move speed, laser); resolution scale is applied by
    /// <see cref="VrManager"/> when the menu closes (it needs a swapchain/RT rebuild).
    /// </summary>
    internal sealed class VrSettingsMenu
    {
        private static ManualLogSource Log => Plugin.Logger;

        private enum Kind { Float, Toggle, Action }

        private sealed class Row
        {
            public string Label;
            public Kind Kind;
            public Func<string> Value;     // display string for the current value
            public Action<int> Adjust;     // dir = +1/-1 (numeric) or +1 (toggle/action)
            public GameObject Go;          // row background quad (carries the collider)
            public Material Mat;           // its material (for hover highlight)
            public TextMeshPro ValueText;  // right-aligned value label
            public int ColliderId;
            public string LastValue = null;
        }

        // Layout (metres at MenuScale = 1).
        private const float PANEL_W = 0.60f;
        private const float ROW_H = 0.052f;
        private const float ROW_GAP = 0.006f;
        private const float PAD = 0.03f;
        private const float TITLE_H = 0.072f;
        private const float FOOTER_H = 0.05f;

        private static readonly Color C_BG = new Color(0.05f, 0.06f, 0.08f, 1f);
        private static readonly Color C_ROW = new Color(0.13f, 0.15f, 0.19f, 1f);
        private static readonly Color C_ROW_HOVER = new Color(0.18f, 0.42f, 0.80f, 1f);
        private static readonly Color C_LABEL = new Color(0.92f, 0.94f, 0.98f, 1f);
        private static readonly Color C_VALUE = new Color(1f, 0.86f, 0.45f, 1f);

        private readonly List<Row> _rows = new List<Row>();
        private readonly Dictionary<int, int> _idToRow = new Dictionary<int, int>();
        private GameObject _root;
        private TMP_FontAsset _font;
        private CameraRig _rig;
        internal HandVisuals Hands;   // for the in-menu hand Calibrate tool
        internal GrabManager Grab;    // for the clipboard/watch Calibrate tools on the HUD tab
        private bool _open;
        private int _hoverIndex = -1;
        private bool _prevTrigger;
        private int _layer;

        private enum Page { Settings, Hud, Lobbies }
        private Page _page = Page.Settings;
        private int _scroll;                       // lobby-list scroll offset (windowed view)
        private const int LOBBY_VISIBLE = 6;       // join slots shown at once on the Lobbies tab
        private const float TAB_H = 0.05f;
        private readonly Dictionary<int, int> _idToTab = new Dictionary<int, int>(); // collider id -> page

        public bool IsOpen => _open;

        public void Toggle(CameraRig rig)
        {
            if (_open) Close();
            else Open(rig);
        }

        public void Open(CameraRig rig)
        {
            if (_open || !Config.MenuEnabled) return;
            try
            {
                _rig = rig;
                _layer = ResolveLayer();
                EnsureFont();
                Build(rig);
                _open = true;
                _hoverIndex = -1;
                _prevTrigger = true; // swallow the trigger if it happens to be held as the menu opens
                Log.LogInfo("[menu] opened.");
            }
            catch (Exception e)
            {
                Log.LogWarning("[menu] open failed: " + e.Message);
                Destroy();
            }
        }

        public void Close()
        {
            if (!_open) return;
            try { Hands?.CancelCalibration(); } catch { } // don't leave a grip hijacked after the menu shuts
            try { Grab?.CancelCalibrate(); } catch { }     // disarm clipboard/watch calibration on close
            try { Config.Save(); } catch { }               // persist menu edits + hand calibration for next session
            Destroy();
            _open = false;
            _hoverIndex = -1;
            Log.LogInfo("[menu] closed.");
        }

        public void Tick(VrInput input, CameraRig rig)
        {
            if (!_open) return;
            if (_root == null) { _open = false; return; }
            _rig = rig;
            try
            {
                var origin = rig.OriginTransform;
                bool gotHit = false;
                int hoverRow = -1;
                int hoverTab = -1;
                Vector3 hitPoint = Vector3.zero;

                if (input.AimValid && origin != null)
                {
                    Posef p = input.AimPose;
                    var lp = new Vector3(p.Position.X, p.Position.Y, -p.Position.Z);
                    var lr = new Quaternion(-p.Orientation.X, -p.Orientation.Y, p.Orientation.Z, p.Orientation.W);
                    Vector3 o = origin.TransformPoint(lp);
                    Vector3 d = (origin.rotation * lr) * Vector3.forward;

                    var hits = Physics.RaycastAll(o, d, Config.LaserMaxDistance + 1f, ~0, QueryTriggerInteraction.Ignore);
                    float best = float.MaxValue;
                    if (hits != null)
                    {
                        for (int i = 0; i < hits.Length; i++)
                        {
                            var h = hits[i];
                            var col = h.collider;
                            if (col == null) continue;
                            int cid = col.GetInstanceID();
                            if (_idToRow.TryGetValue(cid, out int ri) && h.distance < best)
                            {
                                best = h.distance; hoverRow = ri; hoverTab = -1; hitPoint = h.point; gotHit = true;
                            }
                            else if (_idToTab.TryGetValue(cid, out int ti) && h.distance < best)
                            {
                                best = h.distance; hoverTab = ti; hoverRow = -1; gotHit = true;
                            }
                        }
                    }
                }

                SetHover(hoverRow);

                bool held = input.TriggerHeld;
                if (held && !_prevTrigger && gotHit)
                {
                    if (hoverTab >= 0) SwitchPage((Page)hoverTab);
                    else if (hoverRow >= 0) Activate(hoverRow, hitPoint, rig);
                    input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
                }
                _prevTrigger = held;

                RefreshValues();
            }
            catch (Exception e)
            {
                Log.LogWarning("[menu] tick: " + e.Message);
                Close();
            }
        }

        public void Dispose() { Destroy(); _open = false; }

        // ---------------- internals ----------------

        private void Activate(int rowIndex, Vector3 hitPoint, CameraRig rig)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count) return;
            var row = _rows[rowIndex];
            int dir = 1;
            if (row.Kind == Kind.Float && rig.TryGetHeadBasis(out _, out var right) && row.Go != null)
            {
                float side = Vector3.Dot(hitPoint - row.Go.transform.position, right);
                dir = side >= 0f ? 1 : -1;
            }
            try { row.Adjust?.Invoke(dir); } catch (Exception e) { Log.LogWarning("[menu] adjust: " + e.Message); }
        }

        private void SetHover(int idx)
        {
            if (idx == _hoverIndex) return;
            if (_hoverIndex >= 0 && _hoverIndex < _rows.Count) SetMat(_rows[_hoverIndex].Mat, C_ROW);
            if (idx >= 0 && idx < _rows.Count) SetMat(_rows[idx].Mat, C_ROW_HOVER);
            _hoverIndex = idx;
        }

        private void RefreshValues()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r.ValueText == null || r.Value == null) continue;
                string v = r.Value();
                if (v != r.LastValue) { r.ValueText.text = v; r.ValueText.ForceMeshUpdate(false, false); r.LastValue = v; }
            }
        }

        private void Build(CameraRig rig)
        {
            DefineRows();
            int n = _rows.Count;
            float contentH = PAD + TITLE_H + TAB_H + n * (ROW_H + ROW_GAP) + FOOTER_H + PAD;

            // Place in front of the head, world-locked, facing the player.
            rig.TryGetHeadPose(out var hp, out var hr);
            if (!rig.TryGetHeadBasis(out var fwd, out _))
            {
                fwd = hr * Vector3.forward; fwd.y = 0f;
                fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            }
            Vector3 panelPos = hp + fwd * Config.MenuDistance + Vector3.up * Config.MenuHeightOffset;
            // Face the panel AWAY from the head so TMP's readable (front) face points at the player.
            // That makes "toward the player" the local -Z direction (see FRONT_* depth offsets below).
            Quaternion rot = Quaternion.LookRotation((panelPos - hp).normalized, Vector3.up);
            if (Config.MenuFlip) rot *= Quaternion.Euler(0f, 180f, 0f);

            _root = new GameObject("IronNestVR_SettingsMenu");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;
            _root.transform.SetPositionAndRotation(panelPos, rot);
            _root.transform.localScale = Vector3.one * Mathf.Max(0.2f, Config.MenuScale);
            _root.layer = _layer;

            // Depth layering: -Z is toward the player (panel faces away), so closer = more negative.
            const float Z_ROW = -0.004f, Z_TEXT = -0.008f;
            float innerW = PANEL_W - 0.06f;
            float labelW = innerW * 0.60f;
            float valueW = innerW * 0.36f;
            float labelX = -innerW * 0.5f + labelW * 0.5f;
            float valueX = innerW * 0.5f - valueW * 0.5f;
            // Text boxes use ~80% of the row height so auto-size leaves a little vertical padding.
            float textH = ROW_H * 0.80f;

            // Background.
            MakeQuad(_root.transform, Vector3.zero, new Vector3(PANEL_W + 0.02f, contentH, 1f), C_BG, false);

            float top = contentH * 0.5f - PAD;
            // Title.
            float titleY = top - TITLE_H * 0.5f;
            MakeText(_root.transform, "IRON NEST VR", TextAlignmentOptions.Center,
                     new Vector3(0f, titleY, Z_TEXT), new Vector2(PANEL_W - 0.06f, TITLE_H * 0.8f), C_LABEL, true);
            float y = top - TITLE_H;

            // Tab bar (Settings | Lobbies) — clickable like rows; switching rebuilds the active page.
            _idToTab.Clear();
            BuildTabs(y, Z_ROW, Z_TEXT);
            y -= TAB_H;

            // Rows.
            for (int i = 0; i < n; i++)
            {
                var row = _rows[i];
                float cy = y - ROW_H * 0.5f;
                var go = MakeQuad(_root.transform, new Vector3(0f, cy, Z_ROW),
                                  new Vector3(PANEL_W - 0.02f, ROW_H, 1f), C_ROW, true);
                row.Go = go;
                var mr = go.GetComponent<MeshRenderer>();
                row.Mat = mr != null ? mr.material : null;
                var mc = go.GetComponent<MeshCollider>();
                row.ColliderId = mc != null ? mc.GetInstanceID() : go.GetInstanceID();
                _idToRow[row.ColliderId] = i;

                MakeText(_root.transform, row.Label, TextAlignmentOptions.Left,
                         new Vector3(labelX, cy, Z_TEXT), new Vector2(labelW, textH), C_LABEL, false);
                if (row.Kind != Kind.Action)
                {
                    row.ValueText = MakeText(_root.transform, row.Value != null ? row.Value() : "",
                                             TextAlignmentOptions.Right,
                                             new Vector3(valueX, cy, Z_TEXT), new Vector2(valueW, textH), C_VALUE, false);
                    row.LastValue = row.ValueText.text;
                }
                y -= (ROW_H + ROW_GAP);
            }

            // Footer hint.
            float footerY = y - FOOTER_H * 0.5f;
            MakeText(_root.transform, "trigger: select  -  point left/right to change  -  both sticks: close",
                     TextAlignmentOptions.Center,
                     new Vector3(0f, footerY, Z_TEXT), new Vector2(PANEL_W - 0.06f, FOOTER_H * 0.7f),
                     new Color(0.6f, 0.65f, 0.72f, 1f), false);

            Log.LogInfo($"[menu] built {n} rows at {panelPos} (font={(_font != null ? _font.name : "null")}).");
        }

        private void DefineRows()
        {
            _rows.Clear();
            _idToRow.Clear();
            if (_page == Page.Lobbies) DefineLobbyRows();
            else if (_page == Page.Hud) DefineHudRows();
            else DefineSettingsRows();
        }

        // The in-VR lobby browser (its own tab). Status + join-slot value text auto-refresh each tick via
        // RefreshValues, so the list and the scroll window update live without rebuilding the menu.
        private void DefineLobbyRows()
        {
            AddToggle("Status", () => SteamNet.StatusLine(), () => { });
            AddAction("Create Lobby", () => SteamNet.CreateLobby());
            AddAction("Refresh List", () => SteamNet.RefreshLobbyList());
            AddAction("Leave Lobby", () => SteamNet.Leave());
            AddToggle("Showing", LobbyRangeLabel, () => { });
            AddAction("Prev Page", () => ScrollLobbies(-1));
            AddAction("Next Page", () => ScrollLobbies(+1));
            for (int i = 0; i < LOBBY_VISIBLE; i++)
            {
                int slot = i;   // join the lobby at the current scroll window + this slot
                AddToggle("Join", () => SteamNet.SlotLabel(_scroll + slot), () => SteamNet.JoinLobbyByIndex(_scroll + slot));
            }
            AddAction("Close Menu", Close);
        }

        private string LobbyRangeLabel()
        {
            int n = SteamNet.Lobbies.Count;
            if (n == 0) return "0 lobbies";
            int a = _scroll + 1, b = Mathf.Min(_scroll + LOBBY_VISIBLE, n);
            return $"{a}-{b} of {n}";
        }

        private void ScrollLobbies(int dir)
        {
            int maxScroll = Mathf.Max(0, SteamNet.Lobbies.Count - LOBBY_VISIBLE);
            _scroll = Mathf.Clamp(_scroll + dir * LOBBY_VISIBLE, 0, maxScroll);
        }

        private void DefineSettingsRows()
        {
            AddFloat("Clipboard Size", () => Config.ClipboardScale.ToString("0.0") + "x",
                     d => Config.ClipboardScale = Clamp(Config.ClipboardScale + d * 0.1f, 0.5f, 4f));
            AddFloat("Watch Size", () => Config.WatchScale.ToString("0.0") + "x",
                     d => Config.WatchScale = Clamp(Config.WatchScale + d * 0.1f, 0.3f, 4f));
            AddFloat("Resolution Scale", () => Mathf.RoundToInt(Config.RenderScale * 100f) + "%",
                     d => Config.RenderScale = Clamp(Config.RenderScale + d * 0.05f, 0.2f, 1f));
            AddToggle("Turn Mode", () => Config.SnapTurn ? "Snap" : "Smooth",
                      () => Config.SnapTurn = !Config.SnapTurn);
            AddFloat("Smooth Turn Speed", () => Mathf.RoundToInt(Config.TurnSpeedDegPerSec) + " deg/s",
                     d => Config.TurnSpeedDegPerSec = Clamp(Config.TurnSpeedDegPerSec + d * 10f, 30f, 240f));
            AddFloat("Snap Turn Angle", () => Mathf.RoundToInt(Config.SnapTurnAngle) + " deg",
                     d => Config.SnapTurnAngle = Clamp(Config.SnapTurnAngle + d * 5f, 10f, 90f));
            AddFloat("Move Speed", () => Config.MoveSpeedScale.ToString("0.0") + "x",
                     d => Config.MoveSpeedScale = Clamp(Config.MoveSpeedScale + d * 0.1f, 0.2f, 3f));
            AddToggle("Laser Always On", () => Config.LaserAlwaysOn ? "On" : "Off",
                      () => Config.LaserAlwaysOn = !Config.LaserAlwaysOn);
            AddFloat("Switch Throw", () => Mathf.RoundToInt(Config.SwitchThrowDistance * 100f) + " cm",
                     d => Config.SwitchThrowDistance = Clamp(Config.SwitchThrowDistance + d * 0.01f, 0.02f, 0.15f));

            // --- Hand calibration (live) ---
            // Calibrate: tap to arm, then hold the OPPOSITE controller's grip and move the hand into
            // place (like grabbing the clipboard); release to keep, tap again to finish. (Hands on/off,
            // size and finger-curl are fixed at their Config defaults — not player-configurable.)
            AddToggle("Calibrate Right Hand",
                      () => (Hands != null && Hands.CalibratingRight) ? "hold LEFT grip" : "tap",
                      () => Hands?.ToggleCalibration(true));
            AddToggle("Calibrate Left Hand",
                      () => (Hands != null && Hands.Calibrating && !Hands.CalibratingRight) ? "hold RIGHT grip" : "tap",
                      () => Hands?.ToggleCalibration(false));
            AddAction("Reset Hand Offsets", () => Hands?.ResetOffsets());

            AddAction("Recenter View", () => { _rig?.Recenter(); });
            AddAction("Close Menu", Close);
        }

        // HUD layout tab: calibrate the waist-holstered clipboard and the wrist watch by physically placing
        // them, the same way the hand models are calibrated. Arm a row (tap), then GRIP the prop and move it
        // to where you want it — release to set. Works with this menu open (the menu uses the trigger; grab
        // uses the grip). The placed pose is saved to the cfg. Fine angle adjustment helps the watch most.
        private void DefineHudRows()
        {
            AddToggle("Calibrate Clipboard",
                      () => (Grab != null && Grab.CalibratingClip) ? "grip & place" : "tap",
                      () => Grab?.ToggleCalibrate(false));
            AddToggle("Calibrate Watch",
                      () => (Grab != null && Grab.CalibratingWatch) ? "R-grip on wrist" : "tap",
                      () => Grab?.ToggleCalibrate(true));

            // Angle fine-tune (grab nails position; small wrist jitter makes exact angles hard to grab-set).
            AddFloat("Clip Tilt", () => Deg(Config.ClipWaistEuler.x),
                     d => Config.ClipWaistEuler.x = Wrap(Config.ClipWaistEuler.x + d * 5f));
            AddFloat("Watch Pitch", () => Deg(Config.WatchWristEuler.x),
                     d => Config.WatchWristEuler.x = Wrap(Config.WatchWristEuler.x + d * 5f));
            AddFloat("Watch Yaw", () => Deg(Config.WatchWristEuler.y),
                     d => Config.WatchWristEuler.y = Wrap(Config.WatchWristEuler.y + d * 5f));
            AddFloat("Watch Roll", () => Deg(Config.WatchWristEuler.z),
                     d => Config.WatchWristEuler.z = Wrap(Config.WatchWristEuler.z + d * 5f));

            AddAction("Reset HUD Layout", ResetHudLayout);
            AddAction("Close Menu", Close);
        }

        // Restore the waist/wrist anchors to their built-in defaults (the values baked into Config).
        private void ResetHudLayout()
        {
            Config.ClipWaistOffset = new Vector3(0f, -0.45f, 0.33f);
            Config.ClipWaistEuler = new Vector3(45f, 0f, 0f);
            Config.WatchWristOffset = new Vector3(0f, 0f, -0.05f);
            Config.WatchWristEuler = Vector3.zero;
            try { Config.Save(); } catch { }
            Log.LogInfo("[menu] HUD layout reset to defaults.");
        }

        private static string Deg(float deg) => Mathf.RoundToInt(deg) + " deg";
        private static float Wrap(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            else if (deg < -180f) deg += 360f;
            return deg;
        }

        private void AddFloat(string label, Func<string> value, Action<int> adjust)
            => _rows.Add(new Row { Label = label, Kind = Kind.Float, Value = value, Adjust = adjust });

        private void AddToggle(string label, Func<string> value, System.Action toggle)
            => _rows.Add(new Row { Label = label, Kind = Kind.Toggle, Value = value, Adjust = _ => toggle() });

        private void AddAction(string label, System.Action act)
            => _rows.Add(new Row { Label = label, Kind = Kind.Action, Value = null, Adjust = _ => act() });

        private static float Clamp(float v, float lo, float hi) => Mathf.Clamp(v, lo, hi);

        // Two side-by-side clickable tabs at the top of the panel. Active tab is highlighted; collider ids
        // map to the page index in _idToTab (handled in Tick alongside the row colliders).
        private void BuildTabs(float yTop, float zQuad, float zText)
        {
            string[] names = { "Settings", "HUD", "Lobbies" };
            int count = names.Length;
            float usableW = PANEL_W - 0.04f;
            float tabW = usableW / count;
            float startX = -usableW * 0.5f + tabW * 0.5f;
            float cy = yTop - TAB_H * 0.5f;
            for (int p = 0; p < count; p++)
            {
                bool active = (int)_page == p;
                float tx = startX + p * tabW;
                var go = MakeQuad(_root.transform, new Vector3(tx, cy, zQuad),
                                  new Vector3(tabW - 0.008f, TAB_H - 0.006f, 1f), active ? C_ROW_HOVER : C_ROW, true);
                var mc = go.GetComponent<MeshCollider>();
                _idToTab[mc != null ? mc.GetInstanceID() : go.GetInstanceID()] = p;
                MakeText(_root.transform, names[p], TextAlignmentOptions.Center,
                         new Vector3(tx, cy, zText), new Vector2(tabW - 0.02f, TAB_H * 0.62f),
                         active ? C_VALUE : C_LABEL, active);
            }
        }

        private void SwitchPage(Page p)
        {
            if (p == _page || _rig == null) return;
            _page = p;
            _scroll = 0;
            var rig = _rig;
            Destroy();
            try { Build(rig); }
            catch (Exception e) { Log.LogWarning("[menu] tab switch: " + e.Message); Destroy(); _open = false; return; }
            _hoverIndex = -1;
            _prevTrigger = true;   // swallow the trigger pull that switched tabs
        }

        private GameObject MakeQuad(Transform parent, Vector3 localPos, Vector3 scale, Color color, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "vrmenu_quad";
            go.layer = _layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;

            var mc = go.GetComponent<MeshCollider>();
            if (!collider && mc != null) UnityEngine.Object.Destroy(mc);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.material = MakeMat(color);
            return go;
        }

        // World-space TMP sized by AUTO-FIT to its rect (metres), not by a fontSize guess: TMP's
        // fontSize is in font units (not metres), so a literal "0.026" rendered nearly invisible.
        // Auto-sizing grows the text to fill the box, so text height ≈ rect height — deterministic.
        private TextMeshPro MakeText(Transform parent, string s, TextAlignmentOptions align,
                                     Vector3 localPos, Vector2 size, Color color, bool bold)
        {
            var go = new GameObject("vrmenu_text");
            go.layer = _layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var tmp = go.AddComponent<TextMeshPro>();
            if (_font != null) tmp.font = _font;
            tmp.alignment = align;
            tmp.color = color;
            tmp.richText = false;
            tmp.enableWordWrapping = false;
            if (bold) tmp.fontStyle = FontStyles.Bold;
            tmp.rectTransform.sizeDelta = size;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 0.01f;
            tmp.fontSizeMax = 300f;
            tmp.text = s;
            tmp.ForceMeshUpdate(false, false);
            return tmp;
        }

        private static Material MakeMat(Color color)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            var m = new Material(sh);
            SetMat(m, color);
            try { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); } catch { } // draw both sides
            return m;
        }

        private static void SetMat(Material m, Color c)
        {
            if (m == null) return;
            try { m.color = c; } catch { }
            try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
            try { if (m.HasProperty("_Color")) m.SetColor("_Color", c); } catch { }
        }

        private void EnsureFont()
        {
            if (_font != null) return;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var t = arr[i].TryCast<TMP_Text>();
                        if (t != null && t.font != null) { _font = t.font; break; }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[menu] font search: " + e.Message); }
            if (_font == null)
            {
                try { _font = TMP_Settings.defaultFontAsset; } catch { }
            }
            if (_font == null) Log.LogWarning("[menu] no TMP font found — text may not render.");
        }

        // Render the menu on a layer the eye cameras actually draw. The eye cameras copy Main Camera's
        // culling mask, so use a layer that's in it (default 0 = the world layer the player sees).
        private static int ResolveLayer() => 0;

        private void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _rows.Clear();
            _idToRow.Clear();
            _idToTab.Clear();
            _hoverIndex = -1;
            _prevTrigger = false;
        }
    }
}
