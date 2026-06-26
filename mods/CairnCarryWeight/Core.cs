using System.Globalization;
using MelonLoader;
using CairnAPI;

[assembly: MelonInfo(typeof(CairnCarryWeight.Core), "CairnCarryWeight", "1.0.0", "dustin")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnCarryWeight;

// Carry-weight fatigue: heavier bag => climbing tires you faster.
//
// Cairn ships a full per-item weight model (InventoryItem.GetWeight, summed into
// ClimbingV2PawnController.CurrentInventoryWeight) but never feeds it into stamina
// drain — it only gates bag capacity. This mod wires that summed weight into the
// drain by scaling the per-limb CurrentEffortCostMultiplier (the same knob the game
// uses to make holds cost more), so weight literally becomes climbing effort.
//
// Weight is per-pawn, so each climber drains by their OWN bag — co-op safe.
//
// Item weights can be overridden (a true override: capacity, UI weight readout, and
// our drain all see the new value) via CairnAPI.ItemWeights.
public class Core : MelonMod
{
    // The drain knob is expressed as the effort multiplier at a FULL bag. Effort cost
    // scales linearly with how full the bag is:
    //   factor = 1 + (FullBagEffortMultiplier - 1) * (currentWeight / maxInventoryWeight)
    // so an empty bag is vanilla (1x) and a maxed-out bag costs FullBagEffortMultiplier.
    internal static MelonPreferences_Entry<bool> Enabled;
    internal static MelonPreferences_Entry<float> FullBagEffortMultiplier;
    internal static MelonPreferences_Entry<bool> ApplyWeightOverrides;
    internal static MelonPreferences_Entry<string> WeightOverridesRaw;

    // Reverse map from enum name -> int value for config parsing.
    private static System.Collections.Generic.Dictionary<string, Il2Cpp.InventoryItemStringIdEnum> nameToId;

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("CairnCarryWeight");

        Enabled = cat.CreateEntry("Enabled", true,
            description: "Master switch for carry-weight stamina drain.");

        FullBagEffortMultiplier = cat.CreateEntry("FullBagEffortMultiplier", 2.0f,
            description: "Climbing effort cost when the bag is FULL (at maxInventoryWeight). "
                       + "An empty bag is always 1x (vanilla); cost scales linearly with how "
                       + "full the bag is. 1 = weight has no effect; 2 = a full bag makes "
                       + "holds cost twice as much stamina.");

        ApplyWeightOverrides = cat.CreateEntry("ApplyWeightOverrides", true,
            description: "Apply the per-item weight overrides below (affects bag capacity, the "
                       + "in-game weight readout, AND this mod's drain — a true override).");

        WeightOverridesRaw = cat.CreateEntry("WeightOverrides", "",
            description: "Per-item weight multipliers, comma-separated as ITEM_ID=multiplier. "
                       + "Multiplier scales the item's vanilla weight (2 = twice as heavy, "
                       + "0 = weightless). Use the InventoryItemStringIdEnum names, e.g. "
                       + "\"ITEM_PITON=2.0, ITEM_FLASK=0.5, ITEM_NUTS=0\".");

        BuildNameMap();
        ReloadOverrides();
        DrainPatch.Register();
        BagScreen.Register();
        RegisterModOptions();

        // Re-parse overrides if the user edits the cfg live.
        WeightOverridesRaw.OnEntryValueChanged.Subscribe((_, _) => ReloadOverrides());

        MelonLogger.Msg($"CairnCarryWeight ready (full-bag effort={FullBagEffortMultiplier.Value}x, "
                      + $"{_overrideCount} overrides).");
    }

    public override void OnUpdate() => BagScreen.UpdateText();

    internal static int _overrideCount;

    private static void RegisterModOptions()
    {
        CairnAPI.ModOptions.Register("CairnCarryWeight", new[]
        {
            CairnAPI.ModOption.Toggle("Enable carry-weight drain", Enabled,
                tooltip: "Master switch. When off, bag weight has no effect on stamina."),

            CairnAPI.ModOption.Slider("Full-bag effort multiplier", 1f, 5f, FullBagEffortMultiplier,
                tooltip: "Effort cost at a completely full bag (empty bag is always 1×). "
                       + "Linear scale between empty and full."),

            CairnAPI.ModOption.Toggle("Apply item weight overrides", ApplyWeightOverrides,
                tooltip: "Apply the per-item weight multipliers below. "
                       + "Affects bag capacity, weight readout, and drain."),

            CairnAPI.ModOption.Text("Item weight overrides", WeightOverridesRaw,
                tooltip: "Comma-separated ITEM_ID=multiplier pairs, e.g. ITEM_PITON=2.0, ITEM_FLASK=0.5"),

            CairnAPI.ModOption.Action("Reload weight overrides", ReloadOverrides,
                tooltip: "Re-parse the overrides field above without restarting."),
        });
    }

    private static void BuildNameMap()
    {
        nameToId = new();
        foreach (var name in System.Enum.GetNames(typeof(Il2Cpp.InventoryItemStringIdEnum)))
        {
            var val = (Il2Cpp.InventoryItemStringIdEnum)
                System.Enum.Parse(typeof(Il2Cpp.InventoryItemStringIdEnum), name);
            nameToId[name] = val;
        }
    }

    private static void ReloadOverrides()
    {
        ItemWeights.ClearAll();
        _overrideCount = 0;

        if (!ApplyWeightOverrides.Value)
            return;

        var raw = WeightOverridesRaw.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return;

        foreach (var pair in raw.Split(','))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2)
            {
                MelonLogger.Warning($"Ignoring malformed override '{pair.Trim()}' (want ITEM_ID=multiplier).");
                continue;
            }

            var key = kv[0].Trim();
            if (!nameToId.TryGetValue(key, out var id))
            {
                MelonLogger.Warning($"Ignoring override for unknown item '{key}'.");
                continue;
            }
            if (!float.TryParse(kv[1].Trim(), NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mult)
                || mult < 0f)
            {
                MelonLogger.Warning($"Ignoring override for '{key}': '{kv[1].Trim()}' is not a valid multiplier.");
                continue;
            }

            ItemWeights.Set(id, mult);
            _overrideCount++;
        }

        MelonLogger.Msg($"Loaded {_overrideCount} item weight override(s).");
    }
}
