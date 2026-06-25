using System.Collections.Generic;

namespace CairnAccessories;

/// <summary>
/// The player's bones, snapshotted from the live rig and bucketed by anatomical region — the
/// ontology the editor's body-diagram picker reads.
///
/// Building it walks Aava's whole hierarchy across the Il2Cpp boundary (hundreds of transforms),
/// so it's cached: built lazily on first access and only rebuilt on <see cref="Refresh"/> (the
/// rig doesn't change while editing, and OnGUI fires per input event — rebuilding per mouse-move
/// was a real perf cliff). Lives here, not in the UI, because "the categorized rig" is a domain
/// concept, not a view concern.
/// </summary>
public sealed class BoneCatalog
{
    private Dictionary<Cat, List<Bone>> _byRegion;

    /// <summary>Bones in the given region (empty if none). Builds + caches the snapshot on demand.</summary>
    public IReadOnlyList<Bone> InRegion(Cat region) =>
        Snapshot().TryGetValue(region, out var list) ? list : System.Array.Empty<Bone>();

    /// <summary>How many bones a region holds — for the diagram's per-button counts.</summary>
    public int CountIn(Cat region) => InRegion(region).Count;

    /// <summary>Drop the cached snapshot; next access re-walks the rig (player respawned, etc.).</summary>
    public void Refresh() => _byRegion = null;

    private Dictionary<Cat, List<Bone>> Snapshot() => _byRegion ??= Build();

    private static Dictionary<Cat, List<Bone>> Build()
    {
        var map = new Dictionary<Cat, List<Bone>>();
        foreach (var kv in Rig.Bones())
        {
            var region = BoneRegions.Classify(kv.Key);
            if (!map.TryGetValue(region, out var list)) map[region] = list = new();
            list.Add(new Bone(kv.Key, kv.Value));
        }
        return map;
    }
}
