// Probe the local climber's stamina/eject state. One line, easy to poll while watching exhaustion build.
// Fields (binary-verified, re/systems/climber/stamina-eject-chain.md):
//   ClimbingV2PawnController.criticalStaminaTimer @0x290  — the eject countdown (>= CriticalStaminaStateDuration → Fall(Exhausted))
//   ClimbingV2PawnLimb._StaminaState_k__BackingField @0x3B4 — 0 Rest, 1 Free, 2 Tiring(draining), 3 SmallRestBonus, 4 RestBonus, 5 Punishment
//   ClimbingV2PawnLimb.CurrentStaminaConsumptionSpeed — positive = draining (Tiring), negative = regenerating
// Returns: "module=<n> critTimer=<f> | LH st=.. hold=.. ratio=.. spd=.. staSt=.. | RH .. | LF .. | RF .."
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no pc";
  var sb = new System.Text.StringBuilder();
  long pcp = (long)pc.Pointer;
  float critTimer = System.BitConverter.ToSingle(System.BitConverter.GetBytes(
      System.Runtime.InteropServices.Marshal.ReadInt32((System.IntPtr)(pcp + 0x290))), 0);
  sb.Append("module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none"));
  sb.Append(" critTimer=" + critTimer.ToString("F3"));
  var limbs = new Il2Cpp.ClimbingV2PawnLimb[] { pc.leftHand, pc.rightHand, pc.leftFoot, pc.rightFoot };
  var names = new string[] { "LH", "RH", "LF", "RF" };
  for (int i = 0; i < 4; i++) {
    var l = limbs[i];
    if (l == null) { sb.Append(" | " + names[i] + "=null"); continue; }
    long p = (long)l.Pointer;
    int staState = System.Runtime.InteropServices.Marshal.ReadInt32((System.IntPtr)(p + 0x3B4));
    sb.Append(" | " + names[i]
      + " st=" + l.CurrentState.ToString()
      + " hold=" + l.IsHolding
      + " ratio=" + l.StaminaRatio.ToString("F2")
      + " spd=" + l.CurrentStaminaConsumptionSpeed.ToString("F2")
      + " staSt=" + staState);
  }
  return sb.ToString();
} catch (System.Exception e) { return "err:" + e.Message; }
