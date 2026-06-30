using System;
using System.Collections.Generic;
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

        // ---- PER-SIDE FIRE-INTENT QUEUE (Bug 2 fix — replaces the old single global _pendingValid flag) ----
        // The old global flag (6 s, not per-gun) mis-tagged a LOCAL shot as a peer replay whenever the OTHER player
        // was active, hijacking it onto the peer's target ("wild, not dispersion"). Instead every shot that WILL fire
        // is tagged at RequestFire time and queued PER SIDE; OnShellVisualPost consumes FIFO, so each shell gets
        // exactly its own author's decision. Probe-verified: RequestFire→FireShell is
        // 1:1 ordered per gun (Q1) and a no-op RequestFire is synchronously detectable via CanFire (Q2), so we never
        // enqueue a phantom. fireDelay is ~0.01 s (Q3) — useless as a deadline — so the orphan backstop is a fixed 2 s.
        internal struct FireIntent
        {
            public bool Replay;        // true = peer's replayed shot (carries the shooter's flight); false = our local shot (announce)
            public Vector2 Tgt, Start; // replay flight: board-local crater + launch point
            public float Time;         // replay travelTime
            public bool HaveStart;     // whether Start/Time are present (guard an old/short MSG_FIRE)
            public float Deadline;     // sweep backstop: drop if no FireShell consumes it by here
        }
        private static readonly List<FireIntent>[] _intents = { new List<FireIntent>(), new List<FireIntent>() };
        private static bool _replaying;            // true ONLY during our own replayed RequestFire (synchronous) — see ReplayShot
        private const float IntentDeadlineSec = 2f;

        // Per-FireShell consume state (FireShell is synchronous + non-reentrant, so a single set is safe). Reset in
        // OnFireShellPre; the first ShellVisual.Initialize of the shot dequeues one intent, later visuals reuse it.
        private static bool _shotIntentLoaded;
        private static bool _shotHaveIntent;
        private static FireIntent _shotIntent;

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
                _shotIntentLoaded = false; _shotHaveIntent = false;   // fresh per-shot intent dequeue
            }
            catch (Exception e) { try { Log.LogWarning("[ball] pre: " + e.Message); } catch { } }
        }

        public static void OnFireShellPost()
        {
            try { Restore(); } catch (Exception e) { try { Log.LogWarning("[ball] post: " + e.Message); } catch { } }
            // The intent for this shot was consumed at ShellVisual.Initialize (FIFO dequeue). End the gun reference's
            // lifetime here so a stray later shell can't resolve a stale side (OnShellVisualPost already ran + used it).
            _capGun = null;
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
                if (__instance == null || !Active()) return;   // stock when not an in-mission co-op shot

                int side = CoopControls.SideOfGun(_capGun);     // _capGun = the gun firing THIS shell (set in OnFireShellPre)
                if (side < 0 || side > 1) return;

                // Dequeue ONE intent for this FireShell (first ShellVisual only); later visuals of the same shot reuse it.
                if (!_shotIntentLoaded)
                {
                    var q = _intents[side];
                    if (q.Count > 0) { _shotIntent = q[0]; q.RemoveAt(0); _shotHaveIntent = true; }
                    else
                    {
                        // No intent ⇒ we did NOT tag this shot at RequestFire. A REPLAY always carries its Replay intent
                        // (enqueued in ReplayShot right before its own RequestFire, consumed ~fireDelay later by its own
                        // FireShell — and no other shot can slip in because the gun goes CanFire=false the instant it
                        // fires), so an untagged shell is NEVER a replay → announcing it can't echo. Treat it as a local
                        // shot (announce). This also gracefully degrades to the pre-queue behavior if the RequestFire
                        // intent hook ever fails to install (local shots still sync; replays still overwrite via intent).
                        _shotHaveIntent = false;
                        Diagnostics.V($"[ball] shell with NO queued intent side={side} — treating as local (announce)");
                    }
                    _shotIntentLoaded = true;
                }

                if (_shotHaveIntent && _shotIntent.Replay)
                {
                    // PEER replay: force the shooter's resolved flight onto our shell (every visual of this shot).
                    try
                    {
                        __instance.targetLocalPos = _shotIntent.Tgt;                  // the crater — always present
                        if (_shotIntent.HaveStart)                                    // the launch point / arc — guard an old/short packet
                        {
                            __instance.startLocalPos = _shotIntent.Start;
                            if (_shotIntent.Time > 0f) __instance.travelTime = _shotIntent.Time;
                            try { __instance.totalPathDistance = Vector2.Distance(_shotIntent.Start, _shotIntent.Tgt); } catch { }
                            try { __instance.previousPos = _shotIntent.Start; } catch { }
                        }
                    }
                    catch { }
                    _copied++;
                    Vector2 rs, rt; try { rs = __instance.startLocalPos; rt = __instance.targetLocalPos; } catch { return; }
                    Diagnostics.V($"[ball] shell flight now start=({rs.x:0.0},{rs.y:0.0}) target=({rt.x:0.0},{rt.y:0.0})");
                }
                else if (!_sentThisShot)
                {
                    // LOCAL shot (tagged Local, or untagged per the safe default above): announce the resolved flight once.
                    Vector2 s, t; float time;
                    try { s = __instance.startLocalPos; t = __instance.targetLocalPos; time = __instance.travelTime; } catch { return; }
                    _sentThisShot = true;
                    try { CoopControls.SendLocalShot(_capGun, t, s, time); }
                    catch (Exception e) { try { Log.LogWarning("[ball] send: " + e.Message); } catch { } }
                }
            }
            catch { }
        }

        // ================= plumbing: enqueue / sweep =================

        // Q4 (dev probe only): true ONLY during our own synchronous replayed RequestFire, so CoopFireProbe can tag a
        // RequestFire as "MSG_FIRE-replay" vs "local-trigger". (Replaces the old global pending flag of the same name.)
        internal static bool IsApplyingRemoteImpact => _replaying;

        // PEER MSG_FIRE handler (CoopControls): replay the shooter's shot on our matching gun. Tag a Replay intent with
        // the shooter's flight BEFORE firing, then RequestFire — but ONLY if the gun will actually fire (Q2: CanFire
        // predicts it perfectly). If it can't (reloading) the replay would no-op: we enqueue nothing (no orphan, no
        // stale flag) and the shot is simply not shown on this peer (reload divergence — a separate issue), instead of
        // poisoning the next local shot the way the old 6 s global flag did.
        internal static void ReplayShot(GunController gun, int side, Vector2 tgt, Vector2 start, float time, bool haveStart)
        {
            try
            {
                if (gun == null || side < 0 || side > 1) return;
                bool willFire = false; try { willFire = gun.CanFire; } catch { }
                if (!willFire) { Diagnostics.V($"[ball] replay gun {side} dropped — CanFire=false (gun reloading here)"); return; }

                float now; try { now = Time.unscaledTime; } catch { now = 0f; }
                _intents[side].Add(new FireIntent { Replay = true, Tgt = tgt, Start = start, Time = time, HaveStart = haveStart, Deadline = now + IntentDeadlineSec });

                _replaying = true;
                try { gun.RequestFire(); }
                catch (Exception e) { try { Log.LogWarning("[ball] replay fire: " + e.Message); } catch { } }
                finally { _replaying = false; }
            }
            catch { _replaying = false; }
        }

        // OBSERVE-ONLY hook (CoopSim, PREFIX on GunController.RequestFire): tag a LOCAL shot. Runs for EVERY RequestFire
        // including our own replay — _replaying tells them apart (the replay was already tagged in ReplayShot). Enqueue
        // only when the shot WILL fire (CanFire, read PRE-body before hasFired flips), so a no-op trigger adds no phantom.
        internal static void NoteLocalRequestFire(GunController gun)
        {
            try
            {
                if (_replaying) return;                 // our own replay — already enqueued as Replay
                if (!Active() || gun == null) return;   // inert in solo / PvP
                bool willFire = false; try { willFire = gun.CanFire; } catch { }
                if (!willFire) return;                  // no-op trigger (reloading) — produces no shell (Q2)
                int side = CoopControls.SideOfGun(gun);
                if (side < 0 || side > 1) return;
                float now; try { now = Time.unscaledTime; } catch { now = 0f; }
                _intents[side].Add(new FireIntent { Replay = false, Deadline = now + IntentDeadlineSec });
            }
            catch { }
        }

        // Backstop sweep (VrManager tick): drop a front intent that no FireShell ever consumed (a Q2 miss). The
        // synchronous CanFire gate above prevents the common orphan; this only catches a pathological miss.
        public static void SweepTick()
        {
            try
            {
                float now; try { now = Time.unscaledTime; } catch { return; }
                for (int s = 0; s < 2; s++)
                {
                    var q = _intents[s];
                    while (q.Count > 0 && now >= q[0].Deadline)
                    {
                        var dead = q[0]; q.RemoveAt(0);
                        Diagnostics.V($"[ball] swept orphan intent side={s} replay={dead.Replay} (no shell consumed it)");
                    }
                }
            }
            catch { }
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
        internal static bool Active()
        {
            if (Config.PvpActive) return false;   // PvP owns its own shot lane — never replay/zero co-op fire here
            if (!Config.CoopDeterministicFire) return false;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) return false;
            try { var mm = MissionManager.Instance; return mm != null && mm.CurrentPhase == MissionManager.GamePhase.MissionActive; }
            catch { return false; }
        }

        // ================= diagnostics =================

        public static string Status() => $"ball: deterministic={(Active() ? "ON" : "off")} shots={_shots} copied={_copied}";
    }
}
