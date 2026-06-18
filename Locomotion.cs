using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Left-thumbstick locomotion. Drives the game's <c>FirstPersonController.controller</c>
    /// (a Unity <see cref="CharacterController"/>) directly so collisions/slopes are respected,
    /// moving relative to where the player is looking in the headset. Speed comes from the game's
    /// own <c>walkSpeed</c> so it matches the flat game.
    /// </summary>
    internal sealed class Locomotion
    {
        private static ManualLogSource Log => Plugin.Logger;

        private FirstPersonController _fpc;
        private CharacterController _cc;
        private float _nextFind;
        private bool _loggedFound;
        private bool _snapArmed = true; // snap turn fires once per stick flick

        public void Tick(VrInput input, CameraRig rig, float dt)
        {
            if (dt <= 0f) return;

            // Right-stick view turn (self-contained in the rig; no game objects needed).
            if (Config.TurnEnabled)
            {
                float tx = input.TurnX;
                if (Config.SnapTurn)
                {
                    // Discrete snap: rotate a fixed angle on the rising flick, then wait for re-arm.
                    if (_snapArmed && Mathf.Abs(tx) >= Config.SnapTurnThreshold)
                    {
                        rig.ApplyTurn(Mathf.Sign(tx) * Config.SnapTurnAngle);
                        input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
                        _snapArmed = false;
                    }
                    else if (Mathf.Abs(tx) < Config.SnapTurnReArm)
                    {
                        _snapArmed = true;
                    }
                }
                else if (Mathf.Abs(tx) > Config.TurnDeadzone)
                {
                    float scaledT = (Mathf.Abs(tx) - Config.TurnDeadzone) / (1f - Config.TurnDeadzone);
                    rig.ApplyTurn(Mathf.Sign(tx) * scaledT * Config.TurnSpeedDegPerSec * dt);
                }
            }

            if (!Config.LocomotionEnabled) return;
            try
            {
                float x = input.MoveX, y = input.MoveY;
                float mag = Mathf.Sqrt(x * x + y * y);
                if (mag < Config.MoveDeadzone) return; // nothing to do; don't even bother finding the FPC

                EnsureController();
                if (_cc == null) return;

                if (!rig.TryGetHeadBasis(out var fwd, out var right)) return;

                // Radial deadzone with rescale so motion ramps from 0 at the edge of the deadzone.
                float scaled = Mathf.Clamp01((mag - Config.MoveDeadzone) / (1f - Config.MoveDeadzone));
                Vector2 dir = new Vector2(x, y) / mag; // unit
                float speed = _fpc.walkSpeed;
                if (speed <= 0.01f) speed = Config.MoveSpeedFallback;
                speed *= Config.MoveSpeedScale;

                Vector3 motion = (right * dir.x + fwd * dir.y) * (scaled * speed * dt);
                _cc.Move(motion);
            }
            catch (Exception e)
            {
                Log.LogWarning("[locomotion] " + e.Message);
                _fpc = null; _cc = null;
            }
        }

        private void EnsureController()
        {
            if (_cc != null) return;
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;

            var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<FirstPersonController>(), FindObjectsSortMode.None);
            if (arr != null && arr.Length > 0)
            {
                _fpc = arr[0].TryCast<FirstPersonController>();
                _cc = _fpc != null ? _fpc.controller : null;
                if (_cc != null && !_loggedFound)
                {
                    _loggedFound = true;
                    Log.LogInfo($"[locomotion] FirstPersonController found (walkSpeed={_fpc.walkSpeed:0.##}).");
                }
            }
        }

        public void Reset() { _fpc = null; _cc = null; _loggedFound = false; }
    }
}
