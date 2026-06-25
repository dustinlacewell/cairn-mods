using System.Collections.Generic;
using UnityEngine;
using Il2Cpp;

namespace CairnAccessories;

/// <summary>
/// Locates the player's REAL skeleton bones. Aava's GameObject (PawnManager.MCGameObject)
/// contains several skeletons — the rendered rig plus ragdoll/IK/facial/gear dummies, each
/// with its own bn_Head etc. Attaching to a dummy either doesn't show or double-binds, so
/// we keep only bones whose ancestry is the clean rig (no dummy marker in the path).
/// </summary>
public static class Rig
{
    private static readonly string[] DummyMarkers =
        { "Ragdoll", "Dummy", "IKDummy", "Facial", "Gear", "Charm" };

    public static GameObject Player()
    {
        var pm = PawnManager.Instance;
        return pm != null ? pm.MCGameObject : null;
    }

    /// <summary>All real bones (name -> transform), dummy skeletons excluded. Empty if no player.</summary>
    public static Dictionary<string, Transform> Bones()
    {
        var result = new Dictionary<string, Transform>();
        var player = Player();
        if (player == null) return result;

        foreach (var t in player.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("bn_")) continue;
            if (IsUnderDummy(t, player.transform)) continue;
            // first real bone of a given name wins (rendered rig is enumerated first)
            if (!result.ContainsKey(t.name)) result[t.name] = t;
        }
        return result;
    }

    public static Transform Bone(string name)
    {
        var bones = Bones();
        return bones.TryGetValue(name, out var t) ? t : null;
    }

    private static bool IsUnderDummy(Transform t, Transform root)
    {
        for (var p = t.parent; p != null && p != root.parent; p = p.parent)
            foreach (var m in DummyMarkers)
                if (p.name.Contains(m)) return true;
        return false;
    }
}
