using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Mirrors the game's screen-space hover tooltip (<c>HoverTooltip</c> — the "[E] interact" /
    /// "Drink", "Pick Up" prompt that follows the crosshair) into a world-space TMP card so it's
    /// visible in the headset. The game tooltip is a <c>RectTransform</c> positioned by projecting a
    /// world anchor through a screen camera (<see cref="CockpitInteractor"/> already repoints its
    /// <c>raycastCamera</c> down the controller so hover DETECTION follows the laser) — but that rect
    /// lives on a screen-space canvas that never renders into the eye RenderTextures, so the prompt is
    /// invisible in VR. We read the tooltip's live text + its <c>_worldAnchor</c> and float a small
    /// billboard card just above the hovered object.
    ///
    /// Self-contained and read-only against the game: it never moves or disables the real tooltip, just
    /// reflects it. Driven from the VR frame loop only, so flatscreen is untouched.
    /// </summary>
    internal sealed class VrTooltip
    {
        private static ManualLogSource Log => Plugin.Logger;

        private HoverTooltip _tip;
        private float _nextFind;

        private GameObject _root;
        private TextMeshPro _text;
        private string _lastText;

        public void Tick(CameraRig rig)
        {
            if (!Config.TooltipVrEnabled || rig == null) { Hide(); return; }
            try
            {
                EnsureTip();

                bool visible = false;
                Transform anchor = null;
                if (_tip != null)
                {
                    try { visible = _tip._visible; } catch { }
                    try { anchor = _tip._worldAnchor; } catch { }
                }
                if (!visible || anchor == null) { Hide(); return; }

                string txt = ReadText(_tip);
                if (string.IsNullOrEmpty(txt)) { Hide(); return; }

                if (!rig.TryGetHeadPose(out var hp, out _)) { Hide(); return; }
                Vector3 anchorPos = anchor.position;
                float maxD = Mathf.Max(0.5f, Config.TooltipMaxDistance);
                if ((anchorPos - hp).sqrMagnitude > maxD * maxD) { Hide(); return; }

                EnsureRoot();
                if (txt != _lastText)
                {
                    _text.text = txt;
                    _text.ForceMeshUpdate(false, false);
                    _lastText = txt;
                    Log.LogInfo("[tooltip] " + txt);
                }

                Vector3 pos = anchorPos + Vector3.up * Config.TooltipHeightOffset;
                _root.transform.SetPositionAndRotation(pos, VrText.FaceViewer(pos, hp));
                _root.transform.localScale = Vector3.one * Mathf.Max(0.1f, Config.TooltipScale);
                if (!_root.activeSelf) _root.SetActive(true);
            }
            catch (Exception e) { Log.LogWarning("[tooltip] " + e.Message); Hide(); }
        }

        // Cache the (effectively singleton) HoverTooltip. A scene change destroys it; refind on a throttle
        // whenever the cached one has gone away.
        private void EnsureTip()
        {
            if (_tip != null) return;
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 0.5f;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<HoverTooltip>(), FindObjectsSortMode.None);
                if (arr != null && arr.Length > 0) _tip = arr[0].TryCast<HoverTooltip>();
            }
            catch { }
        }

        // Faithful text: read the TMP child(ren) under the tooltip's own rect (what the flat player sees),
        // stripped of rich-text/sprite markup. Fall back to the hovered Interactable's resolved prompt when
        // the rect has no readable TMP yet.
        private static string ReadText(HoverTooltip tip)
        {
            try
            {
                Transform rect = null;
                try { rect = tip._rectTransform; } catch { }
                Transform scan = rect != null ? rect : tip.transform;
                var tmps = scan.GetComponentsInChildren<TMP_Text>(true);
                if (tmps != null)
                {
                    string best = null;
                    for (int i = 0; i < tmps.Length; i++)
                    {
                        var t = tmps[i];
                        if (t == null) continue;
                        string s = null;
                        try { s = t.text; } catch { }
                        s = VrText.Strip(s);
                        if (string.IsNullOrEmpty(s)) continue;
                        // Prefer the longest non-empty label (the prompt) over a single key glyph.
                        if (best == null || s.Length > best.Length) best = s;
                    }
                    if (!string.IsNullOrEmpty(best)) return best;
                }
            }
            catch { }

            // Fallback: resolve the prompt off the hovered interactable directly.
            try
            {
                Transform anchor = tip._worldAnchor;
                if (anchor != null)
                {
                    var it = anchor.GetComponentInParent<Interactable>();
                    if (it != null) return VrText.Strip(it.GetResolvedPrompt());
                }
            }
            catch { }
            return null;
        }

        private void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("IronNestVR_Tooltip");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.hideFlags = HideFlags.HideAndDontSave;

            // Dark card + bright label, sized like the join toast but a touch smaller.
            var bg = VrText.Panel(_root.transform, new Vector2(0.46f, 0.11f), new Color(0.05f, 0.06f, 0.08f, 0.9f), 0);
            bg.transform.localPosition = Vector3.zero;
            _text = VrText.Make(_root.transform, _lastText ?? "", new Vector2(0.42f, 0.075f),
                                new Color(0.95f, 0.97f, 1f, 1f), 0, true);
            _text.transform.localPosition = new Vector3(0f, 0f, -0.006f);
            _lastText = null; // force a refresh of the text on the next Tick
        }

        public void Hide()
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
        }

        public void Dispose()
        {
            try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
            _root = null; _text = null; _tip = null; _lastText = null;
        }
    }
}
