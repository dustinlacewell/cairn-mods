using CairnAPI;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using TGB = Il2CppTheGameBakers.Cairn;
using CairnUI = Il2CppTheGameBakers.Cairn.UI;
using LocKey = Il2CppTGBTools.Localization.LocKeyStringId;

namespace CairnFreeRoam;

/// <summary>
/// Drives bookmark add/delete/rename while the eagle-eye fast-travel view is open.
///  - Add: bookmark the climber's current position as a new warp point (native SortWarps slots it by height).
///  - Delete: remove the selected custom point.
///  - Rename: enter an inline edit on the selected custom point — clears the name, live-types into the row,
///    Enter confirms, Esc cancels.
///
/// Entirely event-driven: the eagle-eye refs are captured from Harmony postfixes on the warp list's
/// OnContentActivated / OnContentDeactivated (open/close). Bookmark names are owned at the source — each point
/// holds a CairnAPI loc key and CairnAPI.Localization owns the LocalizationManager.Get hook that returns the
/// stored name, so the game's own refresh pipeline renders names everywhere; this controller only mutates the
/// name via Localization.Update and rebuilds the list to re-resolve it.
/// When the view is closed the controller holds no refs and does NO per-frame work — TickInput early-returns.
/// While open, TickInput reads the 3 cross-device InputActions and drives the operations. Each action is a
/// CROSS-DEVICE InputAction (keyboard key + gamepad button) built via CairnAPI Glyph.Custom — the SAME action
/// drives both the on-screen glyph (adaptive-swaps KBM↔pad) and the trigger (WasPerformedThisFrame).
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

    // Eagle-eye refs, captured by the Enter/Exit hooks. Non-null ⇔ the free-roam view is open; when null the
    // controller is dormant (zero per-frame work). No FindObjectOfType — the hooks hand us these.
    private EagleEyeUI _eye;
    private CairnUI.FreeRoamEagleEyeWarpPointListUI _list;

    private TGB.FreeRoamWarpPoint _renaming; // the point being renamed (held across the edit)
    private BookmarkData _renameData;
    private LocKey _renameLocKey;            // its CairnAPI loc key — live edits go through Localization.Update
    private string _renameOriginal;          // original label, restored on cancel

    // Key bindings are read from prefs when the actions are first built (on first view open).
    private readonly MelonPreferences_Entry<Key> _addKey, _deleteKey, _renameKey;

    public BookmarkController(
        BookmarkStore store,
        MelonPreferences_Entry<Key> addKey,
        MelonPreferences_Entry<Key> deleteKey,
        MelonPreferences_Entry<Key> renameKey)
    {
        _store = store;
        _addKey = addKey;
        _deleteKey = deleteKey;
        _renameKey = renameKey;
    }

    /// <summary>True while the eagle-eye view is open (refs captured) — Core gates the input tick on this so the
    /// mod does nothing per frame when the menu is closed.</summary>
    public bool ViewOpen => _eye != null;

    // ---- view lifecycle (driven by the ListUI OnContentActivated/Deactivated Harmony postfixes) ----

    /// <summary>Free-roam view opened (the warp list's content was activated): cache the list, resolve the
    /// eagle-eye once (for the prompt line — a one-time scan on this rare open event, not per frame), ensure
    /// actions, build/show prompts.</summary>
    public void OnViewOpened(CairnUI.FreeRoamEagleEyeWarpPointListUI list)
    {
        if (list == null) return;
        _list = list;
        _eye = UnityEngine.Object.FindObjectOfType<EagleEyeUI>(true); // one-time, on open only
        if (_eye == null) { _list = null; return; }
        EnsureActions();
        _prompts.Ensure(_eye, _add, _renameA, _delete);
    }

    /// <summary>Free-roam view closed: dispose prompts, restore input, drop the cached refs (dormant again).</summary>
    public void OnViewClosed()
    {
        if (_rename.IsEditing) EndRename();
        _prompts.Dispose();
        SetGameInput(true);   // never leave input muted across a close
        _eye = null;
        _list = null;
    }

    /// <summary>Drop the prompt rows when gameplay tears down — the eagle-eye subtree we parented into is
    /// destroyed on scene unload, so we dispose our handles and rebuild fresh next load.</summary>
    public void Teardown()
    {
        _prompts.Dispose();
        SetGameInput(true);   // never leave input muted across a teardown
        _eye = null;
        _list = null;
    }

    // Build the three cross-device actions once. Keyboard key is from prefs; gamepad button is fixed per action.
    private void EnsureActions()
    {
        if (_actionsBuilt) return;
        _add     = Glyph.Custom("CairnFreeRoam.Add",    $"<Keyboard>/{KeyName(_addKey.Value)}",    "<Gamepad>/buttonWest");
        _renameA = Glyph.Custom("CairnFreeRoam.Rename", $"<Keyboard>/{KeyName(_renameKey.Value)}", "<Gamepad>/buttonNorth");
        _delete  = Glyph.Custom("CairnFreeRoam.Delete", $"<Keyboard>/{KeyName(_deleteKey.Value)}", "<Gamepad>/rightStickPress");
        _actionsBuilt = _add != null && _renameA != null && _delete != null;
    }

    /// <summary>Per-frame input read — early-returns (one null check) unless the view is open. While open: drive
    /// the active rename, else refresh selection-dependent prompts and fire add/delete/rename on their actions.
    /// No scene scans: selection and rebuild read the cached list ref.</summary>
    public void TickInput()
    {
        if (_eye == null) return; // view closed → dormant

        if (_rename.IsEditing) { TickRename(); return; }

        // Safety net: never leave game input muted when not editing (e.g. if a rename ended abnormally).
        SetGameInput(true);

        var selected = SelectedWarpPoint();
        bool custom = selected != null && _store.IsCustom(selected);
        _prompts.Refresh(_eye, true, deletable: custom, renamable: custom);

        if (!_actionsBuilt) return;

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
        _renameLocKey = _store.LocKeyOf(wp);
        _renameOriginal = data.Label;
        _rename.Begin("");        // start from an empty field, as requested
        _prompts.Refresh(_eye, true, deletable: false, renamable: false); // hide action prompts during edit

        // Mute ALL game input so typed keys don't drive the game — w/s would navigate the list, Space would
        // fast-travel, etc. onTextInput still feeds the rename buffer (device-level). Restored in EndRename.
        SetGameInput(false);
    }

    private void TickRename()
    {
        SetGameInput(false);       // keep game input muted for the whole edit (idempotent)
        _rename.Update();

        // Drive the live edit through CairnAPI.Localization, not the TMP: set this bookmark's name to the buffer
        // + caret, then re-resolve ONLY the editing row (RefreshRow) — NOT a full CreateLocations, which would
        // re-seed the cursor + world pins every keystroke and make the selected mountain pin loop through all
        // points.
        Localization.Update(_renameLocKey, _rename.Buffer + "▏"); // ▏ caret
        RefreshRow(_renaming);

        if (_rename.Committed)
        {
            Localization.Update(_renameLocKey, _rename.Buffer); // drop the caret
            _store.Rename(_renameData, _rename.Buffer);      // persist to bookmarks.json
            EndRename();
        }
        else if (_rename.Cancelled)
        {
            Localization.Update(_renameLocKey, _renameOriginal); // restore the pre-edit name
            EndRename();
        }
    }

    private void EndRename()
    {
        _renaming = null;
        _renameData = null;
        SetGameInput(true);       // restore all game input
        RebuildList();            // re-resolve every row through Get with the final names
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

    // ---- cached UI lookups (no scene scans; refs supplied by the Enter/Exit hooks) ----

    /// <summary>The FreeRoamWarpPoint of the currently-highlighted row, or null.</summary>
    private TGB.FreeRoamWarpPoint SelectedWarpPoint()
    {
        if (_list == null) return null;
        var rows = _list.activeWarpPoints;
        int i = _list.cursorPosition;
        if (rows == null || i < 0 || i >= rows.Count) return null;
        var row = rows[i];
        return row != null ? row.WarpPoint : null;
    }

    /// <summary>Rebuild the visible list from the registry so a new/removed/renamed point updates now. The native
    /// CreateLocations re-binds every row; each name cell re-resolves through LocalizationManager.Get, where our
    /// prefix returns the bookmark's current name — no per-row stamping.</summary>
    private void RebuildList()
    {
        if (_list != null) _list.CreateLocations();
    }

    /// <summary>Re-resolve ONLY the row that owns <paramref name="wp"/>, without rebuilding the whole list.
    /// CreateLocations re-seeds the cursor + world pins, so calling it per-keystroke during a live rename makes
    /// the selected pin loop through every point. Instead we re-bind just the editing row's LocalizedText with
    /// its own key — that re-pulls through LocalizationManager.Get (→ our buffer+caret) and repaints that one
    /// cell, leaving cursor and pins untouched.</summary>
    private void RefreshRow(TGB.FreeRoamWarpPoint wp)
    {
        if (_list == null || wp == null) return;
        var rows = _list.activeWarpPoints;
        if (rows == null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || row.WarpPoint == null || row.WarpPoint.Pointer != wp.Pointer) continue;
            var loc = row.locationText;
            if (loc != null)
            {
                var key = wp.LocKey;                 // ParametrizedLocKey (carries our unique locKey id)
                loc.ChangeLocKey(ref key);           // re-resolve → Get → our current dict name (+caret)
            }
            return;
        }
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
