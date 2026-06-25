// Put the climber into a sustained REACHING (load-bearing) state — the high-level verb behind the player's
// "hold right-bumper + press a direction to select a limb" gesture: ClimbingV2PawnController.StartReachingWithLimb.
// This is what makes the climber actually EXERT: the reaching limb leaves the wall and the remaining holding
// limbs bear the load, so (with crank-stamina-drain.cs applied) they enter the Tiring branch
// (StaminaState=2, CurrentStaminaConsumptionSpeed > 0) and drain. A bare Drop of one limb does NOT work —
// on easy terrain the limb auto-re-grabs within a frame and the climber returns to resting Idle (no drain).
//
// Live-verified 2026-06-17: crank + StartReachingWithLimb(rightHand) → module=Reaching, the holding limbs go
// Tiring and drain to 0 in ~3s, criticalStaminaTimer accrues the 5s grace, then the climber lets go and
// falls (module → SecuredFalling when roped / Falling when free). The natural exhaustion fall.
//
// Reach with a FOOT, not a hand: lifting a foot transfers the body's weight onto the HANDS, which bear the
// real climbing load and tire (a hand reach barely loads the others, so they keep regenerating). Prefer a
// holding foot; fall back to a holding hand only if neither foot is on the wall.
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no pc";
  var limbs = new Il2Cpp.ClimbingV2PawnLimb[] { pc.leftFoot, pc.rightFoot, pc.leftHand, pc.rightHand };
  var names = new string[] { "LF", "RF", "LH", "RH" };
  Il2Cpp.ClimbingV2PawnLimb target = null; string which = "none";
  for (int i = 0; i < 4; i++) { if (limbs[i] != null && limbs[i].IsHolding) { target = limbs[i]; which = names[i]; break; } }
  if (target == null) { target = pc.leftFoot; which = "LF(forced)"; }
  pc.StartReachingWithLimb(target);
  return "reach-to-exert " + which + "; module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none");
} catch (System.Exception e) { return "err:" + e.Message; }
