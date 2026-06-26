using System;
using BepInEx.Logging;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 4 co-op: SHOOTER-AUTHORITATIVE SHELL FLIGHT (visible arc + fall-of-shot).
    ///
    /// THE PROBLEM (LAN tests 2026-06): a fired shell lands in different spots on host vs client even after aim/
    /// powder/shell sync. A long dead-end chain proved NO pre-shot input can be reliably synced, so we copy the
    /// shooter's RESOLVED shot instead. Artillery here is 2D: the shell the players SEE is a <c>ShellVisual</c> that
    /// arcs across the tactical-map board (a RectTransform) from <c>startLocalPos</c> to <c>targetLocalPos</c> and
    /// drops its crater there. Because the shell IS the visual, State_ImpactStart adjudicates the hit at wherever it
    /// lands - so map-space == board-space, and copying the visual's flight syncs the crater AND the hit for free.
    ///
    /// TIMING (what broke the earlier attempts): hasFired flips at RequestFire, BEFORE FireShell runs (announcing
    /// there shipped NaN); and the map hit point isn't resolved until the shell LANDS, seconds later (announcing
    /// there fired the peer too late / not at all). What IS known at fire time, inside FireShell, is
    /// ShellVisual.Initialize(start, target, travelTime, shell) - the whole flight. So announce from there.
    ///
    /// FLOW. SHOOTER (local shot): the ShellVisual.Initialize postfix reads the real stored flight (start, target,
    /// travelTime) and ships it in MSG_FIRE immediately. PEER: the MSG_FIRE handler stashes it as "pending" + replays
    /// RequestFire (gun recoils); the peer runs its own FireShell, and its ShellVisual.Initialize postfix overwrites
    /// startLocalPos / targetLocalPos / travelTime (and recomputes totalPathDistance) with the shooter's -> the
    /// visible arc flies the same launch point, direction, speed and curve to the identical crater.
    ///
    /// IL2CPP NOTE: we override by writing the INSTANCE FIELDS in the postfix, NOT by modifying ref value-type args
    /// in a prefix - the latter silently no-ops here (it's what made earlier builds' logs match while the screens
    /// didn't). The fields are copied VERBATIM from the shooter's own ShellVisual, so their coordinate space never
    /// matters. Dispersion is still zeroed so the shooter's own shot is itself a clean point.
    ///
    /// Adjudication stays host-authoritative via CoopImpact's MSG_IMPACT hit broadcast (we don't touch StartImpact).
    /// SCOPE: co-op lobby + peer + MissionActive only. Solo/flatscreen fire stock. See [[ironnest-aim-desync]] [[ironnest-flatscreen-parity]].
    /// </summary>
    internal static class CoopBallistics
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Dispersion stash. FireShell is a synchronous, non-reentrant native call, so a single static stash is safe.
        private static bool _stashed;
        private static GunController _gun;
        private static float _gunH, _gunV;
        private static ShellDefinition _shell;
        private static float _shH, _shV, _shSpeed;

        private static int _shots, _copied;

        // ---- SHOOTER per-shot state (reset in OnFireShellPre) ----
        private static GunController _capGun;          // the firing gun (NOT nulled by Restore) -> side at send time
        private static bool _sentThisShot;

        // ---- PEER pending (the shooter's flight to force onto our about-to-fire shell) ----
        // Set when MSG_FIRE arrives, consumed at our ShellVisual.Initialize (fire time, same FireShell), cleared in
        // OnFireShellPost. The expiry is a backstop if our replayed RequestFire is rejected; it must exceed fireDelay.
        private static bool _pendingValid;
        private static Vector2 _pendingTgt, _pendingStart;
        private static float _pendingTime;
        private static float _pendingExpiry;

        // ================= FireShell prefix/postfix (dispersion + per-shot reset) =================

        public static void OnFireShellPre(GunController __instance)
        {
            // CRITICAL: runs on EVERY shot for host, client AND solo. Must never throw into the gun state machine.
            try
            {
                Restore();                       // self-heal: undo a stash a prior throwing FireShell left zeroed
                if (!Active() || __instance == null) return;

                _gun = __instance; _capGun = __instance;
                _gunH = _gun.gunHorizontalDispersion;
                _gunV = _gun.gunVerticalDispersion;
                _gun.gunHorizontalDispersion = 0f;
                _gun.gunVerticalDispersion = 0f;

                _shell = ChamberedDef(_gun);
                if (_shell != null)
                {
                    _shH = _shell.horizontalDispersion;
                    _shV = _shell.verticalDispersion;
                    _shSpeed = _shell.shellSpeedVariationPercent;
                    _shell.horizontalDispersion = 0f;
                    _shell.verticalDispersion = 0f;
                    _shell.shellSpeedVariationPercent = 0f;
                }

                _stashed = true;
                _shots++;
                _sentThisShot = false;
            }
            catch (Exception e) { try { Log.LogWarning("[ball] pre: " + e.Message); } catch { } }
        }

        public static void OnFireShellPost()
        {
            try { Restore(); } catch (Exception e) { try { Log.LogWarning("[ball] post: " + e.Message); } catch { } }
            // The pending flight was consumed by our ShellVisual.Initialize during this FireShell; clear it so a later
            // LOCAL shot can never inherit a stale remote target.
            _pendingValid = false;
        }

        // Restore the zeroed coefficients to their authored values. Idempotent.
        private static void Restore()
        {
            if (!_stashed) return;
            try { if (_gun != null) { _gun.gunHorizontalDispersion = _gunH; _gun.gunVerticalDispersion = _gunV; } } catch { }
            try
            {
                if (_shell != null)
                {
                    _shell.horizontalDispersion = _shH;
                    _shell.verticalDispersion = _shV;
                    _shell.shellSpeedVariationPercent = _shSpeed;
                }
            }
            catch { }
            _stashed = false; _gun = null; _shell = null;
        }

        // ================= ShellVisual.Initialize — announce (shooter) / override flight (peer) =================

        // ShellVisual.Initialize(start, target, travelTime, shell) postfix. PEER: overwrite the whole flight with the
        // shooter's (instance-field writes - reliable, unlike a ref-arg prefix). SHOOTER: capture the real flight and
        // announce it to the peer NOW (fire time) - the first and only moment it exists inside FireShell.
        public static void OnShellVisualPost(ShellVisual __instance)
        {
            try
            {
                if (__instance == null) return;
                if (IsApplyingRemoteImpact)
                {
                    bool haveStart = CoopWire.Finite(_pendingStart.x) && CoopWire.Finite(_pendingStart.y);
                    try
                    {
                        __instance.targetLocalPos = _pendingTgt;          // the crater - always present
                        if (haveStart)                                    // the launch point / arc - guard against an old/short packet
                        {
                            __instance.startLocalPos = _pendingStart;
                            if (_pendingTime > 0f) __instance.travelTime = _pendingTime;
                            try { __instance.totalPathDistance = Vector2.Distance(_pendingStart, _pendingTgt); } catch { }
                            try { __instance.previousPos = _pendingStart; } catch { }
                        }
                    }
                    catch { }
                    _copied++;
                    Vector2 s, t; try { s = __instance.startLocalPos; t = __instance.targetLocalPos; } catch { return; }
                    Diagnostics.V($"[ball] shell flight now start=({s.x:0.0},{s.y:0.0}) target=({t.x:0.0},{t.y:0.0})");
                }
                else if (!_sentThisShot && Active())
                {
                    Vector2 s, t; float time;
                    try { s = __instance.startLocalPos; t = __instance.targetLocalPos; time = __instance.travelTime; } catch { return; }
                    _sentThisShot = true;
                    try { CoopControls.SendLocalShot(_capGun, t, s, time); }
                    catch (Exception e) { try { Log.LogWarning("[ball] send: " + e.Message); } catch { } }
                }
            }
            catch { }
        }

        // ================= plumbing =================

        // True while a remote shooter's flight is in force (peer side, during the replayed shell's FireShell).
        internal static bool IsApplyingRemoteImpact => _pendingValid && Config.CoopDeterministicFire && Time.unscaledTime < _pendingExpiry;

        // Peer MSG_FIRE handler: the shooter's flight to force onto our about-to-fire shell.
        internal static void SetPending(Vector2 tgt, Vector2 start, float time)
        {
            _pendingTgt = tgt; _pendingStart = start; _pendingTime = time; _pendingValid = true;
            try { _pendingExpiry = Time.unscaledTime + 6f; } catch { _pendingExpiry = 0f; }
        }

        // ================= helpers =================

        private static ShellDefinition ChamberedDef(GunController gun)
        {
            try
            {
                var bp = gun.ChamberedShellBlueprint;
                if (bp == null) return null;
                return bp.shellDefinition;
            }
            catch { return null; }
        }

        // Only act for an in-mission co-op session with a peer. Solo/host-without-peer = stock.
        private static bool Active()
        {
            if (!Config.CoopDeterministicFire) return false;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) return false;
            try { var mm = MissionManager.Instance; return mm != null && mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        // ================= diagnostics =================

        public static string Status() => $"ball: deterministic={(Active() ? "ON" : "off")} shots={_shots} copied={_copied}";
    }
}
