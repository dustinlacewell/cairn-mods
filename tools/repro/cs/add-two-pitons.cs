// On the LOCAL (middle) climber, build ONE securingRope spanning BOTH neighbors with TWO pitons:
//   neighborB -> [pitonB] -> me -> [pitonA] -> neighborA
// Places pitonA at neighborA's harness pos, pitonB at neighborB's harness pos, both onto the
// personal securingRope (AddPiton + RequestAddQuickdraw), exactly like CairnCoop.PartnerBelay.CreateAnchor.
// Reports the resulting holder/piton layout + which is GetLastSecuringPiton.
// Run ONCE on the middle instance AFTER the chain has formed and the rope is strung.
using System;
using System.Text;
using UnityEngine;
using Il2Cpp;

var sb = new StringBuilder();
Func<Vector3,string> V = p => "(" + p.x.ToString("F2") + "," + p.y.ToString("F2") + "," + p.z.ToString("F2") + ")";
try {
  var nm = NetplayManager.Instance;
  var srg = (nm != null ? nm.Gamemode : null) as SharedRopeGamemode;
  if (srg == null) return "no SharedRopeGamemode";
  int localId = srg.LocalId;

  var pc = MoSingleton<PawnManager>.Instance != null ? MoSingleton<PawnManager>.Instance.ClimbingPawnController : null;
  var lifeline = pc != null && pc.harness != null ? pc.harness.PersonalLifeline : null;
  var rope = lifeline != null ? lifeline.securingRope : null;
  if (lifeline == null || rope == null) return "no lifeline/securingRope (pc=" + (pc != null) + ")";
  if (!rope.Visible) rope.SetVisible(true);

  // Resolve the two neighbor harness positions from the gamemode by climber id.
  var rps = srg.GetRemotePlayers();
  int rn = rps != null ? rps.Count : 0;
  if (rn < 2) return "need 2 remote neighbors, have " + rn + " (this instance is NOT a middle climber)";
  // Take the two remote ids as the neighbors; their order in the chain is read from positions below.
  int idA = rps[0].Id, idB = rps[1].Id;
  var hA = srg.GetHarness(idA); var hB = srg.GetHarness(idB);
  if (hA == null || hB == null) return "GetHarness null (A=" + (hA != null) + " B=" + (hB != null) + ")";
  Vector3 posA = ((Component)hA).transform.position;
  Vector3 posB = ((Component)hB).transform.position;

  var setting = new Lifeline.ClimbingSetting(pc);
  // pitonA toward neighborA
  var qdA = lifeline.AddPiton(posA, Quaternion.identity, PitonExecutionQuality.Perfect, 1000, new InventoryItemStringId(3), setting);
  rope.RequestAddQuickdraw(qdA, true);
  var pitonA = lifeline.GetLastPiton();
  // pitonB toward neighborB
  var qdB = lifeline.AddPiton(posB, Quaternion.identity, PitonExecutionQuality.Perfect, 1000, new InventoryItemStringId(3), setting);
  rope.RequestAddQuickdraw(qdB, true);
  var pitonB = lifeline.GetLastPiton();

  sb.Append("placed pitonA(id" + idA + ")@" + V(posA) + " pitonB(id" + idB + ")@" + V(posB) + "\n");
  sb.Append("pitonA.ptr=" + pitonA.Pointer + " pitonB.ptr=" + pitonB.Pointer + "\n");

  var holders = rope.ropeHolders; int n = holders != null ? holders.Count : 0;
  sb.Append("ropeHolders.Count=" + n + " pitons(native)=" + rope.GetNumberOfHoldingPiton() + ":\n");
  for (int i = 0; i < n; i++) {
    var rh = holders[i]; if (rh == null) { sb.Append("  [" + i + "] null\n"); continue; }
    var pit = rh.TryCast<Piton>(); string kind = pit != null ? "PITON" : rh.GetType().Name;
    Vector3 pos = Vector3.zero; try { pos = ((Component)rh).transform.position; } catch {}
    string tag = pit == null ? "" : pit.Pointer == pitonA.Pointer ? " (pitonA->A)" : pit.Pointer == pitonB.Pointer ? " (pitonB->B)" : "";
    sb.Append("  [" + i + "] " + kind + " " + V(pos) + tag + "\n");
  }
  var last = lifeline.GetLastSecuringPiton();
  string which = last == null ? "null" : last.Pointer == pitonA.Pointer ? "pitonA(->A)" : last.Pointer == pitonB.Pointer ? "pitonB(->B)" : "OTHER " + V(((Component)last).transform.position);
  sb.Append("GetLastSecuringPiton=" + which + "\n");
  return sb.ToString();
} catch (Exception e) { return sb.ToString() + "err:" + e.Message; }
