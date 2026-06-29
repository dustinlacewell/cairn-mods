using System.Collections.Generic;
using MelonLoader;
using CrossMenuUI = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI;

namespace CairnAPI;

/// <summary>
/// Owns the live menu integration. Finds the active vanilla <see cref="CrossMenuUI"/>, keeps
/// every registered action's routing handler in its dispatch dict, stamps base-menu actions
/// into the vanilla top layer's free slots, and drives the chord-selected mod menus via
/// <see cref="ChordMenus"/>. Re-binds automatically when the HUD is rebuilt.
///
/// All work runs on the Unity main thread (from <see cref="Core.OnUpdate"/>).
/// </summary>
internal static class MenuController
{
    internal static bool Ready { get; private set; }

    private static CrossMenuUI _menu;
    private static ChordMenus _chords;
    private static int _appliedRevision = -1;

    // one stable routing handler per custom type, reused across dict re-insertions
    private static readonly Dictionary<int, RoutingHandler> _handlers = new();

    internal static void Tick()
    {
        if (!EnsureMenuFound()) { Ready = false; return; }

        if (_appliedRevision != Registry.Revision)
        {
            ApplyBaseLayer();
            _appliedRevision = Registry.Revision;
        }

        EnsureHandlers();        // self-heals the stock dict rebuilds (save load etc.)
        _chords?.Tick();         // chord-selected mod menus (borrow-the-real-menu)
        Ready = true;
    }

    // --- bind / rebind the real menu ---

    private static bool EnsureMenuFound()
    {
        if (_menu != null && !_menu.WasCollected && _menu.gameObject != null) return true;
        if (_menu != null) Rebind();

        var found = MenuFinder.FindActivePopulated();
        if (found == null) return false;
        _menu = found;
        _chords = new ChordMenus(_menu);
        MelonLogger.Msg($"[CairnAPI:CrossMenu] bound real CrossMenuUI '{_menu.name}'; chord menus active.");
        return true;
    }

    private static void Rebind()
    {
        _chords?.Teardown();
        _chords = null;
        _handlers.Clear();
        _appliedRevision = -1;
        _menu = null;
    }

    // --- base-menu actions: stamp into the vanilla top layer's free slots ---

    private static void ApplyBaseLayer()
    {
        foreach (var e in Registry.All)
        {
            if (e.So == null) e.So = IconFactory.BuildSo(e);
            if (e.Action.Menu != CrossMenu.BaseMenu) continue;          // chord menus swap in on demand
            // base actions occupy the vanilla TOP layer by direction (free slots only — we
            // don't overwrite an occupied stock direction)
            if (LayerOps.HasDirection(LayerOps.GetActions(_menu, MenuLayerId.Top), MapDir(e.Action.Direction)))
            {
                MelonLogger.Warning($"[CairnAPI:CrossMenu] base-menu slot {e.Action.Direction} is taken by vanilla; '{e.Action.Id}' skipped. Use a chord menu instead.");
                continue;
            }
            LayerOps.SetSlot(_menu, MenuLayerId.Top, e.Action.Direction, e.So);
        }
        MelonLogger.Msg($"[CairnAPI:CrossMenu] applied base-menu registrations.");
    }

    private static CrossMenuDir MapDir(CrossMenuDir d) => d;   // identity; kept for clarity at call site

    // --- handler dict upkeep ---

    private static void EnsureHandlers()
    {
        if (_menu == null) return;
        foreach (var e in Registry.All)
        {
            if (HandlerOps.Has(_menu, e.TypeValue)) continue;
            HandlerOps.Put(_menu, e.TypeValue, GetOrMakeHandler(e.TypeValue));
        }
    }

    private static RoutingHandler GetOrMakeHandler(int typeValue)
    {
        if (_handlers.TryGetValue(typeValue, out var h) && h != null) return h;
        h = new RoutingHandler { TypeValue = typeValue };
        _handlers[typeValue] = h;
        return h;
    }
}
