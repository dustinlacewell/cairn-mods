using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(CairnAPI.Core), "CairnAPI", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnAPI;

// CairnAPI is a pure library mod: verified, reusable wrappers over Cairn's il2cpp internals
// so consumer mods (CairnTools, etc.) call a stable C# surface instead of re-deriving the
// interop plumbing. No UI, no input, no console — that all lives in consumer mods.
//
// First cut (verified live against the running game): item lookup/enumeration (Items) and
// adding/removing items for the local player (Inventory). The "give an item" path is the
// game's own: InventoryManager.GetInventory(item.storedIn).Add(item, count, out data),
// which respects CanAdd (some storages, e.g. Piton, reject).
public class Core : MelonMod
{
    public override void OnInitializeMelon()
    {
        Screen.Install(new HarmonyLib.Harmony("CairnAPI.Screen"));
        LoggerInstance.Msg("CairnAPI loaded.");
    }
}
