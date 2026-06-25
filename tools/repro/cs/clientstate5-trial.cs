// DECISIVE TRIAL: the mod forces NetplayManager.clientState (the UI MIRROR @0x88) down to REGISTERED(3)
// every second to suppress the Harness.get_Lifeline redirect. The native ghost-interaction verbs need the
// mirror >=5 (IN_ROOM) to report available. Question: if we let the mirror reach 5, do the verbs light up
// WITHOUT breaking the bespoke catch (which reads harness.PersonalLifeline.securingRope — a DIFFERENT,
// un-redirected accessor than the virtual get_Lifeline)?
//
// This verb writes clientState=5 then ATOMICALLY (same eval, before the mod's 1Hz reset) reads:
//   IsInRoom                          — should flip true at 5
//   the 3 verbs' isAvailable()        — should flip available at 5 (the keystone gate)
//   PersonalLifeline.securingRope     — the bespoke catch rope; MUST stay on the climbot rope (intact)
//   virtual Lifeline.securingRope     — the redirected accessor; may diverge (that's the redirect firing)
// If PersonalLifeline.securingRope is unchanged while verbs go available, we can safely let the mirror hit 5.
//
// Writes one field (clientState); the mod re-resets it next tick, so this is a transient probe, not a
// lasting mutation. Still: discard the instance after (per restart-clean rule).
using System;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn.Netplay;

try {
  var sb = new StringBuilder();
  var nm = MoSingleton<NetplayManager>.Instance;
  if (nm == null) return "no NetplayManager";

  // --- BEFORE (mirror currently held at 3) ---
  sb.Append("before: cstate=" + (int)nm.clientState + " IsInRoom=" + nm.IsInRoom);

  // Resolve local pawn's harness + both lifeline accessors + the climbot rope BEFORE the write.
  var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
  var harness = pc != null ? pc.harness : null;
  Func<Lifeline, string> ropeId = (ll) => {
    if (ll == null) return "ll-null";
    var r = ll.securingRope;
    return r != null ? ("rope#" + r.Pointer.ToString("X")) : "null";
  };
  string persBefore = "n/a", virtBefore = "n/a";
  if (harness != null) {
    try { persBefore = ropeId(harness.PersonalLifeline); } catch (Exception e) { persBefore = "err(" + e.Message + ")"; }
    try { virtBefore = ropeId(harness.Lifeline); } catch (Exception e) { virtBefore = "err(" + e.Message + ")"; }
  }
  sb.Append(" persLL=" + persBefore + " virtLL=" + virtBefore);

  // --- WRITE clientState = IN_ROOM(5). Roslyn eval disallows `unsafe`, so compute the field address
  //     (both calls return values, no deref) and write via System.Runtime.InteropServices.Marshal. ---
  {
    IntPtr cls = Il2CppClassPointerStore<NetplayManager>.NativeClassPtr;
    IntPtr fi = IL2CPP.GetIl2CppField(cls, "clientState");
    IntPtr obj = IL2CPP.Il2CppObjectBaseToPtrNotNull(nm);
    IntPtr addr = (IntPtr)((long)obj + IL2CPP.il2cpp_field_get_offset(fi));
    System.Runtime.InteropServices.Marshal.WriteInt32(addr, 5);
  }

  // --- AFTER (mirror = 5 now) ---
  sb.Append(" || after: cstate=" + (int)nm.clientState + " IsInRoom=" + nm.IsInRoom);

  // Verb availability on the partner ghost at clientState=5.
  var ghosts = UnityEngine.Object.FindObjectsOfType<NetplayRemotePlayer>();
  if (ghosts != null && ghosts.Length > 0 && ghosts[0] != null && ghosts[0].interactionProvider != null) {
    var prov = ghosts[0].interactionProvider;
    sb.Append(" provIsAvail=" + prov.IsAvailable());
    try {
      var pos = prov.possibleInteractions;
      sb.Append(" verbs[");
      if (pos != null) for (int i = 0; i < pos.Length; i++) {
        bool av = false; try { if (pos[i].isAvailable != null) av = pos[i].isAvailable.Invoke(); } catch {}
        sb.Append("v" + i + ":" + av + ",");
      }
      sb.Append("]");
    } catch (Exception e) { sb.Append(" verbsErr(" + e.Message + ")"); }
  } else sb.Append(" no-ghost-provider");

  // The decisive read: did the bespoke catch's PersonalLifeline.securingRope change at 5? (and did the
  // virtual redirect fire?)
  string persAfter = "n/a", virtAfter = "n/a";
  if (harness != null) {
    try { persAfter = ropeId(harness.PersonalLifeline); } catch (Exception e) { persAfter = "err(" + e.Message + ")"; }
    try { virtAfter = ropeId(harness.Lifeline); } catch (Exception e) { virtAfter = "err(" + e.Message + ")"; }
  }
  sb.Append(" persLL=" + persAfter + " virtLL=" + virtAfter);
  sb.Append(" || persChanged=" + (persBefore != persAfter) + " virtChanged=" + (virtBefore != virtAfter));
  return sb.ToString();
} catch (Exception e) { return "err:" + e.Message; }
