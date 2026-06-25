// Reach with a HAND (for the post-revive hand-jitter check). Picks a holding hand (right preferred, then
// left) and calls ClimbingV2PawnController.StartReachingWithLimb on it — the high-level "reach" gesture that
// lifts the hand off the wall into Reaching state, where the jitter (if any) manifests as the IK target
// oscillating. Returns which hand + the module. Local pawn only.
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no pc";
  Il2Cpp.ClimbingV2PawnLimb hand = null; string which = "none";
  if (pc.rightHand != null && pc.rightHand.IsHolding) { hand = pc.rightHand; which = "RH"; }
  else if (pc.leftHand != null && pc.leftHand.IsHolding) { hand = pc.leftHand; which = "LH"; }
  else if (pc.rightHand != null) { hand = pc.rightHand; which = "RH(forced)"; }
  if (hand == null) return "no hand";
  pc.StartReachingWithLimb(hand);
  return "reach-hand " + which + "; module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none");
} catch (System.Exception e) { return "err:" + e.Message; }
