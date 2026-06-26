using CairnAPI;
using Il2Cpp;

namespace CairnCarryWeight;

// Scales per-limb stamina drain speed based on how full the bag is.
//
// Uses CairnAPI.Stamina.DrainScale — a postfix on UpdateStaminaState that multiplies
// _CurrentStaminaConsumptionSpeed after the game computes it. Only applied when a limb
// is actually draining (speed > 0), so resting/neutral limbs are unaffected.
//
// Scale formula: factor = 1 + (FullBagEffortMultiplier - 1) × (currentWeight / maxBagWeight)
//   Empty bag = 1x (vanilla drain).  Full bag = FullBagEffortMultiplier.
//
// Each limb reads its own pawn's inventory weight, so co-op players drain by their OWN bag.
internal static class DrainPatch
{
    internal static void Register()
        => Stamina.AddDrainScaleProvider("CairnCarryWeight", ComputeScale);

    private static float ComputeScale(ClimbingV2PawnLimb limb)
    {
        if (!Core.Enabled.Value)
            return 1f;

        var controller = limb?.pawnController;
        if (controller == null)
            return 1f;

        float weight = controller.CurrentInventoryWeight;
        if (weight <= 0f)
            return 1f;

        float maxWeight = Inventory.MaxBagWeight();
        if (maxWeight <= 0f)
            return 1f;

        float fullness = weight / maxWeight;
        float factor = 1f + (Core.FullBagEffortMultiplier.Value - 1f) * fullness;
        return factor < 1f ? 1f : factor;
    }
}
