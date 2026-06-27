using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTGBTools.UI;
using FieldInfo = Il2CppTGBTools.UI.FieldInfo;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static Il2CppInterop.Runtime.IL2CPP;

namespace CairnAPI;

// Surfaces ModOptions registry entries as a "Mods" page inside the SettingsMenu.
//
// Uses CairnMenu.Settings.AddEntry for the nav-rail button. Handles the settings
// page open/close flow itself because it requires SettingsMenu-specific machinery:
//   - SettingsPageButton (null settingsPage — no managed subclassing possible)
//   - fieldsUI.Add / Activate / RemoveAll
//   - CloseSettingsPage null guard (crashes on null settingsPage without this prefix)
//   - OnPointerExit fix (Select mode leaves rows stuck; we manually deselect on exit)
[HarmonyPatch]
internal static class ModOptionsHook
{
    // Rail handle lives for the mod's lifetime — never disposed.
    private static IDisposable        _railEntry;
    // Rail button SPB — added to the Mods GO in AfterOpened; ToggleSettingPageButton drives it.
    private static SettingsPageButton _railSpb;
    // currentSettingsPageButton stub — null settingsPage, kept separate from the rail visual.
    private static SettingsPageButton _stubSpb;
    private static Image              _stubBackground;
    private static SettingsMenu       _settingsMenu;

