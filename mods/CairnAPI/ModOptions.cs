using System;
using System.Collections.Generic;
using Il2CppTMPro;
using MelonLoader;

namespace CairnAPI;

// Registry for mod-authored settings that CairnAPI surfaces as a "Mods" page inside
// the game's native SettingsMenu.
//
// Usage:
//   ModOptions.Register("MyMod", new[]
//   {
//       ModOption.Toggle      ("God Mode",         myBoolEntry),
//       ModOption.Slider      ("Speed", 0f, 10f,   myFloatEntry),
//       ModOption.TextField   ("Player Name",       myStringEntry),
//       ModOption.ListArrows  ("Difficulty",        new[]{"Easy","Normal","Hard"}, myIntEntry),
//       ModOption.Dropdown    ("Resolution",        new[]{"1080p","1440p","4K"},   myIntEntry),
//       ModOption.ButtonDouble("Save / Load", "Save", OnSave, "Load", OnLoad),
//       ModOption.Action      ("Reset to defaults", OnReset),
//       ModOption.Info        ("Version: 1.0.0"),
//   });
//
// Options are displayed in declaration order. Re-registering a mod replaces its option list.
// The UI reads this registry; consumer mods never touch the UI directly.
public static class ModOptions
{
    private static readonly List<ModOptionSet> _sets = new();
    private static readonly object _lock = new();

    public static IReadOnlyList<ModOptionSet> Sets
    {
        get { lock (_lock) return _sets.AsReadOnly(); }
    }

    public static void Register(string modName, IEnumerable<ModOption> options)
    {
        if (string.IsNullOrWhiteSpace(modName))
            throw new ArgumentException("modName must not be empty.", nameof(modName));

        lock (_lock)
        {
            var idx = _sets.FindIndex(s => s.ModName == modName);
            var set = new ModOptionSet(modName, new List<ModOption>(options));
            if (idx >= 0) _sets[idx] = set;
            else          _sets.Add(set);
        }

        MelonLogger.Msg($"[ModOptions] Registered {modName}.");
    }

    public static void Unregister(string modName)
    {
        lock (_lock) _sets.RemoveAll(s => s.ModName == modName);
    }
}

// One mod's block of options.
public sealed class ModOptionSet
{
    public string ModName                  { get; }
    public IReadOnlyList<ModOption> Options { get; }

    public ModOptionSet(string modName, List<ModOption> options)
    {
        ModName = modName;
        Options = options.AsReadOnly();
    }
}

// A single option entry. Construct via the static factory methods; do not subclass.
public sealed class ModOption
{
    public enum Kind
    {
        Toggle,       // bool on/off
        Slider,       // float range
        TextField,    // editable string
        ListArrows,   // string list, left/right arrow navigation
        Dropdown,     // string list, dropdown picker
        ButtonDouble, // two side-by-side action buttons
        Action,       // single action button
        Label,        // read-only display text (no interaction)
    }

    public Kind   Type    { get; }
    public string Label   { get; }
    public string Tooltip { get; }

    // Toggle
    public Func<bool>   GetBool { get; private set; }
    public Action<bool> SetBool { get; private set; }

    // Slider
    public float         SliderMin { get; private set; }
    public float         SliderMax { get; private set; }
    public Func<float>   GetFloat  { get; private set; }
    public Action<float> SetFloat  { get; private set; }

    // TextField
    public TMP_InputField.ContentType TextContentType { get; private set; }
    public Func<string>   GetTextField { get; private set; }
    public Action<string> SetTextField { get; private set; }

    // ListArrows / Dropdown (shared storage — a given instance is one or the other)
    public string[]    Choices  { get; private set; }
    public Func<int>   GetIndex { get; private set; }
    public Action<int> SetIndex { get; private set; }

    // ButtonDouble
    public string LeftLabel   { get; private set; }
    public string RightLabel  { get; private set; }
    public Action InvokeLeft  { get; private set; }
    public Action InvokeRight { get; private set; }
    public bool   LeftActive  { get; private set; }
    public bool   RightActive { get; private set; }

    // Action
    public Action Invoke { get; private set; }

    private ModOption(Kind type, string label, string tooltip)
    {
        Type    = type;
        Label   = label;
        Tooltip = tooltip ?? "";
    }

    // ── Toggle ────────────────────────────────────────────────────────────────

