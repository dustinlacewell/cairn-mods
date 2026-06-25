// Make ordinary climbing exhausting, so a load-bearing climber tires within seconds instead of minutes.
// These are the game's OWN stamina difficulty knobs (ClimbingTweakablesPackage.Stamina) — not a poke at a
// derived field — so the climber still exhausts via the real Tiring → drain → criticalStaminaTimer →
// Fall(Exhausted) path. Live-verified 2026-06-17: with these values + the climber moving/load-bearing, all
// holding limbs go Tiring (staSt=2), drain to ratio 0 in ~6s, then the 5s grace elapses and the climber
// falls off the wall (module → JumpDownFromWall / Falling).
//   handRestThreshold/footRestThreshold → ~0   : ANY effort tips the limb into the Tiring branch
//   handFreeEffortThreshold/footFreeEffortThreshold → ~0
//   staminaLossSpeed = 8 (16x default 0.5)      : drain hard
//   staminaGainSpeed = 0                         : no recovery while not bearing load
// Defaults (restored by reset-stamina-drain.cs): rest 0.1/0.3, freeEffort 0.3/0.5, loss 0.5, gain 0.05.
try {
  var g = Il2Cpp.GameDataManager.Instance;
  if (g == null) return "no GDM";
  var st = g.ClimbingPackage != null ? g.ClimbingPackage.stamina : null;
  if (st == null) return "no stamina pkg";
  st.handRestThreshold = 0.001f;
  st.footRestThreshold = 0.001f;
  st.handFreeEffortThreshold = 0.001f;
  st.footFreeEffortThreshold = 0.001f;
  st.staminaLossSpeed = 8.0f;
  st.staminaGainSpeed = 0.0f;
  return "stamina drain cranked (rest=0.001 freeEffort=0.001 loss=8 gain=0)";
} catch (System.Exception e) { return "err:" + e.Message; }
