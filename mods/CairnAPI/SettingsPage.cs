using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTGBTools.UI;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static Il2CppInterop.Runtime.IL2CPP;

namespace CairnAPI;

// A managed page inside the game's native SettingsMenu, reached through a custom
// Settings-rail entry (see CairnMenu.Settings.AddPage). The page is backed by the
// menu's own fieldsUI; SettingsPage encapsulates the SettingsMenu-specific machinery
// that a custom page needs but the game never anticipated:
//   - a stub SettingsPageButton (null settingsPage — no managed subclassing possible)
//   - fieldsUI.Add / Activate / RemoveAll population
//   - null guards on CloseSettingsPage / Return / OnFieldSelected / OnFieldChanged
//     (the native bodies dereference settingsPage unconditionally and abort on null)
//   - the rail-button visual (Animator + SettingsPageButton) so the entry slides /
//     shows its chevron exactly like the game's own category buttons
//   - dropdown stay-open, scroll-gap, and pointer-exit fixes for our rows
//
// Content is set by the builder passed to AddPage and can be swapped live via
// Refresh() / SetFields() — used to turn one page into a multi-section view.
public sealed class SettingsPage
{
    internal readonly string        _label;
    internal readonly Func<Field[]> _build;

    // Rail button SPB — added to this page's rail GO in AfterOpened; ToggleSettingPageButton drives it.
    internal SettingsPageButton _railSpb;
    // currentSettingsPageButton stub — null settingsPage, kept separate from the rail visual.
    internal SettingsPageButton _stubSpb;
    internal Image              _stubBackground;
    // Pointers of FieldUI rows belonging to this page — used by the row-gated fixes.
    internal readonly HashSet<IntPtr> _rows = new();

    private IDisposable _railEntry;

    internal SettingsPage(string label, Func<Field[]> build)
    {
        _label = label;
        _build = build;
    }

    public string Label  => _label;
    public bool   IsOpen => SettingsPages.Active == this;

    /// <summary>Re-run the page builder and show the result without leaving the page.</summary>
    public void Refresh()
    {
        if (IsOpen) SettingsPages.DeferSwap(this, _build());
    }

    /// <summary>
    /// Replace the page's visible rows with <paramref name="fields"/> without leaving the page.
    /// Safe to call from inside a field callback — the swap is deferred to the next frame so the
    /// row that raised the event is not torn down mid-dispatch.
    /// </summary>
    public void SetFields(Field[] fields)
    {
        if (IsOpen) SettingsPages.DeferSwap(this, fields);
    }

    // ── internal lifecycle (driven by SettingsPages) ──────────────────────────

    internal void BindRailEntry(IDisposable handle) => _railEntry = handle;

    internal void ResetOpenState()
    {
        _railSpb        = null;
        _stubSpb        = null;
        _stubBackground = null;
        _rows.Clear();
    }

    /// <summary>Remove the rail entry. Pages normally live for the mod's lifetime.</summary>
    public void Dispose()
    {
        _railEntry?.Dispose();
        _railEntry = null;
        SettingsPages.Unregister(this);
    }
}

// Static coordinator: owns the registered pages, the Harmony patches on SettingsMenu /
// FieldsUI / FieldUI / FieldUIListDropdown, and the single "currently open page" state.
// Only one settings page is ever open at a time, so open-state tracking is a single slot.
[HarmonyPatch]
internal static class SettingsPages
{
    private static readonly List<SettingsPage> _pages = new();

    private static SettingsMenu _menu;   // current menu (set in OnOpened)
    private static SettingsPage _active; // currently open page, or null

    internal static SettingsPage Active => _active;

    internal static void Install(HarmonyLib.Harmony harmony) => harmony.PatchAll(typeof(SettingsPages));

    // Called by CairnMenu.Settings.AddPage. Registers the page and its rail entry; the
    // entry's click opens the page through the native page machinery.
    internal static SettingsPage Register(string label, Func<Field[]> buildFields)
    {
        var page = new SettingsPage(label, buildFields);
        page.BindRailEntry(CairnMenu.Settings.AddEntry(label, () => Open(page)));
        _pages.Add(page);
        return page;
    }

    internal static void Unregister(SettingsPage page) => _pages.Remove(page);

