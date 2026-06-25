using CairnAPI;
using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;
using TGB = Il2CppTheGameBakers.Cairn;
using CairnUI = Il2CppTheGameBakers.Cairn.UI;

namespace CairnFreeRoam;

/// <summary>
/// Drives bookmark add/delete/rename while the eagle-eye fast-travel view is open.
///  - Add: bookmark the climber's current position as a new warp point (native SortWarps slots it by height).
///  - Delete: remove the selected custom point.
///  - Rename: enter an inline edit on the selected custom point — clears the name, live-types into the row,
///    Enter confirms, Esc cancels.
/// After add/delete the list is rebuilt (CreateLocations); stored names are repainted onto custom rows each
/// frame (RowLabels). Each action is a CROSS-DEVICE InputAction (keyboard key + gamepad button) built via
/// CairnAPI Glyph.Custom — the SAME action drives both the on-screen glyph (adaptive-swaps KBM↔pad) and the
/// trigger (WasPerformedThisFrame), so it works and shows correctly on both devices.
/// </summary>
public sealed class BookmarkController
{
    private readonly BookmarkStore _store;
    private readonly BookmarkPrompts _prompts = new();
    private readonly RenameField _rename = new();

    // Cross-device actions, built lazily once the input system is up. Add = B / gamepad X(buttonWest),
    // Rename = R / gamepad Y(buttonNorth), Delete = Delete / gamepad R3(rightStickPress).
    private InputAction _add, _renameA, _delete;
    private bool _actionsBuilt;

    private TGB.FreeRoamWarpPoint _renaming; // the point being renamed (held across the edit)
    private BookmarkData _renameData;

    public BookmarkController(BookmarkStore store) => _store = store;

    /// <summary>Drop the prompt rows when gameplay tears down — the eagle-eye subtree we parented into is
    /// destroyed on scene unload, so we dispose our handles and rebuild fresh next load.</summary>
    public void Teardown()
    {
        _prompts.Dispose();
        SetGameInput(true);   // never leave input muted across a teardown
    }

    // Build the three cross-device actions once. Keyboard key is from prefs; gamepad button is fixed per action.
    private void EnsureActions(Key addKey, Key deleteKey, Key renameKey)
    {
        if (_actionsBuilt) return;
        _add     = Glyph.Custom("CairnFreeRoam.Add",    $"<Keyboard>/{KeyName(addKey)}",    "<Gamepad>/buttonWest");
        _renameA = Glyph.Custom("CairnFreeRoam.Rename", $"<Keyboard>/{KeyName(renameKey)}", "<Gamepad>/buttonNorth");
        _delete  = Glyph.Custom("CairnFreeRoam.Delete", $"<Keyboard>/{KeyName(deleteKey)}", "<Gamepad>/rightStickPress");
        _actionsBuilt = _add != null && _renameA != null && _delete != null;
    }

    public void Tick(Key addKey, Key deleteKey, Key renameKey)
    {
        EnsureActions(addKey, deleteKey, renameKey);

        var eye = EagleEye();
        bool inView = eye != null && eye.isInFreeRoamView;

        if (inView) _prompts.Ensure(eye, _add, _renameA, _delete);

        if (_rename.IsEditing) { TickRename(); return; }

        // Safety net: never leave game input muted when not editing (e.g. if the view closed mid-rename).
        SetGameInput(true);

        var selected = inView ? SelectedWarpPoint() : null;
        bool custom = selected != null && _store.IsCustom(selected);
        _prompts.Refresh(eye, inView, deletable: custom, renamable: custom);

        // Keep custom rows showing their stored names (vanilla shows the empty locKey → "[none_string]").
        RowLabels.Apply(List(), _store);

        if (!inView || !_actionsBuilt) return;

        // Same action object that drives each prompt glyph — fires on keyboard OR gamepad.
        if (_add.WasPerformedThisFrame()) AddHere();
        else if (_delete.WasPerformedThisFrame()) DeleteSelected();
        else if (_renameA.WasPerformedThisFrame()) BeginRename();
    }

