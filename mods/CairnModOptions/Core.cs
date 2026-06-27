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
        RegisterShowcase();
        LoggerInstance.Msg("CairnModOptions loaded.");
    }

    // TEMPORARY: showcase every ModOption widget kind for visual verification.
    // Remove before release.
    private static void RegisterShowcase()
    {
        bool   toggleVal = false;
        float  sliderVal = 5f;
        string textVal   = "hello";
        int    arrowsIdx = 0;
        int    dropIdx   = 1;

        ModOptions.Register("Widget Showcase", new[]
        {
            ModOption.Info        ("── Read-only label (Info) ──"),
            ModOption.Toggle      ("Toggle",     () => toggleVal,  v => toggleVal = v),
            ModOption.Slider      ("Slider",     0f, 10f,          () => sliderVal, v => sliderVal = v),
            ModOption.TextField   ("TextField",  () => textVal,    v => textVal   = v),
            ModOption.ListArrows  ("ListArrows", new[]{"Alpha","Beta","Gamma","Delta"},
                                                 () => arrowsIdx,  v => arrowsIdx = v),
            ModOption.Dropdown    ("Dropdown",   new[]{"Option A","Option B","Option C"},
                                                 () => dropIdx,    v => dropIdx   = v),
            ModOption.ButtonDouble("ButtonDouble", "Left", () => MelonLogger.Msg("[Showcase] Left"),
                                                  "Right",() => MelonLogger.Msg("[Showcase] Right")),
            ModOption.Action      ("Action Button", () => MelonLogger.Msg("[Showcase] Action")),
        });
    }
}
