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
        CairnMenu.Install(harmony);      // injects rail GOs on SettingsMenu.OnOpened
        SettingsPages.Install(harmony);  // wires page visuals — must run after the GOs exist
        CrossMenu.Install(harmony);      // LT+chord radial menus; pumped each frame in OnUpdate
        LoggerInstance.Msg("CairnAPI loaded.");
    }

    public override void OnUpdate()
    {
        CrossMenu.Tick();                // drives the chord-menu state machine every frame
    }
}
