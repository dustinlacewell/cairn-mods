using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MelonLoader.Utils;
using UnityEngine;

namespace CairnRoutes;

/// <summary>One recorded route: a named world-space polyline over the mountain.</summary>
public class RouteData
{
    public string Name { get; set; } = "Unnamed route";
    public List<float[]> Points { get; set; } = new();
    public int ColorIndex { get; set; }
    public int Difficulty { get; set; }
    public bool Visible { get; set; } = true;
    public string CreatedAt { get; set; }

    public Vector3 PointAt(int i) => new(Points[i][0], Points[i][1], Points[i][2]);

    // Property would be picked up by System.Text.Json, and the Il2Cpp Vector3
    // proxy is infinitely recursive to a serializer (normalized → Vector3 → …).
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Start => PointAt(0);

    public float LengthMeters()
    {
        float total = 0f;
        for (int i = 1; i < Points.Count; i++)
            total += Vector3.Distance(PointAt(i - 1), PointAt(i));
        return total;
    }
}

/// <summary>Owns the route list and its JSON persistence under UserData/CairnRoutes.</summary>
public class RouteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<RouteData> Routes { get; private set; } = new();

    private readonly string _file;
    private float _dirtySince = -1f;

    public RouteStore()
    {
        var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "CairnRoutes");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "routes.json");
    }

    public void Load()
    {
        if (!File.Exists(_file)) return;
        Routes = JsonSerializer.Deserialize<List<RouteData>>(File.ReadAllText(_file)) ?? new();
    }

    public RouteData Add(string name, List<Vector3> points)
    {
        var route = new RouteData
        {
            Name = name,
            ColorIndex = Routes.Count,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
        foreach (var p in points)
            route.Points.Add(new[] { p.x, p.y, p.z });
        Routes.Add(route);
        Save();
        return route;
    }

    public void Remove(RouteData route)
    {
        Routes.Remove(route);
        Save();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(Routes, JsonOptions));
        }
        catch (System.Exception e)
        {
            MelonLoader.MelonLogger.Error($"[CairnRoutes] Saving routes.json failed: {e.Message}");
        }
        _dirtySince = -1f;
    }

    /// <summary>Debounced save for high-frequency edits (rename keystrokes).</summary>
    public void MarkDirty() { if (_dirtySince < 0f) _dirtySince = Time.unscaledTime; }

    public void SaveIfDue()
    {
        if (_dirtySince >= 0f && Time.unscaledTime - _dirtySince > 1f) Save();
    }
}
