using System;
using Il2Cpp;                       // InputPrompt, LocalizedText, GameplayPromptHud, GlobalUIs, ParametrizedLocKey, MoSingleton
using Il2CppTGBTools.Localization;  // LocKeyStringId
using UnityEngine;
using UnityEngine.UI;               // Image, LayoutElement, ContentSizeFitter, Canvas, CanvasScaler, GraphicRaycaster
using UnityEngine.InputSystem;      // InputAction
using Il2CppTMPro;                  // TextMeshProUGUI, TMP_FontAsset
using MelonLoader;
using Il2CppInterop.Runtime;        // DelegateSupport (managed→Il2Cpp delegate conversion)
using TGBImg = Il2CppTGBTools.InputImage.InputImageStaticAction;
using Object = UnityEngine.Object;

namespace CairnAPI;

// A SCREEN-SPACE prompt: a self-sizing "[glyph] label" row built from a game InputPrompt, drawn on a mod-owned
// overlay Canvas (or parented into your own layout). Text + glyph are set ONCE — NO per-frame pump.
//
//   var h = ScreenPrompt.Show("Open the hatch", Glyph.Action(GameAction.Interact));  // own Canvas
//   ScreenPrompt.Move(h, new Vector2(0, -120));                                       // place it anywhere
//   ScreenPrompt.Show("Confirm", Glyph.Key("e"), parent: myRow.transform);           // inside your HLG row
//   ScreenPrompt.Hide(h);
//
// WHY NO PUMP (decompiled proof, re/systems/ui/prompts/DESIGN-prompt-primitives.md §2): nothing re-stamps a
// mod-owned off-rail widget. (a) the glyph component (InputImageStaticAction) has NO Update/coroutine — its
// keycap repaint fires only on a real keyboard↔gamepad device switch (input-glyph-system.md §4). (b) the loc
// re-stamp (LocalizedText.Refresh → set_text) is RAIL-internal: it only visits widgets the rail inserted into
// gameplayPrompts/promptUis. A TMP on our own GameObject is never visited, so a plain `tmp.text=` persists.
// So text + glyph are written exactly once at Show. (The OLD Tier-1/2 PromptTextPump existed only because the
// rail re-stamps POOLED widgets — see onRail below, which participates in that pipeline instead of fighting it.)
//
// Built from the proven self-sizing InputPrompt construction (formerly MenuPrompt): root TMP label + a
// left-anchored InputImageStaticAction glyph, fetching the game's own font/material/sprite by name. The row
// self-sizes; under a HorizontalLayoutGroup (childControlWidth=true) it lays out inline natively.
public static class ScreenPrompt
{
    /// <summary>
    /// Show a screen-space prompt. <paramref name="text"/> is shown as-is, <paramref name="glyph"/> is the
    /// button icon (see <see cref="Glyph"/>, null = none). If <paramref name="parent"/> is null the row goes
    /// under a mod-owned overlay Canvas — place it with <see cref="Move"/>; if given, the row is parented there
    /// (self-sizes for a HorizontalLayoutGroup). <paramref name="onRail"/> joins the game's prompt rail to
    /// inherit its fade/priority/stacking (opt-in; see §3 — NEW, pending live verification of the parser seam).
    /// Returns a handle, or <see cref="ScreenPromptHandle.Invalid"/> if a required game asset isn't loaded yet.
    /// </summary>
    public static ScreenPromptHandle Show(string text, InputAction glyph = null, Transform parent = null,
        bool onRail = false)
    {
        // onRail (rail fade/priority/stacking via the parser seam) is DESIGNED but NOT yet live-proven — the
        // convenience-register path rendered a stray glyph with no row/text on screen (see ShowOnRail). Until the
        // rail-row construction is verified, onRail falls back to the proven own-Canvas path so callers always get
        // a working prompt. The parser-seam wiring is retained below for the investigation; it is not wired in.
        if (onRail)
            MelonLogger.Warning("[CairnAPI] ScreenPrompt onRail not yet verified — using own-Canvas fallback.");

        // The font is the load-bearing asset: without it the label can't render and the row has no width.
        var font = PromptAssets.Find<TMP_FontAsset>("Cairn_TextFont");
        if (font == null) return ScreenPromptHandle.Invalid;

        var host = parent != null ? parent : OverlayCanvas();
        if (host == null) return ScreenPromptHandle.Invalid;

        var root = BuildRoot(host);
        // Own-Canvas prompts anchor/pivot CENTER so Move()'s coords are screen-center-relative (a layout-group
        // child keeps the top-left anchor the HLG expects). Without this the top-left anchor + a positive Move Y
        // pushes the row off the top of the screen (live-proven: rectPos landed at y=1100 on a 900px screen).
        if (parent == null)
        {
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;
        }
        var label = BuildLabel(root, font);
        var glyphComp = BuildGlyph(root, font);
        var prompt = AddPrompt(root, glyphComp);
        if (prompt == null) { Object.Destroy(root.gameObject); return ScreenPromptHandle.Invalid; }

        var h = new ScreenPromptHandle(root, prompt, glyphComp, label, parent == null);
        h.SetText(text);          // set ONCE — no pump (see header)
        h.SetGlyph(glyph);        // set ONCE — repaints only on a real device switch
        return h;
    }

