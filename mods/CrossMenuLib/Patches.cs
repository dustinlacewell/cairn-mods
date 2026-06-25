using HarmonyLib;
using Il2Cpp;
using Il2CppActionType = Il2CppTheGameBakers.Cairn.UI.CrossMenuActionType;

namespace CrossMenuLib;

/// <summary>
/// Zone whitelist. When a TenzingVolume restricts the cross-menu to certain actions,
/// GoalManager.IsCrossMenuActionAllowed returns false for anything off the list — which
/// would block every custom action. We force custom types (≥ FirstCustomType) to allowed,
/// leaving stock gating untouched. A mod that wants its action zone-restricted simply
/// returns false from its own IsAvailable instead.
/// </summary>
[HarmonyPatch(typeof(GoalManager), nameof(GoalManager.IsCrossMenuActionAllowed))]
internal static class Patch_IsCrossMenuActionAllowed
{
    private static void Postfix(Il2CppActionType action, ref bool __result)
    {
        if (Registry.IsCustomType((int)action)) __result = true;
    }
}

/// <summary>
/// Audio. PawnAudioHandler.GetCrossMenuActionId switches the action enum to a Wwise
/// switch id and returns the default ("Not CrossMenuActionType") for unknown values, so
/// a custom action would be silent. We map any custom type to the SAVEMARKER interaction
/// switch so it gets reasonable begin/hold/complete feedback. (A future API could let a
/// mod pick which stock sound to borrow; SAVEMARKER is a neutral, non-piton click.)
/// </summary>
[HarmonyPatch(typeof(PawnAudioHandler), nameof(PawnAudioHandler.GetCrossMenuActionId))]
internal static class Patch_GetCrossMenuActionId
{
    // Wwise SWITCH_CROSSMENU_INTERACTION.SWITCH value for SAVEMARKER, resolved once.
    private static bool _resolved;
    private static uint _saveMarkerSwitch;

    private static void Postfix(Il2CppActionType action, ref uint __result)
    {
        if (!Registry.IsCustomType((int)action)) return;
        if (!_resolved) { _saveMarkerSwitch = ResolveSaveMarkerSwitch(); _resolved = true; }
        if (_saveMarkerSwitch != 0) __result = _saveMarkerSwitch;
    }

    /// <summary>
    /// Read the SAVEMARKER switch constant from the generated AK switches type so we don't
    /// hardcode a build-specific id. Falls back to 0 (no override) if the field shape differs.
    /// </summary>
    private static uint ResolveSaveMarkerSwitch()
    {
        try
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = null;
                try { t = asm.GetType("Il2CppSWITCHES+SWITCH_CROSSMENU_INTERACTION+SWITCH", false); } catch { }
                if (t == null) continue;
                var f = t.GetField("SAVEMARKER", System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.Static);
                if (f != null)
                {
                    var v = f.GetValue(null);
                    return System.Convert.ToUInt32(v);
                }
            }
        }
        catch { }
        return 0;
    }
}
