using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;

namespace CairnCoop;

/// <summary>
/// Fixes the co-op edelweiss GROUND-RECOVERY: a climber sometimes revives at the mountain BASE instead of
/// rewinding up the wall (~20%). PROVEN mechanism (binary + live managed stacks, re/systems/coop/
/// BUG-ground-recovery-rewind-budget.md):
///
/// At a co-op death a standalone <c>TimeManager.OnTimeFastForwarded</c> fires. Its subscriber
/// <c>RewindRecorder.Instance_OnTimeFastForwarded</c> — when <c>hasLoadedData==false</c> (always true for a
/// fresh free-solo co-op climb, no save) — dispatches <c>RewindRecorder.Clear</c>, which zeroes the rewind
/// tape (totalSecondsInCurrentRecord @0x88) AND re-inits the LiveRecord frame buffer. The edelweiss revive
/// then calls <c>RewindController.StartRewindIfPossible → CanRewind</c>, reads tape=0, and refuses with
/// <c>NotEnoughTape</c> → DeathPlane → checkpoint → base.
///
/// It is a RACE against ONE producer — the death-window time-skip (SpendTime) that raises
/// <c>OnTimeFastForwarded</c>. The rewind machinery itself NEVER raises it or calls Clear (verified by reading
/// the whole rewind call graph — MoveNext/EnterRewind/Scrub/ExitRewind/CleanupRewind touch the recorder tape
/// nowhere; EnterRewind EXPORTS the tape into its own puppet and scrubs THAT). So every
/// <c>OnTimeFastForwarded→Clear</c> on the local recorder is the destructive standalone skip; the rewind is a
/// pure bystander that survives ONLY if its EnterRewind export already happened before the Clear lands.
///
/// TWO failure modes, same root, both fixed here:
///   • GROUND-RECOVERY (~20%): the standalone skip fires BEFORE StartRewindIfPossible reads the tape → tape=0
///     → <c>NotEnoughTape</c> → DeathPlane → base. (The climber that lost the race; death/accept ~1.5 s late.)
///   • SECOND-REVIVE (co-op, both fall): the SAME race, a second instance of it. The second climber's
///     standalone skip lands in its fall→rewind window and zeroes the RECORDER's tape (<c>field_0x88</c>),
///     which <c>PathRecorder.GetRewindAvailableLength</c> reads and <c>CanRewind</c> gates on → its revive
///     refuses (<c>NotEnoughTape</c>) → dumped dead at the mountain base. (The first climber wins the race:
///     its EnterRewind export already happened.) NOTE: this is NOT a scrub-overshoot — the scrub reads the
///     CONTROLLER's <c>readerLengthMs</c> (set from the exported reader in Parse), a different object that
///     Clear never touches; the alarming <c>scrub01=-23.9</c> in the logs is the FIRST (succeeding) climber's
///     normal auto-rewind EXIT value, a red herring. (re/systems/coop/BUG-ground-recovery-rewind-budget.md.)
///
/// THE FIX (the differential, not a symptom patch): suppress the standalone Clear on the LOCAL climber's
/// recorder across the WHOLE fall→revive→scrub window, so the recorded tape survives for both the revive's
/// CanRewind gate AND the in-flight scrub. Restoring the scalar tape length is NOT enough — Clear also wipes
/// the LiveRecord frame buffer — so we skip the whole <c>Instance_OnTimeFastForwarded</c> (its ONLY effect is
/// that Clear; validated). We do NOT touch:
///   • remote ghosts' recorders — scoped by pointer to the local PathRecorder's rewindRecorder;
///   • the other OnTimeFastForwarded subscribers (NightDayCycle / netplay Gamemode / PhotoMode) — Harmony
///     patches only this one subscriber method.
///
/// Window: open while the LOCAL climber is FALLING (the resurrection-eligible state the standalone skip lands
/// in, before the revive reads the tape) OR a rewind is IN FLIGHT (the scrub the mid-rewind skip would
/// destroy). EARLIER than CoopRevive.Active (which only opens at EnterRewind), so it keys off the fall+rewind,
/// not the rewind-enter. (The earlier build let the in-rewind Clear through on a now-disproven "the rewind
/// consumes its own tape" assumption — that was the second-revive bug.)
///
/// SAFETY caveat (open): the exact game event that raises the standalone death-window OnTimeFastForwarded is
/// not yet pinned (it is NOT the resurrection animation — that advances stats in real time — nor an obvious
/// bivouac). The fast-forward IS a real game-time advance, but the preserved tape is a CONTIGUOUS pre-skip
/// path (capture is gated off during the skip, so no straddling frames exist), so the rewind does not scrub
/// across a discontinuity. The companion <c>TimeSkipTrace</c> logs the SpendTime caller to confirm the skip
/// is incidental; if it ever proves to be a legitimately-needed wipe, narrow the window further.
/// </summary>
[HarmonyPatch(typeof(Il2CppTheGameBakers.Cairn.Ghost.RewindRecorder),
    nameof(Il2CppTheGameBakers.Cairn.Ghost.RewindRecorder.Instance_OnTimeFastForwarded))]
