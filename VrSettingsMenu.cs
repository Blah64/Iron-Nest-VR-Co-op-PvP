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
        private bool _open;
        private int _hoverIndex = -1;
        private bool _prevTrigger;
        private int _layer;

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
                            if (_idToRow.TryGetValue(col.GetInstanceID(), out int ri) && h.distance < best)
                            {
                                best = h.distance; hoverRow = ri; hitPoint = h.point; gotHit = true;
                            }
                        }
                    }
                }

                SetHover(hoverRow);

                bool held = input.TriggerHeld;
                if (held && !_prevTrigger && gotHit && hoverRow >= 0)
                {
                    Activate(hoverRow, hitPoint, rig);
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
            float contentH = PAD + TITLE_H + n * (ROW_H + ROW_GAP) + FOOTER_H + PAD;

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
            MakeText(_root.transform, "VR SETTINGS", TextAlignmentOptions.Center,
                     new Vector3(0f, titleY, Z_TEXT), new Vector2(PANEL_W - 0.06f, TITLE_H * 0.8f), C_LABEL, true);
            float y = top - TITLE_H;

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
            AddToggle("HUD Rotates w/View", () => Config.HudRotateWithCamera ? "On" : "Off",
                      () => Config.HudRotateWithCamera = !Config.HudRotateWithCamera);
            AddToggle("Laser Pointer", () => Config.ShowLaser ? "On" : "Off",
                      () => Config.ShowLaser = !Config.ShowLaser);

            // --- Hand tuning (live) ---
            AddToggle("Hands", () => Config.HandsEnabled ? "On" : "Off",
                      () => Config.HandsEnabled = !Config.HandsEnabled);
            AddFloat("Hand Size", () => Config.HandScale.ToString("0.00") + "x",
                     d => Config.HandScale = Clamp(Config.HandScale + d * 0.05f, 0.3f, 2.5f));
            // Calibrate: tap to arm, then hold the OPPOSITE controller's grip and move the hand into
            // place (like grabbing the clipboard); release to keep, tap again to finish.
            AddToggle("Calibrate Right Hand",
                      () => (Hands != null && Hands.CalibratingRight) ? "hold LEFT grip" : "tap",
                      () => Hands?.ToggleCalibration(true));
            AddToggle("Calibrate Left Hand",
                      () => (Hands != null && Hands.Calibrating && !Hands.CalibratingRight) ? "hold RIGHT grip" : "tap",
                      () => Hands?.ToggleCalibration(false));
            AddAction("Reset Hand Offsets", () => Hands?.ResetOffsets());
            AddToggle("Finger Curl", () => Config.FingerCurlEnabled ? "On" : "Off",
                      () => Config.FingerCurlEnabled = !Config.FingerCurlEnabled);
            AddFloat("Curl Amount", () => Mathf.RoundToInt(Config.FingerCurlMaxDeg) + " deg",
                     d => Config.FingerCurlMaxDeg = Clamp(Config.FingerCurlMaxDeg + d * 5f, 0f, 120f));
            AddFloat("Curl Axis", () => Config.FingerCurlAxis == 0 ? "X" : Config.FingerCurlAxis == 1 ? "Y" : "Z",
                     d => Config.FingerCurlAxis = ((Config.FingerCurlAxis + (d >= 0 ? 1 : 2)) % 3));
            AddToggle("Curl Direction", () => Config.FingerCurlSign >= 0f ? "+" : "-",
                      () => Config.FingerCurlSign = -Config.FingerCurlSign);

            AddAction("Recenter View", () => { _rig?.Recenter(); });
            AddAction("Close Menu", Close);
        }

        private void AddFloat(string label, Func<string> value, Action<int> adjust)
            => _rows.Add(new Row { Label = label, Kind = Kind.Float, Value = value, Adjust = adjust });

        private void AddToggle(string label, Func<string> value, System.Action toggle)
            => _rows.Add(new Row { Label = label, Kind = Kind.Toggle, Value = value, Adjust = _ => toggle() });

        private void AddAction(string label, System.Action act)
            => _rows.Add(new Row { Label = label, Kind = Kind.Action, Value = null, Adjust = _ => act() });

        private static float Clamp(float v, float lo, float hi) => Mathf.Clamp(v, lo, hi);

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
            _hoverIndex = -1;
            _prevTrigger = false;
        }
    }
}