    internal static bool OwnsRow(IntPtr ptr) => _active != null && _active._rows.Contains(ptr);

    // ── Page open ─────────────────────────────────────────────────────────────

    // Mirrors OpenSettingsPage: deactivate bouncing buttons, close any current page,
    // populate our fields, set currentSettingsPageButton, enable categoryReturnClickArea,
    // and toggle the rail button's visual state via ToggleSettingPageButton.
    static void Open(SettingsPage page)
    {
        if (_menu == null) return;
        try
        {
            // Lock the rail button into Selected state BEFORE Toggle(false) fires.
            // Toggle(false) → set_interactable(false) → DoStateTransition(Disabled) → SetTrigger("Disabled").
            // With KeepSelected=true already set, the Selected→Disabled transition is blocked and the
            // animator stays in Selected state — exactly what native OpenSettingsPage achieves.
            if (page._railSpb != null)
                _menu.ToggleSettingPageButton(page._railSpb, true);

            // Mirrors OpenSettingsPage steps 1-2: deactivate hover arrow + disable nav buttons.
            var baz = _menu.bouncingButtons?.BouncingArrowZone;
            if (baz != null) baz.Deactivate(false);
            _menu.bouncingButtons?.Toggle(false);

            if (_menu.currentSettingsPageButton != null)
                _menu.CloseSettingsPage(false);

            // Build the stub SPB that currentSettingsPageButton points to.
            // Kept separate from the rail button — this is just for Return()/Cancel tracking.
            // settingsPage intentionally null; our BeforeCloseSettingsPage prefix handles teardown.
            if (page._stubSpb == null)
            {
                var go = new GameObject($"{page._label}PageStub");
                go.transform.SetParent(_menu.transform, false);
                go.SetActive(false);
                page._stubBackground = go.AddComponent<Image>();
                page._stubBackground.color   = new Color(0f, 0f, 0f, 0f);
                page._stubBackground.enabled = false;
                var spb = go.AddComponent<SettingsPageButton>();
                spb.settingsMenu = _menu;
                spb.background   = page._stubBackground;
                page._stubSpb = spb;
                go.SetActive(true);
            }

            _active = page;
            Populate(page, page._build());

            _menu.currentSettingsPageButton = page._stubSpb;

            if (_menu.categoryReturnClickArea != null)
                _menu.categoryReturnClickArea.enabled = true;

            // One frame later: re-lock the animator/image state AND re-bounce the label.
            // Both get clobbered after Activate(): EventSystem focus on a field row fires
            // OnDeselect on the rail BWME (resets TMP alpha to 1.0), and baz.Deactivate()
            // fires OnDeselectButton (tweens label Y back to 0, covering the chevron).
            if (page._railSpb != null)
            {
                var pageRef = page;
                var smRef   = _menu;
                var bbRef   = _menu.bouncingButtons;
                var selRef  = page._railSpb.GetComponent<Selectable>();
                MelonCoroutines.Start(ReapplySelectedVisualNextFrame(smRef, pageRef, bbRef, selRef));
            }
        }
        catch (Exception ex) { MelonLogger.Error($"[SettingsPage] Open '{page._label}' failed: {ex}"); }
    }

    // Clear the current rows and add a fresh set, tracking which rows are ours for the
    // row-gated fixes. Shared by Open and the live swap (Refresh/SetFields).
    static void Populate(SettingsPage page, Field[] fields)
    {
        var fieldsUI = _menu?.fieldsUI;
        if (fieldsUI == null) return;

        fieldsUI.RemoveAll();
        page._rows.Clear();

        if (fields != null)
            foreach (var field in fields)
                if (field != null) fieldsUI.Add(field);

        var insts = fieldsUI.Instances;
        if (insts != null)
            for (int i = 0; i < insts.Count; i++)
                if (insts[i] != null) page._rows.Add(insts[i].Pointer);

        fieldsUI.Activate();

        // Anchor EventSystem focus on the first row, exactly as native OpenSettingsPage does
        // (FindSelectable(0,false).Select()). Without a selected row inside the page, a controller
        // has nothing to move from and can't navigate the fields.
        fieldsUI.SelectFirst();
    }

    // Deferred live swap — runs Populate on the next frame so the field callback that
    // requested the swap (e.g. a dropdown's OnValueChanged) finishes before its row is torn down.
    internal static void DeferSwap(SettingsPage page, Field[] fields)
        => MelonCoroutines.Start(SwapNextFrame(page, fields));

