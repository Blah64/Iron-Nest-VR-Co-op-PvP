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
    /// Co-op: cockpit-control replication with TRANSIENT, PER-CONTROL ownership, plus turret/gun
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
    ///   shared DesiredRotation. We sync CurrentAngle exactly ONCE, in the JIP snapshot, for a tight start —
    ///   never in the live stream, which is what made the host fight and override the client when we streamed
    ///   physical state continuously.
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
        private const byte MSG_FIRE = 7;     // [t][side u8][tgtX f32][tgtY f32][startX f32][startY f32][travelTime f32]  reliable  gun discharge (0=L,1=R) + the SHOOTER's full ShellVisual flight (crater + launch point + travel time); the peer forces it onto its own shell so the whole arc + crater match regardless of pre-shot desync (see CoopBallistics)
        private const byte MSG_SNAP = 13;    // [t][9×f32]                   reliable   join-in-progress turret/gun state
        private const byte MSG_RECON = 25;   // [t][9×f32]                   reliable   recurring host current-state reconcile
        private const byte MSG_POWDER = 39;  // [t][side u8][charges i32]    reliable   per-gun powder/charge state, EITHER→peer (symmetric, last-writer-wins)
        private const byte MSG_AIM = 41;     // [t][desRot f32][turretElev f32][gunLElev f32][gunRElev f32]  reliable  EITHER→peer, symmetric firing-solution edge-replicate. The FDC graph resolves the aim with NO drag, so it never rides the drag-owned MSG_GROUP stream → the peer kept its stale default and missed.
        private const byte MSG_TURRET_POS = 45;  // [t][gridX f32][gridY f32]  reliable  HOST→client, host-authoritative turret MAP ORIGIN (turretBase.anchoredPosition). Edge-on-change (throttled) + ~2s heal beat. NOT client-authored (never relayed): the host is the sole author and Send() already fans out to every client. Cross-player divergence: identical aim/range lands at a different MAP point when each machine's turret sits at a different origin.
        private const byte MSG_RELOAD_STATE = 46;  // [t][side u8][stateIdx u8][loaded u8 (0/1)][powder i32]  reliable  EITHER→peer, symmetric per-gun reload state. The reload is animation-gated (click-replay can't drive a remote one — it stalls), so we replicate the AUTHORITATIVE state and the peer reaches it through the game's OWN paced AdvanceState() path (never pokes internals — eject corrupts). Co-op + same-team PvP LOAD direction.

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

        // Echo guard: when we REPLAY a peer's click, the game flips isClicked on our side too, which our own poll
        // would re-detect and bounce back. Suppress local detection for a control (keyed by netId) for a short
        // window after a replay. (Fire no longer needs this: a shot is announced from the impact postfix, and a
        // replayed shell is a "remote" shot the postfix skips - so it can never bounce. See CoopBallistics.)
        private static readonly Dictionary<int, float> _echoUntil = new Dictionary<int, float>();
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
        // Aim / firing-solution edge-replication (mirrors the powder pattern above). The bearing+elevation is often
        // resolved INDIRECTLY — the fire-direction-center punchcard graph slews the turret on submit, with NO drag
        // on a group-mapped control — so MSG_GROUP (which only streams while a drag OWNS the group) never carries it
        // and the peer keeps its stale default aim → its replayed shot misses (the "both consoles read the same
        // bearing but the shells land differently" desync). So, like powder, we edge-replicate the RESOLVED values
        // whenever they change, however produced. NaN = "no baseline yet" (seed silently; join-time aim rides the
        // JIP MSG_SNAP). Echo-suppressed via _echoUntil[AimKey]; adopt-clears-authorship so the two never flap.
        private static float _aimPrevRot = float.NaN, _aimPrevElev = float.NaN, _aimPrevElevL = float.NaN, _aimPrevElevR = float.NaN;
        private static bool _aimAuthored;
        private static float _nextAimBeat;
        private const float AimHeartbeatSec = 2f;
        private const float AimEps = 0.05f;   // deg threshold: ignore sub-tenth jitter so we don't spam the stream
        private static readonly int AimKey = Fnv("__coop_aim__");

        // Per-gun RELOAD-state edge-replication (mirrors the powder/aim pattern). The reload is a
        // 10-state animation-gated machine — replaying the cockpit CLICKS to a remote gun
        // stalls it (the clip sub-state never lines up). So we replicate the AUTHORITATIVE (stateIdx, loaded, powder),
        // and the peer DRIVES its own gun to that target via the game's legit AdvanceState()/SetPowderCharge, paced by
        // the controller's `working` flag, NEVER poking chamber/eject/SetState (those corrupt).
        // Symmetric last-writer-wins + echo-suppress + heartbeat, exactly like powder. EMPTY/fired direction is owned
        // by the MSG_FIRE replay in co-op (deferred in same-team PvP) — we never drive a gun empty here.
        private static readonly int ReloadKeyL = Fnv("__coop_reload_L"), ReloadKeyR = Fnv("__coop_reload_R");
        private static float _nextReloadBeat;
        private const float ReloadHeartbeatSec = 2f;
        // Target MODE is derived in ONE place (never inferred from `loaded` alone — it's overloaded: empty-rest at idx 0
        // vs actively-reloading at idx>0): drive toward loaded UNLESS empty-rest (idx==0 && !loaded).
        private struct ReloadSide
        {
            public int  PrevIdx;      // last sampled CurrentStateIndex (int.MinValue = unseeded, seed silently)
            public bool PrevLoaded;   // last sampled ChamberedShellBlueprint != null
            public bool Authored;     // we own re-asserting this gun's reload until we adopt the peer's
            // paced driver (the converger toward a LoadedRest/Reloading target)
            public bool  Driving;
            public int   TgtPowder;
            public float Deadline;    // unscaledTime hard stop
            public float LastActT;    // min spacing between actions
            public int   Actions;     // action cap
            public int   LastSeenIdx; // stall detector
            public int   SameIdx;
        }
        private static ReloadSide _rsL = new ReloadSide { PrevIdx = int.MinValue, LastSeenIdx = int.MinValue };
        private static ReloadSide _rsR = new ReloadSide { PrevIdx = int.MinValue, LastSeenIdx = int.MinValue };
        private const float ReloadDriveSpacingSec = 0.2f;   // ≤5 legit actions/sec (let each clip play)
        private const float ReloadDriveTimeoutSec = 30f;
        private const int   ReloadDriveStallActions = 6;    // consecutive actions w/o index movement → stop+log
        private const int   ReloadDriveActionCap = 80;
        // Startup grace: the gun's spawn-time initialization (and any mission-start auto-load) runs IDENTICALLY and
        // LOCALLY on both machines — replicating it would make the peer redundantly drive its own guns ("host started
        // loading both cannons on spawn"). So for a few seconds after the turret resolves we SEED baselines silently
        // (track current, never send) — only PLAYER-driven changes after the gun has settled get replicated. Deliberate
        // join-time state will ride the JIP reseed, not this noisy spawn window.
        private static float _reloadStartupUntil;
        private const float ReloadStartupGraceSec = 6f;

        // ===== Host-side RELOAD-CLICK PACING — fixes the intermittent reload desync. =====
        // The reload syncs by REPLAYING the operator's lever/button clicks; the peer LAGS (each step's animation + net
        // latency), so a click the operator sent at reload state N arrives while the peer is still at N-1. Applying it
        // immediately lands it at the WRONG state and breaks the sequence — observed: a 'Charge Rammer' click applied at
        // SelectPowderCharge (instead of RamCharges) killed the dispense->auto-advance, sticking the peer mid-reload
        // ("shell loaded but no powders came out"). FIX: queue ALL of a gun's reload-step clicks per side and drain them
        // strictly IN ORDER, applying the next only when the gun is idle (controller `working`==False) + a small settle —
        // so a step's animation and the auto-advance it triggers finish before the next click lands. Non-reload clicks
        // still apply immediately. (The dispensers live on PowderChargeController, so they must be matched too — else
        // they'd take the immediate lane and jump ahead of a still-queued earlier step, breaking order.)
        // SINGLE queue: the reload controls are SHARED between L/R (the button names carry no side — 'Button Dispencer',
        // 'Charge Rammer', 'Move Cylinder'), so only one cannon is loaded at a time. Ordering is global; the !working gate
        // checks BOTH guns (the idle gun's flag is false anyway). Reload buttons are identified by NAME (the per-gun
        // ref-match fails for these shared controls — they aren't ref-equal to either gun's controller members).
        private struct PendingReloadClick { public int NetId; public float EnqueuedAt; }
        private static readonly System.Collections.Generic.Queue<PendingReloadClick> _reloadClickQ = new System.Collections.Generic.Queue<PendingReloadClick>();
        private static float _reloadClickLastApply;
        private const float ReloadClickSettleSec   = 0.20f;   // min gap between applies — spans the !working->auto-advance race + paces same-state clicks
        private const float ReloadClickTimeoutSec  = 25f;     // drop a click the peer can't reach a state for, so the queue can't wedge the rest
        private const int   ReloadClickQueueCap    = 32;

        // Host-authoritative turret MAP ORIGIN broadcast (MSG_TURRET_POS). Edge-on-change with a throttle (a scripted
        // State_MoveTurret animates anchoredPosition continuously — we cap the send rate so an animated move ships a
        // handful of reliable packets, not one per frame; the change-detect's last send is the settled value) + a slow
        // heal beat (re-assert so a dropped packet / late joiner converges; the client applies idempotently).
        private static Vector2 _turretPosPrev = new Vector2(float.NaN, float.NaN);   // last grid we SENT (host)
        private static float _nextTurretPosSend;   // host: throttle floor between change-driven sends
        private static float _nextTurretPosBeat;   // host: next unconditional heal beat
        private const float TurretPosEps = 0.05f;          // grid units: ignore float jitter
        private const float TurretPosMinSendSec = 0.2f;    // ≤5 change-sends/sec during an animated move
        private const float TurretPosHeartbeatSec = 2f;

        // Join-in-progress snapshot received before the turret/guns resolved locally (scene still loading on the
        // joiner). Held here and applied once the registry is ready — see Tick / ApplySnapshot.
        private static float[] _pendingSnap;

        private static TurretController _turret;
        private static GunController _gunL, _gunR;
        private static int _turretIid = -1;

        private static float _nextScan;
        private static float _nextSend;
        private static float _nextRecon;   // host: next current-state reconcile broadcast
        private static float _nextElevDiag;     // throttle for the "applied peer elevation" (receiver) diagnostic
        private static float _nextElevSendDiag;  // throttle for the "streaming elevation" (sender) diagnostic
        // ALL requisition-console dials are EXCLUDED from this path-hash registry: their PathOf hash collides with the
        // artillery gun's same-named targeting dials (.Gross Bearing/.Range), AND the console's real coordinate input is
        // FOUR dials (two Grid-Location .Range Dials + gross/fine bearing), not just the bridge's one. CoopPunchcards
        // owns them — keyed by console-relative path (collision-free, every relpath is unique under this root), applied
        // with SetDialValue (the proven setter). Counted per scan for the registry log.
        private static int _reconExcluded;
        // Pressure-valve repair dials are owned by CoopPressure (MSG_VALVE / SetDamage01), not the dial-visual stream
        // (the valve reads its dial via an event SetAccumulatedValueUnlimited won't fire; and a valve grab would mark
        // the turret group remotely-owned). Excluded from the registry — counted per scan for the log.
        private static int _valveExcluded;

        // Reusable send buffer (control packets are tiny: 1+4+4 max). Lazily created on the Unity thread.
        private static Il2CppStructArray<byte> _buf;
        // Scratch for strict group-packet parsing: read into here, validate every float finite, then publish to
        // gs.V only if the whole frame is intact (no partial/non-finite turret state).
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
            // PvP (Phase B): control sync now runs for TEAMMATES too — a team SHARES one turret. We still broadcast
            // (the host relay fans out), but the receive-side team gate (OnPacket) drops an opponent's control
            // packets and they drop ours, so only teammates converge on the shared aim. (Was a hard return here,
            // which left a 2v2 team's two crew desynced.) Firing stays per-player (CoopBallistics still gated) so a
            // shot is adjudicated once by its shooter — sharing the gun is Phase C.
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
                            if (_grabs <= 50) Diagnostics.V($"[ctrl] grabbed '{c.T.name}' (grp={c.Grp}) — local owner");
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

                DetectPowder(now);
                DetectAim(now);
                DetectTurretPos(now);
                DetectReload(now);        // reload state mirror — ships and is called every frame, but no-ops at runtime (ReloadStateMirrorEnabled=false, not #if-stripped); reload syncs via paced click replay
                DriveReloadTick(now);     // AdvanceState driver — ships and is called every frame, but no-ops at runtime alongside the mirror
                DrainReloadClicks(now);   // pace replayed reload clicks so each lands at the right state (the intermittent-desync fix)

                // Host broadcasts a low-rate CURRENT-state reconcile so the client can correct any
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
                Diagnostics.V($"[ctrl] click '{c.T.name}' -> peer");
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
                    if ((object)c.Switch == sw) { SendClick(c.NetId); Diagnostics.V($"[ctrl] click '{c.T.name}' (glove) -> peer"); return; }
            }
            catch { }
        }

        // ---------------- fire events ----------------

        // A gun discharge is announced to the peer from the SHELLVISUAL hook (CoopBallistics.OnShellVisualPost ->
        // SendLocalShot), NOT from the GunController.hasFired edge. hasFired flips at RequestFire, BEFORE the fireDelay
        // coroutine runs FireShell (the old edge shipped NaN). And the map-space hit point isn't resolved until the
        // shell LANDS seconds later - too late to fire the peer. ShellVisual.Initialize runs at fire time, inside
        // FireShell, and carries the board-local landing target = exactly what makes the visible shell land where the
        // shooter's did. So we ship THAT. See [[ironnest-aim-desync]].
        //
        // Called by CoopBallistics for a LOCAL shot only (a replayed/remote shot is skipped there, which also breaks
        // the fire loop - no echo guard needed). Ships the whole flight (start, target, travelTime) so the peer's arc
        // matches launch point + direction + speed, not just the crater.
        // Side index of a gun (0 = Left, 1 = Right), matching _gunL/_gunR. The canonical mapping used by the fire path
        // + CoopBallistics' per-side intent queue. Null -> -1 so callers' `side < 0` guard early-returns (never a
        // silent wrong side); any non-_gunR gun -> 0 (Left).
        public static int SideOfGun(GunController gun)
        {
            if (gun == null) return -1;
            return ((object)gun == (object)_gunR) ? 1 : 0;
        }

        internal static void SendLocalShot(GunController gun, Vector2 tgt, Vector2 start, float time)
        {
            // Gate on the FIRE feature (CoopBallistics.Active() = CoopDeterministicFire + lobby + peer + mission + !PvP),
            // NOT CoopClickSync — fire is no longer a click event, so turning click sync off must not silently kill the
            // shell-flight sync. CoopClickSync still gates the OTHER cockpit clicks.
            if (!CoopBallistics.Active()) return;
            byte side = ((object)gun == (object)_gunR) ? (byte)1 : (byte)0;
            SendFire(side, tgt, start, time);
            Diagnostics.V($"[ctrl] fire gun {side} -> peer  start=({start.x:0.0},{start.y:0.0}) tgt=({tgt.x:0.0},{tgt.y:0.0})");
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
                Diagnostics.V($"[ctrl] powder gun {side} -> peer ({cur})");
            }
            return cur;
        }

        // ---------------- aim / firing-solution (symmetric, value-edge) ----------------

        // Edge-replicate the RESOLVED turret aim so it converges however it was produced. The fire-direction-center
        // punchcard graph slews the turret on submit with NO drag on a group-mapped control, so the drag-owned
        // MSG_GROUP stream never carries it and the peer keeps its stale default (its replayed shot then misses).
        // Mirrors DetectPowder: send on change, re-assert what WE authored on a low-rate heartbeat (self-heals a lost
        // reliable packet), adopt-clears-authorship so the two never flap. Skipped while WE actively drag an aim
        // group — the live MSG_GROUP stream already carries that and our input must win.
        private static void DetectAim(float now)
        {
            if (!Config.CoopControlSync || _turret == null) return;
            if (LocallyOwnsGroup(Group.Rotation) || LocallyOwnsGroup(Group.Elevation)) return;

            float rot, elev, eL = 0f, eR = 0f;
            try { rot = _turret.DesiredRotation; elev = _turret.DesiredElevation; } catch { return; }
            try { if (_gunL != null) eL = _gunL.DesiredElevationAngle; } catch { }
            try { if (_gunR != null) eR = _gunR.DesiredElevationAngle; } catch { }
            if (!CoopWire.Finite(rot) || !CoopWire.Finite(elev) || !CoopWire.Finite(eL) || !CoopWire.Finite(eR)) return;

            bool suppressed = _echoUntil.TryGetValue(AimKey, out var u) && now < u;
            bool seeded = !float.IsNaN(_aimPrevRot);
            // DeltaAngle for the wrap-around bearing; plain diff for the (bounded) elevations.
            bool changed = seeded && (Mathf.Abs(Mathf.DeltaAngle(rot, _aimPrevRot)) > AimEps
                                      || Mathf.Abs(elev - _aimPrevElev) > AimEps
                                      || Mathf.Abs(eL - _aimPrevElevL) > AimEps
                                      || Mathf.Abs(eR - _aimPrevElevR) > AimEps);
            if (changed && !suppressed)
            {
                SendAim(rot, elev, eL, eR);
                _aimAuthored = true;   // a local aim change → we own re-asserting it until we adopt the peer's
                Diagnostics.V($"[ctrl] aim -> peer rot={rot:0.0} elev={elev:0.0} gunL={eL:0.0} gunR={eR:0.0}");
            }
            _aimPrevRot = rot; _aimPrevElev = elev; _aimPrevElevL = eL; _aimPrevElevR = eR;

            // Heartbeat self-heal: re-assert the aim WE authored at a low rate so a value lost in a link-drop blackout
            // re-converges (the peer adopts it idempotently; its diff-guard makes a matching value a silent no-op).
            if (now >= _nextAimBeat)
            {
                _nextAimBeat = now + AimHeartbeatSec;
                if (_aimAuthored) { try { SendAim(rot, elev, eL, eR); } catch { } }
            }
        }

        private static void SendAim(float rot, float elev, float elevL, float elevR)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_AIM); w.Float(rot); w.Float(elev); w.Float(elevL); w.Float(elevR);
            CoopP2P.Send(_buf, w.Length, true);   // reliable: a discrete firing-solution change, must not be lost
        }

        // ===== Per-gun RELOAD-state replication — mirrors powder/aim. Detect+send on the operating
        // machine; the peer DRIVES its own gun to the target via the game's legit AdvanceState path. =====

        // Runtime kill-switch: the reload syncs via CLICK replication instead. The MSG_RELOAD_STATE mirror could not
        // drive a non-operated gun (animation-gated). This whole mirror/driver still COMPILES INTO EVERY BUILD and is
        // CALLED EVERY FRAME — it is NOT #if-stripped — but no-ops at runtime while this flag is false. Kept (not deleted)
        // because it may return as a self-healing RECONCILER (re-fire the missing step toward the peer's state target).
        // `readonly` (not const) so the runtime-dead branch doesn't trip CS0162.
        private static readonly bool ReloadStateMirrorEnabled = false;

        private static void DetectReload(float now)
        {
            if (!Config.CoopControlSync || !ReloadStateMirrorEnabled) return;   // mirror disabled → emit nothing (don't interfere with click sync)
            DetectReloadGun(ref _rsL, _gunL, 0, ReloadKeyL, now);
            DetectReloadGun(ref _rsR, _gunR, 1, ReloadKeyR, now);

            // Self-heal heartbeat: re-assert the reload state WE authored (and aren't currently driving from a peer)
            // so a value lost in a link-drop blackout re-converges (the peer adopts/drives idempotently).
            if (now >= _nextReloadBeat)
            {
                _nextReloadBeat = now + ReloadHeartbeatSec;
                if (_rsL.Authored && !_rsL.Driving && _gunL != null) { try { ReSendReload(0, _gunL); } catch { } }
                if (_rsR.Authored && !_rsR.Driving && _gunR != null) { try { ReSendReload(1, _gunR); } catch { } }
            }
        }

        // Edge-detect (idx OR loaded) and push. Skipped while we're DRIVING this gun from a peer's target (so our own
        // driver's AdvanceState calls don't echo back as locally-authored changes). Powder alone is NOT a trigger
        // (it rides MSG_POWDER) but is carried as a passenger so the peer converges chamber+powder together.
        private static void DetectReloadGun(ref ReloadSide rs, GunController gun, byte side, int echoKey, float now)
        {
            if (gun == null || rs.Driving) return;
            ArtilleryReloadController rc = null;
            try { rc = gun.artilleryReloadController; } catch { }
            if (rc == null) return;
            int idx; bool loaded; int powder;
            try { idx = rc.CurrentStateIndex; loaded = gun.ChamberedShellBlueprint != null; powder = gun.PowderCharges; }
            catch { return; }
            if (idx == rs.PrevIdx && loaded == rs.PrevLoaded) return;
            bool suppressed = _echoUntil.TryGetValue(echoKey, out var u) && now < u;
            // prev==MinValue: first sample after (re)connect — seed silently (join state rides MSG_SNAP / JIP reseed).
            // now < _reloadStartupUntil: still in the spawn grace — track the baseline but DON'T replicate the
            // identical-on-both spawn/initial-load sequence (else the peer redundantly drives its own guns).
            if (rs.PrevIdx != int.MinValue && !suppressed && now >= _reloadStartupUntil)
            {
                SendReload(side, idx, loaded, powder);
                rs.Authored = true;   // a local change → we own re-asserting this gun's reload until we adopt the peer's
                Diagnostics.V($"[ctrl] reload gun {side} -> peer (idx={idx} loaded={loaded} powder={powder})");
            }
            rs.PrevIdx = idx; rs.PrevLoaded = loaded;
        }

        private static void ReSendReload(byte side, GunController gun)
        {
            ArtilleryReloadController rc = null; try { rc = gun.artilleryReloadController; } catch { }
            if (rc == null) return;
            int idx; bool loaded; int powder;
            try { idx = rc.CurrentStateIndex; loaded = gun.ChamberedShellBlueprint != null; powder = gun.PowderCharges; } catch { return; }
            SendReload(side, idx, loaded, powder);
        }

        private static void SendReload(byte side, int idx, bool loaded, int powder)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_RELOAD_STATE); w.Byte(side); w.Byte((byte)idx); w.Byte((byte)(loaded ? 1 : 0)); w.Int(powder);
            CoopP2P.Send(_buf, w.Length, true);   // reliable: a discrete state change, must not be lost
        }

        // Apply a received reload target. EmptyRest (idx==0 && !loaded) → never drive (don't poke eject; co-op empties
        // via MSG_FIRE). Else (LoadedRest / Reloading) → drive toward loaded via the paced AdvanceState driver.
        private static void ApplyReloadTarget(ref ReloadSide rs, GunController gun, int echoKey, byte side, int idx, bool loaded, int powder, float now)
        {
            _echoUntil[echoKey] = now + 0.3f;
            rs.Authored = false;   // adopting the peer's value yields authorship (they re-assert, not us)

            if (!loaded && idx == 0)   // EmptyRest
            {
                rs.Driving = false; rs.PrevIdx = idx; rs.PrevLoaded = false;
                Diagnostics.V($"[ctrl] applied remote reload gun {side} <- empty-rest");
                return;
            }

            rs.TgtPowder = powder;
            bool already = false; try { already = gun.ChamberedShellBlueprint != null && gun.CanFire; } catch { }
            if (already)
            {
                // Already converged — set powder + adopt baseline, no drive (prevents 2s heartbeat churn).
                try { if (gun.PowderCharges != powder) gun.SetPowderCharge(powder); } catch { }
                rs.Driving = false; rs.PrevLoaded = true;
                try { var rc = gun.artilleryReloadController; rs.PrevIdx = rc != null ? rc.CurrentStateIndex : idx; } catch { rs.PrevIdx = idx; }
                Diagnostics.V($"[ctrl] remote reload gun {side}: already loaded, powder={powder} set");
                return;
            }

            // LOAD-DRIVE SKIPPED: the AdvanceState driver CANNOT load a gun nobody is operating. This method still runs
            // on any received MSG_RELOAD_STATE; it just doesn't drive here. The chamber transfer is fired by the reload
            // CLIP's animation event (AnimationEvent_TransferShellToChamber), which only runs while the gun is actively
            // operated; programmatic AdvanceState() on the peer ripped through the state indices with working=False and
            // gunChamber=False FOREVER (never loaded), and the 2s "loaded" heartbeat re-armed the drive each cycle → the
            // host "stuck in a shell loading loop". So we DO NOT drive here. We log the target but leave the gun untouched
            // (DetectReload keeps tracking its REAL state — PrevIdx/PrevLoaded unchanged, so we never re-broadcast). Until
            // a working apply path replaces this the LOAD direction is inert (the peer gun looks unloaded); shots still
            // arrive via the MSG_FIRE fallback.
            rs.Driving = false; rs.Deadline = now + ReloadDriveTimeoutSec;
            Diagnostics.V($"[ctrl] remote reload gun {side}: load target idx={idx} powder={powder} — apply path PARKED, not driving");
        }

        // The paced driver — runs every frame while a side is Driving. Takes ONE legit action per spacing window
        // when not `working`, until the gun is loaded+CanFire (or a guard trips). Never pokes chamber/eject/SetState.
        private static void DriveReloadTick(float now)
        {
            if (!Config.CoopControlSync) return;
            DriveReloadSide(ref _rsL, _gunL, 0, ReloadKeyL, now);
            DriveReloadSide(ref _rsR, _gunR, 1, ReloadKeyR, now);
        }

        private static void DriveReloadSide(ref ReloadSide rs, GunController gun, byte side, int echoKey, float now)
        {
            if (!rs.Driving) return;
            if (gun == null) { rs.Driving = false; return; }
            ArtilleryReloadController rc = null; try { rc = gun.artilleryReloadController; } catch { }
            if (rc == null) { rs.Driving = false; return; }

            bool loaded = false; try { loaded = gun.ChamberedShellBlueprint != null; } catch { }
            bool canFire = false; try { canFire = gun.CanFire; } catch { }
            if (loaded && canFire)   // success: loaded & fireable (powder not required for CanFire; set it on the way out)
            {
                try { if (gun.PowderCharges != rs.TgtPowder) gun.SetPowderCharge(rs.TgtPowder); } catch { }
                StopReloadDrive(ref rs, gun, rc, echoKey, now, "success");
                return;
            }
            if (now > rs.Deadline) { StopReloadDrive(ref rs, gun, rc, echoKey, now, "timeout"); return; }

            bool working = false; try { working = rc.working; } catch { }
            if (working) return;                                       // a clip is playing — let it finish
            if (now - rs.LastActT < ReloadDriveSpacingSec) return;     // min spacing between actions

            int idx = -1; try { idx = rc.CurrentStateIndex; } catch { }
            string key = null; try { var cs = rc.CurrentState; if (cs != null) key = cs.stateKey; } catch { }

            if (idx == rs.LastSeenIdx) rs.SameIdx++; else { rs.SameIdx = 0; rs.LastSeenIdx = idx; }
            if (rs.SameIdx > ReloadDriveStallActions) { Diagnostics.V($"[ctrl] reload drive gun {side} STALLED at idx={idx} key='{key}' — stop"); StopReloadDrive(ref rs, gun, rc, echoKey, now, "stall"); return; }
            if (++rs.Actions > ReloadDriveActionCap) { Diagnostics.V($"[ctrl] reload drive gun {side} action cap — stop"); StopReloadDrive(ref rs, gun, rc, echoKey, now, "cap"); return; }

            // ONE legit action: set powder at the charge state (so charges ram to target), else AdvanceState().
            int powder = 0; try { powder = gun.PowderCharges; } catch { }
            if (key == "SelectPowderCharge" && powder < rs.TgtPowder) { try { gun.SetPowderCharge(rs.TgtPowder); } catch { } }
            else { try { rc.AdvanceState(); } catch (Exception e) { Diagnostics.V("[ctrl] reload drive AdvanceState: " + e.Message); } }
            rs.LastActT = now;
        }

        // Stop the driver and adopt the gun's CURRENT (idx, loaded) as our baseline so DetectReload doesn't then
        // re-broadcast the post-drive state as locally-authored; echo-suppress to be safe.
        private static void StopReloadDrive(ref ReloadSide rs, GunController gun, ArtilleryReloadController rc, int echoKey, float now, string why)
        {
            rs.Driving = false;
            int idx = 0; bool loaded = false;
            try { idx = rc.CurrentStateIndex; } catch { }
            try { loaded = gun.ChamberedShellBlueprint != null; } catch { }
            rs.PrevIdx = idx; rs.PrevLoaded = loaded; rs.Authored = false;
            _echoUntil[echoKey] = now + 0.3f;
            Diagnostics.V($"[ctrl] reload drive done ({why}) idx={idx} loaded={loaded}");
        }

        // ---------------- turret map ORIGIN (host-authoritative) ----------------

        // HOST only. Broadcast the turret's map grid (turretBase.anchoredPosition) so every client snaps its shared
        // turret to the host's origin → identical aim/range lands at the same map point (Bug 1). _turret != null only
        // ever holds during an active mission, so no separate phase check is needed. Edge-detect (throttled) + heal beat.
        private static void DetectTurretPos(float now)
        {
            if (Config.PvpActive || !Config.CoopControlSync || !CoopP2P.IsHost || _turret == null) return;

            Vector2 grid;
            try { var bas = _turret.turretBase; if (bas == null) return; grid = bas.anchoredPosition; }
            catch { return; }
            if (!CoopWire.Finite(grid.x) || !CoopWire.Finite(grid.y)) return;

            bool seeded = !float.IsNaN(_turretPosPrev.x);
            bool changed = !seeded || Mathf.Abs(grid.x - _turretPosPrev.x) > TurretPosEps
                                   || Mathf.Abs(grid.y - _turretPosPrev.y) > TurretPosEps;
            bool sent = false;
            if (changed && now >= _nextTurretPosSend)
            {
                SendTurretPos(grid);
                _turretPosPrev = grid;
                _nextTurretPosSend = now + TurretPosMinSendSec;
                _nextTurretPosBeat = now + TurretPosHeartbeatSec;   // a fresh send already heals — defer the next beat
                sent = true;
                Diagnostics.V($"[ctrl] turret origin -> peer grid=({grid.x:0.00},{grid.y:0.00})");
            }

            // Heal beat: re-assert the current origin at a low rate (idempotent on the client) so a lost packet or a
            // late joiner converges without waiting for the next move. Skipped on a tick we already sent.
            if (!sent && seeded && now >= _nextTurretPosBeat)
            {
                _nextTurretPosBeat = now + TurretPosHeartbeatSec;
                try { SendTurretPos(grid); _turretPosPrev = grid; } catch { }
            }
        }

        private static void SendTurretPos(Vector2 grid)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_TURRET_POS); w.Float(grid.x); w.Float(grid.y);
            CoopP2P.Send(_buf, w.Length, true);   // reliable: a discrete origin change, must not be lost
        }

        // ---------------- after the game's Update: apply remote visuals + snap turret state ----------------

        public static void LateApply()
        {
            // PvP (Phase B): apply TEAMMATE control state too. Remote ownership/state is only ever set by packets
            // that passed the OnPacket team gate, so this is already teammate-scoped — an opponent's state never
            // reaches here.
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
        // ownership; the reliable release applies once regardless). Always skips a group we own
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
                        // Throttled diagnostic: the applied peer elevation and the resulting gun des/cur slew.
                        if (Time.unscaledTime >= _nextElevDiag)
                        {
                            _nextElevDiag = Time.unscaledTime + 1f;
                            Diagnostics.V($"[ctrl] applied peer elevation: turret={gs.V[0]:0.0} gunL={gs.V[1]:0.0} gunR={gs.V[2]:0.0} " +
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
            // is deferred, so we don't force the reload coroutine state here.
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
                var w = new CoopWire.Writer(_buf);
                w.Byte(MSG_SNAP);
                w.Float(_turret.DesiredRotation);
                w.Float(_turret.CurrentAngle);
                w.Float(_turret.DesiredElevation);
                w.Float(_gunL != null ? _gunL.DesiredElevationAngle : 0f);
                w.Float(_gunL != null ? _gunL.CurrentElevation : 0f);
                w.Float(_gunR != null ? _gunR.DesiredElevationAngle : 0f);
                w.Float(_gunR != null ? _gunR.CurrentElevation : 0f);
                w.Float(_gunL != null ? _gunL.PowderCharges : 0f);
                w.Float(_gunR != null ? _gunR.PowderCharges : 0f);
                CoopP2P.Send(_buf, w.Length, true);

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
                if (!LocallyOwnsGroup(Group.Rotation) && CoopWire.Finite(v[0]) && CoopWire.Finite(v[1]))
                {
                    _turret.DesiredRotation = v[0]; _turret.CurrentAngle = v[1];
                    try { _turret.ApplyRotationToTransforms(); } catch { }
                }
                if (!LocallyOwnsGroup(Group.Elevation))
                {
                    if (CoopWire.Finite(v[2])) _turret.DesiredElevation = v[2];
                    if (_gunL != null) { if (CoopWire.Finite(v[3])) _gunL.DesiredElevationAngle = v[3]; if (CoopWire.Finite(v[4])) _gunL.CurrentElevation = v[4]; }
                    if (_gunR != null) { if (CoopWire.Finite(v[5])) _gunR.DesiredElevationAngle = v[5]; if (CoopWire.Finite(v[6])) _gunR.CurrentElevation = v[6]; }
                }
                if (_gunL != null && !LocallyOwnsGroup(Group.GunLeft)) { int p = Mathf.RoundToInt(v[7]); try { if (_gunL.PowderCharges != p) _gunL.SetPowderCharge(p); } catch { } }
                if (_gunR != null && !LocallyOwnsGroup(Group.GunRight)) { int p = Mathf.RoundToInt(v[8]); try { if (_gunR.PowderCharges != p) _gunR.SetPowderCharge(p); } catch { } }
                Log.LogInfo($"[ctrl] applied JIP snapshot (rot={v[1]:0.0} desRot={v[0]:0.0} elev={v[2]:0.0} powderL={Mathf.RoundToInt(v[7])} powderR={Mathf.RoundToInt(v[8])})");
            }
            catch (Exception e) { Log.LogWarning("[ctrl] apply snapshot: " + e.Message); }
        }

        // ---------------- current-state reconcile ----------------

        // Host → client: the CURRENT turret/gun physical state, broadcast on a low-rate reliable heartbeat. Same
        // 9-float layout as the JIP snapshot, but a SEPARATE type so the client applies it softly (drift-gated)
        // rather than hard-snapping like a join.
        private static void SendRecon()
        {
            if (_turret == null || !EnsureBuf()) return;
            try
            {
                var w = new CoopWire.Writer(_buf); w.Byte(MSG_RECON);
                // The host's CURRENT gun elevation is authoritative ONLY while the host is the one driving it.
                // When the CLIENT drives elevation (or nobody does) the host's gun-elevation reading must not be
                // imposed on the client — broadcasting it (often stale, since the adopting side's barrel lags or,
                // pre-fix, never slewed) is exactly what dragged the client's elevation back every reconcile.
                // Send NaN for the gun CURRENT-elevation fields in that case; the client's CoopWire.Finite() guard skips
                // them. Rotation stays authoritative — the turret always auto-slews CurrentAngle toward desired.
                bool elevAuth = LocallyOwnsGroup(Group.Elevation);
                w.Float(_turret.DesiredRotation);
                w.Float(_turret.CurrentAngle);
                w.Float(_turret.DesiredElevation);
                w.Float(_gunL != null ? _gunL.DesiredElevationAngle : 0f);
                w.Float(elevAuth && _gunL != null ? _gunL.CurrentElevation : float.NaN);
                w.Float(_gunR != null ? _gunR.DesiredElevationAngle : 0f);
                w.Float(elevAuth && _gunR != null ? _gunR.CurrentElevation : float.NaN);
                w.Float(_gunL != null ? _gunL.PowderCharges : 0f);
                w.Float(_gunR != null ? _gunR.PowderCharges : 0f);
                CoopP2P.Send(_buf, w.Length, true);
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
                if (!LocallyOwnsGroup(Group.Rotation) && CoopWire.Finite(v[0]) && CoopWire.Finite(v[1])
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
                    if (_gunL != null && CoopWire.Finite(v[3]) && CoopWire.Finite(v[4])
                        && Mathf.Abs(_gunL.DesiredElevationAngle - v[3]) <= tol
                        && Mathf.Abs(_gunL.CurrentElevation - v[4]) > tol)
                        _gunL.CurrentElevation = v[4];
                    if (_gunR != null && CoopWire.Finite(v[5]) && CoopWire.Finite(v[6])
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

        // Relay ACL: the packet types a client may author, which the host relays to the OTHER
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
                case MSG_AIM:
                    return true;   // cockpit controls — either crew member operates them
            }
            return type == CoopClipboard.MSG_SECTION || type == CoopClipboard.MSG_TOOL
                || type == CoopMap.MSG_GRAB || type == CoopMap.MSG_POS || type == CoopMap.MSG_PLACE
                || type == CoopMap.MSG_MARKER_ADD || type == CoopMap.MSG_MARKER_DEL || type == CoopMap.MSG_PIECE_MOVE
                || type == CoopScene.MSG_MISSION_READY
                || type == CoopCards.MSG_CARD   // fire-mission cards are bidirectional — fan out to the other clients (N>2)
                // Requisition punchcards are peer/either-authored: any crew member may grab/move/place a card or turn
                // a recon dial, so the host must relay them to the OTHER clients (without this a client's punchcard
                // action reached the host but never the other clients at N>2). Host-only types
                // (MSG_PUNCH_DECK/CONSUME/GRAPH) stay off this list.
                || type == CoopPunchcards.MSG_PUNCH_GRAB || type == CoopPunchcards.MSG_PUNCH_POS
                || type == CoopPunchcards.MSG_PUNCH_PLACE || type == CoopPunchcards.MSG_PUNCH_DIAL
                || type == PvpPlayers.MSG_PVP_POS    // PvP player position — either player authors it; host relays to other clients (N>2)
                || type == PvpCombat.MSG_PVP_HIT     // PvP hit report — attacker authors it, host relays; addressed to the victim's id in-payload
                // Pressure-valve damage is symmetric (either crew member repairs a valve), so a client's repair must
                // reach the OTHER clients at N>2. MSG_ENGINE stays OFF — host-authored.
                || type == CoopPressure.MSG_VALVE;
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
        // unreliable on the host→client leg and a dropped final leaves the piece/dial stuck off.
        // Both layouts put the flag at index 9: [t][i32][i32-or-f32][flags].
        public static bool IsMixedFinalStream(byte type)
        {
            return type == CoopMap.MSG_PIECE_MOVE || type == CoopPunchcards.MSG_PUNCH_DIAL;
        }

        // PvP TEAM ISOLATION (Phase B): in a PvP lobby co-op replication is TEAM-SCOPED — a packet from a
        // NON-teammate is dropped at LOCAL APPLY (the host still relayed it in Deliver; every machine filters
        // independently, so two teams never share a turret/map/clipboard). A small allowlist stays GLOBAL because
        // it MUST cross teams: the match lifecycle (everyone enters/leaves the arena together), the PvP combat +
        // roster channel (shells / HP / team assignments are cross-team by nature), and the host-consumed desync
        // digest. Everything else (controls, map, clipboard, entities, punchcards, cards, orders, score, impact) is
        // crew-internal and isolated per team. Pose isn't routed here (CoopP2P handles it; RemoteAvatar hides
        // opponents). Only consulted when PvpActive, so co-op/solo pay nothing.
        public static bool IsGlobalType(byte type)
        {
            return type == CoopScene.MSG_MISSION_START || type == CoopScene.MSG_MISSION_END || type == CoopScene.MSG_MISSION_READY
                || type == PvpPlayers.MSG_PVP_POS || type == PvpPlayers.MSG_PVP_SPAWN || type == PvpCombat.MSG_PVP_HIT || type == PvpTeams.MSG_PVP_TEAM
                || type == CoopNetDiag.MSG_DIGEST;
        }

        // origin = the SteamID that authored this packet (derived by CoopP2P from the Steam `from`, or the
        // host-stamped trailer on a relayed packet). Unused by the control handlers at the 2-player cap; Phase C
        // keys per-control ownership on it so a release from player B doesn't clear player C's lock.
        public static void OnPacket(byte type, ulong origin, Il2CppStructArray<byte> a, int len)
        {
            // PvP team gate (Phase B): drop a non-teammate's co-op packet at LOCAL APPLY. The host already relayed
            // it (Deliver runs RelayInner before this), and every machine filters the same way, so opponents never
            // share our turret/map/clipboard while teammates still converge. Global types always pass (IsGlobalType).
            if (Config.PvpActive && !IsGlobalType(type) && !PvpTeams.IsTeammate(origin)) return;

            float now = Time.unscaledTime;
            var r = new CoopWire.Reader(a, len, 1);
            switch (type)
            {
                case MSG_GRAB:
                {
                    if (len < 6) return;
                    int id = r.Int();
                    Group g = (Group)r.Byte();
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
                            Diagnostics.V($"[ctrl] remote grabbed '{c.T.name}' (grp={c.Grp}) <- {origin}");
                        }
                    }
                    else { _pendingGrab[id] = origin; }
                    break;
                }
                case MSG_RELEASE:
                {
                    if (len < 5) return;
                    int id = r.Int();
                    // Optional settled group state rides this reliable packet. Apply it ONCE,
                    // before clearing ownership, so the dial lands on the final value even if the live unreliable
                    // stream's last frame was lost. Strict-framed + finite-checked like MSG_GROUP.
                    if (r.Pos < len)
                    {
                        Group g = (Group)r.Byte();
                        int gi = (int)g;
                        if (gi > 0 && gi < _grp.Length)
                        {
                            // Only honor a settled-final from the group's owner — a non-owner's release must not
                            // settle the dial. Ownership is still set here (we clear it below).
                            var gsOwn = _grp[gi];
                            bool ownerOk = !gsOwn.RemoteOwned || now >= gsOwn.Until || gsOwn.RemoteOwner == origin;
                            int n = GroupFloatCount(g);
                            if (ownerOk && len == r.Pos + 4 * n)
                            {
                                bool ok = true;
                                for (int i = 0; i < n; i++) { float f = r.Float(); if (!CoopWire.Finite(f)) { ok = false; break; } _tmpGroup[i] = f; }
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
                    if (_byId.TryGetValue(id, out var c) && c.RemoteOwner == origin) { c.RemoteOwner = 0; RecomputeGroupRemote(c.Grp); Diagnostics.V($"[ctrl] remote released '{c.T.name}' <- {origin}"); }
                    if (_pendingGrab.TryGetValue(id, out var po) && po == origin) _pendingGrab.Remove(id);
                    break;
                }
                case MSG_VALUE:
                {
                    if (len < 9) return;
                    int id = r.Int();
                    float v = r.Float();
                    // Only the control's current owner may move it. A non-owner's late value (simultaneous-grab
                    // contention at N>2) must not drag a control another origin already won.
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
                    Group g = (Group)r.Byte();
                    int gi = (int)g;
                    if (gi <= 0 || gi >= _grp.Length) return;
                    var gsOwn = _grp[gi];
                    // Only the group's current owner may stream it. Reject a non-owner's contention frame (N>2);
                    // accept if nobody owns it yet (stream beat the grab) — matches the pre-cap behavior.
                    if (gsOwn.RemoteOwned && now < gsOwn.Until && gsOwn.RemoteOwner != origin) return;
                    int n = GroupFloatCount(g);
                    if (len != r.Pos + 4 * n) return;                 // strict framing: exactly n floats, no more/less
                    for (int i = 0; i < n; i++) { float f = r.Float(); if (!CoopWire.Finite(f)) return; _tmpGroup[i] = f; }
                    var gs = _grp[gi];                            // whole frame valid → publish atomically
                    for (int i = 0; i < n; i++) gs.V[i] = _tmpGroup[i];
                    gs.Has = true; gs.Until = now + StaleSec;
                    break;
                }
                case MSG_CLICK:
                {
                    if (len < 5) return;
                    int id = r.Int();
                    if (_byId.TryGetValue(id, out var c) && c.Switch != null)
                    {
                        string cnm = null; try { cnm = c.T != null ? c.T.name : null; } catch { }
                        if (IsReloadButtonName(cnm) || IsReloadButton(c.Switch))
                        {
                            EnqueueReloadClick(id, now);   // paced + in-order so it lands at the right reload state, not mid-animation
                        }
                        else
                        {
                            ReplayClick(c.Switch);
                            _echoUntil[id] = now + 0.3f;   // don't bounce our own replay back
                            Diagnostics.V($"[ctrl] applied remote click '{c.T.name}' <- peer");
                        }
                    }
                    break;
                }
                case MSG_FIRE:
                {
                    if (len < 2) return;
                    byte side = r.Byte();
                    float tx = float.NaN, ty = float.NaN, sx = float.NaN, sy = float.NaN, tt = 0f;
                    if (len >= 10) { tx = r.Float(); ty = r.Float(); }                 // board-local target (crater)
                    if (len >= 22) { sx = r.Float(); sy = r.Float(); tt = r.Float(); } // launch point + travelTime (arc)
                    if (!CoopWire.Finite(tx) || !CoopWire.Finite(ty)) break;           // no crater -> nothing to replay
                    var gun = side == 0 ? _gunL : _gunR;
                    if (gun == null) break;
                    bool haveStart = CoopWire.Finite(sx) && CoopWire.Finite(sy);
                    // Tag a PER-SIDE Replay intent with the shooter's flight, then replay RequestFire — but only if our
                    // matching gun will actually fire (ReplayShot checks CanFire). Our replayed shell's ShellVisual
                    // postfix dequeues that intent and overwrites start/target/travelTime -> identical arc + crater,
                    // regardless of our own angle/elevation/powder. The per-side intent (not a global flag) is what
                    // stops a concurrent LOCAL shot from being hijacked onto this target (Bug 2). See CoopBallistics.
                    CoopBallistics.ReplayShot(gun, side, new Vector2(tx, ty), new Vector2(sx, sy), tt, haveStart);
                    Diagnostics.V($"[ctrl] remote fire gun {side} <- peer  start=({sx:0.0},{sy:0.0}) tgt=({tx:0.0},{ty:0.0})");
                    break;
                }
                case MSG_POWDER:
                {
                    if (len < 6) return;   // t + side u8 + charges i32
                    byte side = r.Byte();
                    int charges = r.Int();
                    var gun = side == 0 ? _gunL : _gunR;
                    if (gun != null)
                    {
                        try { if (gun.PowderCharges != charges) gun.SetPowderCharge(charges); } catch (Exception e) { Log.LogWarning("[ctrl] apply powder: " + e.Message); }
                        // Adopt as our baseline + suppress the echo so DetectPowder doesn't bounce it back. Adopting the
                        // peer's value yields authorship: the peer now re-asserts this gun's powder, not us (no flap).
                        if (side == 0) { _powderPrevL = charges; _powderAuthoredL = false; } else { _powderPrevR = charges; _powderAuthoredR = false; }
                        _echoUntil[side == 0 ? PowderKeyL : PowderKeyR] = now + 0.3f;
                        Diagnostics.V($"[ctrl] applied remote powder gun {side} <- peer ({charges})");
                    }
                    break;
                }
                case MSG_AIM:
                {
                    if (len < 1 + 4 * 4) return;   // t + 4 floats
                    float rot = r.Float(), elev = r.Float(), eL = r.Float(), eR = r.Float();
                    if (r.Bad || !CoopWire.Finite(rot) || !CoopWire.Finite(elev) || !CoopWire.Finite(eL) || !CoopWire.Finite(eR)) return;
                    if (_turret == null) break;
                    if (LocallyOwnsGroup(Group.Rotation) || LocallyOwnsGroup(Group.Elevation)) break;   // our live drag wins
                    // Adopt the peer's resolved firing solution through the SAME path the drag stream uses:
                    // OverrideGunDrive so the controller stops re-deriving elevation from our PARKED dial, and the gun
                    // elevation MUST go through SetDesiredElevation (the DesiredElevationAngle property alone is an
                    // inert reported field — see ApplyGroupValues). Skip a no-op heartbeat so we don't churn/log @2s.
                    bool diff = Mathf.Abs(Mathf.DeltaAngle(rot, _turret.DesiredRotation)) > AimEps
                             || Mathf.Abs(elev - _turret.DesiredElevation) > AimEps
                             || (_gunL != null && Mathf.Abs(eL - _gunL.DesiredElevationAngle) > AimEps)
                             || (_gunR != null && Mathf.Abs(eR - _gunR.DesiredElevationAngle) > AimEps);
                    if (diff)
                    {
                        try
                        {
                            _turret.DesiredRotation = rot;
                            OverrideGunDrive();
                            _turret.DesiredElevation = elev;
                            if (_gunL != null) _gunL.SetDesiredElevation(eL);
                            if (_gunR != null) _gunR.SetDesiredElevation(eR);
                        }
                        catch (Exception e) { Log.LogWarning("[ctrl] apply aim: " + e.Message); }
                        Diagnostics.V($"[ctrl] applied remote aim rot={rot:0.0} elev={elev:0.0} gunL={eL:0.0} gunR={eR:0.0} <- peer");
                    }
                    _aimPrevRot = rot; _aimPrevElev = elev; _aimPrevElevL = eL; _aimPrevElevR = eR;
                    _aimAuthored = false;   // adopting the peer's value yields authorship — they re-assert, not us
                    _echoUntil[AimKey] = now + 0.3f;
                    break;
                }

                case MSG_RELOAD_STATE:
                {
                    // Strict validation: t + side u8 + idx u8 + loaded u8 + powder i32 = 8 bytes.
                    if (len < 8) return;
                    byte side = r.Byte();
                    int idx = r.Byte();
                    int lb = r.Byte();
                    int powder = r.Int();
                    if (r.Bad || side > 1 || (lb != 0 && lb != 1)) { Diagnostics.V($"[ctrl] reload pkt rejected side={side} lb={lb}"); return; }
                    var gun = side == 0 ? _gunL : _gunR;
                    ArtilleryReloadController rc = null; try { if (gun != null) rc = gun.artilleryReloadController; } catch { }
                    if (gun == null || rc == null) break;   // no gun resolved yet — drop (JIP reseed re-delivers)
                    int stateCount = 0; try { var st = rc.reloadStates; stateCount = st != null ? st.Count : 0; } catch { }
                    if (idx < 0 || idx >= stateCount) { Diagnostics.V($"[ctrl] reload pkt bad idx={idx}/{stateCount}"); return; }
                    if (powder < 0) powder = 0; else if (powder > 99) powder = 99;   // clamp defensively
                    if (side == 0) ApplyReloadTarget(ref _rsL, gun, ReloadKeyL, 0, idx, lb != 0, powder, now);
                    else           ApplyReloadTarget(ref _rsR, gun, ReloadKeyR, 1, idx, lb != 0, powder, now);
                    break;
                }
                case MSG_SNAP:
                {
                    if (len < 1 + 9 * 4) return;
                    var v = new float[9];
                    for (int i = 0; i < 9; i++) v[i] = r.Float();
                    Log.LogInfo("[ctrl] received JIP turret snapshot <- peer");
                    if (_turret != null) ApplySnapshot(v);   // apply now if ready …
                    else _pendingSnap = v;                    // … else stash for Tick once the registry resolves
                    break;
                }
                case MSG_RECON:
                {
                    if (len < 1 + 9 * 4 || CoopP2P.IsHost || _turret == null) return;   // client applies; needs a turret
                    var v = new float[9];
                    for (int i = 0; i < 9; i++) v[i] = r.Float();
                    ApplyRecon(v);
                    break;
                }
                case MSG_TURRET_POS:
                {
                    // Client-only: snap the shared turret to the HOST's authoritative map origin (Bug 1). The host
                    // ignores its own broadcast; PvP keeps per-player origins so a PvP machine never reaches here.
                    if (Config.PvpActive || CoopP2P.IsHost || _turret == null) break;
                    if (len < 1 + 2 * 4) return;
                    float gx = r.Float(), gy = r.Float();
                    if (r.Bad || !CoopWire.Finite(gx) || !CoopWire.Finite(gy)) break;
                    try
                    {
                        // Diff-guard: if we're already at the host's grid, do nothing — avoids re-snapping (and any
                        // fight with an in-progress local move) on every heal beat.
                        var bas = _turret.turretBase;
                        if (bas != null)
                        {
                            var cur = bas.anchoredPosition;
                            if (Mathf.Abs(cur.x - gx) <= TurretPosEps && Mathf.Abs(cur.y - gy) <= TurretPosEps) break;
                        }
                        // Grid → world through the SAME frame the map markers use (CoopMapFrame), then the exact call
                        // PvpPlayers.PlaceMyTurret proved snaps anchoredPosition to the grid (X/Z preserved, Y resolved).
                        var fmr = CoopMapFrame.Resolve();
                        if (fmr == null) { Diagnostics.V("[ctrl] turret origin apply skipped — no map frame yet"); break; }
                        Vector3 world = fmr.TransformPoint(new Vector3(gx, gy, 0f));
                        _turret.SetTurretLocation(world);
                        Diagnostics.V($"[ctrl] applied turret origin <- peer grid=({gx:0.00},{gy:0.00})");
                    }
                    catch (Exception e) { Log.LogWarning("[ctrl] apply turret origin: " + e.Message); }
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
                    else if (type == CoopPressure.MSG_VALVE || type == CoopPressure.MSG_ENGINE) CoopPressure.OnPacket(type, origin, a, len);
                    else if (type == CoopImpact.MSG_IMPACT) CoopImpact.OnPacket(type, a, len);
                    else if (type == CoopPunchcards.MSG_PUNCH_DECK || type == CoopPunchcards.MSG_PUNCH_REDEEM
                             || type == CoopPunchcards.MSG_PUNCH_GRAB || type == CoopPunchcards.MSG_PUNCH_POS
                             || type == CoopPunchcards.MSG_PUNCH_PLACE || type == CoopPunchcards.MSG_PUNCH_CONSUME
                             || type == CoopPunchcards.MSG_PUNCH_GRAPH || type == CoopPunchcards.MSG_PUNCH_DIAL) CoopPunchcards.OnPacket(type, origin, a, len);
                    else if (type == CoopNetDiag.MSG_DIGEST) CoopNetDiag.OnPacket(type, origin, a, len);
                    else if (type == PvpPlayers.MSG_PVP_POS || type == PvpPlayers.MSG_PVP_SPAWN) PvpPlayers.OnPacket(type, origin, a, len);
                    else if (type == PvpCombat.MSG_PVP_HIT) PvpCombat.OnPacket(type, origin, a, len);
                    else if (type == PvpTeams.MSG_PVP_TEAM) PvpTeams.OnPacket(type, origin, a, len);
                    else if (type == SteamNet.MSG_KICK) SteamNet.OnPacket(type, origin, a, len);
                    else CoopMap.OnPacket(type, origin, a, len);
                    break;
            }
        }

        // Replay a click exactly as the cursor manager would (hover → press → release on its own Interactable),
        // so the game's own handler runs — switch animation + the gameplay effect (reload, powder, toggle).
        private static void ReplayClick(LookAtTarget sw)
        {
#if !PUBLIC_BUILD
            CoopFireProbe.InClickReplay = true;   // tag any RequestFire reached via a replayed fire-button click (probe only)
#endif
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
#if !PUBLIC_BUILD
            finally { CoopFireProbe.InClickReplay = false; }
#endif
        }

        // ---------------- reload-click pacing ----------------

        // Queue a replayed RELOAD click (single shared queue). Order is preserved; drained by DrainReloadClicks only when
        // no gun is mid reload-animation.
        private static void EnqueueReloadClick(int netId, float now)
        {
            if (_reloadClickQ.Count >= ReloadClickQueueCap) { var d = _reloadClickQ.Dequeue(); Diagnostics.V($"[ctrl] reload click queue full — dropped oldest netId={d.NetId}"); }
            _reloadClickQ.Enqueue(new PendingReloadClick { NetId = netId, EnqueuedAt = now });
            Diagnostics.V($"[ctrl] reload click QUEUED (depth={_reloadClickQ.Count})");
        }

        // True while EITHER gun is mid reload-animation (a clip playing). Applying a click now would land mid-step. The
        // idle gun's flag is false, so this naturally tracks the cannon currently being loaded (shared controls).
        private static bool AnyReloadWorking()
        {
            try { if (_gunL != null) { var rc = _gunL.artilleryReloadController; if (rc != null && rc.working) return true; } } catch { }
            try { if (_gunR != null) { var rc = _gunR.artilleryReloadController; if (rc != null && rc.working) return true; } } catch { }
            return false;
        }

        // Drain one queued reload click when no gun is animating + a settle gap has elapsed, so a step's animation and the
        // auto-advance it triggers complete before the next click lands. Drops a click that can't apply within the timeout
        // (peer diverged) so the queue can't wedge. Called every frame from Tick.
        private static void DrainReloadClicks(float now)
        {
            if (_reloadClickQ.Count == 0) return;
            var head = _reloadClickQ.Peek();

            // Timeout: drop a click the peer can't reach a state for, so the queue (and the rest of the reload) can't wedge.
            if (now - head.EnqueuedAt > ReloadClickTimeoutSec)
            {
                _reloadClickQ.Dequeue();
                Diagnostics.V($"[ctrl] reload click TIMED OUT (netId={head.NetId}) — dropped, depth now {_reloadClickQ.Count}");
                return;
            }

            if (now - _reloadClickLastApply < ReloadClickSettleSec) return;   // settle gap between applies
            if (AnyReloadWorking()) return;                                   // hold while a clip is playing — applying now lands mid-step

            if (!_byId.TryGetValue(head.NetId, out var c) || c.Switch == null) { _reloadClickQ.Dequeue(); return; }   // button vanished → drop
            _reloadClickQ.Dequeue();
            ReplayClick(c.Switch);
            _echoUntil[head.NetId] = now + 0.3f;
            _reloadClickLastApply = now;
            Diagnostics.V($"[ctrl] applied remote RELOAD click '{c.T.name}' <- peer (paced; depth now {_reloadClickQ.Count})");
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
            var w = new CoopWire.Writer(_buf); w.Byte(MSG_GRAB); w.Int(c.NetId); w.Byte((byte)c.Grp);
            CoopP2P.Send(_buf, w.Length, true);
        }

        private static void SendRelease(Ctrl c)
        {
            if (!EnsureBuf()) return;
            // Carry the SETTLED group state INSIDE the reliable release, instead of a separate
            // unreliable group packet that could be lost or arrive after the release (which clears RemoteOwned and
            // would freeze the dial at its last streamed value). Now release + final value land together, ordered.
            var w = new CoopWire.Writer(_buf); w.Byte(MSG_RELEASE); w.Int(c.NetId);
            int grpPos = w.Pos; w.Byte((byte)c.Grp);
            if (c.Grp != Group.Other && _turret != null)
            {
                try { WriteGroupFloats(ref w, c.Grp); }
                catch { w.Pos = grpPos; w.Byte((byte)Group.Other); }   // couldn't settle — send a plain release
            }
            CoopP2P.Send(_buf, w.Length, true);
            Diagnostics.V($"[ctrl] released '{c.T.name}' -> peer (settled grp={c.Grp})");
        }

        private static void SendClick(int netId)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf); w.Byte(MSG_CLICK); w.Int(netId);
            CoopP2P.Send(_buf, w.Length, true);
        }

        private static void SendFire(byte side, Vector2 tgt, Vector2 start, float time)
        {
            if (!EnsureBuf()) return;
            // Ship the SHOOTER's whole ShellVisual flight: target (crater) + start (launch) + travelTime. The peer
            // forces these onto its own shell so the arc AND the crater match — no pre-shot sync needed.
            var w = new CoopWire.Writer(_buf);
            w.Byte(MSG_FIRE); w.Byte(side); w.Float(tgt.x); w.Float(tgt.y); w.Float(start.x); w.Float(start.y); w.Float(time);
            CoopP2P.Send(_buf, w.Length, true);
        }

        private static void SendPowder(byte side, int charges)
        {
            if (!EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf); w.Byte(MSG_POWDER); w.Byte(side); w.Int(charges);
            CoopP2P.Send(_buf, w.Length, true);   // reliable: a discrete state change, must not be lost
        }

        private static void SendValue(Ctrl c)
        {
            if (!EnsureBuf()) return;
            float v;
            try { v = c.Dial != null ? c.Dial.AccumulatedValue : c.Slider.CurrentDistance; } catch { return; }
            if (!CoopWire.Finite(v)) return;
            var w = new CoopWire.Writer(_buf); w.Byte(MSG_VALUE); w.Int(c.NetId); w.Float(v);
            CoopP2P.Send(_buf, w.Length, false);
        }

        private static void SendGroupState(Group g)
        {
            if (_turret == null || g == Group.Other || !EnsureBuf()) return;
            var w = new CoopWire.Writer(_buf); w.Byte(MSG_GROUP); w.Byte((byte)g);
            try { WriteGroupFloats(ref w, g); } catch { return; }
            CoopP2P.Send(_buf, w.Length, false);
            // Throttled diagnostic: the elevation values we actually STREAM (turret/gun des + gun cur).
            if (g == Group.Elevation && Time.unscaledTime >= _nextElevSendDiag)
            {
                _nextElevSendDiag = Time.unscaledTime + 1f;
                Diagnostics.V($"[ctrl] streaming elevation -> peer: turret.des={_turret.DesiredElevation:0.0} " +
                            $"gunL.des={(_gunL != null ? _gunL.DesiredElevationAngle : 0f):0.0} gunL.cur={(_gunL != null ? _gunL.CurrentElevation : 0f):0.0}");
            }
        }

        // Serialize a group's GroupFloatCount floats at offset o. Shared by the live MSG_GROUP stream and the
        // settled-state payload folded into the reliable MSG_RELEASE.
        private static void WriteGroupFloats(ref CoopWire.Writer w, Group g)
        {
            switch (g)
            {
                case Group.Rotation:
                    w.Float(_turret.DesiredRotation);   // desired aim only (intent sync)
                    break;
                case Group.Elevation:
                    w.Float(_turret.DesiredElevation);
                    w.Float(_gunL != null ? _gunL.DesiredElevationAngle : 0f);
                    w.Float(_gunR != null ? _gunR.DesiredElevationAngle : 0f);
                    break;
                case Group.GunLeft:  PutGun(ref w, _gunL); break;
                case Group.GunRight: PutGun(ref w, _gunR); break;
            }
        }

        private static void PutGun(ref CoopWire.Writer w, GunController gun)
        {
            int powder = 0; bool reloading = false;
            try { if (gun != null) { powder = gun.PowderCharges; reloading = gun.IsReloading; } } catch { }
            w.Float(powder);
            w.Float(reloading ? 1f : 0f);
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
                // Re-seed reload baselines from scratch and open a startup grace so the spawn/initial-load sequence
                // isn't replicated (it runs identically on both machines — see _reloadStartupUntil).
                _rsL = new ReloadSide { PrevIdx = int.MinValue, LastSeenIdx = int.MinValue };
                _rsR = new ReloadSide { PrevIdx = int.MinValue, LastSeenIdx = int.MinValue };
                _nextReloadBeat = 0f;
                _reloadStartupUntil = Time.unscaledTime + ReloadStartupGraceSec;
                _reloadClickQ.Clear(); _reloadClickLastApply = 0f;
            }
            _turret = turret; _turretIid = iid;
            ResolveGuns(turret);

            _reconExcluded = 0;
            _valveExcluded = 0;
            int added = 0;
            added += Scan(Il2CppType.Of<DialInteractable>(), true);
            added += Scan(Il2CppType.Of<LinearSliderInteractable>(), false);
            int sw = ScanSwitches();
            if (added + sw > 0) Log.LogInfo($"[ctrl] registry: {_byId.Count} controls (+{added} drag, +{sw} click, {_reconExcluded} recon-dials-excluded, {_valveExcluded} valve-dials-excluded; host={CoopP2P.IsHost})");
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
                // PRESSURE VALVES: every repair dial is a DialInteractable, but its damage is replicated by
                // CoopPressure (MSG_VALVE / SetDamage01), NOT the dial-visual stream (the valve recomputes damage from
                // an OnValueChanged EVENT that SetAccumulatedValueUnlimited(...,fireValueChangedEvent:false) won't fire).
                // Excluding them also stops a valve grab marking the turret group remotely-owned.
                // Path shape matches the probe: '…/PressureValve[ (n)]/Dial'.
                if (dial && path.IndexOf("PressureValve", StringComparison.OrdinalIgnoreCase) >= 0) { _valveExcluded++; continue; }
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
        // lighting) all pass. The EXCEPTION is each gun's fireButton: GunController.Awake wires it
        // `fireButton.RegisterOnClickDown(RequestFire)`, so replicating its click would call the peer's
        // RequestFire on the generic click lane — outside CoopBallistics.ReplayShot's _replaying guard — enqueuing
        // a false LOCAL intent and echoing the shot back. Firing is owned by the dedicated
        // MSG_FIRE lane, so the fire button is excluded here (closes the send AND the apply side at one point;
        // local fire still works via its own RegisterOnClickDown).
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
                if (IsFireButton(sw)) continue;                                   // fire button fires via MSG_FIRE, never the click lane
                // Reload buttons stay on the click lane (paced): the reload syncs by REPLAYING the lever/button clicks.
                // The MSG_RELOAD_STATE driver that excluding them once protected can't drive a non-operated gun
                // (animation-gated). The real bug is an INTERMITTENT dropped step in click replication — fixed at the
                // click level (pacing), not by excluding it.
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

        // True if this LookAtTarget is a gun's fireButton (wired straight to RequestFire). ResolveGuns runs
        // immediately before ScanSwitches in EnsureRegistry, and _byId is cleared on every new turret, so the
        // gun refs are always current when this is consulted — a name/path fallback would be both fragile and
        // unnecessary here (if guns fail to resolve at all, the whole co-op fire path is already dark). See
        // ScanSwitches for why the fire button must not be a generic click control.
        private static bool IsFireButton(LookAtTarget sw)
        {
            if (sw == null) return false;
            try
            {
                if (_gunL != null && (object)_gunL.fireButton == (object)sw) return true;
                if (_gunR != null && (object)_gunR.fireButton == (object)sw) return true;
            }
            catch { }
            return false;
        }

        // True if this LookAtTarget is one of a gun's RELOAD-step buttons: a per-state advanceButton, a cylinder
        // load/move button, or a powder dispenser / load-charges button (PowderChargeController). Used to route a
        // replayed click into the per-side PACED QUEUE (DrainReloadClicks) instead of applying it immediately — the
        // peer lags, so an immediate apply lands the click at the wrong reload state and breaks the sequence (the
        // intermittent reload desync). These clicks DO still replicate (unlike the abandoned exclusion); they're just
        // paced. Local clicks drive the LOCAL reload via the game's own handlers regardless.
        private static bool IsReloadButton(LookAtTarget sw)
        {
            if (sw == null) return false;
            try
            {
                if (GunReloadButtonMatch(_gunL, sw)) return true;
                if (GunReloadButtonMatch(_gunR, sw)) return true;
            }
            catch { }
            return false;
        }

        private static bool GunReloadButtonMatch(GunController gun, LookAtTarget sw)
        {
            if (gun == null) return false;
            ArtilleryReloadController rc = null;
            try { rc = gun.artilleryReloadController; } catch { }
            if (rc == null) return false;
            // per-state advance buttons
            try
            {
                var states = rc.reloadStates;
                if (states != null)
                {
                    int n = states.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var def = states[i];
                        if (def == null) continue;
                        LookAtTarget ab = null; try { ab = def.advanceButton; } catch { }
                        if (ab != null && (object)ab == (object)sw) return true;
                    }
                }
            }
            catch { }
            // cylinder load/move buttons (interop-confirmed separate LookAtTargets — not the same objects as advanceButton)
            try
            {
                var css = rc.cylinderShellSelector;
                if (css != null)
                {
                    LookAtTarget lb = null, mb = null;
                    try { lb = css.loadButton; } catch { }
                    try { mb = css.moveButton; } catch { }
                    if (lb != null && (object)lb == (object)sw) return true;
                    if (mb != null && (object)mb == (object)sw) return true;
                }
            }
            catch { }
            return false;
        }

        // Reload-step buttons are SHARED L/R controls with distinctive, stable names; the per-gun ref-match
        // (GunReloadButtonMatch) doesn't catch them, so identify them by name. These are the click-driven reload steps
        // that must be PACED (the auto breech/guide advances need no click and aren't named here). 'Universal Button Arm
        // Right/Left' (the arming lever) is deliberately NOT included — it's not a reload step.
        private static bool IsReloadButtonName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("dispencer") || n.Contains("dispenser")
                || n.Contains("charge rammer") || n.Contains("shell rammer")
                || n.Contains("move cylinder");
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
            _pendingSnap = null;
            _powderPrevL = int.MinValue; _powderPrevR = int.MinValue;   // re-seed powder baseline on next connect
            _powderAuthoredL = false; _powderAuthoredR = false; _nextPowderBeat = 0f;
            _aimPrevRot = float.NaN; _aimPrevElev = float.NaN; _aimPrevElevL = float.NaN; _aimPrevElevR = float.NaN;
            _aimAuthored = false; _nextAimBeat = 0f;
            _rsL = new ReloadSide { PrevIdx = int.MinValue, LastSeenIdx = int.MinValue };
            _rsR = new ReloadSide { PrevIdx = int.MinValue, LastSeenIdx = int.MinValue };
            _nextReloadBeat = 0f;
            _reloadClickQ.Clear(); _reloadClickLastApply = 0f;
            _turretPosPrev = new Vector2(float.NaN, float.NaN); _nextTurretPosSend = 0f; _nextTurretPosBeat = 0f;   // re-seed origin broadcast on next connect
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
        private static int Fnv(string s) => CoopIds.Fnv1A32(s);

        private static bool EnsureBuf()
        {
            if (_buf != null) return true;
            try { _buf = new Il2CppStructArray<byte>(64); return true; }
            catch (Exception e) { Log.LogWarning("[ctrl] buf: " + e.Message); return false; }
        }

    }
}