    /// <summary>Bool on/off toggle backed by a MelonPreferences entry.</summary>
    public static ModOption Toggle(string label, MelonPreferences_Entry<bool> entry,
        string tooltip = null)
    {
        var o = new ModOption(Kind.Toggle, label, tooltip);
        o.GetBool = () => entry.Value;
        o.SetBool = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>Bool on/off toggle backed by arbitrary get/set callbacks.</summary>
    public static ModOption Toggle(string label, Func<bool> get, Action<bool> set,
        string tooltip = null)
    {
        var o = new ModOption(Kind.Toggle, label, tooltip);
        o.GetBool = get;
        o.SetBool = set;
        return o;
    }

    // ── Slider ────────────────────────────────────────────────────────────────

    /// <summary>Float slider in [<paramref name="min"/>, <paramref name="max"/>] backed by a MelonPreferences entry.</summary>
    public static ModOption Slider(string label, float min, float max,
        MelonPreferences_Entry<float> entry, string tooltip = null)
    {
        var o = new ModOption(Kind.Slider, label, tooltip);
        o.SliderMin = min;
        o.SliderMax = max;
        o.GetFloat  = () => entry.Value;
        o.SetFloat  = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>Float slider in [<paramref name="min"/>, <paramref name="max"/>] backed by arbitrary callbacks.</summary>
    public static ModOption Slider(string label, float min, float max,
        Func<float> get, Action<float> set, string tooltip = null)
    {
        var o = new ModOption(Kind.Slider, label, tooltip);
        o.SliderMin = min;
        o.SliderMax = max;
        o.GetFloat  = get;
        o.SetFloat  = set;
        return o;
    }

    // ── TextField ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Editable text field backed by a MelonPreferences entry.
    /// <paramref name="contentType"/> controls the keyboard/validation mode
    /// (e.g. <see cref="TMP_InputField.ContentType.IntegerNumber"/> for numeric-only input).
    /// </summary>
    public static ModOption TextField(string label, MelonPreferences_Entry<string> entry,
        TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard,
        string tooltip = null)
    {
        var o = new ModOption(Kind.TextField, label, tooltip);
        o.TextContentType = contentType;
        o.GetTextField    = () => entry.Value ?? "";
        o.SetTextField    = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>
    /// Editable text field backed by arbitrary get/set callbacks.
    /// <paramref name="contentType"/> controls the keyboard/validation mode.
    /// </summary>
    public static ModOption TextField(string label, Func<string> get, Action<string> set,
        TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard,
        string tooltip = null)
    {
        var o = new ModOption(Kind.TextField, label, tooltip);
        o.TextContentType = contentType;
        o.GetTextField    = get;
        o.SetTextField    = set;
        return o;
    }

    // ── ListArrows ────────────────────────────────────────────────────────────

    /// <summary>
    /// Left/right arrow list selector backed by a MelonPreferences entry (stores selected index).
    /// </summary>
    public static ModOption ListArrows(string label, string[] choices,
        MelonPreferences_Entry<int> entry, string tooltip = null)
    {
        var o = new ModOption(Kind.ListArrows, label, tooltip);
        o.Choices  = choices;
        o.GetIndex = () => entry.Value;
        o.SetIndex = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>Left/right arrow list selector backed by arbitrary get/set callbacks.</summary>
    public static ModOption ListArrows(string label, string[] choices,
        Func<int> get, Action<int> set, string tooltip = null)
    {
        var o = new ModOption(Kind.ListArrows, label, tooltip);
        o.Choices  = choices;
        o.GetIndex = get;
        o.SetIndex = set;
        return o;
    }

    // ── Dropdown ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Dropdown picker backed by a MelonPreferences entry (stores selected index).
    /// </summary>
    public static ModOption Dropdown(string label, string[] choices,
        MelonPreferences_Entry<int> entry, string tooltip = null)
    {
        var o = new ModOption(Kind.Dropdown, label, tooltip);
        o.Choices  = choices;
        o.GetIndex = () => Math.Clamp(entry.Value, 0, Math.Max(0, choices.Length - 1));
        o.SetIndex = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>Dropdown picker backed by arbitrary get/set callbacks.</summary>
    public static ModOption Dropdown(string label, string[] choices,
        Func<int> get, Action<int> set, string tooltip = null)
    {
        var o = new ModOption(Kind.Dropdown, label, tooltip);
        o.Choices  = choices;
        o.GetIndex = get;
        o.SetIndex = set;
        return o;
    }

    // ── ButtonDouble ──────────────────────────────────────────────────────────

    /// <summary>
    /// A row with two labelled action buttons side-by-side. Neither has persistent state.
    /// <paramref name="leftActive"/> / <paramref name="rightActive"/> control interactivity.
    /// </summary>
    public static ModOption ButtonDouble(
        string label,
        string leftLabel,  Action invokeLeft,
        string rightLabel, Action invokeRight,
        bool leftActive = true, bool rightActive = true,
        string tooltip = null)
    {
        var o = new ModOption(Kind.ButtonDouble, label, tooltip);
        o.LeftLabel   = leftLabel;
        o.RightLabel  = rightLabel;
        o.InvokeLeft  = invokeLeft;
        o.InvokeRight = invokeRight;
        o.LeftActive  = leftActive;
        o.RightActive = rightActive;
        return o;
    }

    // ── Action ────────────────────────────────────────────────────────────────

    /// <summary>A single labelled button that fires a callback. No persistent state.</summary>
    public static ModOption Action(string label, Action invoke, string tooltip = null)
    {
        var o = new ModOption(Kind.Action, label, tooltip);
        o.Invoke = invoke;
        return o;
    }

    // ── Label ─────────────────────────────────────────────────────────────────

    /// <summary>Read-only display text. No interaction. Use for section headers or status lines.</summary>
    public static ModOption Info(string text, string tooltip = null)
        => new ModOption(Kind.Label, text, tooltip);
}
