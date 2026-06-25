using System;
using Il2Cpp;                       // WorldGameplayPromptUI, GlobalUIs, WorldGameplayPromptHandle, ...
using Il2CppTGBTools.Localization;  // LocKeyStringId
using UnityEngine;                  // Transform
using UnityEngine.InputSystem;      // InputAction
using MelonLoader;

namespace CairnAPI;

// A WORLD-SPACE prompt: a "[glyph] label" that floats over a point in the world (sticks to a wall, billboards
// to camera), instead of sitting on the screen. Use it when the affordance belongs to a thing out there (a
// hatch, a totem) rather than to the player. Same plain-string + Glyph contract as ScreenPrompt.
//
//   var h = WorldPrompt.Show(thing.transform, "Activate", Glyph.Key("e"));
//   ... later ...  WorldPrompt.Hide(h);
//
// We do NOT drive the game's single shared GlobalUIs.worldGameplayPromptUi (that would clobber the game's own
// world prompts) — WorldPromptBuilder builds our OWN independent WorldGameplayPromptUI from scratch (see Ui).
// The world-UI render path (after-image component + No-ZTest materials + fontStyle=Bold sub-mesh routing)
// lives in WorldPromptBuilder — see world-ui-afterimage-render.md / world-ui-render-pass-materials.md.
//
// WHY TEXT/GLYPH ARE SET ONCE, NOT PUMPED (decompiled proof, DESIGN-prompt-primitives.md §4):
// WorldGameplayPromptUI has NO Update/LateUpdate, so there is no per-frame text writer. The ONE thing that
// re-stamps the label is the widget's own LocalizedText resolving its empty key to "[none_string]" after
// Display — defeated ONCE by disabling that resolver (ApplyOnce), not by a pump. Billboarding + camera scale are
// driven by OnMainCameraPreCull — the game pumps that for us every camera frame, for free. The ONLY per-frame
// work is UpdateSizeAndPosition (recomputes world pos + wall-stick + leader-line height from the anchor), and
// its inputs change only when the ANCHOR moves — not the camera. So a STATIC anchor gets ONE
// UpdateSizeAndPosition at Show and no coroutine; a MOVING anchor (WorldPromptStyle.FollowsAnchor) gets the
// position-only pump, nothing else.
public static class WorldPrompt
{
    /// <summary>
    /// Float a prompt over <paramref name="anchor"/>. <paramref name="text"/> is shown as-is,
    /// <paramref name="glyph"/> is the button icon (see <see cref="Glyph"/>, null = none).
    /// <paramref name="style"/> tunes the float distance / wall-stick and whether it follows a moving anchor
    /// (null = the close default, ~1/3 of the game's wall-mount distance — live-tuned). Returns a handle for
    /// <see cref="Hide"/>. This drives our OWN widget, so a new Show replaces the previous mod prompt on it
    /// (one mod world prompt at a time); the game's own world prompts are untouched.
    /// </summary>
    public static WorldPromptHandle Show(Transform anchor, string text, InputAction glyph = null,
        bool canInteract = true, WorldPromptStyle style = null)
    {
        var ui = Ui;
        if (ui == null || anchor == null) return WorldPromptHandle.Invalid;

        style ??= WorldPromptStyle.Default;

        WorldGameplayPromptParameters p;
        try { p = style.ToParameters(); }
        catch { return WorldPromptHandle.Invalid; }

        WorldGameplayPromptHandle native;
        try { native = ui.Display(anchor, p, EmptyLocKey(), null, glyph, canInteract, false, null); }
        catch { return WorldPromptHandle.Invalid; }

        var h = new WorldPromptHandle(native, text ?? string.Empty, style.FollowsAnchor) { Glyph = glyph };
        h.ApplyOnce();                          // write text + glyph ONCE, position once (see header)
        if (style.FollowsAnchor)
            WorldPromptPump.Track(h);           // moving anchor → position-only pump; static → no coroutine at all
        return h;
    }

    /// <summary>Remove a world prompt. Safe on an invalid / already-hidden handle.</summary>
    public static void Hide(WorldPromptHandle handle)
    {
        if (handle == null || !handle.Valid) return;
        WorldPromptPump.Untrack(handle);
        var ui = Ui;
        var native = handle.Native;
        if (ui != null)
            try { ui.Hide(ref native); } catch { }
        handle.Invalidate();
    }

    /// <summary>
    /// OUR OWN world-prompt widget — a lazily-BUILT WorldGameplayPromptUI (from scratch, no clone), so a mod
    /// prompt coexists with the game's own in-world prompts instead of clobbering the single shared widget.
    /// Null before in-game UI init (the build needs the game's fonts/materials/sprites loaded).
    /// </summary>
    public static WorldGameplayPromptUI Ui => _ours ??= WorldPromptBuilder.Build();

    private static WorldGameplayPromptUI _ours;

    private static ParametrizedLocKey EmptyLocKey() => new ParametrizedLocKey(new LocKeyStringId());
}

/// <summary>
/// How a <see cref="WorldPrompt"/> floats over its anchor (distance / wall-stick). Maps to the game's
/// WorldGameplayPromptParameters. Defaults are ~1/3 of the game's wall-mount distance (live-tuned to sit
/// near the target rather than far on a wall with a long leader line). Tune any field for your prompt.
/// </summary>
public sealed class WorldPromptStyle
{
    /// <summary>Anchor radius. Game default 0.25; we use ~0.083.</summary>
    public float Radius = 0.25f / 3f;
    /// <summary>Wall-stick push offset. Game default 0.2; we use ~0.067 (small = stays near the anchor).</summary>
    public float WallOffset = 0.2f / 3f;
    /// <summary>Max height it floats to. Game default 1.3; we use ~0.43.</summary>
    public float MaxHeight = 1.3f / 3f;
    /// <summary>Height above the anchor. Game default 0.2; we use ~0.067.</summary>
    public float HeightOffset = 0.2f / 3f;
    /// <summary>Flip vertically.</summary>
    public bool UpsideDown = false;
    /// <summary>Whether the prompt follows a moving anchor. true → a per-frame position pump; false → zero pump.</summary>
    public bool FollowsAnchor = true;

