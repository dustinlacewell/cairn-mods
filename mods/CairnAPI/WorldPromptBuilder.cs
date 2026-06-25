using System;
using System.Collections.Generic;
using Il2Cpp;                       // WorldGameplayPromptUI, MaskableCanvas, LocalizedText, GlobalUIs, MoSingleton
using Il2CppTheGameBakers.Cairn;    // WorldUIAfterImageEffects — the world-UI command-buffer renderer
using UnityEngine;
using UnityEngine.UI;               // Image, Canvas, CanvasScaler, CanvasGroup, VerticalLayoutGroup, ContentSizeFitter, LayoutElement
using Il2CppTMPro;                  // TextMeshProUGUI, TMP_FontAsset
using Il2CppInterop.Runtime;        // IL2CPP field poke (private base-class refs)
using TGBImg = Il2CppTGBTools.InputImage.InputImageStaticAction;
using Object = UnityEngine.Object;

namespace CairnAPI;

// Builds a SECOND, independent WorldGameplayPromptUI from scratch — its own WorldSpace canvas, label, glyph —
// so a mod's in-world prompt coexists with the game's single shared widget (GlobalUIs.worldGameplayPromptUi)
// instead of clobbering it. WorldPrompts.cs consumes the result via Display/Hide/UpdateSizeAndPosition.
//
// WHY FROM SCRATCH (no Instantiate): a clone drags the template's whole child tree + internal references,
// and there is no prompt PREFAB to clone in the first place (the widget is authored once into GlobalUIs). So
// we reconstruct the MINIMAL functional tree, fetch the game's own fonts/materials/sprites by name, and wire
// every serialized ref the runtime dereferences. RE-grounded in re/native/.../WorldGameplayPromptUI/*.c —
// the tree mirrors the resident widget's subtree and satisfies every non-null gate in Awake/Display/
// UpdateSizeAndPosition/OnMainCameraPreCull (else those calls hit the native breakpoint / NRE):
//
//   WorldPrompt root      WorldSpace Canvas + MaskableCanvas + CanvasGroup + WorldGameplayPromptUI
//     Root                billboardRoot (OnMainCameraPreCull rotates this)
//       Signal            signalImage  (Image; Display/UpdateSizeAndPosition toggle + color it)
//         InputImage      inputImage   (InputImageStaticAction + Image — the glyph keycap)
//           Label&Picto   worldLabel (RectTransform; Display reads anchoredPosition) + labelLayout (VLG)
//             PictoParent / Unavailability / Icon   spriteRenderer (Awake derefs its rectTransform for a shaker)
//                 …/MaskNotification                maskImage
//                   …/MaskedLabel                   unavailabilityLabel (Display ChangeLocKey's it — must be non-null)
//             Label       label (LocalizedText wrapping the visible TMP we stamp)
//
// Live-proven: Display returns a valid handle, the widget renders its own label + Key_Blank_Light glyph at the
// anchor, world pos == anchor pos, and the shared widget stays untouched (distinct object, keeps its own text).
public static class WorldPromptBuilder
{
    // ── TMP sub-mesh routing (THE world-text fix) ─────────────────────────────────────────────────────────
    //
    // The world-UI renderer (WorldUIAfterImageEffects.AddGraphic) draws TMP text ONLY via TMP_SubMeshUI meshes,
    // never the parent CanvasRenderer mesh (re/systems/ui/prompts/world-ui-afterimage-render.md §5). A TMP
    // produces a sub-mesh only when its glyphs resolve to a NON-PRIMARY material reference (index ≥1).
    //
    // THE MECHANISM (live-proven, matches the game's own value TMP byte-for-byte): set `fontStyle = Bold`.
    // Cairn_TextFont.fontWeightTable[7] (weight 700 = Bold) → regularTypeface = "Boxed-DemiBold SDF". So a BOLD
    // TMP resolves every glyph through that non-primary font asset → material reference index 1 → a TMP_SubMeshUI
    // whose material is derived from the TMP's own (No-ZTest) fontSharedMaterial → drawn in the after-transparent
    // world pass. The game's resident value TMP IS authored fontStyle=Bold for exactly this reason (the ONLY
    // property that differed between it and ours, live-diffed). hasCharacter('E') is true on the PRIMARY, so this
    // is a deliberate weight-route, not a missing-glyph fallback.
    //
    // WHY THIS over the old `<font="Boxed-DemiBold SDF">` tag: fontStyle is a TMP COMPONENT property, immune to
    // text rewrites. The value TMP's letter is rewritten every device-update by the glyph component
    // (InputImageStatic.OnDeviceUpdate → TMP.set_text(plainLetter), re/native/TGBTools/.../OnDeviceUpdate.c:73),
    // which would CLOBBER any inline tag back to no-sub-mesh — so the tag route could never work for the value.
    // With Bold, the route survives every plain-text write (live-proven across repeated set_text calls), so
    // NEITHER the build nor the per-frame pump needs to tag-wrap. One build-time call, robust forever.

