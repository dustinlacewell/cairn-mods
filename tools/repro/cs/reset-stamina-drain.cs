// Restore the stamina difficulty knobs to their shipped defaults (live-read 2026-06-17). Pair with
// crank-stamina-drain.cs so a run can leave the game in a clean state.
try {
  var g = Il2Cpp.GameDataManager.Instance;
  if (g == null) return "no GDM";
  var st = g.ClimbingPackage != null ? g.ClimbingPackage.stamina : null;
  if (st == null) return "no stamina pkg";
  st.handRestThreshold = 0.100f;
  st.footRestThreshold = 0.300f;
  st.handFreeEffortThreshold = 0.300f;
  st.footFreeEffortThreshold = 0.500f;
  st.staminaLossSpeed = 0.500f;
  st.staminaGainSpeed = 0.050f;
  return "stamina drain reset to defaults";
} catch (System.Exception e) { return "err:" + e.Message; }
