using System.Collections.Generic;
using CrossMenuUI = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI;
using QuickInventoryUI = Il2CppTheGameBakers.Cairn.UI.QuickInventoryUI;
using Il2CppReferenceArrayMapped = Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppTheGameBakers.Cairn.UI.CrossMenuUI.MenuLayer.MappedAction>;
using Il2CppMappedAction = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI.MenuLayer.MappedAction;
using Il2CppDirection = Il2CppTheGameBakers.Cairn.UI.CrossMenuUI.Direction;

namespace CairnAPI;

/// <summary>
/// Drives a mod menu by <em>borrowing</em> the vanilla cross-menu's TOP layer, so the game's own
/// machinery (D-pad → hold-fill → execute → handler dispatch → audio → render) does all the work.
/// This is the load-bearing reverse-engineered piece; the offsets and mechanism below were verified
/// live against the running game (see <c>re/systems/ui/crossmenu/crossmenu-internals.md</c>).
///
/// <para><b>The vanilla model.</b> CrossMenuUI has two serialized layers: <c>topLayer</c> (+0x40,
/// the DEFAULT wheel) and <c>bottomLayer</c> (+0x48, the LT wheel). <c>GetRelevantLayer</c> picks one
/// from <c>forcedLayerValue</c> (+0xcc): 2→top, 1→bottom, 0→auto-by-<c>isOnBottomLayer</c> (+0xb8).
/// Physical LT toggles <c>isOnBottomLayer</c> on its press edge. The wheel stays on-screen only while
/// <c>QuickInventoryUI.crossMenuShowTimeRemaining</c> (+0x70) &gt; 0; that window is re-armed by
/// <c>ShowCrossMenuUI</c> on the LT press edge and decays each frame, after which
/// <c>QuickInventoryUI.Refresh</c> fades the wheel out.</para>
///
/// <para><b>Show a mod menu</b> (<see cref="Enter"/>): snapshot <c>topLayer.actions</c>, write the
/// menu's actions, <c>SetForcedLayer(2)</c> (force the modded top layer), <c>Refresh()</c>.
/// <b>Restore</b> (<see cref="Restore"/>): write the saved actions back and hand selection to auto.
/// Because <c>SetForcedLayer(0)</c> zeroes <c>isOnBottomLayer</c> as a side effect, re-assert it
/// AFTER, mirroring physical LT so a still-held LT lands on the LT wheel, not the default.</para>
/// </summary>
internal sealed class LayerSwap
{
    private readonly CrossMenuUI _menu;
    private readonly QuickInventoryUI _qiu;
    private Il2CppReferenceArrayMapped _savedTopActions;

    internal LayerSwap(CrossMenuUI menu, QuickInventoryUI qiu)
    {
        _menu = menu;
        _qiu = qiu;
    }

    /// <summary>Show <paramref name="menu"/> by writing its actions into the forced top layer.</summary>
    internal void Enter(string menu)
    {
        _savedTopActions = _menu.topLayer.actions;
        SetTopActions(BuildLayer(menu));
        Force();
        Refresh();
    }

    /// <summary>
    /// Re-paint the borrowed menu each frame. The menu never polls, so a forced <see cref="Refresh"/>
    /// is what re-reads our top layer and re-commits sprites/counts/hold-fill; force-display keeps it
    /// shown while the chord is held.
    /// </summary>
    internal void KeepShown()
    {
        Force();
        TrySet(() => _qiu?.SetForceDisplayCrossMenu(true));
        Refresh();
        TrySet(() => _qiu?.Refresh(null, true));
    }

    /// <summary>
    /// Put the borrowed top layer back and hand layer selection to vanilla auto-mode.
    /// <paramref name="leftTriggerHeld"/> mirrors physical LT so the wheel lands on the right layer.
    /// </summary>
    internal void Restore(bool leftTriggerHeld)
    {
        SetTopActions(_savedTopActions);
        _savedTopActions = null;

        TrySet(() => _menu.SetForcedLayer(0));                 // auto — this ALSO zeroes isOnBottomLayer,
        TrySet(() => _menu.isOnBottomLayer = leftTriggerHeld); // so re-assert it AFTER (LT held → LT wheel).
        Refresh();

        if (!leftTriggerHeld) Hide();
    }

    /// <summary>
    /// Keep the vanilla LT wheel from fading while LT is held with no chord. After a chord exit the
    /// show window has decayed (it is only re-armed on the LT press edge, long consumed), so re-arm it
    /// each frame — exactly what <c>ShowCrossMenuUI</c> does (+0x70 = +0x60). This uses the game's own
    /// show mechanism, NOT <c>forceDisplayCrossMenu</c> (whose reap path caused the earlier flicker).
    /// </summary>
    internal void KeepVanillaWheelAlive()
    {
        if (_qiu == null) return;
        TrySet(() => _qiu.crossMenuShowTimeRemaining = _qiu.showCrossMenuForSeconds);
    }

    // --- vanilla pokes ---

    private void Force() => TrySet(() => _menu.SetForcedLayer(2));
    private void Refresh() => TrySet(() => _menu.Refresh());

    private void Hide()
    {
        TrySet(() => _qiu?.SetForceDisplayCrossMenu(false));
        TrySet(() => _qiu?.ForceHideImmediate());
    }

    /// <summary>
    /// Write the top layer's actions array. <c>MenuLayer</c> is an inline value-type struct on the
    /// CrossMenuUI; assign through the proxy (read-modify-write of the whole struct), which marshals
    /// back to the inline storage.
    /// </summary>
    private void SetTopActions(Il2CppReferenceArrayMapped actions)
    {
        var layer = _menu.topLayer;
        layer.actions = actions;
        _menu.topLayer = layer;
    }

    /// <summary>A 4-slot layer built from the menu's registrations; unmapped directions are absent.</summary>
    private static Il2CppReferenceArrayMapped BuildLayer(string menu)
    {
        var list = new List<Il2CppMappedAction>();
        foreach (var e in Registry.ForMenu(menu))
        {
            if (e.So == null) e.So = IconFactory.BuildSo(e);
            list.Add(new Il2CppMappedAction
            {
                direction = (Il2CppDirection)(int)e.Action.Direction,
                action = e.So,
            });
        }
        var arr = new Il2CppReferenceArrayMapped(list.Count);
        for (int i = 0; i < list.Count; i++) arr[i] = list[i];
        return arr;
    }

    /// <summary>Il2Cpp proxy calls can throw across the managed↔native seam; never let that abort a frame.</summary>
    private static void TrySet(System.Action poke)
    {
        try { poke(); } catch { }
    }
}