    private static System.Collections.IEnumerator SwapNextFrame(SettingsPage page, Field[] fields)
    {
        yield return null;
        if (_active != page) yield break;
        try { Populate(page, fields); }
        catch (Exception ex) { MelonLogger.Error($"[SettingsPage] swap '{page._label}' failed: {ex}"); }
    }

    // token 100702234 = BouncingButtons.OnSelectButton(Selectable) — private
    private static IntPtr _onSelectButtonMethod = IntPtr.Zero;

    // unsafe helper — cannot use unsafe blocks inside iterator methods
    private static unsafe void CallOnSelectButton(BouncingButtons bb, Selectable sel)
    {
        if (_onSelectButtonMethod == IntPtr.Zero)
            _onSelectButtonMethod = GetIl2CppMethodByToken(
                Il2CppClassPointerStore<BouncingButtons>.NativeClassPtr, 100702234);
        if (_onSelectButtonMethod == IntPtr.Zero) return;

        IntPtr exc = IntPtr.Zero;
        void* arg  = (void*)Il2CppObjectBaseToPtr(sel);
        il2cpp_runtime_invoke(_onSelectButtonMethod, Il2CppObjectBaseToPtr(bb), &arg, ref exc);
        if (exc != IntPtr.Zero)
            MelonLogger.Warning("[SettingsPage] OnSelectButton invoke threw.");
    }

