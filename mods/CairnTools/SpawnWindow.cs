using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Il2Cpp;
using CairnAPI;

namespace CairnTools;

// The item browse-and-spawn view: a draggable Canvas window with a search field over the full
// item catalog (CairnAPI.Items) and per-row spawn buttons (CairnAPI.Inventory.Add). Rebuilds the
// row list as the search filter changes. All game access goes through CairnAPI; this file only
// composes Ui primitives and renders state.
public class SpawnWindow
{
    private enum Sort { NameAsc, NameDesc, WeightAsc, WeightDesc }

    private GameObject _root;
    private RectTransform _window;
    private RectTransform _header;
    private RectTransform _listContent;
    private Text _status;
    private Text _nameColLabel;
    private Text _weightColLabel;
    private string _filter = "";
    private Sort _sort = Sort.NameAsc;
    private readonly List<GameObject> _rows = new();

    // drag state (driven from Core.OnUpdate via InputSystem mouse)
    private bool _dragging;
    private Vector2 _dragOffset;

    public bool Visible => _root != null && _root.activeSelf;

    public void Toggle()
    {
        if (_root == null) Build();
        _root.SetActive(!_root.activeSelf);
        if (_root.activeSelf) Refresh();
    }

    public void Hide()
    {
        if (_root != null) _root.SetActive(false);
    }

    private void Build()
    {
        Ui.Init();

        var canvasGo = new GameObject("CairnTools_Canvas");
        UnityEngine.Object.DontDestroyOnLoad(canvasGo);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000;             // above the game HUD
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();
        _root = canvasGo;

        // window
        var win = Ui.Node("Window", canvasGo.transform);
        win.sizeDelta = new Vector2(460, 560);
        win.anchorMin = win.anchorMax = new Vector2(0.5f, 0.5f);
        win.anchoredPosition = new Vector2(-560, 0);   // left-of-centre by default
        Ui.Panel(win, Ui.Bg);
        Ui.VStack(win, 6f, new RectOffset(8, 8, 8, 8));
        _window = win;

        // header (drag handle + title + close) — drag handled in Core.OnUpdate
        var header = Ui.Node("Header", win);
        Ui.Panel(header, Ui.Header);
        Ui.Sized(header, h: 22f, flexW: true);
        Ui.HStack(header, 6f, new RectOffset(8, 4, 0, 0));
        var titleRt = Ui.Label(header, "Cairn Tools — Items", 13, Ui.Text).rectTransform;
        Ui.Sized(titleRt, flexW: true);
        Ui.Button(header, "X", Ui.AccentDim, Hide, width: 22f, fontSize: 12);
        _header = header;

        // search row
        var searchRow = Ui.Node("SearchRow", win);
        Ui.Sized(searchRow, h: 22f, flexW: true);
        Ui.HStack(searchRow, 6f);
        Ui.Input(searchRow, "search items…", s => { _filter = s ?? ""; Refresh(); });

        // column-header row — click NAME / WT to sort (toggles asc/desc)
        var cols = Ui.Node("Columns", win);
        Ui.Panel(cols, Ui.Header);
        Ui.Sized(cols, h: 20f, flexW: true);
        Ui.HStack(cols, 6f, new RectOffset(8, 6, 0, 0));
        _nameColLabel = ColumnButton(cols, "NAME", flexW: true,
            onClick: () => ToggleSort(Sort.NameAsc, Sort.NameDesc));
        _weightColLabel = ColumnButton(cols, "WT", width: 40f, align: TextAnchor.MiddleRight,
            onClick: () => ToggleSort(Sort.WeightAsc, Sort.WeightDesc));
        // spacer matching the two +N buttons so the WT header lines up over the weight column
        Ui.Sized(Ui.Node("Spacer", cols), w: 74f);

        // count / status line
        _status = Ui.Label(win, "", 12, Ui.TextDim);
        Ui.Sized(_status.rectTransform, h: 16f, flexW: true);

        // scrollable item list
        _listContent = Ui.ScrollList(win, out _);
    }

    // A clickable column header; returns its Text so Refresh can repaint the sort arrow.
    private Text ColumnButton(RectTransform parent, string label, Action onClick,
        bool flexW = false, float? width = null, TextAnchor align = TextAnchor.MiddleLeft)
    {
        var cell = Ui.Node("Col", parent);
        var img = cell.gameObject.AddComponent<Image>();
        img.color = new Color(0, 0, 0, 0);          // invisible but raycastable
        var btn = cell.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
        if (flexW) Ui.Sized(cell, flexW: true); else if (width.HasValue) Ui.Sized(cell, w: width.Value);
        var t = Ui.Label(cell, label, 11, Ui.TextDim, align);
        Ui.Fill((RectTransform)t.transform);
        return t;
    }

