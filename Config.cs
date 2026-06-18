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

        // How often (seconds) to retry OpenXR init while no headset/runtime is available yet.
        public const float XrRetryIntervalSec = 5f;

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
        // Draw the laser/pointer line.
        public static bool ShowLaser = true;
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

        // --- HUD follow (held clipboard etc. lag-follow the VR head, not the flat camera) ---
        public static bool HudFollowEnabled = true;
        // Exponential lag time-constant (s): higher = lazier/smoother follow.
        public static float HudFollowPosLag = 0.18f;
        public static float HudFollowRotLag = 0.22f;
        // Push the HUD forward/up from the head so the clipboard is readable & reachable (metres).
        public static float HudPushForward = 0.28f;
        public static float HudPushUp = 0f;

        // --- Clipboard grab-to-place ---
        public static bool ClipboardGrabEnabled = true;
        // Head-locked props (HUD clipboard + watch) rotate WITH the VR camera when true; when false
        // they keep a fixed orientation and only follow your position (you turn to look at them).
        public static bool HudRotateWithCamera = true;
        // Hand must be within this distance (m) of the clipboard to grab it with the grip button.
        public static float GrabRadius = 0.4f;
        // Scale the clipboard up for VR: it was authored for the flat ~60° FOV; the VR view is ~94°,
        // so the same object looks ~half size. 1.8 roughly cancels that. 1 = no change.
        public static float ClipboardScale = 1.8f;

        // --- VR settings menu (click BOTH thumbsticks at once to open/close) ---
        public static bool MenuEnabled = true;
        // Where the panel appears, relative to the head when opened (it then stays put in the world).
        public static float MenuDistance = 0.8f;       // metres in front
        public static float MenuHeightOffset = -0.05f; // metres below eye level
        // Overall size multiplier for the whole panel + text (one knob to tune readability).
        public static float MenuScale = 1f;
        // Rotate the panel 180° if it ends up facing away from you (handedness sanity flip).
        public static bool MenuFlip = false;

        // --- Interaction diagnostics ---
        // Throttled per-frame log of head/controller geometry + hover target (for tuning aim).
        public static bool LogInteractGeometry = true;
        public static float InteractLogIntervalSec = 2f;
    }
}
