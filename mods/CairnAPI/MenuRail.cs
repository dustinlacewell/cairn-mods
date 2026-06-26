using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace CairnAPI;

// Low-level primitive: inject one labeled button into any BouncingButtons rail.
// Dispose the returned handle to remove it.
internal static class MenuRail
{
    internal static Entry AddEntry(
        BouncingButtons bb,
        Transform parent,
        string label,
        Action onClick,
        string insertBefore = null)
    {
        var dict = bb.ButtonsData;
        if (dict == null) throw new InvalidOperationException("BouncingButtons.ButtonsData is null.");

        var font = FindFont("Cairn_TextFont");
        if (font == null) throw new InvalidOperationException("Cairn_TextFont not found.");

        // ── Root GO ───────────────────────────────────────────────────────────
        var go = new GameObject(label);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot     = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(471f, 55f);

        go.AddComponent<CanvasRenderer>();
        var bg = go.AddComponent<Image>();
        bg.color   = new Color(0f, 0f, 0f, 0f);
        bg.enabled = false;

        var bwme = go.AddComponent<ButtonWithMoreEvents>();
        bwme.onClick.AddListener(
            DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(onClick));

        // ── Label child ───────────────────────────────────────────────────────
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        labelGo.AddComponent<CanvasRenderer>();
        var labelRt = labelGo.AddComponent<RectTransform>();
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        var tmp = labelGo.AddComponent<Il2CppTMPro.TextMeshProUGUI>();
        tmp.font      = font;
        tmp.fontSize  = 38f;
        tmp.color     = new Color(0.784f, 0.784f, 0.784f, 1f);
        tmp.margin    = new Vector4(32f, 0f, 0f, 0f);
        tmp.text      = label;
        tmp.alignment = Il2CppTMPro.TextAlignmentOptions.Left;

        // ── Sibling order ─────────────────────────────────────────────────────
        if (insertBefore != null)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).name == insertBefore)
                { go.transform.SetSiblingIndex(i); break; }
            }
        }

        // ── BouncingButtons registration ──────────────────────────────────────
        bwme.add_OnPointerEntered(
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ButtonWithMoreEvents>>(
                (System.Action<ButtonWithMoreEvents>)(_ => bb.SelectOnPointerEnter(bwme))));
        dict.Add(bwme, new BouncingButtons.ButtonData(tmp));

        // ── BouncingArrowZone registration ────────────────────────────────────
        var baz = bb.BouncingArrowZone;
        if (baz != null)
        {
            var old    = baz.childs;
            int oldLen = old?.Length ?? 0;
            var neo    = new Il2CppReferenceArray<BouncingArrowZone.SelectableChild>(oldLen + 1);
            for (int i = 0; i < oldLen; i++) neo[i] = old[i];
            var sc = new BouncingArrowZone.SelectableChild(bwme.TryCast<Selectable>());
            sc.rectTransform = rt;
            neo[oldLen] = sc;
            baz.childs = neo;
        }
        else MelonLogger.Warning($"[MenuRail] BouncingArrowZone null for entry '{label}' — hover bounce disabled.");

        return new Entry(go, bwme, bb, baz, bg);
    }

    private static Il2CppTMPro.TMP_FontAsset FindFont(string name)
    {
        foreach (var f in Resources.FindObjectsOfTypeAll<Il2CppTMPro.TMP_FontAsset>())
            if (f.name == name) return f;
        return null;
    }

    // Handle returned to callers — dispose to remove the injected entry.
    internal sealed class Entry : IDisposable
    {
        private readonly GameObject           _go;
        private readonly ButtonWithMoreEvents _bwme;
        private readonly BouncingButtons      _bb;
        private readonly BouncingArrowZone    _baz;

        internal Image Background { get; }

        internal Entry(
            GameObject go, ButtonWithMoreEvents bwme,
            BouncingButtons bb, BouncingArrowZone baz, Image bg)
        {
            _go   = go;
            _bwme = bwme;
            _bb   = bb;
            _baz  = baz;
            Background = bg;
        }

        public void Dispose()
        {
            if (_bb != null) _bb.buttonsData?.Remove(_bwme);

            if (_baz != null)
            {
                var childs = _baz.childs;
                if (childs != null && childs.Length > 0)
                {
                    int newLen  = childs.Length - 1;
                    var trimmed = new Il2CppReferenceArray<BouncingArrowZone.SelectableChild>(newLen);
                    for (int i = 0; i < newLen; i++) trimmed[i] = childs[i];
                    _baz.childs = trimmed;
                }
            }

            if (_go != null) GameObject.Destroy(_go);
        }
    }
}
