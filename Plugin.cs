using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// BepInEx IL2CPP entry point. Keeps almost no logic itself: it registers the
    /// <see cref="VrManager"/> MonoBehaviour with the IL2CPP domain and attaches it to a
    /// persistent GameObject. All VR / OpenXR work lives in VrManager so the loader shim stays thin
    /// (and portable to MelonLoader if we ever need the fallback).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "com.ironnest.vr";
        public const string Name = "IronNestVR";
        public const string Version = "0.1.0";

        public static ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            Log.LogInfo($"{Name} {Version} loading — Unity {Application.unityVersion}");

            // Restore persisted VR settings (menu tunables + hand calibration) before the rig comes up.
            IronNestVR.Config.Load();

            // Verify the shared packet codec round-trips in the REAL Il2Cpp runtime (the standalone golden harness
            // only exercises managed byte[]; SelfTest exercises an actual Il2CppStructArray<byte>). One-time startup
            // check; logs PASS/FAIL so a tester log self-proves the serializer on this build.
            { bool wok = CoopWire.SelfTest(out string werr); Log.LogInfo("[wire] CoopWire self-test: " + (wok ? "PASS" : ("FAIL: " + werr))); }

            // Startup banner for the same-machine co-op test link, so a tester's log self-proves THIS build has
            // the loopback transport and whether it's enabled (otherwise nothing prints until a key is pressed).
            Log.LogInfo($"[loop] same-machine co-op test transport: {(IronNestVR.Config.CoopLoopback ? "ENABLED" : "DISABLED")} " +
                        $"— Ctrl+F2 connect (press in BOTH windows) · Ctrl+F3 stop · port {IronNestVR.Config.CoopLoopbackPort}");

            // Same-machine co-op testing means only ONE window can be focused at a time; the unfocused
            // instance must keep its player loop (and CoopP2P.Tick) running or its half of the sync dies. Unity
            // pauses an unfocused standalone window when runInBackground is false (the game's default), so force
            // it ON from launch while the test transport is enabled. (A shipping/parity build sets
            // Config.CoopLoopback=false to restore unmodded alt-tab pausing for no-headset players.)
            if (IronNestVR.Config.CoopLoopback)
            {
                try { Application.runInBackground = true; Log.LogInfo("[loop] runInBackground forced ON at startup (unfocused instance keeps ticking)"); }
                catch { }
            }

            // Phase 4 co-op: install the host-authoritative sim-gate (Harmony). Safe pre-scene: the patched
            // mission-graph methods only run during a mission, and the gate is inert until a client joins.
            CoopSim.ApplyPatches();
            CoopMap.Init();   // Harmony hooks for fire-mission map pieces (MapPiece3D drag) + bearing/range lines (MapMarkerLineUI finalize)

            // Make our MonoBehaviour type callable from the IL2CPP runtime.
            ClassInjector.RegisterTypeInIl2Cpp<VrManager>();

            var go = new GameObject("IronNestVR");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<VrManager>();

            Log.LogInfo("VrManager registered and attached to persistent GameObject.");
        }
    }
}
