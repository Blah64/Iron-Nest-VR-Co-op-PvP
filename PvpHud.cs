using System;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// PvP DEV HUD (flatscreen OnGUI) — a TEMPORARY readout to test the Phase 2 shot/damage lane before real target
    /// acquisition is chosen (teleprinter fire mission / always-visible marker / scout recon — see PLAN-pvp.md).
    /// Shows my HP, the opponent's map grid + HP, where my last shell landed vs the target (so you range shells onto
    /// them), and hit counters. Non-public builds only, and only while in a live PvP mission. Replace once the real
    /// acquisition mechanic lands.
    /// </summary>
    internal static class PvpHud
    {
        public static void DrawFlat()
        {
#if !PUBLIC_BUILD
            try
            {
                if (!Config.PvpActive) return;
                var mm = MissionManager.Instance;
                if (mm == null || mm.CurrentPhase != MissionManager.GamePhase.MissionActive) return;

                const float w = 340f, h = 150f, rh = 20f;
                float x = 12f, y = Screen.height - h - 12f;
                GUI.Box(new Rect(x, y, w, h), "PvP DUEL  (dev readout)");
                float lx = x + 10f, lw = w - 20f, cy = y + 26f;

                GUI.Label(new Rect(lx, cy, lw, rh), $"You: hp {PvpPlayers.MyHealth}/{PvpPlayers.MaxHealth}{(PvpPlayers.Eliminated ? "   *ELIMINATED*" : "")}"); cy += rh;

                Vector2 myg = PvpPlayers.MyGridPublic;
                GUI.Label(new Rect(lx, cy, lw, rh), $"Your grid (placeholder): ({myg.x:0.0}, {myg.y:0.0})"); cy += rh;

                if (PvpPlayers.TryGetFirstEnemy(out var eg, out var ehp))
                {
                    GUI.Label(new Rect(lx, cy, lw, rh), $"ENEMY: grid ({eg.x:0.0}, {eg.y:0.0})   hp {ehp}"); cy += rh;
                    if (PvpCombat.LastImpactTime > 0f)
                    {
                        Vector2 li = PvpCombat.LastImpact;
                        float d = Vector2.Distance(li, eg);
                        GUI.Label(new Rect(lx, cy, lw, rh), $"Last impact: ({li.x:0.0}, {li.y:0.0})   off by {d:0.0}{(PvpCombat.LastImpactHit ? "   HIT!" : "")}"); cy += rh;
                    }
                    else { GUI.Label(new Rect(lx, cy, lw, rh), "Last impact: (fire a ranging shot toward the enemy grid)"); cy += rh; }
                }
                else { GUI.Label(new Rect(lx, cy, lw, rh), "ENEMY: (waiting for opponent…)"); cy += rh; }

                GUI.Label(new Rect(lx, cy, lw, rh), $"hits dealt {PvpCombat.Dealt}   taken {PvpCombat.Taken}");
            }
            catch { }
#endif
        }
    }
}
