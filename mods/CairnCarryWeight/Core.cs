using System.Collections.Generic;
using System.Globalization;
using MelonLoader;

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
// Weight is per-pawn, so each climber drains by their OWN bag — co-op safe, no
// interaction with CairnCoop (which never touches stamina or inventory weight).
//
// Item weights can be overridden (a true override: capacity, UI weight readout, and
// our drain all see the new value) — see WeightOverrides below.
public class Core : MelonMod
{
    // The drain knob is expressed as the effort multiplier at a FULL bag. Effort cost
    // scales linearly with how full the bag is:
    //   factor = 1 + (FullBagEffortMultiplier - 1) * (currentWeight / maxInventoryWeight)
    // so an empty bag is vanilla (1x) and a maxed-out bag costs FullBagEffortMultiplier.
    // "Full" means the game's live InventoryTweakables.maxInventoryWeight (Addressable-
    // loaded; ctor default 20), so the knob stays meaningful even if designers retune the cap.
    internal static MelonPreferences_Entry<bool> Enabled;
    internal static MelonPreferences_Entry<float> FullBagEffortMultiplier;
    internal static MelonPreferences_Entry<bool> ApplyWeightOverrides;
    internal static MelonPreferences_Entry<string> WeightOverridesRaw;

    // Fallback bag capacity if the live tweakable isn't ready yet (matches the binary's
    // InventoryTweakables..ctor default for maxInventoryWeight).
    internal const float DefaultMaxInventoryWeight = 20f;

    // itemId (InventoryItemStringIdEnum int value) -> multiplier applied to the
    // item's vanilla weight. Parsed from WeightOverridesRaw.
    internal static readonly Dictionary<int, float> WeightOverrides = new();

    // Reverse map from enum name -> int value, built once for parsing the config.
    private static Dictionary<string, int> nameToId;

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

        // Re-parse overrides if the user edits the cfg live.
        WeightOverridesRaw.OnEntryValueChanged.Subscribe((_, _) => ReloadOverrides());

        MelonLogger.Msg($"CairnCarryWeight ready (full-bag effort={FullBagEffortMultiplier.Value}x, "
                      + $"{WeightOverrides.Count} overrides).");
    }

    // Live bag capacity from the game's tweakables, falling back to the binary default
    // until the Addressable-loaded instance is ready.
    internal static float MaxInventoryWeight()
    {
        var tweak = Il2Cpp.InventoryTweakables.Instance;
        if (tweak == null)
            return DefaultMaxInventoryWeight;
        float max = tweak.maxInventoryWeight;
        return max > 0f ? max : DefaultMaxInventoryWeight;
    }

    private static void BuildNameMap()
    {
        nameToId = new Dictionary<string, int>();
        foreach (var name in System.Enum.GetNames(typeof(Il2Cpp.InventoryItemStringIdEnum)))
        {
            var val = (int)(Il2Cpp.InventoryItemStringIdEnum)
                System.Enum.Parse(typeof(Il2Cpp.InventoryItemStringIdEnum), name);
            nameToId[name] = val;
        }
    }

    private static void ReloadOverrides()
    {
        WeightOverrides.Clear();
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
            if (!nameToId.TryGetValue(key, out int id))
            {
                MelonLogger.Warning($"Ignoring override for unknown item '{key}'.");
                continue;
            }
            if (!float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float mult)
                || mult < 0f)
            {
                MelonLogger.Warning($"Ignoring override for '{key}': '{kv[1].Trim()}' is not a valid multiplier.");
                continue;
            }

            WeightOverrides[id] = mult;
        }

        MelonLogger.Msg($"Loaded {WeightOverrides.Count} item weight override(s).");
    }
}
