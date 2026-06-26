using System;
using System.Collections.Generic;
using MelonLoader;

namespace CairnAPI;

// Registry for mod-authored settings that CairnModOptions surfaces in its UI.
//
// Usage:
//   ModOptions.Register("MyMod", new[]
//   {
//       ModOption.Toggle("Enable",        myEnabledEntry),
//       ModOption.Slider("Strength", 0f, 5f, myFloatEntry),
//       ModOption.Text  ("Override",      myStringEntry),
//       ModOption.Action("Reset to defaults", () => { … }),
//   });
//
// Options are stored in declaration order. Re-registering a mod replaces its options list.
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
            if (idx >= 0)
                _sets[idx] = set;
            else
                _sets.Add(set);
        }

        MelonLogger.Msg($"[ModOptions] Registered {modName}.");
    }

    public static void Unregister(string modName)
    {
        lock (_lock) _sets.RemoveAll(s => s.ModName == modName);
    }
}

// One mod's options block.
public sealed class ModOptionSet
{
    public string ModName { get; }
    public IReadOnlyList<ModOption> Options { get; }

    public ModOptionSet(string modName, List<ModOption> options)
    {
        ModName = modName;
        Options = options.AsReadOnly();
    }
}

// A single option entry. Use the factory methods; don't subclass.
public sealed class ModOption
{
    public enum Kind { Toggle, Slider, Text, Action }

    public Kind Type        { get; }
    public string Label     { get; }
    public string Tooltip   { get; }

    // Toggle
    public Func<bool>    GetBool  { get; private set; }
    public Action<bool>  SetBool  { get; private set; }

    // Slider
    public float         SliderMin { get; private set; }
    public float         SliderMax { get; private set; }
    public Func<float>   GetFloat  { get; private set; }
    public Action<float> SetFloat  { get; private set; }

    // Text
    public Func<string>   GetText  { get; private set; }
    public Action<string> SetText  { get; private set; }

    // Action
    public Action Invoke { get; private set; }

    private ModOption(Kind type, string label, string tooltip)
    {
        Type    = type;
        Label   = label;
        Tooltip = tooltip ?? "";
    }

    // Bool toggle backed by a MelonPreferences entry.
    public static ModOption Toggle(string label, MelonPreferences_Entry<bool> entry, string tooltip = null)
    {
        var o = new ModOption(Kind.Toggle, label, tooltip);
        o.GetBool = () => entry.Value;
        o.SetBool = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    // Bool toggle backed by arbitrary get/set callbacks (for computed state).
    public static ModOption Toggle(string label, Func<bool> get, Action<bool> set, string tooltip = null)
    {
        var o = new ModOption(Kind.Toggle, label, tooltip);
        o.GetBool = get;
        o.SetBool = set;
        return o;
    }

    // Float slider backed by a MelonPreferences entry.
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

    // Float slider backed by arbitrary callbacks.
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

    // String text field backed by a MelonPreferences entry.
    public static ModOption Text(string label, MelonPreferences_Entry<string> entry, string tooltip = null)
    {
        var o = new ModOption(Kind.Text, label, tooltip);
        o.GetText = () => entry.Value ?? "";
        o.SetText = v => { entry.Value = v; entry.Category.SaveToFile(false); };
        return o;
    }

    // String text field backed by arbitrary callbacks.
    public static ModOption Text(string label, Func<string> get, Action<string> set, string tooltip = null)
    {
        var o = new ModOption(Kind.Text, label, tooltip);
        o.GetText = get;
        o.SetText = set;
        return o;
    }

    // A labelled button that fires a callback (no persistent state).
    public static ModOption Action(string label, Action invoke, string tooltip = null)
    {
        var o = new ModOption(Kind.Action, label, tooltip);
        o.Invoke = invoke;
        return o;
    }
}