    /// <summary>Place an own-Canvas prompt anywhere on screen (its RectTransform's anchoredPosition). No-op for
    /// a prompt parented into your own layout (let the layout group place it).</summary>
    public static void Move(ScreenPromptHandle handle, Vector2 anchoredPos)
    {
        if (handle == null || !handle.Valid || !handle.OwnCanvas) return;
        if (handle.Root != null) handle.Root.anchoredPosition = anchoredPos;
    }

    /// <summary>Change a live prompt's text in place (set once; persists with no pump).</summary>
    public static void SetText(ScreenPromptHandle handle, string text) => handle?.SetText(text);

    /// <summary>Show / hide the prompt (a layout group re-flows around a hidden one). Cheap — no re-register.</summary>
    public static void SetActive(ScreenPromptHandle handle, bool active) => handle?.SetActive(active);

    /// <summary>Destroy the prompt (and unregister it if it was on the rail). Safe on an invalid handle.</summary>
    public static void Hide(ScreenPromptHandle handle) => handle?.Destroy();

    // ── own overlay Canvas (created lazily, shared by every parent-less ScreenPrompt) ─────────────────────────

    // A mod-owned screen-space overlay Canvas: sortingOrder 30000 so it sits over the game HUD. Its visibility
    // is OUR own CanvasGroup — NOT subject to the game's MustHideGamePrompts / tutorial gates (those drive only
    // the shared GameplayPromptHud.canvasGroup, re/native/.../GameplayPromptHud/Update.c:27-52). DontDestroyOnLoad
    // so it survives scene loads like the game's own GlobalUIs.
    private static Transform _overlay;

    private static Transform OverlayCanvas()
    {
        if (_overlay != null) return _overlay;
        try
        {
            var go = new GameObject("CairnAPI.ScreenPromptCanvas") { layer = 5 };  // 5 = UI
            Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();
            _overlay = go.transform;
        }
        catch { _overlay = null; }
        return _overlay;
    }

    // ── on-rail: opt-in fade / priority / stacking via the LocalizedText.parser seam (§3) ─────────────────────

    // Register on the game's prompt rail with the REAL InputAction (the rail resolves the glyph itself), then
    // drive our literal through the rail's own text pipeline at the ONE per-widget seam it provides:
    // LocalizedText.parser (a public Func<string,string>, dump.cs:135297). Each rail rebuild runs
    // SetPromptData → ChangeLocKey → Refresh, which resolves the loc string THEN — if parser != null — replaces
    // it with parser(resolved) and calls set_text unconditionally (Refresh.c:37-42,59-60). So setting
    // parser = _ => literal makes our text land on every rebuild, pump-free, with fade/priority/stacking intact.
    //
    // NEW + UNVERIFIED-LIVE: the parser-seam paint is reasoned from the decompiles but not yet confirmed on
    // screen. Implemented DEFENSIVELY (try/catch, null-guards, never throws into the mod). The bound pooled
    // LocalizedText can be re-pooled to a different prompt; a tiny guard reasserts parser only when our prompt
    // is (re)bound — far cheaper than the old text-pump (one field on rebind, not a full re-stamp every frame).
    private static ScreenPromptHandle ShowOnRail(string text, InputAction glyph)
    {
        var hud = Hud;
        if (hud == null) return ScreenPromptHandle.Invalid;

        GameplayPromptHandle native;
        try
        {
            // Register under an empty loc key with the real action; the rail authors the glyph + keycap itself.
            native = hud.RegisterPrompt(
                EmptyLocKey(), glyph, true,
                null, false, false, 0f, false, 0f, null, false, null, false, false);
        }
        catch { return ScreenPromptHandle.Invalid; }

        var h = new ScreenPromptHandle(native, text ?? string.Empty);
        RailParserGuard.Track(h);   // sets/reasserts parser on the bound pooled LocalizedText (per-widget, on rebind)
        return h;
    }

