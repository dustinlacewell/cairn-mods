using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

namespace CairnAPI;

// The uGUI design-system layer: the handful of builder idioms the views compose from, so no
// view touches raw AddComponent / RectTransform / layout-group wiring. Real Unity layout
// components (Vertical/HorizontalLayoutGroup, LayoutElement, ContentSizeFitter) do the sizing —
// no hand-placed rects. All verified live in this Il2Cpp build (Canvas, Image, Text, Button
// onClick lambdas, InputField onValueChanged, ScrollRect, RectMask2D).
//
// Lives in CairnAPI (the shared base) so every mod composes its UI from one toolkit — CairnTools'
// SpawnWindow, CairnCoop's HUDs, etc. — instead of copying the builder idioms per assembly.
public static class Ui
{
    public static Font Font;   // resolved once at startup (LegacyRuntime / Arial)

    public static readonly Color Bg        = new(0.10f, 0.10f, 0.12f, 0.94f);
    public static readonly Color Header    = new(0.16f, 0.16f, 0.20f, 1f);
    public static readonly Color RowBg      = new(0.18f, 0.18f, 0.22f, 1f);
    public static readonly Color RowAlt     = new(0.21f, 0.21f, 0.26f, 1f);
    public static readonly Color Accent     = new(0.30f, 0.55f, 0.85f, 1f);
    public static readonly Color AccentDim  = new(0.24f, 0.40f, 0.58f, 1f);
    public static readonly Color Text       = new(0.90f, 0.91f, 0.94f, 1f);
    public static readonly Color TextDim    = new(0.60f, 0.62f, 0.68f, 1f);
    public static readonly Color FieldBg    = new(0.06f, 0.06f, 0.08f, 1f);

    public static void Init()
    {
        if (Font == null)
            Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    // A child GameObject with a RectTransform, parented under `parent`.
    public static RectTransform Node(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    public static Image Panel(RectTransform rt, Color color)
    {
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        return img;
    }

    // Stretch a RectTransform to fill its parent with optional padding.
    public static RectTransform Fill(RectTransform rt, float pad = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
        return rt;
    }

    public static VerticalLayoutGroup VStack(RectTransform rt, float spacing, RectOffset padding = null)
    {
        var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = spacing;
        v.childControlWidth = true; v.childControlHeight = true;
        v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        if (padding != null) v.padding = padding;
        return v;
    }

    public static HorizontalLayoutGroup HStack(RectTransform rt, float spacing, RectOffset padding = null)
    {
        var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = spacing;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = true;
        h.childAlignment = TextAnchor.MiddleLeft;
        if (padding != null) h.padding = padding;
        return h;
    }

    public static LayoutElement Sized(RectTransform rt, float? w = null, float? h = null, bool flexW = false)
    {
        var le = rt.gameObject.AddComponent<LayoutElement>();
        if (w.HasValue) le.preferredWidth = w.Value;
        if (h.HasValue) le.preferredHeight = h.Value;
        if (flexW) le.flexibleWidth = 1f;
        return le;
    }

    public static Text Label(RectTransform parent, string text, int size, Color color,
        TextAnchor align = TextAnchor.MiddleLeft)
    {
        var rt = Node("Label", parent);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = Font; t.fontSize = size; t.color = color; t.text = text;
        t.alignment = align; t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        return t;
    }

    public static Button Button(RectTransform parent, string label, Color bg, Action onClick,
        float? width = null, int fontSize = 13)
    {
        var rt = Node("Button", parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = bg;
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        if (onClick != null) btn.onClick.AddListener((UnityAction)(() => onClick()));
        var label2 = Label(rt, label, fontSize, Text, TextAnchor.MiddleCenter);
        Fill((RectTransform)label2.transform);
        if (width.HasValue) Sized(rt, w: width.Value, h: 24f);
        else Sized(rt, h: 24f);
        return btn;
    }

    // A labelled text field with placeholder; onChanged fires per keystroke.
    public static InputField Input(RectTransform parent, string placeholder, Action<string> onChanged)
    {
        var rt = Node("Input", parent);
        Panel(rt, FieldBg);
        Sized(rt, h: 26f, flexW: true);

        var textRt = Fill(Node("Text", rt), 5f);
        var text = textRt.gameObject.AddComponent<Text>();
        text.font = Font; text.fontSize = 14; text.color = Text; text.supportRichText = false;
        text.alignment = TextAnchor.MiddleLeft;

        var phRt = Fill(Node("Placeholder", rt), 5f);
        var ph = phRt.gameObject.AddComponent<Text>();
        ph.font = Font; ph.fontSize = 14; ph.color = TextDim; ph.text = placeholder;
        ph.alignment = TextAnchor.MiddleLeft; ph.fontStyle = FontStyle.Italic;

        var input = rt.gameObject.AddComponent<InputField>();
        input.textComponent = text;
        input.placeholder = ph;
        if (onChanged != null)
            input.onValueChanged.AddListener((UnityAction<string>)(s => onChanged(s)));
        return input;
    }

    // A vertical scroll viewport; returns the content RectTransform (a VStack) to fill with rows.
    public static RectTransform ScrollList(RectTransform parent, out ScrollRect scroll)
    {
        var viewport = Node("Viewport", parent);
        Panel(viewport, new Color(0, 0, 0, 0.15f));
        viewport.gameObject.AddComponent<RectMask2D>();
        Sized(viewport, flexW: true).flexibleHeight = 1f;

        scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24f;

        var content = Node("Content", viewport);
        content.anchorMin = new Vector2(0, 1);
        content.anchorMax = new Vector2(1, 1);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0, 0);
        var v = VStack(content, 2f, new RectOffset(2, 2, 2, 2));
        var fit = content.gameObject.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = viewport;
        scroll.content = content;
        return content;
    }
}
