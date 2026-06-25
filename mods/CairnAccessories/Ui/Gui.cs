using System;
using System.Collections.Generic;
using UnityEngine;

namespace CairnAccessories;

/// <summary>
/// The design-system layer: the handful of IMGUI idioms the accessory views compose from, so no
/// view touches raw GUI.Button(Rect) / scroll-space math itself. Two families:
///   • GUILayout sugar — Row/Right/ActiveButton — for flow-laid windows (wardrobe, transform).
///   • Absolute helpers — Grid, ScrollList — for the editor's hand-placed body diagram and bone
///     list (GUILayout.GetLastRect is stripped in this Il2Cpp build, so those must place rects
///     themselves; this is where that machinery lives, once).
/// </summary>
public static class Gui
{
    // ---- GUILayout sugar ----------------------------------------------------------------------

    /// <summary>A horizontal strip; <paramref name="body"/> draws its contents.</summary>
    public static void Row(Action body)
    {
        GUILayout.BeginHorizontal();
        body();
        GUILayout.EndHorizontal();
    }

    /// <summary>A label on the left, right-aligned <paramref name="trailing"/> after a spring.</summary>
    public static void Row(string left, Action trailing)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(left);
        GUILayout.FlexibleSpace();
        trailing();
        GUILayout.EndHorizontal();
    }

    /// <summary>A button that highlights when <paramref name="active"/>. Returns true on click.</summary>
    public static bool ActiveButton(bool active, string label, params GUILayoutOption[] opts)
    {
        var prev = GUI.color;
        if (active) GUI.color = Color.cyan;
        bool clicked = GUILayout.Button(label, opts);
        GUI.color = prev;
        return clicked;
    }

    /// <summary>A press-and-hold button at an absolute rect: fires once on press, then repeats at a
    /// steady rate (<see cref="RepeatInterval"/>) after a short delay, like a key auto-repeat, for as
    /// long as it's held. <paramref name="id"/> keys the per-button timing state, so each repeat
    /// button needs a unique id. Returns true on the frames it should fire.</summary>
    public static bool RepeatButtonAt(Rect r, string id, string label)
    {
        const float delay = 0.35f;
        bool held = GUI.RepeatButton(r, label);
        float now = Time.unscaledTime;

        if (!held) { _repeatNext.Remove(id); return false; }
        if (!_repeatNext.TryGetValue(id, out float next)) { _repeatNext[id] = now + delay; return true; }
        if (now >= next) { _repeatNext[id] = now + RepeatInterval; return true; }
        return false;
    }

    /// <summary>Seconds between repeats while a RepeatButton is held — user-tunable (the repeat-speed
    /// slider writes this). Smaller = faster.</summary>
    public static float RepeatInterval = 0.12f;

    /// <summary>If the mouse wheel scrolled while over <paramref name="rect"/> (window-local), return
    /// its sign (+1 wheel up, −1 wheel down) and consume the event; otherwise 0. Lets a row react to
    /// hover-scroll without GUILayoutUtility (which is stripped here).</summary>
    public static int ScrollOver(Rect rect)
    {
        var e = Event.current;
        if (e.type != EventType.ScrollWheel || !rect.Contains(e.mousePosition)) return 0;
        e.Use();
        return e.delta.y > 0 ? -1 : 1;   // wheel-up gives negative delta.y in IMGUI
    }

    private static readonly Dictionary<string, float> _repeatNext = new();

    // ---- absolute helpers ---------------------------------------------------------------------

    /// <summary>A fixed-cell grid placed by (col, row) within a window. Used for the body diagram;
    /// columns are equal-width across <paramref name="width"/>, rows are <paramref name="cellH"/>
    /// tall. Build one, Cell(...) the buttons, then read <see cref="BottomY"/>.</summary>
    public readonly struct Grid
    {
        private readonly float _topY, _cellH, _gap;
        private readonly float[] _colX;
        private readonly float _colW;
        private readonly int _rows;

        public Grid(float x, float width, float topY, int cols, int rows, float cellH, float gap)
        {
            _topY = topY; _cellH = cellH; _gap = gap; _rows = rows;
            _colW = (width - (cols - 1) * gap) / cols;
            _colX = new float[cols];
            for (int c = 0; c < cols; c++) _colX[c] = x + c * (_colW + gap);
        }

        public bool Cell(int col, int row, bool active, string label)
        {
            float y = _topY + row * (_cellH + _gap);
            return Gui.AbsActiveButton(new Rect(_colX[col], y, _colW, _cellH), active, label);
        }

        /// <summary>Y just below the grid — where following content (the bone list) starts.</summary>
        public float BottomY => _topY + _rows * (_cellH + _gap) + 6f;
    }

    /// <summary>A vertically-scrolling list of fixed-height rows inside <paramref name="view"/>.
    /// Calls <paramref name="onClick"/> when a row is clicked and returns the hovered item (or
    /// default), encapsulating the scroll-space hover transform. <paramref name="scroll"/> is
    /// threaded through by ref so the caller keeps the scroll position.</summary>
    public static T ScrollList<T>(
        Rect view, ref Vector2 scroll, IReadOnlyList<T> items, float rowH,
        Func<T, string> label, Action<T> onClick)
    {
        var mouse = Event.current.mousePosition;
        int n = items?.Count ?? 0;
        var content = new Rect(0, 0, view.width - 18f, n * rowH);

        T hovered = default;
        scroll = GUI.BeginScrollView(view, scroll, content);
        for (int i = 0; i < n; i++)
        {
            var row = new Rect(0, i * rowH, content.width, rowH - 1f);
            if (GUI.Button(row, label(items[i]))) onClick(items[i]);
            // content-space cursor = (screen mouse - view origin) + scroll; only when over the viewport
            if (view.Contains(mouse) && row.Contains(scroll + (mouse - view.position)))
                hovered = items[i];
        }
        GUI.EndScrollView();
        return hovered;
    }

    private static bool AbsActiveButton(Rect r, bool active, string label)
    {
        var prev = GUI.color;
        if (active) GUI.color = Color.cyan;
        bool clicked = GUI.Button(r, label);
        GUI.color = prev;
        return clicked;
    }

    /// <summary>Drag handle over a window's title strip (so it doesn't eat clicks on content).</summary>
    public static void TitleDrag(float width) => GUI.DragWindow(new Rect(0, 0, width, 18f));

    /// <summary>A bottom-right resize grip. Call inside a window's body (so coords are window-local)
    /// with the window's id and current rect; it draws the grip and, while dragged, grows/shrinks
    /// <paramref name="rect"/> by the mouse delta (clamped to a minimum). Resize by delta — not
    /// absolute mouse — because the window moves under the cursor mid-resize. Grabs hotControl on
    /// press so drag events keep coming even when the cursor leaves the window bounds (otherwise the
    /// resize stalls the moment you drag past the edge). GUILayoutUtility is stripped here, so we
    /// hand-track.</summary>
    public static void ResizeGrip(int id, ref Rect rect, float minW = 200f, float minH = 140f)
    {
        const float grip = 18f;
        var local = new Rect(rect.width - grip, rect.height - grip, grip, grip);
        GUI.Box(local, "◢");

        // a stable control id for this grip, so hotControl routes all drag events here until release
        int ctrl = GUIUtility.GetControlID(0x5215_0000 ^ id, FocusType.Passive);
        var e = Event.current;
        switch (e.type)
        {
            case EventType.MouseDown when local.Contains(e.mousePosition):
                GUIUtility.hotControl = ctrl; e.Use(); break;
            case EventType.MouseDrag when GUIUtility.hotControl == ctrl:
                rect.width = Mathf.Max(minW, rect.width + e.delta.x);
                rect.height = Mathf.Max(minH, rect.height + e.delta.y);
                e.Use(); break;
            case EventType.MouseUp when GUIUtility.hotControl == ctrl:
                GUIUtility.hotControl = 0; e.Use(); break;
        }
    }
}
