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
    private static IDisposable           _railEntry;
    private static SettingsPageButton    _injectedSpb;
    private static Image                 _injectedBackground;
    private static SettingsMenu          _settingsMenu;

    // Pointers of FieldUI rows belonging to our page — used by AfterOnPointerExit.
    private static readonly HashSet<IntPtr> _modPageRows = new();

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        _railEntry = CairnMenu.Settings.AddEntry("Mods", OnModsButtonClicked);
        harmony.PatchAll(typeof(ModOptionsHook));
    }

    // ── Settings page open ────────────────────────────────────────────────────

    // Called when the CairnMenu.Settings rail entry is clicked.
    // Replicates the settingsPage!=null branch of OpenSettingsPage.
    static void OnModsButtonClicked()
    {
        if (_settingsMenu == null) return;
        try
        {
            // Deactivate hover arrow + disable nav buttons (mirrors OpenSettingsPage steps 1-2)
            var baz = _settingsMenu.bouncingButtons?.BouncingArrowZone;
            if (baz != null) baz.Deactivate(false);
            _settingsMenu.bouncingButtons?.Toggle(false);

            if (_settingsMenu.currentSettingsPageButton != null)
                _settingsMenu.CloseSettingsPage(false);

            // Build a stub SettingsPageButton so Return()/Cancel resolves to us.
            // settingsPage is intentionally null — Awake early-returns on null, CloseSettingsPage
            // is guarded by our prefix below.
            if (_injectedSpb == null)
            {
                var go = new GameObject("ModsPageStub");
                go.transform.SetParent(_settingsMenu.transform, false);
                go.SetActive(false);
                _injectedBackground = go.AddComponent<Image>();
                _injectedBackground.color   = new Color(0f, 0f, 0f, 0f);
                _injectedBackground.enabled = false;
                var spb = go.AddComponent<SettingsPageButton>();
                spb.settingsMenu = _settingsMenu;
                spb.background   = _injectedBackground;
                _injectedSpb = spb;
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

            _settingsMenu.currentSettingsPageButton = _injectedSpb;

            if (_settingsMenu.categoryReturnClickArea != null)
                _settingsMenu.categoryReturnClickArea.enabled = true;

            if (_injectedBackground != null)
                _injectedBackground.enabled = true;
        }
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] OnModsButtonClicked failed: {ex}"); }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SettingsMenu), "OnOpened")]
    [HarmonyPostfix]
    static void AfterOpened(SettingsMenu __instance)
    {
        _settingsMenu = __instance;
        _injectedSpb  = null;
    }

    [HarmonyPatch(typeof(SettingsMenu), "OnClosed")]
    [HarmonyPostfix]
    static void AfterClosed(SettingsMenu __instance)
    {
        _injectedSpb        = null;
        _injectedBackground = null;
        _settingsMenu       = null;
        _modPageRows.Clear();
    }

    // ── CloseSettingsPage null guard ──────────────────────────────────────────

    // CloseSettingsPage crashes when currentSettingsPageButton.settingsPage==null:
    // ToggleSettingPageButton runs unconditionally, then falls through to an abort.
    // Prefix: when our stub is current, do cleanup and null currentSettingsPageButton
    // so the native body hits its early-return on null.
    [HarmonyPatch(typeof(SettingsMenu), "CloseSettingsPage")]
    [HarmonyPrefix]
    static void BeforeCloseSettingsPage(SettingsMenu __instance)
    {
        if (_injectedSpb == null) return;
        if (__instance.currentSettingsPageButton?.Pointer != _injectedSpb?.Pointer) return;
        try
        {
            __instance.currentSettingsPageButton = null;
            if (_injectedBackground != null) _injectedBackground.enabled = false;
            if (__instance.categoryReturnClickArea != null)
                __instance.categoryReturnClickArea.enabled = false;
            __instance.fieldsUI?.RemoveAll();
            _modPageRows.Clear();
        }
        catch (Exception ex) { MelonLogger.Error($"[ModOptions] BeforeCloseSettingsPage failed: {ex}"); }
    }

    // ── OnPointerExit fix ─────────────────────────────────────────────────────

    // FieldUI.OnPointerExit is a no-op in Select mode (gamepad design).
    // For our mouse-driven rows, manually deselect and reset both the slide and chevron.
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
                    ModOption.Kind.Toggle => MakeToggle(opt),
                    ModOption.Kind.Slider => MakeSlider(opt),
                    ModOption.Kind.Action => MakeButton(opt),
                    ModOption.Kind.Text   => new FieldInfo($"{opt.Label}: {opt.GetText?.Invoke() ?? ""}"),
                    _                     => new FieldInfo(opt.Label),
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

    private static FieldButton MakeButton(ModOption opt)
    {
        var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (System.Action)(() => opt.Invoke?.Invoke()));
        return new FieldButton(new FieldLabel(opt.Label), cb);
    }
}
