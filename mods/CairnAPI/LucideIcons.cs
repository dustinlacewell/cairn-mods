using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using MelonLoader;

namespace CairnAPI;

/// <summary>
/// Built-in Lucide icon set (1964 icons), embedded as an indexed PNG blob and decoded to
/// sprites on demand. Mods reference an icon by its Lucide name (e.g. "anchor", "flame",
/// "mountain-snow"). Icons are white line-art on transparent — they tint cleanly over the
/// menu's coloured wedge backgrounds.
///
/// <para>Pack format (little-endian): u32 count, then count × (u16 nameLen, name UTF-8,
/// u32 dataLen) index, then the concatenated PNG bytes in index order.</para>
/// </summary>
public static class LucideIcons
{
    private const string ResourceName = "CairnAPI.Resources.lucide.pack";

    private sealed class Slot { internal long Offset; internal int Length; }

    private static readonly Dictionary<string, Slot> _index = new();
    private static readonly Dictionary<string, Sprite> _cache = new();
    private static byte[] _blob;
    private static bool _loaded;
    private static long _dataStart;

    /// <summary>All available Lucide icon names (loads the index on first call).</summary>
    public static IEnumerable<string> Names { get { EnsureLoaded(); return _index.Keys; } }

    /// <summary>True if the named icon exists in the bundle.</summary>
    public static bool Has(string name) { EnsureLoaded(); return name != null && _index.ContainsKey(name); }

    /// <summary>
    /// Get the sprite for a Lucide icon name, decoding + caching on first use. Returns null
    /// if the name is unknown (caller falls back to a placeholder).
    /// </summary>
    public static Sprite Get(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        EnsureLoaded();
        if (_cache.TryGetValue(name, out var cached)) return cached;
        if (!_index.TryGetValue(name, out var slot)) return null;

        var png = new byte[slot.Length];
        System.Array.Copy(_blob, _dataStart + slot.Offset, png, 0, slot.Length);

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "lucide_" + name };
        if (!ImageConversion.LoadImage(tex, png))
        {
            MelonLogger.Warning($"[CairnAPI:CrossMenu] failed to decode lucide '{name}'.");
            return null;
        }
        tex.wrapMode = TextureWrapMode.Clamp;
        var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        _cache[name] = sprite;
        return sprite;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s == null) { MelonLogger.Error($"[CairnAPI:CrossMenu] embedded resource '{ResourceName}' missing."); return; }
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _blob = ms.ToArray();
            ParseIndex();
            MelonLogger.Msg($"[CairnAPI:CrossMenu] Lucide loaded: {_index.Count} icons.");
        }
        catch (System.Exception ex) { MelonLogger.Error($"[CairnAPI:CrossMenu] Lucide load failed: {ex}"); }
    }

    private static void ParseIndex()
    {
        int o = 0;
        uint count = ReadU32(ref o);
        long runningDataOffset = 0;
        // first pass: read the index, recording each icon's offset WITHIN the data section
        var order = new List<(string, int)>((int)count);
        for (uint i = 0; i < count; i++)
        {
            ushort nameLen = ReadU16(ref o);
            string name = System.Text.Encoding.UTF8.GetString(_blob, o, nameLen); o += nameLen;
            int dataLen = (int)ReadU32(ref o);
            order.Add((name, dataLen));
        }
        _dataStart = o;
        foreach (var (name, len) in order)
        {
            _index[name] = new Slot { Offset = runningDataOffset, Length = len };
            runningDataOffset += len;
        }
    }

    private static uint ReadU32(ref int o)
    {
        uint v = (uint)(_blob[o] | (_blob[o + 1] << 8) | (_blob[o + 2] << 16) | (_blob[o + 3] << 24));
        o += 4; return v;
    }

    private static ushort ReadU16(ref int o)
    {
        ushort v = (ushort)(_blob[o] | (_blob[o + 1] << 8));
        o += 2; return v;
    }
}
