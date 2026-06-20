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
    /// • We sync the RESULT, not the input: whoever operates a control streams the turret/gun physical state
    ///   that control's GROUP drives (azimuth CurrentAngle+DesiredRotation; per-gun elevation; powder). The
    ///   other machine snaps to it. Snapping the result is robust whether a dial is position- or
    ///   speed-controlled, and pins the very visible rotating cockpit (Barbet) tightly.
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
            public bool RemoteOwned;                // the peer is operating it
            public float RemoteUntil;               // ownership/value freshness expiry
            public bool HasRemoteVal;
            public float RemoteVal;                 // dial: accumulated angle; slider: current distance
        }

        private sealed class GroupState
        {
            public bool RemoteOwned;
            public float Until;
            public bool Has;
            public readonly float[] V = new float[5];
        }

        private static readonly Dictionary<int, Ctrl> _byId = new Dictionary<int, Ctrl>();
        private static readonly GroupState[] _grp = { new GroupState(), new GroupState(), new GroupState(), new GroupState(), new GroupState() };

        // Remote messages that arrive before the control is registered (different scene-load timing). Applied
        // when the control first appears in EnsureRegistry.
        private static readonly Dictionary<int, float> _pendingVal = new Dictionary<int, float>();
        private static readonly Dictionary<int, Group> _pendingGrab = new Dictionary<int, Group>();

        // Echo guard: when we REPLAY a peer's click/fire, the game flips isClicked/hasFired on our side too,
        // which our own poll would re-detect and bounce back. Suppress local detection for a control (keyed by
        // netId; fire uses synthetic keys) for a short window after a replay.
        private static readonly Dictionary<int, float> _echoUntil = new Dictionary<int, float>();
        private static readonly int FireKeyL = Fnv("__coop_fire_L"), FireKeyR = Fnv("__coop_fire_R");
        private static bool _firedPrevL, _firedPrevR;

        private static TurretController _turret;
        private static GunController _gunL, _gunR;
        private static int _turretIid = -1;
        private static float _nextScan;
        private static float _nextSend;

        // Reusable send buffer (control packets are tiny: 1+4+4 max). Lazily created on the Unity thread.
        private static Il2CppStructArray<byte> _buf;
        private static readonly byte[] _f4 = new byte[4];

        private static int _grabs, _releases;

        public static bool Active => _byId.Count > 0 && _turret != null;

        // ---------------- per-frame: detect local drags + transmit ----------------

        public static void Tick(float dt)
        {
            if (!Config.CoopControlSync) return;
            if (!SteamNet.InLobby || !CoopP2P.HasPeer) { if (_byId.Count > 0) ClearOwnership(); return; }
            try
            {
                EnsureRegistry();
                if (_turret == null) return;

                float now = Time.unscaledTime;
                bool sendNow = Config.CoopSendHz <= 0f || now >= _nextSend;
                if (sendNow && Config.CoopSendHz > 0f) _nextSend = now + 1f / Config.CoopSendHz;

                bool[] groupOwnedLocal = new bool[_grp.Length];

                foreach (var c in _byId.Values)
                {
                    if (c.Switch != null) { DetectClick(c, now); continue; }   // click control, not a drag

                    bool dragging = IsDragging(c);

                    if (dragging && !c.PrevDragging)
                    {
                        // Rising edge: claim the control unless the peer currently owns it (then we yield —
                        // their stream overrides our local movement in LateApply; we never fight the game's
                        // own drag system).
                        if (!(c.RemoteOwned && now < c.RemoteUntil))
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
                    if (c.RemoteOwned && now >= c.RemoteUntil) { c.RemoteOwned = false; RecomputeGroupRemote(c.Grp); }
                }

                if (sendNow)
                    for (int g = 1; g < _grp.Length; g++)
                        if (groupOwnedLocal[g]) SendGroupState((Group)g);

                for (int g = 0; g < _grp.Length; g++)
                    if (_grp[g].RemoteOwned && now >= _grp[g].Until) _grp[g].RemoteOwned = false;

                DetectFire(now);
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
                    if (c.LocalOwned || !c.RemoteOwned || now >= c.RemoteUntil || !c.HasRemoteVal) continue;
                    try
                    {
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
            if (!gs.RemoteOwned || now >= gs.Until || !gs.Has) return;
            if (LocallyOwnsGroup(g)) return;   // a tug-of-war shouldn't fight our own live drag
            try
            {
                switch (g)
                {
                    case Group.Rotation:
                        _turret.DesiredRotation = gs.V[0];
                        _turret.CurrentAngle = gs.V[1];
                        try { _turret.ApplyRotationToTransforms(); } catch { }
                        break;
                    case Group.Elevation:
                        _turret.DesiredElevation = gs.V[0];
                        if (_gunL != null) { _gunL.DesiredElevationAngle = gs.V[1]; _gunL.CurrentElevation = gs.V[2]; }
                        if (_gunR != null) { _gunR.DesiredElevationAngle = gs.V[3]; _gunR.CurrentElevation = gs.V[4]; }
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

        // ---------------- receive ----------------

        public static void OnPacket(byte type, Il2CppStructArray<byte> a, int len)
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
                            // Both grabbed the same control. Host keeps it; client yields.
                            if (CoopP2P.IsHost) return;            // I'm host — ignore their grab, keep mine
                            c.LocalOwned = false;                  // I'm client — yield to host
                        }
                        c.RemoteOwned = true; c.RemoteUntil = now + StaleSec;
                        MarkGroupRemote(c.Grp, now);
                    }
                    else { _pendingGrab[id] = g; }
                    break;
                }
                case MSG_RELEASE:
                {
                    if (len < 5) return;
                    int id = GetInt(a, ref o);
                    if (_byId.TryGetValue(id, out var c)) { c.RemoteOwned = false; RecomputeGroupRemote(c.Grp); }
                    _pendingGrab.Remove(id);
                    break;
                }
                case MSG_VALUE:
                {
                    if (len < 9) return;
                    int id = GetInt(a, ref o);
                    float v = GetFloat(a, ref o);
                    if (_byId.TryGetValue(id, out var c)) { c.RemoteVal = v; c.HasRemoteVal = true; c.RemoteUntil = now + StaleSec; }
                    else _pendingVal[id] = v;
                    break;
                }
                case MSG_GROUP:
                {
                    if (len < 2) return;
                    Group g = (Group)a[o++];
                    int gi = (int)g;
                    if (gi <= 0 || gi >= _grp.Length) return;
                    int n = GroupFloatCount(g);
                    var gs = _grp[gi];
                    for (int i = 0; i < n; i++)
                    {
                        if (o + 4 > len) break;
                        gs.V[i] = GetFloat(a, ref o);
                    }
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
                    }
                    break;
                }
                case MSG_FIRE:
                {
                    if (len < 2) return;
                    byte side = a[o++];
                    var gun = side == 0 ? _gunL : _gunR;
                    if (gun != null) { try { gun.RequestFire(); } catch (Exception e) { Log.LogWarning("[ctrl] replay fire: " + e.Message); } }
                    _echoUntil[side == 0 ? FireKeyL : FireKeyR] = now + 0.3f;
                    break;
                }
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
                    return e.RemoteOwned && now < e.RemoteUntil;
            }
            return false;
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
            // Push the settled group state first so the peer holds the right value after we let go, then the
            // reliable release.
            if (c.Grp != Group.Other) SendGroupState(c.Grp);
            int o = 0; _buf[o++] = MSG_RELEASE; o = PutInt(o, c.NetId);
            CoopP2P.Send(_buf, o, true);
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
            if (_turret == null || !EnsureBuf()) return;
            int o = 0; _buf[o++] = MSG_GROUP; _buf[o++] = (byte)g;
            try
            {
                switch (g)
                {
                    case Group.Rotation:
                        o = PutFloat(o, _turret.DesiredRotation); o = PutFloat(o, _turret.CurrentAngle);
                        break;
                    case Group.Elevation:
                        o = PutFloat(o, _turret.DesiredElevation);
                        o = PutFloat(o, _gunL != null ? _gunL.DesiredElevationAngle : 0f);
                        o = PutFloat(o, _gunL != null ? _gunL.CurrentElevation : 0f);
                        o = PutFloat(o, _gunR != null ? _gunR.DesiredElevationAngle : 0f);
                        o = PutFloat(o, _gunR != null ? _gunR.CurrentElevation : 0f);
                        break;
                    case Group.GunLeft:  o = PutGun(o, _gunL); break;
                    case Group.GunRight: o = PutGun(o, _gunR); break;
                    default: return;
                }
            }
            catch { return; }
            CoopP2P.Send(_buf, o, false);
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
            Group.Rotation => 2,
            Group.Elevation => 5,
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
                for (int i = 0; i < _grp.Length; i++) { _grp[i].RemoteOwned = false; _grp[i].Has = false; }
            }
            _turret = turret; _turretIid = iid;
            ResolveGuns(turret);

            int added = 0;
            added += Scan(Il2CppType.Of<DialInteractable>(), true);
            added += Scan(Il2CppType.Of<LinearSliderInteractable>(), false);
            int sw = ScanSwitches();
            if (added + sw > 0) Log.LogInfo($"[ctrl] registry: {_byId.Count} controls (+{added} drag, +{sw} click; host={CoopP2P.IsHost})");
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

                int id = Fnv(PathOf(tr));
                if (_byId.ContainsKey(id)) continue;
                var c = new Ctrl { NetId = id, T = tr, Dial = d, Slider = s, Grp = Classify(PathOf(tr)) };

                // Apply anything that arrived for this control before it existed.
                if (_pendingGrab.TryGetValue(id, out var pg)) { c.RemoteOwned = true; c.RemoteUntil = Time.unscaledTime + StaleSec; MarkGroupRemote(c.Grp, Time.unscaledTime); _pendingGrab.Remove(id); }
                if (_pendingVal.TryGetValue(id, out var pv)) { c.RemoteVal = pv; c.HasRemoteVal = true; _pendingVal.Remove(id); }

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
            return p.Contains("menu") || p.Contains("campaign") || p.Contains("mission select")
                || p.Contains("missionselect") || p.Contains("settings") || p.Contains("operating manual");
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

        private static void MarkGroupRemote(Group g, float now)
        {
            int gi = (int)g; if (gi <= 0 || gi >= _grp.Length) return;
            _grp[gi].RemoteOwned = true; _grp[gi].Until = now + StaleSec;
        }

        private static void RecomputeGroupRemote(Group g)
        {
            int gi = (int)g; if (gi <= 0 || gi >= _grp.Length) return;
            bool any = false;
            foreach (var c in _byId.Values) if (c.Grp == g && c.RemoteOwned) { any = true; break; }
            _grp[gi].RemoteOwned = any;
        }

        private static void ClearOwnership()
        {
            foreach (var c in _byId.Values) { c.LocalOwned = false; c.RemoteOwned = false; c.PrevDragging = false; c.PrevClicked = false; c.HasRemoteVal = false; }
            for (int i = 0; i < _grp.Length; i++) { _grp[i].RemoteOwned = false; _grp[i].Has = false; }
            _pendingGrab.Clear(); _pendingVal.Clear(); _echoUntil.Clear();
            _firedPrevL = false; _firedPrevR = false;
        }

        private static bool IsDragging(Ctrl c)
        {
            try { return c.Dial != null ? c.Dial.isDragging : (c.Slider != null && c.Slider.isDragging); }
            catch { return false; }
        }

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

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) { sb.Insert(0, '/'); sb.Insert(0, p.name); }
            return sb.ToString();
        }

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

        private static int PutInt(int o, int v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }
        private static int PutFloat(int o, float v) { var t = BitConverter.GetBytes(v); _buf[o] = t[0]; _buf[o + 1] = t[1]; _buf[o + 2] = t[2]; _buf[o + 3] = t[3]; return o + 4; }

        private static int GetInt(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToInt32(_f4, 0); }
        private static float GetFloat(Il2CppStructArray<byte> a, ref int o) { _f4[0] = a[o]; _f4[1] = a[o + 1]; _f4[2] = a[o + 2]; _f4[3] = a[o + 3]; o += 4; return BitConverter.ToSingle(_f4, 0); }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
