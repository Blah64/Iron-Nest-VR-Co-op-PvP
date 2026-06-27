using System;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP feedback effects — the "your team got hit" and match-result cues, layered across both render modes:
    ///   • a world-space EXPLOSION (the base game's own counter-battery cinematic impact — VFX + sound) lands near the
    ///     struck turret via <see cref="PvpMatch.SpawnIncomingImpact"/>; world-space ⇒ shows in the VR eyes AND the
    ///     flat camera. The thematically-correct effect: an opponent's hit = an incoming round on your position.
    ///   • a text cue on the shared <see cref="Notify"/> toast (head-locked card in VR, top box on flatscreen), so one
    ///     call reaches a headset player and a flatscreen player.
    ///   • a flatscreen red screen-flash for desktop punch (renders to the desktop mirror only — VR isn't full-red-
    ///     flashed, which is comfort-safe; the headset's cue is the explosion + card).
    ///
    /// Fired on a TEAM-HEALTH DROP, so every member of a team feels each hit: the captain triggers it from
    /// <see cref="PvpPlayers.ApplyTeamDamage"/> and non-captains from <see cref="PvpPlayers.AdoptTeamHealth"/> when the
    /// captain's keyframe shows a lower pool. Match result fires once when <see cref="PvpPlayers"/> latches Won/Lost.
    ///
    /// Gated on <see cref="Config.PvpActive"/> (a PvP lobby) ⇒ co-op/solo untouched. SHIPPING game-feel, NOT a dev tool
    /// — deliberately not behind <c>#if !PUBLIC_BUILD</c>. Still a follow-up: a VR eye red-flash/vignette
    /// (CameraRig has the head-locked overlay machinery) + comfort-gated camera shake.
    /// </summary>
    internal static class PvpEffects
    {
        private const float FlashSec = 0.45f;   // flatscreen red-flash duration
        private static float _flashUntil;
        private static Texture2D _px;

        // The whole team took a hit. dmg = HP lost (already clamped), newHealth = remaining shared team HP.
        public static void OnTeamHit(int dmg, int newHealth)
        {
            try
            {
                if (!Config.PvpActive || dmg <= 0) return;
                Notify.Show(newHealth > 0 ? $"TEAM HIT   -{dmg}   (hp {newHealth})" : "TEAM DOWN");
                _flashUntil = Time.unscaledTime + FlashSec;
                try { PvpMatch.SpawnIncomingImpact(); } catch { }   // the game's own explosion VFX+sound (VR + flatscreen)
            }
            catch { }
        }

        // Match decided — announce the result (once; PvpPlayers latches it). The flatscreen HUD also keeps a
        // persistent banner; this is the VR-visible + toast cue.
        public static void OnMatchResult(bool won)
        {
            try { if (Config.PvpActive) Notify.Show(won ? "VICTORY  -  your team won" : "DEFEAT  -  your team lost"); }
            catch { }
        }

        // Flatscreen red screen-flash, fading out. Drawn from VrManager.OnGUI (desktop mirror only). Peaks at ~0.45
        // alpha then fades to 0 over FlashSec; a tinted full-screen 1x1 quad (cheap, no shader).
        public static void DrawFlat()
        {
            try
            {
                if (!Config.PvpActive) return;
                float now = Time.unscaledTime;
                if (now >= _flashUntil) return;
                var tex = Px(); if (tex == null) return;
                float a = Mathf.Clamp01((_flashUntil - now) / FlashSec) * 0.45f;
                var prev = GUI.color;
                GUI.color = new Color(0.85f, 0.05f, 0.05f, a);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), tex);
                GUI.color = prev;
            }
            catch { }
        }

        private static Texture2D Px()
        {
            if (_px != null) return _px;
            try
            {
                _px = new Texture2D(1, 1);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
                _px.hideFlags = HideFlags.HideAndDontSave;
            }
            catch { _px = null; }
            return _px;
        }
    }
}
