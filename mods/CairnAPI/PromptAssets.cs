using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppTMPro;                  // TMP_FontAsset
using Object = UnityEngine.Object;

namespace CairnAPI;

// Shared asset lookup + atlas-matched label material for the screen-space prompt primitives. The game ships
// the fonts/materials/sprites; we never recreate them — just find the already-loaded instance by name and
// cache it (FindObjectsOfTypeAll is a full scan, so do it once per (type,name)). WorldPromptBuilder keeps its
// OWN copy of this (its No-ZTest world variants differ), so this serves only the screen path (ScreenPrompt).
internal static class PromptAssets
{
    // Find an already-loaded asset by exact name. Returns null pre-game (assets not yet loaded); callers guard.
    private static readonly Dictionary<string, Object> _cache = new();

    internal static T Find<T>(string name) where T : Object
    {
        var key = typeof(T).FullName + "\0" + name;
        if (_cache.TryGetValue(key, out var cached) && cached != null)
            return cached as T;
        try
        {
            foreach (var a in Resources.FindObjectsOfTypeAll<T>())
                if (a != null && a.name == name)
                {
                    _cache[key] = a;
                    return a;
                }
        }
        catch { }
        return null;
    }

    // The label's shared material. "Cairn_TextFont DropShadow" gives the authentic drop-shadow look, but there
    // are MULTIPLE material instances by that name and the wrong one samples a stale atlas → wingdings
    // (live-proven). So accept the named material ONLY if its atlas texture matches the font's; otherwise fall
    // back to the font's own material (always atlas-correct, just no shadow).
    internal static Material LabelMaterial(TMP_FontAsset font)
    {
        var fontMat = font != null ? font.material : null;
        var atlas = fontMat != null ? fontMat.mainTexture : null;
        try
        {
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.name == "Cairn_TextFont DropShadow" && atlas != null && m.mainTexture == atlas)
                    return m;
        }
        catch { }
        return fontMat;   // atlas-matching default (no shadow) — correct over pretty
    }
}