    private void ToggleSort(Sort asc, Sort desc)
    {
        _sort = _sort == asc ? desc : asc;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var go in _rows) UnityEngine.Object.Destroy(go);
        _rows.Clear();

        if (!Items.IsReady)
        {
            _status.text = "item library not loaded (enter a session)";
            return;
        }

        // filter
        var view = new List<ItemInfo>();
        var all = Items.All;
        for (int i = 0; i < all.Count; i++)
        {
            var info = all[i];
            if (_filter.Length == 0 || info.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
                view.Add(info);
        }

        // sort (NaN weights — garbage/none — sink to the bottom either direction)
        view.Sort((a, b) =>
        {
            switch (_sort)
            {
                case Sort.NameDesc: return string.Compare(b.Name, a.Name, StringComparison.Ordinal);
                case Sort.WeightAsc: return CompareWeight(a, b, ascending: true);
                case Sort.WeightDesc: return CompareWeight(a, b, ascending: false);
                default: return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            }
        });

        for (int i = 0; i < view.Count; i++)
            BuildRow(view[i], i);

        // column-header arrows
        _nameColLabel.text = "NAME" + (_sort == Sort.NameAsc ? " ▲" : _sort == Sort.NameDesc ? " ▼" : "");
        _weightColLabel.text = "WT" + (_sort == Sort.WeightAsc ? " ▲" : _sort == Sort.WeightDesc ? " ▼" : "");

        float cap = Inventory.MaxBagWeight();
        _status.text = $"{view.Count} items" + (_filter.Length > 0 ? $" matching \"{_filter}\"" : "")
                     + (cap > 0 ? $"  ·  bag cap {cap:0.#}" : "");
    }

    private static int CompareWeight(ItemInfo a, ItemInfo b, bool ascending)
    {
        float wa = a.UnitWeight, wb = b.UnitWeight;
        bool na = float.IsNaN(wa), nb = float.IsNaN(wb);
        if (na && nb) return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        if (na) return 1;          // NaN sinks
        if (nb) return -1;
        int c = ascending ? wa.CompareTo(wb) : wb.CompareTo(wa);
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
    }

    private void BuildRow(ItemInfo info, int index)
    {
        var row = Ui.Node("Row", _listContent);
        Ui.Panel(row, (index & 1) == 0 ? Ui.RowBg : Ui.RowAlt);
        Ui.Sized(row, h: 28f, flexW: true);
        Ui.HStack(row, 6f, new RectOffset(8, 6, 0, 0));

        var name = Ui.Label(row, info.Name, 13, Ui.Text);
        Ui.Sized(name.rectTransform, flexW: true);

        float w = info.UnitWeight;
        var wt = Ui.Label(row, float.IsNaN(w) ? "—" : w.ToString("0.##"), 12, Ui.TextDim,
            TextAnchor.MiddleRight);
        Ui.Sized(wt.rectTransform, w: 40f);

        Ui.Button(row, "+1", Ui.Accent, () => Spawn(info, 1), width: 34f, fontSize: 12);
        Ui.Button(row, "+5", Ui.AccentDim, () => Spawn(info, 5), width: 34f, fontSize: 12);

        _rows.Add(row.gameObject);
    }

    private void Spawn(ItemInfo info, int count)
    {
        var r = Inventory.Add(info.Id, count);
        if (r.Ok)
            _status.text = $"+{r.Added} {info.Name}  (now {Inventory.Count(info.Id)})";
        else
            _status.text = $"can't add {info.Name}: {r.Error}";
    }

    // Drag the window by its header, driven from Core.OnUpdate via InputSystem mouse. Uses
    // RectTransformUtility for the hit-test + screen->local conversion so canvas scaling
    // (CanvasScaler ScaleWithScreenSize) is handled correctly. On press inside the header we
    // record the offset between the window's anchored position and the pointer (both in the
    // window-parent's local space); each frame we move the window to keep that offset.
    public void UpdateDrag()
    {
        if (!Visible || _window == null || _header == null) return;
        var parent = _window.parent as RectTransform;
        if (parent == null) return;
        Vector2 mouse = Inputs.MousePos;

        if (Inputs.MouseDown
            && RectTransformUtility.RectangleContainsScreenPoint(_header, mouse, null))
        {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, mouse, null, out var lp))
            {
                _dragging = true;
                _dragOffset = _window.anchoredPosition - lp;
            }
        }
        if (!Inputs.MouseHeld) _dragging = false;
        if (_dragging
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, mouse, null, out var cur))
        {
            _window.anchoredPosition = cur + _dragOffset;
        }
    }

}