    /// <summary>Configure a world-prompt TMP so PLAIN text (written by us or the glyph component) routes to a
    /// TMP_SubMeshUI and renders through the after-image pass: fontStyle=Bold → Boxed-DemiBold SDF (index ≥1).
    /// A component-level property, so it survives the glyph's per-frame raw text writes — no tag-wrapping.</summary>
    public static void WorldTextStyle(TextMeshProUGUI tmp)
    {
        if (tmp == null) return;
        tmp.richText = true;
        tmp.fontStyle = FontStyles.Bold;
    }

    /// <summary>
    /// Construct a fresh, independent WorldGameplayPromptUI under GlobalUIs (DontDestroyOnLoad). Returns the
    /// live component (Awake/OnEnable have run; the widget self-hides until Display), or null pre-asset-load
    /// (the build needs the game's fonts/materials/sprites loaded) or on any construction failure.
    /// </summary>
    public static WorldGameplayPromptUI Build()
    {
        // The font is the load-bearing asset: no font → no label geometry, and we're surely pre-game.
        var font = FindAsset<TMP_FontAsset>("Cairn_TextFont");
        if (font == null) return null;

        try
        {
            var root = BuildRoot(out var canvas, out var maskable, out var canvasGroup);
            var billboard = BuildBillboard(root);
            var signal = BuildSignal(billboard, out var signalImg);
            var inputImage = BuildGlyph(signal, font, out var glyphImg);
            var labelPicto = BuildLabelPicto(inputImage, out var labelLayout);
            var spriteRenderer = BuildUnavailabilityBranch(labelPicto, font, out var maskImg, out var unavailLabel);
            var label = BuildLabel(labelPicto, font);

            var ui = root.gameObject.AddComponent<WorldGameplayPromptUI>();
            WireFields(ui, maskable, canvasGroup, inputImage, label, billboard, signalImg, labelPicto, labelLayout);
            WireBaseRefs(ui, spriteRenderer, maskImg, unavailLabel);

            // THE renderer for a WorldSpace UI here: WorldUIAfterImageEffects collects this canvas's Graphics
            // (Awake→InitializeGraphicsData = GetComponentsInChildren<Graphic>) and its OnEnable self-registers
            // into CairnPostProcessAfterTransparent's static list, whose OnRenderImage pass calls
            // AddToCommandBuffer→AddGraphic per widget each frame. The canvas's worldCamera is null, so URP never
            // auto-draws it — without this component the tree builds, has geometry, but is NEVER submitted. The
            // resident widget carries one; ours must too. Added BEFORE activation so its Awake sees the full tree.
            var afterImage = root.gameObject.AddComponent<WorldUIAfterImageEffects>();
            afterImage.canvas = canvas;
            afterImage.canvasGroup = canvasGroup;

            Parent(root, MoSingleton<GlobalUIs>.Instance);
            root.gameObject.SetActive(true);   // activate LAST → Awake/OnEnable run with every ref already wired
            return ui;
        }
        catch { return null; }
    }

    // ── tree construction (top-down; each node mirrors the resident widget) ───────────────────────────────

    // The root: a tiny (0.01 scale) WorldSpace canvas. MaskableCanvas.get_Canvas lazily GetComponent<Canvas>()s,
    // so the Canvas + MaskableCanvas just need to share this GO. CanvasGroup starts opaque (the game fades from
    // 0; we want immediate visibility). Built inactive so AddComponent<WorldGameplayPromptUI> doesn't fire Awake
    // before its refs are wired.
    private static RectTransform BuildRoot(out Canvas canvas, out MaskableCanvas maskable, out CanvasGroup canvasGroup)
    {
        var go = new GameObject("CairnAPI.WorldPrompt") { layer = 5 };
        go.SetActive(false);
        var rt = go.AddComponent<RectTransform>();
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        go.AddComponent<CanvasScaler>();
        canvasGroup = go.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 1f;
        maskable = go.AddComponent<MaskableCanvas>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(1.2f, 85f);
        rt.localScale = new Vector3(0.01f, 0.01f, 0.01f);
        return rt;
    }

