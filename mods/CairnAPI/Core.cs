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
        RegisterShowcase();
        LoggerInstance.Msg("CairnAPI loaded.");
    }

    // TEMPORARY: showcase every ModOption widget kind for visual verification.
    // Remove before release.
    private static void RegisterShowcase()
    {
        bool   toggleVal  = false;
        float  sliderVal  = 5f;
        string textVal    = "hello";
        int    arrowsIdx  = 0;
        int    dropIdx    = 1;

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
            ModOption.ButtonDouble("ButtonDouble", "Left", () => MelonLoader.MelonLogger.Msg("[Showcase] Left"),
                                                  "Right",() => MelonLoader.MelonLogger.Msg("[Showcase] Right")),
            ModOption.Action      ("Action Button", () => MelonLoader.MelonLogger.Msg("[Showcase] Action")),
        });
    }
}
