using HarmonyLib;
using Il2Cpp;

namespace CairnCarryWeight;

// True per-item weight override. Each concrete InventoryItem subclass implements
// GetWeight (vanilla: count * perUnitWeight, ContainerItem also adds contents). We
// postfix every implementation and scale __result by the item's configured
// multiplier — so the override flows through the game's own weight everywhere it's
// used: bag-capacity checks, the UI weight readout, and (via CurrentInventoryWeight)
// this mod's stamina drain. Scaling the result preserves stack-count and container
// recursion without us re-deriving per-unit weight.
//
// CharmItem (vanilla weight 0) is included so a charm CAN be given weight via config.
// NoneInventoryItem.GetWeight throws (it's the null-item sentinel) and is not patched.
internal static class WeightOverride
{
    internal static void Apply(InventoryItem item, ref float result)
    {
        if (!Core.Enabled.Value || !Core.ApplyWeightOverrides.Value)
            return;
        if (item == null)
            return;
        if (Core.WeightOverrides.TryGetValue(item.id.Value, out float mult))
            result *= mult;
    }
}

[HarmonyPatch(typeof(ClimbingGearItem), nameof(ClimbingGearItem.GetWeight))]
internal static class ClimbingGearItemWeightPatch
{
    private static void Postfix(ClimbingGearItem __instance, ref float __result)
        => WeightOverride.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(ConsumableItem), nameof(ConsumableItem.GetWeight))]
internal static class ConsumableItemWeightPatch
{
    private static void Postfix(ConsumableItem __instance, ref float __result)
        => WeightOverride.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(ContainerItem), nameof(ContainerItem.GetWeight))]
internal static class ContainerItemWeightPatch
{
    private static void Postfix(ContainerItem __instance, ref float __result)
        => WeightOverride.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(CustomItem), nameof(CustomItem.GetWeight))]
internal static class CustomItemWeightPatch
{
    private static void Postfix(CustomItem __instance, ref float __result)
        => WeightOverride.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(GarbageItem), nameof(GarbageItem.GetWeight))]
internal static class GarbageItemWeightPatch
{
    private static void Postfix(GarbageItem __instance, ref float __result)
        => WeightOverride.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(ReadableItem), nameof(ReadableItem.GetWeight))]
internal static class ReadableItemWeightPatch
{
    private static void Postfix(ReadableItem __instance, ref float __result)
        => WeightOverride.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(CharmItem), nameof(CharmItem.GetWeight))]
internal static class CharmItemWeightPatch
{
    private static void Postfix(CharmItem __instance, ref float __result)
        => WeightOverride.Apply(__instance, ref __result);
}
