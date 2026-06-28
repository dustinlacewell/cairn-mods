using HarmonyLib;
using Il2Cpp;
using LimbState = Il2Cpp.ClimbingV2PawnLimb.LimbState;

namespace CairnBrokenArm.Patches;

// Layer B — attach veto (R3) + the master state backstop (R4 => R3 => R5).

// B3b — R3: TryAndGrabHold is the EARLIEST grab veto, before currentGrab is filled. Skip and
// report failure for the broken arm.
[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.TryAndGrabHold))]
internal static class TryAndGrabHoldPatch
{
    private static bool Prefix(ClimbingV2PawnLimb __instance, ref bool __result)
    {
        if (!BrokenArm.IsBrokenLocal(__instance))
            return true;
        __result = false;
        return false;
    }
}

// B3 — R3: ApplyHoldGrab commits the hold (incl. the initial spawn hold). Skip it for the
// broken arm so no grab is ever committed and no dangling currentGrab is left.
[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.ApplyHoldGrab))]
internal static class ApplyHoldGrabPatch
{
    private static bool Prefix(ClimbingV2PawnLimb __instance, ref bool __result)
    {
        if (!BrokenArm.IsBrokenLocal(__instance))
            return true;
        __result = false;
        return false;
    }
}

// B1 — MASTER backstop. set_CurrentState is the parameterized funnel. The 5 inline state-3
// writers BYPASS it (that's why Layer A exists), but every setter-routed state-2 attach
// arrival (GoingToHoldStateUpdate / ApplyHoldGrab) and any setter-routed reach lands here.
// Clamp Holding(2) / Reaching(3) / GoingToHold(4) to Idle(1) for the broken arm.
[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.CurrentState), MethodType.Setter)]
internal static class SetCurrentStatePatch
{
    private static void Prefix(ClimbingV2PawnLimb __instance, ref LimbState value)
    {
        if (!BrokenArm.IsBrokenLocal(__instance))
            return;
        if (value == LimbState.Holding || value == LimbState.Reaching || value == LimbState.GoingToHold)
            value = LimbState.Idle;
    }
}