    // Pointers of FieldUI rows belonging to our page — used by AfterOnPointerExit.
    private static readonly HashSet<IntPtr> _modPageRows = new();

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        _railEntry = CairnMenu.Settings.AddEntry("Mods", OnModsButtonClicked);
        harmony.PatchAll(typeof(ModOptionsHook));
    }

    // ── Settings page open ────────────────────────────────────────────────────

    // Called when the CairnMenu.Settings rail entry is clicked.
    // Mirrors OpenSettingsPage: deactivate bouncing buttons, close any current page,
    // populate our fields, set currentSettingsPageButton, enable categoryReturnClickArea,
    // and toggle the rail button's visual state via ToggleSettingPageButton.
    static void OnModsButtonClicked()
    {
        if (_settingsMenu == null) return;
        try
        {
            // Lock our rail button into Selected state BEFORE Toggle(false) fires.
            // Toggle(false) → set_interactable(false) → DoStateTransition(Disabled) → SetTrigger("Disabled").
            // With KeepSelected=true already set, the Selected→Disabled transition is blocked and the
            // animator stays in Selected state — exactly what native OpenSettingsPage achieves.
            if (_railSpb != null)
                _settingsMenu.ToggleSettingPageButton(_railSpb, true);

            // Mirrors OpenSettingsPage steps 1-2: deactivate hover arrow + disable nav buttons.
            var baz = _settingsMenu.bouncingButtons?.BouncingArrowZone;
            if (baz != null) baz.Deactivate(false);
            _settingsMenu.bouncingButtons?.Toggle(false);

            if (_settingsMenu.currentSettingsPageButton != null)
                _settingsMenu.CloseSettingsPage(false);

            // Build the stub SPB that currentSettingsPageButton points to.
            // Kept separate from the rail button — this is just for Return()/Cancel tracking.
            // settingsPage intentionally null; our BeforeCloseSettingsPage prefix handles teardown.
            if (_stubSpb == null)
            {
                var go = new GameObject("ModsPageStub");
                go.transform.SetParent(_settingsMenu.transform, false);
                go.SetActive(false);
                _stubBackground = go.AddComponent<Image>();
                _stubBackground.color   = new Color(0f, 0f, 0f, 0f);
                _stubBackground.enabled = false;
                var spb = go.AddComponent<SettingsPageButton>();
                spb.settingsMenu = _settingsMenu;
                spb.background   = _stubBackground;
                _stubSpb = spb;
                go.SetActive(true);
            }

            var fieldsUI = _settingsMenu.fieldsUI;
            if (fieldsUI != null)
            {
                _modPageRows.Clear();
                int countBefore = fieldsUI.Instances?.Count ?? 0;
                foreach (var field in BuildFields())
                    if (field != null) fieldsUI.Add(field);
                var insts = fieldsUI.Instances;
                if (insts != null)
                    for (int i = countBefore; i < insts.Count; i++)
                        if (insts[i] != null) _modPageRows.Add(insts[i].Pointer);
                fieldsUI.Activate();
            }

            _settingsMenu.currentSettingsPageButton = _stubSpb;

            if (_settingsMenu.categoryReturnClickArea != null)
                _settingsMenu.categoryReturnClickArea.enabled = true;

            // One frame later: re-lock the animator/image state AND re-bounce the label.
            // Both get clobbered after Activate(): EventSystem focus on a field row fires
            // OnDeselect on the Mods BWME (resets TMP alpha to 1.0), and baz.Deactivate()
            // fires OnDeselectButton (tweens label Y back to 0, covering the chevron).
            if (_railSpb != null)
            {
                var spbRef   = _railSpb;
                var smRef    = _settingsMenu;
                var bbRef    = _settingsMenu.bouncingButtons;
                var selRef   = _railSpb.GetComponent<Selectable>();
                MelonLoader.MelonCoroutines.Start(
                    ReapplySelectedVisualNextFrame(smRef, spbRef, bbRef, selRef));
            }
        }
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] OnModsButtonClicked failed: {ex}"); }
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
            MelonLogger.Warning("[ModOptions] OnSelectButton invoke threw.");
    }

    // After fieldsUI.Activate(), EventSystem selects the first field row which fires
    // OnDeselect on the Mods BWME → TriggerAnimation("Normal") → Normal clip runs →
    // TMP.color.alpha resets to 1.0 (opaque) → covers the chevron.
    // Also, baz.Deactivate() fires OnDeselectButton on the Mods selectable → label Y
    // tweens back to baseOffset (0), covering the chevron area with the button text.
    //
    // One frame later: re-lock the animator/image via ToggleSettingPageButton AND
    // re-call OnSelectButton to bounce the label back up.
    private static System.Collections.IEnumerator ReapplySelectedVisualNextFrame(
        SettingsMenu sm, SettingsPageButton spb, BouncingButtons bb, Selectable modsSelectable)
    {
        yield return null;  // one frame — let EventSystem deselect settle
        try
        {
            if (sm == null || spb == null || _stubSpb == null
                || sm.currentSettingsPageButton?.Pointer != _stubSpb.Pointer)
                yield break;

            // Re-lock the selected visual (Image.enabled=true, KeepSelected=true).
            sm.ToggleSettingPageButton(spb, true);

            // Re-bounce the label upward. Mirrors what BouncingArrowZone.Update() does
            // when the Mods selectable has EventSystem focus — can't use Select() because
            // interactable=false after Toggle(false).
            if (bb != null && modsSelectable != null)
                CallOnSelectButton(bb, modsSelectable);
        }
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] ReapplySelectedVisual failed: {ex}"); }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SettingsMenu), "OnOpened")]
    [HarmonyPostfix]
    static void AfterOpened(SettingsMenu __instance)
    {
        _settingsMenu = __instance;
        _stubSpb      = null;
        _stubBackground = null;
        _railSpb      = null;

        // Wire the Mods rail button GO into the native visual system.
        // Native GOs at Main/SettingsPageButtons/<name> have: SettingsPageButton + Image + Animator.
        // Our GO has only Image + ButtonWithMoreEvents — we add Animator + SettingsPageButton here
        // so ToggleSettingPageButton can drive the slide/chevron/background exactly as native buttons.
        try
        {
            var spbParent = __instance.transform.Find("Main/SettingsPageButtons");
            if (spbParent == null) return;
            var modsT = spbParent.Find("Mods");
            if (modsT == null) return;
            var modsGo = modsT.gameObject;

            // Get the shared RuntimeAnimatorController from any sibling native button GO.
            // Must use go.GetComponent<Animator>() — SettingsPageButton.animator is a lazy-cached
            // backing field that is null until get_Animator() is first called.
            RuntimeAnimatorController controller = null;
            for (int i = 0; i < spbParent.childCount; i++)
            {
                var child = spbParent.GetChild(i).gameObject;
                if (child == modsGo) continue;
                var a = child.GetComponent<Animator>();
                if (a != null && a.runtimeAnimatorController != null)
                    { controller = a.runtimeAnimatorController; break; }
            }

            if (controller == null)
            {
                MelonLogger.Warning("[ModOptions] No native SPB animator found — Mods button won't animate.");
                return;
            }

            // Add Animator to the Mods GO with the same controller and update mode as native buttons.
            // Native SPB animators use UnscaledTime — the settings menu opens while the game is paused.
            var railAnimator = modsGo.AddComponent<Animator>();
            railAnimator.runtimeAnimatorController = controller;
            railAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;

            // Copy the selected-feedback sprite and color from a native sibling button.
            // The sprite (UI_PauseMenu_SelectedFeedback) contains both the grey background and
            // the mountain chevron shape — without it the Image renders a plain invisible rect.
            // ToggleSettingPageButton enables/disables this Image to show/hide the selected state.
            var railImg = modsGo.GetComponent<Image>();
            if (railImg != null)
            {
                for (int i = 0; i < spbParent.childCount; i++)
                {
                    var sibling = spbParent.GetChild(i).gameObject;
                    if (sibling == modsGo) continue;
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
            var spb = modsGo.AddComponent<SettingsPageButton>();
            spb.settingsMenu = __instance;
            spb.background   = railImg;
            _railSpb = spb;
        }
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] AfterOpened rail setup failed: {ex}"); }
    }

    [HarmonyPatch(typeof(SettingsMenu), "OnClosed")]
    [HarmonyPostfix]
    static void AfterClosed(SettingsMenu __instance)
    {
        _stubSpb        = null;
        _stubBackground = null;
        _railSpb        = null;
        _settingsMenu   = null;
        _modPageRows.Clear();
    }

    // ── CloseSettingsPage null guard ──────────────────────────────────────────

    // CloseSettingsPage crashes when currentSettingsPageButton.settingsPage==null:
    // ToggleSettingPageButton runs unconditionally on the SPB, then the settingsPage==null
    // guard means currentSettingsPageButton is never nulled and fieldsUI.RemoveAll never fires.
    // When our stub is current, handle teardown and skip the native body.
    [HarmonyPatch(typeof(SettingsMenu), "CloseSettingsPage")]
    [HarmonyPrefix]
    static bool BeforeCloseSettingsPage(SettingsMenu __instance)
    {
        // CloseModsPageIfCurrent nulls currentSettingsPageButton BEFORE the native body sees it,
        // so the native body's first check (currentSettingsPageButton == null → early return) fires.
        // Return false to skip native entirely when we handled it.
        return !CloseModsPageIfCurrent(__instance);
    }

    // ── Return / Cancel interception ─────────────────────────────────────────

    // Return() and CategoryReturn() (same body; CategoryReturn is what right-click fires)
    // both read currentSettingsPageButton BEFORE calling CloseSettingsPage, then jump to
    // a MenuCancelPerformed handler that closes the whole settings menu. When our stub is
    // current we must intercept early: close the mods page ourselves and skip the native body.
    [HarmonyPatch(typeof(SettingsMenu), "Return")]
    [HarmonyPrefix]
    static bool BeforeReturn(SettingsMenu __instance)
        => !CloseModsPageIfCurrent(__instance);

    [HarmonyPatch(typeof(SettingsMenu), "CategoryReturn")]
    [HarmonyPrefix]
    static bool BeforeCategoryReturn(SettingsMenu __instance)
        => !CloseModsPageIfCurrent(__instance);

    // Called from our prefix patches (and from BeforeCloseSettingsPage when another page opens).
    // Does the full mods-page teardown: remove fields, toggle rail visual off, disable return area,
    // restore bouncing buttons.
    // Returns true if we were the active page (and handled the close).
    static bool CloseModsPageIfCurrent(SettingsMenu sm)
    {
        if (_stubSpb == null) return false;
        if (sm.currentSettingsPageButton?.Pointer != _stubSpb.Pointer) return false;
        try
        {
            sm.currentSettingsPageButton = null;
            if (_stubBackground != null) _stubBackground.enabled = false;
            // Deactivate the rail button's visual state through the native system.
            if (_railSpb != null)
                sm.ToggleSettingPageButton(_railSpb, false);
            if (sm.categoryReturnClickArea != null) sm.categoryReturnClickArea.enabled = false;
            sm.fieldsUI?.RemoveAll();
            _modPageRows.Clear();
            sm.ActivateBouncingButtons();
        }
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] CloseModsPage failed: {ex}"); }
        return true;
    }

    // ── OnFieldSelected / OnFieldChanged null guard ───────────────────────────

    // Both methods dereference currentSettingsPageButton->settingsPage unconditionally
    // (hitting the IL2CPP abort / swi(3) path when settingsPage == null). Skip them
    // when our stub (settingsPage==null) is the current page.
    [HarmonyPatch(typeof(SettingsMenu), "OnFieldSelected", typeof(Field))]
    [HarmonyPrefix]
    static bool BeforeOnFieldSelected(SettingsMenu __instance)
        => _stubSpb == null || __instance.currentSettingsPageButton?.Pointer != _stubSpb.Pointer;

    [HarmonyPatch(typeof(SettingsMenu), "OnFieldChanged", typeof(Field))]
    [HarmonyPrefix]
    static bool BeforeOnFieldChanged(SettingsMenu __instance)
        => _stubSpb == null || __instance.currentSettingsPageButton?.Pointer != _stubSpb.Pointer;

    // ── FieldUIListDropdown stay-open fix ─────────────────────────────────────

    // FieldUIListDropdown.Update() hides the dropdown whenever EventSystem.currentSelected
    // isn't the TMP_Dropdown's own GameObject. But when the dropdown opens, TMP_Dropdown
    // calls Select() on a child Toggle item, not on its own GO — so GetComponent<TMP_Dropdown>
    // on currentSelected returns null and Hide() fires every frame.
    //
    // Fix: replace the native Update entirely. Run the FieldUI base Update for animation/
    // selection tracking, then only hide the dropdown if currentSelected is NOT a descendant
    // of the dropdown list GO (m_Dropdown).
    [HarmonyPatch(typeof(FieldUIListDropdown), "Update")]
    [HarmonyPrefix]
    static bool ReplaceDropdownUpdate(FieldUIListDropdown __instance)
    {
        // Only intercept rows on our mods page — let native handle the game's own pages.
        if (!_modPageRows.Contains(__instance.Pointer)) return true;

        var dd = __instance.dropdown;
        if (dd == null || !dd.IsExpanded) return true;

        // Dropdown IS expanded. Run FieldUI.Update (base) for animation/selection tracking,
        // then skip the native hide-check by returning false.
        if (_fieldUIUpdateMethod == System.IntPtr.Zero)
            _fieldUIUpdateMethod = Il2CppInterop.Runtime.IL2CPP.GetIl2CppMethodByToken(
                Il2CppInterop.Runtime.Il2CppClassPointerStore<FieldUI>.NativeClassPtr,
                100663697);
        if (_fieldUIUpdateMethod != System.IntPtr.Zero)
        {
            try
            {
                unsafe
                {
                    System.IntPtr exc = System.IntPtr.Zero;
                    Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(
                        _fieldUIUpdateMethod,
                        Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtr(__instance),
                        null, ref exc);
                }
            }
            catch { /* ignore — animation only */ }
        }

        // currentSelected is a child Toggle inside m_Dropdown; leave the dropdown open.
        return false;
    }

    // FieldUI.Update method pointer — lazily cached; token 100663697 from FieldUI/_Type.cs.
    private static System.IntPtr _fieldUIUpdateMethod = System.IntPtr.Zero;

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
            if (_settingsMenu == null) return;
            var myFieldsUI = _settingsMenu.fieldsUI;
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
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] AfterFieldsUIActivate failed: {ex}"); }
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
        if (!_modPageRows.Contains(__instance.Pointer)) return;
        try
        {
            var es = EventSystem.current;
            if (es != null) es.SetSelectedGameObject(null);
            __instance.OnSelectedChange(false);
            __instance.RefreshAnimator();
        }
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] AfterOnPointerExit failed: {ex}"); }
    }

    // ── Field building ────────────────────────────────────────────────────────

    private static Field[] BuildFields()
    {
        var sets = ModOptions.Sets;
        if (sets.Count == 0) return Array.Empty<Field>();

        var list = new List<Field>();
        foreach (var set in sets)
        {
            list.Add(new FieldInfo(set.ModName));
            foreach (var opt in set.Options)
            {
                Field field = opt.Type switch
                {
                    ModOption.Kind.Toggle       => MakeToggle(opt),
                    ModOption.Kind.Slider       => MakeSlider(opt),
                    ModOption.Kind.TextField    => MakeTextField(opt),
                    ModOption.Kind.ListArrows   => MakeListArrows(opt),
                    ModOption.Kind.Dropdown     => MakeDropdown(opt),
                    ModOption.Kind.ButtonDouble => MakeButtonDouble(opt),
                    ModOption.Kind.Action       => MakeButton(opt),
                    ModOption.Kind.Label        => new FieldInfo(opt.Label),
                    _                           => new FieldInfo(opt.Label),
                };
                list.Add(field);
            }
        }
        return list.ToArray();
    }

    private static FieldToggle MakeToggle(ModOption opt)
    {
        var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(
            (System.Action<bool>)(v => opt.SetBool?.Invoke(v)));
        return new FieldToggle(new FieldLabel(opt.Label), Field.DisplayMode.Active,
            opt.GetBool?.Invoke() ?? false, cb);
    }

    private static FieldSlider MakeSlider(ModOption opt)
    {
        System.Action<float, float> twoArg = (_, newVal) => opt.SetFloat?.Invoke(newVal);
        var cb = DelegateSupport.ConvertDelegate<FieldSlider.OnValueChangedDelegate>(twoArg);
        return new FieldSlider(new FieldLabel(opt.Label), opt.SliderMin, opt.SliderMax,
            false, opt.GetFloat?.Invoke() ?? 0f, cb);
    }

    private static FieldText MakeTextField(ModOption opt)
    {
        // OnValueChangedDelegate: void(string oldValue, string newValue)
        System.Action<string, string> twoArg = (_, newVal) => opt.SetTextField?.Invoke(newVal);
        var cb = DelegateSupport.ConvertDelegate<FieldText.OnValueChangedDelegate>(twoArg);
        return new FieldText(
            new FieldLabel(opt.Label),
            opt.TextContentType,
            opt.GetTextField?.Invoke() ?? "",
            cb);
    }

    private static FieldListArrows MakeListArrows(ModOption opt)
    {
        var choices = opt.Choices ?? Array.Empty<string>();
        var labels  = new Il2CppReferenceArray<FieldLabel>(choices.Length);
        for (int i = 0; i < choices.Length; i++) labels[i] = new FieldLabel(choices[i]);

        int cur = Math.Clamp(opt.GetIndex?.Invoke() ?? 0, 0, Math.Max(0, choices.Length - 1));

        // OnValueChangedDelegate: void(int previousIndex, int nextIndex)
        System.Action<int, int> twoArg = (_, newIdx) => opt.SetIndex?.Invoke(newIdx);
        var cb = DelegateSupport.ConvertDelegate<FieldList.OnValueChangedDelegate>(twoArg);

        return new FieldListArrows(new FieldLabel(opt.Label), labels, cur, cb);
    }

    private static FieldListDropdown MakeDropdown(ModOption opt)
    {
        var choices = opt.Choices ?? Array.Empty<string>();
        var labels  = new Il2CppReferenceArray<FieldLabel>(choices.Length);
        for (int i = 0; i < choices.Length; i++) labels[i] = new FieldLabel(choices[i]);

        int cur = Math.Clamp(opt.GetIndex?.Invoke() ?? 0, 0, Math.Max(0, choices.Length - 1));

        // OnValueChangedDelegate: void(int previousIndex, int nextIndex)
        System.Action<int, int> twoArg = (_, newIdx) => opt.SetIndex?.Invoke(newIdx);
        var cb = DelegateSupport.ConvertDelegate<FieldList.OnValueChangedDelegate>(twoArg);

        return new FieldListDropdown(new FieldLabel(opt.Label), labels, cur, cb);
    }

    private static FieldButtonDouble MakeButtonDouble(ModOption opt)
    {
        var cbLeft  = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (System.Action)(() => opt.InvokeLeft?.Invoke()));
        var cbRight = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (System.Action)(() => opt.InvokeRight?.Invoke()));
        return new FieldButtonDouble(
            new FieldLabel(opt.Label),
            new FieldLabel(opt.LeftLabel),  cbLeft,  opt.LeftActive,
            new FieldLabel(opt.RightLabel), cbRight, opt.RightActive);
    }

    private static FieldButton MakeButton(ModOption opt)
    {
        var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (System.Action)(() => opt.Invoke?.Invoke()));
        return new FieldButton(new FieldLabel(opt.Label), cb);
    }
}

