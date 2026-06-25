using HarmonyLib;
using MelonLoader;
using UnityEngine.InputSystem;
using CairnAPI;
using CairnUI = Il2CppTheGameBakers.Cairn.UI;

[assembly: MelonInfo(typeof(CairnTools.Core), "CairnTools", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]
// CairnTools is built entirely on CairnAPI — force it to load first.
[assembly: MelonAdditionalDependencies("CairnAPI")]

namespace CairnTools;

// CairnTools — the dev-facing in-game tools surface. Real uGUI Canvas windows + gameplay
// conveniences. All game access goes through CairnAPI; this mod only does presentation, input,
// and hooks. Item window toggles with the configured hotkey (default F9).
public class Core : MelonMod
{
    private MelonPreferences_Entry<Key> _toggleKey;
    private SpawnWindow _spawn;

    // Gameplay convenience: access backpack contents while climbing (vanilla blocks it).
    internal static MelonPreferences_Entry<bool> UnlockBagWhileClimbing;

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("CairnTools");
        // F7=CairnAccessories, F8=CairnRoutes/CairnArchipelago — F9 is free.
        _toggleKey = cat.CreateEntry("ToggleKey", Key.F9,
            description: "Key that shows/hides the CairnTools item window.");
        UnlockBagWhileClimbing = cat.CreateEntry("UnlockBagWhileClimbing", true,
            description: "Allow accessing backpack contents while climbing (removes the "
                       + "\"not accessible while climbing\" lock).");
        _spawn = new SpawnWindow();
        LoggerInstance.Msg($"CairnTools loaded — press {_toggleKey.Value} for the item window.");
    }

    public override void OnUpdate()
    {
        if (Inputs.KeyDown(_toggleKey.Value))
            _spawn.Toggle();
        _spawn.UpdateDrag();
    }
}

// Backpack-while-climbing unlock. Vanilla BagInventorySection.OnOpening sets Allowed=false and
// shows a "not accessible while climbing" mask; we re-unlock via CairnAPI.Backpack right after,
// once per open (no polling).
[HarmonyPatch(typeof(CairnUI.BagInventorySection), nameof(CairnUI.BagInventorySection.OnOpening))]
internal static class BagOpenPatch
{
    private static void Postfix(CairnUI.BagInventorySection __instance)
    {
        if (Core.UnlockBagWhileClimbing != null && Core.UnlockBagWhileClimbing.Value)
            Backpack.Unlock(__instance);
    }
}
