using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CairnAccessories;

/// <summary>
/// Local, per-machine persisted state — distinct from the shared GLBs (whose placement already
/// lives in their own asset.extras). Holds which accessories are worn (by filename) and where each
/// window sits, in one JSON next to the mod. Writes are debounced (so dragging a window doesn't
/// hammer the disk) and flushed on quit.
///
/// The manager reads/writes the worn set; each view persists its own rect by a stable key. Both go
/// through <see cref="MarkDirty"/>; <see cref="Tick"/> performs the debounced write and
/// <see cref="Flush"/> forces a final one.
/// </summary>
public sealed class ModConfig
{
    private const float DebounceSeconds = 0.5f;

    private readonly string _path;
    private readonly HashSet<string> _worn = new();
    private readonly Dictionary<string, Rect> _windows = new();

    private bool _dirty;
    private float _saveAt = -1f;

    public ModConfig(string folder) => _path = Path.Combine(folder, "config.json");

    public IReadOnlyCollection<string> WornFiles => _worn;

    // ---- tuning ----------------------------------------------------------------------------------

    private float _repeatInterval = 0.12f;
    /// <summary>Seconds between nudge-button repeats (the repeat-speed slider). Persisted.</summary>
    public float RepeatInterval
    {
        get => _repeatInterval;
        set { if (_repeatInterval != value) { _repeatInterval = value; MarkDirty(); } }
    }

    // ---- worn set --------------------------------------------------------------------------------

    public void SetWorn(string fileName, bool worn)
    {
        if (worn ? _worn.Add(fileName) : _worn.Remove(fileName)) MarkDirty();
    }

    // ---- window rects ----------------------------------------------------------------------------

    /// <summary>The saved rect for a window key, or <paramref name="fallback"/> if none stored.</summary>
    public Rect Window(string key, Rect fallback) =>
        _windows.TryGetValue(key, out var r) ? r : fallback;

    /// <summary>Record a window's rect; only marks dirty if it actually moved/resized.</summary>
    public void SetWindow(string key, Rect r)
    {
        if (_windows.TryGetValue(key, out var prev) && prev == r) return;
        _windows[key] = r;
        MarkDirty();
    }

    // ---- persistence -----------------------------------------------------------------------------

    public void MarkDirty() { _dirty = true; _saveAt = Time.unscaledTime + DebounceSeconds; }

    /// <summary>Call every frame: writes once the debounce window elapses after the last change.</summary>
    public void Tick()
    {
        if (_dirty && Time.unscaledTime >= _saveAt) Flush();
    }

    public void Load()
    {
        if (!File.Exists(_path)) return;
        var root = JObject.Parse(File.ReadAllText(_path));

        if (root["repeatInterval"] != null) _repeatInterval = (float)root["repeatInterval"];

        _worn.Clear();
        if (root["worn"] is JArray worn)
            foreach (var t in worn) _worn.Add((string)t);

        _windows.Clear();
        if (root["windows"] is JObject wins)
            foreach (var p in wins)
                if (p.Value is JArray a && a.Count == 4)
                    _windows[p.Key] = new Rect((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
    }

    public void Flush()
    {
        var wins = new JObject();
        foreach (var kv in _windows)
            wins[kv.Key] = new JArray(kv.Value.x, kv.Value.y, kv.Value.width, kv.Value.height);

        var root = new JObject
        {
            ["repeatInterval"] = _repeatInterval,
            ["worn"] = new JArray(_worn),
            ["windows"] = wins,
        };
        File.WriteAllText(_path, root.ToString(Newtonsoft.Json.Formatting.Indented));
        _dirty = false;
        _saveAt = -1f;
    }
}
