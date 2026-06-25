// PHASE 0 (2-instance rig variant): build a TWO-PITON securingRope WITHOUT requiring two real neighbors.
// Places pitonA at the ONE real neighbor's harness position, and pitonB at a fixed world OFFSET from the
// climber (the substrate keys the catch on piton WORLD-POSITION, not on who holds the rope end — proven).
// This answers the Phase-0 substrate question on the 2-instance rig:
//   - do two pitons coexist as DISTINCT holders on one securingRope?
//   - which one is GetLastSecuringPiton (the arrest piton)?
//   - (followup) does removing one fail over to the other?
// Reuses CairnCoop.PartnerBelay.CreateAnchor's exact pattern (AddPiton + RequestAddQuickdraw, item id 3).
using System;
using System.Text;
using UnityEngine;
using Il2Cpp;

var sb = new StringBuilder();
Func<Vector3,string> V = p => "(" + p.x.ToString("F2") + "," + p.y.ToString("F2") + "," + p.z.ToString("F2") + ")";
try {
  var pc = MoSingleton<PawnManager>.Instance != null ? MoSingleton<PawnManager>.Instance.ClimbingPawnController : null;
  var lifeline = pc != null && pc.harness != null ? pc.harness.PersonalLifeline : null;
  var rope = lifeline != null ? lifeline.securingRope : null;
  if (lifeline == null || rope == null) return "no lifeline/securingRope (pc=" + (pc != null) + ")";
  if (!rope.Visible) rope.SetVisible(true);

  Vector3 climberPos = ((Component)pc).transform.position;
  // The catch keys only on piton WORLD-position (proven), so two offset pitons answer the substrate Q on
  // the 2-instance rig. posA above-and-right (the "A neighbor" side), posB above-and-left (the "B" side).
  Vector3 posA = climberPos + new Vector3(1.5f, 1.0f, 0f);
  Vector3 posB = climberPos + new Vector3(-1.5f, 1.0f, 0f);
  int rn = -1; // gamemode not needed for the substrate probe

  var setting = new Lifeline.ClimbingSetting(pc);
  var qdA = lifeline.AddPiton(posA, Quaternion.identity, PitonExecutionQuality.Perfect, 1000, new InventoryItemStringId(3), setting);
  rope.RequestAddQuickdraw(qdA, true);
  var pitonA = lifeline.GetLastPiton();
  var qdB = lifeline.AddPiton(posB, Quaternion.identity, PitonExecutionQuality.Perfect, 1000, new InventoryItemStringId(3), setting);
  rope.RequestAddQuickdraw(qdB, true);
  var pitonB = lifeline.GetLastPiton();

  sb.Append("climber=" + V(climberPos) + " realNeighbors=" + rn + "\n");
  sb.Append("placed pitonA@" + V(posA) + " pitonB@" + V(posB) + "\n");
  sb.Append("pitonA.ptr=" + (pitonA != null ? pitonA.Pointer.ToString() : "null")
    + " pitonB.ptr=" + (pitonB != null ? pitonB.Pointer.ToString() : "null") + "\n");

  var holders = rope.ropeHolders; int n = holders != null ? holders.Count : 0;
  sb.Append("ropeHolders.Count=" + n + " pitons(native)=" + rope.GetNumberOfHoldingPiton() + ":\n");
  for (int i = 0; i < n; i++) {
    var rh = holders[i]; if (rh == null) { sb.Append("  [" + i + "] null\n"); continue; }
    var pit = rh.TryCast<Piton>(); string kind = pit != null ? "PITON" : rh.GetType().Name;
    Vector3 pos = Vector3.zero; try { var mb = rh.TryCast<MonoBehaviour>(); if (mb != null) pos = mb.transform.position; } catch {}
    string tag = (pit == null) ? "" : (pitonA != null && pit.Pointer == pitonA.Pointer) ? " (pitonA->A)" : (pitonB != null && pit.Pointer == pitonB.Pointer) ? " (pitonB->B)" : "";
    sb.Append("  [" + i + "] " + kind + " " + V(pos) + tag + "\n");
  }
  var last = lifeline.GetLastSecuringPiton();
  string which = last == null ? "null" : (pitonA != null && last.Pointer == pitonA.Pointer) ? "pitonA(->A)" : (pitonB != null && last.Pointer == pitonB.Pointer) ? "pitonB(->B)" : "OTHER " + V(((Component)last).transform.position);
  sb.Append("GetLastSecuringPiton=" + which + "\n");
  return sb.ToString();
} catch (Exception e) { return sb.ToString() + "err:" + e.Message; }
