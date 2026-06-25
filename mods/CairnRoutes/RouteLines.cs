using System.Collections.Generic;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace CairnRoutes;

/// <summary>
/// Hosts each route on a real ClimbPathRenderer — the game's official route
/// renderer — so mod routes get the native material, difficulty colors,
/// per-camera width scaling (OnCameraPreCull), and PathRecorder difficulty
/// tagging (via the populated spline + global registry).
///
/// Two Ghidra-verified nudges make them visible in normal play:
/// official routes boot with EnableGraphics(false) in OnEnable (they're drawn
/// by the topo-view command buffer instead), and a fresh component's
/// lineWidthMulitplier of 0 would zero the line width. We force both.
///
/// Objects live in the active scene, so they vanish on scene unload — Rebuild()
/// is cheap and idempotent and runs whenever the pawn (re)spawns.
/// </summary>
public class RouteLines
{
    private static readonly Color[] Palette =
    {
        new(0.95f, 0.55f, 0.15f), // orange
        new(0.25f, 0.75f, 0.95f), // sky
        new(0.55f, 0.90f, 0.30f), // green
        new(0.95f, 0.35f, 0.55f), // pink
        new(0.85f, 0.80f, 0.25f), // yellow
        new(0.65f, 0.45f, 0.95f), // violet
    };

    private const float SplineKnotSpacing = 3f;

    private GameObject _root;
    private readonly Dictionary<RouteData, ClimbPathRenderer> _paths = new();
    private Material _material;

    public bool IsAlive => _root != null;

    public void Rebuild(IEnumerable<RouteData> routes)
    {
        Clear();
        _root = new GameObject("CairnRoutes.Lines");
        foreach (var route in routes)
            if (route.Visible && route.Points.Count >= 2)
                _paths[route] = BuildPath(route);
    }

    public void SetVisible(RouteData route, bool visible)
    {
        route.Visible = visible;
        if (_root == null) return;
        if (_paths.TryGetValue(route, out var path) && path != null)
        {
            // Reactivation reruns OnEnable → EnableGraphics(false); flip it back.
            path.gameObject.SetActive(visible);
            if (visible) path.EnableGraphics(true);
        }
        else if (visible && route.Points.Count >= 2)
        {
            _paths[route] = BuildPath(route);
        }
    }

    /// <summary>Rebuild one route's path (e.g. after a difficulty change).</summary>
    public void Refresh(RouteData route)
    {
        Remove(route);
        if (_root != null && route.Visible && route.Points.Count >= 2)
            _paths[route] = BuildPath(route);
    }

    public void Remove(RouteData route)
    {
        if (_paths.TryGetValue(route, out var path) && path != null)
            Object.Destroy(path.gameObject);
        _paths.Remove(route);
    }

    public void Clear()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        _paths.Clear();
    }

    private ClimbPathRenderer BuildPath(RouteData route)
    {
        var go = new GameObject($"CairnRoute:{route.Name}");
        go.transform.SetParent(_root.transform, worldPositionStays: true);
        go.SetActive(false); // configure fully before OnEnable registers it

        var path = go.AddComponent<ClimbPathRenderer>(); // SplineContainer comes via RequireComponent
        path.lineMaterial = ResolveMaterial();
        path.lineWidthMulitplier = 1f;
        path.noIcons = true;
        path.difficulty = route.Difficulty;
        path.detectionRadius = 4f;
        path.lineColor = ColorFor(route);
        PopulateSpline(go.GetComponent<SplineContainer>(), route);

        go.SetActive(true); // OnEnable: registry + width-on-precull subscription

        var line = path.AddNewLineRenderer(0);
        var points = new Vector3[route.Points.Count];
        for (int i = 0; i < points.Length; i++) points[i] = route.PointAt(i);
        line.useWorldSpace = true;
        line.positionCount = points.Length;
        line.SetPositions(points);

        path.EnableGraphics(true); // OnEnable booted with graphics off
        line.enabled = true;
        return path;
    }

    /// <summary>
    /// The spline isn't used for drawing (we set line positions directly) but it
    /// must hold real knots: the global registry queries it for closest-route
    /// lookups, and it's what makes PathRecorder difficulty tagging work.
    /// </summary>
    private static void PopulateSpline(SplineContainer container, RouteData route)
    {
        var spline = new Spline();
        Vector3 last = route.PointAt(0);
        spline.Add(new float3(last.x, last.y, last.z), TangentMode.Linear);
        for (int i = 1; i < route.Points.Count; i++)
        {
            var p = route.PointAt(i);
            if (Vector3.Distance(last, p) < SplineKnotSpacing && i != route.Points.Count - 1)
                continue;
            spline.Add(new float3(p.x, p.y, p.z), TangentMode.Linear);
            last = p;
        }
        container.Spline = spline;
    }

    /// <summary>Difficulty color from the game's own tweakables; palette fallback.</summary>
    private static Color ColorFor(RouteData route)
    {
        try
        {
            var difficulties = ClimbingGlobalTweakables.Instance?.topologyView?.difficulties;
            if (difficulties != null && difficulties.Length > 0)
                return difficulties[System.Math.Clamp(route.Difficulty, 0, difficulties.Length - 1)].color;
        }
        catch
        {
            // tweakables not loaded yet — fall through
        }
        return Palette[route.ColorIndex % Palette.Length];
    }

    /// <summary>Official route material from any scene route; Aava path material as fallback.</summary>
    private Material ResolveMaterial()
    {
        if (_material != null) return _material;

        var donor = Object.FindObjectOfType<ClimbPathRenderer>();
        if (donor != null && donor.lineMaterial != null)
            return _material = donor.lineMaterial;

        var aava = Object.FindObjectOfType<AavaPathRenderer>();
        if (aava != null && aava.lineMaterial != null)
            return _material = aava.lineMaterial;

        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        return _material = new Material(shader);
    }
}
