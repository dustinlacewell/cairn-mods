using System;
using System.Collections;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.IO;
using MelonLoader;
using UnityEngine;

namespace CairnAPI;

// Climber teleport, routed through the game's own warp machinery so the IK-rigged pawn arrives
// cleanly (streaming-aware): FreeRoamManager.WarpToPoint runs the async warp routine.
//
// We keep one reusable FreeRoamWarpPoint on an INACTIVE GameObject — Awake never runs, so it never
// registers itself into the native free-roam warp list; its TeleportDestination then falls back to
// the GameObject's own transform position, which we set per call. Verified live (lifted from
// CairnRoutes, where it shipped working).
public static class Teleport
{
    private static GameObject _warpGo;
    private static FreeRoamWarpPoint _warpPoint;

    /// <summary>A warp is in flight; callers should gate repeat requests on this.</summary>
    public static bool Busy { get; private set; }

    /// <summary>
    /// Warp the local climber to a world position. <paramref name="done"/> fires with the warp's
    /// success flag (false immediately if there is no FreeRoamManager yet or a warp is in flight).
    /// </summary>
    public static void To(Vector3 position, Action<bool> done)
    {
        var manager = MoSingleton<FreeRoamManager>.Instance;
        if (manager == null || Busy) { done?.Invoke(false); return; }

        EnsureWarpPoint();
        _warpGo.transform.position = position;

        Busy = true;
        var callback = DelegateSupport.ConvertDelegate<Disk.IOOperationCallback>(
            new Action<bool>(ok => { Busy = false; done?.Invoke(ok); }));
        manager.WarpToPoint(_warpPoint, callback);
    }

    /// <summary>
    /// Teleport to a position in a (possibly UNLOADED, possibly cross-world) zone. The game's warp does NOT
    /// stream a far sector (the position-driven streamer only resolves to already-loaded zones — see
    /// re/systems/world-streaming/freeroam-warp.md), so we drive the engine's own teleport recipe: switch
    /// world if needed → force-load the owning zone (GateTriggered) → wait for the scene manager to settle →
    /// THEN warp the pawn onto the now-loaded ground. <paramref name="done"/> fires with success.
    /// </summary>
    public static void ToZone(ZoneSceneData zone, Vector3 position, Action<bool> done)
    {
        var sm = MoSingleton<StreamingManager>.Instance;
        var scene = MoSingleton<CairnSceneManager>.Instance;
        if (sm == null || scene == null || zone == null || Busy) { done?.Invoke(false); return; }

        Busy = true;
        MelonCoroutines.Start(LoadThenWarp(sm, scene, zone, position, done));
    }

    private static IEnumerator LoadThenWarp(
        StreamingManager sm, CairnSceneManager scene, ZoneSceneData zone, Vector3 position, Action<bool> done)
    {
        // Cross-world: re-seed the active world before its zones can be addressed.
        var owningWorld = World.WorldOf(zone);
        if (owningWorld != null && (sm.World == null || sm.World.Pointer != owningWorld.Pointer))
        {
            sm.SetWorldZoneData(owningWorld);
            yield return null;
        }

        // Force-load the owning zone if it isn't already current. GateTriggered = the engine-consistent
        // path (also sets currentZone + weather), mirroring StreamingManager.OnPreTeleportedTo.
        var current = sm.CurrentZone;
        if (current == null || current.Pointer != zone.Pointer)
        {
            // Wait out any in-flight load first, then command ours.
            while (scene.IsBusy()) yield return null;
            sm.GateTriggered(zone);
        }

        // Wait for the streamed load to settle.
        int guard = 0;
        while (scene.IsBusy() && guard++ < 1800) yield return null; // ~30s cap at 60fps

        // The sector is loaded (its limiters now registered) → the normal warp lands on real ground.
        Busy = false;
        To(position, done);
    }

    private static void EnsureWarpPoint()
    {
        if (_warpGo != null) return;
        _warpGo = new GameObject("CairnAPI.Warp");
        UnityEngine.Object.DontDestroyOnLoad(_warpGo);
        _warpGo.SetActive(false); // keep Awake from running → no native-list registration
        _warpPoint = _warpGo.AddComponent<FreeRoamWarpPoint>();
        SetWarpMode(_warpPoint, (int)PawnControllerSwitcher.Mode.Walking);
    }

    /// <summary>warpMode is a private serialized field; a fresh component holds Mode.None.</summary>
    private static unsafe void SetWarpMode(FreeRoamWarpPoint wp, int mode)
    {
        IntPtr cls = Il2CppClassPointerStore<FreeRoamWarpPoint>.NativeClassPtr;
        IntPtr obj = IL2CPP.Il2CppObjectBaseToPtrNotNull(wp);
        IntPtr field = IL2CPP.GetIl2CppField(cls, "warpMode");
        *(int*)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(field)) = mode;
    }
}
