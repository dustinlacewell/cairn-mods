using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CairnArchipelago.Core), "CairnArchipelago", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnArchipelago;

// Archipelago multiworld client for Cairn.
//
// Locations: the game's numbered story-beat sensors. StoryEventSensor.OnTrigger
// is patched (Hooks.cs) to send a location check for LOCATION_BASE + sensor id.
// Items: received AP items map back to InventoryItemStringIdEnum values
// (ITEM_BASE + value) and are granted into the matching inventory (ItemGranter).
// Goal: slot_data carries the AP id of the final story sensor; checking it
// sends StatusUpdate(Goal).
public class Core : MelonMod
{
    public const long ItemBase = 71_110_000;
    public const long LocationBase = 71_120_000;

    internal static Core Instance;

    internal static MelonPreferences_Entry<bool> Enabled;
    internal static MelonPreferences_Entry<string> Host;
    internal static MelonPreferences_Entry<int> Port;
    internal static MelonPreferences_Entry<string> SlotName;
    internal static MelonPreferences_Entry<string> Password;

    internal ApClient Client;

    public override void OnInitializeMelon()
    {
        Instance = this;

        var cat = MelonPreferences.CreateCategory("CairnArchipelago");
        Enabled = cat.CreateEntry("Enabled", false,
            description: "Connect to an Archipelago server and randomize this playthrough.");
        Host = cat.CreateEntry("Host", "archipelago.gg",
            description: "Archipelago server hostname.");
        Port = cat.CreateEntry("Port", 38281,
            description: "Archipelago server port.");
        SlotName = cat.CreateEntry("SlotName", "Aava",
            description: "Your slot (player) name in the multiworld.");
        Password = cat.CreateEntry("Password", "",
            description: "Server password, if any.");

        Client = new ApClient(LoggerInstance);
    }

    public override void OnUpdate()
    {
        if (!Enabled.Value)
            return;

        Client.EnsureConnected();
        if (ItemGranter.WorldReady())
            Client.PumpReceivedItems(ItemGranter.Grant);

        if (Input.GetKeyDown(KeyCode.F8))
            Scanner.DumpWorld(LoggerInstance);
    }

    public override void OnApplicationQuit() => Client.Disconnect();

    // Called from Hooks on the main thread whenever a story sensor fires.
    internal static void OnStorySensorTriggered(int sensorValue, string sensorName)
    {
        if (Instance == null || !Enabled.Value)
            return;
        Instance.Client.SendLocationCheck(LocationBase + sensorValue, sensorName);
    }
}