    // billboardRoot — OnMainCameraPreCull sets this transform's rotation to face the camera.
    private static RectTransform BuildBillboard(RectTransform parent)
    {
        var rt = NewUI("Root", parent);
        Stretch(rt);
        return rt;
    }

    // signalImage — the leader LINE: a GradientFilling sprite stretched vertically; UpdateSizeAndPosition sizes
    // its height from the anchor distance (so a zeroed WorldPromptStyle = no line). No-ZTest material so it draws
    // in the after-transparent world pass.
    private static RectTransform BuildSignal(RectTransform parent, out Image signalImg)
    {
        var rt = NewUI("Signal", parent);
        Stretch(rt);
        signalImg = rt.gameObject.AddComponent<Image>();
        signalImg.sprite = FindAsset<Sprite>("GradientFilling");
        var sigMat = ImageNoZTest();
        if (sigMat != null) signalImg.material = sigMat;
        signalImg.type = Image.Type.Simple;
        signalImg.raycastTarget = false;
        return rt;
    }

    // inputImage — the glyph keycap. InputImageStaticAction [RequireComponent(Image)] resolves the device-correct
    // sprite/letter; fallback=true paints the blank keycap when no DB icon matches the path (else nothing draws).
    //
    // The "Value" child TMP is LOAD-BEARING: InputImageStatic.get_TextMesh lazily resolves textMesh via
    // GetComponentInChildren<TextMeshProUGUI> from THIS GO and OnDeviceUpdate writes the key letter ("E") into it.
    // Without an own TMP under the glyph, that lookup falls THROUGH to our visible Label (a deeper descendant) and
    // stamps the keycap letter over the prompt text. The resident widget has exactly this "Value" TMP for the same
    // reason. We also poke textMesh directly so the resolution can't drift to the Label regardless of child order.
    private static TGBImg BuildGlyph(RectTransform parent, TMP_FontAsset font, out Image glyphImg)
    {
        var rt = NewUI("InputImageAction", parent);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(20f, 20f);
        rt.anchoredPosition = new Vector2(0f, 8f);
        glyphImg = rt.gameObject.AddComponent<Image>();
        var keycapMat = ImageNoZTest();          // No-ZTest so the keycap draws in the world pass
        if (keycapMat != null) glyphImg.material = keycapMat;
        glyphImg.raycastTarget = false;
        var glyph = rt.gameObject.AddComponent<TGBImg>();
        glyph.useLayout = true;
        glyph.adaptiveBinding = true;
        glyph.bindingIndex = 0;
        glyph.fallback = true;

        // The glyph's own letter sink — first child so GetComponentInChildren resolves to it, not the Label.
        // No-ZTest material (routes glyphs to a sub-mesh so they draw) + the game's dark keycap-letter color.
        var valRt = NewUI("Value", rt);
        Stretch(valRt);
        var valTmp = valRt.gameObject.AddComponent<TextMeshProUGUI>();
        valTmp.font = font;
        var valMat = TmpNoZTest(font, "Cairn_TextFont NoZTestExceptAava");
        if (valMat != null) valTmp.fontSharedMaterial = valMat;
        valTmp.fontSize = 12f;
        valTmp.color = new Color(0.22f, 0.212f, 0.196f, 1f);   // dark letter on the cream keycap (matches the game)
        valTmp.alignment = TextAlignmentOptions.Center;
        valTmp.raycastTarget = false;
        WorldTextStyle(valTmp);   // Bold → glyph resolves through Boxed-DemiBold SDF (index ≥1) → sub-mesh → drawn
        PokeField(glyph, "textMesh", valTmp);   // pin it; defeats the GetComponentInChildren fall-through

        return glyph;
    }