    // After fieldsUI.Activate(), EventSystem selects the first field row which fires
    // OnDeselect on the rail BWME → TriggerAnimation("Normal") → Normal clip runs →
    // TMP.color.alpha resets to 1.0 (opaque) → covers the chevron.
    // Also, baz.Deactivate() fires OnDeselectButton on the rail selectable → label Y
    // tweens back to baseOffset (0), covering the chevron area with the button text.
    //
    // One frame later: re-lock the animator/image via ToggleSettingPageButton AND
    // re-call OnSelectButton to bounce the label back up.
    private static System.Collections.IEnumerator ReapplySelectedVisualNextFrame(
        SettingsMenu sm, SettingsPage page, BouncingButtons bb, Selectable railSelectable)
    {
        yield return null;  // one frame — let EventSystem deselect settle
        try
        {
            if (sm == null || page._railSpb == null || page._stubSpb == null
                || sm.currentSettingsPageButton?.Pointer != page._stubSpb.Pointer)
                yield break;

            // Re-lock the selected visual (Image.enabled=true, KeepSelected=true).
            sm.ToggleSettingPageButton(page._railSpb, true);

            // Re-bounce the label upward. Mirrors what BouncingArrowZone.Update() does
            // when the rail selectable has EventSystem focus — can't use Select() because
            // interactable=false after Toggle(false).
            if (bb != null && railSelectable != null)
                CallOnSelectButton(bb, railSelectable);
        }
        catch (Exception ex) { MelonLogger.Error($"[SettingsPage] ReapplySelectedVisual failed: {ex}"); }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SettingsMenu), "OnOpened")]
    [HarmonyPostfix]
    static void AfterOpened(SettingsMenu __instance)
    {
        _menu   = __instance;
        _active = null;
        foreach (var page in _pages) page.ResetOpenState();

        // Wire each page's rail button GO into the native visual system.
        // Native GOs at Main/SettingsPageButtons/<name> have: SettingsPageButton + Image + Animator.
        // Our GOs have only Image + ButtonWithMoreEvents — we add Animator + SettingsPageButton here
        // so ToggleSettingPageButton can drive the slide/chevron/background exactly as native buttons.
        var spbParent = __instance.transform.Find("Main/SettingsPageButtons");
        if (spbParent == null) return;
        foreach (var page in _pages)
            WireRailVisual(__instance, spbParent, page);
    }

    static void WireRailVisual(SettingsMenu sm, Transform spbParent, SettingsPage page)
    {
        try
        {
            var pageT = spbParent.Find(page._label);
            if (pageT == null) return;
            var pageGo = pageT.gameObject;

            // Get the shared RuntimeAnimatorController from any sibling native button GO.
            // Must use go.GetComponent<Animator>() — SettingsPageButton.animator is a lazy-cached
            // backing field that is null until get_Animator() is first called.
            RuntimeAnimatorController controller = null;
            for (int i = 0; i < spbParent.childCount; i++)
            {
                var child = spbParent.GetChild(i).gameObject;
                if (child == pageGo) continue;
                var a = child.GetComponent<Animator>();
                if (a != null && a.runtimeAnimatorController != null)
                    { controller = a.runtimeAnimatorController; break; }
            }

            if (controller == null)
            {
                MelonLogger.Warning($"[SettingsPage] No native SPB animator found — '{page._label}' button won't animate.");
                return;
            }

            // Add Animator to the rail GO with the same controller and update mode as native buttons.
            // Native SPB animators use UnscaledTime — the settings menu opens while the game is paused.
            var railAnimator = pageGo.AddComponent<Animator>();
            railAnimator.runtimeAnimatorController = controller;
            railAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;

            // Copy the selected-feedback sprite and color from a native sibling button.
            // The sprite (UI_PauseMenu_SelectedFeedback) contains both the grey background and
            // the mountain chevron shape — without it the Image renders a plain invisible rect.
            // ToggleSettingPageButton enables/disables this Image to show/hide the selected state.
            var railImg = pageGo.GetComponent<Image>();
            if (railImg != null)
            {
                for (int i = 0; i < spbParent.childCount; i++)
                {
                    var sibling = spbParent.GetChild(i).gameObject;
                    if (sibling == pageGo) continue;
                    var sibImg = sibling.GetComponent<Image>();
                    if (sibImg?.sprite != null)
                    {
                        railImg.sprite = sibImg.sprite;
                        railImg.color  = sibImg.color;
                        break;
                    }
                }
            }

            // Add SettingsPageButton — the native component ToggleSettingPageButton operates on.
            // Pre-populate spb.background so ToggleSettingPageButton finds the correct Image
            // (the one with the sprite already set) without relying on GetComponent<Image>().
            // settingsPage is left null — we don't route clicks through SPB.OnClick.
            var spb = pageGo.AddComponent<SettingsPageButton>();
            spb.settingsMenu = sm;
            spb.background   = railImg;
            page._railSpb = spb;
        }
        catch (Exception ex) { MelonLogger.Error($"[SettingsPage] rail setup for '{page._label}' failed: {ex}"); }
    }

    [HarmonyPatch(typeof(SettingsMenu), "OnClosed")]
    [HarmonyPostfix]
    static void AfterClosed(SettingsMenu __instance)
    {
        foreach (var page in _pages) page.ResetOpenState();
        _menu   = null;
        _active = null;
    }

    // ── CloseSettingsPage null guard ──────────────────────────────────────────

    // CloseSettingsPage crashes when currentSettingsPageButton.settingsPage==null:
    // ToggleSettingPageButton runs unconditionally on the SPB, then the settingsPage==null
    // guard means currentSettingsPageButton is never nulled and fieldsUI.RemoveAll never fires.
    // When our stub is current, handle teardown and skip the native body.
    [HarmonyPatch(typeof(SettingsMenu), "CloseSettingsPage")]
    [HarmonyPrefix]
    static bool BeforeCloseSettingsPage(SettingsMenu __instance) => !CloseActiveIfCurrent(__instance);

    // ── Return / Cancel interception ─────────────────────────────────────────

    // Return() and CategoryReturn() (same body; CategoryReturn is what right-click fires)
    // both read currentSettingsPageButton BEFORE calling CloseSettingsPage, then jump to
    // a MenuCancelPerformed handler that closes the whole settings menu. When our stub is
    // current we must intercept early: close the active page ourselves and skip the native body.
    [HarmonyPatch(typeof(SettingsMenu), "Return")]
    [HarmonyPrefix]
    static bool BeforeReturn(SettingsMenu __instance) => !CloseActiveIfCurrent(__instance);

    [HarmonyPatch(typeof(SettingsMenu), "CategoryReturn")]
    [HarmonyPrefix]
    static bool BeforeCategoryReturn(SettingsMenu __instance) => !CloseActiveIfCurrent(__instance);

    // Full active-page teardown: remove fields, toggle rail visual off, disable return area,
    // restore bouncing buttons. Returns true if our page was current (and we handled the close).
    static bool CloseActiveIfCurrent(SettingsMenu sm)
    {
        var page = _active;
        if (page?._stubSpb == null) return false;
        if (sm.currentSettingsPageButton?.Pointer != page._stubSpb.Pointer) return false;
        try
        {
            sm.currentSettingsPageButton = null;
            if (page._stubBackground != null) page._stubBackground.enabled = false;
            if (page._railSpb != null) sm.ToggleSettingPageButton(page._railSpb, false);
            if (sm.categoryReturnClickArea != null) sm.categoryReturnClickArea.enabled = false;
            sm.fieldsUI?.RemoveAll();
            page._rows.Clear();
            sm.ActivateBouncingButtons();

            // Native Return re-selects the page's rail button so a controller stays anchored on the
            // rail; without it EventSystem focus is left on the now-destroyed field rows and the rail
            // is "stuck". Must run AFTER ActivateBouncingButtons re-enables interactable — a
            // non-interactable Selectable rejects Select(). Use the rail GO's ButtonWithMoreEvents
            // (the SettingsPageButton's own button backing field can be lazy-null).
            var railBtn = page._railSpb != null
                ? page._railSpb.gameObject.GetComponent<Selectable>() : null;
            if (railBtn != null && railBtn.interactable) railBtn.Select();
        }
        catch (Exception ex) { MelonLogger.Error($"[SettingsPage] CloseActive failed: {ex}"); }
        _active = null;
        return true;
    }

    // ── OnFieldSelected / OnFieldChanged null guard ───────────────────────────

    // Both methods dereference currentSettingsPageButton->settingsPage unconditionally
    // (hitting the IL2CPP abort / swi(3) path when settingsPage == null). Skip them
    // when our stub (settingsPage==null) is the current page.
    [HarmonyPatch(typeof(SettingsMenu), "OnFieldSelected", typeof(Field))]
    [HarmonyPrefix]
    static bool BeforeOnFieldSelected(SettingsMenu __instance)
        => _active?._stubSpb == null || __instance.currentSettingsPageButton?.Pointer != _active._stubSpb.Pointer;

    [HarmonyPatch(typeof(SettingsMenu), "OnFieldChanged", typeof(Field))]
    [HarmonyPrefix]
    static bool BeforeOnFieldChanged(SettingsMenu __instance)
        => _active?._stubSpb == null || __instance.currentSettingsPageButton?.Pointer != _active._stubSpb.Pointer;

    // ── FieldUIListDropdown stay-open fix ─────────────────────────────────────

    // FieldUIListDropdown.Update() hides the dropdown whenever EventSystem.currentSelected
    // isn't the TMP_Dropdown's own GameObject. But when the dropdown opens, TMP_Dropdown
    // calls Select() on a child Toggle item, not on its own GO — so GetComponent<TMP_Dropdown>
    // on currentSelected returns null and Hide() fires every frame.
    //
    // Fix: run the FieldUI base Update for animation/selection tracking, then skip the native
    // hide-check while the dropdown is expanded (currentSelected is a child Toggle inside it).
    [HarmonyPatch(typeof(FieldUIListDropdown), "Update")]
    [HarmonyPrefix]
    static bool ReplaceDropdownUpdate(FieldUIListDropdown __instance)
    {
        // Only intercept rows on our active page — let native handle the game's own pages.
        if (!OwnsRow(__instance.Pointer)) return true;

        var dd = __instance.dropdown;
        if (dd == null || !dd.IsExpanded) return true;

        // Dropdown IS expanded. Run FieldUI.Update (base) for animation/selection tracking,
        // then skip the native hide-check by returning false.
        if (_fieldUIUpdateMethod == IntPtr.Zero)
            _fieldUIUpdateMethod = GetIl2CppMethodByToken(
                Il2CppClassPointerStore<FieldUI>.NativeClassPtr, 100663697);
        if (_fieldUIUpdateMethod != IntPtr.Zero)
        {
            try
            {
                unsafe
                {
                    IntPtr exc = IntPtr.Zero;
                    il2cpp_runtime_invoke(_fieldUIUpdateMethod, Il2CppObjectBaseToPtr(__instance), null, ref exc);
                }
            }
            catch { /* ignore — animation only */ }
        }

        // currentSelected is a child Toggle inside m_Dropdown; leave the dropdown open.
        return false;
    }

    // FieldUI.Update method pointer — lazily cached; token 100663697 from FieldUI/_Type.cs.
    private static IntPtr _fieldUIUpdateMethod = IntPtr.Zero;

    // ── Scroll gap fix ───────────────────────────────────────────────────────

    // FieldsUI rows each have a FieldEventHandler (IScrollHandler) on their GO.
    // Gaps between rows have no raycast target, so scroll events in those gaps fall
    // through. Fix: add a transparent Image to the FieldsUI GO (makes it a raycast
    // target covering all gap pixels) and add a FieldEventHandler with owner set to
    // the first row's FieldUI — so IScrollHandler.OnScroll fires and chains through
    // owner.OnScrolled → FieldsUI.OnFieldScrolled → fieldsUI.scrollRect.OnScroll(ped).
    [HarmonyPatch(typeof(FieldsUI), "Activate")]
    [HarmonyPostfix]
    static void AfterFieldsUIActivate(FieldsUI __instance)
    {
        try
        {
            // Only patch our own fieldsUI instance.
            if (_menu == null) return;
            var myFieldsUI = _menu.fieldsUI;
            if (myFieldsUI == null || __instance.Pointer != myFieldsUI.Pointer) return;

            var go = __instance.gameObject;

            // Idempotent: if we already added our FieldEventHandler with a valid owner, skip.
            // If it exists but owner is null (from a prior session), fall through to set it.
            var existing = go.GetComponent(Il2CppType.Of<FieldEventHandler>())
                             ?.TryCast<FieldEventHandler>();
            if (existing != null && existing.owner != null) return;

            // Find a row FieldUI to use as the owner — its OnScrolled is already
            // subscribed to by FieldsUI.SetCallbacks, so scroll events chain correctly.
            var instances = __instance.Instances;
            if (instances == null || instances.Count == 0) return;
            FieldUI ownerFieldUI = null;
            for (int i = 0; i < instances.Count; i++)
            {
                var row = instances[i];
                if (row != null) { ownerFieldUI = row; break; }
            }
            if (ownerFieldUI == null) return;

            // A transparent Image makes the FieldsUI GO a raycast target so Unity's
            // EventSystem delivers scroll events that land in inter-row gaps to us.
            var imgComp = go.GetComponent(Il2CppType.Of<Image>())?.TryCast<Image>();
            if (imgComp == null)
            {
                imgComp = go.AddComponent(Il2CppType.Of<Image>())?.TryCast<Image>();
                if (imgComp == null) return;
                imgComp.color         = new Color(0, 0, 0, 0);
                imgComp.raycastTarget = true;
            }
            else
            {
                imgComp.raycastTarget = true;
            }

            // FieldEventHandler is the native IScrollHandler component on each row.
            // Adding it to the FieldsUI GO gives the GO a working IScrollHandler.
            var feh = existing ?? go.AddComponent(Il2CppType.Of<FieldEventHandler>())?.TryCast<FieldEventHandler>();
            if (feh == null) return;
            feh.owner = ownerFieldUI;
        }
        catch (Exception ex) { MelonLogger.Error($"[SettingsPage] AfterFieldsUIActivate failed: {ex}"); }
    }

    // ── OnPointerExit fix ─────────────────────────────────────────────────────

    // FieldUI.OnPointerExit is a no-op in Select mode (gamepad design).
    // For our mouse-driven rows, manually deselect and reset both the slide and chevron.
    // wasSelected is unreliable from managed code (struct-offset mismatch in the IL2Cpp
    // reconstruction), so RefreshAnimator() must be called explicitly here.
    [HarmonyPatch(typeof(FieldUI), "OnPointerExit", typeof(PointerEventData))]
    [HarmonyPostfix]
    static void AfterOnPointerExit(FieldUI __instance)
    {
        if (!Cursor.visible) return;
        if (!OwnsRow(__instance.Pointer)) return;
        try
        {
            var es = EventSystem.current;
            if (es != null) es.SetSelectedGameObject(null);
            __instance.OnSelectedChange(false);
            __instance.RefreshAnimator();
        }
        catch (Exception ex) { MelonLogger.Error($"[SettingsPage] AfterOnPointerExit failed: {ex}"); }
    }
}
