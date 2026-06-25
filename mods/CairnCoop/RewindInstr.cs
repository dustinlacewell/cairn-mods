using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnCoop;

/// <summary>
/// Co-op Edelweiss revive correctness.
///
/// The resurrection runs the game's auto-rewind INSIDE the FallingModule, which enabled the ragdoll
/// when the fall began (FallingModule.OnModuleStart -> ClimbingPawnRagdollCairnIK.EnableRagdoll). The
/// climber only returns to IK/animation control when the ragdoll is disabled — the ONLY disable site is
/// FallingModule.OnModuleStop, reached solely by the rewind COMPLETING (ExitRewind -> the module swap).
///
/// The STUCK-HALFWAY failure breaks that completion: the auto-rewind coroutine dies mid-scrub on a
/// transient netplay throw (the relay drops the room during the rewind's frame hitches ->
/// Gamemode.IsLocalClientAuthoritative throws "Not in room"). It never reaches ExitRewind, so the module
/// never swaps: frozen, ragdoll still on. This file makes the revive deterministic regardless of netplay
/// timing:
///   1. NetplayAuthorityGuard stops the "Not in room" throw at its source (separate file).
///   2. If PerformAutoRewind throws anyway, the iterator is dead — force a clean ExitRewind instead
///      of letting the climber freeze (<see cref="RewindCoroutineInstr"/>).
///   3. On ExitRewind, disable the ragdoll if it is still enabled — exactly what OnModuleStop does — so
///      a stuck-halfway recovery still lands IK-driven (<see cref="RewindExitInstr"/>).
///   4. While a resurrection rewind is in flight (<see cref="CoopRevive.Active"/>), GameDriver pauses
///      its room/belay reconciliation so the mod never churns the room or fights the rewind.
///
/// NOT in scope here — the two OPEN co-op revive bugs (each has its own proven-mechanism file):
///   • Ground recovery (dumped at the bottom): re/systems/coop/BUG-ground-recovery-rewind-budget.md
///     (co-op export → readerLengthMs≈0 → resumable-scan finds nothing → scrub wedge). NOT a ragdoll/
///     tape issue.
///   • Reaching-limb jitter: re/systems/coop/BUG-reaching-limb-jitter.md. PROVEN field-level to be a
///     two-writer target↔bone swap on a Reaching limb — and the ragdoll is OFF (IsRagdoll==False) when
///     it jitters, so step 3 below is NOT its fix. The earlier "ragdoll-enabled → mesh-fights-physics"
///     explanation was falsified live and deleted.
/// </summary>
internal static class CoopRevive
{
    /// <summary>True while a RESURRECTION auto-rewind is in flight (EnterRewind(resurrection=true)
    /// until ExitRewind). GameDriver pauses its reconciliation in this window. Always cleared by
    /// <see cref="RewindExitInstr"/> (normal completion) or <see cref="RewindCoroutineInstr"/> (the
    /// coroutine-death fallback forces ExitRewind), so it can never stick.</summary>
    internal static volatile bool Active;

    /// <summary>Stand the mod belay down before the native rewind runs. Wired by Core to the driver's
    /// teardown; the patch invokes THIS instead of locating <c>Core.Instance.Driver</c> — no service
    /// locator, no patch→root dependency cycle. Null-safe: if unset, the stand-down is simply skipped.</summary>
    internal static Action StandDownBelay;

    /// <summary>Return the climber to clean IK/animation control: disable the ragdoll if it is still
    /// enabled. This is the same instant disable FallingModule.OnModuleStop performs
    /// (ClimbingPawnRagdollCairnIK.DisableRagdoll: isKinematic=true, ragdollEnabledTimer=-1).
    /// Idempotent — a no-op when the ragdoll is already off.</summary>
    internal static void ForceIkDriven(ClimbingV2PawnController controller)
    {
        try
        {
            var cairnIk = controller != null ? controller.ik : null;
            var ragdoll = cairnIk != null ? cairnIk.ragdoll : null;
            if (ragdoll != null && ragdoll.IsRagdoll)
            {
                ragdoll.DisableRagdoll();
                FallTelemetry.Log?.Invoke("revive: ragdoll still enabled after rewind — forced DisableRagdoll (clean on-wall)");
            }
        }
        catch (Exception e) { FallTelemetry.Log?.Invoke("revive: ForceIkDriven failed: " + e.Message); }
    }

