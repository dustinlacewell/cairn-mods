using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;

namespace CairnCoop;

/// <summary>
/// The hang-stamina load: while a roped partner is weighting the rope (hanging / caught / abseiling /
/// dead on the line), the local BELAYING climber's limbs drain stamina FASTER, so they tire naturally
/// into Aava's critical-stamina panic state (the game's own blur/grayscale/vignette) and, if the hang
/// is heavy or sustained enough, drop on the vanilla exhaustion path. The team-fall is emergent — the
/// weight wears you down — not an instant scripted cascade.
///
/// Lever (decompile-verified, see re/systems/climber/fatigue-system.md + the UpdateStaminaState/UpdateStamina/
/// UpdateCriticalStamina decompiles): ClimbingV2PawnLimb.UpdateStaminaState recomputes each limb's
/// CurrentStaminaConsumptionSpeed (@0x3B0) every frame from effort, then UpdateStamina drains
/// currentStamina by deltaTime * that speed. Both run (all limbs StaminaState, then all Stamina) inside
/// one ClimbingV2PawnController.UpdateLimbsStamina tick, so a POSTFIX on UpdateStaminaState that ADDS to
/// the consumption speed is consumed the same frame — no lag. A positive add keeps the speed > 0, which
/// satisfies UpdateCriticalStamina's "still draining" gate, so the faster drain accumulates the
/// criticalStaminaTimer → panic → eventual force-drop, all on the game's own path.
///
/// The add is expressed scale-independently as (rate × this limb's CurrentMaxStamina) stamina-units per
/// second, where rate = LoadPerSecond accumulates k per hanging partner. So one hanger empties a limb in
/// ~1/k seconds (critical/panic hits sooner). The CurrentStaminaConsumptionSpeed setter is private, so
/// we write the field at offset 0x3B0 directly via the IL2CPP offset poke (the mod's established idiom,
/// cf. GameDriver's field writes); UpdateStaminaState overwrites it every frame, so the add is naturally
/// transient and re-applied each frame by the postfix.
///
/// Mirrors the mod's static-seam + static-[HarmonyPatch] pattern (cf. RopeLengthGuard,
/// FreeSoloRecoveryGate); auto-registered by Core's PatchAll.
/// </summary>
internal static class HangStaminaSeam
{
    /// <summary>Pointer of the LOCAL belaying climber's controller. The postfix fires for EVERY limb in
    /// the scene (local + remote ghosts); only limbs under this controller get the load. Pointer, not a
    /// managed ref, for a cheap hot-path identity compare (cf. RopeLengthGuard.OwnedRope).</summary>
    internal static IntPtr LocalController = IntPtr.Zero;

    /// <summary>Extra stamina-consumption RATE per second per unit of the limb's CurrentMaxStamina,
    /// summed across hanging partners (= k × hangerCount). 0 == no load. The postfix multiplies this by
    /// each local limb's CurrentMaxStamina to get the stamina-units/second it adds to the limb's
    /// consumption speed.</summary>
    internal static float LoadPerSecond;

    /// <summary>Per-hanger drain coefficient: a single hanging partner adds k × CurrentMaxStamina/second
    /// of drain, emptying a full limb in ~1/k s (≈6.7 s at 0.15) and reaching critical/panic sooner —
    /// the "wears you down over a few seconds" feel. Public + mutable so it can be tuned live from the
    /// CairnDevTools console (eval CairnCoop.HangStaminaSeam.PerHangerLoad = …) without a rebuild.</summary>
    internal static float PerHangerLoad = 0.15f;

    /// <summary>The single reset point — no load. Called on every off-path (roped off, anchor torn down,
    /// local controller unresolved, session stopped) so a solo/ended player can never carry a stale
    /// drain.</summary>
    internal static void Clear()
    {
        LocalController = IntPtr.Zero;
        LoadPerSecond = 0f;
    }
}

/// <summary>Postfix on each limb's per-frame stamina-state recompute: adds the belay load to the local
/// carrier's limbs' consumption speed so they drain faster.</summary>
[HarmonyPatch(typeof(ClimbingV2PawnLimb), nameof(ClimbingV2PawnLimb.UpdateStaminaState))]
internal static class LimbBelayLoad
{
    // CurrentStaminaConsumptionSpeed lives at offset 0x3B0 on ClimbingV2PawnLimb; its setter is private,
    // so write the field directly (the mod's IL2CPP-offset-poke idiom). Offset resolved once.
    private const int ConsumptionSpeedOffset = 0x3B0;

    private static unsafe void Postfix(ClimbingV2PawnLimb __instance)
    {
        if (HangStaminaSeam.LocalController == IntPtr.Zero || HangStaminaSeam.LoadPerSecond <= 0f)
            return;
        // Local-only: scope the load to the belaying climber's four limbs (the method is invoked for
        // every limb in the scene, including remote ghosts).
        var owner = __instance.pawnController;
        if (owner == null || owner.Pointer != HangStaminaSeam.LocalController)
            return;
        try
        {
            // Add a per-second rate (UpdateStamina multiplies by deltaTime), scaled to this limb's max
            // so the drain is meaningful regardless of the absolute stamina scale. Positive → keeps the
            // limb "draining" for the critical-state gate.
            float add = HangStaminaSeam.LoadPerSecond * __instance.CurrentMaxStamina;
            float* speed = (float*)((nint)__instance.Pointer + ConsumptionSpeedOffset);
            *speed += add;
        }
        catch (Exception)
        {
            // Never let a stamina-field write throw out of native UpdateStaminaState.
        }
    }
}
