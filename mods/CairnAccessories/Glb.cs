using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CairnAccessories;

/// <summary>
/// A self-contained runtime glTF-binary (.glb) reader for this game. No external glTF
/// library: the format is a 12-byte header + a JSON chunk + a binary blob, and the mesh
/// is just typed accessors into that blob. We build a Unity Mesh + Texture directly,
/// crossing the Il2Cpp boundary only through Il2CppStructArray and ImageConversion —
/// both proven to work in-process. See Placement for the round-trippable transform data
/// we stash in the glTF asset.extras so a shared .glb carries its own positioning.
/// </summary>
public static class Glb
{
    // glTF componentType enum
    private const int FLOAT = 5126;
    private const int UINT32 = 5125;
    private const int UINT16 = 5123;

    /// <summary>Result of loading: the renderable object plus the parsed placement (may be defaults).</summary>
    public sealed class Loaded
    {
        public GameObject Go;
        public Mesh Mesh;
        public Material Material;
        public Placement Placement;
    }

    /// <summary>Content fingerprint of a .glb on disk — used to detect external edits on rescan.
    /// FNV-1a over the raw bytes: fast, dependency-free, and we only need change-detection, not
    /// cryptographic strength.</summary>
    public static ulong HashFile(string glbPath)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        byte[] data = File.ReadAllBytes(glbPath);
        ulong h = offset;
        for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= prime; }
        return h;
    }

    public static Loaded Load(string glbPath)
    {
        byte[] data = File.ReadAllBytes(glbPath);
        var (json, binStart) = SplitContainer(data);
        var js = JObject.Parse(json);

        var mesh = BuildMesh(js, data, binStart, out int submeshCount);
        var mat = BuildMaterial(js, data, binStart);

        var go = new GameObject("Accessory:" + Path.GetFileNameWithoutExtension(glbPath));
        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>().material = mat;

        return new Loaded
        {
            Go = go,
            Mesh = mesh,
            Material = mat,
            Placement = ReadPlacement(js),
        };
    }

    // --- container ---

    private static (string json, int binStart) SplitContainer(byte[] data)
    {
        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic != 0x46546C67) // "glTF"
            throw new InvalidDataException("not a GLB (bad magic)");
        uint jlen = BitConverter.ToUInt32(data, 12);
        string json = System.Text.Encoding.UTF8.GetString(data, 20, (int)jlen);
        int binChunkOff = 20 + (int)jlen;       // [len][type][bytes]
        int binStart = binChunkOff + 8;
        return (json, binStart);
    }

    // --- geometry ---

    private readonly struct Acc
    {
        public readonly int BaseOffset, Stride, Count, ComponentType;
        public Acc(int b, int s, int c, int ct) { BaseOffset = b; Stride = s; Count = c; ComponentType = ct; }
    }

    private static Acc Accessor(JObject js, int binStart, int accIndex, int packedStride)
    {
        var accessors = (JArray)js["accessors"];
        var bufferViews = (JArray)js["bufferViews"];
        var a = accessors[accIndex];
        int bv = (int)a["bufferView"];
        int accOff = (int)(a["byteOffset"] ?? 0);
        int count = (int)a["count"];
        int ct = (int)a["componentType"];
        int bvOff = (int)(bufferViews[bv]["byteOffset"] ?? 0);
        int stride = (int)(bufferViews[bv]["byteStride"] ?? 0);
        if (stride == 0) stride = packedStride;
        return new Acc(binStart + bvOff + accOff, stride, count, ct);
    }

    private static Mesh BuildMesh(JObject js, byte[] data, int binStart, out int submeshCount)
    {
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };

        // We support the common single-mesh / first-primitive case (what Sketchfab/Blender
        // export for a prop). attrs we use: POSITION, NORMAL?, TEXCOORD_0?.
        var prim = js["meshes"][0]["primitives"][0];
        var attrs = (JObject)prim["attributes"];

        int posAcc = (int)attrs["POSITION"];
        var pa = Accessor(js, binStart, posAcc, 12);
        var verts = new Il2CppStructArray<Vector3>(pa.Count);
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < pa.Count; i++)
        {
            int o = pa.BaseOffset + i * pa.Stride;
            var v = new Vector3(
                -BitConverter.ToSingle(data, o),          // glTF(+X right-handed) -> Unity(left-handed): negate X
                 BitConverter.ToSingle(data, o + 4),
                 BitConverter.ToSingle(data, o + 8));
            verts[i] = v;
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        // Re-center on the geometry's own bounds: some GLBs bake the mesh far from its pivot (e.g.
        // a hat 1700u off-origin), so attaching pivot-to-bone would leave the mesh meters away.
        // Centering the pivot on the visual center makes "localPos 0 on the bone" sit it on the bone.
        var center = (min + max) * 0.5f;
        for (int i = 0; i < pa.Count; i++) verts[i] = verts[i] - center;
        mesh.vertices = verts;

        if (attrs["NORMAL"] != null)
        {
            var na = Accessor(js, binStart, (int)attrs["NORMAL"], 12);
            var norms = new Il2CppStructArray<Vector3>(na.Count);
            for (int i = 0; i < na.Count; i++)
            {
                int o = na.BaseOffset + i * na.Stride;
                norms[i] = new Vector3(
                    -BitConverter.ToSingle(data, o),
                     BitConverter.ToSingle(data, o + 4),
                     BitConverter.ToSingle(data, o + 8));
            }
            mesh.normals = norms;
        }

        if (attrs["TEXCOORD_0"] != null)
        {
            var ua = Accessor(js, binStart, (int)attrs["TEXCOORD_0"], 8);
            var uvs = new Il2CppStructArray<Vector2>(ua.Count);
            for (int i = 0; i < ua.Count; i++)
            {
                int o = ua.BaseOffset + i * ua.Stride;
                uvs[i] = new Vector2(
                    BitConverter.ToSingle(data, o),
                    1f - BitConverter.ToSingle(data, o + 4)); // glTF V is top-down
            }
            mesh.uv = uvs;
        }

        var ia = Accessor(js, binStart, (int)prim["indices"], 0);
        var tris = new Il2CppStructArray<int>(ia.Count);
        for (int i = 0; i < ia.Count; i++)
        {
            int o = ia.BaseOffset + i * IndexSize(ia.ComponentType);
            tris[i] = ia.ComponentType == UINT16
                ? BitConverter.ToUInt16(data, o)
                : (int)BitConverter.ToUInt32(data, o);
        }
        // negate-X flips winding -> reverse each triangle so front faces stay front
        for (int i = 0; i < ia.Count; i += 3)
        {
            int t = tris[i]; tris[i] = tris[i + 2]; tris[i + 2] = t;
        }
        mesh.triangles = tris;

        if (attrs["NORMAL"] == null) mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        submeshCount = 1;
        return mesh;
    }

    private static int IndexSize(int ct) => ct == UINT16 ? 2 : 4;

    // --- material / texture ---

    private static Material BuildMaterial(JObject js, byte[] data, int binStart)
    {
        // Game is a custom forward renderer (not URP); built-in "Standard" is present,
        // lit, depth-correct, and takes _MainTex. Good enough to read form + albedo.
        var mat = new Material(Shader.Find("Standard"));
        mat.color = Color.white;

        var tex = TryLoadBaseColor(js, data, binStart);
        if (tex != null) { mat.mainTexture = tex; mat.SetTexture("_MainTex", tex); }
        return mat;
    }

    private static Texture2D TryLoadBaseColor(JObject js, byte[] data, int binStart)
    {
        try
        {
            var mats = (JArray)js["materials"];
            if (mats == null || mats.Count == 0) return null;
            var baseTex = mats[0]?["pbrMetallicRoughness"]?["baseColorTexture"]?["index"];
            if (baseTex == null) return null;
            int texIndex = (int)baseTex;
            int imgSrc = (int)js["textures"][texIndex]["source"];
            var img = js["images"][imgSrc];
            if (img["bufferView"] == null) return null; // only support embedded (bufferView) images

            int bv = (int)img["bufferView"];
            var bufferViews = (JArray)js["bufferViews"];
            int off = binStart + (int)(bufferViews[bv]["byteOffset"] ?? 0);
            int len = (int)bufferViews[bv]["byteLength"];

            var png = new Il2CppStructArray<byte>(len);
            for (int i = 0; i < len; i++) png[i] = data[off + i];

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            return UnityEngine.ImageConversion.LoadImage(tex, png, false) ? tex : null;
        }
        catch { return null; }
    }

    // --- placement round-trip (asset.extras.cairnAccessory) ---

    private const string ExtrasKey = "cairnAccessory";

    public static Placement ReadPlacement(JObject js)
    {
        var extras = js["asset"]?["extras"]?[ExtrasKey] as JObject;
        return extras == null ? Placement.Default() : Placement.FromJson(extras);
    }

    /// <summary>Rewrite the .glb in place with an updated placement in asset.extras.
    /// Recomputes the JSON chunk (4-byte aligned) and rewrites the container header.</summary>
    public static void WritePlacement(string glbPath, Placement p)
    {
        byte[] data = File.ReadAllBytes(glbPath);
        var (json, binStart) = SplitContainer(data);
        var js = JObject.Parse(json);

        var asset = (JObject)(js["asset"] ?? (js["asset"] = new JObject()));
        var extras = (JObject)(asset["extras"] ?? (asset["extras"] = new JObject()));
        extras[ExtrasKey] = p.ToJson();

        // BIN bytes are everything after the (old) bin chunk header.
        int binChunkOff = binStart - 8;
        byte[] bin = new byte[data.Length - binStart];
        Array.Copy(data, binStart, bin, 0, bin.Length);

        Write(glbPath, js.ToString(Newtonsoft.Json.Formatting.None), bin);
    }

    private static void Write(string glbPath, string json, byte[] bin)
    {
        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytes.Length & 3)) & 3;        // pad with spaces (0x20)
        int binPad = (4 - (bin.Length & 3)) & 3;               // pad with zeros
        int jsonChunkLen = jsonBytes.Length + jsonPad;
        int binChunkLen = bin.Length + binPad;
        int total = 12 + 8 + jsonChunkLen + 8 + binChunkLen;

        using var ms = new MemoryStream(total);
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u);          // "glTF"
        w.Write(2u);                   // version
        w.Write((uint)total);
        w.Write((uint)jsonChunkLen);
        w.Write(0x4E4F534Au);          // "JSON"
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)binChunkLen);
        w.Write(0x004E4942u);          // "BIN\0"
        w.Write(bin);
        for (int i = 0; i < binPad; i++) w.Write((byte)0x00);
        w.Flush();
        File.WriteAllBytes(glbPath, ms.ToArray());
    }
}
