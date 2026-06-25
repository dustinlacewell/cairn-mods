using System;
using HarmonyLib;
using UnityEngine;
using Il2Cpp;

namespace CairnCoop;

/// <summary>
/// IK-WRITER trace for the reaching-limb jitter. The jitter is PROVEN (live): on a Reaching limb the
/// IK target (CurrentIKTargetPosition, "G") and the end-node bone (CurrentEndNodePosition, "A") trade the
/// same two fixed positions P1/P2 frame-to-frame — i.e. two writers each frame set the target to opposite
/// members of the pair, so it oscillates. AlignIKTargetPositionOnCurrentLimbTransform writes target←bone
/// (the P2 side); a reach writer sets target←goal (the P1 side). The clean baseline had NO Align during a
/// reach. This trace logs, per frame on the jittering limb, WHICH writer ran and the target value AFTER it,
/// so the log shows the alternation directly: "Wx set RF tgt=P1; Align set RF tgt=P2; …".
///
/// Logs only the limb that is actively Reaching (the jitter is isolated to it), so it cannot flood.
/// </summary>
internal static class CallTrace
{
    internal static Action<string> Log;

    private static ClimbingV2PawnController Local()
    {
        try { return MoSingleton<PawnManager>.Instance?.ClimbingPawnController; } catch { return null; }
    }

    // Identify which local limb this instance is, and whether it's the reaching one.
    private static string LimbTag(ClimbingV2PawnLimb limb, out bool isReaching)
    {
        isReaching = false;
        var c = Local();
        if (c == null || limb == null) return null;
        string tag = null;
        try
        {
            if (limb.Pointer == c.leftHand?.Pointer) tag = "LH";
            else if (limb.Pointer == c.rightHand?.Pointer) tag = "RH";
            else if (limb.Pointer == c.leftFoot?.Pointer) tag = "LF";
            else if (limb.Pointer == c.rightFoot?.Pointer) tag = "RF";
            else return null; // not a local limb (remote ghost) — skip
            isReaching = limb.CurrentState.ToString() == "Reaching";
        }
        catch { return null; }
        return tag;
    }

    // Log a writer call on the local reaching limb, with the target it now holds (P1/P2 disambiguated
    // by distance to the bone — the two positions are fixed, so "which is target" is the whole signal).
    private static void LogWrite(string method, ClimbingV2PawnLimb limb)
    {
        try
        {
            string tag = LimbTag(limb, out bool reaching);
            if (tag == null || !reaching) return;
            Vector3 g = limb.CurrentIKTargetPosition, a = limb.CurrentEndNodePosition;
            int f; try { f = Time.frameCount; } catch { f = -1; }
            Log?.Invoke($"ik f{f} {tag} {method} tgt=({g.x:F3},{g.y:F3},{g.z:F3}) bone=({a.x:F3},{a.y:F3},{a.z:F3}) d={Vector3.Distance(g, a):F3}");
        }
        catch { /* never throw out of native */ }
    }

    // ── prefixes per writer (each names itself) ──────────────────────────────────────────────────────
    private static void ReachTowardPre(ClimbingV2PawnLimb __instance) => LogWrite("ReachToward", __instance);
    private static void SetIKTargetPre(ClimbingV2PawnLimb __instance) => LogWrite("SetIKTargetPosition", __instance);
    private static void AlignPre(ClimbingV2PawnLimb __instance) => LogWrite("Align(target<-bone)", __instance);
    private static void MoveIKPre(ClimbingV2PawnLimb __instance) => LogWrite("MoveIKTargetPosition", __instance);
    private static void ForceEndPre(ClimbingV2PawnLimb __instance) => LogWrite("ForceEndNodeTransform", __instance);

    private static readonly (string method, string handler)[] LimbWriters =
    {
        ("ReachToward", nameof(ReachTowardPre)),
        ("SetIKTargetPosition", nameof(SetIKTargetPre)),
        ("AlignIKTargetPositionOnCurrentLimbTransform", nameof(AlignPre)),
        ("MoveIKTargetPosition", nameof(MoveIKPre)),
        ("ForceEndNodeTransform", nameof(ForceEndPre)),
    };

    internal static void Install(HarmonyLib.Harmony h)
    {
        int ok = 0;
        var limbType = typeof(ClimbingV2PawnLimb);
        foreach (var (method, handler) in LimbWriters)
        {
            try
            {
                var m = AccessTools.Method(limbType, method);
                if (m == null) { Log?.Invoke($"ik: NOT FOUND ClimbingV2PawnLimb.{method}"); continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(CallTrace), handler));
                ok++;
            }
            catch (Exception e) { Log?.Invoke($"ik: FAILED {method}: {e.Message}"); }
        }
        Log?.Invoke($"ik: writer trace installed — patched {ok}/{LimbWriters.Length}");
    }
}
