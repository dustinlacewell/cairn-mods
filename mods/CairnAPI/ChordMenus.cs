using CrossMenuUI = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI;

namespace CairnAPI;

/// <summary>
/// Per-frame state machine for chord-selected mod menus. Each frame it reads the pad
/// (<see cref="ChordInput"/>) and reconciles the displayed wheel via <see cref="LayerSwap"/>:
///
/// <list type="bullet">
/// <item><b>Enter/exit a mod menu</b> when the selected chord changes — borrow or restore the
/// vanilla top layer.</item>
/// <item><b>While a mod menu is shown</b> — re-force + re-render it each frame (the menu doesn't
/// poll).</item>
/// <item><b>While LT is held with no chord</b> — keep the vanilla LT wheel from fading by re-arming
/// the game's show window.</item>
/// </list>
///
/// All work runs on the Unity main thread, pumped from <see cref="MenuController"/>.
/// </summary>
internal sealed class ChordMenus
{
    private readonly LayerSwap _layers;

    private string _active;   // currently-shown mod-menu id, or null

    internal ChordMenus(CrossMenuUI menu)
        => _layers = new LayerSwap(menu, QuickInventoryLocator.For(menu));

    internal void Tick()
    {
        var input = ChordInput.Read();

        if (input.Menu != _active)
        {
            if (_active != null) _layers.Restore(leftTriggerHeld: input.LeftTriggerHeld);
            if (input.Menu != null) _layers.Enter(input.Menu);
            _active = input.Menu;
        }

        if (_active != null) _layers.KeepShown();
        else if (input.LeftTriggerHeld) _layers.KeepVanillaWheelAlive();
    }

    /// <summary>On rebind/teardown, restore any borrowed layer so the vanilla menu is left intact.</summary>
    internal void Teardown()
    {
        if (_active == null) return;
        _layers.Restore(leftTriggerHeld: false);
        _active = null;
    }
}
