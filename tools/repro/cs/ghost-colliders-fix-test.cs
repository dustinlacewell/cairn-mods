// FIX-TEST for the ghost-interaction gate: the live probe showed the partner ghost's provider has the right
// id + a trigger collider on its GameObject, but its serialized colliders[] array is EMPTY, so DetectColliders
// builds no listeners and the ghost never enters the local handler's candidate set. The scout-proposed fix:
// stuff colliders[] with the collider already on the GameObject, then call public DetectColliders(). This verb
// APPLIES that fix live and re-reads the candidate set to confirm the ghost registers — AND reads each verb's
// availability (to settle whether cstate=3/REGISTERED blocks the attach verb vs IN_ROOM=5).
//
// MUTATES live state (writes colliders[], calls DetectColliders) — a one-shot fix-test; discard the instance
// after. Returns: per-verb availability BEFORE, the fix result, then the handler's surrounding count AFTER +
// whether the ghost's provider now appears as a detected candidate.
using System;
using System.Text;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays; // Il2CppReferenceArray

try {
  var sb = new StringBuilder();
  var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
  var handler = pc != null ? pc.GetComponentInChildren<PawnInteractionHandler>(true) : null;
  if (handler == null) return "no local handler";

  var ghosts = UnityEngine.Object.FindObjectsOfType<NetplayRemotePlayer>();
  if (ghosts == null || ghosts.Length == 0) return "no ghosts";
  var g = ghosts[0];
  var prov = g != null ? g.interactionProvider : null;
  if (prov == null) return "no provider on ghost";

  sb.Append("npid=" + prov.netPlayerId);

  // 1) BEFORE: read each verb's availability (settles cstate=3 verb-gating).
  try {
    var pos = prov.possibleInteractions;
    sb.Append(" verbs=" + (pos != null ? pos.Length : 0) + "[");
    if (pos != null) for (int i = 0; i < pos.Length; i++) {
      var ri = pos[i];
      bool valid = false, avail = false;
      try { valid = ri.isValid; } catch {}
      try { if (ri.isAvailable != null) avail = ri.isAvailable.Invoke(); } catch (Exception e) { sb.Append("availErr(" + e.Message + ")"); }
      sb.Append("v" + i + ":valid" + valid + ":avail" + avail + ",");
    }
    sb.Append("]");
    try { sb.Append(" provIsAvail=" + prov.IsAvailable()); } catch (Exception e) { sb.Append(" provIsAvail=err(" + e.Message + ")"); }
  } catch (Exception e) { sb.Append(" verbsErr(" + e.Message + ")"); }

  // 2) Candidate set BEFORE the fix.
  int beforeSurrounding = -1;
  try { beforeSurrounding = handler.surroundingContexts != null ? handler.surroundingContexts.Count : -1; } catch {}
  sb.Append(" | beforeSurrounding=" + beforeSurrounding);

  // 3) APPLY THE FIX: put the on-GameObject trigger collider into colliders[], then DetectColliders().
  try {
    var on = prov.GetComponents<Collider>();
    if (on == null || on.Length == 0) { sb.Append(" | FIX-ABORT: no collider on GameObject"); return sb.ToString(); }
    var arr = new Il2CppReferenceArray<Collider>(on.Length);
    for (int i = 0; i < on.Length; i++) arr[i] = on[i];
    prov.colliders = arr;
    // make sure both modalities are accepted (Hands|Walking). InteractionSensorType: Hands=1, Walking=8.
    try { prov.acceptedSensorTypes = (InteractionSensorType)(1 | 8); } catch {}
    prov.DetectColliders();
    sb.Append(" | FIX-APPLIED arr=" + prov.colliders.Length);
  } catch (Exception e) { sb.Append(" | FIX-ERR(" + e.Message + ")"); return sb.ToString(); }

  return sb.ToString() + " (re-probe surrounding next tick with ghost-interaction-probe.cs)";
} catch (Exception e) { return "err:" + e.Message; }
