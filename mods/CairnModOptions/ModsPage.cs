using System;
using System.Collections.Generic;
using CairnAPI;
using Il2CppTGBTools.UI;

namespace CairnModOptions;

// The "Mods" entry on the SettingsMenu rail. Rather than stacking every mod's options
// onto one long page, the page shows a single mod at a time: a mod-selector dropdown sits
// at the top, and the rows beneath it are the selected mod's options. Changing the dropdown
// swaps the page content (CairnAPI.SettingsPage handles the live fieldsUI swap).
//
// CairnAPI owns the page machinery + the Field factories; this type owns only the policy
// (which mod is shown) and the ModOption → Field translation.
internal static class ModsPage
{
    private static SettingsPage _page;
    private static int          _selected;  // index into ModOptions.Sets of the shown mod

    internal static void Install()
    {
        _page = CairnMenu.Settings.AddPage("Mods", BuildFields);
    }

    // Page content = [ mod selector dropdown ] + [ selected mod's option rows ].
    private static Field[] BuildFields()
    {
        var sets = ModOptions.Sets;
        if (sets.Count == 0)
            return new Field[] { Fields.Info("No installed mods provide options.") };

        _selected = Math.Clamp(_selected, 0, sets.Count - 1);

        var names = new string[sets.Count];
        for (int i = 0; i < sets.Count; i++) names[i] = sets[i].ModName;

        var rows = new List<Field>
        {
            // Selecting a different mod records the choice and re-runs this builder; the page
            // swaps its rows to the newly-selected mod on the next frame.
            Fields.Dropdown("Mod", names, _selected, OnModSelected),
        };

        foreach (var opt in sets[_selected].Options)
            rows.Add(Render(opt));

        return rows.ToArray();
    }

    private static void OnModSelected(int index)
    {
        _selected = index;
        _page?.Refresh();
    }

    // ── ModOption → native Field, via the CairnAPI.Fields primitives ───────────

    private static Field Render(ModOption opt) => opt.Type switch
    {
        ModOption.Kind.Toggle       => Fields.Toggle(opt.Label, opt.GetBool?.Invoke() ?? false, opt.SetBool),
        ModOption.Kind.Slider       => Fields.Slider(opt.Label, opt.SliderMin, opt.SliderMax,
                                                      opt.GetFloat?.Invoke() ?? 0f, opt.SetFloat),
        ModOption.Kind.TextField    => Fields.Text(opt.Label, opt.TextContentType,
                                                   opt.GetTextField?.Invoke() ?? "", opt.SetTextField),
        ModOption.Kind.ListArrows   => Fields.ListArrows(opt.Label, opt.Choices,
                                                         opt.GetIndex?.Invoke() ?? 0, opt.SetIndex),
        ModOption.Kind.Dropdown     => Fields.Dropdown(opt.Label, opt.Choices,
                                                       opt.GetIndex?.Invoke() ?? 0, opt.SetIndex),
        ModOption.Kind.ButtonDouble => Fields.ButtonDouble(opt.Label,
                                                          opt.LeftLabel,  opt.InvokeLeft,  opt.LeftActive,
                                                          opt.RightLabel, opt.InvokeRight, opt.RightActive),
        ModOption.Kind.Action       => Fields.Button(opt.Label, opt.Invoke),
        _                           => Fields.Info(opt.Label),
    };
}
