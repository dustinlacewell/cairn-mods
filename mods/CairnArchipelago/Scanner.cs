using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CairnArchipelago;

// F8 world dump: every interaction provider (loot spots, with stable gpeIds)
// and story sensor in the loaded scenes, plus the full item library. This is
// the dataset the v2 apworld location tables get authored from.
internal static class Scanner
{
    public static void DumpWorld(MelonLogger.Instance log)
    {
        var scene = SceneManager.GetActiveScene().name;
        var dump = new Dictionary<string, object>
        {
            ["scene"] = scene,
            ["interactionProviders"] = CollectProviders(),
            ["storySensors"] = CollectSensors(),
            ["itemLibrary"] = CollectItems(),
        };

        var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "CairnArchipelago", "scans");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"scan-{scene}.json");
        File.WriteAllText(path, JsonConvert.SerializeObject(dump, Formatting.Indented));
        log.Msg($"World scan written to {path}");
    }

    private static List<object> CollectProviders()
    {
        var rows = new List<object>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<InteractionProvider>()))
        {
            var provider = obj.TryCast<InteractionProvider>();
            if (provider == null)
                continue;
            var position = provider.transform.position;
            rows.Add(new Dictionary<string, object>
            {
                ["name"] = provider.name,
                ["type"] = provider.GetIl2CppType().Name,
                ["gpeId"] = provider.gpeId.Value,
                ["scene"] = provider.gameObject.scene.name,
                ["position"] = new[] { position.x, position.y, position.z },
                ["lootItem"] = LootItemOf(provider),
            });
        }
        return rows;
    }

    private static string LootItemOf(InteractionProvider provider)
    {
        var loot = provider.TryCast<LootProvider>();
        var table = loot?.LootTable;
        if (table == null || table.loots == null || table.loots.Length == 0)
            return null;
        return ((InventoryItemStringIdEnum)loot.LootTable.loots[0].itemId.Value).ToString();
    }

    private static List<object> CollectSensors()
    {
        var rows = new List<object>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<StoryEventSensor>()))
        {
            var sensor = obj.TryCast<StoryEventSensor>();
            if (sensor?.setup == null)
                continue;
            var position = sensor.transform.position;
            rows.Add(new Dictionary<string, object>
            {
                ["name"] = sensor.name,
                ["sensorId"] = sensor.setup.id.Value,
                ["sensorIdName"] = ((StoryEventSensorStringIdEnum)sensor.setup.id.Value).ToString(),
                ["scene"] = sensor.gameObject.scene.name,
                ["position"] = new[] { position.x, position.y, position.z },
            });
        }
        return rows;
    }

    private static List<object> CollectItems()
    {
        var rows = new List<object>();
        var manager = InventoryManager.Instance;
        if (manager?.library?.items == null)
            return rows;
        foreach (var item in manager.library.items)
        {
            if (item == null)
                continue;
            rows.Add(new Dictionary<string, object>
            {
                ["id"] = item.id.Value,
                ["enumName"] = ((InventoryItemStringIdEnum)item.id.Value).ToString(),
                ["type"] = item.GetIl2CppType().Name,
                ["storedIn"] = item.storedIn.ToString(),
            });
        }
        return rows;
    }
}
