// Induce the LOCAL (middle) climber to FALL, then read which piton arrests it.
// Trigger = the VERIFIED exhaustion-fall chain (re/systems/climber/stamina-eject-chain.md):
//   prerequisite: crank-stamina-drain.cs already applied this session, THEN this script reaches with
//   a FOOT to load the hands -> Tiring -> drain -> criticalStaminaTimer grace -> Fall(Exhausted) ->
//   roped => SecuredFalling. There is NO verified per-side fall-DIRECTION control (see report);
//   the discriminator is WHICH piton GetLastSecuringPiton ends up clamping to, not a commanded direction.
// This script: (1) fire the reach to start the fall, (2) sample GetLastSecuringPiton + module + clamp
// distance over several frames so the coordinator can read the arrest. Run AFTER add-two-pitons.cs.
using System;
using System.Text;
using UnityEngine;
using Il2Cpp;

var sb = new StringBuilder();
Func<Vector3,string> V = p => "(" + p.x.ToString("F2") + "," + p.y.ToString("F2") + "," + p.z.ToString("F2") + ")";
try {
  var pc = MoSingleton<PawnManager>.Instance != null ? MoSingleton<PawnManager>.Instance.ClimbingPawnController : null;
  if (pc == null) return "no pc";
  var ll = pc.harness != null ? pc.harness.PersonalLifeline : null;
  if (ll == null) return "no lifeline";

  // identify the two pitons by reading the rope's piton holders (top-down order matters for GetLast)
  var rope = ll.securingRope; var holders = rope != null ? rope.ropeHolders : null;
  int n = holders != null ? holders.Count : 0;
  System.Collections.Generic.List<Vector3> pitonPos = new System.Collections.Generic.List<Vector3>();
  for (int i = 0; i < n; i++) { var rh = holders[i]; if (rh == null) continue; var p = rh.TryCast<Piton>(); if (p != null) { try { pitonPos.Add(((Component)p).transform.position); } catch {} } }
  sb.Append("pre-fall pitons=" + pitonPos.Count + ":"); foreach (var pp in pitonPos) sb.Append(" " + V(pp)); sb.Append("\n");

  // FIRE the exhaustion reach with a holding FOOT (loads the hands). crank-stamina-drain must be active.
  var limbs = new ClimbingV2PawnLimb[] { pc.leftFoot, pc.rightFoot, pc.leftHand, pc.rightHand };
  var names = new string[] { "LF","RF","LH","RH" };
  ClimbingV2PawnLimb t = null; string which = "none";
  for (int i = 0; i < 4; i++) if (limbs[i] != null && limbs[i].IsHolding) { t = limbs[i]; which = names[i]; break; }
  if (t == null) { t = pc.leftFoot; which = "LF(forced)"; }
  pc.StartReachingWithLimb(t);
  sb.Append("reach=" + which + " module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none") + "\n");

  // Sample the arrest over a handful of frames. (Eval is synchronous; this captures the instantaneous
  // snapshot + a short busy-spin sample. The DEFINITIVE arrest read is to re-run read-securing-state.cs
  // a couple seconds later once SecuredFalling has clamped.)
  Func<Vector3> last = () => { var L = ll.GetLastSecuringPiton(); return L != null ? ((Component)L).transform.position : new Vector3(-999,-999,-999); };
  float rem0 = 0f; try { rem0 = pc.harness.GetRemainingSecuringRopeDistanceOnLifeline(); } catch {}
  sb.Append("t0 module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none")
    + " lastPiton=" + V(last()) + " remaining=" + rem0.ToString("F2")
    + " climber=" + V(((Component)pc).transform.position) + "\n");
  return sb.ToString();
} catch (Exception e) { return sb.ToString() + "err:" + e.Message; }