    /// <summary>The one live prompt HUD (the rail), or null before in-game UI init.</summary>
    public static GameplayPromptHud Hud
    {
        get
        {
            var ui = MoSingleton<GlobalUIs>.Instance;
            return ui != null ? ui.gameplayPromptHud : null;
        }
    }

    // ── construction (lifted from the proven self-sizing InputPrompt row; built from scratch, no template) ────

    // The root: a UI-layer GameObject sized like the game's prompt (305×40), reporting a preferred size so a
    // parent HorizontalLayoutGroup (childControlWidth=true) places it inline. The label's 60px left margin
    // reserves the glyph's space; the glyph is an absolutely-anchored child floating over that margin.
    private static RectTransform BuildRoot(Transform parent)
    {
        var go = new GameObject("CairnAPI.ScreenPrompt") { layer = 5 };  // 5 = UI
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(305f, 40f);

        var le = go.AddComponent<LayoutElement>();   // report a preferred size so a parent HLG sizes+places us
        le.preferredWidth = 305f;
        le.preferredHeight = 40f;

        var fitter = go.AddComponent<ContentSizeFitter>();   // width follows the label so longer text widens us
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
        return rt;
    }

    // The visible label, on the ROOT GameObject: a TMP wrapped by a LocalizedText. 60px left margin leaves room
    // for the glyph keycap. We DISABLE the LocalizedText resolver (below, in SetText) so it never overwrites us.
    private static LocalizedText BuildLabel(RectTransform root, TMP_FontAsset font)
    {
        var tmp = root.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = font;                                     // set font FIRST — it resets the material…
        tmp.fontSharedMaterial = PromptAssets.LabelMaterial(font);  // …so set the atlas-matching material AFTER.
        tmp.fontSize = 26f;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Left;
        tmp.margin = new Vector4(60f, 0f, 0f, 0f);
        tmp.enableWordWrapping = true;
        tmp.raycastTarget = false;

        var loc = root.gameObject.AddComponent<LocalizedText>();
        loc.text = tmp;                                      // bind the LocalizedText to its sibling TMP explicitly
        loc.persistent = true;
        return loc;
    }

    // The keycap glyph: a 40×40 Image left-anchored at the row's start, driven by an InputImageStaticAction.
    // Keeps the DEFAULT UI material (NOT TGB_UI_FULLWHITE — that ignores the sprite and renders solid white).
    private static TGBImg BuildGlyph(RectTransform root, TMP_FontAsset font)
    {
        var go = new GameObject("Glyph") { layer = 5 };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(root, false);
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(40f, 40f);
        rt.anchoredPosition = new Vector2(2f, 0f);

        var img = go.AddComponent<Image>();                  // InputImageStaticAction [RequireComponent(Image)]
        var sprite = PromptAssets.Find<Sprite>("Key_Blank_Light");
        if (sprite != null) img.sprite = sprite;
        img.color = Color.white;
        img.type = Image.Type.Simple;
        img.preserveAspect = true;
        img.raycastTarget = false;

        var glyph = go.AddComponent<TGBImg>();
        glyph.useLayout = true;
        glyph.adaptiveBinding = true;                        // auto-pick the binding matching the active device
        glyph.bindingIndex = 0;
        glyph.fallback = true;                               // blank-keycap + letter fallback when no exact icon
                                                             // matches the path (else the manager returns null and
                                                             // nothing paints — RE-proven, InputImageManager)

        BuildGlyphValue(rt, font);
        return glyph;
    }

