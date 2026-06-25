using System;
using HarmonyLib;
using Il2Cpp;

namespace CairnCoop;

/// <summary>
/// Completes the half-finished native teardown when a co-op climber leaves a
/// RappelAscendRopeHangIdle (RAH) rope-hang — the root fix for the whole-climber post-revive jitter.
///
/// THE NATIVE ASYMMETRY (traced from re/native/.../RappelAscendRopeHangIdle/OnModuleStart.c +
/// OnModuleStop.c — see re/systems/coop/JITTER-TASK.md):
///   • <c>OnModuleStart</c> SUBSCRIBES a per-frame handler to the climber's
///     <c>ClimbingPawnCairnIK.OnLastPostIKSolve</c> (IK+0xa0) that SNAPS each limb's IK target onto its
///     bone (freeze-while-hanging). That subscribe is gated ONLY on pawnController + ik being non-null —
///     the lifeline is irrelevant to it.
///   • <c>OnModuleStop</c>'s matching <c>Delegate.Remove</c> (the unsubscribe) sits 13 gates deep, behind
///     <c>harness.get_Lifeline() != null</c> (OnModuleStop.c:44). There is NO path to the unsubscribe that
///     doesn't require a non-null securing lifeline.
///
/// So subscribe is lifeline-INDEPENDENT but unsubscribe is lifeline-DEPENDENT. In co-op the mod hangs the
/// climber on a BESPOKE rope (the climbot's <c>securedLifeline</c> + the mod's <c>PersonalLifeline</c>
/// piton); the pawn's own <c>Harness.lifeline</c> (field 0x50) is NEVER populated. With <c>clientState</c>
/// held at REGISTERED(3) so <c>IsInRoom</c> is false, <c>get_Lifeline()</c> returns that null 0x50 field
/// directly. RAH therefore subscribes the snap handler but can NEVER unsubscribe it — OnModuleStop NREs on
/// the null lifeline (re/systems/coop/rah-onmodulestop-nre.md) before reaching the Remove. The handler
/// leaks 100% deterministically; once the climber is climbing again it fights every reach, oscillating the
/// IK target target(f)=bone(f-1) on EVERY limb — the whole-climber "all reaching limbs jitter" bug
/// (root cause PROVEN live: removing exactly this subscriber stops the jitter, restoring it brings it back).
///
/// THE FIX — supply native's missing unsubscribe, by TYPE:
///   • <see cref="Prefix"/> runs BEFORE the native body. When (and only when) the securing lifeline is null
///     — the exact condition under which native's teardown CANNOT reach its own Remove — it strips every
///     RAH-typed handler from the IK's OnLastPostIKSolve. Crucially by TYPE, not by the stopping instance:
///     the leaked handler belongs to a STALE RAH instance (a prior hang's module that never unsubscribed),
///     a DIFFERENT object than the one whose OnModuleStop is firing — matching the stopping <c>__instance</c>
///     spared the leaker and left the jitter (live-proven by the adversary). When the lifeline IS present,
///     it does nothing: native unsubscribes its own and we must not interfere.
///   • <see cref="RewindExitInstr"/> (RewindInstr.cs) runs the SAME by-type strip once per revive — the
///     deterministic catch for a stale RAH whose own OnModuleStop never fires.
///   • <see cref="Finalizer"/> still swallows the (now handler-free) null-lifeline NRE so the outgoing
///     <c>TransitionToModule(Falling)</c> always lands and the faller doesn't wedge in the hang.
/// </summary>
[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle),
    nameof(ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle.OnModuleStop))]
