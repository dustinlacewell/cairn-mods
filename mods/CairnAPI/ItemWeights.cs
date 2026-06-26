using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;

namespace CairnAPI;

// Per-item weight overrides. Mods register a multiplier per InventoryItemStringIdEnum
// int value; CairnAPI postfixes every concrete GetWeight() implementation and applies
// the multiplier to __result. Scaling the output (rather than overriding per-unit
// weight) preserves stack-count and ContainerItem recursive totals automatically.
//
// The override flows through the game's own weight everywhere: bag-capacity checks,
// the in-game UI weight readout, CurrentInventoryWeight (and therefore any effort
// scale that reads it). A true override — not just a drain adjustment.
//
// CharmItem (vanilla weight 0) is included so mods CAN assign charms a weight.
// NoneInventoryItem throws on GetWeight(null) and is intentionally not patched.
public static class ItemWeights
{
    // itemId (InventoryItemStringIdEnum int value) -> multiplier on the vanilla weight.
    internal static readonly Dictionary<int, float> _overrides = new();

    // Set a weight multiplier for an item. 1f = vanilla, 2f = twice as heavy, 0f = weightless.
    // Idempotent by id — calling again replaces the previous value.
    public static void Set(InventoryItemStringIdEnum id, float multiplier)
        => _overrides[(int)id] = multiplier;

    // Remove the override for an item (revert to vanilla weight).
    public static void Clear(InventoryItemStringIdEnum id)
        => _overrides.Remove((int)id);

    // Remove all registered overrides.
    public static void ClearAll()
        => _overrides.Clear();

    // True if any overrides are registered.
    public static bool HasOverrides => _overrides.Count > 0;

    // Check if an item has an override registered.
    public static bool Has(InventoryItemStringIdEnum id)
        => _overrides.ContainsKey((int)id);

    // Retrieve the registered multiplier for an item, or 1f if none is set.
    public static float Get(InventoryItemStringIdEnum id)
        => _overrides.TryGetValue((int)id, out float m) ? m : 1f;

    // Apply the registered override to a weight result. Called by every GetWeight patch.
    internal static void Apply(InventoryItem item, ref float result)
    {
        if (_overrides.Count == 0 || item == null) return;
        if (_overrides.TryGetValue(item.id.Value, out float mult))
            result *= mult;
    }

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.PatchAll(typeof(ClimbingGearItemWeightPatch));
        harmony.PatchAll(typeof(ConsumableItemWeightPatch));
        harmony.PatchAll(typeof(ContainerItemWeightPatch));
        harmony.PatchAll(typeof(CustomItemWeightPatch));
        harmony.PatchAll(typeof(GarbageItemWeightPatch));
        harmony.PatchAll(typeof(ReadableItemWeightPatch));
        harmony.PatchAll(typeof(CharmItemWeightPatch));
    }
}

[HarmonyPatch(typeof(ClimbingGearItem), nameof(ClimbingGearItem.GetWeight))]
internal static class ClimbingGearItemWeightPatch
{
    private static void Postfix(ClimbingGearItem __instance, ref float __result)
        => ItemWeights.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(ConsumableItem), nameof(ConsumableItem.GetWeight))]
internal static class ConsumableItemWeightPatch
{
    private static void Postfix(ConsumableItem __instance, ref float __result)
        => ItemWeights.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(ContainerItem), nameof(ContainerItem.GetWeight))]
internal static class ContainerItemWeightPatch
{
    private static void Postfix(ContainerItem __instance, ref float __result)
        => ItemWeights.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(CustomItem), nameof(CustomItem.GetWeight))]
internal static class CustomItemWeightPatch
{
    private static void Postfix(CustomItem __instance, ref float __result)
        => ItemWeights.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(GarbageItem), nameof(GarbageItem.GetWeight))]
internal static class GarbageItemWeightPatch
{
    private static void Postfix(GarbageItem __instance, ref float __result)
        => ItemWeights.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(ReadableItem), nameof(ReadableItem.GetWeight))]
internal static class ReadableItemWeightPatch
{
    private static void Postfix(ReadableItem __instance, ref float __result)
        => ItemWeights.Apply(__instance, ref __result);
}

[HarmonyPatch(typeof(CharmItem), nameof(CharmItem.GetWeight))]
internal static class CharmItemWeightPatch
{
    private static void Postfix(CharmItem __instance, ref float __result)
        => ItemWeights.Apply(__instance, ref __result);
}
