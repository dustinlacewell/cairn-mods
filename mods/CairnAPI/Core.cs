using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(CairnAPI.Core), "CairnAPI", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnAPI;

public class Core : MelonMod
{
    public override void OnInitializeMelon()
    {
        var harmony = new HarmonyLib.Harmony("CairnAPI");
        Screen.Install(harmony);
        Stamina.Install(harmony);
        ItemWeights.Install(harmony);
        CairnMenu.Install(harmony);
        ModOptionsHook.Install(harmony);
        LoggerInstance.Msg("CairnAPI loaded.");
    }
}