internal static class TapeGuard
{
    internal static Action<string> Log;

    /// <summary>Skip the time-fast-forward tape Clear for the LOCAL climber's recorder while it is in the
    /// fall→revive window and no rewind is running yet. Returns false to suppress the original method (whose
    /// sole effect is the Clear). Any failure → run the original (fail open: never starve the game's own
    /// behaviour on a bad read).</summary>
    private static bool Prefix(Il2CppTheGameBakers.Cairn.Ghost.RewindRecorder __instance)
    {
        try
        {
            var pm = MoSingleton<PawnManager>.Instance;
            var pcs = pm != null ? pm.PawnControllerSwitcher : null;
            if (pcs == null)
                return true; // can't resolve local — let the game do its thing

            // Scope: only the LOCAL climber's rewind recorder, never a remote ghost's. rewindRecorder is a
            // private field @0x80 on PathRecorder — read its pointer directly (the mod's offset-read idiom).
            var pr = pcs.PathRecorder;
            if (pr == null)
                return true;
            IntPtr localRrPtr = Marshal.ReadIntPtr((IntPtr)((long)pr.Pointer + 0x80));
            if (localRrPtr == IntPtr.Zero || __instance.Pointer != localRrPtr)
                return true;

            // Protect the tape across the WHOLE fall→revive→scrub window — INCLUDING while a rewind runs.
            // The rewind machinery NEVER calls Clear / raises OnTimeFastForwarded itself: verified by reading
            // every body in the rewind call graph (PerformAutoRewind.MoveNext, EnterRewind, Scrub, ExitRewind,
            // CleanupRewind) — none touch the recorder tape; EnterRewind EXPORTS the tape into its own puppet
            // and the scrub runs against that puppet, not the live recorder. So there is only ONE producer of
            // OnTimeFastForwarded (the death-window SpendTime time-skip), and the rewind is a pure bystander —
            // EVERY OnTimeFastForwarded→Clear that lands on the local recorder is the destructive standalone.
            //
            // The earlier `IsRewinding → return true` bail (which let the in-rewind Clear "proceed as the
            // rewind consuming its own tape") rested on a now-disproven assumption and was a real bug: when
            // both co-op climbers fall+revive, the SECOND climber's standalone Clear races into its fall→rewind
            // window and zeroes the RECORDER tape (field_0x88), which PathRecorder.GetRewindAvailableLength
            // reads and CanRewind gates on → NotEnoughTape → dumped dead at the base (the same race as
            // ground-recovery, a second instance of it). The FIRST climber wins the race (its EnterRewind
            // export already happened). So we suppress the local Clear whenever the climber is falling OR a
            // rewind is in flight; the window closes when inRewind clears in CleanupRewind and the climber is
            // no longer falling. Safe: the rewind scrubs its exported puppet, never the live recorder tape, so
            // preserving that tape through the rewind is a no-op for the working climber (adversary-verified).
            var rc = pcs.Rewind;
            var ctrl = pm.ClimbingPawnController;
            bool falling = ctrl != null && ctrl.IsFalling;
            bool rewinding = rc != null && rc.IsRewinding;
            if (!falling && !rewinding)
                return true; // outside fall→revive→scrub: a time-skip Clear is a normal bivouac/wait wipe — leave it

            Log?.Invoke("tapeGuard: suppressed standalone OnTimeFastForwarded Clear on LOCAL recorder "
                + $"(falling={falling} rewinding={rewinding}) — preserving rewind tape through the revive+scrub");
            return false; // skip the Clear — preserve the tape
        }
        catch (Exception e)
        {
            Log?.Invoke("tapeGuard: prefix failed (running original): " + e.Message);
            return true;
        }
    }
}
