namespace CairnAccessories;

/// <summary>
/// Visibility orchestrator for the accessory UI: the Wardrobe is always shown while the panel is
/// open (photo mode); the Editor + Transform windows appear only while a worn accessory is
/// selected for editing. Owns no layout itself — each window is its own view.
/// </summary>
public sealed class Panel
{
    public bool Open { get; set; }
    public BoneViz Viz => _editor.Viz;   // Core renders the in-world bone overlay from here

    private readonly AccessoryManager _mgr;
    private readonly WardrobeView _wardrobe;
    private readonly EditorView _editor;
    private readonly TransformView _transform;

    public Panel(AccessoryManager mgr, Gizmo gizmo, BoneCatalog bones, ModConfig config)
    {
        _mgr = mgr;
        _wardrobe = new WardrobeView(mgr, config);
        _editor = new EditorView(mgr, bones, config);
        _transform = new TransformView(mgr, gizmo, config);
    }

    public void Draw()
    {
        if (!Open) { _editor.Hide(); return; }

        _wardrobe.Draw();

        var s = _mgr.Selected;
        if (s != null && s.Enabled)
        {
            _editor.Draw(s);
            _transform.Draw();
        }
        else
        {
            _editor.Hide();
        }
    }
}
