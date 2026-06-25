// Jitter watch for the post-revive reaching-hand bug. Samples the LOCAL pawn's hands' IK target (G =
// CurrentIKTargetPosition, what the IK is commanded to) and end-node bone (A = CurrentEndNodePosition, the
// real wrist). Loop this from the shell (each POST lands on a different frame); a 2-position oscillation
// shows as G alternating between two fixed values with a rock-constant d. Resolves the LOCAL controller
// explicitly (NOT the scene's first one — that can be a remote ghost in co-op). Reports per-hand state +
// IsRagdoll/IsHolding so we can confirm it's a non-holding, non-ragdoll Reaching limb (the bug's signature).
using System;
using UnityEngine;
using Il2Cpp;

var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
if (pc == null) return "(no local pc)";

Func<Vector3, string> V = p => p.x.ToString("F3") + "," + p.y.ToString("F3") + "," + p.z.ToString("F3");
Func<string, ClimbingV2PawnLimb, string> one = (tag, L) => {
    if (L == null) return tag + "=null";
    Vector3 g = L.CurrentIKTargetPosition, a = L.CurrentEndNodePosition;
    // The jitter signature is G oscillating between two fixed positions frame-to-frame (constant d). State +
    // IsHolding confirm it's a non-holding Reaching limb. (ClimbingV2PawnLimb has no `ik` field — don't read it.)
    return tag + " " + L.CurrentState + " hold=" + L.IsHolding
        + " G[" + V(g) + "] d=" + Vector3.Distance(g, a).ToString("F4");
};
return "f" + Time.frameCount + " | " + one("LH", pc.leftHand) + " | " + one("RH", pc.rightHand);
