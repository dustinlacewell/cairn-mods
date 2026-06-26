using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;

namespace CairnAPI;

// Stamina API — climbing stamina and effort tuning.
//
// Three independent scaling layers, each named so multiple mods coexist:
//
//   EffortScale — multiplies CurrentEffortCostMultiplier (the solver-level effort cost
//     read by the SST IK solver). Affects grip resistance, limb dynamics, and indirectly
//     the effort ratio → stamina drain pipeline. 1.0 = no effect.
//
//   DrainScale — multiplies _CurrentStaminaConsumptionSpeed after UpdateStaminaState
//     computes it. This is the DIRECT drain rate: positive values drain, negative gain.
//     A per-limb provider lets each limb drain at a different rate (e.g. based on which
//     hand is loaded). 1.0 = no effect.
//
//   Package — live access to ClimbingTweakablesPackage.Stamina fields (baseStamina,
//     staminaGainSpeed, staminaLossSpeed, thresholds, etc.). Write these to change the
//     global stamina model. These are shared across all limbs — use DrainScale for
//     per-limb control.
//
// Effort vs Drain:
//   EffortRatio = SmoothedEffort.magnitude / MaxEffortWeight (how hard the limb works).
//   Above handFreeEffortThreshold → draining; below handRestThreshold → gaining.
//   DrainScale multiplies the drain speed directly; EffortScale changes solver load
//   (indirectly shifts EffortRatio, affecting which state the limb is in).
//
// StaminaState (read-only, per-limb):
//   0 = resting (gaining), 1 = neutral (stopped), 2 = exerting (draining), 5 = punished.
public static class Stamina
{
    // ── Effort scales (solver-level, multiplies CurrentEffortCostMultiplier) ──────────

    private static readonly Dictionary<string, float> _globalEffortScales = new();
    private static readonly Dictionary<string, Func<ClimbingV2PawnLimb, float>> _effortProviders = new();

    /// <summary>Register (or replace) a global effort cost multiplier. Applied uniformly to
    /// all limbs via CurrentEffortCostMultiplier. 1.0 = no effect.</summary>
    public static void AddEffortScale(string key, float scale)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        _globalEffortScales[key] = scale;
    }

    /// <summary>Remove a registered global effort scale.</summary>
    public static void RemoveEffortScale(string key)
    {
        if (key != null) _globalEffortScales.Remove(key);
    }

    /// <summary>Register (or replace) a per-limb effort scale provider. Callback receives the
    /// limb and returns a multiplier for that limb's CurrentEffortCostMultiplier. 1.0 = no effect.</summary>
    public static void AddEffortScaleProvider(string key, Func<ClimbingV2PawnLimb, float> provider)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        _effortProviders[key] = provider;
    }

    /// <summary>Remove a registered per-limb effort scale provider.</summary>
    public static void RemoveEffortScaleProvider(string key)
    {
        if (key != null) _effortProviders.Remove(key);
    }

    // ── Drain scales (direct, multiplies staminaConsumptionSpeed after UpdateStaminaState) ──

    private static readonly Dictionary<string, float> _globalDrainScales = new();
    private static readonly Dictionary<string, Func<ClimbingV2PawnLimb, float>> _drainProviders = new();

    /// <summary>Register (or replace) a global drain speed multiplier. Applied to every limb's
    /// _CurrentStaminaConsumptionSpeed after UpdateStaminaState computes it. Only scales
    /// positive (draining) values; gaining limbs are not affected. 1.0 = no effect.</summary>
    public static void AddDrainScale(string key, float scale)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        _globalDrainScales[key] = scale;
    }

    /// <summary>Remove a registered global drain scale.</summary>
    public static void RemoveDrainScale(string key)
    {
        if (key != null) _globalDrainScales.Remove(key);
    }

    /// <summary>Register (or replace) a per-limb drain speed scale provider. Callback receives the
    /// limb and returns a multiplier for that limb's drain rate. Only applied when the limb is
    /// draining (consumptionSpeed &gt; 0). 1.0 = no effect.</summary>
    public static void AddDrainScaleProvider(string key, Func<ClimbingV2PawnLimb, float> provider)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        _drainProviders[key] = provider;
    }

    /// <summary>Remove a registered per-limb drain scale provider.</summary>
    public static void RemoveDrainScaleProvider(string key)
    {
        if (key != null) _drainProviders.Remove(key);
    }

    // ── Global tweakables ─────────────────────────────────────────────────────────────

    /// <summary>The live ClimbingTweakablesPackage.Stamina for the local pawn.
    /// Fields: baseStamina, staminaGainSpeed, staminaLossSpeed, staminaLossCurve,
    /// criticalStaminaStateDuration, punishedCriticalStaminaStateDuration,
    /// effortExhaustionThreshold, handRestThreshold, footRestThreshold,
    /// handFreeEffortThreshold, footFreeEffortThreshold.
    /// Null if not in a session or the package isn't loaded.</summary>
    public static ClimbingTweakablesPackage.Stamina Package
        => Pawn.ClimbingPackage?.stamina;

    // ── Internal scale computation ────────────────────────────────────────────────────

    internal static bool HasEffortScales => _globalEffortScales.Count > 0 || _effortProviders.Count > 0;
    internal static bool HasDrainScales  => _globalDrainScales.Count > 0 || _drainProviders.Count > 0;

    internal static float ComputeEffortScale(ClimbingV2PawnLimb limb)
    {
        float scale = 1f;
        foreach (var kv in _globalEffortScales)
            scale *= kv.Value;
        foreach (var kv in _effortProviders)
            try { scale *= kv.Value(limb); } catch { }
        return scale;
    }

    internal static float ComputeDrainScale(ClimbingV2PawnLimb limb)
    {
        float scale = 1f;
        foreach (var kv in _globalDrainScales)
            scale *= kv.Value;
        foreach (var kv in _drainProviders)
            try { scale *= kv.Value(limb); } catch { }
        return scale;
    }

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Effort scale: postfix on CurrentEffortCostMultiplier getter.
        var effortTarget = typeof(ClimbingV2PawnLimb).GetProperty(
            nameof(ClimbingV2PawnLimb.CurrentEffortCostMultiplier))?.GetGetMethod();
        if (effortTarget != null)
            harmony.Patch(effortTarget, postfix: new HarmonyMethod(
                typeof(StaminaPatches).GetMethod(nameof(StaminaPatches.EffortCostPostfix))));

        // Drain scale: postfix on UpdateStaminaState to scale the computed consumption speed.
        var drainTarget = typeof(ClimbingV2PawnLimb).GetMethod("UpdateStaminaState",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        if (drainTarget != null)
            harmony.Patch(drainTarget, postfix: new HarmonyMethod(
                typeof(StaminaPatches).GetMethod(nameof(StaminaPatches.DrainScalePostfix))));
    }
}

internal static class StaminaPatches
{
    public static void EffortCostPostfix(ClimbingV2PawnLimb __instance, ref float __result)
    {
        if (!Stamina.HasEffortScales) return;
        float scale = Stamina.ComputeEffortScale(__instance);
        if (scale != 1f) __result *= scale;
    }

    public static void DrainScalePostfix(ClimbingV2PawnLimb __instance)
    {
        if (!Stamina.HasDrainScales) return;
        float speed = __instance.CurrentStaminaConsumptionSpeed;
        if (speed <= 0f) return;   // resting/neutral — don't touch the gain
        float scale = Stamina.ComputeDrainScale(__instance);
        if (scale != 1f) __instance.CurrentStaminaConsumptionSpeed = speed * scale;
    }
}
