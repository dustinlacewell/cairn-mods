using System.Collections.Generic;
using UnityEngine;
using Il2Cpp;
using TGB = Il2CppTheGameBakers.Cairn;

namespace CairnAPI;

// One story-beat sensor: its authored label and world position (bounds center of trigger volume).
/// <summary>A story-beat sensor location.</summary>
public readonly struct Beat
{
    public Beat(string label, Vector3 position) { Label = label; Position = position; }

    /// <summary>Authored beat name.</summary>
    public string Label { get; }

    /// <summary>World-space center of the sensor trigger.</summary>
    public Vector3 Position { get; }
}

// Read-only access to the live story-beat sensors from StoryEventManager.allSensors.
// Pure snapshot — no caching, no UI. Beats are sorted by label on every call.
/// <summary>
/// Enumerate story-beat sensors in the current scene.
/// <code class="lang-csharp">
/// if (!Beats.Available) return;
///
/// var beats = Beats.Snapshot();
/// foreach (var b in beats)
///     Log($"{b.Label}  @{b.Position}");
///
/// Teleport.To(beats[0].Position, _ =&gt; { });
/// </code>
/// </summary>
public static class Beats
{
    /// <summary>True when the story manager is live. Check before calling Snapshot.</summary>
    /// <returns>bool</returns>
    public static bool Available => MoSingleton<TGB.StoryEventManager>.Instance != null;

    /// <summary>Every story-beat sensor in the current scene, sorted by label. Pure — no caching.</summary>
    /// <returns>List&lt;Beat&gt;</returns>
    public static List<Beat> Snapshot()
    {
        var result = new List<Beat>();
        var manager = MoSingleton<TGB.StoryEventManager>.Instance;
        if (manager == null) return result;

        var sensors = manager.allSensors;
        if (sensors == null) return result;

        for (int i = 0; i < sensors.Count; i++)
        {
            var sensor = sensors[i];
            if (sensor == null) continue;
            if (!TryPosition(sensor, out var pos)) continue;
            result.Add(new Beat(Label(sensor), pos));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        return result;
    }

    // Authored beat name (e.g. "_21010_Waterfall_Seq"); falls back to the GameObject name.
    private static string Label(StoryEventSensor sensor)
    {
        var setup = sensor.setup;
        if (setup != null)
        {
            var enumId = StoryEventSensorStringIdEnumHelper.ToEnum(setup.id);
            var name = StoryEventSensorStringIdEnumHelper.FastToString(enumId);
            if (!string.IsNullOrEmpty(name) && name != "None")
                return name;
        }
        var go = sensor.gameObject;
        return go != null ? go.name : "(unnamed sensor)";
    }

    // Bounds center of the first trigger collider; falls back to transform position.
    private static bool TryPosition(StoryEventSensor sensor, out Vector3 pos)
    {
        pos = default;
        var colliders = sensor.Colliders;
        if (colliders != null && colliders.Length > 0 && colliders[0] != null)
        {
            pos = colliders[0].bounds.center;
            return true;
        }
        var t = sensor.transform;
        if (t == null) return false;
        pos = t.position;
        return true;
    }
}
