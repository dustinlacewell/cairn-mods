using System;
using System.Collections.Generic;
using UnityEngine;
using Il2Cpp;
using TheGameBakers = Il2CppTheGameBakers.Cairn;

namespace CairnAPI;

// One item the game knows about: its enum id, the live config object, and its per-unit weight.
/// <summary>Per-item metadata.</summary>
public readonly struct ItemInfo
{
    /// <summary>Enum identifier.</summary>
    public readonly InventoryItemStringIdEnum Id;
    public readonly InventoryItem Item;     // the live config (ScriptableObject)

    public ItemInfo(InventoryItemStringIdEnum id, InventoryItem item) { Id = id; Item = item; }

    /// <summary>Display name.</summary>
    public string Name => Id.ToString();
    /// <summary>Which storage slot holds this item.</summary>
    public StorageType StoredIn { get { try { return Item.storedIn; } catch { return StorageType.Invalid; } } }
    /// <summary>Maximum stack count.</summary>
    public int MaxCount { get { try { return Item.MaxCount; } catch { return 0; } } }

    public string TypeName { get { try { return Item.GetIl2CppType().Name; } catch { return "?"; } } }

    // Per-unit base weight. GetWeight(null) returns the single-unit weight for most item types,
    // but GarbageItem and NoneInventoryItem dereference itemData unconditionally and THROW on
    // null (verified in the decomp) — calling those spams the il2cpp trampoline error log even
    // when caught managed-side. So we skip them by type and report NaN (display as "—").
    /// <summary>Weight per unit. NaN for non-physical items.</summary>
    public float UnitWeight
    {
        get
        {
            var tn = TypeName;
            if (tn == "GarbageItem" || tn == "NoneInventoryItem") return float.NaN;
            try { return Item.GetWeight(null); } catch { return float.NaN; }
        }
    }
}

// Read-side access to the item catalog. Backed by InventoryItemsLibrary (the full 315-item
// array) with InventoryManager as the id->config resolver.
/// <summary>
/// Read-only catalog of every item in the game.
/// <code class="lang-csharp">
/// foreach (var info in Items.All)
///     Log($"{info.Name}  {info.StoredIn}  {info.UnitWeight:F2}kg");
///
/// var rope = Items.Get(InventoryItemStringIdEnum.Rope_Standard);
/// </code>
/// </summary>
public static class Items
{
    // Snapshot of every item in the library, in library order. Cached after first build.
    private static List<ItemInfo> _cache;

    /// <summary>Every item in the game (315 entries). Cached after first access.</summary>
    /// <returns>IReadOnlyList&lt;ItemInfo&gt;</returns>
    public static IReadOnlyList<ItemInfo> All
    {
        get
        {
            if (_cache != null) return _cache;
            var list = new List<ItemInfo>();
            var libs = Resources.FindObjectsOfTypeAll<InventoryItemsLibrary>();
            if (libs != null && libs.Length > 0 && libs[0].items != null)
            {
                var items = libs[0].items;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it == null) continue;
                    InventoryItemStringIdEnum id;
                    try { id = (InventoryItemStringIdEnum)it.id.Value; }
                    catch { continue; }
                    list.Add(new ItemInfo(id, it));
                }
                _cache = list; // only cache once the library is actually loaded
            }
            return list;
        }
    }

    // Drop the cache if the library reloads (e.g. across a level/session change).
    public static void Invalidate() => _cache = null;

    public static bool IsReady
    {
        get
        {
            var libs = Resources.FindObjectsOfTypeAll<InventoryItemsLibrary>();
            return libs != null && libs.Length > 0 && libs[0].items != null && libs[0].items.Count > 0;
        }
    }

    // Resolve a single item config by enum id, via the game's own manager.
    /// <summary>Resolve a single item config by enum id.</summary>
    /// <param name="id">The item's enum identifier.</param>
    /// <returns>InventoryItem</returns>
    public static InventoryItem Get(InventoryItemStringIdEnum id)
    {
        var mgr = TheGameBakers.InventoryManager.Instance;
        if (mgr == null) return null;
        try { return mgr.GetItem(new InventoryItemStringId((int)id)); }
        catch { return null; }
    }
}
