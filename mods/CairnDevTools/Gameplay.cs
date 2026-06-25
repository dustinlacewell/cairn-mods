using System;
using UnityEngine;
using Il2CppTheGameBakers.Cairn.UI;

namespace CairnDevTools;

/// <summary>
/// Jump straight from the MainMenu into playable gameplay by continuing the most-recent save —
/// the fast path into a real climbing scene for live testing (no menu navigation, no input sim,
/// not the DebugMenu). Mirrors what the "Continue" menu entry does:
/// <c>MainMenu.mustContinueSavegame = true; MainMenu.TransitionToContinueIfAvailable()</c>.
/// Scene goes MainMenu → CommonBaseScene (~12 s) where the player and CrossMenu exist.
/// </summary>
internal static class Gameplay
{
    internal static string Continue(string[] _)
    {
        var mm = UnityEngine.Object.FindObjectOfType<MainMenu>(true);
        if (mm == null)
            return "no MainMenu in scene — only works from the main menu (current scene: "
                   + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + ")";
        try
        {
            mm.mustContinueSavegame = true;
            mm.TransitionToContinueIfAvailable();
            return "continuing save → gameplay (scene change to CommonBaseScene in ~12s)";
        }
        catch (Exception e)
        {
            return "failed: " + e.Message;
        }
    }
}
