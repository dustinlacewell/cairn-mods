using System;
using System.IO;
using MelonLoader.Utils;
using Newtonsoft.Json;

namespace CairnArchipelago;

// Persists how many received items have been granted into the game, per
// (seed, slot), so reconnects and game restarts never double-grant.
internal class GrantLedger
{
    public int GrantedCount;

    [JsonIgnore]
    private string path;

    public static GrantLedger Load(string seed, string slot)
    {
        var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "CairnArchipelago");
        Directory.CreateDirectory(dir);
        var safeSlot = string.Concat(slot.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(dir, $"ledger-{seed}-{safeSlot}.json");

        var ledger = File.Exists(path)
            ? JsonConvert.DeserializeObject<GrantLedger>(File.ReadAllText(path)) ?? new GrantLedger()
            : new GrantLedger();
        ledger.path = path;
        return ledger;
    }

    public void Save() => File.WriteAllText(path, JsonConvert.SerializeObject(this));
}
