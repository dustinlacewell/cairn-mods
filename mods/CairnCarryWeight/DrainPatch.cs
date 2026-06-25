using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace CairnCarryWeight;

// The drain hook. ClimbingV2PawnLimb.CurrentEffortCostMultiplier is read per-limb,
// per-frame and folded into how fast that limb burns stamina (vanilla: a pure
// function of the limb's stamina ratio, ramping up as stamina runs low). We postfix
// it to additionally scale by the climber's total carried weight, so a heavy bag
// makes every hold cost more — exactly as if the load were extra effort.
//
// pawnController is the limb's own ClimbingV2PawnController, so CurrentInventoryWeight
// is this climber's bag. Co-op safe: each pawn taxes only its own load.
[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.CurrentEffortCostMultiplier),
    MethodType.Getter)]
internal static class EffortCostMultiplierPatch
{
    private static void Postfix(ClimbingV2PawnLimb __instance, ref float __result)
    {
        if (!Core.Enabled.Value)
            return;

        var controller = __instance.pawnController;
        if (controller == null)
            return;

        float weight = controller.CurrentInventoryWeight;
        if (weight <= 0f)
            return;

        // Linear: empty bag -> 1x, full bag (maxInventoryWeight) -> FullBagEffortMultiplier.
        float fullness = weight / Core.MaxInventoryWeight();
        float factor = 1f + (Core.FullBagEffortMultiplier.Value - 1f) * fullness;
        if (factor < 1f)
            factor = 1f;

        __result *= factor;
    }
}
