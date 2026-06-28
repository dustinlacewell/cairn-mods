using HarmonyLib;
using Il2Cpp;

namespace CairnBrokenArm.Patches;

// Layer A (reach-start exclusion) + Layer B writer B2.
// Each prefix returns false to SKIP the original for the broken arm.

// A1 — R4 W1: StartReachingWithLimb is the sole manual reach terminal
// (LimbSelectionHud.OnClosing calls it) AND the controller reach-start. Skip it for the
// broken arm so the limb is never targeted to reach.
[HarmonyPatch(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.StartReachingWithLimb))]
internal static class StartReachingWithLimbPatch
{
    private static bool Prefix(ClimbingV2PawnLimb limb)
        => !BrokenArm.IsBrokenLocal(limb);
}

// A2 — R4 W2: set_ReachingLimb writes _currentState=3 inline (re-asserted per-frame by
// EarlyUpdate). This is the FLOOR of auto-select: any auto path that still tries to commit the
// broken arm gets the OPPOSITE hand committed instead — substitute, don't skip (skipping makes
// auto-select give up). Let the original run with the swapped value. Manual reach goes via
// StartReachingWithLimb (A1), NOT this setter, so manual R1 is unaffected.
[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleReaching),
    nameof(ClimbingV2PawnControllerModuleReaching.ReachingLimb), MethodType.Setter)]
internal static class SetReachingLimbPatch
{
    private static void Prefix(ref ClimbingV2PawnLimb value)
    {
        if (BrokenArm.IsBrokenLocal(value))
            value = BrokenArm.OppositeOf(value);
    }
}

// B2 — R4 W4: StartReach is the module-bypass inline writer (sets _currentState=3 directly).
// Skip it for the broken arm.
[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.StartReach))]
internal static class StartReachPatch
{
    private static bool Prefix(ClimbingV2PawnLimb __instance)
        => !BrokenArm.IsBrokenLocal(__instance);
}
