using UnityEngine;

namespace CairnAccessories;

/// <summary>A rig bone the player can attach to: its name and live transform. Replaces the
/// naked KeyValuePair&lt;string, Transform&gt; that the picker used to pass around.</summary>
public readonly struct Bone
{
    public readonly string Name;
    public readonly Transform Transform;
    public Bone(string name, Transform transform) { Name = name; Transform = transform; }
}
