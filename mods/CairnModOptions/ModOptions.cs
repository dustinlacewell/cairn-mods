using System;
using System.Collections.Generic;
using Il2CppTMPro;
using MelonLoader;

namespace CairnModOptions;

/// <summary>
/// Register a list of options under your mod's name. They become one section of the Mods page,
/// selectable from the dropdown. Re-registering replaces your previous list. Consumer mods never
/// touch the UI — the page reads this registry.
/// <code class="lang-csharp">
/// ModOptions.Register("MyMod", new[]
/// {
///     ModOption.Toggle("God mode", godModeEntry),
///     ModOption.Slider("Speed", 0f, 10f, speedEntry),
///     ModOption.Dropdown("Difficulty",
///         new[]{ "Easy", "Normal", "Hard" }, difficultyEntry),
///     ModOption.Action("Reset to defaults", ResetAll),
/// });
/// </code>
/// </summary>
public static class ModOptions
{
    private static readonly List<ModOptionSet> _sets = new();
    private static readonly object _lock = new();

    /// <summary>Every registered mod's option block, in registration order.</summary>
    /// <returns>IReadOnlyList&lt;ModOptionSet&gt;</returns>
    public static IReadOnlyList<ModOptionSet> Sets
    {
        get { lock (_lock) return _sets.AsReadOnly(); }
    }

    /// <summary>Register or replace your mod's option list. modName labels your entry in the dropdown.</summary>
    /// <param name="modName">Display name shown in the mod selector.</param>
    /// <param name="options">Rows to display, in declaration order.</param>
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

    /// <summary>Remove your mod's options from the page.</summary>
    /// <param name="modName">The name you registered under.</param>
    public static void Unregister(string modName)
    {
        lock (_lock) _sets.RemoveAll(s => s.ModName == modName);
    }
}

/// <summary>One mod's block of options.</summary>
public sealed class ModOptionSet
{
    /// <summary>Display name the block was registered under.</summary>
    public string ModName                  { get; }

    /// <summary>The mod's option rows, in declaration order.</summary>
    public IReadOnlyList<ModOption> Options { get; }

    /// <summary>Wrap a mod's name and its option rows into one block.</summary>
    /// <param name="modName">Display name shown in the mod selector.</param>
    /// <param name="options">Rows to display, in declaration order.</param>
    public ModOptionSet(string modName, List<ModOption> options)
    {
        ModName = modName;
        Options = options.AsReadOnly();
    }
}

/// <summary>
/// Build each row with a ModOption factory. Every option takes a label and an optional tooltip.
/// Stateful options (toggle, slider, text, list, dropdown) bind either to a MelonPreferences entry —
/// persisted to disk automatically — or to your own get/set callbacks.
/// <code class="lang-csharp">
/// // backed by a MelonPreferences entry (persists to disk)
/// ModOption.Toggle("Enable", enabledEntry);
///
/// // or your own get/set
/// ModOption.Toggle("Enable",
///     () =&gt; _enabled, v =&gt; _enabled = v);
///
/// // numeric-only text field
/// ModOption.TextField("Max count", maxEntry,
///     TMP_InputField.ContentType.IntegerNumber);
///
/// // a non-persistent action button
/// ModOption.Action("Reload config", ReloadConfig);
/// </code>
/// </summary>
public sealed class ModOption
{
    /// <summary>Which kind of row this option renders as.</summary>
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

