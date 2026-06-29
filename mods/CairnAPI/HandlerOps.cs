using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using CrossMenuUI = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI;
using CrossMenuActionHandler = Il2CppTheGameBakers.Cairn.UI.CrossMenuActionHandler;
using Il2CppActionType = Il2CppTheGameBakers.Cairn.UI.CrossMenuActionType;

namespace CairnAPI;

/// <summary>
/// Inserts/replaces an entry in a CrossMenuUI's <c>handlers</c> dictionary
/// (<c>Dictionary&lt;CrossMenuActionType, CrossMenuActionHandler&gt;</c>), keyed by the
/// synthetic custom type int.
/// </summary>
internal static class HandlerOps
{
    internal static void Put(CrossMenuUI ui, int typeValue, RoutingHandler handler)
    {
        var dict = ui.handlers;
        if (dict == null)
        {
            MelonLogger.Warning("[CairnAPI:CrossMenu] target menu has no handlers dict; cannot add handler.");
            return;
        }
        var key = (Il2CppActionType)typeValue;
        dict[key] = handler.Cast<CrossMenuActionHandler>();
    }

    internal static bool Has(CrossMenuUI ui, int typeValue)
    {
        var dict = ui.handlers;
        return dict != null && dict.ContainsKey((Il2CppActionType)typeValue);
    }
}
