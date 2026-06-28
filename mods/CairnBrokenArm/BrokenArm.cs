using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using IKLimbTarget = Il2Cpp.ClimbingPawnCairnIK.IKLimbTarget;

namespace CairnBrokenArm;

// Identity + local-pawn resolution for the broken arm.
//
// Design: cairn-re/re/systems/climber/limb-control/BROKEN-ARM-VERDICT.md
//
// "Broken" is decided by limb IDENTITY (limb.limb is one of the configured disabled targets
// — at most one HAND and/or one FOOT, each a ctor-set readonly enum that never drifts) AND
// local-pawn ownership (limb.pawnController is the cached LOCAL controller). Co-op ghosts each
// carry their own controller + 4 limbs; we only disable the LOCAL climber's limbs.
//
// Local-pawn resolution: in CO-OP the local pawn is the controller whose harness the active
// SharedRopeGamemode maps to OUR id — exactly how CairnCoop.ClimberQuery resolves it
// (gamemode.GetHarness(LocalId).Pointer == controller.harness.Pointer). In SINGLE-PLAYER no
// gamemode exists, so the lone controller-with-harness IS the local pawn. We never break a
// random/ghost arm: when the local controller can't be positively identified AND the scene
// holds more than one controller-with-harness (ambiguous co-op), ownership is denied.
//
// Limbs are addressed via the named-field accessors (controller.rightHand/leftHand/...),
// NOT GetLimb(IKLimbTarget) — the field-accessor path is the stable interop surface (no
// enum→switch round-trip); GetLimb's enum overload is simply not needed here.
internal static class BrokenArm
{
    // The disabled hand/foot, each selected from config (nullable = none). Anything not
    // Left/Right parses to null.
    private static IKLimbTarget? DisabledHand =>
        Parse(Core.DisabledHand?.Value, IKLimbTarget.LeftHand, IKLimbTarget.RightHand);

    private static IKLimbTarget? DisabledFoot =>
        Parse(Core.DisabledFoot?.Value, IKLimbTarget.LeftFoot, IKLimbTarget.RightFoot);

    private static IKLimbTarget? Parse(string v, IKLimbTarget left, IKLimbTarget right)
    {
        switch (v?.Trim().ToLowerInvariant())
        {
            case "left": return left;
            case "right": return right;
            default: return null;
        }
    }

    // True iff the given limb target is one of the configured disabled targets.
    private static bool IsDisabledTarget(IKLimbTarget t)
    {
        var h = DisabledHand;
        var f = DisabledFoot;
        return (h.HasValue && t == h.Value) || (f.HasValue && t == f.Value);
    }

    // Cached local controller. Re-resolved ONLY when the cache is null/destroyed — never a
    // per-frame scan. On a hit the predicate is a single pointer compare.
    private static ClimbingV2PawnController _localController;

    private static bool Enabled => Core.Enabled == null || Core.Enabled.Value;

    // The core identity predicate used by EVERY patch.
    internal static bool IsBrokenLocal(ClimbingV2PawnLimb limb)
    {
        if (!Enabled || limb == null)
            return false;
        if (!IsDisabledTarget(limb.limb))
            return false;
        return IsLocalOwned(limb.pawnController);
    }

    // Local ownership. STRICT when a local controller is resolved (pointer match only — a
    // ghost's arm is never broken). When unresolved, accept ONLY the unambiguous case: a
    // single controller-with-harness in the scene (single-player / pre-co-op). In a
    // multi-controller scene with no positive local id, deny — we will not break a random arm.
    private static bool IsLocalOwned(ClimbingV2PawnController owner)
    {
        if (owner == null)
            return false;
        var local = ResolveLocalController();
        // local == null ⇒ ambiguous co-op (multiple controllers, no positive local id) ⇒ deny.
        return local != null && owner.Pointer == local.Pointer;
    }

    // The local pawn's controller, cached. Cheap re-resolve only on cache invalidation:
    //   co-op   — the controller whose harness the active gamemode maps to OUR id;
    //   sp/none — the lone controller-with-harness in the scene.
    // Returns null when nothing can be positively identified (ambiguous co-op pre-resolve).
    private static ClimbingV2PawnController ResolveLocalController()
    {
        if (_localController != null && _localController.harness != null)
            return _localController;

        _localController = ResolveByGamemodeHarness() ?? SoleControllerOrNull();
        return _localController;
    }

    // Co-op signal: match the active SharedRopeGamemode's local harness pointer against the
    // controllers in the scene — identical to CairnCoop.ClimberQuery.FindLocalClimbController.
    // Null in single-player (no gamemode) or before the harness mapping exists.
    private static ClimbingV2PawnController ResolveByGamemodeHarness()
    {
        try
        {
            var gamemode = MoSingleton<NetplayManager>.Instance?.Gamemode?.TryCast<SharedRopeGamemode>();
            if (gamemode == null)
                return null;
            var localHarness = gamemode.GetHarness(gamemode.LocalId);
            if (localHarness == null)
                return null;
            var ptr = localHarness.Pointer;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<ClimbingV2PawnController>())
                if (c != null && c.harness != null && c.harness.Pointer == ptr)
                    return c;
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Single-player / pre-co-op disambiguation: the ONLY controller-with-harness, or null if
    // none or more than one exists (a multi-controller scene is ambiguous co-op — don't guess).
    private static ClimbingV2PawnController SoleControllerOrNull()
    {
        var all = UnityEngine.Object.FindObjectsOfType<ClimbingV2PawnController>();
        if (all == null)
            return null;
        ClimbingV2PawnController sole = null;
        foreach (var c in all)
        {
            if (c == null || c.harness == null)
                continue;
            if (sole != null)
                return null; // more than one — ambiguous
            sole = c;
        }
        return sole;
    }

    // The type-aware same-kind mirror of a limb (left↔right within hand or foot). For
    // auto-pickers that must never hand back null (the dyno hand-switch traps on a null limb)
    // and for substitution of a disabled target. NEVER returns a disabled limb because at most
    // one of each pair is disabled.
    internal static ClimbingV2PawnLimb OppositeOf(ClimbingV2PawnLimb limb)
    {
        if (limb == null)
            return null;
        var c = limb.pawnController;
        if (c == null)
            return null;
        switch (limb.limb)
        {
            case IKLimbTarget.LeftHand:  return c.rightHand;
            case IKLimbTarget.RightHand: return c.leftHand;
            case IKLimbTarget.LeftFoot:  return c.rightFoot;
            case IKLimbTarget.RightFoot: return c.leftFoot;
            default: return null;
        }
    }

    // The disabled local limbs of a SPECIFIC controller — empty unless that controller is the
    // local pawn (a ghost's controller, or unresolved ownership, yields an empty list). Lets a
    // per-controller hook (OnModuleStart, Layer C) scope its action to the local pawn without
    // ever touching a ghost's state.
    internal static List<ClimbingV2PawnLimb> BrokenLimbsOf(ClimbingV2PawnController c)
    {
        var list = new List<ClimbingV2PawnLimb>(2);
        if (!Enabled || c == null || !IsLocalOwned(c))
            return list;
        var h = DisabledHand;
        if (h.HasValue)
            list.Add(h.Value == IKLimbTarget.RightHand ? c.rightHand : c.leftHand);
        var f = DisabledFoot;
        if (f.HasValue)
            list.Add(f.Value == IKLimbTarget.RightFoot ? c.rightFoot : c.leftFoot);
        return list;
    }

    // The disabled local limbs of the resolved local pawn (empty if unresolved).
    internal static List<ClimbingV2PawnLimb> LocalBrokenLimbs() => BrokenLimbsOf(ResolveLocalController());
}