    // The leaked-RAH-snap jitter fix is by TYPE in RahOnModuleStopGuard.StripRahHandlers, called from two
    // sites: the RAH.OnModuleStop prefix (when a RAH stops with a null co-op lifeline) and the ExitRewind
    // postfix below (once per revive). Two sites because the leaker is a STALE RAH instance whose own
    // OnModuleStop may never fire — the OnModuleStop prefix only catches it if SOME RAH on the same IK stops;
    // the ExitRewind sweep catches it deterministically per revive. The ExitRewind count is also a probe:
    // it tells us whether the leak exists at revive completion or forms later.

    /// <summary>DIAGNOSTIC: a one-line snapshot of a rewinding climber — pawn world position, current
    /// module, and how many roped partners are ALIVE right now (the partner-alive condition the second
    /// climber's revive is suspected to trip on). Logged at ENTER and EXIT so the two-instance log shows
    /// whether the second climber entered a resurrection rewind at all, whether a partner had already
    /// revived, and whether the rewind actually SCRUBBED (exit pos ≠ enter pos) or stood them up where
    /// they died (exit ≈ enter). All reads guarded — never throws out of native code.</summary>
    internal static string ReviveContext(RewindController rc)
    {
        string pos = "?", module = "?", partners = "?", scrub = "?", best = "?";
        // The recorder-tape state AT THE REWIND ATTEMPT — the exact figure CanRewind reads to decide
        // NotEnoughTape. Read THIS controller's OWN recorder (rc.pcs.PathRecorder), never the scene's
        // first PathRecorder: in co-op that is often a remote ghost's empty one (the old prs[0] read
        // produced phantom tape=0). `scope` marks whether rc is the local pawn — the StartRewindIfPossible
        // patch is cross-contaminated, firing for the remote ghost's RewindController too. The tape is a
        // rolling buffer FILLED by RewindRecorder.CaptureFrame (its ShouldCapture gate) and EMPTIED by
        // RewindRecorder.Clear on a recording restart — NOT by saves (SaveAndClear is a no-op override).
        string tapeRec = "tape=?", cstate = "?", scope = "?";
        try
        {
            var pr = rc != null && rc.pcs != null ? rc.pcs.PathRecorder : null;
            tapeRec = TapeProbe.Of(pr);
        }
        catch (Exception e) { tapeRec = "tape=?(" + e.Message + ")"; }
        try
        {
            var localPcs = MoSingleton<PawnManager>.Instance?.PawnControllerSwitcher;
            scope = (rc != null && rc.pcs != null && localPcs != null && rc.pcs.Pointer == localPcs.Pointer)
                ? "LOCAL" : "remote";
        }
        catch { }
        try { var nm = MoSingleton<NetplayManager>.Instance; cstate = nm != null ? ((int)nm.clientState).ToString() : "?"; }
        catch (Exception e) { cstate = "?(" + e.Message + ")"; }
        // The decisive fields for "restored without rewinding": ScrubPosition01 is where the rewind
        // reader currently sits; bestAutoRewindScrubPosition01 is the best RESUMABLE climb frame the
        // recorder marked (Nullable). PerformAutoRewind exits in place when that target has no value —
        // i.e. a climber who free-fell too far recorded no resumable frame → stands up where they died.
        try { scrub = rc.ScrubPosition01.ToString("0.###"); } catch { }
        try
        {
            var b = rc.bestAutoRewindScrubPosition01;
            best = b.HasValue ? b.Value.ToString("0.###") : "NONE";
        }
        catch (Exception e) { best = "?(" + e.Message + ")"; }
        try { pos = ((UnityEngine.Component)rc).transform.position.ToString("F1"); } catch { }
        try
        {
            var c = ((UnityEngine.Component)rc).GetComponent<ClimbingV2PawnController>();
            var m = c != null ? c.CurrentModule : null;
            module = m != null ? m.type.ToString() : "—";
        }
        catch { }
        try
        {
            var gm = MoSingleton<NetplayManager>.Instance?.Gamemode?.TryCast<SharedRopeGamemode>();
            if (gm != null)
            {
                var remotes = gm.GetRemotePlayers();
                int alive = 0, total = 0;
                for (int i = 0; i < remotes.Count; i++)
                {
                    var r = remotes[i];
                    if (r == null) continue;
                    total++;
                    if (r.IsHoldingFirmly
                        || r.PawnState == NetFrame.PawnStateType.Walking
                        || r.PawnState == NetFrame.PawnStateType.Climbing)
                        alive++;
                }
                partners = $"{alive}/{total} alive";
            }
        }
        catch (Exception e) { partners = "? (" + e.Message + ")"; }
        return $"{scope} pos={pos} module={module} scrub01={scrub} bestTarget={best} partners={partners} "
            + $"{tapeRec} cstate={cstate}";
    }

