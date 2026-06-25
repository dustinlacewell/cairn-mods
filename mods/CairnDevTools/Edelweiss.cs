using HarmonyLib;
using Il2Cpp;

namespace CairnDevTools;

/// <summary>
/// Make the edelweiss (resurrection item) non-consumable, so a single climber can revive over and over —
/// the autonomous repro loop needs to drive the death→edelweiss→revive cycle many times, but the game
/// consumes the one resurrection item on the first accept (one revive per save). Rather than synthesize a
/// fake item (the finder returns a REAL item the accept callback then Removes, so a fake risks a downstream
/// crash), we simply stop the consumption: the accept callback
/// (<c>GameDataManager.&lt;&gt;c__DisplayClass250_0.b__0</c>) consumes via
/// <c>InventoryDataBase.Remove(InventoryItem, InventoryItemData)</c> (vtable 0x2c8, decompile-verified in
/// dump/decomp-death/c__DisplayClass250_0_b__0.c:50). We patch that Remove on the concrete inventory types
/// and SKIP it when the item being removed is a <see cref="ResurrectionItem"/> — so the climber keeps its
/// one starting edelweiss across every revive.
///
/// Toggle with the <c>edelweiss</c> console command; default OFF (normal one-revive-per-save behaviour).
/// Mirrors <see cref="Survival"/>'s getter-patch idiom; auto-registered by Core's PatchAll.
/// </summary>
internal static class Edelweiss
{
    internal static bool Infinite;

    /// <summary>Console command: <c>edelweiss</c> toggles, <c>edelweiss on|off</c> sets explicitly.</summary>
    internal static string Command(string[] args)
    {
        if (args.Length > 0)
            Infinite = args[0].Equals("on", System.StringComparison.OrdinalIgnoreCase);
        else
            Infinite = !Infinite;
        return "infinite edelweiss (resurrection item not consumed): " + (Infinite ? "ON" : "OFF — normal one-revive-per-save");
    }

    /// <summary>True if the item being removed is the edelweiss/resurrection item and we should preserve it.</summary>
    private static bool ShouldPreserve(InventoryItem inventoryItem) =>
        Infinite && inventoryItem != null && inventoryItem.TryCast<ResurrectionItem>() != null;

    // The accept callback removes the resurrection item via this 2-arg Remove. Skip it (return false from
    // the prefix) when preserving the edelweiss, so the item is never consumed.
    [HarmonyPatch(typeof(InventoryData), nameof(InventoryData.Remove), typeof(InventoryItem), typeof(InventoryItemData))]
    private static class InventoryDataRemovePatch
    {
        private static bool Prefix(InventoryItem inventoryItem) => !ShouldPreserve(inventoryItem);
    }

    [HarmonyPatch(typeof(BagInventoryData), nameof(BagInventoryData.Remove), typeof(InventoryItem), typeof(InventoryItemData))]
    private static class BagInventoryDataRemovePatch
    {
        private static bool Prefix(InventoryItem inventoryItem) => !ShouldPreserve(inventoryItem);
    }
}
