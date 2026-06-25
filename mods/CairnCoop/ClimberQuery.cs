using System;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// Pure, read-only queries the belay reconciler asks of live game state — "where is my climber", "am I
/// weighting the rope", "is this an unsecured fall", "who are the remotes" — plus the local-controller
/// cache they all funnel through. Every method here only READS the scene/net frame and never drives
/// anything, so the reconciler's OBSERVE phase is exactly these calls. Extracted out of
/// <see cref="PartnerBelay"/> verbatim so the reconciler reads as observe→derive→drive.
/// </summary>
internal static class ClimberQuery
{
    /// <summary>
    /// Whether OUR local climber is currently WEIGHTING the securing rope — caught/hanging, abseiling,
    /// ascending the rope, or rappelling (their body load is on the rope, not the wall). Announced to
    /// the partner so THEY drain while bearing us. Read from the active module's type (a clean public
    /// getter); the RAH hang-idle / abseil / ascend / rappel sub-modes all surface as one of these
    /// types, so we don't need the module's private rahMode field. SecuredFalling covers the
    /// just-caught hang. False when the controller/module can't be resolved (treat as on-wall).
    /// </summary>
    internal static bool LocalClimberWeightingRope(SharedRopeGamemode gamemode, int localId)
    {
        try
        {
            var controller = FindLocalClimbController(gamemode, localId);
            var module = controller?.CurrentModule;
            if (module == null)
                return false;
            switch (module.type)
            {
                case ClimbingV2PawnControllerModule.ClimbingModule.SecuredFalling: // caught / hanging
                case ClimbingV2PawnControllerModule.ClimbingModule.HangIdle:       // dangling on rope
                case ClimbingV2PawnControllerModule.ClimbingModule.Abseil:         // rappelling down
                case ClimbingV2PawnControllerModule.ClimbingModule.RopeAscending:  // climbing the rope
                case ClimbingV2PawnControllerModule.ClimbingModule.Rappel:
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether OUR local climber is in an UNSECURED fall — a real plummet OR the Edelweiss respawn rewind,
    /// which the game drives inside FallingModule (Falling_RespawnToWallBeforeFall, ending in a transition
    /// back to IdleModule on the wall). <c>IsFalling &amp;&amp; !IsSecureFalling</c> isolates the unsecured
    /// FallingModule: <c>IsFalling</c> is true for BOTH FallingModule and SecuredFallingModule, so excluding
    /// <c>IsSecureFalling</c> leaves exactly the unsecured fall. The reconciler must keep the belay OFF a
    /// climber in this state — a caught hang is SecuredFalling (covered by <see cref="LocalClimberWeightingRope"/>)
    /// and is intentionally NOT flagged here. False when the controller can't be resolved (treat as on-wall).
    /// </summary>
    internal static bool LocalClimberUnsecuredFalling(SharedRopeGamemode gamemode, int localId)
    {
        try
        {
            var controller = FindLocalClimbController(gamemode, localId);
            return controller != null && controller.IsFalling && !controller.IsSecureFalling;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>My current climbing-module type name for the diagnostic readout ("Idle", "SecuredFalling",
    /// "Rappel", …) or a short reason it couldn't be read. Never throws.</summary>
    internal static string LocalModuleName(SharedRopeGamemode gamemode, int localId)
    {
        try
        {
            var module = FindLocalClimbController(gamemode, localId)?.CurrentModule;
            return module == null ? "—" : module.type.ToString();
        }
        catch (Exception) { return "?"; }
    }

    internal static bool LocalPawnDropping(ClimbingV2PawnController controller)
    {
        try
        {
            var body = controller.harness.nativeRigidbody;
            return body != null && body.velocity.y < -3f;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The first remote player's id (the partner in the 1-partner case), or -1 if none.</summary>
    internal static int FirstRemoteId(Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> remotes, int localId)
    {
        for (int r = 0; r < remotes.Count; r++)
        {
            var remote = remotes[r];
            if (remote != null && remote.Id != localId)
                return remote.Id;
        }
        return -1;
    }

    /// <summary>Live world position of the local climber's harness (for picking the nearest partner).
    /// Falls back to origin if the controller isn't resolved yet — every remote is then equidistant,
    /// so the first is chosen until the harness exists.</summary>
    internal static Vector3 LocalClimberPos(SharedRopeGamemode gamemode, int localId)
    {
        var controller = FindLocalClimbController(gamemode, localId);
        var harness = controller?.harness;
        return harness != null ? harness.transform.position : Vector3.zero;
    }

    // The local climber's controller is stable for the life of its pawn; it only changes on a respawn,
    // which swaps the harness the gamemode maps to our id. So cache it, keyed on that harness pointer —
    // a cheap base-Gamemode lookup the resolve already did every call. On a hit we return the cached
    // controller after one pointer compare; the expensive FindObjectsOfType scan runs only on a miss
    // (first resolve or post-respawn). This collapses the ~4 full scene scans a belay tick used to do
    // (one per snapshot helper) into one, which was the bulk of the old 1 Hz "anchor" cost.
    private static ClimbingV2PawnController _cachedLocalController;
    private static IntPtr _cachedLocalHarnessPtr;

    /// <summary>The local climber's controller — the one whose harness the gamemode maps to our id.
    /// Cached across ticks; the cache self-invalidates when the mapped harness pointer changes
    /// (respawn) and is dropped explicitly by <see cref="InvalidateLocalControllerCache"/> on session
    /// teardown.</summary>
    internal static ClimbingV2PawnController FindLocalClimbController(SharedRopeGamemode gamemode, int localId)
    {
        var localHarness = gamemode.GetHarness(localId);
        if (localHarness == null)
        {
            InvalidateLocalControllerCache();
            return null;
        }
        IntPtr harnessPtr = localHarness.Pointer;
        if (_cachedLocalController != null && _cachedLocalHarnessPtr == harnessPtr)
            return _cachedLocalController;
        foreach (var controller in UnityEngine.Object.FindObjectsOfType<ClimbingV2PawnController>())
            if (controller.harness != null && controller.harness.Pointer == harnessPtr)
            {
                _cachedLocalController = controller;
                _cachedLocalHarnessPtr = harnessPtr;
                return controller;
            }
        InvalidateLocalControllerCache();
        return null;
    }

    /// <summary>Drop the cached local controller. The harness-pointer key already self-invalidates on
    /// respawn; call this when the gamemode goes away entirely (session end) so we don't pin a stale
    /// pawn wrapper across sessions.</summary>
    internal static void InvalidateLocalControllerCache()
    {
        _cachedLocalController = null;
        _cachedLocalHarnessPtr = IntPtr.Zero;
    }
}
