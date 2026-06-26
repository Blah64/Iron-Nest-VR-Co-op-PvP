using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Phase 3 co-op: cockpit-control replication with TRANSIENT, PER-CONTROL ownership, plus turret/gun
    /// physical-state sync. Both players are free-walking crew who can operate ANY dial/lever/switch and
    /// either gun — so ownership is per-control and per-moment, not a static seat split.
    ///
    /// HOW IT WORKS
    /// • Ownership is detected input-source-agnostically by polling each control's own <c>isDragging</c> —
    ///   true whether the local player grabbed it with the VR gravity-glove (<see cref="HandManipulator"/>
    ///   calls BeginDialDrag) OR the flatscreen player dragged it with the native mouse (we never see that
    ///   input, but the control's drag flag flips all the same). First to grab a FREE control owns it; if a
    ///   peer already owns it we don't claim (their stream overrides our local fiddling). Simultaneous grabs
    ///   are broken by host priority so exactly one side yields.
    /// • We sync the INTENT, not the input: whoever operates a control streams the turret/gun DESIRED aim
    ///   that control's GROUP drives (azimuth DesiredRotation; per-gun DesiredElevationAngle; powder). BOTH
    ///   machines run the turret sim locally and slew CurrentAngle/CurrentElevation toward the shared desired,
    ///   so EITHER player can drive and the other follows — symmetric, no host authority. Rate-driven motion
    ///   ("turn a wheel, release, the turret keeps moving") mirrors for free: it's just the local slew to the
    ///   shared DesiredRotation. (Matches the reference mod's Turrets.cs; we sync CurrentAngle exactly ONCE, in
    ///   the JIP snapshot, for a tight start — never in the live stream, which is what made the host fight and
    ///   override the client when we streamed physical state continuously.)
    /// • Presence: each operated dial/lever also streams its visual value so you SEE the other player turning
    ///   it (the turret state alone wouldn't move a flavor dial or a valve).
    ///
    /// Controls key by a STABLE hash of their transform path (FNV-1a — NOT string.GetHashCode, which is
    /// per-process randomised and would differ between the two machines). Same build + same scene ⇒ same path
    /// ⇒ same id on both sides. Groups are classified from the path (rotation / elevation / gun-left/right).
    ///
    /// Riding the same Steam P2P channel as the pose stream (<see cref="CoopP2P"/>), distinguished by the
    /// first packet byte. Grabs/releases go reliable (a lost one would strand ownership); the continuous
    /// value/state stream is unreliable-no-delay and rate-capped to <see cref="Config.CoopSendHz"/>.
    ///
    /// DEFERRED: faithful reload-animation replay (we sync powder + reload READINESS, not the rammer theatre)
    /// and entity/mission/shell sync (Phase 4). Combat isn't unlocked in the demo yet.
    /// </summary>
    internal static class CoopControls
    {
        private static ManualLogSource Log => Plugin.Logger;

        // Packet types — distinct from CoopP2P.MSG_POSE (1).
        private const byte MSG_GRAB = 2;     // [t][netId i32][group u8]      reliable   drag-control grab
        private const byte MSG_RELEASE = 3;  // [t][netId i32]               reliable   drag-control release
        private const byte MSG_VALUE = 4;    // [t][netId i32][value f32]    unreliable drag-control value
        private const byte MSG_GROUP = 5;    // [t][group u8][f32 ...]       unreliable turret/gun state
        private const byte MSG_CLICK = 6;    // [t][netId i32]               reliable   LookAtTarget click
        private const byte MSG_FIRE = 7;     // [t][side u8]                 reliable   gun discharge (0=L,1=R)
        private const byte MSG_SNAP = 13;    // [t][9×f32]                   reliable   join-in-progress turret/gun state
        private const byte MSG_RECON = 25;   // [t][9×f32]                   reliable   recurring host current-state reconcile (REVIEW-fix P3)
        private const byte MSG_POWDER = 39;  // [t][side u8][charges i32]    reliable   per-gun powder/charge state, EITHER→peer (symmetric, last-writer-wins)

        // How long remote ownership / streamed state survives without a refresh. The stream runs at
        // CoopSendHz (~30/s), so 2s easily rides minor packet loss but recovers fast if the peer vanishes
        // mid-drag (lost RELEASE, disconnect) — the control un-sticks instead of staying frozen.
        private const float StaleSec = 2f;

        private enum Group : byte { Other = 0, Rotation = 1, Elevation = 2, GunLeft = 3, GunRight = 4 }

        private sealed class Ctrl
        {
            public int NetId;
            public Transform T;
            public DialInteractable Dial;          // drag control: exactly one of Dial/Slider is set …
            public LinearSliderInteractable Slider;
            public LookAtTarget Switch;            // … OR a click control (Switch set, Dial/Slider null)
            public Group Grp;

            public bool LocalOwned;                 // drag: we are operating it right now
            public bool PrevDragging;               // drag: edge-detect on isDragging
            public bool PrevClicked;                // click: edge-detect on isClicked
            public ulong RemoteOwner;               // SteamID of the peer operating it (0 = none)
            public float RemoteUntil;               // ownership/value freshness expiry
            public bool HasRemoteVal;
            public float RemoteVal;                 // dial: accumulated angle; slider: current distance
        }

        private sealed class GroupState
        {
            public bool RemoteOwned;
            public ulong RemoteOwner;               // SteamID currently driving this group (0 = none); gates the value/group stream at N>2
            public float Until;
            public bool Has;
            public readonly float[] V = new float[5];
        }

        private static readonly Dictionary<int, Ctrl> _byId = new Dictionary<int, Ctrl>();
        private static readonly GroupState[] _grp = { new GroupState(), new GroupState(), new GroupState(), new GroupState(), new GroupState() };

        // Remote messages that arrive before the control is registered (different scene-load timing). Applied
        // when the control first appears in EnsureRegistry.
        private static readonly Dictionary<int, (ulong Origin, float V)> _pendingVal = new Dictionary<int, (ulong, float)>();
        private static readonly Dictionary<int, ulong> _pendingGrab = new Dictionary<int, ulong>();   // netId -> grab origin

        // Echo guard: when we REPLAY a peer's click/fire, the game flips isClicked/hasFired on our side too,
        // which our own poll would re-detect and bounce back. Suppress local detection for a control (keyed by
        // netId; fire uses synthetic keys) for a short window after a replay.
        private static readonly Dictionary<int, float> _echoUntil = new Dictionary<int, float>();
        private static readonly int FireKeyL = Fnv("__coop_fire_L"), FireKeyR = Fnv("__coop_fire_R");
        private static bool _firedPrevL, _firedPrevR;
        // Per-gun powder/charge edge-detect: the reload sequence runs locally (the "rammer theatre"), so a button
        // click alone never reproduces the loaded charge on the peer. We watch each gun's PowderCharges and push the
        // VALUE itself, either direction, whenever it changes. int.MinValue = "no baseline yet" (seed silently on the
        // first sample / after join — the JIP snapshot already carried join-time powder). Echo-suppressed via _echoUntil.
        private static readonly int PowderKeyL = Fnv("__coop_powder_L"), PowderKeyR = Fnv("__coop_powder_R");
        private static int _powderPrevL = int.MinValue, _powderPrevR = int.MinValue;
        // Robustness: a powder change is one reliable packet; if it's lost in a link-drop blackout it never self-heals
        // (recon no longer carries powder). So the side that AUTHORED a gun's powder re-asserts it on a low-rate
        // heartbeat. Only the author re-asserts (adopting the peer's value clears the flag), so the two never flap.
        private static bool _powderAuthoredL, _powderAuthoredR;
        private static float _nextPowderBeat;
        private const float PowderHeartbeatSec = 2f;

        // Join-in-progress snapshot received before the turret/guns resolved locally (scene still loading on the
        // joiner). Held here and applied once the registry is ready — see Tick / ApplySnapshot.
        private static float[] _pendingSnap;

        private static TurretController _turret;
        private static GunController _gunL, _gunR;
        private static int _turretIid = -1;
        private static float _nextScan;
        private static float _nextSend;
        private static float _nextRecon;   // host: next current-state reconcile broadcast (REVIEW-fix P3)
        private static float _nextElevDiag;     // throttle for the "applied peer elevation" (receiver) diagnostic
        private static float _nextElevSendDiag;  // throttle for the "streaming elevation" (sender) diagnostic
        // ALL requisition-console dials are EXCLUDED from this path-hash registry: their PathOf hash collides with the
        // artillery gun's same-named targeting dials (.Gross Bearing/.Range), AND the console's real coordinate input is
        // FOUR dials (two Grid-Location .Range Dials + gross/fine bearing), not just the bridge's one. CoopPunchcards
        // owns them — keyed by console-relative path (collision-free, every relpath is unique under this root), applied
        // with SetDialValue (the proven setter). Counted per scan for the registry log.
        private static int _reconExcluded;

        // Reusable send buffer (control packets are tiny: 1+4+4 max). Lazily created on the Unity thread.
        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];
        // Scratch for strict group-packet parsing: read into here, validate every float finite, then publish to
        // gs.V only if the whole frame is intact (REVIEW-fix P2 — no partial/non-finite turret state).
        private static readonly float[] _tmpGroup = new float[5];
        // Per-tick scratch: which groups WE own this frame. Cleared each Tick instead of reallocated. _grp is
        // initialized above (source order), so sizing off _grp.Length here is safe at field-init time.
        private static readonly bool[] _groupOwnedLocal = new bool[_grp.Length];

        private static int _grabs, _releases;
        private static int _dialCollisions;   // DIAGNOSTIC: count/log dials dropped because their path-hash NetId already exists

        public static bool Active => _byId.Count > 0 && _turret != null;

        // ---------------- per-frame: detect local drags + transmit ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopControlSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { if (_byId.Count > 0) ClearOwnership(); return; }
            try
            {
                EnsureRegistry();
                if (_pendingSnap != null && _turret != null) { ApplySnapshot(_pendingSnap); _pendingSnap = null; }
                if (_turret == null) return;

                float now = Time.unscaledTime;
                bool sendNow = Config.CoopSendHz <= 0f || now >= _nextSend;
                if (sendNow && Config.CoopSendHz > 0f) _nextSend = now + 1f / Config.CoopSendHz;

                Array.Clear(_groupOwnedLocal, 0, _groupOwnedLocal.Length);
                var groupOwnedLocal = _groupOwnedLocal;

                foreach (var c in _byId.Values)
                {
                    if (c.Switch != null) { DetectClick(c, now); continue; }   // click control, not a drag

                    bool dragging = IsDragging(c);

                    if (dragging && !c.PrevDragging)
                    {
                        // Rising edge: claim the control unless the peer currently owns it (then we yield —
                        // their stream overrides our local movement in LateApply; we never fight the game's
                        // own drag system).
                        if (!(c.RemoteOwner != 0 && now < c.RemoteUntil))
                        {
                            c.LocalOwned = true;
                            SendGrab(c);
                            _grabs++;
                            if (_grabs <= 50) Log.LogInfo($"[ctrl] grabbed '{c.T.name}' (grp={c.Grp}) — local owner");
                        }
                    }
                    else if (!dragging && c.PrevDragging && c.LocalOwned)
                    {
                        // Falling edge: release + push a final state so the peer settles on the right value.
                        c.LocalOwned = false;
                        SendRelease(c);
                        _releases++;
                    }
                    c.PrevDragging = dragging;

                    if (c.LocalOwned)
                    {
                        groupOwnedLocal[(int)c.Grp] = true;
                        if (sendNow) SendValue(c);
                    }

                    // Expire remote ownership (lost RELEASE / vanished peer).
                    if (c.RemoteOwner != 0 && now >= c.RemoteUntil) { c.RemoteOwner = 0; RecomputeGroupRemote(c.Grp); }
                }

                // Stream the DESIRED aim only for groups WE are operating right now (symmetric — either side can
                // drive). The peer applies our desired and its own turret sim slews toward it; after we release,
                // the desired is fixed AND already delivered (the last stream frame + the reliable release carry
                // it), so BOTH turrets slew to the same final aim locally — no host authority, no post-release
                // freeze, and no CurrentAngle tug-of-war (the bug when the host streamed physical state
                // continuously and overrode the client's input).
                if (sendNow)
                    for (int g = 1; g < _grp.Length; g++)
                        if (groupOwnedLocal[g]) SendGroupState((Group)g);

                // Reclaimed the elevation dial locally → give gun-elevation control back to the turret controller
                // (we force it off only while adopting the peer's aim).
                if (_elevDriveSaved.HasValue && groupOwnedLocal[(int)Group.Elevation]) RestoreGunDrive();

                for (int g = 0; g < _grp.Length; g++)
                    if (_grp[g].RemoteOwned && now >= _grp[g].Until) _grp[g].RemoteOwned = false;

                DetectFire(now);
                DetectPowder(now);

                // REVIEW-fix (P3): host broadcasts a low-rate CURRENT-state reconcile so the client can correct any
                // accumulated CurrentAngle/elevation drift (framerate-dependent slew, a missed reliable packet).
                if (CoopP2P.IsHost && Config.CoopTurretReconcile && now >= _nextRecon)
                {
                    _nextRecon = now + Mathf.Max(0.25f, Config.CoopTurretReconcileSec);
                    SendRecon();
                }
            }
            catch (Exception e) { Log.LogWarning("[ctrl] tick: " + e.Message); }
        }

        // ---------------- click events (LookAtTarget) ----------------

        // Detect a LOCAL click on a click-control (flatscreen native mouse OR any path that flips isClicked)
        // and broadcast it. The VR glove goes through LocalClick() instead; this catches everything else.
        private static void DetectClick(Ctrl c, float now)
        {
            if (!Config.CoopClickSync) return;
            bool clicked = false;
            try { clicked = c.Switch.isClicked; } catch { }
            bool suppressed = _echoUntil.TryGetValue(c.NetId, out var u) && now < u;
            if (clicked && !c.PrevClicked && !suppressed)
            {
                SendClick(c.NetId);
                Log.LogInfo($"[ctrl] click '{c.T.name}' -> peer");
            }
            c.PrevClicked = clicked;
        }

        // VR gravity-glove activation hook (HandManipulator). The glove fires the click in a single frame
        // (down+up together), which the isClicked poll can't see — so the glove reports it explicitly.
        public static void LocalClick(LookAtTarget sw)
        {
            if (!Config.CoopClickSync || sw == null || !SteamNet.InLobby || !CoopP2P.HasPeer) return;
            try
            {
                foreach (var c in _byId.Values)
                    if ((object)c.Switch == sw) { SendClick(c.NetId); Log.LogInfo($"[ctrl] click '{c.T.name}' (glove) -> peer"); return; }
            }
            catch { }
        }

        // ---------------- fire events ----------------

        // Replicate a gun discharge so the peer sees the recoil (and, in Phase 4, so it can drive the shared
        // shell sim). The trigger is a slider on one machine, so the value stream alone wouldn't fire the peer's
        // gun — we detect the discharge from GunController.hasFired and replay RequestFire on the other side.
        private static void DetectFire(float now)
        {
            if (!Config.CoopClickSync) return;
            _firedPrevL = DetectFireGun(_gunL, _firedPrevL, 0, FireKeyL, now);
            _firedPrevR = DetectFireGun(_gunR, _firedPrevR, 1, FireKeyR, now);
        }

        private static bool DetectFireGun(GunController gun, bool prev, byte side, int echoKey, float now)
        {
            if (gun == null) return false;
            bool fired = false;
            try { fired = gun.hasFired; } catch { }
            bool suppressed = _echoUntil.TryGetValue(echoKey, out var u) && now < u;
            if (fired && !prev && !suppressed) { SendFire(side); Log.LogInfo($"[ctrl] fire gun {side} -> peer"); }
            return fired;
        }

        // ---------------- powder / charge state (symmetric) ----------------

        // Replicate each gun's loaded charge to the peer. Powder is part of the locally-run reload sim, so the
        // Charge-Rammer click (a Group.Other LookAtTarget) replays the BUTTON on the peer but doesn't reproduce the
        // loaded charge when the two reload state machines differ — which is exactly the desync the detector caught.
        // Edge-triggered on the value: whoever loads (host OR client) pushes the number, the peer adopts it. No host
        // authority (the recurring reconcile no longer touches powder — see ApplyRecon), so a client-side load holds.
        private static void DetectPowder(float now)
        {
            if (!Config.CoopControlSync) return;
            _powderPrevL = DetectPowderGun(_gunL, _powderPrevL, 0, PowderKeyL, now, ref _powderAuthoredL);
            _powderPrevR = DetectPowderGun(_gunR, _powderPrevR, 1, PowderKeyR, now, ref _powderAuthoredR);

            // Self-heal: re-assert the powder WE authored at a low rate so a value lost in a link-drop blackout
            // re-converges (the peer applies it idempotently; its guard makes a matching value a no-op).
            if (now >= _nextPowderBeat)
            {
                _nextPowderBeat = now + PowderHeartbeatSec;
                if (_powderAuthoredL && _gunL != null) { try { SendPowder(0, _gunL.PowderCharges); } catch { } }
                if (_powderAuthoredR && _gunR != null) { try { SendPowder(1, _gunR.PowderCharges); } catch { } }
            }
        }

        private static int DetectPowderGun(GunController gun, int prev, byte side, int echoKey, float now, ref bool authored)
        {
            if (gun == null) return prev;
            int cur;
            try { cur = gun.PowderCharges; } catch { return prev; }
            if (cur == prev) return cur;
            // prev==MinValue: first sample after (re)connect — seed the baseline silently (no spurious blast; join
            // state rides MSG_SNAP). suppressed: we just applied the peer's value, don't bounce it straight back.
            bool suppressed = _echoUntil.TryGetValue(echoKey, out var u) && now < u;
            if (prev != int.MinValue && !suppressed)
            {
                SendPowder(side, cur);
                authored = true;   // a local change → we own re-asserting this gun's powder until we adopt the peer's
                Log.LogInfo($"[ctrl] powder gun {side} -> peer ({cur})");
            }
            return cur;
        }

        // ---------------- after the game's Update: apply remote visuals + snap turret state ----------------

        public static void LateApply()
        {
            if (!Config.CoopControlSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer || _turret == null) return;
            try
            {
                float now = Time.unscaledTime;

                // Remote-operated control visuals (so you see the peer turning dials/levers). Skip ones we
                // own locally — our own drag drives those.
                foreach (var c in _byId.Values)
                {
                    if (c.LocalOwned || c.RemoteOwner == 0 || now >= c.RemoteUntil || !c.HasRemoteVal) continue;
                    try
                    {
                        // VISUAL KNOB ONLY for every dial in this registry, NO value-changed event. The recon console's
                        // bearing/range dials are NOT here (excluded — synced collision-free by CoopPunchcards); what's
                        // left is turret/gun and flavor dials. Firing value-changed would drive the gun's targeting
                        // dials into the barrel (their event moves azimuth/elevation) — turret/gun aim syncs via
                        // ApplyGroup, not this. So just snap the knob to the peer's angle.
                        if (c.Dial != null) c.Dial.SetAccumulatedValueUnlimited(c.RemoteVal, false, false);
                        else if (c.Slider != null) c.Slider.ApplyLocalPosition(c.RemoteVal);
                    }
                    catch { }
                }

                // Snap turret/gun physical state for any group the peer owns (and we don't).
                ApplyGroup(Group.Rotation, now);
                ApplyGroup(Group.Elevation, now);
                ApplyGroup(Group.GunLeft, now);
                ApplyGroup(Group.GunRight, now);
            }
            catch (Exception e) { Log.LogWarning("[ctrl] apply: " + e.Message); }
        }

        private static bool LocallyOwnsGroup(Group g)
        {
            foreach (var c in _byId.Values) if (c.Grp == g && c.LocalOwned) return true;
            return false;
        }

        private static void ApplyGroup(Group g, float now)
        {
            var gs = _grp[(int)g];
            if (!gs.Has || now >= gs.Until) return;
            if (LocallyOwnsGroup(g)) return;   // never fight our own live drag (our input wins, symmetric)
            // Apply the peer's DESIRED aim; our local turret sim slews toward it. Symmetric — no host/client
            // distinction. Once neither side operates the group its stream goes stale (StaleSec) and we stop
            // re-applying; by then both turrets have slewed to the shared desired.
            ApplyGroupValues(g, gs);
        }

        // Push a group's stored floats onto the turret/guns. Caller decides WHEN (the live stream gates on remote
        // ownership; the reliable release applies once regardless — REVIEW-fix P2). Always skips a group we own
        // locally; that check is the caller's (ApplyGroup gates it; the release path checks before calling).
        private static void ApplyGroupValues(Group g, GroupState gs)
        {
            try
            {
                switch (g)
                {
                    case Group.Rotation:
                        _turret.DesiredRotation = gs.V[0];   // intent only — local sim slews CurrentAngle toward it
                        break;
                    case Group.Elevation:
                        // The guns' elevation is normally slaved to the turret's elevation CONTROLLER
                        // (driveGunElevationsFromController): every frame the turret re-derives each gun's target
                        // from the physical elevation dial. On the side ADOPTING the peer's aim the dial is parked,
                        // so that re-derivation kept clobbering the gun target back toward 0 and the barrel never
                        // slewed. Worse, writing the DesiredElevationAngle PROPERTY only sets an inert reported
                        // backing field — the slew physics (UpdateElevationPhysics) tracks the gun's
                        // internalDesiredElevation, reached ONLY via SetDesiredElevation(). That double bug is the
                        // "host sees the slider move but the elevation never changes / keeps resetting to the host's"
                        // report. Fix: stop the controller re-driving, then hand each gun its REAL target. Restored
                        // when we reclaim the dial (Tick) or drop the peer (ClearOwnership) — one owner at a time.
                        OverrideGunDrive();
                        _turret.DesiredElevation = gs.V[0];
                        if (_gunL != null) _gunL.SetDesiredElevation(gs.V[1]);
                        if (_gunR != null) _gunR.SetDesiredElevation(gs.V[2]);
                        // DIAG: with the fix, gunL.cur should now SLEW toward gunL.des across successive samples
                        // (before, des stuck at the target while cur stayed 0 — the physics tracked a different field).
                        if (Time.unscaledTime >= _nextElevDiag)
                        {
                            _nextElevDiag = Time.unscaledTime + 1f;
                            Log.LogInfo($"[ctrl] applied peer elevation: turret={gs.V[0]:0.0} gunL={gs.V[1]:0.0} gunR={gs.V[2]:0.0} " +
                                        $"(now gunL.des={(_gunL != null ? _gunL.DesiredElevationAngle : 0f):0.0} gunL.cur={(_gunL != null ? _gunL.CurrentElevation : 0f):0.0} drive={(_turret != null && _turret.driveGunElevationsFromController)})");
                        }
                        break;
                    case Group.GunLeft:  ApplyGunState(_gunL, gs); break;
                    case Group.GunRight: ApplyGunState(_gunR, gs); break;
                }
            }
            catch (Exception e) { Log.LogWarning($"[ctrl] applyGroup {g}: " + e.Message); }
        }

        private static void ApplyGunState(GunController gun, GroupState gs)
        {
            if (gun == null) return;
            int powder = Mathf.RoundToInt(gs.V[0]);
            try { if (gun.PowderCharges != powder) gun.SetPowderCharge(powder); } catch { }
            // gs.V[1] = reload-in-progress flag. We sync powder + readiness; faithful rammer-animation replay
            // is deferred (Phase 3 follow-up), so we don't force the reload coroutine state here.
        }

        // While we ADOPT the peer's elevation we force the turret controller to stop re-deriving the gun targets
        // from our (parked) elevation dial — otherwise it overwrites the synced target every frame and the barrel
        // never slews. Capture the original flag once; RestoreGunDrive puts it back when we reclaim the dial or
        // lose the peer. Exactly ONE system drives the guns' elevation at a time (the dial OR the peer stream).
        private static bool? _elevDriveSaved;
        private static void OverrideGunDrive()
        {
            if (_turret == null) return;
            try
            {
                if (!_elevDriveSaved.HasValue) _elevDriveSaved = _turret.driveGunElevationsFromController;
                if (_turret.driveGunElevationsFromController) _turret.driveGunElevationsFromController = false;
            }
            catch { }
        }
        private static void RestoreGunDrive()
        {
            if (_turret != null && _elevDriveSaved.HasValue)
            {
                try { _turret.driveGunElevationsFromController = _elevDriveSaved.Value; } catch { }
            }
            _elevDriveSaved = null;
        }

        // ---------------- join-in-progress snapshot ----------------

        // Host → new joiner: push the CURRENT turret/gun physical state as one reliable packet, so the joiner
        // adopts the host's aim/elevation/powder instead of its stale default. The continuous MSG_GROUP stream
        // only flows while a control is being operated, so without this a joiner who arrives after the host
        // last touched the turret would see it parked at zero. Also re-asserts any control the host is holding
        // RIGHT NOW (re-send MSG_GRAB) so the joiner's ownership-refusal is correct from the first frame.
        // Called by CoopP2P.SendJoinSnapshot (host only, after the peer/session has settled).
        public static void SendSnapshot()
        {
            if (!Config.CoopControlSync) return;
            if (_turret == null) { Log.LogInfo("[ctrl] JIP snapshot skipped — no turret resolved yet"); return; }
            if (!EnsureBuf()) return;
            try
            {
                int o = 0; _buf[o++] = MSG_SNAP;
                o = PutFloat(o, _turret.DesiredRotation);
                o = PutFloat(o, _turret.CurrentAngle);
                o = PutFloat(o, _turret.DesiredElevation);
                o = PutFloat(o, _gunL != null ? _gunL.DesiredElevationAngle : 0f);
                o = PutFloat(o, _gunL != null ? _gunL.CurrentElevation : 0f);
                o = PutFloat(o, _gunR != null ? _gunR.DesiredElevationAngle : 0f);
                o = PutFloat(o, _gunR != null ? _gunR.CurrentElevation : 0f);
                o = PutFloat(o, _gunL != null ? _gunL.PowderCharges : 0f);
                o = PutFloat(o, _gunR != null ? _gunR.PowderCharges : 0f);
                CoopP2P.Send(_buf, o, true);

                int owned = 0;
                foreach (var c in _byId.Values) if (c.LocalOwned) { SendGrab(c); owned++; }
                Log.LogInfo($"[ctrl] sent JIP snapshot -> peer (rot={_turret.CurrentAngle:0.0} elev={_turret.DesiredElevation:0.0}, {owned} held controls)");
            }
            catch (Exception e) { Log.LogWarning("[ctrl] send snapshot: " + e.Message); }
        }

        // Joiner: adopt the host's turret/gun state from a snapshot. Applied ONCE (not gated on remote
        // ownership, unlike the live stream). Skips any group the joiner is somehow already driving locally
        // (defensive — at join the joiner owns nothing) and rejects non-finite values so a bad packet can't
        // poison the turret transform.
        private static void ApplySnapshot(float[] v)
        {
            if (_turret == null || v == null || v.Length < 9) return;
            try
            {
                if (!LocallyOwnsGroup(Group.Rotation) && Finite(v[0]) && Finite(v[1]))
                {
                    _turret.DesiredRotation = v[0]; _turret.CurrentAngle = v[1];
                    try { _turret.ApplyRotationToTransforms(); } catch { }
                }
                if (!LocallyOwnsGroup(Group.Elevation))
                {
                    if (Finite(v[2])) _turret.DesiredElevation = v[2];
                    if (_gunL != null) { if (Finite(v[3])) _gunL.DesiredElevationAngle = v[3]; if (Finite(v[4])) _gunL.CurrentElevation = v[4]; }
                    if (_gunR != null) { if (Finite(v[5])) _gunR.DesiredElevationAngle = v[5]; if (Finite(v[6])) _gunR.CurrentElevation = v[6]; }
                }
                if (_gunL != null && !LocallyOwnsGroup(Group.GunLeft)) { int p = Mathf.RoundToInt(v[7]); try { if (_gunL.PowderCharges != p) _gunL.SetPowderCharge(p); } catch { } }
                if (_gunR != null && !LocallyOwnsGroup(Group.GunRight)) { int p = Mathf.RoundToInt(v[8]); try { if (_gunR.PowderCharges != p) _gunR.SetPowderCharge(p); } catch { } }
                Log.LogInfo($"[ctrl] applied JIP snapshot (rot={v[1]:0.0} desRot={v[0]:0.0} elev={v[2]:0.0} powderL={Mathf.RoundToInt(v[7])} powderR={Mathf.RoundToInt(v[8])})");
            }
            catch (Exception e) { Log.LogWarning("[ctrl] apply snapshot: " + e.Message); }
        }

        // ---------------- current-state reconcile (REVIEW-fix P3) ----------------

        // Host → client: the CURRENT turret/gun physical state, broadcast on a low-rate reliable heartbeat. Same
        // 9-float layout as the JIP snapshot, but a SEPARATE type so the client applies it softly (drift-gated)
        // rather than hard-snapping like a join.
        private static void SendRecon()
        {
            if (_turret == null || !EnsureBuf()) return;
            try
            {
                int o = 0; _buf[o++] = MSG_RECON;
                // The host's CURRENT gun elevation is authoritative ONLY while the host is the one driving it.
                // When the CLIENT drives elevation (or nobody does) the host's gun-elevation reading must not be
                // imposed on the client — broadcasting it (often stale, since the adopting side's barrel lags or,
                // pre-fix, never slewed) is exactly what dragged the client's elevation back every reconcile.
                // Send NaN for the gun CURRENT-elevation fields in that case; the client's Finite() guard skips
                // them. Rotation stays authoritative — the turret always auto-slews CurrentAngle toward desired.
                bool elevAuth = LocallyOwnsGroup(Group.Elevation);
                o = PutFloat(o, _turret.DesiredRotation);
                o = PutFloat(o, _turret.CurrentAngle);
                o = PutFloat(o, _turret.DesiredElevation);
                o = PutFloat(o, _gunL != null ? _gunL.DesiredElevationAngle : 0f);
                o = PutFloat(o, elevAuth && _gunL != null ? _gunL.CurrentElevation : float.NaN);
                o = PutFloat(o, _gunR != null ? _gunR.DesiredElevationAngle : 0f);
                o = PutFloat(o, elevAuth && _gunR != null ? _gunR.CurrentElevation : float.NaN);
                o = PutFloat(o, _gunL != null ? _gunL.PowderCharges : 0f);
                o = PutFloat(o, _gunR != null ? _gunR.PowderCharges : 0f);
                CoopP2P.Send(_buf, o, true);
            }
            catch (Exception e) { Log.LogWarning("[ctrl] send recon: " + e.Message); }
        }

        // Client: correct accumulated CURRENT-angle/elevation SLEW DRIFT against the host — but ONLY for a group
        // whose DESIRED already AGREES with the host's (so we're fixing framerate/lost-packet slew lag, NOT
        // overriding a fresh re-aim). It must NEVER write DESIRED: desired is the shared intent, delivered by the
        // reliable ownership stream/release, and overwriting it is exactly what let the host drag the client's aim
        // back to the host's value ("elevation keeps resetting to the host's" — the bug this fixes). Skips any group
        // the client is operating right now. Powder is discrete (no slew) and host-authoritative when un-owned.
        private static void ApplyRecon(float[] v)
        {
            float tol = Mathf.Max(0.1f, Config.CoopTurretReconcileTolDeg);
            try
            {
                // ROTATION: snap CurrentAngle only if the desired aim already matches and the current angle drifted.
                if (!LocallyOwnsGroup(Group.Rotation) && Finite(v[0]) && Finite(v[1])
                    && Mathf.Abs(Mathf.DeltaAngle(_turret.DesiredRotation, v[0])) <= tol
                    && Mathf.Abs(Mathf.DeltaAngle(_turret.CurrentAngle, v[1])) > tol)
                {
                    _turret.CurrentAngle = v[1];
                    try { _turret.ApplyRotationToTransforms(); } catch { }
                    Log.LogInfo($"[ctrl] reconciled rotation current -> {v[1]:0.0} (desired agreed, drift>{tol:0.0}deg) <- host");
                }
                // ELEVATION (per-gun): snap CurrentElevation only where the gun's DESIRED already agrees with the host.
                if (!LocallyOwnsGroup(Group.Elevation))
                {
                    if (_gunL != null && Finite(v[3]) && Finite(v[4])
                        && Mathf.Abs(_gunL.DesiredElevationAngle - v[3]) <= tol
                        && Mathf.Abs(_gunL.CurrentElevation - v[4]) > tol)
                        _gunL.CurrentElevation = v[4];
                    if (_gunR != null && Finite(v[5]) && Finite(v[6])
                        && Mathf.Abs(_gunR.DesiredElevationAngle - v[5]) <= tol
                        && Mathf.Abs(_gunR.CurrentElevation - v[6]) > tol)
                        _gunR.CurrentElevation = v[6];
                }
                // Powder is NO LONGER reconciled here. The recurring reconcile is host→client only, so applying the
                // host's powder authoritatively clobbered a CLIENT-side load right back to the host's value (the
                // never-recovering "powder local=1 peer=0" desync). Powder now travels symmetrically via MSG_POWDER
                // (edge-triggered, either side authors, last-writer-wins). v[7]/v[8] are ignored. (recon still WRITES
                // them for wire-format/JIP-layout parity — only the apply is dropped.)
            }
            catch (Exception e) { Log.LogWarning("[ctrl] apply recon: " + e.Message); }
        }

        // ---------------- receive ----------------

        // Relay ACL (PLAN.md §2.4): the packet types a client may author, which the host relays to the OTHER
        // clients. These are the bidirectional, player-operated subsystems — cockpit controls, clipboard edits,
        // map tokens — plus the join-readiness ack. Everything NOT listed is host-authored (the host is the sole
        // source — never relayed) or a host-consumed diagnostic (MSG_DIGEST). CoopP2P relays MSG_POSE itself.
        // This mirrors the role split the subsystems already self-guard on (`if (IsHost) return` etc.).
        public static bool IsClientAuthored(byte type)
        {
            switch (type)
            {
                case MSG_GRAB:
                case MSG_RELEASE:
                case MSG_VALUE:
                case MSG_GROUP:
                case MSG_CLICK:
                case MSG_FIRE:
                case MSG_POWDER:
                    return true;   // cockpit controls — either crew member operates them
            }
            return type == CoopClipboard.MSG_SECTION || type == CoopClipboard.MSG_TOOL
                || type == CoopMap.MSG_GRAB || type == CoopMap.MSG_POS || type == CoopMap.MSG_PLACE
                || type == CoopMap.MSG_MARKER_ADD || type == CoopMap.MSG_MARKER_DEL || type == CoopMap.MSG_PIECE_MOVE
                || type == CoopScene.MSG_MISSION_READY
                || type == CoopCards.MSG_CARD   // fire-mission cards are bidirectional — fan out to the other clients (N>2)
                // Requisition punchcards are peer/either-authored: any crew member may grab/move/place a card or turn
                // a recon dial, so the host must relay them to the OTHER clients (REVIEW-fix P1 — without this a
                // client's punchcard action reached the host but never the other clients at N>2). Host-only types
                // (MSG_PUNCH_DECK/CONSUME/GRAPH) stay off this list.
                || type == CoopPunchcards.MSG_PUNCH_GRAB || type == CoopPunchcards.MSG_PUNCH_POS
                || type == CoopPunchcards.MSG_PUNCH_PLACE || type == CoopPunchcards.MSG_PUNCH_DIAL;
        }

        // Streaming packet types the host relays UNRELIABLE (high-rate, loss-tolerant). MSG_POSE is handled by
        // CoopP2P, and mixed live/final types by IsMixedFinalStream. Everything else relays reliable so a lost
        // grab/release/click/snapshot/edit isn't dropped.
        public static bool IsUnreliableStream(byte type)
        {
            return type == MSG_VALUE || type == MSG_GROUP
                || type == CoopMap.MSG_POS
                || type == CoopEntities.MSG_MOVE
                || type == CoopPunchcards.MSG_PUNCH_POS;   // held-card position stream — loss-tolerant; the reliable PLACE finalizes
        }

        // Mixed-mode stream types: the SAME packet carries unreliable LIVE updates and a reliable FINAL/RELEASE
        // edge, distinguished by a flag byte at payload index 9 (flags&1 = final/release). The host relay must read
        // that flag instead of classifying by type alone, or the reliable final is silently downgraded to
        // unreliable on the host→client leg and a dropped final leaves the piece/dial stuck off (REVIEW-fix P1 —
        // mixed live/final relay reliability). Both layouts put the flag at index 9: [t][i32][i32-or-f32][flags].
        public static bool IsMixedFinalStream(byte type)
        {
            return type == CoopMap.MSG_PIECE_MOVE || type == CoopPunchcards.MSG_PUNCH_DIAL;
        }

        // origin = the SteamID that authored this packet (derived by CoopP2P from the Steam `from`, or the
        // host-stamped trailer on a relayed packet). Unused by the control handlers at the 2-player cap; Phase C
        // keys per-control ownership on it so a release from player B doesn't clear player C's lock.
        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            float now = Time.unscaledTime;
            int o = 1;
            switch (type)
            {
                case MSG_GRAB:
                {
                    if (len < 6) return;
                    int id = GetInt(a, ref o);
                    Group g = (Group)a[o++];
                    if (_byId.TryGetValue(id, out var c))
                    {
                        if (c.LocalOwned)
                        {
                            // Simultaneous grab: deterministic priority tie-break (host beats any client; among
                            // clients the lower SteamID wins). Every machine computes the same winner, so it
                            // converges with no grant/deny protocol. At the 2-player cap this is exactly "host
                            // keeps, client yields".
                            if (CoopP2P.GrabBeats(CoopP2P.MyId, origin)) return;   // I win — keep mine, ignore their grab
                            c.LocalOwned = false;                          // I lose — yield
                        }
                        // Adopt the incoming owner unless a higher-priority remote owner already holds it (N>2).
                        if (c.RemoteOwner == 0 || now >= c.RemoteUntil || c.RemoteOwner == origin || CoopP2P.GrabBeats(origin, c.RemoteOwner))
                        {
                            c.RemoteOwner = origin; c.RemoteUntil = now + StaleSec;
                            MarkGroupRemote(c.Grp, origin, now);
                            Log.LogInfo($"[ctrl] remote grabbed '{c.T.name}' (grp={c.Grp}) <- {origin}");
                        }
                    }
                    else { _pendingGrab[id] = origin; }
                    break;
                }
                case MSG_RELEASE:
                {
                    if (len < 5) return;
                    int id = GetInt(a, ref o);
                    // Optional settled group state rides this reliable packet (REVIEW-fix P1). Apply it ONCE,
                    // before clearing ownership, so the dial lands on the final value even if the live unreliable
                    // stream's last frame was lost. Strict-framed + finite-checked like MSG_GROUP.
                    if (o < len)
                    {
                        Group g = (Group)a[o++];
                        int gi = (int)g;
                        if (gi > 0 && gi < _grp.Length)
                        {
                            // Only honor a settled-final from the group's owner — a non-owner's release must not
                            // settle the dial (REVIEW-fix P1a). Ownership is still set here (we clear it below).
                            var gsOwn = _grp[gi];
                            bool ownerOk = !gsOwn.RemoteOwned || now >= gsOwn.Until || gsOwn.RemoteOwner == origin;
                            int n = GroupFloatCount(g);
                            if (ownerOk && len == o + 4 * n)
                            {
                                bool ok = true;
                                for (int i = 0; i < n; i++) { float f = GetFloat(a, ref o); if (!Finite(f)) { ok = false; break; } _tmpGroup[i] = f; }
                                if (ok)
                                {
                                    var gs = _grp[gi];
                                    for (int i = 0; i < n; i++) gs.V[i] = _tmpGroup[i];
                                    gs.Has = true; gs.Until = now + StaleSec;
                                    if (_turret != null && !LocallyOwnsGroup(g)) ApplyGroupValues(g, gs);   // settle authoritative
                                }
                            }
                        }
                    }
                    // Only the current owner's release clears ownership — so player B's release can't free a
                    // control player C is holding (at the 2-player cap origin always == the owner).
                    if (_byId.TryGetValue(id, out var c) && c.RemoteOwner == origin) { c.RemoteOwner = 0; RecomputeGroupRemote(c.Grp); Log.LogInfo($"[ctrl] remote released '{c.T.name}' <- {origin}"); }
                    if (_pendingGrab.TryGetValue(id, out var po) && po == origin) _pendingGrab.Remove(id);
                    break;
                }
                case MSG_VALUE:
                {
                    if (len < 9) return;
                    int id = GetInt(a, ref o);
                    float v = GetFloat(a, ref o);
                    // Only the control's current owner may move it. A non-owner's late value (simultaneous-grab
                    // contention at N>2) must not drag a control another origin already won (REVIEW-fix P1a).
                    if (_byId.TryGetValue(id, out var c))
                    {
                        bool ownerMatch = c.RemoteOwner == origin;
                        if (ownerMatch) { c.RemoteVal = v; c.HasRemoteVal = true; c.RemoteUntil = now + StaleSec; }
                    }
                    else _pendingVal[id] = (origin, v);   // arrived before the control registered — adopt only if this origin becomes owner
                    break;
                }
                case MSG_GROUP:
                {
                    if (len < 2) return;
                    Group g = (Group)a[o++];
                    int gi = (int)g;
                    if (gi <= 0 || gi >= _grp.Length) return;
                    var gsOwn = _grp[gi];
                    // Only the group's current owner may stream it. Reject a non-owner's contention frame (N>2);
                    // accept if nobody owns it yet (stream beat the grab) — matches the pre-cap behavior. (P1a)
                    if (gsOwn.RemoteOwned && now < gsOwn.Until && gsOwn.RemoteOwner != origin) return;
                    int n = GroupFloatCount(g);
                    if (len != o + 4 * n) return;                 // strict framing: exactly n floats, no more/less
                    for (int i = 0; i < n; i++) { float f = GetFloat(a, ref o); if (!Finite(f)) return; _tmpGroup[i] = f; }
                    var gs = _grp[gi];                            // whole frame valid → publish atomically
                    for (int i = 0; i < n; i++) gs.V[i] = _tmpGroup[i];
                    gs.Has = true; gs.Until = now + StaleSec;
                    break;
                }
                case MSG_CLICK:
                {
                    if (len < 5) return;
                    int id = GetInt(a, ref o);
                    if (_byId.TryGetValue(id, out var c) && c.Switch != null)
                    {
                        ReplayClick(c.Switch);
                        _echoUntil[id] = now + 0.3f;   // don't bounce our own replay back
                        Log.LogInfo($"[ctrl] applied remote click '{c.T.name}' <- peer");
                    }
                    break;
                }
                case MSG_FIRE:
                {
                    if (len < 2) return;
                    byte side = a[o++];
                    var gun = side == 0 ? _gunL : _gunR;
                    if (gun != null) { try { gun.RequestFire(); Log.LogInfo($"[ctrl] applied remote fire gun {side} <- peer"); } catch (Exception e) { Log.LogWarning("[ctrl] replay fire: " + e.Message); } }
                    _echoUntil[side == 0 ? FireKeyL : FireKeyR] = now + 0.3f;
                    break;
                }
                case MSG_POWDER:
                {
                    if (len < 6) return;   // t + side u8 + charges i32
                    byte side = a[o++];
                    int charges = GetInt(a, ref o);
                    var gun = side == 0 ? _gunL : _gunR;
                    if (gun != null)
                    {
                        try { if (gun.PowderCharges != charges) gun.SetPowderCharge(charges); } catch (Exception e) { Log.LogWarning("[ctrl] apply powder: " + e.Message); }
                        // Adopt as our baseline + suppress the echo so DetectPowder doesn't bounce it back. Adopting the
                        // peer's value yields authorship: the peer now re-asserts this gun's powder, not us (no flap).
                        if (side == 0) { _powderPrevL = charges; _powderAuthoredL = false; } else { _powderPrevR = charges; _powderAuthoredR = false; }
                        _echoUntil[side == 0 ? PowderKeyL : PowderKeyR] = now + 0.3f;
                        Log.LogInfo($"[ctrl] applied remote powder gun {side} <- peer ({charges})");
                    }
                    break;
                }
                case MSG_SNAP:
                {
                    if (len < 1 + 9 * 4) return;
                    var v = new float[9];
                    for (int i = 0; i < 9; i++) v[i] = GetFloat(a, ref o);
                    Log.LogInfo("[ctrl] received JIP turret snapshot <- peer");
                    if (_turret != null) ApplySnapshot(v);   // apply now if ready …
                    else _pendingSnap = v;                    // … else stash for Tick once the registry resolves
                    break;
                }
                case MSG_RECON:
                {
                    if (len < 1 + 9 * 4 || CoopP2P.IsHost || _turret == null) return;   // client applies; needs a turret
                    var v = new float[9];
                    for (int i = 0; i < 9; i++) v[i] = GetFloat(a, ref o);
                    ApplyRecon(v);
                    break;
                }
                default:
                    // Other co-op subsystems share the same P2P channel; forward by type.
                    if (type == CoopClipboard.MSG_SECTION || type == CoopClipboard.MSG_TOOL) CoopClipboard.OnPacket(type, a, len);
                    else if (type == CoopEntities.MSG_SPAWN || type == CoopEntities.MSG_UPDATE || type == CoopEntities.MSG_DESPAWN || type == CoopEntities.MSG_MOVE || type == CoopEntities.MSG_ENTSET) CoopEntities.OnPacket(type, a, len);
                    else if (type == CoopScene.MSG_MISSION_START || type == CoopScene.MSG_MISSION_END || type == CoopScene.MSG_MISSION_READY) CoopScene.OnPacket(type, origin, a, len);
                    else if (type == CoopOrders.MSG_ORDER) CoopOrders.OnPacket(type, a, len);
                    else if (type == CoopCards.MSG_CARD) CoopCards.OnPacket(type, a, len);
                    else if (type == CoopScore.MSG_OUTCOME || type == CoopScore.MSG_OPSTATE) CoopScore.OnPacket(type, a, len);
                    else if (type == CoopImpact.MSG_IMPACT) CoopImpact.OnPacket(type, a, len);
                    else if (type == CoopPunchcards.MSG_PUNCH_DECK || type == CoopPunchcards.MSG_PUNCH_REDEEM
                             || type == CoopPunchcards.MSG_PUNCH_GRAB || type == CoopPunchcards.MSG_PUNCH_POS
                             || type == CoopPunchcards.MSG_PUNCH_PLACE || type == CoopPunchcards.MSG_PUNCH_CONSUME
                             || type == CoopPunchcards.MSG_PUNCH_GRAPH || type == CoopPunchcards.MSG_PUNCH_DIAL) CoopPunchcards.OnPacket(type, origin, a, len);
                    else if (type == CoopNetDiag.MSG_DIGEST) CoopNetDiag.OnPacket(type, a, len);
                    else CoopMap.OnPacket(type, origin, a, len);
                    break;
            }
        }

        // Replay a click exactly as the cursor manager would (hover → press → release on its own Interactable),
        // so the game's own handler runs — switch animation + the gameplay effect (reload, powder, toggle).
        private static void ReplayClick(LookAtTarget sw)
        {
            try
            {
                var it = sw.interactable;
                if (it != null)
                {
                    try { sw.HandleHoverChangedFromManager(it); } catch { }
                    sw.HandleClickDownFromManager(it);
                    sw.HandleClickUpFromManager(it);
                }
                else { sw.OnClickDown(); sw.OnClickUp(); }
            }
            catch (Exception e) { Log.LogWarning("[ctrl] replay click: " + e.Message); }
        }

        // ---------------- diagnostics ----------------

        public static string Status()
        {
            int drag = 0, click = 0, local = 0, remote = 0;
            foreach (var c in _byId.Values)
            {
                if (c.Switch != null) click++; else drag++;
                if (c.LocalOwned) local++;
                if (c.RemoteOwner != 0) remote++;
            }
            int gOwn = 0; for (int g = 1; g < _grp.Length; g++) if (_grp[g].RemoteOwned) gOwn++;
            return $"controls: {drag} drag + {click} click registered, owned local={local} remote={remote}, " +
                   $"remote groups={gOwn}, guns L={_gunL != null} R={_gunR != null}, turret={_turret != null}";
        }

        public static void Dump()
        {
            Log.LogInfo("[ctrl] " + Status());
            foreach (var c in _byId.Values)
            {
                if (c.LocalOwned) Log.LogInfo($"[ctrl]   LOCAL-owned: '{c.T.name}' (grp={c.Grp})");
                else if (c.RemoteOwner != 0) Log.LogInfo($"[ctrl]   remote-owned: '{c.T.name}' (grp={c.Grp})");
            }
            for (int g = 1; g < _grp.Length; g++)
            {
                var gs = _grp[g];
                if (!gs.RemoteOwned && !gs.Has) continue;
                Log.LogInfo($"[ctrl]   group {(Group)g}: remoteOwned={gs.RemoteOwned} has={gs.Has} v=[{gs.V[0]:0.0},{gs.V[1]:0.0},{gs.V[2]:0.0},{gs.V[3]:0.0},{gs.V[4]:0.0}]");
            }
        }

        // ---------------- ownership queries (for HandManipulator refusal) ----------------

        // True if the peer currently owns the control behind this component, so the VR glove shouldn't grab
        // it (avoids a two-player tug-of-war; the flatscreen player's native drag yields via host priority).
        public static bool IsRemotelyOwned(Component c)
        {
            if (c == null || !Config.CoopControlSync) return false;
            float now = Time.unscaledTime;
            foreach (var e in _byId.Values)
            {
                if ((object)e.Dial == c || (object)e.Slider == c)
                    return e.RemoteOwner != 0 && now < e.RemoteUntil;
            }
            return false;
        }

        // ---------------- desync-detector hooks (CoopNetDiag) ----------------

        // True if the local player is operating any control right now, so the detector can skip the turret-angle
        // comparison while someone is actively slewing (that legitimately diverges across a laggy link).
        public static bool AnyLocalOwnership
        {
            get { foreach (var c in _byId.Values) if (c.LocalOwned) return true; return false; }
        }

        // Snapshot the convergent turret/gun CURRENT state for the digest. False if no turret is resolved.
        public static bool TryGetTurretDigest(out float rot, out float elevL, out float elevR, out int powderL, out int powderR)
        {
            rot = elevL = elevR = 0f; powderL = powderR = 0;
            var t = _turret; if (t == null) return false;
            try
            {
                rot = t.CurrentAngle;
                elevL = _gunL != null ? _gunL.CurrentElevation : 0f;
                elevR = _gunR != null ? _gunR.CurrentElevation : 0f;
                powderL = _gunL != null ? _gunL.PowderCharges : 0;
                powderR = _gunR != null ? _gunR.PowderCharges : 0;
                return true;
            }
            catch { return false; }
        }

        // ---------------- send ----------------

        private static void SendGrab(Ctrl c)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_GRAB; o = PutInt(o, c.NetId); _buf[o++] = (byte)c.Grp;
            CoopP2P.Send(_buf, o, true);
        }

        private static void SendRelease(Ctrl c)
        {
            if (!EnsureBuf()) return;
            // REVIEW-fix (P1): carry the SETTLED group state INSIDE the reliable release, instead of a separate
            // unreliable group packet that could be lost or arrive after the release (which clears RemoteOwned and
            // would freeze the dial at its last streamed value). Now release + final value land together, ordered.
            int o = 0; _buf[o++] = MSG_RELEASE; o = PutInt(o, c.NetId);
            int grpPos = o; _buf[o++] = (byte)c.Grp;
            if (c.Grp != Group.Other && _turret != null)
            {
                try { o = WriteGroupFloats(o, c.Grp); }
                catch { _buf[grpPos] = (byte)Group.Other; o = grpPos + 1; }   // couldn't settle — send a plain release
            }
            CoopP2P.Send(_buf, o, true);
            Log.LogInfo($"[ctrl] released '{c.T.name}' -> peer (settled grp={c.Grp})");
        }

        private static void SendClick(int netId)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_CLICK; o = PutInt(o, netId);
            CoopP2P.Send(_buf, o, true);
        }

        private static void SendFire(byte side)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_FIRE; _buf[o++] = side;
            CoopP2P.Send(_buf, o, true);
        }

        private static void SendPowder(byte side, int charges)
        {
            if (!EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_POWDER; _buf[o++] = side; o = PutInt(o, charges);
            CoopP2P.Send(_buf, o, true);   // reliable: a discrete state change, must not be lost
        }

        private static void SendValue(Ctrl c)
        {
            if (!EnsureBuf()) return;
            float v;
            try { v = c.Dial != null ? c.Dial.AccumulatedValue : c.Slider.CurrentDistance; } catch { return; }
            if (!Finite(v)) return;
            int o = 0; _buf[o++] = MSG_VALUE; o = PutInt(o, c.NetId); o = PutFloat(o, v);
            CoopP2P.Send(_buf, o, false);
        }

        private static void SendGroupState(Group g)
        {
            if (_turret == null || g == Group.Other || !EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_GROUP; _buf[o++] = (byte)g;
            try { o = WriteGroupFloats(o, g); } catch { return; }
            CoopP2P.Send(_buf, o, false);
            // DIAG (elevation desync hunt, sender side — self-sufficient on ONE log since host logs keep getting
            // overwritten): what we actually STREAM for elevation. If gunL.des is ~0 while gunL.cur is raised, the
            // control updates CURRENT not the DESIRED we send (capture bug → we must send current). If gunL.des
            // carries the raised value but the peer's gun stays at 0 (per the [diag] DESYNC peer= field), the peer
            // isn't adopting/slewing (apply bug).
            if (g == Group.Elevation && Time.unscaledTime >= _nextElevSendDiag)
            {
                _nextElevSendDiag = Time.unscaledTime + 1f;
                Log.LogInfo($"[ctrl] streaming elevation -> peer: turret.des={_turret.DesiredElevation:0.0} " +
                            $"gunL.des={(_gunL != null ? _gunL.DesiredElevationAngle : 0f):0.0} gunL.cur={(_gunL != null ? _gunL.CurrentElevation : 0f):0.0}");
            }
        }

        // Serialize a group's GroupFloatCount floats at offset o. Shared by the live MSG_GROUP stream and the
        // settled-state payload folded into the reliable MSG_RELEASE (REVIEW-fix P1).
        private static int WriteGroupFloats(int o, Group g)
        {
            switch (g)
            {
                case Group.Rotation:
                    o = PutFloat(o, _turret.DesiredRotation);   // desired aim only (intent sync)
                    break;
                case Group.Elevation:
                    o = PutFloat(o, _turret.DesiredElevation);
                    o = PutFloat(o, _gunL != null ? _gunL.DesiredElevationAngle : 0f);
                    o = PutFloat(o, _gunR != null ? _gunR.DesiredElevationAngle : 0f);
                    break;
                case Group.GunLeft:  o = PutGun(o, _gunL); break;
                case Group.GunRight: o = PutGun(o, _gunR); break;
            }
            return o;
        }

        private static int PutGun(int o, GunController gun)
        {
            int powder = 0; bool reloading = false;
            try { if (gun != null) { powder = gun.PowderCharges; reloading = gun.IsReloading; } } catch { }
            o = PutFloat(o, powder);
            o = PutFloat(o, reloading ? 1f : 0f);
            return o;
        }

        private static int GroupFloatCount(Group g) => g switch
        {
            Group.Rotation => 1,
            Group.Elevation => 3,
            Group.GunLeft => 2,
            Group.GunRight => 2,
            _ => 0,
        };

        // ---------------- registry ----------------

        private static void EnsureRegistry()
        {
            TurretController turret = null;
            try { turret = TurretController.Instance; } catch { }
            if (turret == null) { if (_byId.Count > 0) { _byId.Clear(); _turret = null; _turretIid = -1; } return; }

            int iid = turret.GetInstanceID();
            bool rescan = _turretIid != iid || _byId.Count == 0 || Time.unscaledTime >= _nextScan;
            if (!rescan) return;
            _nextScan = Time.unscaledTime + 3f;

            if (_turretIid != iid)   // new turret = new scene: drop the stale registry + group state
            {
                _byId.Clear();
                for (int i = 0; i < _grp.Length; i++) { _grp[i].RemoteOwned = false; _grp[i].RemoteOwner = 0; _grp[i].Has = false; }
            }
            _turret = turret; _turretIid = iid;
            ResolveGuns(turret);

            _reconExcluded = 0;
            int added = 0;
            added += Scan(Il2CppType.Of<DialInteractable>(), true);
            added += Scan(Il2CppType.Of<LinearSliderInteractable>(), false);
            int sw = ScanSwitches();
            if (added + sw > 0) Log.LogInfo($"[ctrl] registry: {_byId.Count} controls (+{added} drag, +{sw} click, {_reconExcluded} recon-dials-excluded; host={CoopP2P.IsHost})");
        }

        private static int Scan(Il2CppSystem.Type t, bool dial)
        {
            int added = 0;
            var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
            if (arr == null) return 0;
            for (int i = 0; i < arr.Length; i++)
            {
                Transform tr = null;
                DialInteractable d = null; LinearSliderInteractable s = null;
                try
                {
                    if (dial) { d = arr[i].TryCast<DialInteractable>(); tr = d != null ? d.transform : null; }
                    else { s = arr[i].TryCast<LinearSliderInteractable>(); tr = s != null ? s.transform : null; }
                }
                catch { }
                if (tr == null) continue;

                string path = PathOf(tr);
                // Skip ALL requisition-console dials — their path-hash collides with the gun's same-named targeting
                // dials, and CoopPunchcards owns them (console-relative key + SetDialValue). The submit LEVER is a click
                // (ScanSwitches), not a dial, so it still syncs.
                if (dial && path.IndexOf("Requisition Console", StringComparison.OrdinalIgnoreCase) >= 0) { _reconExcluded++; continue; }
                int id = Fnv(path);
                if (_byId.TryGetValue(id, out var exist))
                {
                    // The periodic rescan (every 3s) does NOT clear _byId (it must preserve live ownership/value
                    // state), so an already-registered control re-appears here every scan — that's NOT a collision,
                    // just a re-find of the SAME transform; skip it silently. Only warn on a TRUE id clash (two
                    // DIFFERENT transforms hashing to one NetId — the peer couldn't tell them apart).
                    bool sameControl = false;
                    try { sameControl = exist.T != null && exist.T.GetInstanceID() == tr.GetInstanceID(); } catch { }
                    if (!sameControl && _dialCollisions < 12)
                    {
                        _dialCollisions++;
                        Log.LogWarning($"[ctrl] dial NetId COLLISION: '{tr.name}' (grp={Classify(path)}) hashes to id={id}, already held by '{SafeName(exist)}' (grp={exist.Grp}) — DROPPED. path='{path}'");
                    }
                    continue;
                }
                var c = new Ctrl { NetId = id, T = tr, Dial = d, Slider = s, Grp = Classify(path) };

                // Apply anything that arrived for this control before it existed.
                if (_pendingGrab.TryGetValue(id, out var pgOrigin)) { c.RemoteOwner = pgOrigin; c.RemoteUntil = Time.unscaledTime + StaleSec; MarkGroupRemote(c.Grp, pgOrigin, Time.unscaledTime); _pendingGrab.Remove(id); }
                if (_pendingVal.TryGetValue(id, out var pv)) { if (c.RemoteOwner == pv.Origin) { c.RemoteVal = pv.V; c.HasRemoteVal = true; } _pendingVal.Remove(id); }

                _byId[id] = c;
                added++;
            }
            return added;
        }

        // Register click controls (LookAtTarget). Skips the "operating manual" props (PickUpZoomTarget — those
        // are local read-zoom, not shared state, same as HandManipulator) and menu/mission-select pages (whose
        // clicks shouldn't drive the peer's UI). Cockpit switches/levers/buttons (reload, powder, power,
        // lighting, fire button) all pass.
        private static int ScanSwitches()
        {
            int added = 0;
            var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<LookAtTarget>(), FindObjectsSortMode.None);
            if (arr == null) return 0;
            for (int i = 0; i < arr.Length; i++)
            {
                LookAtTarget sw = null;
                try { sw = arr[i].TryCast<LookAtTarget>(); } catch { }
                if (sw == null) continue;
                Transform tr = sw.transform;
                if (tr == null || HasInParent<PickUpZoomTarget>(tr)) continue;     // manual read-zoom — skip
                string path = PathOf(tr);
                if (IsMenuish(path)) continue;
                int id = Fnv(path);
                if (_byId.ContainsKey(id)) continue;
                _byId[id] = new Ctrl { NetId = id, T = tr, Switch = sw, Grp = Classify(path) };
                added++;
            }
            return added;
        }

        private static bool IsMenuish(string path)
        {
            string p = path.ToLowerInvariant();
            // Skip menu/mission-select AND the mission-LAUNCH buttons (Play/Deploy/Start Operation): the mission
            // lifecycle is host-authoritative via CoopScene (host's OperationID → client loads the matching relay).
            // Replicating the launch click would fire a second, conflicting start on the client. See CoopScene.
            return p.Contains("menu") || p.Contains("campaign") || p.Contains("mission select")
                || p.Contains("missionselect") || p.Contains("settings") || p.Contains("operating manual")
                || p.Contains("play button") || p.Contains("deploy") || p.Contains("start operation") || p.Contains("launch operation");
        }

        private static bool HasInParent<T>(Transform t) where T : Component
        {
            for (Transform p = t; p != null; p = p.parent)
                if (p.GetComponent<T>() != null) return true;
            return false;
        }

        private static void ResolveGuns(TurretController turret)
        {
            _gunL = null; _gunR = null;
            try
            {
                var guns = turret.guns;
                if (guns == null) return;
                int n = guns.Count;
                for (int i = 0; i < n; i++)
                {
                    var g = guns[i];
                    if (g == null) continue;
                    string nm = "";
                    try { nm = g.name.ToLowerInvariant(); } catch { }
                    if (nm.Contains("left")) _gunL = g;
                    else if (nm.Contains("right")) _gunR = g;
                    else if (_gunL == null) _gunL = g;     // fall back to order if names are unhelpful
                    else if (_gunR == null) _gunR = g;
                }
            }
            catch (Exception e) { Log.LogWarning("[ctrl] guns: " + e.Message); }
        }

        // ---------------- ownership bookkeeping ----------------

        private static void MarkGroupRemote(Group g, ulong origin, float now)
        {
            int gi = (int)g; if (gi <= 0 || gi >= _grp.Length) return;
            var gs = _grp[gi];
            // Adopt the new origin as the group's owner unless a fresher, higher-priority owner already holds it
            // (deterministic GrabBeats tie-break, same as control-level ownership). At N=2 there's only one remote.
            if (!gs.RemoteOwned || now >= gs.Until || gs.RemoteOwner == origin || CoopP2P.GrabBeats(origin, gs.RemoteOwner))
                gs.RemoteOwner = origin;
            gs.RemoteOwned = true; gs.Until = now + StaleSec;
        }

        private static void RecomputeGroupRemote(Group g)
        {
            int gi = (int)g; if (gi <= 0 || gi >= _grp.Length) return;
            // Recompute owner from the surviving remote-owned controls: the highest-priority owner wins the group.
            ulong owner = 0;
            foreach (var c in _byId.Values)
            {
                if (c.Grp != g || c.RemoteOwner == 0) continue;
                if (owner == 0 || CoopP2P.GrabBeats(c.RemoteOwner, owner)) owner = c.RemoteOwner;
            }
            _grp[gi].RemoteOwned = owner != 0;
            _grp[gi].RemoteOwner = owner;
        }

        private static void ClearOwnership()
        {
            foreach (var c in _byId.Values) { c.LocalOwned = false; c.RemoteOwner = 0; c.PrevDragging = false; c.PrevClicked = false; c.HasRemoteVal = false; }
            for (int i = 0; i < _grp.Length; i++) { _grp[i].RemoteOwned = false; _grp[i].RemoteOwner = 0; _grp[i].Has = false; }
            _pendingGrab.Clear(); _pendingVal.Clear(); _echoUntil.Clear();
            _firedPrevL = false; _firedPrevR = false; _pendingSnap = null;
            _powderPrevL = int.MinValue; _powderPrevR = int.MinValue;   // re-seed powder baseline on next connect
            _powderAuthoredL = false; _powderAuthoredR = false; _nextPowderBeat = 0f;
            RestoreGunDrive();   // hand gun-elevation control back to the local turret controller on link-down
        }

        private static bool IsDragging(Ctrl c)
        {
            try { return c.Dial != null ? c.Dial.isDragging : (c.Slider != null && c.Slider.isDragging); }
            catch { return false; }
        }

        private static string SafeName(Ctrl c) { try { return c.T != null ? c.T.name : "?"; } catch { return "?"; } }

        private static Group Classify(string path)
        {
            string p = path.ToLowerInvariant();
            if (p.Contains("rotation")) return Group.Rotation;
            if (p.Contains("elevation")) return Group.Elevation;
            if (p.Contains("gun system left") || p.Contains("gunleft")) return Group.GunLeft;
            if (p.Contains("gun system right") || p.Contains("gunright")) return Group.GunRight;
            return Group.Other;
        }

        // ---------------- helpers ----------------

        // FULL hierarchy path, each segment disambiguated by its SIBLING INDEX. Name-only paths collide whenever two
        // sibling GameObjects share a name — and the cockpit is full of them: the gun loading consoles have several
        // identical "PressureValve/Dial" subtrees, the aiming console has paired Elevation/Rotation pressure dials, the
        // turret has duplicate ".Spur Gear 12 DRIVER" wheels. Name-only, those hash to ONE id and the second is dropped,
        // so the elevation + loading dials never register → their group never detects ownership → elevation/powder stop
        // syncing (only the uniquely-named rotation Lever survived). The sibling index is authored into the scene/prefab,
        // identical on both machines for the same build, so '#i' keeps every id deterministic AND collision-free.
        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder();
            sb.Append(t.name).Append('#').Append(SiblingIndex(t));
            for (var p = t.parent; p != null; p = p.parent)
                sb.Insert(0, p.name + "#" + SiblingIndex(p) + "/");
            return sb.ToString();
        }

        private static int SiblingIndex(Transform t) { try { return t.GetSiblingIndex(); } catch { return 0; } }

        // FNV-1a 32-bit — deterministic across processes (unlike string.GetHashCode), so both machines derive
        // the same id from the same transform path.
        private static int Fnv(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= 16777619u; }
            return unchecked((int)h);
        }

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(64); return true; }
            catch (Exception e) { Log.LogWarning("[ctrl] buf: " + e.Message); return false; }
        }

        private static int PutInt(int o, int v) { _buf[o] = (byte)v; _buf[o + 1] = (byte)(v >> 8); _buf[o + 2] = (byte)(v >> 16); _buf[o + 3] = (byte)(v >> 24); return o + 4; }
        private static int PutFloat(int o, float v) { int __b = BitConverter.SingleToInt32Bits(v); _buf[o] = (byte)__b; _buf[o + 1] = (byte)(__b >> 8); _buf[o + 2] = (byte)(__b >> 16); _buf[o + 3] = (byte)(__b >> 24); return o + 4; }

        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_f4, 0); }
        private static float GetFloat(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToSingle(_f4, 0); }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
