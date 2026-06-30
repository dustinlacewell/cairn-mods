using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using TGB = Il2CppTheGameBakers.Cairn;
using LocKey = Il2CppTGBTools.Localization.LocKeyStringId;

namespace CairnFreeRoam;

/// <summary>One user-placed warp point: a world position with a label, persisted across sessions.</summary>
public class BookmarkData
{
    public float[] Pos { get; set; } = new float[3];
    public string Label { get; set; } = "Bookmark";

    // System.Text.Json would recurse into the Il2Cpp Vector3 proxy (.normalized → Vector3 → …) forever; only
    // the serializable float[] Pos is persisted, Position is a runtime convenience.
    [System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Position => new(Pos[0], Pos[1], Pos[2]);
    public static float[] Of(Vector3 v) => new[] { v.x, v.y, v.z };
}

/// <summary>
/// Owns the persisted bookmark list (UserData/CairnFreeRoam/bookmarks.json) and the live FreeRoamWarpPoints
/// created from it. A bookmark is a FreeRoamWarpPoint on an INACTIVE GameObject (so its Awake never runs and
/// it does not auto-register) that we Register into FreeRoamManager.orderedWarpPoints ourselves — the same
/// deterministic pattern CairnAPI.Teleport uses, here kept registered so it appears in the eagle-eye list.
/// The native SortWarps then orders it by world-Y height automatically (SortKey = transform.position.y).
/// </summary>
public class BookmarkStore
{
    private const string GoPrefix = "CairnFreeRoam.Bookmark";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _file;
    private readonly List<BookmarkData> _bookmarks = new();
    private readonly HashSet<IntPtr> _livePoints = new();

    // live warp point → the CairnAPI loc key that renders its name. CairnAPI.Localization owns the id band,
    // the registry, and the LocalizationManager.Get prefix now; we just map each point to its key so
    // rename/delete can target it. The key is written into the point's locKey field (SetLocKey) so the game's
    // own refresh pipeline resolves the name through CairnAPI's hook everywhere, durably.
    private readonly Dictionary<IntPtr, LocKey> _locIds = new();

