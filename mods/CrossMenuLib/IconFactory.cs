using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppGameAction = Il2CppTheGameBakers.Cairn.UI.CrossMenuAction;

namespace CrossMenuLib;

/// <summary>
/// Builds the game-side <c>CrossMenuAction</c> ScriptableObject (the per-slot data
/// asset: sprites + type + display flags) from a managed <see cref="CrossMenuAction"/>,
/// and synthesises a placeholder sprite when a mod supplies none.
/// </summary>
internal static class IconFactory
{
    // A small palette so distinct placeholder glyphs are visually separable.
    private static readonly Color[] Palette =
    {
        new(0.86f, 0.32f, 0.31f), new(0.30f, 0.62f, 0.86f), new(0.46f, 0.76f, 0.40f),
        new(0.92f, 0.74f, 0.27f), new(0.66f, 0.46f, 0.84f), new(0.34f, 0.78f, 0.74f),
    };

    /// <summary>
    /// Create the game ScriptableObject for an action. The sprites array is laid out
    /// as the game expects: index 0 = available, index 1 = unavailable (GetSprite
    /// clamps to [0]). Higher indices are unused by custom actions.
    /// </summary>
    internal static Il2CppGameAction BuildSo(Registry.Entry entry)
    {
        var a = entry.Action;
        var so = ScriptableObject.CreateInstance<Il2CppGameAction>();
        so.name = "CML_" + a.Id;
        so.hideFlags = HideFlags.HideAndDontSave;

        // precedence: explicit Icon sprite → Lucide name → generated placeholder
        var primary = a.Icon != null ? a.Icon
                    : (!string.IsNullOrEmpty(a.IconName) ? LucideIcons.Get(a.IconName) : null);
        var available = primary != null ? primary : MakePlaceholder(entry, dimmed: false);
        var unavailable = a.IconUnavailable != null ? a.IconUnavailable
                        : (primary != null ? primary : MakePlaceholder(entry, dimmed: true));

        var sprites = new Il2CppReferenceArray<Sprite>(2);
        sprites[0] = available;
        sprites[1] = unavailable;

        // The data fields are private [SerializeField]s but the interop proxy surfaces
        // them as settable properties (confirmed live).
        // executeImmediatly=false: chord menus use the library's own hold-to-confirm timer.
        So.Set(so, sprites, entry.TypeValue, a.DisplayCount, a.CountWarningMaxValue, false);
        return so;
    }

    /// <summary>
    /// A round coloured disc with the first letter of the label — readable at the
    /// small icon size, distinct per action, and obviously a placeholder.
    /// </summary>
    private static Sprite MakePlaceholder(Registry.Entry entry, bool dimmed)
    {
        const int N = 96;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "CML_ph_" + entry.Action.Id };
        var baseColor = Palette[entry.TypeValue % Palette.Length];
        if (dimmed) baseColor = Color.Lerp(baseColor, Color.gray, 0.6f);

        float r = N * 0.46f, cx = N * 0.5f, cy = N * 0.5f;
        char glyph = string.IsNullOrEmpty(entry.Action.Label) ? '?' : char.ToUpperInvariant(entry.Action.Label[0]);

        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float dx = x - cx, dy = y - cy;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            Color c;
            if (d <= r)
            {
                // soft edge
                float edge = Mathf.Clamp01((r - d) / 3f);
                c = baseColor;
                c.a = edge;
                if (Glyph.Covers(glyph, x, y, N)) c = dimmed ? new Color(0.85f, 0.85f, 0.85f, 1f) : Color.white;
            }
            else c = new Color(0, 0, 0, 0);
            tex.SetPixel(x, y, c);
        }
        tex.Apply(false, false);
        return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
    }
}
