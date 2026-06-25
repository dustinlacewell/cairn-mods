using System.Collections.Generic;
using Il2Cpp;

namespace CairnAPI;

// Read-only access to Cairn's authored world catalog — the 6 WorldZoneData (Kami, Lakpa, Tenzing, …) and
// their ZoneSceneData, all resident as ScriptableObjects even when their scenes are not streamed. This is
// the enumerator the teleport layer uses to resolve "which sector owns this position" by ASSET (not by the
// position-driven streamer, which only sees loaded zones). See re/systems/world-streaming/.
public static class World
{
    /// <summary>The active world the StreamingManager is currently streaming, or null pre-gameplay.</summary>
    public static WorldZoneData Current
    {
        get
        {
            var sm = MoSingleton<StreamingManager>.Instance;
            return sm != null ? sm.World : null;
        }
    }

    /// <summary>Every authored world, resident even when only one is streamed (from StreamingTweakables).</summary>
    public static IReadOnlyList<WorldZoneData> Worlds()
    {
        var result = new List<WorldZoneData>();
        var tweak = TweakableBase<StreamingTweakables>.Instance;
        if (tweak == null) return result;
        var src = tweak.worldZoneDataList;
        if (src != null)
            for (int i = 0; i < src.Count; i++)
                if (src[i] != null) result.Add(src[i]);
        return result;
    }

    /// <summary>Every zone of a world (its authored ZoneSceneData list).</summary>
    public static IReadOnlyList<ZoneSceneData> Zones(WorldZoneData world)
    {
        var result = new List<ZoneSceneData>();
        var src = world?.zones;
        if (src != null)
            for (int i = 0; i < src.Count; i++)
                if (src[i] != null) result.Add(src[i]);
        return result;
    }

    /// <summary>Find a zone by its asset name (e.g. "01_FirstRidge") within a world.</summary>
    public static ZoneSceneData ResolveZone(WorldZoneData world, string zoneName)
        => world != null ? world.GetZoneSceneData(zoneName) : null;

    /// <summary>The world that owns a zone (by identity scan over the catalog), or null.</summary>
    public static WorldZoneData WorldOf(ZoneSceneData zone)
    {
        if (zone == null) return null;
        foreach (var w in Worlds())
        {
            var zones = w.zones;
            if (zones == null) continue;
            for (int i = 0; i < zones.Count; i++)
                if (zones[i] != null && zones[i].Pointer == zone.Pointer) return w;
        }
        return null;
    }
}
