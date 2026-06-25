// PHASE 0: failover-by-removal probe. Remove the piton GetLastSecuringPiton currently returns, then
// re-read GetLastSecuringPiton — does it fall over to the OTHER piton on the same rope?
// This is the core of the chain failover model: "if your catching neighbor's piton is gone, you fall
// back to the next surviving neighbor's piton." Run AFTER p0-add-two-pitons.cs (rope has 2 pitons).
using System;
using System.Text;
using UnityEngine;
using Il2Cpp;

var sb = new StringBuilder();
Func<Vector3,string> V = p => "(" + p.x.ToString("F2") + "," + p.y.ToString("F2") + "," + p.z.ToString("F2") + ")";
try {
  var pc = MoSingleton<PawnManager>.Instance != null ? MoSingleton<PawnManager>.Instance.ClimbingPawnController : null;
  var ll = pc != null && pc.harness != null ? pc.harness.PersonalLifeline : null;
  var rope = ll != null ? ll.securingRope : null;
  if (rope == null) return "no rope";

  var before = ll.GetLastSecuringPiton();
  sb.Append("BEFORE: pitons(native)=" + rope.GetNumberOfHoldingPiton()
    + " GetLastSecuringPiton=" + (before != null ? V(((Component)before).transform.position) : "null") + "\n");
  if (before == null) return sb.ToString() + "(nothing to remove)";

  // Remove exactly the current last-securing piton via the Lifeline's own RemovePiton.
  ll.RemovePiton(before);

  var after = ll.GetLastSecuringPiton();
  bool failedOver = after != null && before != null && after.Pointer != before.Pointer;
  sb.Append("AFTER remove: pitons(native)=" + rope.GetNumberOfHoldingPiton()
    + " GetLastSecuringPiton=" + (after != null ? V(((Component)after).transform.position) : "null")
    + " failedOverToOther=" + failedOver + "\n");

  var holders = rope.ropeHolders; int n = holders != null ? holders.Count : 0;
  sb.Append("ropeHolders.Count=" + n + ":\n");
  for (int i = 0; i < n; i++) {
    var rh = holders[i]; if (rh == null) { sb.Append("  [" + i + "] null\n"); continue; }
    var pit = rh.TryCast<Piton>(); string kind = pit != null ? "PITON" : rh.GetType().Name;
    Vector3 pos = Vector3.zero; try { var mb = rh.TryCast<MonoBehaviour>(); if (mb != null) pos = mb.transform.position; } catch {}
    sb.Append("  [" + i + "] " + kind + " " + V(pos) + "\n");
  }
  return sb.ToString();
} catch (Exception e) { return sb.ToString() + "err:" + e.Message; }
