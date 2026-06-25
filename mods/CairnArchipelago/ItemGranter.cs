using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using MelonLoader;

namespace CairnArchipelago;

// Turns received AP items into game inventory items. AP ids are
// ITEM_BASE + InventoryItemStringIdEnum by construction, so the game item is
// recovered arithmetically and granted through the same InventoryDataBase.Add
// path the game's own loot flow uses.
internal static class ItemGranter
{
    public static bool WorldReady()
    {
        var manager = InventoryManager.Instance;
        return manager != null && manager.InventoriesCreated;
    }

    public static void Grant(long apItemId, string itemName, string senderName)
    {
        var enumValue = (int)(apItemId - Core.ItemBase);
        if (enumValue < 0)
        {
            // Abstract progression items (Progressive Altitude Permit) have no
            // in-game inventory form; chapter gating consumes them instead.
            MelonLogger.Msg($"[item] {itemName} from {senderName} (abstract, id {apItemId})");
            return;
        }

        var manager = InventoryManager.Instance;
        var item = manager.library.GetItem(new InventoryItemStringId(enumValue));
        if (item == null)
        {
            MelonLogger.Warning($"[item] {itemName}: unknown game item value {enumValue}");
            return;
        }

        var inventory = InventoryFor(manager, item.storedIn);
        if (inventory == null)
        {
            MelonLogger.Warning($"[item] {itemName}: no inventory for storage {item.storedIn}");
            return;
        }

        var added = inventory.Add(item, 1, out _);
        MelonLogger.Msg(added > 0
            ? $"[item] {itemName} from {senderName} — added to {item.storedIn}"
            : $"[item] {itemName} from {senderName} — inventory full, dropped (TODO: retry queue)");
    }

    private static InventoryDataBase InventoryFor(InventoryManager manager, StorageType storage)
    {
        switch (storage)
        {
            case StorageType.Bag: return manager.BagInventoryData;
            case StorageType.Puff: return manager.PuffInventoryData;
            case StorageType.Readables: return manager.ReadablesInventoryData;
            case StorageType.Piton: return manager.PitonInventoryData;
            case StorageType.Robot: return manager.ClimbotInventoryData;
            case StorageType.Infinite: return manager.InfiniteInventoryData;
            case StorageType.Charms: return manager.CharmsInventoryData;
            default: return manager.BagInventoryData;
        }
    }
}