    /// <summary>The local rewind controller currently rewinding — for the coroutine-death fallback,
    /// where we don't have the coroutine's owning instance to hand.</summary>
    internal static RewindController CurrentlyRewinding()
    {
        try
        {
            foreach (var rc in UnityEngine.Object.FindObjectsOfType<RewindController>())
                if (rc != null && rc.IsRewinding)
                    return rc;
        }
        catch (Exception) { }
        return null;
    }
}

/// <summary>
/// EnterRewind(resurrection): stand the mod belay down BEFORE the native rewind runs (so it never
/// finds our anchor attached and fights it / frees the climber halfway), and open the revive window
/// so the driver stops reconciling.
/// </summary>
[HarmonyPatch(typeof(RewindController), nameof(RewindController.EnterRewind))]
internal static class RewindEnterInstr
{
    private static void Prefix(bool resurrection, RewindController __instance)
    {
        if (resurrection)
        {
            CoopRevive.Active = true;
            FallTelemetry.Log?.Invoke($"revive: ENTER resurrection — {CoopRevive.ReviveContext(__instance)}");
        }
        try { CoopRevive.StandDownBelay?.Invoke(); }
        catch (Exception e) { FallTelemetry.Log?.Invoke("revive: belay stand-down failed: " + e.Message); }
    }
}

/// <summary>
/// DIAGNOSTIC: capture the edelweiss-trigger decision. The joiner's death never reaches
/// <see cref="RewindEnterInstr"/> (no ENTER resurrection) — it takes the game's checkpoint/Spawner
/// fallback and stands up at the bottom. That fallback runs precisely when StartRewindIfPossible's
/// internal CanRewind() refuses. So log every rewind ATTEMPT (StartRewindIfPossible — only fires on a
/// real death/manual trigger, never per-frame), and on a refusal re-read CanRewind for the exact
/// <see cref="RewindController.RefusedRewindReason"/> (Netplay / NotClimbing / CurrentModuleForbidsIt /
/// NotEnoughTape / …) the game computed. The host should log an attempt that succeeds (→ ENTER); the
/// joiner should log an attempt that is REFUSED with the deciding reason — or NO attempt at all, which
/// would prove the gate is upstream in the death/respawn trigger. __0/__1/__2 are
/// bypassAssistMode/automaticRewind/resurrection (indexed injection — robust to param-name drift).
/// </summary>
[HarmonyPatch(typeof(RewindController), nameof(RewindController.StartRewindIfPossible))]
internal static class RewindAttemptInstr
{
    private static void Prefix(RewindController __instance, bool __0, bool __1, bool __2)
    {
        FallTelemetry.Log?.Invoke($"revive: StartRewindIfPossible(bypass={__0} auto={__1} res={__2}) — "
            + CoopRevive.ReviveContext(__instance));
    }

    private static void Postfix(RewindController __instance, bool __result, bool __0)
    {
        if (__result) return; // proceeded to EnterRewind — RewindEnterInstr logs the ENTER
        try
        {
            RewindController.RefusedRewindReason reason;
            __instance.CanRewind(out reason, __0);
            FallTelemetry.Log?.Invoke($"revive: rewind REFUSED — reason={reason}");
        }
        catch (Exception e) { FallTelemetry.Log?.Invoke("revive: CanRewind reason read failed: " + e.Message); }
    }
}

