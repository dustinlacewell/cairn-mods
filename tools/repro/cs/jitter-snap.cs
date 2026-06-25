// jitter-snap.cs — diagnose the post-revive reaching-limb jitter ROOT (target oscillation).
// Arm AFTER a revive leaves a limb stuck in Reaching. Auto-binds to whichever limb is Reaching and,
// once per frame, records the IK target transform's WORLD position, LOCAL position, and PARENT — the
// single decisive read: if localPosition oscillates → a direct write to this transform (the writer is
// named separately); if localPosition is stable but world oscillates → the PARENT bone is moving
// (parent name identifies it). Read results from Bag["jittersnaprows"].
//
// Established this session (re/systems/coop/BUG-reaching-limb-jitter.md): the target oscillates between
// two fixed points, already flipped before the limb's EarlyUpdate, and NO ClimbingV2PawnLimb /
// ClimbingPawnLimbCairnIK method writes it — so the writer is OUTSIDE that pipeline. This probe localizes
// it to local-write vs parent-move. (Survival stat max=100; lock survival before relying on the stuck reach.)
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Il2Cpp;
using HarmonyLib;

public static class JitterSnap {
  public static ClimbingV2PawnController ctrl;
  public static List<string> rows = new List<string>();
  public static int max = 60;
  public static int lastFrame = -1;
  static ClimbingV2PawnLimb Reaching(){
    foreach (var l in new[]{ctrl.leftHand, ctrl.rightHand, ctrl.leftFoot, ctrl.rightFoot})
      if (l != null && (int)l.CurrentState == 3) return l;
    return null;
  }
  public static void EU(ClimbingV2PawnLimb __instance){
    try {
      var r = Reaching();
      if (r == null || __instance.Pointer != r.Pointer) return;
      if (Time.frameCount == lastFrame || rows.Count >= max) return;
      lastFrame = Time.frameCount;
      var rt = r.limbIK.target.realTransform;
      Vector3 wp = rt.position, lp = rt.localPosition;
      var par = rt.parent;
      string pn = par != null ? par.name : "<root>";
      Vector3 pp = par != null ? par.position : Vector3.zero;
      rows.Add(Time.frameCount + " wp.y=" + wp.y.ToString("F4") + " lp.y=" + lp.y.ToString("F4")
        + " parent=" + pn + " parent.y=" + pp.y.ToString("F4"));
    } catch (Exception e) { if(rows.Count<max) rows.Add("ERR "+e.Message); }
  }
}

var ctrl = MoSingleton<PawnManager>.Instance.ClimbingPawnController;
if (ctrl == null) return "NO CTRL";
JitterSnap.ctrl = ctrl;
JitterSnap.rows.Clear();
JitterSnap.lastFrame = -1;
var id = "jittersnap";
if (CairnDevTools.Eval.Globals.Bag.ContainsKey(id)) {
  ((HarmonyLib.Harmony)CairnDevTools.Eval.Globals.Bag[id]).UnpatchSelf();
  CairnDevTools.Eval.Globals.Bag.Remove(id);
}
var h = new HarmonyLib.Harmony(id);
CairnDevTools.Eval.Globals.Bag[id] = h;
CairnDevTools.Eval.Globals.Bag["jittersnaprows"] = JitterSnap.rows;
var bf = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
h.Patch(typeof(ClimbingV2PawnLimb).GetMethod("EarlyUpdate", bf),
        postfix: new HarmonyMethod(typeof(JitterSnap).GetMethod("EU", BindingFlags.Public|BindingFlags.Static)));
return "JITTER-SNAP ARMED (auto-bind reaching limb)";
