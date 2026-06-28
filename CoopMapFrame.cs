using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Canonical resolver for the world-space "Fire Mission Root" map frame — the transform whose LOCAL (X,Y) plane
    /// IS the tactical-map grid. Grid↔world for the turret origin and the map entities all go through this SAME frame,
    /// so the host's <c>turretBase.anchoredPosition</c> (a grid coord) reconstructs to the identical WORLD position on
    /// a client via <c>Resolve().TransformPoint(new Vector3(gridX, gridY, 0))</c> → <c>SetTurretLocation(world)</c>.
    ///
    /// This is the shared home for the FMR lookup that <see cref="PvpPlayers"/> and <see cref="CoopEntities"/> each
    /// grew privately (PvpPlayers.ResolveParent / CoopEntities.ResolveCloneParent). New co-op code uses THIS one so
    /// the turret-origin sync can never diverge from the frame the entity sync already relies on. (The PvP/entity
    /// copies are left in place to avoid editing those subsystems; this method mirrors PvpPlayers.ResolveParent's
    /// proven logic — prefer a live native EntityLocation's parent, else find "Fire Mission Root" by name.)
    /// </summary>
    internal static class CoopMapFrame
    {
        // Prefer the parent of an existing (non-PvP) EntityLocation: that is EXACTLY the container the current scene
        // parents its map markers under, so the turret shares the markers' frame. Fall back to the named root.
        public static Transform Resolve()
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<EntityLocation>(), FindObjectsSortMode.None);
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var loc = arr[i].TryCast<EntityLocation>(); if (loc == null) continue;
                    var go = loc.gameObject; if (go == null) continue;
                    string nm = null; try { nm = go.name; } catch { }
                    if (nm != null && nm.StartsWith("PvpPlayer_", StringComparison.Ordinal)) continue;   // not a map marker
                    var p = loc.transform.parent; if (p != null) return p;
                }
            }
            catch { }
            try { var fmr = GameObject.Find("Fire Mission Root"); if (fmr != null) return fmr.transform; } catch { }
            return null;
        }
    }
}
