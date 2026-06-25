// Per-instance chain topology readout for the N-chain failover tests: my module/fall-state, IsSecured, the
// native holding-piton count on my securingRope, the last-securing piton position, plus the mod's CarryGraph
// and BelayDiag. One line of facts the failover test parses (pitons=N, secured=True/False) and a second/third
// line of mod diagnostics. POST raw to each instance's eval port.
using System; using System.Text; using UnityEngine; using Il2Cpp;
Func<Vector3,string> V = p => "(" + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1") + ")";
try {
  var pc = MoSingleton<PawnManager>.Instance != null ? MoSingleton<PawnManager>.Instance.ClimbingPawnController : null;
  if (pc == null) return "no pc";
  var h = pc.harness;
  var ll = h != null ? h.PersonalLifeline : null;
  var rope = ll != null ? ll.securingRope : null;
  var d = CairnCoop.Core.Instance.Driver;
  var sb = new StringBuilder();
  sb.Append("module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "null")
    + " falling=" + pc.IsFalling + " secFall=" + pc.IsSecureFalling
    + " secured=" + (h != null ? h.IsSecured(1) : false)
    + " pitons=" + (rope != null ? rope.GetNumberOfHoldingPiton() : -1));
  if (rope != null) {
    var last = ll.GetLastSecuringPiton();
    sb.Append(" lastPiton=" + (last != null ? V(((Component)last).transform.position) : "null"));
  }
  sb.Append("\n  " + d.CarryGraph());
  sb.Append("\n  " + d.BelayDiag());
  return sb.ToString();
} catch (Exception e) { return "err:" + e.Message; }
