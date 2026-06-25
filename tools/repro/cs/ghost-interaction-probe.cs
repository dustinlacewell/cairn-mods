// GATING LIVE-CHECK for the rope-request design: does the game's shipped remote-player interaction
// provider actually surface on a partner ghost in OUR free-solo co-op session? Keystone verified from
// bytes (re/systems/interaction/remote-player-interaction-provider.md). Read-only, one-shot, quiescent.
//
// CORRECTED probe surface (v2): sensors live under a private sensorContainer registered in
// PawnInteractionHandler.AllSensors (a Dictionary) — NOT direct children of the pawn root. So we read
// AllSensors.Count + the public per-limb sensor getters, NOT GetComponentsInChildren (which missed them).
// Likewise the ghost provider's catch-path reads its SERIALIZED colliders[] array (DetectColliders iterates
// it), NOT GetComponents — so arr=0 with colOnObj=1 means "a collider exists but isn't registered".
//
// Reports, per ghost: provider present/enabled, netPlayerId (attach target), acceptedSensorTypes, the
// serialized colliders[] length (the real gap), and colliders physically on the GameObject. Plus the LOCAL
// pawn's AUTHORITATIVE sensor count (AllSensors.Count) so we settle "0 sensors" vs probe-artifact.
using System;
using System.Text;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

try {
  var sb = new StringBuilder();
  var nm = MoSingleton<NetplayManager>.Instance;
  sb.Append("cstate=" + (nm != null ? (int)nm.clientState : -1));
  SharedRopeGamemode srg = null;
  if (nm != null && nm.Gamemode != null) { try { srg = nm.Gamemode.TryCast<SharedRopeGamemode>(); } catch {} }
  sb.Append(" gm=" + (srg != null ? "SharedRope" : "not-SharedRope"));

  // --- Ghost provider state ---
  var ghosts = UnityEngine.Object.FindObjectsOfType<NetplayRemotePlayer>();
  sb.Append(" ghosts=" + (ghosts != null ? ghosts.Length : 0));
  if (ghosts != null) foreach (var g in ghosts) {
    if (g == null) { sb.Append(" | [null]"); continue; }
    var prov = g.interactionProvider;
    if (prov == null) { sb.Append(" | NO-PROVIDER"); continue; }
    sb.Append(" | ghost");
    try { sb.Append(" npid=" + prov.netPlayerId); } catch {}
    try { sb.Append(" en=" + prov.enabled + " act=" + prov.gameObject.activeInHierarchy); } catch {}
    try { sb.Append(" sensTypes=" + (int)prov.acceptedSensorTypes); } catch {}
    try { var arr = prov.colliders; sb.Append(" arr=" + (arr != null ? arr.Length : 0)); } catch { sb.Append(" arr=err"); }
    try { var on = prov.GetComponents<Collider>(); sb.Append(" colOnObj=" + (on != null ? on.Length : 0)); } catch {}
    // Is that on-GameObject collider a trigger + what layer? (sensor overlap needs the sensor to be the
    // trigger; the provider collider just needs to be on an interactable layer.)
    try { var on = prov.GetComponents<Collider>(); if (on != null && on.Length > 0) sb.Append(" trig=" + on[0].isTrigger + " layer=" + prov.gameObject.layer); } catch {}
  }

  // --- LOCAL pawn interaction rig (AUTHORITATIVE surface) ---
  var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
  if (pc != null) {
    var h = pc.GetComponentInChildren<PawnInteractionHandler>(true);
    sb.Append(" || handler=" + (h != null));
    if (h != null) {
      try { sb.Append(" AllSensors=" + (h.AllSensors != null ? h.AllSensors.Count : -1)); } catch (Exception e) { sb.Append(" AllSensors=err(" + e.Message + ")"); }
      // public per-limb sensor getters
      try { sb.Append(" LH=" + (h.LeftHandSensor != null) + " RH=" + (h.RightHandSensor != null) + " Walk=" + (h.WalkingSensor != null)); } catch (Exception e) { sb.Append(" getters=err(" + e.Message + ")"); }
      // does the handler currently see ANY surrounding interactable?
      try { var sc = h.surroundingContexts; sb.Append(" surrounding=" + (sc != null ? sc.Count : -1)); } catch (Exception e) { sb.Append(" surrounding=err(" + e.Message + ")"); }
    }
  } else sb.Append(" || no-local-pc");
  return sb.ToString();
} catch (Exception e) { return "err:" + e.Message; }
