using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Central tunables. These are plain constants for now; Phase 5 swaps them for a BepInEx
    /// config file so they can be edited without rebuilding.
    /// </summary>
    internal static class Config
    {
        // How often (seconds) to emit a head-pose line to the log during bring-up/debugging.
        public const float PoseLogIntervalSec = 2f;

        // How often (seconds) to emit a frame-rate / worst-frame line to the log.
        public const float PerfLogIntervalSec = 5f;

        // How often (seconds) to retry OpenXR init while no headset/runtime is available yet.
        public const float XrRetryIntervalSec = 5f;

        // Co-op: cap on how many pose packets we transmit per second. A high-fps VR host otherwise floods the
        // wire (~90/s) with near-redundant poses; 30 Hz keeps the remote avatar smooth (receiver snaps to the
        // latest pose each frame). A client running BELOW this still sends every frame, so a slow peer is never
        // throttled further. Set <= 0 to disable the cap (send every frame).
        public static float CoopSendHz = 30f;

        // Phase 3 co-op: replicate cockpit-control operation + turret/gun physical state between the two
        // lobby members (transient per-control ownership). Off = avatars-only (Phase 2). Reuses CoopSendHz
        // for the value/state stream rate.
        public static bool CoopControlSync = true;

        // Phase 3 co-op: replicate click-activated controls (LookAtTarget — reload rammers, powder dispenser,
        // power lever, lighting, fire button) and gun discharge as events, replayed via the game's own click
        // handler so the switch animation + effect (reload, fire) play on the peer too. Skips manuals + menus.
        public static bool CoopClickSync = true;

        // Phase 3 co-op: replicate HUD clipboard contents (per-section text + active tool) between the two
        // members via state-based diff keyed by NotepadSection.UnityTag. Captures player notes AND
        // mission-graph briefing text. Pose (raised/focused/hidden) stays per-player and is NOT synced.
        public static bool CoopClipboardSync = true;

        // Phase 3 co-op: replicate tactical-map item placements (DraggableItem tokens) with transient
        // per-item ownership, in MapPiece3D-board-local space (Barbet-relative, so turret rotation/sway and
        // per-player board pan don't desync them). Dynamic markers + mission entities are separate/later.
        public static bool CoopMapSync = true;

        // How often (seconds) the co-op diagnostics hub logs its status block while in a lobby. Keep it
        // frequent enough to be useful to testers but not so chatty it floods the log.
        public static float CoopDiagIntervalSec = 4f;

        // Co-op join-in-progress: when a second player joins an already-running session, the HOST waits this
        // long after first detecting them, then pushes a ONE-TIME authoritative snapshot of the current world
        // (turret/gun aim + powder, clipboard text, map-token layout, held-control ownership) so the joiner
        // adopts the host's state instead of a stale default. The delay lets BOTH sides resolve the peer and
        // bring the P2P session up first — until then each side's receive gate drops packets from a not-yet-
        // resolved peer, so an instant snapshot could be discarded. 1.5s is comfortably past lobby/session
        // settling without a noticeable "pop-in" wait for the joiner.
        public static float CoopSnapshotDelaySec = 1.5f;

        // Phase 4 co-op: host-authoritative simulation. The HOST runs the mission node-graph (enemy spawns,
        // damage, objectives, score); a co-op CLIENT's copy is GATED OFF (Harmony-suppressed MissionGraph /
        // MissionPassiveGraph Run+Update) so it never double-spawns — it mirrors the host's world instead.
        // Active ONLY for a client connected to a peer; solo play and the host always run the sim. The gated
        // methods only execute during a mission, so this never touches the validated hub co-op. See CoopSim.
        public static bool CoopSimAuthority = true;

        // Phase 4 co-op (4b): replicate the host's mission ENTITIES (enemies/targets) to the client. The host
        // diffs FindObjectsByType<EntityLocation>() and broadcasts spawn/move/state/despawn; the client mirrors
        // them (adopts a same-ID scene entity or clones a cached EntityLocation template). Scoped to an active
        // mission (GamePhase.MissionActive) so the hub is untouched. Needs CoopSimAuthority on (gated client).
        public static bool CoopEntitySync = true;

        // Phase 4 co-op (4b keystone): replicate the mission/scene transition so both players are co-located.
        // The HOST broadcasts its own phase changes (→MissionActive / back out); the CLIENT follows by driving
        // its own OperationLoadRelay.StartAssignedOperation() / ReturnToMap(). Without this, the host starting a
        // mission leaves the client in the hub and entity sync has nothing to mirror into. See CoopScene.
        public static bool CoopSceneSync = true;

        // Phase 4 co-op (4d): replicate the TELEPRINTER "typing machine" orders. The host captures every
        // Teleprinter.SubmitLines (resolved order text) and the client replays it on its matching printer, so
        // the gated client's teleprinter types the same orders. Scoped to an active mission (hub runs its own
        // teleprinter locally on both, so syncing there would double-print). See CoopOrders.
        public static bool CoopOrdersSync = true;

        // Co-op: how long (seconds) the remote avatar holds its last pose before we hide it as stale. Must be
        // generous enough to ride out a low-fps / hitchy peer (a 4 fps client sends only ~4 poses/sec and can
        // gap several seconds during a freeze) — otherwise the avatar blinks out. Genuine disconnects clear it
        // immediately via lobby peer-leave, so this only backstops a peer that's present but not sending.
        public static float RemoteStaleSeconds = 8f;

        // Camera near/far used by the VR eye cameras (cm-scale cockpit → small near plane).
        public const float NearClip = 0.02f;
        public const float FarClip = 2000f;

        // Multiplier on the runtime's recommended per-eye resolution. The Galaxy XR's recommended
        // size is enormous (~3648x3936); rendering the scene twice at full size can TDR the GPU.
        // 0.7 keeps quality high while cutting pixel count to ~half. Lower if unstable, raise later.
        public static float RenderScale = 0.4f;

        // --- Diagnostics / quick toggles for live tuning ---
        // Phase 2.5 isolation: render each eye as a flat clear color (no scene) to prove the
        // swapchain copy + projection-layer submission path independent of scene rendering.
        public static bool SolidColorTest = false;

        // Flip the rendered eye image vertically (common mismatch between Unity RT origin and the
        // OpenXR swapchain expectation). Toggle if the headset image is upside-down.
        public static bool FlipY = true;

        // Headless rendering self-test: exercises eye-camera creation, the URP render path, and the
        // D3D11 copy WITHOUT OpenXR (no headset needed) so the render pipeline can be validated and
        // crashes isolated independently of the swapchain/submit path.
        public static bool SelfTestRender = false;

        // Self-test bisection stage: 0 = create eye cameras only; 1 = + render each eye;
        // 2 = + D3D11 copy between RTs. A surviving stage emits a steady heartbeat.
        public static int SelfTestStage = 2;

        // Use enabled auto-rendering cameras instead of RenderPipeline.SubmitRenderRequest<T>.
        // The generic render-request can hard-crash under Il2CppInterop if the game never compiled
        // that generic instantiation, so this is the safer default for an injected plugin.
        public static bool UseEnabledCameras = true;

        // --- Phase 4: motion-controller cockpit interaction ---
        // Trigger pull fraction that counts as a click/grab "press" (rising edge).
        public static float TriggerFireThreshold = 0.6f;
        // Haptic tap on click/toggle.
        public static float HapticAmplitude = 0.5f;
        public static float HapticSeconds = 0.05f;

        // Interaction mode: false = laser-pointer (aim pose, long ray), true = hand-grab (grip pose,
        // short reach). Toggle in-game with the controller's toggle button.
        public static bool StartHandMode = false;
        // How far the laser reaches (clamped up to at least the game's own ray distance).
        public static float LaserMaxDistance = 8f;
        // Reach distance in hand-grab mode — you must put your hand on/into the control.
        public static float HandMaxDistance = 0.30f;
        // Laser/pointer line. When false (default), it only appears while aimed at something
        // interactable; when true it's drawn at all times.
        public static bool LaserAlwaysOn = false;
        public static float LaserWidth = 0.004f;

        // --- Locomotion (left thumbstick) ---
        // Drive the game's FirstPersonController with the left thumbstick.
        public static bool LocomotionEnabled = true;
        // Stick magnitude below this is ignored (drift guard).
        public static float MoveDeadzone = 0.15f;
        // Scales the game's own walkSpeed for VR comfort (1 = native speed).
        public static float MoveSpeedScale = 1f;
        // Fallback m/s if the FirstPersonController's walkSpeed reads as 0.
        public static float MoveSpeedFallback = 4f;

        // --- View turn (right thumbstick) ---
        public static bool TurnEnabled = true;
        public static float TurnDeadzone = 0.2f;
        // Smooth-turn rate (deg/sec) at full stick deflection.
        public static float TurnSpeedDegPerSec = 110f;
        // Snap turn instead of smooth: flick the stick to rotate by a fixed angle once.
        public static bool SnapTurn = false;
        public static float SnapTurnAngle = 30f;     // degrees per snap
        public static float SnapTurnThreshold = 0.7f; // stick past this triggers a snap
        public static float SnapTurnReArm = 0.3f;     // stick must fall below this before the next snap

        // --- HUD follow (legacy) ---
        // Superseded by GrabManager, which now head-locks BOTH the clipboard and the watch with a
        // runtime toggle (HudRotateWithCamera). HudFollower reparented the watch under its own anchor
        // and ALWAYS followed head yaw, which fought GrabManager and ignored the toggle — so it's off.
        public static bool HudFollowEnabled = false;
        // Exponential lag time-constant (s): higher = lazier/smoother follow.
        public static float HudFollowPosLag = 0.18f;
        public static float HudFollowRotLag = 0.22f;
        // Push the HUD forward/up from the head so the clipboard is readable & reachable (metres).
        public static float HudPushForward = 0.28f;
        public static float HudPushUp = 0f;

        // --- Clipboard grab-to-place ---
        public static bool ClipboardGrabEnabled = true;
        // Also let the world "operating manual" clipboard props (PickUpZoomTarget) be grip-grabbed and
        // repositioned in 3D (world-locked). Trigger still does the game's click-to-zoom-and-read.
        public static bool ManualGrabEnabled = true;
        // Head-locked props (HUD clipboard + watch) rotate WITH the VR camera when true; when false
        // they keep a fixed orientation and only follow your position (you turn to look at them).
        public static bool HudRotateWithCamera = false;
        // Hand must be within this distance (m) of the clipboard to grab it with the grip button.
        public static float GrabRadius = 0.4f;
        // Scale the clipboard up for VR: it was authored for the flat ~60° FOV; the VR view is ~94°,
        // so the same object looks ~half size. 1 = no change.
        public static float ClipboardScale = 2.5f;
        // Same idea for the gun watch (independent knob — it's a different size to start with).
        public static float WatchScale = 2.5f;

        // --- VR settings menu (click BOTH thumbsticks at once to open/close) ---
        public static bool MenuEnabled = true;
        // Where the panel appears, relative to the head when opened (it then stays put in the world).
        public static float MenuDistance = 0.8f;       // metres in front
        public static float MenuHeightOffset = -0.05f; // metres below eye level
        // Overall size multiplier for the whole panel + text (one knob to tune readability).
        public static float MenuScale = 1f;
        // Rotate the panel 180° if it ends up facing away from you (handedness sanity flip).
        public static bool MenuFlip = false;

        // --- Menu / UI aiming ---
        // The game's cursor manager raycasts UI through a SCREEN point (the virtual cursor position).
        // In menus it flips to FreeMouse, so the cursor sits at the (off-centre) mouse position and the
        // ray gains a constant angular offset from the controller — you have to aim to the side, and the
        // offset shifts with render scale. Forcing FPSLocked + lock-to-centre while we drive the cursor
        // pins it to screen-centre, so the ray == controller forward (no offset, render-scale-independent).
        public static bool MenuForceCenter = true;

        // --- Tactical map (bearing/range lines + hover sector grid) ---
        // The map's line-drawing (MapMarkerPlacer) and the hover sector grid
        // (GridSquareHighlighterWithSubsector) both project the virtual cursor's SCREEN position (which we
        // pin to centre while engaged) through a camera onto the world-space map canvas. Out of the box
        // that camera is the flat Main Camera, so lines + grid land where the HEAD gazes, not where the
        // controller points. When on, we repoint those systems' cameras down our pointer cam (the same fix
        // we already apply to dials/levers/tooltips), force the placer's marker hover on, and wire the
        // right-hand delete button to the placer's secondary (delete-hovered-marker) action. Off = leave the
        // game's flat cameras alone.
        public static bool MapVrEnabled = true;

        // --- Interaction diagnostics ---
        // Throttled per-frame log of head/controller geometry + hover target (for tuning aim).
        public static bool LogInteractGeometry = true;
        public static float InteractLogIntervalSec = 2f;

        // --- Phase 6: VR hand models + physical dial/lever grab ("gravity glove") ---
        // Render tracked hand models at the controller grip poses. Loaded from an AssetBundle
        // shipped beside the plugin; if the bundle or a prefab is missing, a simple primitive hand
        // is used instead so the grab mechanic still works without the art.
        public static bool HandsEnabled = true;
        // AssetBundle file (looked for next to IronNestVR.dll) and the prefab asset names inside it.
        // If HandPrefabLeft is absent, the right prefab is mirrored (negative-X scale) for the left.
        public static string HandBundleFile = "hands.bundle";
        public static string HandPrefabRight = "HandRight";
        public static string HandPrefabLeft = "HandLeft";
        // Re-map any non-URP material on the loaded hand to URP/Lit (a bundle built outside a URP
        // project renders magenta otherwise). Turn off if your bundle already uses URP shaders.
        public static bool HandFixMaterials = true;
        // Uniform scale of the hand model. Per-hand local pose offset from the raw grip pose is set in
        // VR with the Calibrate tool (grab the hand with the opposite controller and move it) — wrist
        // origins vary between assets, and grabbing it into place beats guessing slider values.
        public static float HandScale = 1f;
        // Defaults are a real in-VR calibration (baked in so a fresh install's hands sit roughly right
        // out of the box); re-Calibrate in the menu for your own controllers, or Reset Hand Offsets.
        public static Vector3 HandOffsetPosR = new Vector3(0.038537443f, 0.015747365f, 0.02024787f);  // right: model local position (m) under its anchor
        public static Vector3 HandOffsetEulR = new Vector3(43.800255f, 19.527473f, 309.81247f);       // right: model local euler (deg)
        public static Vector3 HandOffsetPosL = new Vector3(-0.04657502f, 0.020743763f, 0.0036829882f); // left
        public static Vector3 HandOffsetEulL = new Vector3(47.386585f, 340.40277f, 69.669525f);
        // Animator float params driven by trigger (index curl) and grip (fist). Set to "" to disable
        // if your hand prefab has no Animator/controller with these parameters.
        public static string HandTriggerParam = "Trigger";
        public static string HandGripParam = "Grip";

        // Procedural finger curl (for skinned hand FBXs that ship without an Animator, e.g. the XR Hands
        // sample meshes). We find the finger-joint bones by name and rotate them: the index follows the
        // analog trigger, the other fingers + thumb follow the grip. Curl axis/sign/angle are tunable in
        // the in-VR menu because the correct local axis depends on the rig.
        public static bool FingerCurlEnabled = true;
        public static float FingerCurlMaxDeg = 70f;   // per-joint curl at full close (proximal/intermediate/distal)
        // XR Hands rig: each joint's local +Z runs down the finger, so flexion is rotation about local X,
        // and +angle folds toward the palm (-Y). (Tunable in-menu for other rigs.)
        public static int FingerCurlAxis = 0;         // local rotate axis: 0=X, 1=Y, 2=Z
        public static float FingerCurlSign = 1f;      // +1 / -1 (which way the joints fold)
        public static float FingerCurlSmooth = 14f;   // per-second rate the grip on/off curl eases at

        // Physical grab of dials/levers: right-hand grip while the laser is on a control snaps the
        // hand onto it and drives it from controller motion (the laser stays your targeting tool).
        public static bool HandManipEnabled = true;
        // Seconds to lerp the hand from the controller to the grabbed control (and back on release).
        public static float HandSnapTime = 0.12f;
        // Dial: degrees of dial rotation per degree of controller twist about the dial's spin axis.
        public static float DialTwistSensitivity = 1f;
        // Lever: metres of handle travel per metre of controller travel along the lever's axis.
        public static float LeverMoveSensitivity = 1f;
        // Hold a dial smoothly by disabling its detents while grabbed; restored on release so it
        // snaps to the nearest detent. Turn off if a dial misbehaves.
        public static bool HandManipSuppressDetents = true;
        // Haptic tick amplitude on grab/release and per detent-step crossed while turning a dial.
        public static float DetentHapticAmplitude = 0.3f;

        // Physical grab of CLICK switches/buttons (LookAtTarget — the simple click-to-activate controls,
        // not drag dials/levers). Grip-grab one with the laser on it, then PHYSICALLY MOVE your hand: once
        // the hand travels past SwitchThrowDistance the control fires its click (as if you flipped it).
        public static bool SwitchGrabEnabled = true;
        // Metres the grabbed hand must travel from the grab point to trip the switch.
        public static float SwitchThrowDistance = 0.05f;
        // Once tripped, the hand must return within this distance of the grab point to re-arm — so a
        // back-and-forth flips a toggle on then off within one grab (keep < SwitchThrowDistance).
        public static float SwitchThrowReset = 0.025f;

        // ---------------- persistence ----------------
        // The plain static fields stay the source of truth; Save/Load just mirror the user-tunable subset
        // (everything the in-VR menu + hand calibration changes) to a key=value file so it survives between
        // sessions. Loaded once at startup (Plugin.Load); saved when the settings menu closes.

        private static string SettingsPath => Path.Combine(BepInEx.Paths.ConfigPath, "IronNestVR.cfg");

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# IronNestVR settings — written by the in-VR menu. Delete this file to reset to defaults.");
                WF(sb, "ClipboardScale", ClipboardScale);
                WF(sb, "WatchScale", WatchScale);
                WF(sb, "RenderScale", RenderScale);
                WB(sb, "SnapTurn", SnapTurn);
                WF(sb, "TurnSpeedDegPerSec", TurnSpeedDegPerSec);
                WF(sb, "SnapTurnAngle", SnapTurnAngle);
                WF(sb, "MoveSpeedScale", MoveSpeedScale);
                WB(sb, "HudRotateWithCamera", HudRotateWithCamera);
                WB(sb, "LaserAlwaysOn", LaserAlwaysOn);
                WB(sb, "HandsEnabled", HandsEnabled);
                WF(sb, "HandScale", HandScale);
                WV(sb, "HandOffsetPosR", HandOffsetPosR);
                WV(sb, "HandOffsetEulR", HandOffsetEulR);
                WV(sb, "HandOffsetPosL", HandOffsetPosL);
                WV(sb, "HandOffsetEulL", HandOffsetEulL);
                WB(sb, "FingerCurlEnabled", FingerCurlEnabled);
                WF(sb, "FingerCurlMaxDeg", FingerCurlMaxDeg);
                WI(sb, "FingerCurlAxis", FingerCurlAxis);
                WF(sb, "FingerCurlSign", FingerCurlSign);
                WB(sb, "SwitchGrabEnabled", SwitchGrabEnabled);
                WF(sb, "SwitchThrowDistance", SwitchThrowDistance);
                File.WriteAllText(SettingsPath, sb.ToString());
                Plugin.Logger?.LogInfo("[config] saved settings to " + SettingsPath);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[config] save failed: " + e.Message); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var lines = File.ReadAllLines(SettingsPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
                Plugin.Logger?.LogInfo("[config] loaded settings from " + SettingsPath);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[config] load failed: " + e.Message); }
        }

        private static void Apply(string k, string v)
        {
            switch (k)
            {
                case "ClipboardScale": ClipboardScale = PF(v, ClipboardScale); break;
                case "WatchScale": WatchScale = PF(v, WatchScale); break;
                case "RenderScale": RenderScale = PF(v, RenderScale); break;
                case "SnapTurn": SnapTurn = PB(v, SnapTurn); break;
                case "TurnSpeedDegPerSec": TurnSpeedDegPerSec = PF(v, TurnSpeedDegPerSec); break;
                case "SnapTurnAngle": SnapTurnAngle = PF(v, SnapTurnAngle); break;
                case "MoveSpeedScale": MoveSpeedScale = PF(v, MoveSpeedScale); break;
                case "HudRotateWithCamera": HudRotateWithCamera = PB(v, HudRotateWithCamera); break;
                case "LaserAlwaysOn": LaserAlwaysOn = PB(v, LaserAlwaysOn); break;
                case "HandsEnabled": HandsEnabled = PB(v, HandsEnabled); break;
                case "HandScale": HandScale = PF(v, HandScale); break;
                case "HandOffsetPosR": HandOffsetPosR = PV(v, HandOffsetPosR); break;
                case "HandOffsetEulR": HandOffsetEulR = PV(v, HandOffsetEulR); break;
                case "HandOffsetPosL": HandOffsetPosL = PV(v, HandOffsetPosL); break;
                case "HandOffsetEulL": HandOffsetEulL = PV(v, HandOffsetEulL); break;
                case "FingerCurlEnabled": FingerCurlEnabled = PB(v, FingerCurlEnabled); break;
                case "FingerCurlMaxDeg": FingerCurlMaxDeg = PF(v, FingerCurlMaxDeg); break;
                case "FingerCurlAxis": FingerCurlAxis = PI(v, FingerCurlAxis); break;
                case "FingerCurlSign": FingerCurlSign = PF(v, FingerCurlSign); break;
                case "SwitchGrabEnabled": SwitchGrabEnabled = PB(v, SwitchGrabEnabled); break;
                case "SwitchThrowDistance": SwitchThrowDistance = PF(v, SwitchThrowDistance); break;
            }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static void WF(StringBuilder sb, string k, float v) => sb.AppendLine(k + "=" + v.ToString("R", Inv));
        private static void WI(StringBuilder sb, string k, int v) => sb.AppendLine(k + "=" + v.ToString(Inv));
        private static void WB(StringBuilder sb, string k, bool v) => sb.AppendLine(k + "=" + (v ? "true" : "false"));
        private static void WV(StringBuilder sb, string k, Vector3 v) =>
            sb.AppendLine(k + "=" + v.x.ToString("R", Inv) + " " + v.y.ToString("R", Inv) + " " + v.z.ToString("R", Inv));
        private static float PF(string v, float def) => float.TryParse(v, NumberStyles.Float, Inv, out var r) ? r : def;
        private static int PI(string v, int def) => int.TryParse(v, NumberStyles.Integer, Inv, out var r) ? r : def;
        private static bool PB(string v, bool def) => bool.TryParse(v, out var r) ? r : def;
        private static Vector3 PV(string v, Vector3 def)
        {
            var p = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3) return def;
            return new Vector3(PF(p[0], def.x), PF(p[1], def.y), PF(p[2], def.z));
        }
    }
}
