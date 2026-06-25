// PHASE 0 failover, self-contained. Step is chosen by the bag key "p0step":
//   (absent/0) ADD two pitons, stash both quickdraws in the bag, report layout + GetLastSecuringPiton.
//   (1)        REMOVE the quickdraw whose piton is the current GetLastSecuringPiton, via the mod-proven
//              rope.ExternalRemoveQuickdraw (NOT Lifeline.RemovePiton, which desyncs). Report failover.
// Re-POST the SAME script to advance the step. Removal is the real chain-failover operation.
using System; using System.Text; using UnityEngine; using Il2Cpp;
var bag = CairnDevTools.Eval.Globals.Bag;
var sb = new StringBuilder();
Func<Vector3,string> V = p => "("+p.x.ToString("F2")+","+p.y.ToString("F2")+","+p.z.ToString("F2")+")";
try {
  var pc = MoSingleton<PawnManager>.Instance != null ? MoSingleton<PawnManager>.Instance.ClimbingPawnController : null;
  var ll = pc != null && pc.harness != null ? pc.harness.PersonalLifeline : null;
  var rope = ll != null ? ll.securingRope : null;
  if (rope == null) return "no rope";
  int step = bag.ContainsKey("p0step") ? (int)bag["p0step"] : 0;

  if (step == 0) {
    Vector3 c = ((Component)pc).transform.position;
    if (!rope.Visible) rope.SetVisible(true);
    var setting = new Lifeline.ClimbingSetting(pc);
    var q0 = ll.AddPiton(c + new Vector3(1.5f,1.0f,0f), Quaternion.identity, PitonExecutionQuality.Perfect, 1000, new InventoryItemStringId(3), setting);
    rope.RequestAddQuickdraw(q0, true);
    var q1 = ll.AddPiton(c + new Vector3(-1.5f,1.0f,0f), Quaternion.identity, PitonExecutionQuality.Perfect, 1000, new InventoryItemStringId(3), setting);
    rope.RequestAddQuickdraw(q1, true);
    bag["p0_qdA"] = q0; bag["p0_qdB"] = q1; bag["p0step"] = 1;
    sb.Append("STEP0 added 2 pitons; qdA.ptr=" + q0.Pointer + " qdB.ptr=" + q1.Pointer + "\n");
    sb.Append("(quickdraw split is async — wait, then re-POST to remove the last-securing one)\n");
    return sb.ToString();
  }

  // step >= 1: report current, then remove the quickdraw matching the current last-securing piton.
  var last = ll.GetLastSecuringPiton();
  sb.Append("BEFORE remove: native pitons=" + rope.GetNumberOfHoldingPiton()
    + " GetLastSecuringPiton=" + (last!=null?V(((Component)last).transform.position):"null") + "\n");
  if (last == null) { bag["p0step"] = 2; return sb.ToString() + "(no securing piton)"; }

  // pick the quickdraw on the same side as 'last' (compare X sign vs climber)
  var qdA = bag.ContainsKey("p0_qdA") ? bag["p0_qdA"] as LogicalQuickdraw : null;
  var qdB = bag.ContainsKey("p0_qdB") ? bag["p0_qdB"] as LogicalQuickdraw : null;
  Vector3 lp = ((Component)last).transform.position;
  Vector3 c2 = ((Component)pc).transform.position;
  // qdA was at +x, qdB at -x; the last piton's x relative to climber tells which side
  var toRemove = (lp.x > c2.x) ? qdA : qdB;
  string side = (lp.x > c2.x) ? "qdA(+x)" : "qdB(-x)";
  if (toRemove == null) return sb.ToString() + "(quickdraw handle lost from bag: qdA=" + (qdA!=null) + " qdB=" + (qdB!=null) + ")";
  try { rope.ExternalRemoveQuickdraw(toRemove); sb.Append("ExternalRemoveQuickdraw(" + side + ") called\n"); }
  catch (Exception re) { sb.Append("ExternalRemoveQuickdraw threw: " + re.Message + "\n"); }
  bag["p0step"] = 2;
  sb.Append("(async — re-read state next call to see failover)\n");
  return sb.ToString();
} catch (Exception e) { return sb.ToString()+"err:"+e.Message; }
