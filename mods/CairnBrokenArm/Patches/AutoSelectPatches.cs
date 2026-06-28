using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;

namespace CairnBrokenArm.Patches;

// Layer A3 — R2: the broken arm must never be AUTO-chosen to reach. But the fix is SUBSTITUTE /
// fall-through, NOT null — nulling the broken arm makes the auto-selector give up (selection feels
// disabled). Each picker is reworked to hand back the game's OWN next-best instead.

// W5 dyno reach guard — ReachingLimbSwitch.OnEnter is the REAL writer of handToReach (reads a
// holds-set slot into the field at OnEnter.c:107-110); GoToSubState then reads handToReach and
// writes _currentState=3 (GoToSubState.c:83/199). The old GetBestHandForReaching postfix was
// INERT (that method has zero native callers), so the broken arm could transiently enter
// Reaching(3) on the dyno path. Postfix OnEnter: AFTER it sets handToReach, if that's the broken
// arm, swap it to the OTHER hand of the same controller. NEVER null — GoToSubState traps swi(3)
// on a null handToReach (OnEnter.c:84). OnEnter is public virtual → binds by name.
[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleReaching.ReachingLimbSwitch), "OnEnter")]
internal static class ReachingLimbSwitchOnEnterPatch
{
    private static void Postfix(ClimbingV2PawnControllerModuleReaching.ReachingLimbSwitch __instance)
    {
        var hand = __instance.handToReach;
        if (BrokenArm.IsBrokenLocal(hand))
            __instance.handToReach = BrokenArm.OppositeOf(hand);
        var foot = __instance.footToGrabHold;
        if (BrokenArm.IsBrokenLocal(foot))
            __instance.footToGrabHold = BrokenArm.OppositeOf(foot);
    }
}

// GetBestSelectionLimbToAvoidLoop (the hand-cycler). It ignores bannedLimbs, and its OWN native
// loop-avoidance fallback IS the opposite hand. So when it would hand back the broken arm,
// substitute the opposite hand — matching the method's own behavior. Private → patched by name.
[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleReaching), "GetBestSelectionLimbToAvoidLoop")]
internal static class GetBestSelectionLimbToAvoidLoopPatch
{
    private static void Postfix(ref ClimbingV2PawnLimb __result)
    {
        if (BrokenArm.IsBrokenLocal(__result))
            __result = BrokenArm.OppositeOf(__result);
    }
}

// OnModuleStart (the scored scan that DOES honor bannedLimbs, and can rank a FOOT next). Scope the
// exclusion to the scan: a Prefix adds the local broken arm to bannedLimbs (if absent), a Postfix
// removes it. This makes the game return its OWN next-best (foot-eligible) without permanently
// fighting the transient cycling-history list. bannedLimbs is exposed by Il2CppInterop as a direct
// List<ClimbingV2PawnLimb> member, so no AccessTools/Traverse is needed.
[HarmonyPatch(typeof(ClimbingV2PawnControllerModuleReaching), "OnModuleStart")]
internal static class OnModuleStartScopedBanPatch
{
    private static void Prefix(ClimbingV2PawnControllerModuleReaching __instance, out List<ClimbingV2PawnLimb> __state)
    {
        __state = null;
        if (__instance == null)
            return;
        // Resolve the disabled limbs of THIS module's OWN controller — empty unless it's the local
        // pawn, so a ghost's OnModuleStart never touches the local disabled limbs (or the ghost's list).
        var broken = BrokenArm.BrokenLimbsOf(__instance.pawnController);
        if (broken.Count == 0)
            return;
        var banned = __instance.bannedLimbs;
        if (banned == null)
            return;
        List<ClimbingV2PawnLimb> added = null;
        foreach (var b in broken)
        {
            if (b == null || banned.Contains(b))
                continue;
            banned.Add(b);
            (added ??= new List<ClimbingV2PawnLimb>(2)).Add(b);
        }
        __state = added; // exactly the limbs THIS prefix added; the postfix removes exactly these.
    }

    private static void Postfix(ClimbingV2PawnControllerModuleReaching __instance, List<ClimbingV2PawnLimb> __state)
    {
        if (__state == null || __instance == null)
            return;
        var banned = __instance.bannedLimbs;
        if (banned == null)
            return;
        foreach (var b in __state)
            banned.Remove(b);
    }
}
