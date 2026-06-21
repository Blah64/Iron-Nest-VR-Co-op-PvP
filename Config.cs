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

        // VR fallback: how many CONSECUTIVE failing frames (xrWaitFrame returning a runtime error, not a
        // benign not-yet-visible frame) we tolerate before abandoning VR and dropping to flatscreen for the
        // rest of the session. A present-but-broken runtime (headset not actually streaming) makes xrWaitFrame
        // block for seconds then error EVERY frame, freezing the game at <1 fps; this bounds that to a few
        // frames before flatscreen takes over. Each failing frame can cost several seconds (the block is inside
        // the runtime, with no app-side timeout), so keep this small.
        public const int XrFrameFailLimit = 3;

        // Co-op: cap on how many pose packets we transmit per second. A high-fps VR host otherwise floods the
        // wire (~90/s) with near-redundant poses; 30 Hz keeps the remote avatar smooth (receiver snaps to the
        // latest pose each frame). A client running BELOW this still sends every frame, so a slow peer is never
        // throttled further. Set <= 0 to disable the cap (send every frame).
        public static float CoopSendHz = 30f;

        // Co-op: max players per lobby — the Steam lobby member cap AND the count the host-relay transport fans
        // out to. The avatar / control / map-token / ownership layers are all per-peer, so raising this works
        // structurally. CAVEAT: >2 is compile-verified but not yet runtime-validated end-to-end (see PLAN.md §9);
        // lower back to 2 if a >2 session misbehaves. Steam allows up to 250, but the single shared turret makes
        // ~4 the practical ceiling.
        public static int CoopMaxPlayers = 4;

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

        // Phase 4 co-op: host-authoritative SPAWNS (NARROW gate). Both players run their own mission machinery
        // (gun, reload/ammo, objectives, score, teleprinter) locally; the ONLY thing gated on a co-op CLIENT is
        // the enemy/target SPAWN node action (Harmony-suppressed State_SpawnMapEntity.OnEnter) so it never
        // double-spawns with its own RNG — enemies are mirrored from the host via CoopEntities instead. Active
        // ONLY for a client connected to a peer; solo play and the host always spawn normally. The spawn node
        // only runs during a mission, so this never touches the validated hub co-op. See CoopSim.
        public static bool CoopSimAuthority = true;

        // Phase 4 co-op (4b): replicate the host's mission ENTITIES (enemies/targets) to the client. The host
        // diffs FindObjectsByType<EntityLocation>() and broadcasts spawn/move/state/despawn; the client mirrors
        // them (adopts a same-ID scene entity or clones a cached EntityLocation template). Scoped to an active
        // mission (GamePhase.MissionActive) so the hub is untouched. Needs CoopSimAuthority on (gated client).
        public static bool CoopEntitySync = true;

        // REVIEW-fix (P2): how often (seconds) the host re-sends each entity's position on the RELIABLE channel as a
        // keyframe, so a lost unreliable move self-heals within this bound instead of leaving the mirror stale. A
        // discrete state/hp change counts as a keyframe too (already reliable). 0 disables periodic keyframes.
        public static float CoopEntityKeyframeSec = 3f;

        // Phase 4 co-op (4b keystone): replicate the mission/scene transition so both players are co-located.
        // The HOST broadcasts its own phase changes (→MissionActive / back out); the CLIENT follows by driving
        // its own OperationLoadRelay.StartAssignedOperation() / ReturnToMap(). Without this, the host starting a
        // mission leaves the client in the hub and entity sync has nothing to mirror into. See CoopScene.
        public static bool CoopSceneSync = true;

        // Phase 4 co-op (4d): replicate the TELEPRINTER "typing machine" orders. ON even under the narrow gate.
        // Tested 2026-06-20: the client's order text came out EMPTY — the gated spawn node (State_SpawnMapEntity)
        // is what stores the target in a graph context variable, so a client that skips it has no target to
        // resolve {grid}/{bearing} against and prints a blank order. So the resolved text must come from the host:
        // the host captures every Teleprinter.SubmitLines and the client replays it on its matching printer. We do
        // NOT gate the client's own (empty) teleprinter node — gating its OnEnter could stall the graph on
        // State_TeleprinterText.WaitUntilComplete; its empty local submit is harmless next to the replayed text.
        // Scoped to an active mission. See CoopOrders / CoopSim.
        public static bool CoopOrdersSync = true;

        // Phase 4 co-op: replicate the gun-console FIRE-MISSION CARD (PATH B — player-driven). When a player
        // computes a firing solution the printer spawns a FireMissionCard and Apply()s six resolved strings; we
        // capture those and the peer spawns a mirror card from the printer's prefab. Bidirectional + echo-guarded
        // (either player may print). See CoopCards.
        public static bool CoopCardSync = true;

        // Phase 4 co-op: host-authoritative SCORE / outcome. The host replays MarkMissionComplete/Failed onto the
        // client (matching win/lose result), and syncs RequisitionPoints + PowderCharges via OperationState while
        // OUT of a mission (LoadOperationState is heavy → never applied mid-mission). See CoopScore.
        public static bool CoopScoreSync = true;

        // Phase 4 co-op (4c): host-authoritative IMPACT RESULT. The host broadcasts each shell impact that HIT a
        // target (location + hit entities + shell id) and the client replays it to its ImpactIndicators so the
        // tactical map shows the hit — the client's own local adjudication usually misses (target already
        // host-destroyed). Misses aren't replicated (client keeps its own fall-of-shot). See CoopImpact.
        public static bool CoopImpactSync = true;

        // REVIEW-fix (P3): turret CURRENT-state reconcile. Both machines slew their turret locally toward the shared
        // DESIRED aim so they normally converge — but a lost reliable packet or framerate-dependent slew can leave
        // CurrentAngle/CurrentElevation drifting. The HOST periodically broadcasts its current turret/gun state; the
        // client snaps a group to it ONLY when it isn't operating that group itself and the drift exceeds the
        // tolerance, so it never fights the player's hand or jumps during normal play. Active only with a peer.
        public static bool CoopTurretReconcile = true;
        public static float CoopTurretReconcileSec = 2.5f;     // how often the host broadcasts current turret state
        public static float CoopTurretReconcileTolDeg = 1.5f;  // min drift (deg) before the client corrects

        // REVIEW-fix (desync detector): each side periodically exchanges a quantized digest of convergent state
        // (turret aim/elevation/powder + mirrored entity/marker counts) and logs when a field stays divergent for
        // CoopDesyncPersist consecutive digests — surfacing the silent desyncs latency/loss cause. Diagnostic only
        // (it logs; never changes gameplay). Active only with a peer. See CoopNetDiag.
        public static bool CoopDesyncDetect = true;
        public static float CoopDesyncIntervalSec = 1f;
        public static float CoopDesyncAngleTolDeg = 2.5f;
        public static int CoopDesyncPersist = 3;

        // --- Co-op LOCAL TEST transport (same-machine, no second Steam account/PC) ---
        // TEST AID ONLY. When on, the Ctrl+F1/F2/F3 keys can stand up a localhost TCP link between two game
        // instances on ONE machine, bypassing Steam entirely so the whole co-op stack (avatars, control /
        // click / fire sync, clipboard / map / entity / scene) can be exercised without a second Steam account
        // — the recurring 2-player test blocker. Steam P2P stays the real shipping transport; nothing here
        // opens a socket unless the operator presses the key. Set false to keep a shipping build inert.
        //   Ctrl+F1 = host (instance A)   Ctrl+F2 = join 127.0.0.1 (instance B)   Ctrl+F3 = stop
        public static bool CoopLoopback = true;
        public static int CoopLoopbackPort = 56561;

        // Same-machine test: keep each instance in a WINDOW of this size while a loopback link is up. Two
        // standalone instances can't both hold Windows EXCLUSIVE fullscreen on one display, so when the host
        // grabs it on a mission scene-load the background client gets knocked to a tiny resolution. Forcing a
        // plain window sidesteps that. Defaults match the rig in use (two windowed 2560x1080). Set
        // CoopTestForceWindow=false to leave the game's own window handling alone, or change W/H for another size.
        // Active ONLY while connected via the loopback test transport → zero effect on a normal flatscreen player.
        public static bool CoopTestForceWindow = true;
        public static int CoopTestWindowW = 2560;
        public static int CoopTestWindowH = 1080;

        // --- Co-op NETSIM: artificial WAN conditions on the INGRESS path (TEST AID; off by default) ---
        // Loopback (and a same-room LAN) hides the desyncs real distance causes — zero latency/loss. Turn this on
        // to inject latency/jitter/loss/dup/reorder/bandwidth/link-drops at the RECEIVE side, so the sync code and
        // the desync detector get a realistic beating from your desk. Shapes ONE side's ingress (Side) so the link
        // is lopsided like a real one. Loss/dup/reorder hit UNRELIABLE packets only (Steam retransmits reliable
        // ones); reliable packets are delayed/queued, never dropped — except during a simulated link-drop, which
        // blacks out ALL ingress for a window (exercises reconnect/resync). Off => completely inert. See CoopNetSim.
        public static bool CoopNetSim = false;
        public static int CoopNetSimLatencyMs = 120;       // base one-way added delay
        public static int CoopNetSimJitterMs = 40;         // uniform extra 0..this added per packet
        public static float CoopNetSimLossPct = 3f;        // unreliable drop chance (%)
        public static float CoopNetSimDupPct = 1f;         // unreliable duplicate chance (%)
        public static float CoopNetSimReorderPct = 3f;     // unreliable extra-delay (reorder) chance (%)
        public static int CoopNetSimBandwidthBps = 0;      // 0 = unlimited; e.g. 32000 ≈ 256 kbit/s (bufferbloat under load)
        public static int CoopNetSimSeed = 12345;          // deterministic RNG so a failing run reproduces
        public static int CoopNetSimSide = 2;              // whose ingress to shape: 0=both, 1=host, 2=client (default)
        public static float CoopNetSimDropEverySec = 0f;   // 0 = never; else simulate a link drop this often …
        public static float CoopNetSimDropForSec = 3f;     // … lasting this long (ingress blackout → reconnect/resync test)

        // --- Co-op presence niceties (avatar polish) ---
        // Pop a short, non-focus-pulling toast when a peer joins/leaves the lobby (so it's obvious someone
        // arrived). Shown both on flatscreen (IMGUI box) and in VR (world-space card in front of the head).
        public static bool CoopJoinNotify = true;
        public static float NotifySeconds = 2.5f;     // how long the toast stays up
        public static float NotifyDistance = 1.1f;    // VR: metres in front of the head
        public static float NotifyHeight = 0.28f;     // VR: metres above eye level
        public static float NotifyScale = 1f;         // VR: toast size multiplier

        // Float the peer's Steam persona name above their avatar's head so you know who's who. Renders in
        // both VR and flatscreen (layer-0 3D text), billboarded toward the viewer.
        public static bool CoopNameTags = true;
        public static float NameTagHeight = 0.34f;    // metres above the remote head

        // Stream local finger-curl amounts (index from the trigger, other fingers from the grip) so the remote
        // avatar's hands visibly close around dials/triggers instead of staying open. Uses the same curl rig
        // and Config.FingerCurl* tunables as the local hands.
        public static bool CoopFingerCurlSync = true;

        // ---- Co-op avatar "uniform" props (gas mask + Castile flag) ----
        // Render-only cosmetics hung on the REMOTE teammate's avatar, sourced from the GAME'S OWN loaded assets
        // (gas-mask mesh / Castile flag). They add NOTHING to the wire — both are driven from the already-synced
        // head pose. The gas mask tracks the FULL look direction (yaw + pitch); the Castile flag tracks YAW ONLY
        // and hangs upright like a cape off the back shoulders. Identical for a VR teammate (VR head pose) and a
        // flatscreen teammate (window camera) — the sender streams whichever camera applies. Tune live with F6
        // (the solo self-test avatar) + this .cfg; offsets are in METRES, in head/torso-local space.
        public static bool CoopGasMask = true;
        public static float CoopMaskScale = 1f;
        public static Vector3 CoopMaskOffset = new Vector3(0f, -0.03f, 0.06f);   // over the face
        public static Vector3 CoopMaskEuler = Vector3.zero;

        public static bool CoopFlag = true;
        public static float CoopFlagScale = 1f;
        // Cape collar (top-centre of the mesh) sits on the body axis at shoulder height; the mesh itself wraps the
        // back + drapes down, so this just lifts the collar to the shoulders (z≈0 keeps it centred on the torso).
        public static Vector3 CoopFlagOffset = new Vector3(0f, 0.27f, -0.02f);
        public static Vector3 CoopFlagEuler = Vector3.zero;

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

        // Persisted grab-dragged placement for the two head-locked HUD props (clipboard + watch). Each resting
        // pose is captured in BOTH frames the follower can use — head-relative (the "rotate with view" mode) and
        // seat/rig-origin-relative (the fixed-orientation default mode) — so wherever you drop them is restored
        // next session in either mode. "Saved" gates restore vs. the game's authored default placement; written
        // on grab-release. Rotations are euler degrees (same convention as the hand offsets). See GrabManager.
        public static bool ClipPlacementSaved = false;
        public static Vector3 ClipHeadOffPos, ClipHeadOffEul, ClipOriginOffPos, ClipOriginOffEul;
        public static bool WatchPlacementSaved = false;
        public static Vector3 WatchHeadOffPos, WatchHeadOffEul, WatchOriginOffPos, WatchOriginOffEul;

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
                WB(sb, "ClipPlacementSaved", ClipPlacementSaved);
                WV(sb, "ClipHeadOffPos", ClipHeadOffPos);
                WV(sb, "ClipHeadOffEul", ClipHeadOffEul);
                WV(sb, "ClipOriginOffPos", ClipOriginOffPos);
                WV(sb, "ClipOriginOffEul", ClipOriginOffEul);
                WB(sb, "WatchPlacementSaved", WatchPlacementSaved);
                WV(sb, "WatchHeadOffPos", WatchHeadOffPos);
                WV(sb, "WatchHeadOffEul", WatchHeadOffEul);
                WV(sb, "WatchOriginOffPos", WatchOriginOffPos);
                WV(sb, "WatchOriginOffEul", WatchOriginOffEul);
                WB(sb, "CoopGasMask", CoopGasMask);
                WF(sb, "CoopMaskScale", CoopMaskScale);
                WV(sb, "CoopMaskOffset", CoopMaskOffset);
                WV(sb, "CoopMaskEuler", CoopMaskEuler);
                WB(sb, "CoopFlag", CoopFlag);
                WF(sb, "CoopFlagScale", CoopFlagScale);
                WV(sb, "CoopFlagOffset", CoopFlagOffset);
                WV(sb, "CoopFlagEuler", CoopFlagEuler);
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
                case "ClipPlacementSaved": ClipPlacementSaved = PB(v, ClipPlacementSaved); break;
                case "ClipHeadOffPos": ClipHeadOffPos = PV(v, ClipHeadOffPos); break;
                case "ClipHeadOffEul": ClipHeadOffEul = PV(v, ClipHeadOffEul); break;
                case "ClipOriginOffPos": ClipOriginOffPos = PV(v, ClipOriginOffPos); break;
                case "ClipOriginOffEul": ClipOriginOffEul = PV(v, ClipOriginOffEul); break;
                case "WatchPlacementSaved": WatchPlacementSaved = PB(v, WatchPlacementSaved); break;
                case "WatchHeadOffPos": WatchHeadOffPos = PV(v, WatchHeadOffPos); break;
                case "WatchHeadOffEul": WatchHeadOffEul = PV(v, WatchHeadOffEul); break;
                case "WatchOriginOffPos": WatchOriginOffPos = PV(v, WatchOriginOffPos); break;
                case "WatchOriginOffEul": WatchOriginOffEul = PV(v, WatchOriginOffEul); break;
                case "CoopGasMask": CoopGasMask = PB(v, CoopGasMask); break;
                case "CoopMaskScale": CoopMaskScale = PF(v, CoopMaskScale); break;
                case "CoopMaskOffset": CoopMaskOffset = PV(v, CoopMaskOffset); break;
                case "CoopMaskEuler": CoopMaskEuler = PV(v, CoopMaskEuler); break;
                case "CoopFlag": CoopFlag = PB(v, CoopFlag); break;
                case "CoopFlagScale": CoopFlagScale = PF(v, CoopFlagScale); break;
                case "CoopFlagOffset": CoopFlagOffset = PV(v, CoopFlagOffset); break;
                case "CoopFlagEuler": CoopFlagEuler = PV(v, CoopFlagEuler); break;
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
