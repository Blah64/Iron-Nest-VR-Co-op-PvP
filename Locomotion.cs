using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Silk.NET.OpenXR;
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
        private int _popCount;           // times the anti-pop guard has fired (throttled logging)
        private float _nextPopLog;
        private float _floatTimer;       // how long we've continuously looked "floating"
        private float _nextFloatCheck;   // throttle the ground raycast
        private float _lastFloatCheck;   // unscaled time of the previous check (for timer accumulation)
        private float _nextFloatLog;
        private int _floatRecoveries;
        private bool _snapArmed = true; // snap turn fires once per stick flick

        // Teleport-locomotion state (Config.TeleportMove). Aim a ground marker down the left controller's ray
        // while the stick is held; blink there on release.
        private bool _teleAiming;
        private bool _teleValid;
        private Vector3 _teleTarget;
        private float _teleFlash;        // 0..1 comfort-blink envelope, decays after a teleport
        private GameObject _teleMarker;
        private MeshRenderer _teleMr;
        private Material _teleMat;
        private GameObject _teleArcGo;   // projectile-arc line (Unity-XR-style teleport curve)
        private LineRenderer _teleArc;
        private readonly List<Vector3> _arcPts = new List<Vector3>(72);
        private float _teleLogNext;

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

            // Comfort tunnelling vignette: darken the periphery during smooth artificial motion. Driven every
            // frame (even with locomotion disabled — smooth turn still counts) and BEFORE the early-outs below.
            UpdateVignette(input, rig, dt);

            if (!Config.LocomotionEnabled) { HideTele(); return; }
            try
            {
                EnsureController();
                // Floating watchdog runs EVERY gameplay frame (not just while the stick is pushed) — the player
                // can be left stranded in the air with the stick centred. Cheap (a throttled downward raycast).
                if (Config.FloatWatchdogEnabled) FloatWatchdogTick();

                // Teleport mode replaces smooth strafing: aim + blink instead of continuous translation.
                if (Config.TeleportMove) { TeleportTick(input, rig); return; }
                HideTele();   // smooth mode: never leave a stale marker/arc up

                float x = input.MoveX, y = input.MoveY;
                float mag = Mathf.Sqrt(x * x + y * y);
                if (mag < Config.MoveDeadzone) return; // nothing more to do; below stick deadzone
                if (_cc == null) return;

                if (!rig.TryGetHeadBasis(out var fwd, out var right)) return;

                // Radial deadzone with rescale so motion ramps from 0 at the edge of the deadzone.
                float scaled = Mathf.Clamp01((mag - Config.MoveDeadzone) / (1f - Config.MoveDeadzone));
                Vector2 dir = new Vector2(x, y) / mag; // unit
                float speed = _fpc.walkSpeed;
                if (speed <= 0.01f) speed = Config.MoveSpeedFallback;
                speed *= Config.MoveSpeedScale;

                Vector3 motion = (right * dir.x + fwd * dir.y) * (scaled * speed * dt);
                Vector3 before = _cc.transform.position;
                _cc.Move(motion);

                // Locomotion is HORIZONTAL — the FirstPersonController owns vertical (gravity/jump/grounding).
                // But CharacterController.Move can rise on its own: a step-up, or — the rare "stuck floating"
                // bug — depenetrating UPWARD when the capsule briefly overlaps cockpit geometry, popping the
                // player into the air where the FPC can read isGrounded=true and zero verticalVelocity, so
                // gravity never pulls them back down. Allow a natural slope (rise up to the horizontal distance
                // moved, ≈45°) but cancel any pop beyond that, so a thumbstick walk can never launch us upward.
                // This only touches OUR Move's side-effect — a real jump (the FPC's own Move) is untouched.
                if (Config.LocomotionAntiPop)
                {
                    float rise = _cc.transform.position.y - before.y;
                    float horiz = new Vector2(motion.x, motion.z).magnitude;
                    if (rise > horiz + 0.003f)
                    {
                        _cc.Move(Vector3.down * (rise - horiz));
                        _popCount++;
                        if (Time.unscaledTime >= _nextPopLog)
                        {
                            _nextPopLog = Time.unscaledTime + 1f;
                            Log.LogWarning($"[locomotion] anti-pop cancelled upward rise={rise:0.000}m " +
                                           $"(horiz={horiz:0.000}m), count={_popCount}.");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[locomotion] " + e.Message);
                _fpc = null; _cc = null;
            }
        }

        // ---------------- comfort vignette driver ----------------

        // Compute how strongly to darken the periphery this frame and hand it to the rig (which smooths +
        // renders it). Smooth translation contributes only in smooth-locomotion mode (teleport blinks instead);
        // smooth turn always contributes. A just-fired teleport adds a brief full-strength blink regardless of
        // the movement-vignette toggle.
        private void UpdateVignette(VrInput input, CameraRig rig, float dt)
        {
            if (rig == null) return;
            float moveMag = Mathf.Sqrt(input.MoveX * input.MoveX + input.MoveY * input.MoveY);
            float moveT = Mathf.Clamp01((moveMag - Config.MoveDeadzone) / (1f - Config.MoveDeadzone));
            float turnT = (Config.TurnEnabled && !Config.SnapTurn)
                ? Mathf.Clamp01((Mathf.Abs(input.TurnX) - Config.TurnDeadzone) / (1f - Config.TurnDeadzone))
                : 0f;
            float moveContribution = (Config.LocomotionEnabled && !Config.TeleportMove) ? moveT : 0f;
            float movementVig = Config.VignetteEnabled ? Mathf.Max(moveContribution, turnT) * Config.VignetteStrength : 0f;

            if (_teleFlash > 0f) _teleFlash = Mathf.Max(0f, _teleFlash - dt / Mathf.Max(0.02f, Config.TeleportBlinkTime));
            rig.SetVignette(Mathf.Max(movementVig, _teleFlash), dt);
        }

        // ---------------- teleport locomotion ----------------

        // Aim a projectile arc out of the LEFT controller while the stick is deflected (point roughly forward;
        // the arc falls to the floor — like Unity XR's teleporter), showing a curve + ground marker. On release
        // (stick back to centre) blink to the last valid target. Snap/smooth view-turn is unaffected.
        private void TeleportTick(VrInput input, CameraRig rig)
        {
            if (_cc == null) { HideTele(); return; }
            var origin = rig.OriginTransform;
            float mag = Mathf.Sqrt(input.MoveX * input.MoveX + input.MoveY * input.MoveY);

            if (!_teleAiming && mag >= Config.TeleportEngage) _teleAiming = true;

            if (_teleAiming && mag >= Config.MoveDeadzone)
            {
                if (origin != null && input.AimValidL)
                {
                    // World ray from the left aim pose (same OpenXR→Unity convention as the menu/right laser).
                    Posef p = input.AimPoseL;
                    Vector3 lp = new Vector3(p.Position.X, p.Position.Y, -p.Position.Z);
                    Quaternion lr = new Quaternion(-p.Orientation.X, -p.Orientation.Y, p.Orientation.Z, p.Orientation.W);
                    Vector3 o = origin.TransformPoint(lp);
                    Vector3 d = (origin.rotation * lr) * Vector3.forward;
                    _teleValid = TraceArc(o, d, out _teleTarget);
                    ShowTeleAim(_teleValid);

                    if (Time.unscaledTime >= _teleLogNext)
                    {
                        _teleLogNext = Time.unscaledTime + 0.75f;
                        Log.LogInfo($"[teleport] aiming valid={_teleValid} target={_teleTarget} aimValidL={input.AimValidL} pts={_arcPts.Count}");
                    }
                }
            }
            else if (_teleAiming) // released to centre
            {
                if (_teleValid) DoTeleport(_teleTarget, input);
                else Log.LogInfo("[teleport] released with no valid target — point lower so the arc lands on a floor.");
                _teleAiming = false; _teleValid = false;
                HideTele();
            }
            else HideTele();
        }

        // Ballistic arc from the controller: step a projectile under gravity, raycasting each segment (skipping
        // our own capsule). Fills _arcPts with the polyline and returns true if it lands on standable ground.
        private bool TraceArc(Vector3 o, Vector3 fwd, out Vector3 target)
        {
            target = Vector3.zero;
            _arcPts.Clear();
            Transform self = _fpc != null ? _fpc.transform : (_cc != null ? _cc.transform : null);
            Vector3 v = fwd.normalized * Mathf.Max(1f, Config.TeleportArcSpeed);
            Vector3 g = Vector3.down * Mathf.Max(0.1f, Config.TeleportArcGravity);
            const float step = 0.04f;
            Vector3 p = o;
            _arcPts.Add(p);
            float travelled = 0f;
            for (int i = 0; i < 96; i++)
            {
                Vector3 pNext = p + v * step + 0.5f * g * (step * step);
                v += g * step;
                Vector3 seg = pNext - p;
                float segLen = seg.magnitude;
                if (segLen > 0.0001f && SegHit(p, seg / segLen, segLen, self, out RaycastHit hit))
                {
                    _arcPts.Add(hit.point);
                    target = hit.point;
                    return hit.normal.y >= Config.TeleportMinNormalY; // standable only on near-flat ground
                }
                _arcPts.Add(pNext);
                travelled += segLen;
                p = pNext;
                if (travelled > Config.TeleportMaxDistance) break;     // arc too long — give up
                if (p.y < o.y - 60f) break;                            // fell off the world
            }
            return false;
        }

        // Nearest non-self hit along a segment (the player's own capsule never counts as the floor).
        private bool SegHit(Vector3 from, Vector3 dir, float len, Transform self, out RaycastHit best)
        {
            best = default;
            var hits = Physics.RaycastAll(from, dir, len, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null) return false;
            float bd = float.MaxValue; bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null) continue;
                if (self != null && IsSelf(h.collider.transform, self)) continue;
                if (h.distance < bd) { bd = h.distance; best = h; found = true; }
            }
            return found;
        }

        private void ShowTeleAim(bool valid)
        {
            EnsureTeleVisuals();
            Color c = valid ? new Color(0.25f, 1f, 0.4f, 1f) : new Color(1f, 0.35f, 0.25f, 1f);

            // Arc line.
            if (_teleArc != null && _arcPts.Count >= 2)
            {
                _teleArc.positionCount = _arcPts.Count;
                for (int i = 0; i < _arcPts.Count; i++) _teleArc.SetPosition(i, _arcPts[i]);
                _teleArc.startColor = c; _teleArc.endColor = c;
                var am = _teleArc.material;
                if (am != null)
                {
                    try { am.color = c; } catch { }
                    try { if (am.HasProperty("_BaseColor")) am.SetColor("_BaseColor", c); } catch { }
                }
                if (!_teleArc.enabled) _teleArc.enabled = true;
            }

            // Ground disc at the landing point (only meaningful when valid; still shown red-ish at the arc end).
            if (_teleMarker != null)
            {
                _teleMarker.transform.position = _teleTarget + Vector3.up * 0.02f;
                _teleMarker.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // lie flat
                float diam = valid ? 0.55f : 0.4f;
                _teleMarker.transform.localScale = new Vector3(diam, diam, 1f);
                Color mc = valid ? new Color(0.25f, 1f, 0.4f, 0.85f) : new Color(1f, 0.35f, 0.25f, 0.6f);
                if (_teleMat != null)
                {
                    try { if (_teleMat.HasProperty("_BaseColor")) _teleMat.SetColor("_BaseColor", mc); } catch { }
                    try { _teleMat.color = mc; } catch { }
                    try { if (_teleMat.HasProperty("_Color")) _teleMat.SetColor("_Color", mc); } catch { }
                }
                if (_teleMr != null && !_teleMr.enabled) _teleMr.enabled = true;
            }
        }

        private void HideTele()
        {
            if (_teleMr != null && _teleMr.enabled) _teleMr.enabled = false;
            if (_teleArc != null && _teleArc.enabled) _teleArc.enabled = false;
        }

        private void EnsureTeleVisuals()
        {
            EnsureTeleMarker();
            if (_teleArc != null) return;
            try
            {
                _teleArcGo = new GameObject("IronNestVR_TeleportArc");
                UnityEngine.Object.DontDestroyOnLoad(_teleArcGo);
                _teleArcGo.hideFlags = HideFlags.HideAndDontSave;
                _teleArcGo.layer = 0;
                _teleArc = _teleArcGo.AddComponent<LineRenderer>();
                _teleArc.useWorldSpace = true;
                _teleArc.numCapVertices = 2;
                _teleArc.startWidth = 0.02f;
                _teleArc.endWidth = 0.02f;
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh != null) _teleArc.material = new Material(sh);
                _teleArc.enabled = false;
            }
            catch (Exception e) { Log.LogWarning("[locomotion] arc build: " + e.Message); }
        }

        private void DoTeleport(Vector3 target, VrInput input)
        {
            try
            {
                Transform t = _cc.transform;
                // Place the capsule so its FEET land on the target (feet = transform.y + center.y - height/2),
                // nudged up slightly so it doesn't spawn inside the floor — gravity settles it.
                float feetToCenter = _cc.center.y - _cc.height * 0.5f;
                Vector3 pos = new Vector3(target.x, target.y - feetToCenter + 0.05f, target.z);
                bool wasEnabled = _cc.enabled;
                _cc.enabled = false;   // CharacterController caches its position; disable so the move sticks
                t.position = pos;
                _cc.enabled = wasEnabled;
                _teleFlash = 1f;       // comfort blink
                input.Haptic(Config.HapticAmplitude, Config.HapticSeconds);
            }
            catch (Exception e) { Log.LogWarning("[locomotion] teleport: " + e.Message); _fpc = null; _cc = null; }
        }

        private void ShowTeleMarker(Vector3 pos, bool valid)
        {
            EnsureTeleMarker();
            if (_teleMarker == null) return;
            _teleMarker.transform.position = pos + Vector3.up * 0.02f;
            _teleMarker.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // lie flat on the ground
            float diam = valid ? 0.55f : 0.45f;
            _teleMarker.transform.localScale = new Vector3(diam, diam, 1f);
            Color c = valid ? new Color(0.25f, 1f, 0.4f, 0.85f) : new Color(1f, 0.35f, 0.25f, 0.7f);
            if (_teleMat != null)
            {
                try { _teleMat.color = c; } catch { }
                try { if (_teleMat.HasProperty("_Color")) _teleMat.SetColor("_Color", c); } catch { }
            }
            if (_teleMr != null && !_teleMr.enabled) _teleMr.enabled = true;
        }

        private void HideTeleMarker()
        {
            if (_teleMr != null && _teleMr.enabled) _teleMr.enabled = false;
        }

        private void EnsureTeleMarker()
        {
            if (_teleMarker != null) return;
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "IronNestVR_TeleportMarker";
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.layer = 0;   // default world layer — rendered by both eye cameras (correct stereo on the floor)
                var col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col); // never block the teleport/laser raycasts
                // Real URP shader — Sprites/Default doesn't draw under this URP pipeline (same trap as the vignette).
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Transparent");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                _teleMat = new Material(sh);
                CameraRig.MakeTransparentUnlit(_teleMat);
                var dtex = BuildDiscTexture();
                _teleMat.mainTexture = dtex;
                try { if (_teleMat.HasProperty("_BaseMap")) _teleMat.SetTexture("_BaseMap", dtex); } catch { }
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) { mr.material = _teleMat; mr.enabled = false; }
                _teleMarker = go;
                _teleMr = mr;
            }
            catch (Exception e) { Log.LogWarning("[locomotion] marker build: " + e.Message); }
        }

        // Soft white filled disc (solid centre, fading edge); colour comes from the material tint.
        private static Texture2D BuildDiscTexture()
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Il2CppStructArray<Color32>(N * N);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N - 0.5f, dy = (y + 0.5f) / N - 0.5f;
                    float nr = Mathf.Sqrt(dx * dx + dy * dy) / 0.5f;
                    float a = 1f - Mathf.Clamp01((nr - 0.7f) / 0.3f); // solid to 0.7, fades to edge
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        // Symptom-level safety net for the "stuck floating" bug, independent of what lifted the player (our
        // Move's slow-climb leak that slips under the anti-pop slope allowance, OR a game-side shove): each
        // check we find the REAL floor under the capsule and, if it has hung above it for FloatStuckSeconds
        // without falling, drop it back down and clear the FPC's stuck grounded/zero-velocity state so the
        // game's own gravity resumes. A genuine jump/fall is exempt — it resolves before the dwell time or
        // shows a clear downward verticalVelocity. Throttled to ~10 Hz (one raycast) to stay allocation-light.
        private void FloatWatchdogTick()
        {
            if (_cc == null) return;
            if (Time.unscaledTime < _nextFloatCheck) return;
            float since = Time.unscaledTime - _lastFloatCheck;
            if (since <= 0f || since > 1f) since = 0.1f; // first run / after a gap: use the nominal interval
            _lastFloatCheck = Time.unscaledTime;
            _nextFloatCheck = Time.unscaledTime + 0.1f;

            if (!GroundBelow(out float groundY)) { _floatTimer = 0f; return; }

            Vector3 pos = _cc.transform.position;
            float feet = pos.y + _cc.center.y - _cc.height * 0.5f;
            float gap = feet - groundY;
            float vy = 0f; bool fpcGrounded = false;
            try { vy = _fpc.verticalVelocity; fpcGrounded = _fpc.isGrounded; } catch { }
            bool falling = vy < -Config.FloatFallSpeed;

            if (gap > Config.FloatGapThreshold && !falling)
            {
                _floatTimer += since;
                if (Time.unscaledTime >= _nextFloatLog)
                {
                    _nextFloatLog = Time.unscaledTime + 0.5f;
                    Log.LogWarning($"[locomotion] suspected float: gap={gap:0.00}m vy={vy:0.00} " +
                                   $"grounded(cc={_cc.isGrounded},fpc={fpcGrounded}) held={_floatTimer:0.00}s");
                }
                if (_floatTimer >= Config.FloatStuckSeconds)
                {
                    _cc.Move(Vector3.down * (gap + 0.05f)); // reseat on the floor — Move stops at it
                    try { _fpc.isGrounded = false; _fpc.verticalVelocity = -2f; } catch { } // let FPC gravity re-settle
                    _floatRecoveries++;
                    Log.LogWarning($"[locomotion] RECOVERED stuck-float #{_floatRecoveries}: dropped {gap:0.00}m " +
                                   $"to ground (vy was {vy:0.00}).");
                    _floatTimer = 0f;
                }
            }
            else _floatTimer = 0f;
        }

        // Nearest solid floor directly below the player, skipping the player's own colliders. RaycastAll (the
        // proven interop path here) from just above the capsule, straight down.
        private bool GroundBelow(out float groundY)
        {
            groundY = 0f;
            Vector3 origin = _cc.transform.position + Vector3.up * 0.2f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null) return false;
            Transform self = _fpc != null ? _fpc.transform : _cc.transform;
            float best = float.MaxValue; bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                var col = h.collider;
                if (col == null) continue;
                if (IsSelf(col.transform, self)) continue; // don't treat our own capsule as the floor
                if (h.distance < best) { best = h.distance; groundY = h.point.y; found = true; }
            }
            return found;
        }

        private static bool IsSelf(Transform t, Transform self)
        {
            for (Transform p = t; p != null; p = p.parent) if (p == self) return true;
            return false;
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

        public void Reset()
        {
            _fpc = null; _cc = null; _loggedFound = false; _floatTimer = 0f;
            _teleAiming = false; _teleValid = false; _teleFlash = 0f;
            if (_teleMarker != null) { try { UnityEngine.Object.Destroy(_teleMarker); } catch { } _teleMarker = null; _teleMr = null; }
            if (_teleMat != null) { try { UnityEngine.Object.Destroy(_teleMat); } catch { } _teleMat = null; }
            if (_teleArcGo != null) { try { UnityEngine.Object.Destroy(_teleArcGo); } catch { } _teleArcGo = null; _teleArc = null; }
        }
    }
}
