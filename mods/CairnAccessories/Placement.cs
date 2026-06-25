using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CairnAccessories;

/// <summary>
/// How an accessory sits on the rig: which bone it rides, and its LOCAL transform
/// relative to that bone. Stored in the .glb's asset.extras so a shared file carries
/// its own placement. Local TRS (not world) is what makes it track the bone for free —
/// parent to the bone, set localPosition/Rotation/Scale, and it follows every pose.
/// </summary>
public sealed class Placement
{
    public string Bone = "bn_Head";
    public Vector3 LocalPosition = Vector3.zero;
    public Quaternion LocalRotation = Quaternion.identity;
    public Vector3 LocalScale = Vector3.one * 0.0145f;

    public static Placement Default() => new();

    public void ApplyTo(Transform t, Transform bone)
    {
        t.SetParent(bone, false);
        t.localPosition = LocalPosition;
        t.localRotation = LocalRotation;
        t.localScale = LocalScale;
    }

    /// <summary>Capture a transform's current local TRS back into this placement (after gizmo edits).</summary>
    public void CaptureFrom(Transform t, string bone)
    {
        Bone = bone;
        LocalPosition = t.localPosition;
        LocalRotation = t.localRotation;
        LocalScale = t.localScale;
    }

    public JObject ToJson() => new()
    {
        ["bone"] = Bone,
        ["pos"] = new JArray(LocalPosition.x, LocalPosition.y, LocalPosition.z),
        ["rot"] = new JArray(LocalRotation.x, LocalRotation.y, LocalRotation.z, LocalRotation.w),
        ["scale"] = new JArray(LocalScale.x, LocalScale.y, LocalScale.z),
    };

    public static Placement FromJson(JObject o)
    {
        var p = new Placement();
        if (o["bone"] != null) p.Bone = (string)o["bone"];
        if (o["pos"] is JArray a && a.Count == 3)
            p.LocalPosition = new Vector3((float)a[0], (float)a[1], (float)a[2]);
        if (o["rot"] is JArray r && r.Count == 4)
            p.LocalRotation = new Quaternion((float)r[0], (float)r[1], (float)r[2], (float)r[3]);
        if (o["scale"] is JArray s && s.Count == 3)
            p.LocalScale = new Vector3((float)s[0], (float)s[1], (float)s[2]);
        return p;
    }
}
