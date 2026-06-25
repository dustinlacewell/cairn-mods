using UnityEngine;

namespace CairnAccessories;

/// <summary>
/// The Wardrobe window: the LIBRARY (every .glb in the drop folder, wear/unwear) above the WORN
/// list (attached accessories; pick one to edit, save, or unwear). Owns only its own window
/// geometry + scroll state; all accessory state lives in the manager.
/// </summary>
public sealed class WardrobeView
{
    private const string Key = "wardrobe";

    private readonly AccessoryManager _mgr;
    private readonly ModConfig _config;
    private Rect _rect;
    private Vector2 _libScroll;
    private Vector2 _wornScroll;

    public WardrobeView(AccessoryManager mgr, ModConfig config)
    {
        _mgr = mgr;
        _config = config;
        _rect = config.Window(Key, new Rect(20, 20, 300, 420));
    }

    public void Draw()
    {
        _rect = GUI.Window(0xACCE, _rect, (GUI.WindowFunction)Window, "Wardrobe");
        _config.SetWindow(Key, _rect);
    }

    private void Window(int id)
    {
        Gui.Row(() => { if (GUILayout.Button("Rescan folder")) _mgr.Rescan(); });
        GUILayout.Label(_mgr.Folder);
        GUILayout.Space(4);

        DrawLibrary();
        GUILayout.Space(6);
        DrawWorn();

        Gui.ResizeGrip(id, ref _rect);
        Gui.TitleDrag(_rect.width);
    }

    private void DrawLibrary()
    {
        GUILayout.Label($"LIBRARY  ({_mgr.Items.Count})");
        _libScroll = GUILayout.BeginScrollView(_libScroll, GUILayout.Height(150));
        if (_mgr.Items.Count == 0)
            GUILayout.Label("Drop .glb files into the folder above, then Rescan.");
        foreach (var a in _mgr.Items)
            Gui.Row(a.Enabled ? "● " + a.Name : "○ " + a.Name, () =>
            {
                if (GUILayout.Button(a.Enabled ? "unwear" : "wear", GUILayout.Width(70))) _mgr.Toggle(a);
            });
        GUILayout.EndScrollView();
    }

    private void DrawWorn()
    {
        GUILayout.Label("WORN");
        _wornScroll = GUILayout.BeginScrollView(_wornScroll);
        bool any = false;
        foreach (var a in _mgr.Worn)
        {
            any = true;
            bool sel = _mgr.Selected == a;
            Gui.Row((sel ? "▸ " : "  ") + a.Name, () =>
            {
                if (GUILayout.Button(sel ? "editing" : "edit", GUILayout.Width(64))) _mgr.Select(a);
                if (GUILayout.Button("save", GUILayout.Width(48))) a.Save();
                if (GUILayout.Button("x", GUILayout.Width(24))) _mgr.Toggle(a);
            });
        }
        if (!any) GUILayout.Label("Nothing worn. 'wear' an item above.");
        GUILayout.EndScrollView();
    }
}
