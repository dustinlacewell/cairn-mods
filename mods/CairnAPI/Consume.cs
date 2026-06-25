using System;
using Il2Cpp;   // LootProvider, ConsumableItem, InventoryItem are global-namespace -> Il2Cpp

namespace CairnAPI;

// Consuming a world item in place (apply its effect without bagging it). Built on the verified
// inventory-free consume path: ConsumableItem.ApplyEffects(isHot) applies food/water/stamina/heal
// to the player; every consume in the game funnels through it. World items are "cold" (isHot=false;
// hot only applies to cookpot-warmed bag items). See re/systems/interaction/world-consume.md.
public static class Consume
{
    // Resolve a LootProvider's loot at `index` to its ConsumableItem config, or null if it yields
    // no consumable. (Multi-loot providers can hold several entries; callers may iterate.)
    public static ConsumableItem ConsumableAt(LootProvider provider, int index = 0)
    {
        if (provider == null) return null;
        try
        {
            var item = provider.GetLootItem(index);
            return item == null ? null : item.TryCast<ConsumableItem>();
        }
        catch { return null; }
    }

    // Is this consumable currently worth using (not full hunger/thirst, etc.)? Gates the prompt.
    public static bool CanUse(ConsumableItem item)
    {
        if (item == null) return false;
        try { return item.CanUse(false); } catch { return false; }
    }

    // Apply the consumable's effect to the player (cold). No inventory involvement.
    public static bool Apply(ConsumableItem item)
    {
        if (item == null) return false;
        try { item.ApplyEffects(false); return true; }
        catch { return false; }
    }

    // Remove a world loot item after consuming it. Destroy(gameObject) is the game's own depletion
    // call, so it vanishes for the session. NOTE: the save-aware "mark emptied" path
    // (LootProvider.stocksEmpty / RefreshRemainingLoot) is PRIVATE in the game, so we can't mark it
    // consumed in the loot registry — whether a consumed item respawns on reload is untested.
    public static void DespawnLoot(LootProvider provider)
    {
        if (provider == null) return;
        try { UnityEngine.Object.Destroy(provider.gameObject); } catch { }
    }
}
