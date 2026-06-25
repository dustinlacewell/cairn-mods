using System;
using HarmonyLib;
using Il2Cpp;

namespace CairnCoop;

/// <summary>
/// Makes the local belay rope's length OURS alone.
///
/// SharedRopeGamemode.UpdateRopeLengths (0x1831682A0, run from the gamemode's Update) walks
/// `sharedRopes`, resolves each climber's `lifeline.securingRope`, and drives its length via
/// LogicalRope.SetLengthSafe to the shared-rope payout (70 m). Once we point securingRope at the
/// climbot belay rope, the game drives THAT — ramping it to 70 m every frame (seen live: length
/// climbing 30 → 65 → 134 → 155 m, past its own 40 m max), fighting our reel-in. The game uses
/// SetLengthSafe; our reel uses RequestSetLength — different methods — so dropping SetLengthSafe
/// on our rope alone hands length authority entirely to us, with zero effect on any other rope.
/// </summary>
[HarmonyPatch(typeof(LogicalRope), nameof(LogicalRope.SetLengthSafe))]
internal static class RopeLengthGuard
{
    /// <summary>Pointer of the rope whose length we own; set by the driver while an anchor is
    /// live, IntPtr.Zero otherwise. Pointer rather than reference so the comparison is a cheap
    /// identity check with no managed-wrapper churn on a hot path.</summary>
    internal static IntPtr OwnedRope = IntPtr.Zero;

    private static bool Prefix(LogicalRope __instance)
        => __instance.Pointer != OwnedRope; // false => skip the game's SetLengthSafe on our rope
}
