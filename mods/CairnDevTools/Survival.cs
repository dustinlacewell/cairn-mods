using HarmonyLib;
using Il2Cpp;

namespace CairnDevTools;

/// <summary>
/// Freeze hunger / thirst / cold for long live-debugging sessions, so the climbers don't starve mid-
/// investigation. PRIMARY mechanism: block the drain AT THE SOURCE — a prefix on
/// <see cref="ClimberData"/>.<c>Set(SurvivalStat, float)</c> that refuses any write LOWERING Hunger/Thirst/
/// Cold while frozen, pinning the real stored value at its current (full) level. (The earlier "patch only
/// the ratio getters" approach was insufficient: the pass-out/kill path reads the RAW status value, not the
/// ratio, so the climber still starved and fell — which repeatedly killed bug-repro specimens. The getter/
/// warning patches below are kept as belt-and-suspenders for the UI.) Toggle with the <c>survival</c>
/// console command; default OFF.
/// </summary>
internal static class Survival
{
    internal static bool Frozen;

    /// <summary>Console command: <c>survival</c> toggles, <c>survival on|off</c> sets explicitly.</summary>
    internal static string Command(string[] args)
    {
        if (args.Length > 0)
            Frozen = args[0].Equals("on", System.StringComparison.OrdinalIgnoreCase);
        else
            Frozen = !Frozen;
        return "survival freeze (hunger/thirst/cold): " + (Frozen ? "ON — stats pinned (Set-block)" : "OFF — normal drain");
    }

    /// <summary>PRIMARY freeze: refuse any write that LOWERS a frozen stat, pinning the real stored value at
    /// its current level (lock at full by enabling right after a full-stat load). Hunger=1, Thirst=2,
    /// Cold=3 (SurvivalStat enum). Manual refills (raising the value) still pass through.</summary>
    [HarmonyPatch(typeof(ClimberData), nameof(ClimberData.Set))]
    private static class SetBlockPatch
    {
        private static void Prefix(ClimberData __instance, SurvivalStat stat, ref float value)
        {
            if (!Frozen) return;
            if (stat != SurvivalStat.Hunger && stat != SurvivalStat.Thirst && stat != SurvivalStat.Cold) return;
            float cur = __instance.Get(stat);
            if (value < cur) value = cur; // never decrease while frozen
        }
    }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.HungerRatio), MethodType.Getter)]
    private static class HungerRatioPatch
    {
        private static void Postfix(ref float __result) { if (Frozen) __result = 1f; }
    }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.ThirstRatio), MethodType.Getter)]
    private static class ThirstRatioPatch
    {
        private static void Postfix(ref float __result) { if (Frozen) __result = 1f; }
    }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.ColdRatio), MethodType.Getter)]
    private static class ColdRatioPatch
    {
        private static void Postfix(ref float __result) { if (Frozen) __result = 1f; }
    }

    // The drain/kill and UI also read warning/critical flags directly — pin them clear while frozen so a
    // stale critical state can't kill or debuff between ratio reads.
    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.HungerWarning), MethodType.Getter)]
    private static class HungerWarningPatch { private static void Postfix(ref bool __result) { if (Frozen) __result = false; } }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.HungerCritical), MethodType.Getter)]
    private static class HungerCriticalPatch { private static void Postfix(ref bool __result) { if (Frozen) __result = false; } }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.ThirstWarning), MethodType.Getter)]
    private static class ThirstWarningPatch { private static void Postfix(ref bool __result) { if (Frozen) __result = false; } }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.ThirstCritical), MethodType.Getter)]
    private static class ThirstCriticalPatch { private static void Postfix(ref bool __result) { if (Frozen) __result = false; } }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.ColdWarning), MethodType.Getter)]
    private static class ColdWarningPatch { private static void Postfix(ref bool __result) { if (Frozen) __result = false; } }

    [HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.ColdCritical), MethodType.Getter)]
    private static class ColdCriticalPatch { private static void Postfix(ref bool __result) { if (Frozen) __result = false; } }
}
