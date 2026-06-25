using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using CrossMenuUI = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI;
using Il2CppGameAction = Il2CppTheGameBakers.Cairn.UI.CrossMenuAction;
using Il2CppMenuLayer = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI.MenuLayer;
using Il2CppMappedAction = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI.MenuLayer.MappedAction;
using Il2CppDirection = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI.Direction;

namespace CrossMenuLib;

/// <summary>Top or bottom layer of the vanilla cross-menu (internal — the public API has no layer concept).</summary>
internal enum MenuLayerId { Top = 0, Bottom = 1 }

/// <summary>
/// Reads and mutates a CrossMenuUI layer's <c>MappedAction[]</c> (direction → action SO).
/// A slot is replaced if its direction is already mapped, otherwise appended (rebuilding
/// the Il2Cpp array, which has no in-place resize).
/// </summary>
internal static class LayerOps
{
    internal static Il2CppReferenceArray<Il2CppMappedAction> GetActions(CrossMenuUI ui, MenuLayerId layer)
    {
        var ml = layer == MenuLayerId.Top ? ui.topLayer : ui.bottomLayer;
        return ml.actions;
    }

    internal static bool HasDirection(Il2CppReferenceArray<Il2CppMappedAction> arr, MenuDir dir)
    {
        if (arr == null) return false;
        var d = (Il2CppDirection)(int)dir;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i].direction == d) return true;
        return false;
    }

    /// <summary>Set (replace or append) the action SO at (layer, dir).</summary>
    internal static void SetSlot(CrossMenuUI ui, MenuLayerId layer, MenuDir dir, Il2CppGameAction so)
    {
        var ml = layer == MenuLayerId.Top ? ui.topLayer : ui.bottomLayer;
        var arr = ml.actions;
        var d = (Il2CppDirection)(int)dir;

        // replace if the direction already exists
        if (arr != null)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i].direction == d)
                {
                    var ma = arr[i];
                    ma.action = so;
                    arr[i] = ma;
                    WriteBack(ui, layer, ml);
                    return;
                }
            }
        }

        // otherwise append into a new, one-larger array
        int oldLen = arr == null ? 0 : arr.Length;
        var grown = new Il2CppReferenceArray<Il2CppMappedAction>(oldLen + 1);
        for (int i = 0; i < oldLen; i++) grown[i] = arr[i];

        var added = new Il2CppMappedAction { direction = d, action = so };
        grown[oldLen] = added;

        ml.actions = grown;
        WriteBack(ui, layer, ml);
    }

    /// <summary>
    /// MenuLayer is a struct field; assigning <c>ml.actions</c> mutates a local copy, so
    /// write the whole struct back onto the CrossMenuUI.
    /// </summary>
    private static void WriteBack(CrossMenuUI ui, MenuLayerId layer, Il2CppMenuLayer ml)
    {
        if (layer == MenuLayerId.Top) ui.topLayer = ml;
        else ui.bottomLayer = ml;
    }
}