    /// <summary>The close default (~1/3 of the game's wall-mount distance).</summary>
    public static WorldPromptStyle Default => new();

    internal Il2Cpp.WorldGameplayPromptParameters ToParameters()
    {
        var p = new Il2Cpp.WorldGameplayPromptParameters
        {
            gpeRadius = Radius,
            raycastOffset = WallOffset,
            maxWorldHeight = MaxHeight,
            worldHeightOffset = HeightOffset,
            upsideDown = UpsideDown,
            providerCanMove = FollowsAnchor,
        };
        return p;
    }
}

/// <summary>Opaque handle to a shown <see cref="WorldPrompt"/>. Pass it back to <see cref="WorldPrompt.Hide"/>.</summary>
public sealed class WorldPromptHandle
{
    internal WorldGameplayPromptHandle Native;
    internal string Text;
    internal UnityEngine.InputSystem.InputAction Glyph;
    internal readonly bool FollowsAnchor;
    private bool _valid;

    internal WorldPromptHandle(WorldGameplayPromptHandle native, string text, bool followsAnchor)
    {
        Native = native; Text = text; FollowsAnchor = followsAnchor; _valid = true;
    }

    private WorldPromptHandle() { _valid = false; }
    public static readonly WorldPromptHandle Invalid = new();

    public bool Valid => _valid;
    internal void Invalidate() => _valid = false;

    // Write text + glyph ONCE at Show, and position once. The widget's own Display leaves the label TMP disabled
    // and the label/glyph GameObjects inactive, so we enable + activate them here. Nothing clobbers them after
    // (no Update on the widget, rail-free → no loc re-stamp), so this runs exactly once — no per-frame re-apply.
    internal void ApplyOnce()
    {
        if (!_valid) return;
        var ui = WorldPrompt.Ui;
        if (ui == null) return;
        try
        {
            var label = ui.label;
            var tmp = label != null ? label.text : null;
            if (tmp != null)
            {
                // DISABLE the LocalizedText resolver. The widget's own LocalizedText resolves its (empty) loc key
                // to "[none_string]" after Display and overwrites our text — live-proven (the world prompt showed
                // "[none_string]"). persistent alone does NOT hold; disabling the resolver is the reliable fix
                // (same as ScreenPrompt's off-rail label). The TMP it wraps keeps rendering our text.
                label.enabled = false;
                // PLAIN text — no tag-wrapping. The label TMP is built fontStyle=Bold (WorldTextStyle), which
                // routes its glyphs through Boxed-DemiBold SDF → a TMP_SubMeshUI, the ONLY thing the world-UI
                // renderer draws for text (world-ui-afterimage-render.md §5). fontStyle is a component property,
                // so it survives this text write.
                tmp.text = Text;
                tmp.enabled = true;               // the world widget leaves the label TMP disabled
                ActivateChain(tmp.gameObject);
            }
            if (Glyph != null)
            {
                var iisa = ui.inputImage;
                if (iisa != null)
                {
                    iisa.InputAction = Glyph;
                    iisa.adaptiveBinding = true;
                    iisa.fallback = true;   // paint the blank-keycap fallback for plain keys (else null → blank)
                    iisa.Refresh();
                    ActivateChain(iisa.gameObject);
                }
            }
            ui.UpdateSizeAndPosition();   // position once; a static anchor never needs it again
        }
        catch { }
    }

    // Position-only pump step for a MOVING anchor: recompute world pos + wall-stick + leader-line height. Never
    // touches text/glyph/billboard (text set once; the game billboards via OnMainCameraPreCull). DESIGN §4.
    internal void TickPosition()
    {
        if (!_valid) return;
        var ui = WorldPrompt.Ui;
        if (ui == null) return;
        try { ui.UpdateSizeAndPosition(); } catch { }
    }

    private static void ActivateChain(UnityEngine.GameObject go)
    {
        var t = go.transform;
        while (t != null) { if (!t.gameObject.activeSelf) t.gameObject.SetActive(true); t = t.parent; }
    }
}

// Position-only pump for world prompts whose anchor MOVES (FollowsAnchor). Static prompts are never tracked
// (they're positioned once at Show). One shared widget; usually ≤1 tracked at a time. DESIGN §4.
internal static class WorldPromptPump
{
    private static readonly System.Collections.Generic.List<WorldPromptHandle> Tracked = new();
    private static object _coroutine;

    internal static void Track(WorldPromptHandle h)
    {
        if (h == null || !h.Valid || !h.FollowsAnchor) return;
        if (!Tracked.Contains(h)) Tracked.Add(h);
        if (_coroutine == null) _coroutine = MelonLoader.MelonCoroutines.Start(Pump());
    }

    internal static void Untrack(WorldPromptHandle h) => Tracked.Remove(h);

    private static System.Collections.IEnumerator Pump()
    {
        while (true)
        {
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                var h = Tracked[i];
                if (h == null || !h.Valid) { Tracked.RemoveAt(i); continue; }
                h.TickPosition();
            }
            if (Tracked.Count == 0) { _coroutine = null; yield break; }
            yield return null;
        }
    }
}