/// <summary>
/// ExitRewind: the revive landed. Guarantee the climber is IK-driven (ragdoll off) and close the
/// revive window. Runs for the resurrection path; harmless for a manual rewind (the ragdoll is
/// already off there, so <see cref="CoopRevive.ForceIkDriven"/> is a no-op). RewindController and
/// ClimbingV2PawnController live on the same GameObject (MC_Aava root), so GetComponent resolves the
/// local climber directly.
/// </summary>
[HarmonyPatch(typeof(RewindController), nameof(RewindController.ExitRewind))]
internal static class RewindExitInstr
{
    private static void Postfix(RewindController __instance)
    {
        bool wasResurrection = CoopRevive.Active;
        try
        {
            if (wasResurrection)
                FallTelemetry.Log?.Invoke($"revive: EXIT — {CoopRevive.ReviveContext(__instance)}");
            var controller = ((UnityEngine.Component)__instance).GetComponent<ClimbingV2PawnController>();
            CoopRevive.ForceIkDriven(controller);
            // Deterministic per-revive sweep for the leaked RAH snap-to-bone handler (whole-climber jitter).
            // The RahOnModuleStopGuard prefix clears it when a RAH actually stops, but a STALE RAH instance
            // whose own OnModuleStop never fires would persist; ExitRewind runs once per revive on the revived
            // climber, so a by-type strip here catches the leak regardless. Logs the count so we learn whether
            // the leak already exists at revive completion (count>0) or forms later (count==0 every revive).
            int stripped = RahOnModuleStopGuard.StripRahHandlers(
                controller != null ? controller.ik : null, "ExitRewind (revive complete)");
            if (wasResurrection)
                FallTelemetry.Log?.Invoke($"revive: ExitRewind RAH-strip removed {stripped} leaked handler(s)");
        }
        catch (Exception e) { FallTelemetry.Log?.Invoke("revive: ExitRewind postfix failed: " + e.Message); }
        finally { CoopRevive.Active = false; }
    }
}

/// <summary>
/// Patches the compiler-generated <c>PerformAutoRewind</c> coroutine's MoveNext with a finalizer.
/// A C# iterator that throws is DEAD — it can never resume — so an unhandled throw mid-scrub would
/// freeze the climber halfway with the ragdoll still on. Instead we force a clean ExitRewind (which
/// reinjects the current recorded frame, swaps the module, and — via <see cref="RewindExitInstr"/> —
/// disables the ragdoll), then swallow the throw. Patched manually because the nested type's name is
/// not a valid C# identifier; <see cref="Install"/> is called once after Harmony PatchAll.
/// </summary>
internal static class RewindCoroutineInstr
{
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        try
        {
            System.Type coro = null;
            foreach (var nt in typeof(RewindController).GetNestedTypes(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (nt.Name.Contains("PerformAutoRewind")) { coro = nt; break; }
            if (coro == null)
            {
                FallTelemetry.Log?.Invoke("revive: PerformAutoRewind coroutine type NOT FOUND — uninstrumented");
                return;
            }
            var moveNext = AccessTools.Method(coro, "MoveNext");
            if (moveNext == null)
            {
                FallTelemetry.Log?.Invoke("revive: MoveNext NOT FOUND on " + coro.Name);
                return;
            }
            harmony.Patch(moveNext, finalizer: new HarmonyMethod(typeof(RewindCoroutineInstr), nameof(MoveNextFinalizer)));
            FallTelemetry.Log?.Invoke("revive: installed PerformAutoRewind crash-recovery finalizer");
        }
        catch (Exception e)
        {
            FallTelemetry.Log?.Invoke("revive: coroutine patch failed: " + e.Message);
        }
    }

    private static Exception MoveNextFinalizer(Exception __exception)
    {
        if (__exception == null)
            return null;
        FallTelemetry.Log?.Invoke("revive: PerformAutoRewind threw " + __exception.GetType().Name
            + ": " + __exception.Message + " — forcing clean exit");
        try
        {
            var rc = CoopRevive.CurrentlyRewinding();
            if (rc != null)
                rc.ExitRewind(true); // RewindExitInstr.Postfix then forces IK-driven + clears the window
            else
                CoopRevive.Active = false;
        }
        catch (Exception e)
        {
            FallTelemetry.Log?.Invoke("revive: forced ExitRewind failed: " + e.Message);
            CoopRevive.Active = false;
        }
        return null; // swallow: the iterator is already dead; freezing the climber helps nobody
    }
}
