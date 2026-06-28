using MelonLoader;

[assembly: MelonInfo(typeof(CairnModOptions.Core), "CairnModOptions", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnModOptions;

// Surfaces the ModOptions registry as a "Mods" page in the SettingsMenu, one mod per
// dropdown selection. Built on CairnAPI's SettingsPage + Fields primitives — this mod
// owns the registry and the page policy; CairnAPI owns the native UI machinery.
public class Core : MelonMod
{
    public override void OnInitializeMelon()
    {
        ModsPage.Install();
        LoggerInstance.Msg("CairnModOptions loaded.");
    }
}
