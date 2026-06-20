using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Shared world-space 3D TextMeshPro helpers (font discovery + label/quad builders) for the lightweight
    /// VR overlays that aren't the settings menu: the remote-player name tag (<see cref="RemoteAvatar"/>) and
    /// the join-notification toast (<see cref="Notify"/>). Same approach proven in <see cref="VrSettingsMenu"/>
    /// — a 3D <c>TextMeshPro</c> (not the screen-space UGUI variant) on layer 0 renders into BOTH the VR eye
    /// RenderTextures and the flat Main Camera. The settings menu keeps its own copy of these builders; this is
    /// the shared one so the overlays don't have to depend on the menu.
    ///
    /// Orientation note (matches the menu): TMP reads correctly when its local +Z points AWAY from the viewer,
    /// so a billboard faces the viewer with <c>Quaternion.LookRotation(textPos - viewerPos)</c>.
    /// </summary>
    internal static class VrText
    {
        private static ManualLogSource Log => Plugin.Logger;
        private static TMP_FontAsset _font;

        // Lazily resolve a usable font from the game's own TMP atlas (guaranteed-present shaders), falling back
        // to the TMP default. Cached for the session (font assets survive scene loads).
        public static TMP_FontAsset Font
        {
            get
            {
                if (_font != null) return _font;
                try
                {
                    var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<TMP_Text>(), FindObjectsSortMode.None);
                    if (arr != null)
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var t = arr[i].TryCast<TMP_Text>();
                            if (t != null && t.font != null) { _font = t.font; break; }
                        }
                }
                catch (Exception e) { Log.LogWarning("[vrtext] font search: " + e.Message); }
                if (_font == null) { try { _font = TMP_Settings.defaultFontAsset; } catch { } }
                return _font;
            }
        }

        // A 3D TextMeshPro auto-sized to fill `size` (metres). Auto-fit is used instead of a fontSize guess
        // because TMP fontSize is in font units, not metres (see VrSettingsMenu.MakeText).
        public static TextMeshPro Make(Transform parent, string s, Vector2 size, Color color, int layer,
                                       bool bold = false, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var go = new GameObject("vrtext");
            go.layer = layer;
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.identity;

            var tmp = go.AddComponent<TextMeshPro>();
            var f = Font;
            if (f != null) tmp.font = f;
            tmp.alignment = align;
            tmp.color = color;
            tmp.richText = false;
            tmp.enableWordWrapping = false;
            if (bold) tmp.fontStyle = FontStyles.Bold;
            tmp.rectTransform.sizeDelta = size;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 0.01f;
            tmp.fontSizeMax = 300f;
            tmp.text = s ?? "";
            tmp.ForceMeshUpdate(false, false);
            return tmp;
        }

        // A flat, double-sided, unlit coloured quad used as a label background. Its collider is stripped so it
        // never blocks the laser raycast.
        public static GameObject Panel(Transform parent, Vector2 size, Color color, int layer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "vrtext_bg";
            go.layer = layer;
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);

            try { var mc = go.GetComponent<MeshCollider>(); if (mc != null) UnityEngine.Object.Destroy(mc); } catch { }

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                var m = new Material(sh);
                try { m.color = color; } catch { }
                try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color); } catch { }
                try { if (m.HasProperty("_Color")) m.SetColor("_Color", color); } catch { }
                try { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); } catch { } // draw both sides
                mr.material = m;
            }
            return go;
        }

        // Face a world-space label toward the viewer. Yaw-only (flatten Y) keeps the label upright instead of
        // tilting when the viewer is above/below it.
        public static Quaternion FaceViewer(Vector3 labelPos, Vector3 viewerPos, bool yawOnly = true)
        {
            Vector3 dir = labelPos - viewerPos;            // +Z away from viewer => readable (see class note)
            if (yawOnly) dir.y = 0f;
            if (dir.sqrMagnitude < 1e-5f) dir = Vector3.forward;
            return Quaternion.LookRotation(dir.normalized, Vector3.up);
        }
    }
}
