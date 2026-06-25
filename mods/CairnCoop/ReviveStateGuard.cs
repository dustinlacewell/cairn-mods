using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnCoop;

/// <summary>
/// FIX for the co-op revive desync (re/systems/coop/BUG-invalid-broadcast-during-rewind.md).
///
/// During a death-driven edelweiss auto-rewind, the reviving climber's PawnControllerSwitcher.mode is
/// <c>None</c> for the WHOLE rewind (the RewindController owns the pawn while it scrubs back up the wall).
/// <c>NetplayPawnCapture.GetPlayerPawnState</c> returns <c>Invalid</c> for any mode ∉ {Walking, Climbing}
/// (decomp GetPlayerPawnState.c: <c>mode==2 ? Climbing : Invalid</c>), so the climber broadcasts
/// <c>Invalid</c> to its partner every frame for ~20 s. The partner's NetplayRemotePlayer ghost receives
/// Invalid and FREEZES at the pre-rewind (death) position instead of tracking the climber as it scrubs up —
/// the desync the user observed. (Single-player has no partner, so the base game never exposed this; the mod
/// drives the shipped-but-disabled netplay, running this capture path in a scenario the shippers never hit.)
///
/// THE FIX (purely additive, co-op-only, tightly gated): a postfix on GetPlayerPawnState that, when the
/// result is <c>Invalid</c> AND the local pawn is in an active auto-rewind (RewindController.IsAutoRewinding
/// = inRewind &amp;&amp; automaticRewind), substitutes <c>Climbing</c> — the state the climber will be in when
/// the rewind finishes (an edelweiss auto-rewind always restores the climber onto the wall). So the partner
/// sees the ghost climbing and tracking the rewound position the net frame already carries, instead of a
/// frozen Invalid ghost. Outside a rewind, or for any non-Invalid result, this is a complete no-op. It does
/// NOT touch the revive/rewind itself — it only corrects the state ADVERTISED to the partner during it.
///
/// Why Climbing (not the last-valid state): the last state before the rewind is typically <c>Falling</c> (the
/// climber died falling), and replaying Falling for the whole rewind tells the partner "still falling" while
/// the ghost is actually being placed back on the wall. The rewind's destination is always a wall climb, so
/// Climbing is the correct, stable advertisement for the whole scrub-back-up.
/// </summary>
[HarmonyPatch(typeof(NetplayPawnCapture), nameof(NetplayPawnCapture.GetPlayerPawnState))]
internal static class ReviveStateGuard
{
    internal static Action<string> Log;

    private static bool _loggedThisRewind;

    private static void Postfix(ref NetFrame.PawnStateType __result)
    {
        try
        {
            if (__result != NetFrame.PawnStateType.Invalid)
            {
                _loggedThisRewind = false; // reset the once-per-rewind log latch on any valid sample
                return;
            }

            // result == Invalid: only override while the LOCAL pawn is in an active auto-rewind.
            if (!LocalAutoRewinding()) return;

            if (!_loggedThisRewind)
            {
                Log?.Invoke("reviveGuard: Invalid→Climbing during auto-rewind (partner keeps tracking the scrub-back-up)");
                _loggedThisRewind = true;
            }
            __result = NetFrame.PawnStateType.Climbing;
        }
        catch (Exception e)
        {
            // Fail-open: a guard must never break the net capture. Log once-ish and leave the result as-is.
            Log?.Invoke("reviveGuard: error " + e.Message);
        }
    }

    /// <summary>True iff the LOCAL pawn's RewindController is mid auto-rewind (inRewind &amp;&amp; automaticRewind).</summary>
    private static bool LocalAutoRewinding()
    {
        try
        {
            var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
            var sw = pc != null ? pc.PawnControllerSwitcher : null;
            var rw = sw != null ? sw.Rewind : null;
            return rw != null && rw.IsAutoRewinding;
        }
        catch { return false; }
    }
}
