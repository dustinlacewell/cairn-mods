using System.Collections.Generic;
using System.IO;
using UnityEngine;
using MelonLoader;

namespace CairnAccessories;

/// <summary>
/// Owns the set of accessories discovered in the drop folder, the currently-selected one
/// (the gizmo's target), and the per-frame re-bind. Folder lives next to the game so users
/// just drop .glb files in and they appear in the picker.
/// </summary>
public sealed class AccessoryManager
{
    public string Folder { get; }
    public IReadOnlyList<Accessory> Items => _items;
    /// <summary>Currently-worn (enabled) accessories — the "Worn" list in the wardrobe.</summary>
    public IEnumerable<Accessory> Worn => System.Linq.Enumerable.Where(_items, a => a.Enabled);
    /// <summary>The worn accessory currently targeted by the editor (bone picker + gizmo), if any.</summary>
    public Accessory Selected { get; private set; }

    private readonly List<Accessory> _items = new();
    private readonly ModConfig _config;

    /// <summary>The drop folder, &lt;game&gt;/Accessories — resolved once by the composition root.</summary>
    public static string ResolveFolder()
    {
        var dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Accessories");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public AccessoryManager(string folder, ModConfig config)
    {
        Folder = folder;
        _config = config;
        Rescan();
    }

    // Worn accessories can only attach once the player's rig exists — which happens when a save
    // loads, long after the mod inits, and again whenever the player is rebuilt (new save, respawn).
    // So restoration is deferred: we re-wear the remembered set the first frame a player is present,
    // and re-arm when the player goes away so the next save restores too. Restoring at init instead
    // created the GameObjects in the menu scene, where they were destroyed before gameplay.
    private GameObject _restoredFor;

    private void RestoreWornIfPlayerReady()
    {
        var player = Rig.Player();
        if (player == null) { _restoredFor = null; return; }   // no rig yet (or player torn down)
        if (player == _restoredFor) return;                    // already restored for this player

        _restoredFor = player;
        foreach (var file in _config.WornFiles)
        {
            var a = System.Linq.Enumerable.FirstOrDefault(_items, i => i.Name == file);
            if (a == null) { MelonLogger.Warning($"worn '{file}' not found, skipping"); continue; }
            if (!a.Enabled) a.Enable();
        }
    }

    /// <summary>Re-read the folder: add new .glb files, drop deleted ones, keep existing Accessory
    /// objects (so worn/selected state survives), and reload any whose file changed on disk since we
    /// last loaded it (external Blender re-export) — picking up new geometry, textures, and the
    /// file's own placement.</summary>
    public void Rescan()
    {
        var existing = new Dictionary<string, Accessory>();
        foreach (var a in _items) existing[a.Path] = a;

        _items.Clear();
        foreach (var path in Directory.GetFiles(Folder, "*.glb"))
        {
            if (existing.TryGetValue(path, out var keep))
            {
                if (keep.HasChangedOnDisk()) keep.Reload();
                _items.Add(keep);
            }
            else _items.Add(new Accessory(path));
        }

        if (Selected != null && !_items.Contains(Selected)) Selected = null;
    }

    public void Toggle(Accessory a)
    {
        if (a.Enabled) { a.Disable(); if (Selected == a) Selected = null; }
        else { a.Enable(); Selected = a; }
        _config.SetWorn(a.Name, a.Enabled);
    }

    public void Select(Accessory a) => Selected = a.Enabled ? a : Selected;

    /// <summary>Called every frame: restore the worn set once the rig is ready, then keep enabled
    /// accessories pinned to their (re-resolved) bone.</summary>
    public void Tick()
    {
        RestoreWornIfPlayerReady();
        foreach (var a in _items)
            if (a.Enabled) a.Rebind();
    }
}