    // UnityEngine.InputSystem.Key → the control-path key name (lowercase letter, "delete", …).
    private static string KeyName(Key key) => key.ToString().ToLowerInvariant();

    private void AddHere()
    {
        if (!TryGetPlayerPosition(out var pos)) return;
        _store.Add(pos, $"Bookmark {_store.Bookmarks.Count + 1}");
        RebuildList();
    }

    private void DeleteSelected()
    {
        var wp = SelectedWarpPoint();
        if (wp == null || !_store.IsCustom(wp)) return;
        if (_store.Remove(wp)) RebuildList();
    }

    // ---- rename ----

    private void BeginRename()
    {
        var wp = SelectedWarpPoint();
        var data = _store.DataFor(wp);
        if (data == null) return;
        _renaming = wp;
        _renameData = data;
        _rename.Begin("");        // start from an empty field, as requested
        _prompts.Refresh(EagleEye(), true, deletable: false, renamable: false); // hide action prompts during edit

        // Mute ALL game input so typed keys don't drive the game — w/s would navigate the list, Space would
        // fast-travel, etc. onTextInput still feeds the rename buffer (device-level). Restored in EndRename.
        SetGameInput(false);
    }

    private void TickRename()
    {
        SetGameInput(false);       // keep game input muted for the whole edit (idempotent)
        _rename.Update();
        // Repaint the row with the live buffer + caret.
        RowLabels.ApplyEditing(List(), _renaming, _rename.Buffer);

        if (_rename.Committed)
        {
            _store.Rename(_renameData, _rename.Buffer);
            EndRename();
        }
        else if (_rename.Cancelled)
        {
            EndRename();
        }
    }

    private void EndRename()
    {
        _renaming = null;
        _renameData = null;
        SetGameInput(true);       // restore all game input
        RebuildList();            // re-sort/re-row, then names are repainted next Tick
    }

    // While renaming we disable the game's WHOLE input-action asset (PlayerInputActions) so NOTHING the
    // climber/UI listens to fires — w/s list-navigation, Space fast-travel, every letter/number/shift action.
    // The rename buffer is fed by Keyboard.onTextInput, which is a DEVICE-level event independent of input
    // actions, so typing still reaches the field while every game action is muted. Restored on commit/cancel.
    private static InputActionAsset _suppressed;

    private static void SetGameInput(bool enabled)
    {
        if (!enabled)
        {
            if (_suppressed != null) return; // already suppressed
            foreach (var a in Resources.FindObjectsOfTypeAll<InputActionAsset>())
            {
                if (a != null && a.name == "PlayerInputActions" && a.enabled)
                {
                    a.Disable();
                    _suppressed = a;
                    return;
                }
            }
        }
        else if (_suppressed != null)
        {
            _suppressed.Enable();
            _suppressed = null;
        }
    }

    // ---- live UI lookups ----

    private static EagleEyeUI EagleEye()
        => UnityEngine.Object.FindObjectOfType<EagleEyeUI>(true);

    private static CairnUI.FreeRoamEagleEyeWarpPointListUI List()
        => UnityEngine.Object.FindObjectOfType<CairnUI.FreeRoamEagleEyeWarpPointListUI>(true);

    /// <summary>The FreeRoamWarpPoint of the currently-highlighted row, or null.</summary>
    private static TGB.FreeRoamWarpPoint SelectedWarpPoint()
    {
        var list = List();
        if (list == null) return null;
        var rows = list.activeWarpPoints;
        int i = list.cursorPosition;
        if (rows == null || i < 0 || i >= rows.Count) return null;
        var row = rows[i];
        return row != null ? row.WarpPoint : null;
    }

    /// <summary>Rebuild the visible list from the registry so a new/removed point appears/disappears now.</summary>
    private static void RebuildList()
    {
        var list = List();
        if (list != null) list.CreateLocations();
    }

    private static bool TryGetPlayerPosition(out Vector3 pos)
    {
        pos = default;
        if (!PawnManager.MCSpawned) return false;
        var mc = MoSingleton<PawnManager>.Instance?.MCGameObject;
        if (mc == null) return false;
        pos = mc.transform.position;
        return true;
    }
}
