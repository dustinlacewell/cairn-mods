using MelonLoader;
using CrossMenuUI = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI;
using QuickInventoryUI = Il2CppTheGameBakers.Cairn.UI.QuickInventoryUI;

namespace CrossMenuLib;

/// <summary>
/// Finds the <see cref="QuickInventoryUI"/> that owns a given cross-menu. The menu lives at
/// <c>.../QuickInventory/CrossMenu</c>, so the owner is the nearest QuickInventoryUI up the
/// hierarchy. We poke its <c>forceDisplayCrossMenu</c> to show the menu through the game's
/// own pipeline.
/// </summary>
internal static class QuickInventoryLocator
{
    internal static QuickInventoryUI For(CrossMenuUI menu)
    {
        if (menu == null) return null;
        var qiu = menu.GetComponentInParent<QuickInventoryUI>(true);
        if (qiu == null)
            MelonLogger.Warning("[CrossMenuLib] no QuickInventoryUI parent found; menu force-show unavailable.");
        return qiu;
    }
}
