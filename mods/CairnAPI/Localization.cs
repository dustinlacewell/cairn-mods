using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppTGBTools.Localization;  // LocalizationManager, LocKeyStringId

namespace CairnAPI;

// Register custom strings the game renders NATIVELY, by owning the one resolver chokepoint.
//
// Every rendered-text path in the game funnels through LocalizationManager.Get(LocKeyStringId) — the sole
// string resolver, with no cached-string field and no bypass (LocalizedText.Refresh, dialog, prompts, the
// eagle-eye warp list, every provider). A single Harmony prefix on Get therefore covers 100% of rendered
// text. We hand out loc keys from a reserved id band (counting up from int.MinValue, collision-free against
// the game's FNV-hashed real keys) and serve their text from a mod-side registry, so:
//   - the result survives every Refresh (the game re-calls Get; our persistent prefix re-serves), and
//   - it survives a language change for free (the game re-calls Get on every refreshDelegate; we re-serve
//     the entry for the new CurrentLanguage, falling back to English).
//
// The registry is language-AWARE with an English-default convenience overload: Register(string) keys the
// text under "EN" and that is the fallback for every language, so plain / user-generated content (a bookmark
// name) shows identically everywhere; Register(map) takes a full per-language table. See
// re/systems/world-streaming/cairnapi-localization-DESIGN.md.
public static class Localization
{
    // English fallback language code. LIVE-VERIFIED: LocalizationManager.CurrentLanguage == "EN" (an
    // upper-case 2-letter code); all language codes are upper-normalized so the registry is case-insensitive.
    private const string English = "EN";

    // id.value → (langUpper → text). Identity of a loc key IS its int, so this is keyed by the int.
    private static readonly Dictionary<int, Dictionary<string, string>> _entries = new();

    // Reserved id band: count up from int.MinValue. Collision-free below int.MinValue+1000 against the game's
    // real LocKeyStringIdEnum values (FNV hashes, most-negative ~ -2,145,991,127); we never approach that.
    private static int _nextId = int.MinValue;

    /// <summary>
    /// Register <paramref name="english"/> under English and hand back a loc key that renders it in EVERY
    /// language (English is the fallback for all). For plain or user-generated content (names, labels) that
    /// is not translated. Write the returned key wherever the game wants a <c>LocKeyStringId</c> and the
    /// game's own text pipeline renders this string there, durably.
    /// </summary>
    public static LocKeyStringId Register(string english)
    {
        int id = _nextId++;
        _entries[id] = new Dictionary<string, string> { [English] = english ?? string.Empty };
        return new LocKeyStringId(id);
    }

    /// <summary>
    /// Register a full per-language table and hand back a loc key. Language codes are upper-normalized
    /// (so <c>"en"</c> and <c>"EN"</c> are the same entry). The English entry is the fallback for any
    /// language not in the table; if the table has no English entry the first entry is used as the fallback.
    /// </summary>
    public static LocKeyStringId Register(IDictionary<string, string> byLanguage)
    {
        int id = _nextId++;
        _entries[id] = Normalize(byLanguage);
        return new LocKeyStringId(id);
    }

    /// <summary>Replace the English / fallback text of a key returned by <see cref="Register(string)"/>.</summary>
    public static void Update(LocKeyStringId key, string english) => Update(key, English, english);

    /// <summary>
    /// Set or replace the text for one language of a registered key (language code upper-normalized). A key
    /// not registered here is ignored.
    /// </summary>
    public static void Update(LocKeyStringId key, string lang, string text)
    {
        if (_entries.TryGetValue(key.Value, out var entry))
            entry[Upper(lang)] = text ?? string.Empty;
    }

    /// <summary>Drop a registered key; the game falls back to its own resolution for that id afterward.</summary>
    public static void Unregister(LocKeyStringId key) => _entries.Remove(key.Value);

    /// <summary>True if <paramref name="key"/> is one this facade resolves.</summary>
    public static bool IsRegistered(LocKeyStringId key) => _entries.ContainsKey(key.Value);

    // The hook calls this for every Get; returns false (and leaves text null) for ids we don't own, so the
    // game's own resolver runs for them.
    internal static bool TryResolve(int idValue, out string text)
    {
        if (_entries.TryGetValue(idValue, out var entry)) { text = Resolve(entry); return true; }
        text = null;
        return false;
    }

    // Pick the entry's text for the live language: current language, else English, else the first entry,
    // else a placeholder. CurrentLanguage is guarded — Instance can be null very early (pre-asset-load), in
    // which case we resolve English.
    private static string Resolve(Dictionary<string, string> entry)
    {
        var lang = CurrentLanguage();
        if (entry.TryGetValue(lang, out var s)) return s;
        if (entry.TryGetValue(English, out var en)) return en;
        return entry.Values.FirstOrDefault() ?? "[unnamed]";
    }

    // The live language code, upper-normalized; "EN" if the manager isn't ready or the read throws.
    private static string CurrentLanguage()
    {
        try
        {
            var inst = LocalizationManager.Instance;
            var lang = inst != null ? inst.CurrentLanguage : null;
            return string.IsNullOrEmpty(lang) ? English : Upper(lang);
        }
        catch { return English; }
    }

    private static Dictionary<string, string> Normalize(IDictionary<string, string> byLanguage)
    {
        var map = new Dictionary<string, string>();
        if (byLanguage != null)
            foreach (var kv in byLanguage)
                map[Upper(kv.Key)] = kv.Value ?? string.Empty;
        // Guarantee an English fallback: if none given, the first entry becomes EN.
        if (!map.ContainsKey(English) && map.Count > 0)
            map[English] = map.Values.First();
        return map;
    }

    private static string Upper(string s) => (s ?? English).ToUpperInvariant();

    /// <summary>Install the Get prefix (one prefix covers all rendered text). Call once from Core.</summary>
    internal static void Install(HarmonyLib.Harmony harmony) => harmony.PatchAll(typeof(LocalizationHooks));
}

// The single chokepoint: prefix LocalizationManager.Get(LocKeyStringId). For ids we own, set the result and
// skip the body (our ids are not in the game asset, so the body would return "MISSING KEY" or throw pre-load);
// for everything else, fall through to the game's resolver. The arg-type array selects the LocKeyStringId
// overload (there is also a Get(string)).
[HarmonyPatch]
internal static class LocalizationHooks
{
    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.Get), new[] { typeof(LocKeyStringId) })]
    [HarmonyPrefix]
    private static bool Prefix(LocKeyStringId key, ref string __result)
    {
        if (Localization.TryResolve(key.Value, out var s)) { __result = s; return false; }
        return true;
    }
}
