using UnityEngine;
using Il2CppInterop.Runtime;
using CrossMenuUI = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI;

namespace CrossMenuLib;

/// <summary>
/// Locates the live, in-use vanilla CrossMenuUI. The scene contains two instances
/// (the active "(Clone)" HUD with a populated handler dict, and an inactive template
/// with an empty dict); we want the populated active one.
/// </summary>
internal static class MenuFinder
{
    internal static CrossMenuUI FindActivePopulated()
    {
        var t = Il2CppType.Of<CrossMenuUI>();
        var all = Resources.FindObjectsOfTypeAll(t);
        if (all == null) return null;

        CrossMenuUI fallback = null;
        foreach (var o in all)
        {
            var ui = o.TryCast<CrossMenuUI>();
            if (ui == null) continue;

            var handlers = ui.handlers;
            bool populated = handlers != null && handlers.Count > 0;
            bool active = ui.gameObject.activeInHierarchy;

            if (populated && active) return ui;       // the one we want
            if (populated) fallback = ui;             // populated but momentarily inactive
        }
        return fallback;                               // may be null until InitHandlers has run
    }
}