    public BookmarkStore()
    {
        var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "CairnFreeRoam");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "bookmarks.json");
        Load();
    }

    public IReadOnlyList<BookmarkData> Bookmarks => _bookmarks;

    /// <summary>The CairnAPI loc key for a live warp point, used to target rename/delete — default if not ours.</summary>
    public LocKey LocKeyOf(TGB.FreeRoamWarpPoint wp)
        => wp != null && _locIds.TryGetValue(wp.Pointer, out var key) ? key : default;

    /// <summary>True if this warp point is one of ours (membership test for the delete/rename gate).</summary>
    public bool IsCustom(TGB.FreeRoamWarpPoint wp)
    {
        if (wp == null) return false;
        if (_livePoints.Contains(wp.Pointer)) return true;
        var go = wp.gameObject;
        return go != null && go.name != null && go.name.StartsWith(GoPrefix);
    }

    /// <summary>The persisted record behind a live warp point (matched by position), or null.</summary>
    public BookmarkData DataFor(TGB.FreeRoamWarpPoint wp)
    {
        if (!IsCustom(wp)) return null;
        var pos = wp.transform.position;
        BookmarkData best = null; float bestSq = 0.01f;
        foreach (var b in _bookmarks)
        {
            float sq = (b.Position - pos).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = b; }
        }
        return best;
    }

    /// <summary>Commit a new name for a bookmark and persist.</summary>
    public void Rename(BookmarkData data, string name)
    {
        if (data == null) return;
        data.Label = string.IsNullOrWhiteSpace(name) ? "Bookmark" : name.Trim();
        Save();
    }

    /// <summary>Persist a new bookmark at <paramref name="pos"/> and register a live warp point for it.</summary>
    public BookmarkData Add(Vector3 pos, string label)
    {
        var data = new BookmarkData { Pos = BookmarkData.Of(pos), Label = label };
        _bookmarks.Add(data);
        Save();
        Spawn(data);
        return data;
    }

    /// <summary>Remove a live warp point and its persisted record. Returns true if it was ours.</summary>
    public bool Remove(TGB.FreeRoamWarpPoint wp)
    {
        if (!IsCustom(wp)) return false;
        var pos = wp.transform.position;

        var manager = MoSingleton<TGB.FreeRoamManager>.Instance;
        if (manager != null) manager.Unregister(wp);
        if (_locIds.TryGetValue(wp.Pointer, out var locKey))
        {
            CairnAPI.Localization.Unregister(locKey);
            _locIds.Remove(wp.Pointer);
        }
        _livePoints.Remove(wp.Pointer);
        var go = wp.gameObject;
        if (go != null) UnityEngine.Object.Destroy(go);

        // Drop the nearest matching persisted record (positions are exact at save time).
        int best = -1; float bestSq = 0.01f;
        for (int i = 0; i < _bookmarks.Count; i++)
        {
            float sq = (_bookmarks[i].Position - pos).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        if (best >= 0) { _bookmarks.RemoveAt(best); Save(); }
        return true;
    }

    /// <summary>(Re)create live warp points for every persisted bookmark — call on each gameplay load. Each
    /// spawn registers a fresh CairnAPI loc key for its name; keys accumulate in CairnAPI across cycles (cheap),
    /// we only reset our point→key map here.</summary>
    public void RegisterAll()
    {
        // Drop any loc keys from a prior registration BEFORE clearing the map, so CairnAPI's registry never
        // orphans entries (the MCSpawned-edge guard normally pairs this with UnregisterAll, but make RegisterAll
        // self-correct regardless of call order — orphaned keys would otherwise leak in CairnAPI.Localization).
        DropLocKeys();
        _livePoints.Clear();
        _locIds.Clear();
        foreach (var b in _bookmarks) Spawn(b);
    }

    /// <summary>Unregister and destroy every live bookmark warp point — call on gameplay teardown so the
    /// manager never keeps a dangling entry whose GO a scene unload is about to destroy un-unregistered.</summary>
    public void UnregisterAll()
    {
        var manager = MoSingleton<TGB.FreeRoamManager>.Instance;
        if (manager == null) { DropLocKeys(); _livePoints.Clear(); _locIds.Clear(); return; }

        // Unregister mutates orderedWarpPoints (List.Remove), so iterate backwards by index.
        var list = manager.orderedWarpPoints;
        if (list != null)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var wp = list[i];
                if (wp == null || !IsCustom(wp)) continue;
                manager.Unregister(wp);
                var go = wp.gameObject;
                if (go != null) UnityEngine.Object.Destroy(go);
            }
        }
        DropLocKeys();
        _livePoints.Clear();
        _locIds.Clear();
    }

    /// <summary>Release every CairnAPI loc key we hold (teardown drops the live points, so their names are no
    /// longer needed). RegisterAll re-registers fresh keys on the next gameplay load.</summary>
    private void DropLocKeys()
    {
        foreach (var key in _locIds.Values) CairnAPI.Localization.Unregister(key);
    }

    private void Spawn(BookmarkData data)
    {
        var manager = MoSingleton<TGB.FreeRoamManager>.Instance;
        if (manager == null) return;

        var go = new GameObject($"{GoPrefix}:{data.Label}");
        go.SetActive(false); // Awake never runs → no auto-register / no event subscription side effects
        go.transform.position = data.Position;

        // SortWarps orders by ZoneName (= gameObject.scene.name) THEN world-Y height. A fresh GO lands in
        // CommonBaseScene, a different group from the real points' streamed sub-scene, so it would NOT
        // interleave by height. Move it into the nearest existing warp point's scene so it shares that zone
        // group and sorts in by height. (Not DontDestroyOnLoad: a scene unload destroys it, and RegisterAll
        // rebuilds on the next gameplay load — verified the move yields the matching ZoneName + correct slot.)
        var anchor = NearestRealPoint(manager, data.Position);
        if (anchor != null)
            SceneManager.MoveGameObjectToScene(go, anchor.gameObject.scene);

        var wp = go.AddComponent<TGB.FreeRoamWarpPoint>();
        SetWarpMode(wp, (int)PawnControllerSwitcher.Mode.Walking);
        wp.linkedToBivouac = false; // → IsKnown() true → selectable/warpable, never greyed

        // Register the bookmark's name with CairnAPI.Localization and write the returned loc key into the
        // point's locKey field. The row name cell is a LocalizedText fed by wp.LocKey through
        // LocalizationManager.Get; CairnAPI's Get hook returns our name for that key, so the game's own refresh
        // pipeline renders this bookmark's name everywhere and never reverts it.
        var key = CairnAPI.Localization.Register(data.Label);
        SetLocKey(wp, key.Value);
        _locIds[wp.Pointer] = key;

        manager.Register(wp);
        _livePoints.Add(wp.Pointer);
    }

    /// <summary>Nearest non-custom warp point to a position — used to borrow its scene for zone-grouping.</summary>
    private TGB.FreeRoamWarpPoint NearestRealPoint(TGB.FreeRoamManager manager, Vector3 pos)
    {
        var list = manager.orderedWarpPoints;
        if (list == null) return null;
        TGB.FreeRoamWarpPoint best = null;
        float bestSq = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            var w = list[i];
            if (w == null || w.gameObject == null) continue;
            if (w.gameObject.name.StartsWith(GoPrefix)) continue; // skip our own
            float sq = (w.transform.position - pos).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = w; }
        }
        return best;
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<BookmarkData>>(File.ReadAllText(_file));
            if (loaded != null) { _bookmarks.Clear(); _bookmarks.AddRange(loaded); }
        }
        catch (Exception e) { MelonLogger.Error($"[CairnFreeRoam] bookmarks.json load failed: {e.Message}"); }
    }

    private void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_bookmarks, JsonOptions)); }
        catch (Exception e) { MelonLogger.Error($"[CairnFreeRoam] bookmarks.json save failed: {e.Message}"); }
    }

    /// <summary>warpMode is a private serialized field; a fresh component holds Mode.None.</summary>
    private static unsafe void SetWarpMode(TGB.FreeRoamWarpPoint wp, int mode)
    {
        IntPtr cls = Il2CppClassPointerStore<TGB.FreeRoamWarpPoint>.NativeClassPtr;
        IntPtr obj = IL2CPP.Il2CppObjectBaseToPtrNotNull(wp);
        IntPtr field = IL2CPP.GetIl2CppField(cls, "warpMode");
        *(int*)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(field)) = mode;
    }

    /// <summary>locKey is a private serialized LocKeyStringId — a single int wrapper, so writing the int at the
    /// field offset sets its .value. Same unsafe field-write idiom as SetWarpMode.</summary>
    private static unsafe void SetLocKey(TGB.FreeRoamWarpPoint wp, int locId)
    {
        IntPtr cls = Il2CppClassPointerStore<TGB.FreeRoamWarpPoint>.NativeClassPtr;
        IntPtr obj = IL2CPP.Il2CppObjectBaseToPtrNotNull(wp);
        IntPtr field = IL2CPP.GetIl2CppField(cls, "locKey");
        *(int*)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(field)) = locId;
    }
}
