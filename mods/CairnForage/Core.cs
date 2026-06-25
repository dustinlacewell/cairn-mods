using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using Il2Cpp;                                   // LootProvider, ConsumableItem (global ns)
using TGB = Il2CppTheGameBakers.Cairn;          // InteractionProvider (TheGameBakers.Cairn ns)
using CairnAPI;

[assembly: MelonInfo(typeof(CairnForage.Core), "CairnForage", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]
[assembly: MelonAdditionalDependencies("CairnAPI")]

namespace CairnForage;

// Consume world items in place: when you're near a world item whose config is a usable
// ConsumableItem, a "Consume" prompt appears (on the screen prompt rail, via CairnAPI.ScreenPrompt)
// alongside the vanilla pick-up prompt. Pressing the consume key applies the item's effect
// (food/water/stamina/heal) and removes it from the world — no bagging. The prompt is mirrored from
// the game's own loot prompt via Harmony hooks on InteractionProvider's show/hide; no Il2Cpp type
// injection. See re/systems/interaction/world-consume.md and re/systems/ui/prompts/.
public class Core : MelonMod
{
    internal static Core Instance;
    internal MelonPreferences_Entry<bool> Enabled;
    internal MelonPreferences_Entry<Key> ConsumeKey;

    // The loot provider whose consume prompt is currently shown (null = none).
    internal LootProvider ActiveProvider;
    internal ConsumableItem ActiveItem;

    // The live screen-rail prompt handle (Invalid when nothing shown).
    private ScreenPromptHandle _prompt = ScreenPromptHandle.Invalid;

    // Where on screen the consume prompt sits (own-Canvas coords are screen-center-relative).
    // Just below center, under the game's own prompt area.
    private static readonly Vector2 RailPos = new(0f, -260f);

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("CairnForage");
        Enabled = cat.CreateEntry("Enabled", true,
            description: "Show a 'Consume' prompt on world consumables and allow eating them in place.");
        ConsumeKey = cat.CreateEntry("ConsumeKey", Key.G,
            description: "Key to consume the focused world item in place (vanilla interact still picks up).");
        Instance = this;
        LoggerInstance.Msg($"CairnForage loaded — consume world items with {ConsumeKey.Value}.");
    }

    // Called by the Display/Hide patches to drive the mirror prompt.
    internal void ShowFor(LootProvider provider, ConsumableItem item)
    {
        ActiveProvider = provider;
        ActiveItem = item;
        if (!_prompt.Valid)
        {
            var glyph = Glyph.Key(ConsumeKey.Value.ToString().ToLowerInvariant());
            _prompt = ScreenPrompt.Show("Consume", glyph);
            ScreenPrompt.Move(_prompt, RailPos);
        }
        ScreenPrompt.SetActive(_prompt, true);
    }

    internal void HideFor(LootProvider provider)
    {
        // Only hide if the hidden provider is the one we're showing (a stale-provider hide must not
        // clobber a freshly-shown one).
        if (ActiveProvider != null && provider != null && provider.Pointer != ActiveProvider.Pointer)
            return;
        ActiveProvider = null;
        ActiveItem = null;
        ScreenPrompt.SetActive(_prompt, false);
    }

    public override void OnUpdate()
    {
        if (ActiveProvider == null || ActiveItem == null)
            return;

        // Provider looted out from under us (e.g. picked up normally) — clear.
        if (ActiveProvider.StocksEmpty)
        {
            HideFor(ActiveProvider);
            return;
        }

        var kb = Keyboard.current;
        if (kb != null && kb[ConsumeKey.Value].wasPressedThisFrame)
        {
            var provider = ActiveProvider;
            var item = ActiveItem;
            HideFor(provider);
            if (Consume.Apply(item))
                Consume.DespawnLoot(provider);
        }
    }
}

// Mirror the game's loot prompt: when a LootProvider's prompt shows for a usable consumable, show
// our Consume prompt; hide it when the game hides. DisplayGameplayPrompt / HideGameplayPrompt are
// private on the base InteractionProvider (LootProvider inherits) — patch by string name.
[HarmonyPatch(typeof(TGB.InteractionProvider), "DisplayGameplayPrompt")]
internal static class DisplayPromptPatch
{
    private static void Postfix(TGB.InteractionProvider __instance)
    {
        var core = Core.Instance;
        if (core == null || !core.Enabled.Value) return;
        var lp = __instance.TryCast<LootProvider>();
        if (lp == null) return;
        var item = Consume.ConsumableAt(lp);
        if (item != null && Consume.CanUse(item))
            core.ShowFor(lp, item);
    }
}

[HarmonyPatch(typeof(TGB.InteractionProvider), "HideGameplayPrompt")]
internal static class HidePromptPatch
{
    private static void Postfix(TGB.InteractionProvider __instance)
    {
        var core = Core.Instance;
        if (core == null) return;
        var lp = __instance.TryCast<LootProvider>();
        if (lp != null) core.HideFor(lp);
    }
}
