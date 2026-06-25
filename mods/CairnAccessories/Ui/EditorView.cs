using UnityEngine;

namespace CairnAccessories;

/// <summary>
/// The Editor window: a body-diagram of bone regions over a scrolling list of the chosen region's
/// bones (hover a bone to preview its skeleton in-world via <see cref="BoneViz"/>; click to attach
/// the accessory there). Shown only while a worn accessory is selected for editing.
/// </summary>
public sealed class EditorView
{
    private const float DiagramTopY = 46f;   // window-space Y where the body grid starts (below 1 header row)
    private const float CellH = 22f;
    private const float CellGap = 3f;
    private const float RowH = 19f;

    private const string Key = "editor";

    private readonly AccessoryManager _mgr;
    private readonly BoneCatalog _bones;
    private readonly ModConfig _config;
    public BoneViz Viz { get; } = new();

    private Rect _rect;
    private Vector2 _boneScroll;
    private Cat _region = Cat.Head;
    private Accessory _target;   // the accessory drawn this frame (set in Draw, read in Window)

    public EditorView(AccessoryManager mgr, BoneCatalog bones, ModConfig config)
    {
        _mgr = mgr; _bones = bones; _config = config;
        _rect = config.Window(Key, new Rect(332, 20, 380, 600));
    }

    public void Draw(Accessory s)
    {
        _target = s;
        _rect = GUI.Window(0xACCF, _rect, (GUI.WindowFunction)Window, "Editor — " + s.Name);
        _config.SetWindow(Key, _rect);

        // Mark the selected accessory's origin in-world (a yellow star), so you can see where the
        // thing you're editing is anchored.
        if (Event.current.type == EventType.Repaint)
            Viz.Selected = s.Instance != null ? s.Instance.transform : null;
    }

    /// <summary>Editor not shown this frame → clear any lingering hover/selection markers.</summary>
    public void Hide()
    {
        if (Event.current.type != EventType.Repaint) return;
        Viz.Hovered = null;
        Viz.Selected = null;
    }

    private void Window(int id)
    {
        var s = _target;
        Gui.Row(s.Placement.Bone, () =>
        {
            if (GUILayout.Button("save", GUILayout.Width(48))) s.Save();
            if (GUILayout.Button("↻", GUILayout.Width(28))) _bones.Refresh();
        });

        float listTopY = DrawDiagram();
        DrawBoneList(s, listTopY);

        Gui.ResizeGrip(id, ref _rect);
        Gui.TitleDrag(_rect.width);
    }

    /// <summary>Body-shaped grid of region buttons. Returns the Y just below it.</summary>
    private float DrawDiagram()
    {
        int rows = 0;
        foreach (var c in BoneRegions.Layout) if (c.Row + 1 > rows) rows = c.Row + 1;
        var grid = new Gui.Grid(8f, _rect.width - 16f, DiagramTopY, cols: 3, rows: rows, CellH, CellGap);

        foreach (var cell in BoneRegions.Layout)
        {
            int col = cell.Side == Side.Left ? 0 : cell.Side == Side.Right ? 2 : 1;
            int count = _bones.CountIn(cell.Cat);
            string label = count > 0 ? $"{cell.Label} ({count})" : cell.Label;
            if (grid.Cell(col, cell.Row, _region == cell.Cat, label))
            {
                _region = cell.Cat;
                _boneScroll = Vector2.zero;
            }
        }
        return grid.BottomY;
    }

    private void DrawBoneList(Accessory s, float topY)
    {
        var view = new Rect(8f, topY, _rect.width - 16f, _rect.height - topY - 8f);
        var hovered = Gui.ScrollList(
            view, ref _boneScroll, _bones.InRegion(_region), RowH,
            label: b => b.Name,
            onClick: b => s.SetBone(b.Name));

        if (Event.current.type == EventType.Repaint)
            Viz.Hovered = hovered.Transform;
    }
}
