// Diagnostic: dump the LOCAL climber's securingRope holder/piton layout.
// Run this BEFORE add-two-pitons, AFTER it, and during/after fall-toward.
// POST raw to the MIDDLE instance's eval port (see PROCEDURE for which port).
using System;
using System.Text;
using UnityEngine;
using Il2Cpp;

var sb = new StringBuilder();
Func<Vector3,string> V = p => "(" + p.x.ToString("F2") + "," + p.y.ToString("F2") + "," + p.z.ToString("F2") + ")";
try {
  var nm = NetplayManager.Instance;
  var gm = nm != null ? nm.Gamemode : null;
  var srg = gm as SharedRopeGamemode;
  bool mid = false; try { if (srg != null) mid = srg.IsLocalClientMiddleOnRope(); } catch (Exception me) { sb.Append("mid? " + me.Message + " "); }
  sb.Append("localId=" + (gm != null ? gm.LocalId : -1) + " middleOnRope=" + mid + "\n");

  var pc = MoSingleton<PawnManager>.Instance != null ? MoSingleton<PawnManager>.Instance.ClimbingPawnController : null;
  if (pc == null) return sb.ToString() + "(no local ClimbingPawnController)";
  sb.Append("module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "null")
    + " falling=" + pc.IsFalling + " secureFalling=" + pc.IsSecureFalling + "\n");

  var h = pc.harness;
  bool sec = false; try { sec = h.IsSecured(1); } catch {}
  var ll = h != null ? h.PersonalLifeline : null;
  var rope = ll != null ? ll.securingRope : null;
  sb.Append("IsSecured(1)=" + sec + " lifeline=" + (ll != null) + " securingRope=" + (rope != null) + "\n");
  if (rope == null) return sb.ToString();

  var last = ll.GetLastSecuringPiton();
  sb.Append("holders=" + rope.GetRopeHolderGlobalCount() + " pitons(native)=" + rope.GetNumberOfHoldingPiton() + "\n");
  var holders = rope.ropeHolders;
  int n = holders != null ? holders.Count : 0;
  sb.Append("ropeHolders.Count=" + n + ":\n");
  for (int i = 0; i < n; i++) {
    var rh = holders[i];
    if (rh == null) { sb.Append("  [" + i + "] null\n"); continue; }
    var pit = rh.TryCast<Piton>();
    string kind = pit != null ? "PITON" : rh.GetType().Name;
    Vector3 pos = Vector3.zero; try { var mb = rh.TryCast<MonoBehaviour>(); if (mb != null) pos = mb.transform.position; } catch {}
    bool isLast = pit != null && last != null && pit.Pointer == last.Pointer;
    sb.Append("  [" + i + "] " + kind + " " + V(pos) + (isLast ? "  <== GetLastSecuringPiton" : "") + "\n");
  }
  sb.Append("GetLastSecuringPiton=" + (last != null ? V(((Component)last).transform.position) : "null") + "\n");
  float rem = 0f; try { rem = h.GetRemainingSecuringRopeDistanceOnLifeline(); } catch (Exception re) { sb.Append("rem? " + re.Message + "\n"); }
  sb.Append("RemainingSecuringRopeDistance=" + rem.ToString("F2")
    + " climberPos=" + V(((Component)pc).transform.position) + "\n");
  return sb.ToString();
} catch (Exception e) { return sb.ToString() + "err:" + e.Message; }