internal static class RahOnModuleStopGuard
{
    /// <summary>Before native teardown: if the securing lifeline is null — so native's lifeline-gated
    /// <c>Delegate.Remove</c> is unreachable — strip EVERY RAH-typed handler from this climber's IK
    /// <c>OnLastPostIKSolve</c>, completing the asymmetric teardown. By TYPE, not by this stopping instance:
    /// the leaked snap handler belongs to a STALE RAH instance (a prior hang's module that never unsubscribed),
    /// NOT the one whose OnModuleStop is firing now — so matching the stopping <c>__instance</c> spares the
    /// leaker (live-proven: that left the jitter). One climber has one IK / one OnLastPostIKSolve, and no RAH
    /// handler should survive any RAH stop in co-op, so removing all RAH-typed subscribers here clears both the
    /// current module's and any stale leaked one's. A no-op when a lifeline is present (native handles its own)
    /// and when nothing RAH-typed is subscribed (idempotent).</summary>
    private static void Prefix(ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle __instance)
    {
        if (NativeTeardownWillSkipUnsubscribe(__instance))
            StripRahHandlers(__instance?.pawnController?.ik, "RAH.OnModuleStop (null-lifeline co-op drop)");
    }

    /// <summary>Swallow the null-lifeline NRE the native body still throws on this path (its teardown tail
    /// short-circuits on the null securing lifeline). The leaked-subscription fix is the Prefix's job; this
    /// only guarantees the outgoing transition lands. RAH.OnModuleStop does NOT throw on a vanilla exit
    /// (those keep the rope alive), so this only ever fires on the mod's forced co-op drop.</summary>
    private static Exception Finalizer(Exception __exception)
    {
        if (__exception != null)
            FallTelemetry.Log?.Invoke(
                "belay: swallowed RAH.OnModuleStop null-lifeline NRE (rah-onmodulestop-nre) — transition completes");
        return null;
    }

    /// <summary>True when the native teardown cannot reach its own IK unsubscribe — i.e. the securing
    /// lifeline (<c>harness.get_Lifeline()</c>, the gate at OnModuleStop.c:44) is null. That is precisely the
    /// co-op bespoke-rope case where the pawn's <c>Harness.lifeline</c> (0x50) was never populated. Null-safe:
    /// if we can't resolve the chain, assume native will fail too and complete the unsubscribe ourselves.</summary>
    private static bool NativeTeardownWillSkipUnsubscribe(
        ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle module)
    {
        try
        {
            var harness = module?.pawnController?.harness;
            return harness == null || harness.Lifeline == null;
        }
        catch
        {
            return true; // can't tell → native is at least as likely to NRE; supply the unsubscribe
        }
    }

    /// <summary>Strip every <c>RappelAscendRopeHangIdle</c>-typed subscriber from the IK's
    /// <c>OnLastPostIKSolve</c> multicast — the snap-to-bone handler that must never outlive a RAH hang.
    /// Matches by il2cpp TYPE (catches a stale leaked instance, not just the currently-stopping one), walks
    /// the invocation list, drops the RAH entries, recombines the rest, writes back. Returns the number
    /// removed. Idempotent and null-safe; a no-op (returns 0) if nothing RAH-typed is subscribed.
    /// <paramref name="reason"/> labels the call site in the log when something was actually removed.</summary>
    internal static int StripRahHandlers(ClimbingPawnCairnIK ik, string reason)
    {
        try
        {
            var current = ik?.OnLastPostIKSolve;
            if (ik == null || current == null) return 0;
            var list = current.GetInvocationList();
            Il2CppSystem.Action rebuilt = null;
            int removed = 0;
            foreach (var d in list)
            {
                var target = d.Target; // Il2CppSystem.Object — the subscriber instance
                bool isRah = target != null
                    && target.GetIl2CppType().Name
                       == nameof(ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle);
                if (isRah) { removed++; continue; }
                rebuilt = (Il2CppSystem.Action)(object)Il2CppSystem.Delegate.Combine(
                    rebuilt, d.Cast<Il2CppSystem.Delegate>());
            }
            if (removed > 0)
            {
                ik.OnLastPostIKSolve = rebuilt;
                FallTelemetry.Log?.Invoke(
                    $"belay: stripped {removed} RAH.OnLastPostIKSolve handler(s) at {reason} (jitter fix)");
            }
            return removed;
        }
        catch (Exception e)
        {
            FallTelemetry.Log?.Invoke("belay: RAH OnLastPostIKSolve strip failed: " + e.Message);
            return 0;
        }
    }
}
