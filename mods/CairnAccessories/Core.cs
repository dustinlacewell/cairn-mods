using UnityEngine;
using UnityEngine.InputSystem;
using MelonLoader;

[assembly: MelonInfo(typeof(CairnAccessories.Core), "CairnAccessories", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnAccessories;

/// <summary>
/// Drop a .glb into &lt;game&gt;/Accessories, open the game's PHOTO MODE, and the accessory editor
/// appears: enable a glb, pick a bone, and edit its placement with the keyboard gizmo. Placement
/// is written back into the .glb's asset.extras, so the file carries its own positioning — share
/// it and it loads in place. Photo mode already frees the cursor and gates camera look behind
/// right-mouse, so the editor needs no input handling of its own.
///
/// Composition root: builds the config (persisted worn set + window rects), the manager (folder
/// scan + per-frame re-pin), the gizmo (keyboard transform editor), the camera gate (observes
/// photo-mode open/close), and the panel (asset browser), routing input/render to them. The worn
/// set and window positions persist across sessions in &lt;Accessories&gt;/config.json.
/// </summary>
public sealed class Core : MelonMod
{
    private ModConfig _config;
    private AccessoryManager _mgr;
    private Gizmo _gizmo;
    private BoneCatalog _bones;
    private Panel _panel;
    private CameraGate _gate;

    public override void OnInitializeMelon()
    {
        var folder = AccessoryManager.ResolveFolder();
        _config = new ModConfig(folder);
        _config.Load();

        _mgr = new AccessoryManager(folder, _config);  // re-wears the saved set once the rig loads

        _gizmo = new Gizmo();
        _bones = new BoneCatalog();
        _panel = new Panel(_mgr, _gizmo, _bones, _config);
        _gate = new CameraGate();
        LoggerInstance.Msg($"CairnAccessories loaded. Drop .glb into {folder}, open photo mode.");
    }

    public override void OnUpdate()
    {
        _gate.Subscribe();                            // idempotent; PhotoModeManager events are static
        _panel.Open = _gate.Active;                   // editor is shown exactly while photo mode is open

        if (Inputs.KeyHeld(Key.LeftCtrl) && Inputs.KeyDown(Key.S))
            _mgr.Selected?.Save();

        _mgr.Tick();                                  // keep accessories pinned to their bone
        if (_panel.Open) _gizmo.Tick(_mgr.Selected);  // keyboard edit while the editor is open
        _config.Tick();                               // debounced save of worn set + window rects
    }

    public override void OnApplicationQuit() => _config.Flush();

    public override void OnGUI()
    {
        // GL world-space lines must be issued during Repaint; draw the bone overlay first
        // (under the camera matrices) then the IMGUI window on top.
        if (UnityEngine.Event.current.type == UnityEngine.EventType.Repaint)
            _panel.Viz.Render();
        _panel.Draw();
    }
}
