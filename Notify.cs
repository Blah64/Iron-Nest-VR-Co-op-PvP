using System;
using BepInEx.Logging;
using TMPro;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Transient on-screen toast — a short message that appears, holds for <see cref="Config.NotifySeconds"/>,
    /// then fades out, WITHOUT pulling focus or stealing input (so it never interrupts play). Used to announce
    /// co-op events (a peer joining/leaving). Two render paths, because flatscreen and VR don't share UI:
    ///  - Flatscreen: a small IMGUI box near the top of the screen (drawn from <see cref="VrManager"/>'s OnGUI;
    ///    just GUI.Box, no cursor unlock / no look-freeze, so it's purely cosmetic and non-blocking).
    ///  - VR: a world-space TMP card floating in front of the head, billboarded to face the player.
    /// The message is set once (from the net thread's Update tick); each render path picks it up.
    /// </summary>
    internal static class Notify
    {
        private static ManualLogSource Log => Plugin.Logger;

        private static string _msg;
        private static float _until;

        private static GameObject _vrRoot;
        private static TextMeshPro _vrText;

        private static bool Active => _msg != null && Time.unscaledTime < _until;

        public static void Show(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            _msg = msg;
            _until = Time.unscaledTime + Mathf.Max(0.5f, Config.NotifySeconds);
            Log.LogInfo("[notify] " + msg);
        }

        public static void PeerJoined(string name, bool host)
        {
            if (!Config.CoopJoinNotify) return;
            string who = string.IsNullOrEmpty(name) ? "A player" : name;
            Show(host ? who + " joined" : "Connected to " + who);
        }

        public static void PeerLeft(string name)
        {
            if (!Config.CoopJoinNotify) return;
            string who = string.IsNullOrEmpty(name) ? "A player" : name;
            Show(who + " left");
        }

        // Flatscreen render — called from OnGUI. A centred box near the top; no interaction, so it can't
        // grab the cursor or freeze look (unlike the lobby panel). Harmless in VR (only the desktop mirror
        // shows IMGUI; the headset uses TickVr instead).
        public static void DrawFlat()
        {
            if (!Active) return;
            try
            {
                const float w = 440f, h = 46f;
                float x = (Screen.width - w) * 0.5f;
                GUI.Box(new Rect(x, 64f, w, h), _msg);
            }
            catch { }
        }

        // VR render — called every VR frame with the head pose. Floats a world-space card in front of the
        // player and faces it toward them; hidden when no message is active.
        public static void TickVr(CameraRig rig)
        {
            if (rig == null) return;
            try
            {
                if (!Active)
                {
                    if (_vrRoot != null && _vrRoot.activeSelf) _vrRoot.SetActive(false);
                    return;
                }

                EnsureVr();
                if (_vrText != null && _vrText.text != _msg)
                {
                    _vrText.text = _msg;
                    _vrText.ForceMeshUpdate(false, false);
                }

                if (!rig.TryGetHeadPose(out var hp, out var hr)) return;
                if (!rig.TryGetHeadBasis(out var fwd, out _))
                {
                    fwd = hr * Vector3.forward; fwd.y = 0f;
                    fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                }
                Vector3 pos = hp + fwd * Config.NotifyDistance + Vector3.up * Config.NotifyHeight;
                _vrRoot.transform.SetPositionAndRotation(pos, VrText.FaceViewer(pos, hp));
                _vrRoot.transform.localScale = Vector3.one * Mathf.Max(0.2f, Config.NotifyScale);
                if (!_vrRoot.activeSelf) _vrRoot.SetActive(true);
            }
            catch (Exception e) { Log.LogWarning("[notify] vr: " + e.Message); }
        }

        private static void EnsureVr()
        {
            if (_vrRoot != null) return;
            _vrRoot = new GameObject("IronNestVR_Notify");
            UnityEngine.Object.DontDestroyOnLoad(_vrRoot);
            _vrRoot.hideFlags = HideFlags.HideAndDontSave;

            // Background card, then the text slightly toward the viewer (more -Z) so it draws in front.
            var bg = VrText.Panel(_vrRoot.transform, new Vector2(0.55f, 0.13f), new Color(0.05f, 0.06f, 0.08f, 0.92f), 0);
            bg.transform.localPosition = Vector3.zero;
            _vrText = VrText.Make(_vrRoot.transform, _msg ?? "", new Vector2(0.50f, 0.085f),
                                  new Color(0.85f, 0.95f, 1f, 1f), 0, true);
            _vrText.transform.localPosition = new Vector3(0f, 0f, -0.006f);
        }

        // Called on VR teardown so the card doesn't linger.
        public static void DisposeVr()
        {
            try { if (_vrRoot != null) UnityEngine.Object.Destroy(_vrRoot); } catch { }
            _vrRoot = null; _vrText = null;
        }
    }
}
