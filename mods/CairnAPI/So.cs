using UnityEngine;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppGameAction = Il2CppTheGameBakers.Cairn.UI.CrossMenuAction;

namespace CairnAPI;

/// <summary>
/// One place that touches the private serialized fields of the game's CrossMenuAction
/// ScriptableObject. Il2CppInterop surfaces [SerializeField] privates as properties;
/// their generated names can drift between interop builds, so the coupling is isolated
/// here and falls back to reflection if a direct setter isn't present.
/// </summary>
internal static class So
{
    internal static void Set(Il2CppGameAction so, Il2CppReferenceArray<Sprite> sprites,
                             int actionType, bool displayCount, int countWarningMax, bool executeImmediately)
    {
        // Field offsets (from the dump) the names map to:
        //   sprites @0x18, action @0x20, displayCount @0x24,
        //   displayCountWarning @0x25, countWarningMaxValue @0x28, executeImmediatly @0x2C
        TrySet(so, "sprites", sprites);
        TrySetEnum(so, "action", actionType);
        TrySet(so, "displayCount", displayCount);
        TrySet(so, "displayCountWarning", countWarningMax > 0);
        TrySet(so, "countWarningMaxValue", countWarningMax);
        TrySet(so, "executeImmediatly", executeImmediately);   // note: game's field is misspelled "Immediatly"
    }

    private static void TrySet(Object target, string name, object value)
    {
        var t = target.GetType();
        var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic);
        if (p != null && p.CanWrite) { p.SetValue(target, value); return; }
        var f = t.GetField(name, System.Reflection.BindingFlags.Instance |
                                 System.Reflection.BindingFlags.Public |
                                 System.Reflection.BindingFlags.NonPublic);
        if (f != null) { f.SetValue(target, value); return; }
        MelonLogger.Warning($"[CairnAPI:CrossMenu] CrossMenuAction has no settable '{name}' (sprite/flag may not show).");
    }

    /// <summary>The 'action' field is the CrossMenuActionType enum; set it from the raw int.</summary>
    private static void TrySetEnum(Object target, string name, int raw)
    {
        var t = target.GetType();
        var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic);
        if (p != null && p.CanWrite)
        {
            var enumVal = System.Enum.ToObject(p.PropertyType, raw);
            p.SetValue(target, enumVal);
            return;
        }
        var f = t.GetField(name, System.Reflection.BindingFlags.Instance |
                                 System.Reflection.BindingFlags.Public |
                                 System.Reflection.BindingFlags.NonPublic);
        if (f != null)
        {
            var enumVal = System.Enum.ToObject(f.FieldType, raw);
            f.SetValue(target, enumVal);
            return;
        }
        MelonLogger.Warning("[CairnAPI:CrossMenu] CrossMenuAction has no settable 'action' (type) field.");
    }
}
