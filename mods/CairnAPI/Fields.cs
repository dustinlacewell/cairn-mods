using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTGBTools.UI;
using FieldInfo = Il2CppTGBTools.UI.FieldInfo;
using Il2CppTMPro;

namespace CairnAPI;

// Factory helpers that build the game's native TGBTools.UI.Field rows from plain
// managed values + callbacks. Each wraps the Il2Cpp delegate-conversion gotchas
// (two-arg slider/list signatures, DelegateSupport.ConvertDelegate) so consumers
// never touch the interop machinery.
//
// These are the building blocks for any custom SettingsMenu page (see SettingsPage).
public static class Fields
{
    // ── Read-only label / section header ───────────────────────────────────────

    public static FieldInfo Info(string text) => new FieldInfo(text);

    // ── Toggle (bool on/off) ───────────────────────────────────────────────────

    public static FieldToggle Toggle(string label, bool initial, Action<bool> onChanged)
    {
        var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(
            (Action<bool>)(v => onChanged?.Invoke(v)));
        return new FieldToggle(new FieldLabel(label), Field.DisplayMode.Active, initial, cb);
    }

    // ── Slider (float range) ───────────────────────────────────────────────────

    public static FieldSlider Slider(string label, float min, float max, float initial,
        Action<float> onChanged)
    {
        // OnValueChangedDelegate: void(float oldValue, float newValue)
        Action<float, float> twoArg = (_, v) => onChanged?.Invoke(v);
        var cb = DelegateSupport.ConvertDelegate<FieldSlider.OnValueChangedDelegate>(twoArg);
        return new FieldSlider(new FieldLabel(label), min, max, false, initial, cb);
    }

    // ── Text (editable string) ─────────────────────────────────────────────────

    public static FieldText Text(string label, TMP_InputField.ContentType contentType,
        string initial, Action<string> onChanged)
    {
        // OnValueChangedDelegate: void(string oldValue, string newValue)
        Action<string, string> twoArg = (_, v) => onChanged?.Invoke(v);
        var cb = DelegateSupport.ConvertDelegate<FieldText.OnValueChangedDelegate>(twoArg);
        return new FieldText(new FieldLabel(label), contentType, initial ?? "", cb);
    }

    // ── ListArrows (left/right list selector) ──────────────────────────────────

    public static FieldListArrows ListArrows(string label, string[] choices, int initial,
        Action<int> onChanged)
    {
        var labels = BuildLabels(choices);
        int cur    = Clamp(initial, choices);
        // OnValueChangedDelegate: void(int previousIndex, int nextIndex)
        Action<int, int> twoArg = (_, v) => onChanged?.Invoke(v);
        var cb = DelegateSupport.ConvertDelegate<FieldList.OnValueChangedDelegate>(twoArg);
        return new FieldListArrows(new FieldLabel(label), labels, cur, cb);
    }

    // ── Dropdown (popup list picker) ───────────────────────────────────────────

    public static FieldListDropdown Dropdown(string label, string[] choices, int initial,
        Action<int> onChanged)
    {
        var labels = BuildLabels(choices);
        int cur    = Clamp(initial, choices);
        Action<int, int> twoArg = (_, v) => onChanged?.Invoke(v);
        var cb = DelegateSupport.ConvertDelegate<FieldList.OnValueChangedDelegate>(twoArg);
        return new FieldListDropdown(new FieldLabel(label), labels, cur, cb);
    }

    // ── ButtonDouble (two side-by-side buttons) ────────────────────────────────

    public static FieldButtonDouble ButtonDouble(
        string label,
        string leftLabel,  Action invokeLeft,  bool leftActive,
        string rightLabel, Action invokeRight, bool rightActive)
    {
        var cbLeft  = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (Action)(() => invokeLeft?.Invoke()));
        var cbRight = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (Action)(() => invokeRight?.Invoke()));
        return new FieldButtonDouble(
            new FieldLabel(label),
            new FieldLabel(leftLabel),  cbLeft,  leftActive,
            new FieldLabel(rightLabel), cbRight, rightActive);
    }

    // ── Button (single action) ─────────────────────────────────────────────────

    public static FieldButton Button(string label, Action onClick)
    {
        var cb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
            (Action)(() => onClick?.Invoke()));
        return new FieldButton(new FieldLabel(label), cb);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static Il2CppReferenceArray<FieldLabel> BuildLabels(string[] choices)
    {
        choices ??= Array.Empty<string>();
        var labels = new Il2CppReferenceArray<FieldLabel>(choices.Length);
        for (int i = 0; i < choices.Length; i++) labels[i] = new FieldLabel(choices[i]);
        return labels;
    }

    private static int Clamp(int idx, string[] choices)
        => Math.Clamp(idx, 0, Math.Max(0, (choices?.Length ?? 0) - 1));
}
