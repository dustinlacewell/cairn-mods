using System;
using Il2Cpp;
using TheGameBakers = Il2CppTheGameBakers.Cairn;

namespace CairnAPI;

// Result of a spawn attempt — explicit so callers can surface "couldn't add" rather than
// failing silently (some storages, e.g. a full Piton slot, legitimately reject).
/// <summary>Return value from Inventory.Add.</summary>
public readonly struct AddResult
{
    /// <summary>True if all requested items were added.</summary>
    public readonly bool Ok;
    /// <summary>How many were actually added.</summary>
    public readonly int Added;       // parts actually added
    /// <summary>Failure reason when Ok is false.</summary>
    public readonly string Error;    // null when Ok

    private AddResult(bool ok, int added, string error) { Ok = ok; Added = added; Error = error; }

    public static AddResult Success(int added) => new(true, added, null);
    public static AddResult Fail(string error) => new(false, 0, error);
}

// Write-side access to the local player's inventory. Uses the game's own routing:
// InventoryManager.GetInventory(item.storedIn).Add(...), guarded by CanAdd.
/// <summary>
/// Add items to and query the player's inventory.
/// <code class="lang-csharp">
/// var result = Inventory.Add(InventoryItemStringIdEnum.Food_Nuts, 3);
/// if (!result.Ok) LogWarning(result.Error);
/// else Log($"Added {result.Added}");
///
/// int nuts  = Inventory.Count(InventoryItemStringIdEnum.Food_Nuts);
/// float bag = Inventory.StorageWeight(StorageType.Bag);
/// </code>
/// </summary>
public static class Inventory
{
    private static TheGameBakers.InventoryManager Manager => TheGameBakers.InventoryManager.Instance;

    public static bool IsReady => Manager != null;

    // Add `count` of an item to whichever storage it belongs in (item.storedIn).
    /// <summary>Add items to the appropriate storage slot. Stops cleanly at capacity.</summary>
    /// <param name="id">Item to add.</param>
    /// <param name="count">How many to add.</param>
    /// <returns>AddResult</returns>
    public static AddResult Add(InventoryItemStringIdEnum id, int count = 1)
    {
        if (count <= 0) return AddResult.Fail("count must be > 0");
        var mgr = Manager;
        if (mgr == null) return AddResult.Fail("InventoryManager not ready (not in a session?)");

        InventoryItem item;
        try { item = mgr.GetItem(new InventoryItemStringId((int)id)); }
        catch (Exception e) { return AddResult.Fail("GetItem threw: " + e.Message); }
        if (item == null) return AddResult.Fail("unknown item " + id);

        StorageType storedIn;
        try { storedIn = item.storedIn; } catch (Exception e) { return AddResult.Fail("storedIn threw: " + e.Message); }

        InventoryDataBase inv;
        try { inv = mgr.GetInventory(storedIn); } catch (Exception e) { return AddResult.Fail("GetInventory threw: " + e.Message); }
        if (inv == null) return AddResult.Fail("no inventory for storage " + storedIn);

        // The multi-arg Add(item, N, ...) treats N as "parts", which for a MaxCount-1 item
        // collapses into a single item (verified live: Add(item,5) -> count delta 1). To add N
        // distinct items we add one at a time, re-checking CanAdd so we stop cleanly when the
        // storage fills (partial success is reported, not an error).
        try
        {
            int added = 0;
            for (int i = 0; i < count; i++)
            {
                int canAddCount;
                if (!inv.CanAdd(item, 1, BagSearchMode.BottomUp, out canAddCount) || canAddCount <= 0)
                    break;
                InventoryItemData data;
                inv.Add(item, 1, out data);
                added++;
            }
            if (added == 0)
                return AddResult.Fail($"{storedIn} can't accept {id} (full / not allowed)");
            return AddResult.Success(added);
        }
        catch (Exception e) { return AddResult.Fail("Add threw: " + e.Message); }
    }

    // How many of an item the player currently holds (across its storage).
    /// <summary>How many of an item the player currently holds.</summary>
    /// <param name="id">Item to count.</param>
    /// <returns>int</returns>
    public static int Count(InventoryItemStringIdEnum id)
    {
        var mgr = Manager;
        if (mgr == null) return 0;
        try { return mgr.GetItemCount(new InventoryItemStringId((int)id), null); }
        catch { return 0; }
    }

    // Total weight currently in a given storage (game's own sum).
    /// <summary>Current total weight in a storage slot.</summary>
    /// <param name="storage">Which slot to query.</param>
    /// <returns>float</returns>
    public static float StorageWeight(StorageType storage)
    {
        var mgr = Manager;
        if (mgr == null) return 0f;
        try { return mgr.GetInventoryWeight(storage); }
        catch { return 0f; }
    }

    // Live bag-weight cap (Addressable-loaded InventoryTweakables.maxInventoryWeight; ctor
    // default 20 but the shipped asset overrides it — observed 35). 0 if not ready.
    /// <summary>The bag's weight capacity.</summary>
    /// <returns>float</returns>
    public static float MaxBagWeight()
    {
        try
        {
            var t = Il2Cpp.InventoryTweakables.Instance;
            return t != null ? t.maxInventoryWeight : 0f;
        }
        catch { return 0f; }
    }
}
