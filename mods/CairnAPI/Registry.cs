using System.Collections.Generic;
using MelonLoader;

namespace CairnAPI;

/// <summary>
/// Managed-side store of defined menus and registered actions, plus the action-id ↔ synthetic
/// CrossMenuActionType-int allocation that bridges to the Il2Cpp dispatch dict.
/// </summary>
internal static class Registry
{
    /// <summary>Custom CrossMenuActionType values start here (vanilla uses 0..9).</summary>
    internal const int FirstCustomType = 100;

    internal sealed class Entry
    {
        internal CrossMenuAction Action;
        internal int TypeValue;
        internal Il2CppTheGameBakers.Cairn.UI.CrossMenuAction So;   // cached game data asset
    }

    // menuId -> chord (extra modifiers beyond LT). BaseMenu is implicit (CrossMenuModifier.None).
    private static readonly Dictionary<string, CrossMenuModifier> _menus = new();
    private static readonly Dictionary<string, Entry> _byId = new();
    private static readonly Dictionary<int, Entry> _byType = new();
    private static int _nextType = FirstCustomType;

    internal static int Revision { get; private set; }

    // --- menus ---

    internal static void DefineMenu(string menuId, CrossMenuModifier chord)
    {
        if (string.IsNullOrEmpty(menuId)) { MelonLogger.Warning("[CairnAPI:CrossMenu] DefineMenu: empty id."); return; }
        if (menuId == CrossMenu.BaseMenu) { MelonLogger.Warning("[CairnAPI:CrossMenu] DefineMenu: 'vanilla' is reserved."); return; }
        if (chord == CrossMenuModifier.None) { MelonLogger.Warning($"[CairnAPI:CrossMenu] DefineMenu '{menuId}': chord None collides with the base menu."); return; }

        foreach (var kv in _menus)
            if (kv.Value == chord && kv.Key != menuId)
            {
                MelonLogger.Warning($"[CairnAPI:CrossMenu] DefineMenu '{menuId}': chord {chord} already claimed by '{kv.Key}'; ignored.");
                return;
            }

        _menus[menuId] = chord;
        Revision++;
        MelonLogger.Msg($"[CairnAPI:CrossMenu] menu '{menuId}' = LT + {chord}.");
    }

    internal static IEnumerable<KeyValuePair<string, CrossMenuModifier>> Menus => _menus;

    internal static bool TryGetChord(string menuId, out CrossMenuModifier chord) => _menus.TryGetValue(menuId, out chord);

    // --- actions ---

    internal static void Register(CrossMenuAction action)
    {
        if (action == null || string.IsNullOrEmpty(action.Id))
        {
            MelonLogger.Warning("[CairnAPI:CrossMenu] Register: null action or empty Id.");
            return;
        }
        if (action.Menu != CrossMenu.BaseMenu && !_menus.ContainsKey(action.Menu))
            MelonLogger.Warning($"[CairnAPI:CrossMenu] action '{action.Id}' targets undefined menu '{action.Menu}' (define it first).");
        if (action.OnExecute == null)
            MelonLogger.Warning($"[CairnAPI:CrossMenu] action '{action.Id}' has no OnExecute; no-op.");

        if (_byId.TryGetValue(action.Id, out var existing))
        {
            existing.Action = action;
            existing.So = null;            // rebuild the icon SO on next apply
        }
        else
        {
            var e = new Entry { Action = action, TypeValue = _nextType++ };
            _byId[action.Id] = e;
            _byType[e.TypeValue] = e;
        }
        Revision++;
        MelonLogger.Msg($"[CairnAPI:CrossMenu] registered '{action.Id}' ({action.Menu}/{action.Direction}).");
    }

    internal static void Unregister(string id)
    {
        if (id == null || !_byId.TryGetValue(id, out var e)) return;
        _byId.Remove(id);
        _byType.Remove(e.TypeValue);
        Revision++;
    }

    internal static IEnumerable<Entry> All => _byId.Values;

    internal static bool TryGetByType(int typeValue, out Entry e) => _byType.TryGetValue(typeValue, out e);
    internal static bool IsCustomType(int typeValue) => _byType.ContainsKey(typeValue);

    /// <summary>First action registered to a given menu + direction.</summary>
    internal static bool Find(string menuId, CrossMenuDir dir, out Entry entry)
    {
        foreach (var e in _byId.Values)
            if (e.Action.Menu == menuId && e.Action.Direction == dir) { entry = e; return true; }
        entry = null;
        return false;
    }

    internal static IEnumerable<Entry> ForMenu(string menuId)
    {
        foreach (var e in _byId.Values)
            if (e.Action.Menu == menuId) yield return e;
    }
}
