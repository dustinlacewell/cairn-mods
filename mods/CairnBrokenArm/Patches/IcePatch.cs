using HarmonyLib;
using Il2Cpp;

namespace CairnBrokenArm.Patches;

// Layer D (OPTIONAL) — R4 W3: the ice state-machine's OnCancelReachDone writes _currentState=3
// inline on the ice path. It's an explicit-interface impl; Il2CppInterop renames it to
// IIceClimbingLimbStateMachineOwner_OnCancelReachDone. If this name doesn't exist in the
// interop assembly the patch attribute won't resolve and the build fails — in that case this
// whole file is removed and Layer B1 + Layer C catch the resulting state (noted in the report).
[HarmonyPatch(typeof(ClimbingV2PawnLimb), "IIceClimbingLimbStateMachineOwner_OnCancelReachDone")]
internal static class OnCancelReachDonePatch
{
    private static bool Prefix(ClimbingV2PawnLimb __instance)
        => !BrokenArm.IsBrokenLocal(__instance);
}

// Direct-writer guards for the inline ice-reach state-5/6 writers (the ice analog of the
// rock W1/W4 prefixes): IceClimbingGoingToHoldStart writes GoingToHoldIceClimbing(5) and
// IceClimbingRestoreStart writes RestoreIceClimbing(6) inline, bypassing the B1 clamp and
// Layer C's net (both 2/3/4 only). Skip them for the disabled limb; Layer C reverts as the net.
[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.IceClimbingGoingToHoldStart))]
internal static class IceClimbingGoingToHoldStartPatch
{
    private static bool Prefix(ClimbingV2PawnLimb __instance) => !BrokenArm.IsBrokenLocal(__instance);
}

[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.IceClimbingRestoreStart))]
internal static class IceClimbingRestoreStartPatch
{
    private static bool Prefix(ClimbingV2PawnLimb __instance) => !BrokenArm.IsBrokenLocal(__instance);
}
