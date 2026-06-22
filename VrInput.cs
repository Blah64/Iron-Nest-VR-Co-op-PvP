using System;
using BepInEx.Logging;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace IronNestVR
{
    /// <summary>
    /// OpenXR action set for cockpit interaction. Runtime-agnostic: one set of actions (aim pose,
    /// grip pose, trigger, recenter button, mode-toggle button, haptic output) is bound across several
    /// concrete interaction profiles so whichever controllers the active runtime presents will work.
    /// Actions + suggested bindings are created once after the instance exists; the set + pose spaces
    /// are created after the session; <see cref="Sync"/> + <see cref="LocatePoses"/> run each focused frame.
    /// </summary>
    internal sealed unsafe class VrInput
    {
        private static ManualLogSource Log => Plugin.Logger;

        private XR _api;
        private Instance _instance;
        private Session _session;

        private ActionSet _actionSet;
        private XrAction _aAimPose;   // pointing ray origin (right)
        private XrAction _aAimPoseL;  // pointing ray origin (left)
        private XrAction _aGripPose;  // right hand origin (grab)
        private XrAction _aGripPoseL; // left hand origin (grab)
        private XrAction _aGrabL;     // left squeeze -> grab HUD panel
        private XrAction _aGrabR;     // right squeeze -> grab HUD panel
        private XrAction _aFire;     // float trigger -> click / grab
        private XrAction _aRecenter; // bool button
        private XrAction _aMenu;     // left menu button -> ESC
        private XrAction _aMove;     // left thumbstick -> locomotion
        private XrAction _aTurn;     // right thumbstick -> view turn
        private XrAction _aInteract; // right A button -> [E] interact (also map-tools toggle while right-holding clipboard)
        private XrAction _aMapDelete; // right B button -> map line delete (secondary click)
        private XrAction _aMapToolsL; // left X button -> map-tools palette toggle (while left-holding clipboard)
        private XrAction _aStickL;   // left thumbstick click  \ both at once -> open VR menu
        private XrAction _aStickR;   // right thumbstick click /
        private XrAction _aHaptic;   // vibration output

        private Space _aimSpace, _aimSpaceL, _gripSpace, _gripSpaceL;
        private bool _spacesReady;

        private Posef _aimPose, _aimPoseL, _gripPose, _gripPoseL;
        private bool _grabL, _grabR;
        private float _trigger, _prevTrigger;
        private bool _recenter, _prevRecenter;
        private bool _menu;
        private float _moveX, _moveY;
        private float _turnX, _turnY;
        private bool _interact;
        private bool _mapDelete;
        private bool _mapToolsL;
        private bool _stickL, _stickR;

        public Posef AimPose => _aimPose;
        public Posef AimPoseL => _aimPoseL;
        public Posef GripPose => _gripPose;
        public Posef GripPoseL => _gripPoseL;
        public bool AimValid { get; private set; }
        public bool AimValidL { get; private set; }
        public bool GripValid { get; private set; }
        public bool GripValidL { get; private set; }
        public bool GrabL => _grabL;
        public bool GrabR => _grabR;
        public float Trigger => _trigger;
        public bool TriggerHeld => _trigger >= Config.TriggerFireThreshold;
        public bool RecenterEdge { get; private set; }
        public float MoveX => _moveX;
        public float MoveY => _moveY;
        public float TurnX => _turnX;
        public bool InteractHeld => _interact;
        public bool MapDeleteHeld => _mapDelete;
        public bool MapToolsLHeld => _mapToolsL;
        public bool MenuHeld => _menu;
        public bool StickClickL => _stickL;
        public bool StickClickR => _stickR;

        // -------- setup (once, after instance) --------

        public bool CreateActions(XR api, Instance instance, out string error)
        {
            error = null;
            _api = api;
            _instance = instance;

            var asci = new ActionSetCreateInfo { Type = StructureType.TypeActionSetCreateInfo, Priority = 0 };
            SetFixed(asci.ActionSetName, "gameplay");
            SetFixed(asci.LocalizedActionSetName, "Gameplay");
            if (_api.CreateActionSet(_instance, &asci, ref _actionSet) != Result.Success)
            { error = "xrCreateActionSet failed"; return false; }

            if (!MakeAction("aim_pose", "Aim Pose", ActionType.PoseInput, out _aAimPose, out error)) return false;
            if (!MakeAction("aim_pose_l", "Aim Pose L", ActionType.PoseInput, out _aAimPoseL, out error)) return false;
            if (!MakeAction("grip_pose", "Grip Pose", ActionType.PoseInput, out _aGripPose, out error)) return false;
            if (!MakeAction("grip_pose_l", "Grip Pose L", ActionType.PoseInput, out _aGripPoseL, out error)) return false;
            if (!MakeAction("grab_l", "Grab L", ActionType.BooleanInput, out _aGrabL, out error)) return false;
            if (!MakeAction("grab_r", "Grab R", ActionType.BooleanInput, out _aGrabR, out error)) return false;
            if (!MakeAction("fire", "Interact", ActionType.FloatInput, out _aFire, out error)) return false;
            if (!MakeAction("recenter", "Recenter", ActionType.BooleanInput, out _aRecenter, out error)) return false;
            if (!MakeAction("menu", "Menu Esc", ActionType.BooleanInput, out _aMenu, out error)) return false;
            if (!MakeAction("move", "Move", ActionType.Vector2fInput, out _aMove, out error)) return false;
            if (!MakeAction("turn", "Turn", ActionType.Vector2fInput, out _aTurn, out error)) return false;
            if (!MakeAction("interact", "Use Key", ActionType.BooleanInput, out _aInteract, out error)) return false;
            if (!MakeAction("map_delete", "Map Delete", ActionType.BooleanInput, out _aMapDelete, out error)) return false;
            if (!MakeAction("map_tools_l", "Map Tools L", ActionType.BooleanInput, out _aMapToolsL, out error)) return false;
            if (!MakeAction("stick_l", "Menu Open L", ActionType.BooleanInput, out _aStickL, out error)) return false;
            if (!MakeAction("stick_r", "Menu Open R", ActionType.BooleanInput, out _aStickR, out error)) return false;
            if (!MakeAction("haptic", "Haptic", ActionType.VibrationOutput, out _aHaptic, out error)) return false;

            // One suggestion call per profile; a profile the runtime rejects is non-fatal (others cover it).
            // Right hand = pointer/interact, left hand = recenter/toggle buttons.
            int ok = 0;
            ok += Suggest("/interaction_profiles/oculus/touch_controller", new[]
            {
                (_aAimPose, "/user/hand/right/input/aim/pose"),
                (_aAimPoseL, "/user/hand/left/input/aim/pose"),
                (_aGripPose, "/user/hand/right/input/grip/pose"),
                (_aGripPoseL, "/user/hand/left/input/grip/pose"),
                (_aGrabL, "/user/hand/left/input/squeeze/value"),
                (_aGrabR, "/user/hand/right/input/squeeze/value"),
                (_aFire, "/user/hand/right/input/trigger/value"),
                (_aRecenter, "/user/hand/left/input/y/click"),
                (_aMapToolsL, "/user/hand/left/input/x/click"),
                (_aMenu, "/user/hand/left/input/menu/click"),
                (_aMove, "/user/hand/left/input/thumbstick"),
                (_aTurn, "/user/hand/right/input/thumbstick"),
                (_aInteract, "/user/hand/right/input/a/click"),
                (_aMapDelete, "/user/hand/right/input/b/click"),
                (_aStickL, "/user/hand/left/input/thumbstick/click"),
                (_aStickR, "/user/hand/right/input/thumbstick/click"),
                (_aHaptic, "/user/hand/right/output/haptic"),
            }) ? 1 : 0;
            ok += Suggest("/interaction_profiles/valve/index_controller", new[]
            {
                (_aAimPose, "/user/hand/right/input/aim/pose"),
                (_aAimPoseL, "/user/hand/left/input/aim/pose"),
                (_aGripPose, "/user/hand/right/input/grip/pose"),
                (_aGripPoseL, "/user/hand/left/input/grip/pose"),
                (_aGrabL, "/user/hand/left/input/squeeze/value"),
                (_aGrabR, "/user/hand/right/input/squeeze/value"),
                (_aFire, "/user/hand/right/input/trigger/value"),
                (_aRecenter, "/user/hand/left/input/b/click"),
                (_aMenu, "/user/hand/left/input/a/click"),
                (_aMove, "/user/hand/left/input/thumbstick"),
                (_aTurn, "/user/hand/right/input/thumbstick"),
                (_aInteract, "/user/hand/right/input/a/click"),
                (_aMapDelete, "/user/hand/right/input/b/click"),
                (_aStickL, "/user/hand/left/input/thumbstick/click"),
                (_aStickR, "/user/hand/right/input/thumbstick/click"),
                (_aHaptic, "/user/hand/right/output/haptic"),
            }) ? 1 : 0;
            ok += Suggest("/interaction_profiles/microsoft/motion_controller", new[]
            {
                (_aAimPose, "/user/hand/right/input/aim/pose"),
                (_aAimPoseL, "/user/hand/left/input/aim/pose"),
                (_aGripPose, "/user/hand/right/input/grip/pose"),
                (_aGripPoseL, "/user/hand/left/input/grip/pose"),
                (_aGrabL, "/user/hand/left/input/squeeze/click"),
                (_aGrabR, "/user/hand/right/input/squeeze/click"),
                (_aFire, "/user/hand/right/input/trigger/value"),
                (_aRecenter, "/user/hand/left/input/thumbstick/click"),
                (_aMenu, "/user/hand/left/input/menu/click"),
                (_aMove, "/user/hand/left/input/thumbstick"),
                (_aTurn, "/user/hand/right/input/thumbstick"),
                (_aHaptic, "/user/hand/right/output/haptic"),
            }) ? 1 : 0;
            ok += Suggest("/interaction_profiles/htc/vive_controller", new[]
            {
                (_aAimPose, "/user/hand/right/input/aim/pose"),
                (_aAimPoseL, "/user/hand/left/input/aim/pose"),
                (_aGripPose, "/user/hand/right/input/grip/pose"),
                (_aGripPoseL, "/user/hand/left/input/grip/pose"),
                (_aGrabL, "/user/hand/left/input/squeeze/click"),
                (_aGrabR, "/user/hand/right/input/squeeze/click"),
                (_aFire, "/user/hand/right/input/trigger/value"),
                (_aRecenter, "/user/hand/left/input/trackpad/click"),
                (_aMenu, "/user/hand/left/input/menu/click"),
                (_aMove, "/user/hand/left/input/trackpad"),
                (_aTurn, "/user/hand/right/input/trackpad"),
                (_aHaptic, "/user/hand/right/output/haptic"),
            }) ? 1 : 0;
            // Minimal fallback: pose/click/haptics only. Recenter via menu.
            ok += Suggest("/interaction_profiles/khr/simple_controller", new[]
            {
                (_aAimPose, "/user/hand/right/input/aim/pose"),
                (_aGripPose, "/user/hand/right/input/grip/pose"),
                (_aFire, "/user/hand/right/input/select/click"),
                (_aRecenter, "/user/hand/left/input/menu/click"),
                (_aHaptic, "/user/hand/right/output/haptic"),
            }) ? 1 : 0;

            if (ok == 0) { error = "no interaction profile bindings were accepted by the runtime"; return false; }
            Log.LogInfo($"[input] action set created; {ok} interaction profile(s) bound.");
            return true;
        }

        public bool Attach(Session session, out string error)
        {
            error = null;
            _session = session;
            var set = _actionSet;
            var info = new SessionActionSetsAttachInfo
            {
                Type = StructureType.TypeSessionActionSetsAttachInfo,
                CountActionSets = 1,
                ActionSets = &set
            };
            if (_api.AttachSessionActionSets(session, &info) != Result.Success)
            { error = "xrAttachSessionActionSets failed"; return false; }

            var id = new Posef { Orientation = new Quaternionf(0, 0, 0, 1), Position = new Vector3f(0, 0, 0) };
            bool aim = CreateSpace(_aAimPose, id, ref _aimSpace);
            bool aimL = CreateSpace(_aAimPoseL, id, ref _aimSpaceL);
            bool grip = CreateSpace(_aGripPose, id, ref _gripSpace);
            bool gripL = CreateSpace(_aGripPoseL, id, ref _gripSpaceL);
            _spacesReady = aim && grip;
            Log.LogInfo($"[input] action set attached; pose spaces aim={aim} aimL={aimL} grip={grip} gripL={gripL}.");
            return true;
        }

        private bool CreateSpace(XrAction action, Posef pose, ref Space space)
        {
            var ci = new ActionSpaceCreateInfo
            {
                Type = StructureType.TypeActionSpaceCreateInfo,
                Action = action,
                SubactionPath = 0,
                PoseInActionSpace = pose
            };
            return _api.CreateActionSpace(_session, &ci, ref space) == Result.Success;
        }

        // -------- per-frame --------

        public void Sync()
        {
            var active = new ActiveActionSet { ActionSet = _actionSet, SubactionPath = 0 };
            var info = new ActionsSyncInfo
            {
                Type = StructureType.TypeActionsSyncInfo,
                CountActiveActionSets = 1,
                ActiveActionSets = &active
            };
            var r = _api.SyncAction(_session, &info);
            if (r != Result.Success) { RecenterEdge = false; return; }

            _prevTrigger = _trigger;
            _prevRecenter = _recenter;

            _trigger = GetFloat(_aFire);
            _recenter = GetBool(_aRecenter);
            _menu = GetBool(_aMenu);
            _interact = GetBool(_aInteract);
            _mapDelete = GetBool(_aMapDelete);
            _mapToolsL = GetBool(_aMapToolsL);
            _stickL = GetBool(_aStickL);
            _stickR = GetBool(_aStickR);
            GetVector2(_aMove, out _moveX, out _moveY);
            GetVector2(_aTurn, out _turnX, out _turnY);
            _grabL = GetBool(_aGrabL);
            _grabR = GetBool(_aGrabR);

            RecenterEdge = _recenter && !_prevRecenter;
        }

        public void LocatePoses(Space baseSpace, long time)
        {
            if (!_spacesReady) { AimValid = AimValidL = GripValid = GripValidL = false; return; }
            AimValid = Locate(_aimSpace, baseSpace, time, out _aimPose);
            AimValidL = Locate(_aimSpaceL, baseSpace, time, out _aimPoseL);
            GripValid = Locate(_gripSpace, baseSpace, time, out _gripPose);
            GripValidL = Locate(_gripSpaceL, baseSpace, time, out _gripPoseL);
        }

        private bool Locate(Space space, Space baseSpace, long time, out Posef pose)
        {
            pose = default;
            var loc = new SpaceLocation { Type = StructureType.TypeSpaceLocation };
            if (_api.LocateSpace(space, baseSpace, time, ref loc) != Result.Success) return false;
            pose = loc.Pose;
            return (loc.LocationFlags & SpaceLocationFlags.OrientationValidBit) != 0
                && (loc.LocationFlags & SpaceLocationFlags.PositionValidBit) != 0;
        }

        public void Haptic(float amplitude, float seconds)
        {
            var gi = new HapticActionInfo { Type = StructureType.TypeHapticActionInfo, Action = _aHaptic };
            var vib = new HapticVibration
            {
                Type = StructureType.TypeHapticVibration,
                Duration = (long)(seconds * 1_000_000_000.0),
                Frequency = 0f, // XR_FREQUENCY_UNSPECIFIED
                Amplitude = amplitude
            };
            _api.ApplyHapticFeedback(_session, &gi, (HapticBaseHeader*)&vib);
        }

        // -------- helpers --------

        private bool MakeAction(string name, string localized, ActionType type, out XrAction action, out string error)
        {
            error = null;
            action = default;
            var ci = new ActionCreateInfo { Type = StructureType.TypeActionCreateInfo, ActionType = type };
            SetFixed(ci.ActionName, name);
            SetFixed(ci.LocalizedActionName, localized);
            if (_api.CreateAction(_actionSet, &ci, ref action) != Result.Success)
            { error = "xrCreateAction failed for " + name; return false; }
            return true;
        }

        private bool Suggest(string profile, (XrAction act, string path)[] binds)
        {
            var arr = new ActionSuggestedBinding[binds.Length];
            for (int i = 0; i < binds.Length; i++)
                arr[i] = new ActionSuggestedBinding { Action = binds[i].act, Binding = Path(binds[i].path) };
            fixed (ActionSuggestedBinding* p = arr)
            {
                var info = new InteractionProfileSuggestedBinding
                {
                    Type = StructureType.TypeInteractionProfileSuggestedBinding,
                    InteractionProfile = Path(profile),
                    CountSuggestedBindings = (uint)arr.Length,
                    SuggestedBindings = p
                };
                var r = _api.SuggestInteractionProfileBinding(_instance, &info);
                if (r != Result.Success) { Log.LogWarning($"[input] profile rejected ({profile}): {r}"); return false; }
            }
            return true;
        }

        private ulong Path(string s)
        {
            ulong p = 0;
            var ptr = SilkMarshal.StringToPtr(s, NativeStringEncoding.UTF8);
            _api.StringToPath(_instance, (byte*)ptr, ref p);
            SilkMarshal.Free(ptr);
            return p;
        }

        private float GetFloat(XrAction a)
        {
            var gi = new ActionStateGetInfo { Type = StructureType.TypeActionStateGetInfo, Action = a };
            var st = new ActionStateFloat { Type = StructureType.TypeActionStateFloat };
            _api.GetActionStateFloat(_session, &gi, ref st);
            return st.IsActive != 0 ? st.CurrentState : 0f;
        }

        private bool GetBool(XrAction a)
        {
            var gi = new ActionStateGetInfo { Type = StructureType.TypeActionStateGetInfo, Action = a };
            var st = new ActionStateBoolean { Type = StructureType.TypeActionStateBoolean };
            _api.GetActionStateBoolean(_session, &gi, ref st);
            return st.IsActive != 0 && st.CurrentState != 0;
        }

        private void GetVector2(XrAction a, out float x, out float y)
        {
            x = 0f; y = 0f;
            var gi = new ActionStateGetInfo { Type = StructureType.TypeActionStateGetInfo, Action = a };
            var st = new ActionStateVector2f { Type = StructureType.TypeActionStateVector2f };
            if (_api.GetActionStateVector2(_session, &gi, ref st) != Result.Success || st.IsActive == 0) return;
            x = st.CurrentState.X;
            y = st.CurrentState.Y;
        }

        private static void SetFixed(byte* dst, string s)
        {
            int n = Math.Min(s.Length, 62);
            for (int i = 0; i < n; i++) dst[i] = (byte)s[i];
            dst[n] = 0;
        }

        public void Dispose()
        {
            try
            {
                if (_api != null)
                {
                    if (_spacesReady) { _api.DestroySpace(_aimSpace); _api.DestroySpace(_aimSpaceL); _api.DestroySpace(_gripSpace); _api.DestroySpace(_gripSpaceL); }
                    _api.DestroyActionSet(_actionSet);
                }
            }
            catch { }
        }
    }
}
