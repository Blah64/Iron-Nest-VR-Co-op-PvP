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

            // Phase 4 co-op: install the host-authoritative sim-gate (Harmony). Safe pre-scene: the patched
            // mission-graph methods only run during a mission, and the gate is inert until a client joins.
            CoopSim.ApplyPatches();

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