    // worldLabel (Display reads its anchoredPosition into originalAnchoredPosition) + labelLayout (the VLG that
    // stacks the picto + label). A ContentSizeFitter lets it self-size to the label's preferred size.
    private static RectTransform BuildLabelPicto(TGBImg glyph, out VerticalLayoutGroup labelLayout)
    {
        var rt = NewUI("Label&Picto", glyph.transform);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, 38f);
        labelLayout = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        labelLayout.childControlWidth = true;
        labelLayout.childControlHeight = true;
        labelLayout.childForceExpandWidth = true;
        labelLayout.childForceExpandHeight = false;
        labelLayout.childAlignment = TextAnchor.MiddleCenter;
        labelLayout.spacing = 5f;
        var fit = rt.gameObject.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return rt;
    }

    // The unavailability branch carries two REQUIRED base-class refs: spriteRenderer (Awake derefs its
    // rectTransform to build a DOTween shaker — null ⇒ native breakpoint) and unavailabilityLabel (Display
    // ChangeLocKey's it unconditionally — null ⇒ native breakpoint). We build the minimal chain that hosts them.
    private static Image BuildUnavailabilityBranch(RectTransform labelPicto, TMP_FontAsset font, out Image maskImg, out LocalizedText unavailLabel)
    {
        var picto = NewUI("PictoParent", labelPicto);
        var unavail = NewUI("UnavailabilityNotification", picto); Stretch(unavail);

        var iconRt = NewUI("InteractionNotificationIcon", unavail); Stretch(iconRt);
        var spriteRenderer = iconRt.gameObject.AddComponent<Image>();   // base spriteRenderer
        spriteRenderer.raycastTarget = false;

        var maskRt = NewUI("MaskNotification", unavail);
        maskImg = maskRt.gameObject.AddComponent<Image>();              // base maskImage
        maskImg.raycastTarget = false;

        var bg = NewUI("MaskedBackgroundNotification", maskRt);
        var lblRt = NewUI("MaskedLabelNotification", bg);
        var lblTmp = lblRt.gameObject.AddComponent<TextMeshProUGUI>();
        lblTmp.font = font;
        lblTmp.fontSize = 10f;
        lblTmp.raycastTarget = false;
        unavailLabel = lblRt.gameObject.AddComponent<LocalizedText>();  // base unavailabilityLabel
        unavailLabel.text = lblTmp;
        unavailLabel.persistent = true;
        return spriteRenderer;
    }

    // label — the visible "[glyph] label" text: a TMP wrapped by a LocalizedText. WorldPrompts.cs writes the TMP
    // (label.text) each frame for the plain string. Drop-shadow material if its atlas matches the font's.
    private static LocalizedText BuildLabel(RectTransform labelPicto, TMP_FontAsset font)
    {
        var rt = NewUI("Label", labelPicto);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0f);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.font = font;
        var mat = LabelMaterial(font);
        if (mat != null) tmp.fontSharedMaterial = mat;
        tmp.fontSize = 12f;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        WorldTextStyle(tmp);   // Bold → plain label text routes to a sub-mesh → drawn (no per-frame tag-wrap needed)
        var loc = rt.gameObject.AddComponent<LocalizedText>();
        loc.text = tmp;
        loc.persistent = true;
        return loc;
    }

    // ── reference wiring ──────────────────────────────────────────────────────────────────────────────────

    // The WorldGameplayPromptUI's own serialized refs (interop-exposed). canInteract/cannotInteract are the
    // label/signal tint colors Display applies. isUniqueInstance=false: we are NOT the GlobalUIs singleton, so
    // suppress the uniqueness LogError Awake would emit. localScale is the per-axis world scale UpdateScale uses.
    private static void WireFields(WorldGameplayPromptUI ui, MaskableCanvas maskable, CanvasGroup canvasGroup,
        TGBImg inputImage, LocalizedText label, RectTransform billboard, Image signalImg,
        RectTransform worldLabel, VerticalLayoutGroup labelLayout)
    {
        ui.maskableCanvas = maskable;
        ui.canvasGroup = canvasGroup;
        ui.inputImage = inputImage;
        ui.label = label;
        ui.billboardRoot = billboard.transform;
        ui.signalImage = signalImg;
        ui.worldLabel = worldLabel;
        ui.labelLayout = labelLayout;
        ui.canInteractColor = Color.white;
        ui.cannotInteractColor = new Color(0.902f, 0.902f, 0.902f, 0.749f);
        ui.isUniqueInstance = false;
        ui.localScale = new Vector3(0.01f, 0.01f, 0.01f);
    }

    // spriteRenderer / maskImage / unavailabilityLabel are PRIVATE on the base PlayerActionPromptBase and not
    // surfaced on the interop wrapper, so set them by poking the il2cpp field directly (GC write-barrier).
    private static void WireBaseRefs(WorldGameplayPromptUI ui, Image spriteRenderer, Image maskImg, LocalizedText unavailLabel)
    {
        PokeField(ui, "spriteRenderer", spriteRenderer);
        PokeField(ui, "maskImage", maskImg);
        PokeField(ui, "unavailabilityLabel", unavailLabel);
    }

    private static unsafe void PokeField(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase owner, string fieldName, Object value)
    {
        var basePtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(owner);
        var klass = IL2CPP.il2cpp_object_get_class(basePtr);
        IntPtr field = IntPtr.Zero;
        for (var c = klass; c != IntPtr.Zero; c = IL2CPP.il2cpp_class_get_parent(c))
        {
            field = IL2CPP.GetIl2CppField(c, fieldName);
            if (field != IntPtr.Zero) break;
        }
        if (field == IntPtr.Zero) return;   // field moved (game update) — leave null; caller's try/catch & guards cope
        var off = (int)IL2CPP.il2cpp_field_get_offset(field);
        var valPtr = value == null ? IntPtr.Zero : IL2CPP.Il2CppObjectBaseToPtr(value);
        IL2CPP.il2cpp_gc_wbarrier_set_field(basePtr, (IntPtr)((nint)basePtr + off), valPtr);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private static RectTransform NewUI(string name, Transform parent)
    {
        var go = new GameObject(name) { layer = parent != null ? parent.gameObject.layer : 5 };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = Vector2.zero;
    }

    private static void Parent(RectTransform root, GlobalUIs gui)
    {
        if (gui != null) root.SetParent(gui.transform, false);
    }

    // The label's shared material — the drop-shadow No-ZTest variant. See TmpNoZTest for the atlas-match rule.
    private static Material LabelMaterial(TMP_FontAsset font)
        => TmpNoZTest(font, "Cairn_TextFont NoZTestExceptAava DropShadow");

    // A TMP No-ZTest material, ATLAS-MATCHED. World-UI renders after transparent with the scene depth buffer
    // bound, so a ZTest material is occluded → invisible; the No-ZTest variant bypasses it. CRUCIALLY, assigning
    // this (NOT the font's primary material) as fontSharedMaterial routes the glyphs to material reference
    // index ≥1 → a TMP_SubMeshUI, the ONLY thing the world-UI renderer draws for text (re/systems/ui/
    // GUIDE-building-ui-in-cairn.md GATE 4). The material NAME is not unique — multiple instances point at
    // different (Boxed-*) atlases → wingdings; accept only the one whose atlas == the font's atlas.
    private static Material TmpNoZTest(TMP_FontAsset font, string name)
    {
        var fontMat = font != null ? font.material : null;
        var atlas = fontMat != null ? fontMat.mainTexture : null;
        try
        {
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.name == name && atlas != null && m.mainTexture == atlas)
                    return m;
        }
        catch { }
        return fontMat;   // atlas-correct fallback (ZTest → may not render in the world pass, but never garbage)
    }

    // The No-ZTest Image material for the keycap + leader-line (unique by name — safe). Bypasses the depth
    // rejection in the after-transparent world-UI pass. Falls back to the default UI material if absent.
    private static Material ImageNoZTest()
        => FindAsset<Material>("UI_Default No ZTestExceptAava");

    // Find an already-loaded asset by exact name (the game ships these; we never recreate them). Cached per
    // (type,name) — FindObjectsOfTypeAll is a full scan. Null pre-game (assets not yet loaded).
    private static readonly Dictionary<string, Object> _assetCache = new();

    private static T FindAsset<T>(string name) where T : Object
    {
        var key = typeof(T).FullName + "\0" + name;
        if (_assetCache.TryGetValue(key, out var cached) && cached != null)
            return cached as T;
        try
        {
            foreach (var a in Resources.FindObjectsOfTypeAll<T>())
                if (a != null && a.name == name)
                {
                    _assetCache[key] = a;
                    return a;
                }
        }
        catch { }
        return null;
    }
}
