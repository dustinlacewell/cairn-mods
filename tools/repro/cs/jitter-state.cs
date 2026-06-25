// jitter-state.cs — SAFE whole-climber diagnostic (field reads only, NO patching, no crash risk). Run on a
// climber to test what whole-climber state differs in the broken (all-limbs-jitter) mode vs normal. Prime
// hypothesis: ik.ragdoll.IsRagdoll==true makes the full-body Solve() SKIP all limb IK and leave bones
// physics-driven — a WHOLE-CLIMBER effect that would make every reach jitter (target set, bone physics-
// driven, fighting). Reads ragdoll state + each limb's reach gap. Induce reaches first (reach-hand / the
// driver) to populate the gaps. Compare a broken climber's output to a normal one's.
using System.Text;
using Il2Cpp;
try {
  var sb = new StringBuilder();
  var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no-pc";
  // whole-climber: ragdoll seam (IsRagdoll => Solve skips ALL limb IK; bones physics-driven)
  try {
    var ik = pc.ik;
    var rag = ik != null ? ik.ragdoll : null;
    if (rag == null) sb.AppendLine("RAGD: (no ragdoll layer)");
    else sb.AppendLine("RAGD: IsRagdoll=" + rag.IsRagdoll + " enabledTimer=" + rag.ragdollEnabledTimer.ToString("F3"));
  } catch (System.Exception e) { sb.AppendLine("RAGD ERR " + e.Message); }
  // controller mode + reach module
  try { sb.AppendLine("module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none")); } catch {}
  try { sb.AppendLine("ShouldStickToHoldNoCheat=" + pc.ShouldStickToHoldNoCheat); } catch (System.Exception e) { sb.AppendLine("stick? " + e.Message); }
  // per-limb state + gap
  foreach (var t in new[]{ ("LH",pc.leftHand), ("RH",pc.rightHand), ("LF",pc.leftFoot), ("RF",pc.rightFoot) }) {
    var l = t.Item2; if (l == null) { sb.AppendLine(t.Item1 + " null"); continue; }
    var tg = l.CurrentIKTargetPosition; var bn = l.CurrentEndNodePosition;
    sb.AppendLine(t.Item1 + " state=" + l.CurrentState + " hold=" + l.IsHolding + " gap=" + (tg-bn).magnitude.ToString("F4"));
  }
  return sb.ToString();
} catch (System.Exception e) { return "err:" + e.Message; }
