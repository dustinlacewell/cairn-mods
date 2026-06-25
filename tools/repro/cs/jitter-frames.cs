// jitter-frames.cs — SAFE per-frame recorder for the on-wall jitter window (no global patches, no stack
// traces — those crash the instance). Run on a freshly-broken on-wall climber. In ONE eval: induce a reach
// on the RIGHT HAND, arm a per-frame sampler on the local limb (EarlyUpdate postfix) recording target+bone+
// gap, and arm CHEAP postfixes on the NARROW climber-level candidate writers (RewindController.Update /
// ReinjectCurrentSkeletonInClimber) to record IF they fire per-frame. The Harmony patches record across the
// ~60-frame window autonomously even if the climber falls after a second. Read Bag["jf_frames"] / ["jf_who"].
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Il2Cpp;
using HarmonyLib;

public static class JF {
  public static IntPtr limbPtr;
  public static List<string> frames = new List<string>();
  public static List<string> who = new List<string>();   // climber-level methods that fired (per frame)
  public static int max = 90;

  public static void Frame(ClimbingV2PawnLimb __instance){
    if (__instance.Pointer != limbPtr || frames.Count >= max) return;
    try {
      var t = __instance.CurrentIKTargetPosition; var b = __instance.CurrentEndNodePosition;
      frames.Add(Time.frameCount + " st=" + (int)__instance.CurrentState
        + " tgt=(" + t.x.ToString("F4")+","+t.y.ToString("F4")+","+t.z.ToString("F4")+")"
        + " bone=(" + b.x.ToString("F4")+","+b.y.ToString("F4")+","+b.z.ToString("F4")+")"
        + " gap=" + (t-b).magnitude.ToString("F4"));
    } catch {}
  }
  static void Mark(string m){ if (who.Count < max) who.Add(Time.frameCount + " " + m); }
  public static void Reinject(){ Mark("ReinjectCurrentSkeletonInClimber"); }
  // Align writes target ← poseHandler.Transform (≈ the bone) directly. Filter to the local climber's limbs.
  public static IntPtr lh, rh, lf, rf;
  public static void Align(ClimbingV2PawnLimb __instance){
    var p = __instance.Pointer;
    string n = p==lh?"LH":p==rh?"RH":p==lf?"LF":p==rf?"RF":"";
    if (n != "") Mark("Align[" + n + "]");
  }
}

var ctrl = MoSingleton<PawnManager>.Instance.ClimbingPawnController;
var rh = ctrl.rightHand;
ctrl.StartReachingWithLimb(rh);
JF.limbPtr = rh.Pointer;
JF.lh = ctrl.leftHand.Pointer; JF.rh = rh.Pointer; JF.lf = ctrl.leftFoot.Pointer; JF.rf = ctrl.rightFoot.Pointer;
JF.frames.Clear(); JF.who.Clear();

var id = "jf";
if (CairnDevTools.Eval.Globals.Bag.ContainsKey(id)) {
  ((HarmonyLib.Harmony)CairnDevTools.Eval.Globals.Bag[id]).UnpatchSelf();
  CairnDevTools.Eval.Globals.Bag.Remove(id);
}
var h = new HarmonyLib.Harmony(id);
CairnDevTools.Eval.Globals.Bag[id] = h;
CairnDevTools.Eval.Globals.Bag["jf_frames"] = JF.frames;
CairnDevTools.Eval.Globals.Bag["jf_who"] = JF.who;
var bf = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
h.Patch(typeof(ClimbingV2PawnLimb).GetMethod("EarlyUpdate", bf),
        postfix: new HarmonyMethod(typeof(JF).GetMethod("Frame", BindingFlags.Public|BindingFlags.Static)));
// narrow climber-level candidate writers (cheap — postfix just marks the frame). Align writes target←bone.
try { h.Patch(typeof(RewindController).GetMethod("ReinjectCurrentSkeletonInClimber", bf),
        postfix: new HarmonyMethod(typeof(JF).GetMethod("Reinject", BindingFlags.Public|BindingFlags.Static))); } catch {}
try { h.Patch(typeof(ClimbingV2PawnLimb).GetMethod("AlignIKTargetPositionOnCurrentLimbTransform", bf),
        postfix: new HarmonyMethod(typeof(JF).GetMethod("Align", BindingFlags.Public|BindingFlags.Static))); } catch {}
return "JF ARMED on RH (state=" + rh.CurrentState + ")";
