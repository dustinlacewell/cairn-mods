using System;
using HarmonyLib;
using Il2Cpp;

namespace CairnCoop;

// Free-solo partner anchoring, the native way (history of this file: we first tried
// flipping Harness.IsSecured when a rope neighbor could take the load, but that promotes
// the fall into SecuredFalling MID-FALL — a transition vanilla never exercises. It
// reentrantly stops the module during its own OnModuleStart (which resets reason and made
// the original throw "Invalid fall reason"), and the switcher settles back on unsecured
// anyway because SecuredFalling's swing physics need an anchor POINT on the rope. The
// working approach lives in GameDriver.TickPartnerAnchor: one local, never-replicated
// piton clipped onto the shared rope and parented to the partner's harness — the partner
// IS the piton. IsSecured counts a genuine anchor and falls take the vanilla secured path.)

internal static class FallTelemetry
{
    internal static Action<string> Log;
}

/// <summary>
/// The ONLY automatic exit from SecuredFalling (hanging after a caught fall) is
/// ClimbingV2PawnController.EarlyCheckCurrentModule (0x1828F5A30) transitioning to
/// RappelAscendRopeHangIdle — and it is gated on !InFreeSolo(). On free-solo saves the
/// caught climber therefore hangs forever with dead input. But InFreeSolo() feeds far
/// more than recovery — the free-solo climb timer, rest/stamina rules, capture/pose
/// paths — and overriding it for the whole roped session stopped the game clock and
/// glitched remote pose sync. So the override is scoped to the only window the recovery
/// driver needs it: while the local pawn is actually in a secured hang (bracketed by the
/// fall-module telemetry below), plus a short tail for the hang-idle module to settle.
/// </summary>
[HarmonyPatch(typeof(ClimbingV2PawnController), nameof(ClimbingV2PawnController.InFreeSolo))]
internal static class FreeSoloRecoveryGate
{
    internal static bool PartnerAnchorActive;
    internal static bool InSecuredHang;
    internal static DateTime SecuredHangEndedAt = DateTime.MinValue;

    private static void Postfix(ref bool __result)
    {
        if (!PartnerAnchorActive || !__result)
            return;
        if (InSecuredHang || DateTime.UtcNow - SecuredHangEndedAt < TimeSpan.FromSeconds(10))
            __result = false;
    }
}

/// <summary>Log which falling module starts/stops — the catch is invisible without it.</summary>
[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleFalling), nameof(ClimbingV2PawnControllerModuleFalling.OnModuleStart))]
internal static class UnsecuredFallStartTelemetry
{
    private static void Postfix(ClimbingV2PawnControllerModuleFalling __instance)
        => FallTelemetry.Log?.Invoke($"fall module: UNSECURED Falling started (reason {__instance.reason}) {TapeProbe.LocalStr()}");
}

[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleSecuredFalling), nameof(ClimbingV2PawnControllerModuleSecuredFalling.OnModuleStart))]
internal static class SecuredFallStartTelemetry
{
    // SecuredFalling.OnModuleStart restarts itself if already active: the reentrant
    // OnModuleStop resets reason to Invalid mid-start, and the start's continuation
    // then throws on its `reason == Invalid` sanity check. Track in-flight starts so
    // the stop guard below can restore the reason.
    internal static int StartDepth;
    internal static FallReason InFlightReason;

    private static void Prefix(ClimbingV2PawnControllerModuleSecuredFalling __instance)
    {
        var reason = __instance.reason;
        if (reason == FallReason.Invalid)
        {
            __instance.reason = reason = FallReason.BadHolds;
            FallTelemetry.Log?.Invoke("fall telemetry: stamped fall reason BadHolds (was Invalid)");
        }
        StartDepth++;
        InFlightReason = reason;
    }

    private static void Postfix(ClimbingV2PawnControllerModuleSecuredFalling __instance)
    {
        FreeSoloRecoveryGate.InSecuredHang = true;
        FallTelemetry.Log?.Invoke($"fall module: SECURED falling started (reason {__instance.reason}) {TapeProbe.LocalStr()}");
    }

    private static Exception Finalizer(Exception __exception,
        ClimbingV2PawnControllerModuleSecuredFalling __instance)
    {
        StartDepth--;
        if (__exception != null)
            FallTelemetry.Log?.Invoke(
                $"fall module: SECURED OnModuleStart THREW (reason {__instance.reason}): {__exception.Message}");
        return __exception;
    }
}

[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleSecuredFalling), nameof(ClimbingV2PawnControllerModuleSecuredFalling.OnModuleStop))]
internal static class SecuredFallStopTelemetry
{
    private static void Postfix(ClimbingV2PawnControllerModuleSecuredFalling __instance)
    {
        if (SecuredFallStartTelemetry.StartDepth > 0 && __instance.reason == FallReason.Invalid)
        {
            __instance.reason = SecuredFallStartTelemetry.InFlightReason;
            FallTelemetry.Log?.Invoke(
                $"fall telemetry: restored reason {__instance.reason} after reentrant stop during start");
        }
        FreeSoloRecoveryGate.InSecuredHang = false;
        FreeSoloRecoveryGate.SecuredHangEndedAt = DateTime.UtcNow;
        FallTelemetry.Log?.Invoke($"fall module: SECURED falling stopped {TapeProbe.LocalStr()}");
    }
}
