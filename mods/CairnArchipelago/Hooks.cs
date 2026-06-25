using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace CairnArchipelago;

// Story sensors fire once as the player climbs past each story beat — they are
// the location checks. setup.id is the sensor's StoryEventSensorStringId,
// whose int value the apworld baked into AP location ids.
[HarmonyPatch(typeof(StoryEventSensor), nameof(StoryEventSensor.OnTrigger))]
internal static class StoryEventSensorOnTriggerPatch
{
    private static void Postfix(StoryEventSensor __instance)
    {
        var setup = __instance.setup;
        if (setup == null)
            return;
        Core.OnStorySensorTriggered(setup.id.Value, __instance.name);
    }
}

// Groundwork for loot-pickup locations (v2): log every loot interaction with
// its stable gpeId so scan data and playtest logs can build the location table.
[HarmonyPatch(typeof(LootProvider), nameof(LootProvider.Interact))]
internal static class LootProviderInteractPatch
{
    private static void Postfix(LootProvider __instance)
    {
        MelonLogger.Msg($"[loot] '{__instance.name}' gpeId={__instance.gpeId.Value}");
    }
}
