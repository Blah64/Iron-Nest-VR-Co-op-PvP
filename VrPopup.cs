using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Silk.NET.OpenXR;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IronNestVR
{
    /// <summary>
    /// Makes the game's screen-space confirmation popups usable in VR. The game shows modal popups — the
    /// first-launch "I understand." language/disclaimer box, the exit-mission / exit-to-main-menu
    /// confirmations, etc. — on a screen-space canvas ("Screenspace Popup Confirmation": a "Popup title
    /// (TMP)", a "Popup content (TMP)", and uGUI buttons like "ButtonConfirm" / "ButtonBack"). That canvas
    /// never renders into the VR eye RenderTextures, so a headset player can't SEE the popup and — worse —
    /// it silently blocks input until dismissed.
    ///
    /// This driver detects an active popup, mirrors its title + body into a world-space TMP panel floating
    /// in front of the player (the same approach as <see cref="VrSettingsMenu"/>), and renders one
    /// laser-pressable button per real popup button. Pressing a VR button invokes the REAL uGUI button's
    /// <c>onClick</c> (so it runs exactly the game's own wired action — accept, cancel, exit…). If a popup
    /// is a <see cref="UILocalisationPopup"/> and no buttons are found, it falls back to that component's
    /// public <c>OnAcceptCurrent()</c> / <c>OnCancel()</c>.
    ///
    /// While a popup is up, <see cref="VrManager"/> gives this priority over locomotion / grab / cockpit
    /// clicks (like the settings menu), so the trigger drives the popup. Driven from the VR frame loop
    /// only — flatscreen is untouched.
    /// </summary>
    internal sealed class VrPopup
    {
        private static ManualLogSource Log => Plugin.Logger;

        private sealed class Btn
        {
            public string Label;
            public System.Action Act;
            public GameObject Go;
            public Material Mat;
            public int ColliderId;
        }

        // Layout (metres at PopupScale = 1).
        private const float PANEL_W = 0.66f;
        private const float PAD = 0.035f;
        private const float TITLE_H = 0.078f;
        private const float CONTENT_H = 0.26f;
        private const float BTN_H = 0.064f;
        private const float BTN_GAP = 0.014f;
        private const float GAP = 0.02f;

        private static readonly Color C_BG = new Color(0.05f, 0.06f, 0.08f, 0.97f);
        private static readonly Color C_BTN = new Color(0.15f, 0.30f, 0.55f, 1f);
        private static readonly Color C_BTN_HOVER = new Color(0.22f, 0.50f, 0.90f, 1f);
        private static readonly Color C_TITLE = new Color(1f, 0.86f, 0.45f, 1f);
        private static readonly Color C_BODY = new Color(0.92f, 0.95f, 0.99f, 1f);

        private readonly List<Btn> _btns = new List<Btn>();
        private readonly Dictionary<int, int> _idToBtn = new Dictionary<int, int>();

        private GameObject _root;
        private TMP_FontAsset _font;
        private int _layer;
        private bool _active;
        private int _hoverIndex = -1;
        private bool _prevTrigger;
        private float _nextScan;
        private float _cooldownUntil;
        private string _sig;          // signature of the popup we built from; rebuild on change

        public bool Active => _active;

        // Called every focused VR frame. Detects/refreshes the popup mirror and operates it with the laser.
        public void Tick(VrInput input, CameraRig rig)
        {
            if (!Config.PopupVrEnabled) { if (_active) Teardown(); return; }
            try
            {
                if (Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + 0.25f;
                    Detect(rig);
                }
                if (!_active || _root == null) return;
                Operate(input, rig);
            }
            catch (Exception e)
            {
                Log.LogWarning("[popup] tick: " + e.Message);
                Teardown();
            }
        }

        public void Dispose() { Teardown(); }

        // ---------------- detection ----------------

        private void Detect(CameraRig rig)
        {
            if (Time.unscaledTime < _cooldownUntil) return;   // just acted; let the game settle

            TMP_Text title = null, content = null;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None);
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var t = arr[i].TryCast<TMP_Text>();
                        if (t == null) continue;
                        bool on = false; try { on = t.isActiveAndEnabled; } catch { }
                        if (!on) continue;
                        string n = SafeName(t).ToLowerInvariant();
                        if (title == null && n.Contains("popup") && n.Contains("title")) title = t;
                        else if (content == null && n.Contains("popup") && n.Contains("content")) content = t;
                    }
            }
            catch (Exception e) { Log.LogWarning("[popup] scan: " + e.Message); }

            // Find the popup root: prefer the "...Confirmation" prefab ancestor, else the highest "Popup..."
            // ancestor. Buttons are collected ACTIVE-only, so even a shared canvas root yields only the live
            // popup's buttons.
            TMP_Text marker = title != null ? title : content;
            Transform root = marker != null ? FindRoot(marker.transform) : null;

            // Localisation popup fallback (its title/content may not be named the usual way).
            UILocalisationPopup loc = null;
            if (root == null)
            {
                try
                {
                    var la = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<UILocalisationPopup>(), FindObjectsSortMode.None);
                    if (la != null && la.Length > 0)
                    {
                        loc = la[0].TryCast<UILocalisationPopup>();
                        if (loc != null && loc.isActiveAndEnabled) root = FindRoot(loc.transform);
                    }
                }
                catch { }
            }

            if (root == null) { if (_active) Teardown(); return; }

            string titleStr = title != null ? VrText.Strip(SafeText(title)) : "";
            string bodyStr = content != null ? VrText.Strip(SafeText(content)) : "";

            // Gather the real buttons under the popup root (active + interactable).
            var found = new List<(string label, System.Action act)>();
            try
            {
                var btns = root.GetComponentsInChildren<Button>(false);
                if (btns != null)
                    for (int i = 0; i < btns.Length; i++)
                    {
                        var b = btns[i];
                        if (b == null) continue;
                        bool ok = false; try { ok = b.isActiveAndEnabled && b.interactable; } catch { }
                        if (!ok) continue;
                        string label = ButtonLabel(b);
                        var captured = b;
                        found.Add((label, () => { try { captured.onClick.Invoke(); } catch (Exception e) { Log.LogWarning("[popup] onClick: " + e.Message); } }));
                    }
            }
            catch (Exception e) { Log.LogWarning("[popup] buttons: " + e.Message); }

            // No buttons but it's a localisation popup → use its public accept/cancel methods.
            if (found.Count == 0 && loc == null)
            {
                try
                {
                    var la = root.GetComponentsInChildren<UILocalisationPopup>(false);
                    if (la != null && la.Length > 0) loc = la[0];
                }
                catch { }
            }
            if (found.Count == 0 && loc != null)
            {
                var lp = loc;
                found.Add(("I understand.", () => { try { lp.OnAcceptCurrent(); } catch (Exception e) { Log.LogWarning("[popup] accept: " + e.Message); } }));
                found.Add(("Back", () => { try { lp.OnCancel(); } catch (Exception e) { Log.LogWarning("[popup] cancel: " + e.Message); } }));
            }

            if (found.Count == 0)
            {
                // A popup is up but we found no way to dismiss it — surface it anyway (read-only) so at least
                // the text is visible, with a note. (Should be rare; logged for diagnosis.)
                Log.LogWarning($"[popup] active popup '{SafeName(root)}' has no pressable buttons.");
            }

            string sig = root.GetInstanceID() + "|" + titleStr + "|" + bodyStr + "|" + found.Count;
            if (_active && sig == _sig) return;   // same popup, no change

            Build(rig, titleStr, bodyStr, found);
            _sig = sig;
            Log.LogInfo($"[popup] showing '{SafeName(root)}' — title='{titleStr}' buttons={found.Count} [{string.Join(", ", found.ConvertAll(f => f.label))}]");
        }

        private static Transform FindRoot(Transform from)
        {
            Transform confirmation = null, highestPopup = null, cur = from;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                string n = SafeName(cur).ToLowerInvariant();
                if (n.Contains("confirmation")) confirmation = cur;
                if (n.Contains("popup")) highestPopup = cur;
                cur = cur.parent;
            }
            if (confirmation != null) return confirmation;
            if (highestPopup != null) return highestPopup;
            return from.parent != null ? from.parent : from;
        }

        private static string ButtonLabel(Button b)
        {
            try
            {
                var tmps = b.GetComponentsInChildren<TMP_Text>(true);
                if (tmps != null)
                    for (int i = 0; i < tmps.Length; i++)
                    {
                        string s = VrText.Strip(SafeText(tmps[i]));
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
            }
            catch { }
            try
            {
                var txts = b.GetComponentsInChildren<Text>(true);
                if (txts != null)
                    for (int i = 0; i < txts.Length; i++)
                    {
                        string s = null; try { s = txts[i].text; } catch { }
                        s = VrText.Strip(s);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
            }
            catch { }
            return SafeName(b);
        }

        // ---------------- build ----------------

        private void Build(CameraRig rig, string title, string body, List<(string label, System.Action act)> buttons)
        {
            Teardown();
            EnsureFont();
            _layer = 0;
            int nb = Mathf.Max(1, buttons.Count);
            float panelH = PAD + TITLE_H + GAP + CONTENT_H + GAP + nb * BTN_H + (nb - 1) * BTN_GAP + PAD;

            rig.TryGetHeadPose(out var hp, out var hr);
            if (!rig.TryGetHeadBasis(out var fwd, out _))
            {
                fwd = hr * Vector3.forward; fwd.y = 0f;
                fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            }
            Vector3 pos = hp + fwd * Config.PopupDistance + Vector3.up * Config.PopupHeightOffset;
            Quaternion rot = Quaternion.LookRotation((pos - hp).normalized, Vector3.up);
            if (Config.PopupFlip) rot *= Quaternion.Euler(0f, 180f, 0f);

            _root = new GameObject("IronNestVR_Popup");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;
            _root.transform.SetPositionAndRotation(pos, rot);
            _root.transform.localScale = Vector3.one * Mathf.Max(0.2f, Config.PopupScale);
            _root.layer = _layer;

            const float Z_QUAD = -0.004f, Z_TEXT = -0.009f;
            float innerW = PANEL_W - 0.07f;

            // Background.
            MakeQuad(Vector3.zero, new Vector3(PANEL_W + 0.02f, panelH, 1f), C_BG, false);

            float top = panelH * 0.5f - PAD;

            // Title.
            float titleY = top - TITLE_H * 0.5f;
            MakeText(string.IsNullOrEmpty(title) ? "Message" : title, TextAlignmentOptions.Center,
                     new Vector3(0f, titleY, Z_TEXT), new Vector2(innerW, TITLE_H * 0.85f), C_TITLE, true, false);
            float y = top - TITLE_H - GAP;

            // Body (wrapped, top-aligned, fills the content box).
            float contentY = y - CONTENT_H * 0.5f;
            MakeText(body ?? "", TextAlignmentOptions.Top,
                     new Vector3(0f, contentY, Z_TEXT), new Vector2(innerW, CONTENT_H), C_BODY, false, true);
            y -= CONTENT_H + GAP;

            // Buttons (vertical stack).
            _btns.Clear();
            _idToBtn.Clear();
            for (int i = 0; i < buttons.Count; i++)
            {
                float cy = y - BTN_H * 0.5f;
                var go = MakeQuad(new Vector3(0f, cy, Z_QUAD), new Vector3(PANEL_W - 0.06f, BTN_H, 1f), C_BTN, true);
                var mr = go.GetComponent<MeshRenderer>();
                var mc = go.GetComponent<MeshCollider>();
                var row = new Btn
                {
                    Label = buttons[i].label,
                    Act = buttons[i].act,
                    Go = go,
                    Mat = mr != null ? mr.material : null,
                    ColliderId = mc != null ? mc.GetInstanceID() : go.GetInstanceID()
                };
                _idToBtn[row.ColliderId] = _btns.Count;
                _btns.Add(row);

                MakeText(buttons[i].label, TextAlignmentOptions.Center,
                         new Vector3(0f, cy, Z_TEXT), new Vector2(PANEL_W - 0.1f, BTN_H * 0.6f), Color.white, true, false);
                y -= BTN_H + BTN_GAP;
            }

            _active = true;
            _hoverIndex = -1;
            _prevTrigger = true;   // swallow a trigger that's still held from whatever opened the popup
        }

        // ---------------- operate ----------------

        private void Operate(VrInput input, CameraRig rig)
        {
            var origin = rig.OriginTransform;
            int hoverRow = -1;
            Vector3 hitPoint = Vector3.zero;
            bool gotHit = false;

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
                    for (int i = 0; i < hits.Length; i++)
                    {
                        var col = hits[i].collider;
                        if (col == null) continue;
                        if (_idToBtn.TryGetValue(col.GetInstanceID(), out int ri) && hits[i].distance < best)
                        { best = hits[i].distance; hoverRow = ri; hitPoint = hits[i].point; gotHit = true; }
                    }
            }

            SetHover(hoverRow);

            bool held = input.TriggerHeld;
            if (held && !_prevTrigger && gotHit && hoverRow >= 0 && hoverRow < _btns.Count)
            {
                var row = _btns[hoverRow];
                input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
                Log.LogInfo("[popup] pressed '" + row.Label + "'");
                try { row.Act?.Invoke(); } catch (Exception e) { Log.LogWarning("[popup] act: " + e.Message); }
                // Hide our mirror immediately for responsiveness, but stay "active" through a short cooldown so
                // the modal suppression holds until the player releases the trigger (else the still-held trigger
                // would leak a stray cockpit click the moment the popup closes). Detection is paused during the
                // cooldown, then re-shows only if a follow-up popup is up.
                _cooldownUntil = Time.unscaledTime + 0.4f;
                DestroyVisual();
                return;
            }
            _prevTrigger = held;
            // hitPoint is unused beyond hover, but kept for parity with the menu's row hit logic.
            _ = hitPoint;
        }

        private void SetHover(int idx)
        {
            if (idx == _hoverIndex) return;
            if (_hoverIndex >= 0 && _hoverIndex < _btns.Count) SetMat(_btns[_hoverIndex].Mat, C_BTN);
            if (idx >= 0 && idx < _btns.Count) SetMat(_btns[idx].Mat, C_BTN_HOVER);
            _hoverIndex = idx;
        }

        // ---------------- primitives (mirror VrSettingsMenu) ----------------

        private GameObject MakeQuad(Vector3 localPos, Vector3 scale, Color color, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "vrpopup_quad";
            go.layer = _layer;
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;

            var mc = go.GetComponent<MeshCollider>();
            if (!collider && mc != null) UnityEngine.Object.Destroy(mc);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.material = MakeMat(color);
            return go;
        }

        private TextMeshPro MakeText(string s, TextAlignmentOptions align, Vector3 localPos, Vector2 size,
                                     Color color, bool bold, bool wrap)
        {
            var go = new GameObject("vrpopup_text");
            go.layer = _layer;
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var tmp = go.AddComponent<TextMeshPro>();
            if (_font != null) tmp.font = _font;
            tmp.alignment = align;
            tmp.color = color;
            tmp.richText = false;
            tmp.enableWordWrapping = wrap;
            if (bold) tmp.fontStyle = FontStyles.Bold;
            tmp.rectTransform.sizeDelta = size;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 0.01f;
            tmp.fontSizeMax = 300f;
            tmp.text = s ?? "";
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
            try { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); } catch { }
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
            _font = VrText.Font;
        }

        // Destroy the world-space panel + button state WITHOUT clearing _active — used right after a press so
        // the modal suppression survives the trigger-release window (see Operate).
        private void DestroyVisual()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _btns.Clear();
            _idToBtn.Clear();
            _hoverIndex = -1;
            _prevTrigger = false;
            _sig = null;
        }

        private void Teardown()
        {
            DestroyVisual();
            _active = false;
        }

        // Span-safe name read (the .name getter hits a broken injected path in this IL2CPP build).
        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.GetName() : ""; } catch { }
            try { return o != null ? o.name : ""; } catch { return ""; }
        }

        private static string SafeText(TMP_Text t)
        {
            try { return t != null ? t.text : null; } catch { return null; }
        }
    }
}
