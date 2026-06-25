using System.IO;
using UnityEngine;

namespace CairnAccessories;

/// <summary>One accessory file and, when enabled, its live instance bound to a bone.</summary>
public sealed class Accessory
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    // Use Unity's null-overload: a destroyed GameObject is not C#-null but compares ==null,
    // and touching .transform on it throws. Enabled must reflect a LIVE object, so every
    // `if (!Enabled)` guard downstream is safe.
    public bool Enabled => _loaded != null && _loaded.Go != null;
    public Placement Placement { get; private set; }

    private Glb.Loaded _loaded;
    private Transform _bone;
    private ulong _hash;          // content fingerprint of the .glb the loaded mesh came from
    private bool _hashed;         // whether _hash has been stamped (only after a load/save)

    public Accessory(string path) => Path = path;

    public GameObject Instance => Enabled ? _loaded.Go : null;

    public void Enable()
    {
        if (Enabled) return;
        _loaded = Glb.Load(Path);
        Placement = _loaded.Placement;
        StampHash();
        Rebind();
    }

    /// <summary>True if the .glb on disk differs from what we last loaded (an external edit).
    /// Only meaningful once we've loaded it at least once.</summary>
    public bool HasChangedOnDisk() => _hashed && Glb.HashFile(Path) != _hash;

    /// <summary>Rebuild from the (changed) .glb on disk, preserving worn + bone state. Placement
    /// comes from the file's own extras, so an external placement edit is picked up too.</summary>
    public void Reload()
    {
        if (!Enabled) { StampHash(); return; }   // not worn: nothing live to rebuild, just re-stamp
        Object.Destroy(_loaded.Go);
        _loaded = Glb.Load(Path);
        Placement = _loaded.Placement;
        StampHash();
        _bone = null;                            // force Rebind to reparent the new GameObject
        Rebind();
    }

    private void StampHash() { _hash = Glb.HashFile(Path); _hashed = true; }

    public void Disable()
    {
        if (!Enabled) return;
        Object.Destroy(_loaded.Go);
        _loaded = null;
        _bone = null;
    }

    /// <summary>Keep the accessory pinned to its bone. Called every frame, so it must stay cheap:
    /// once the bone is resolved and the accessory is parented under it, this is a couple of null
    /// checks. We only walk the rig (Rig.Bone -> full hierarchy scan) when we have NO live bone —
    /// at enable, after a SetBone, or if the player respawned and our cached Transform died.
    /// Walking it every frame was the perf cliff that tanked framerate when an accessory was on.</summary>
    public void Rebind()
    {
        if (!Enabled) return;

        // Fast path: bone still alive and we're still parented under it. Nothing to do.
        if (_bone != null && _loaded.Go.transform.parent == _bone) return;

        // Slow path (rare): (re)resolve the bone by name and (re)apply the placement.
        var bone = Rig.Bone(Placement.Bone);
        if (bone == null) return;
        _bone = bone;
        Placement.ApplyTo(_loaded.Go.transform, bone);
    }

    /// <summary>Capture the current transform (after gizmo edits) and persist it into the .glb.</summary>
    public void Save()
    {
        if (!Enabled) return;
        Placement.CaptureFrom(_loaded.Go.transform, Placement.Bone);
        Glb.WritePlacement(Path, Placement);
        StampHash();   // our own write changed the bytes — don't flag it as an external edit
    }

    /// <summary>Move this accessory onto a different bone, keeping its current local offset.</summary>
    public void SetBone(string boneName)
    {
        Placement.Bone = boneName;
        _bone = null; // force Rebind to reparent
        Rebind();
    }
}
