using System.Collections.Generic;
using UnityEngine;

namespace CairnAccessories;

/// <summary>
/// Debug overlay drawn with GL immediate-mode lines under the active camera's matrices (so world
/// coords map correctly). Two things, either/both per frame:
///   • Hovered — a hovered bone's skeleton: a line to each child, recursed depth-first, with a
///     yellow cross at its origin — so you can see where a bone is before picking it.
///   • Selected — a yellow star of lines at the currently-selected accessory's origin, so you can
///     see exactly where the thing you're editing is anchored.
/// The panel sets these each frame; Render() is called from OnGUI's repaint.
/// </summary>
public sealed class BoneViz
{
    public Transform Hovered { get; set; }
    public Transform Selected { get; set; }

    private Material _mat;

    public void Render()
    {
        if (Hovered == null && Selected == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        EnsureMat();

        GL.PushMatrix();
        GL.LoadProjectionMatrix(cam.projectionMatrix);
        GL.modelview = cam.worldToCameraMatrix;
        _mat.SetPass(0);

        GL.Begin(GL.LINES);
        if (Hovered != null)
        {
            DrawMarker(Hovered.position, 0.04f, Color.yellow);   // cross at hovered bone
            DrawBoneTree(Hovered, Color.cyan);                   // its skeleton, depth-first
        }
        if (Selected != null)
            DrawMarker(Selected.position, 0.08f, Color.yellow);  // star at selected accessory origin
        GL.End();

        GL.PopMatrix();
    }

    private static void DrawBoneTree(Transform bone, Color c)
    {
        for (int i = 0; i < bone.childCount; i++)
        {
            var child = bone.GetChild(i);
            GL.Color(c);
            GL.Vertex(bone.position);
            GL.Vertex(child.position);
            DrawBoneTree(child, c);
        }
    }

    private static void DrawMarker(Vector3 p, float s, Color c)
    {
        GL.Color(c);
        GL.Vertex(p - Vector3.right * s);   GL.Vertex(p + Vector3.right * s);
        GL.Vertex(p - Vector3.up * s);      GL.Vertex(p + Vector3.up * s);
        GL.Vertex(p - Vector3.forward * s); GL.Vertex(p + Vector3.forward * s);
    }

    private void EnsureMat()
    {
        if (_mat != null) return;
        // unlit, vertex-colored, draws on top (no ZTest) so bones show through the body
        var sh = Shader.Find("Hidden/Internal-Colored");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        _mat.SetInt("_ZWrite", 0);
        _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
    }
}
