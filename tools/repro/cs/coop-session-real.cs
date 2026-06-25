// Is the co-op session ACTUALLY live? The mod's "roped=True / InRoom" flags lie — the runtime co-op
// state is the ground truth (REINIT-letter directive #3). One line, pollable. Checks:
//   NetplayManager.clientState   — mod IsInRoom needs > 4 (3 = not really in a room)
//   active Gamemode subtype      — must be SharedRopeGamemode for co-op, not the base Gamemode
//   GetRemotePlayers().Count      — a partner actually present in the roster
//   per remote: Id / PawnState / IsHoldingFirmly
//   local holding-limb count      — how many of the local climber's 4 limbs are on a hold RIGHT NOW
//                                   (Thread 1: an exhaustion drop reduces this; <2 holding hands → frames
//                                   stamp non-resumable in CanResumeFromFrame)
// Returns one line: "cstate=<n> gm=<TypeName> remotes=<n>[id:state:firm,...] localHold=<n>/4 [LH RH LF RF holds]"
try {
  var sb = new System.Text.StringBuilder();
  var nm = Il2Cpp.MoSingleton<Il2CppTheGameBakers.Cairn.Netplay.NetplayManager>.Instance;
  int cstate = nm != null ? (int)nm.clientState : -1;
  sb.Append("cstate=" + cstate);

  string gmName = "none";
  Il2CppTheGameBakers.Cairn.Netplay.SharedRopeGamemode srg = null;
  if (nm != null && nm.Gamemode != null) {
    try { gmName = nm.Gamemode.GetIl2CppType().Name; } catch {}
    try { srg = nm.Gamemode.TryCast<Il2CppTheGameBakers.Cairn.Netplay.SharedRopeGamemode>(); } catch {}
  }
  sb.Append(" gm=" + gmName + (srg != null ? "(SharedRope)" : ""));

  if (srg != null) {
    try {
      var remotes = srg.GetRemotePlayers();
      sb.Append(" remotes=" + remotes.Count + "[");
      for (int i = 0; i < remotes.Count; i++) {
        var r = remotes[i];
        if (r == null) { sb.Append("null,"); continue; }
        sb.Append(r.Id + ":" + r.PawnState.ToString() + ":firm" + r.IsHoldingFirmly + ",");
      }
      sb.Append("]");
    } catch (System.Exception e) { sb.Append(" remotes=err(" + e.Message + ")"); }
  } else {
    sb.Append(" remotes=n/a");
  }

  // Local holding state — the input CanResumeFromFrame's resumability stamp depends on.
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc != null) {
    var limbs = new Il2Cpp.ClimbingV2PawnLimb[] { pc.leftHand, pc.rightHand, pc.leftFoot, pc.rightFoot };
    var names = new string[] { "LH", "RH", "LF", "RF" };
    int held = 0;
    var detail = new System.Text.StringBuilder();
    for (int i = 0; i < 4; i++) {
      var l = limbs[i];
      bool h = l != null && l.IsHolding;
      if (h) held++;
      detail.Append(names[i] + (h ? "+" : "-") + " ");
    }
    sb.Append(" localHold=" + held + "/4 [" + detail.ToString().Trim() + "]");
    sb.Append(" module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none"));
  } else {
    sb.Append(" localHold=no-pc");
  }
  return sb.ToString();
} catch (System.Exception e) { return "err:" + e.Message; }