    // The nested "Value" letter slot — the cell the keycap's letter lands in. The glyph component drives it via
    // the icon DB; we leave the text empty (built for parity with the reference graph; harmless if blank).
    private static void BuildGlyphValue(RectTransform glyph, TMP_FontAsset font)
    {
        var go = new GameObject("Value") { layer = 5 };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(glyph, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(10f, 10f);
        rt.offsetMax = new Vector2(-10f, -10f);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.font = font;
        var mat = PromptAssets.Find<Material>("Cairn_TextFont NoZTestExceptAava")
               ?? PromptAssets.Find<Material>("Cairn_TextFont DropShadow");
        if (mat != null) tmp.fontSharedMaterial = mat;
        tmp.fontSize = 17f;
        tmp.color = new Color(0.22f, 0.212f, 0.196f, 1f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 8f;
        tmp.fontSizeMax = 72f;
        tmp.raycastTarget = false;
    }

    // Add the InputPrompt to the root and wire its glyph ref. AddComponent can throw if the type isn't
    // registered with the interop runtime, so guard it.
    private static InputPrompt AddPrompt(RectTransform root, TGBImg glyph)
    {
        try
        {
            var p = root.gameObject.AddComponent<InputPrompt>();
            p.inputImage = glyph;        // the glyph component the prompt drives
            p.holdText = null;           // do NOT point it at our label — the prompt would re-resolve its loc key
                                         // and clobber our plain text; no hold-to-confirm anyway
            p.inert = false;
            p.kbmEnabled = false;
            p.punchOnInput = false;
            return p;
        }
        catch { return null; }
    }

    private static ParametrizedLocKey EmptyLocKey() => new ParametrizedLocKey(new LocKeyStringId());
}

/// <summary>Opaque handle to a shown <see cref="ScreenPrompt"/>. Pass it back to <see cref="ScreenPrompt.Hide"/>.</summary>
public sealed class ScreenPromptHandle
{
    // off-rail (own widget): the row we built and its cached components.
    internal readonly RectTransform Root;
    private readonly InputPrompt _prompt;
    private readonly TGBImg _glyph;
    private readonly LocalizedText _label;
    internal readonly bool OwnCanvas;

    // on-rail: the native handle + the literal the parser-guard keeps applied.
    internal GameplayPromptHandle Native;
    internal string Text;
    internal readonly bool OnRail;

    private bool _valid;

    internal ScreenPromptHandle(RectTransform root, InputPrompt prompt, TGBImg glyph, LocalizedText label, bool ownCanvas)
    {
        Root = root; _prompt = prompt; _glyph = glyph; _label = label; OwnCanvas = ownCanvas;
        OnRail = false; _valid = true;
    }

    internal ScreenPromptHandle(GameplayPromptHandle native, string text)
    {
        Native = native; Text = text ?? string.Empty; OnRail = true; _valid = true;
    }

    private ScreenPromptHandle() { _valid = false; }
    /// <summary>A no-op handle returned when a required asset / the HUD wasn't available.</summary>
    public static readonly ScreenPromptHandle Invalid = new();

    public bool Valid => _valid;
    internal void Invalidate() => _valid = false;
    internal int Id => Native.id;

    // Set the visible label to a plain string. We DISABLE the LocalizedText resolver — its (empty) loc key would
    // otherwise resolve to "[none_string]" on first refresh and overwrite us (persistent alone didn't hold;
    // disabling the resolver is the reliable fix). The TMP it wraps keeps rendering our text. Set once — no pump.
    internal void SetText(string text)
    {
        if (!_valid) return;
        Text = text ?? string.Empty;
        if (OnRail) return;   // the rail path carries text via the parser guard, not the label TMP
        try
        {
            var tmp = _label != null ? _label.text : null;
            if (tmp != null)
            {
                _label.enabled = false;
                tmp.text = Text;
                tmp.enabled = true;
            }
        }
        catch { }
    }

    // Set the glyph from an InputAction. Drives the row's InputImageStaticAction so the keycap/letter resolves
    // for the active device, exactly like a native prompt. Set once — repaints only on a real device switch.
    internal void SetGlyph(InputAction action)
    {
        if (!_valid || OnRail) return;
        try
        {
            if (_glyph != null)
            {
                _glyph.InputAction = action;
                _glyph.adaptiveBinding = true;          // active-device binding (keyboard vs gamepad)
                _glyph.fallback = true;
                if (action != null) _glyph.Refresh();   // set defers the repaint; Refresh paints now
            }
        }
        catch { }
    }

    /// <summary>Show / hide the prompt (the layout group re-flows around a hidden one).</summary>
    public void SetActive(bool active)
    {
        if (!_valid || OnRail) return;   // a rail prompt's visibility is the rail's (unregister to hide)
        if (_prompt != null) _prompt.gameObject.SetActive(active);
    }

    /// <summary>Destroy the prompt GameObject (or unregister it from the rail). Idempotent.</summary>
    public void Destroy()
    {
        if (!_valid) return;
        _valid = false;
        if (OnRail)
        {
            RailParserGuard.Untrack(this);
            var hud = ScreenPrompt.Hud;
            var native = Native;
            if (hud != null)
                try { hud.UnregisterPrompt(ref native); } catch { }
            return;
        }
        if (_prompt != null)
            try { Object.Destroy(_prompt.gameObject); } catch { }
    }
}

// Drives the on-rail parser seam: finds the pooled LocalizedText the rail bound to each tracked prompt and sets
// its `parser` to return our literal, reasserting only when the prompt (re)binds to a (possibly re-pooled) slot.
// Touches one field on rebind — NOT a full text+glyph re-stamp every frame (the old PromptTextPump), so it is
// strictly cheaper. Runs only while ≥1 on-rail prompt exists. NEW + UNVERIFIED-LIVE (see ScreenPrompt.ShowOnRail).
internal static class RailParserGuard
{
    private static readonly System.Collections.Generic.List<ScreenPromptHandle> Tracked = new();
    private static object _coroutine;

    internal static void Track(ScreenPromptHandle h)
    {
        if (h == null || !h.Valid) return;
        if (!Tracked.Contains(h)) Tracked.Add(h);
        if (_coroutine == null) _coroutine = MelonCoroutines.Start(Pump());
    }

    internal static void Untrack(ScreenPromptHandle h) => Tracked.Remove(h);

    private static System.Collections.IEnumerator Pump()
    {
        while (true)
        {
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                var h = Tracked[i];
                if (h == null || !h.Valid) { Tracked.RemoveAt(i); continue; }
                Reassert(h);
            }
            if (Tracked.Count == 0) { _coroutine = null; yield break; }
            yield return null;
        }
    }

    // Find this handle's bound GameplayPromptUI (promptUis[i] ↔ gameplayPrompts[i], by handle id) and set its
    // label's parser to our literal. Idempotent — assigning the same delegate each rebind is harmless; the point
    // is to (re)apply after the rail re-pools the slot to us. Defensive throughout; never throws into the mod.
    private static void Reassert(ScreenPromptHandle h)
    {
        try
        {
            var hud = ScreenPrompt.Hud;
            var set = hud != null ? hud.bigGameplayPromptSet : null;
            if (set == null) return;
            var prompts = set.gameplayPrompts;
            var uis = set.promptUis;
            if (prompts == null || uis == null) return;

            for (int i = 0; i < prompts.Count && i < uis.Count; i++)
            {
                var live = prompts[i];
                if (live == null || live.handle.id != h.Id) continue;
                var ui = uis[i];
                var label = ui != null ? ui.label : null;
                if (label == null) return;
                var literal = h.Text;
                // The rail's Refresh runs Get(locKey) THEN parser(resolved) → our literal (Refresh.c:37-42).
                // parser is an Il2Cpp Func<string,string> field — convert our managed delegate (the pattern in
                // CairnCoop/GhostRopeGesture.cs:102, CairnRoutes/EditField.cs:121).
                label.parser = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<string, string>>(
                    (Func<string, string>)((_) => literal));
                return;
            }
        }
        catch { }
    }
}