    /// <summary>Bool on/off. Backed by a MelonPreferences_Entry&lt;bool&gt;, or a Func&lt;bool&gt; get / Action&lt;bool&gt; set pair.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="entry">Backing preference (or pass get/set callbacks).</param>
    /// <param name="tooltip">Hover help.</param>
    /// <returns>ModOption</returns>
    public static ModOption Toggle(string label, MelonPreferences_Entry<bool> entry,
        string tooltip = null)
    {
        var o = new ModOption(Kind.Toggle, label, tooltip);
        o.GetBool = () => entry.Value;
        o.SetBool = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>Bool on/off. Backed by a MelonPreferences_Entry&lt;bool&gt;, or a Func&lt;bool&gt; get / Action&lt;bool&gt; set pair.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="get">Reads the current value.</param>
    /// <param name="set">Writes the new value.</param>
    /// <param name="tooltip">Hover help.</param>
    /// <returns>ModOption</returns>
    public static ModOption Toggle(string label, Func<bool> get, Action<bool> set,
        string tooltip = null)
    {
        var o = new ModOption(Kind.Toggle, label, tooltip);
        o.GetBool = get;
        o.SetBool = set;
        return o;
    }

    // ── Slider ────────────────────────────────────────────────────────────────

    /// <summary>Float slider in [min, max].</summary>
    /// <param name="label">Row label.</param>
    /// <param name="min">Lower bound.</param>
    /// <param name="max">Upper bound.</param>
    /// <param name="entry">Backing preference (or pass get/set).</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
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

    /// <summary>Float slider in [min, max].</summary>
    /// <param name="label">Row label.</param>
    /// <param name="min">Lower bound.</param>
    /// <param name="max">Upper bound.</param>
    /// <param name="get">Reads the current value.</param>
    /// <param name="set">Writes the new value.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
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

    /// <summary>Editable string. contentType sets the keyboard/validation mode (e.g. IntegerNumber for numeric-only).</summary>
    /// <param name="label">Row label.</param>
    /// <param name="entry">Backing preference (or pass get/set).</param>
    /// <param name="contentType">Input mode. Default Standard.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
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

    /// <summary>Editable string. contentType sets the keyboard/validation mode (e.g. IntegerNumber for numeric-only).</summary>
    /// <param name="label">Row label.</param>
    /// <param name="get">Reads the current value.</param>
    /// <param name="set">Writes the new value.</param>
    /// <param name="contentType">Input mode. Default Standard.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
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

    /// <summary>Pick from a string list with left/right arrows. Stores the selected index.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="choices">Options to cycle through.</param>
    /// <param name="entry">Stores the selected index (or pass get/set).</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
    public static ModOption ListArrows(string label, string[] choices,
        MelonPreferences_Entry<int> entry, string tooltip = null)
    {
        var o = new ModOption(Kind.ListArrows, label, tooltip);
        o.Choices  = choices;
        o.GetIndex = () => entry.Value;
        o.SetIndex = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>Pick from a string list with left/right arrows. Stores the selected index.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="choices">Options to cycle through.</param>
    /// <param name="get">Reads the selected index.</param>
    /// <param name="set">Writes the selected index.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
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

    /// <summary>Pick from a string list with a popup dropdown. Stores the selected index.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="choices">Options to pick from.</param>
    /// <param name="entry">Stores the selected index (or pass get/set).</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
    public static ModOption Dropdown(string label, string[] choices,
        MelonPreferences_Entry<int> entry, string tooltip = null)
    {
        var o = new ModOption(Kind.Dropdown, label, tooltip);
        o.Choices  = choices;
        o.GetIndex = () => Math.Clamp(entry.Value, 0, Math.Max(0, choices.Length - 1));
        o.SetIndex = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    /// <summary>Pick from a string list with a popup dropdown. Stores the selected index.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="choices">Options to pick from.</param>
    /// <param name="get">Reads the selected index.</param>
    /// <param name="set">Writes the selected index.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
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

    /// <summary>A row with two action buttons side by side. No persistent state. leftActive/rightActive toggle interactivity.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="leftLabel">Left button text.</param>
    /// <param name="invokeLeft">Left button callback.</param>
    /// <param name="rightLabel">Right button text.</param>
    /// <param name="invokeRight">Right button callback.</param>
    /// <param name="leftActive">Whether the left button is interactive. Default true.</param>
    /// <param name="rightActive">Whether the right button is interactive. Default true.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
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

    /// <summary>A single action button. No persistent state.</summary>
    /// <param name="label">Button text.</param>
    /// <param name="invoke">Pressed callback.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
    public static ModOption Action(string label, Action invoke, string tooltip = null)
    {
        var o = new ModOption(Kind.Action, label, tooltip);
        o.Invoke = invoke;
        return o;
    }

    // ── Label ─────────────────────────────────────────────────────────────────

    /// <summary>Read-only display text. No interaction — use for section headers or status lines.</summary>
    /// <param name="text">Display text.</param>
    /// <param name="tooltip">Optional hover help.</param>
    /// <returns>ModOption</returns>
    public static ModOption Info(string text, string tooltip = null)
        => new ModOption(Kind.Label, text, tooltip);
}
