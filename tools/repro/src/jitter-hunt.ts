// jitter-hunt.ts — drive fall→accept→resolve cycles on TWO already-set-up instances (roped + edelweiss on,
// survival frozen) and, after EACH revive resolves, read jitter-snap on both sides to catch the intermittent
// post-revive stuck-reaching limb (the IK-target jitter). Loops until a stuck reach is caught or maxCycles.
//
// Why a bespoke driver (not the main repro loop): the main loop re-does full co-op setup per attempt and
// never reads the post-revive limb state — the jitter is a transient stuck reach that must be read the
// moment the revive resolves. This reuses the repro's verbs (crank-stamina-drain / reach-to-exert / the
// accept handler / jitter-snap.cs) but adds the per-cycle post-revive read.
//
// Run from the package:  npm --prefix tools/repro run jitter-hunt -- [--cycles N]
import { evalCs, evalFile, cmd } from "./eval.ts";

const HOST = 14200, JOINER = 14201;
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

const ACCEPT = `
try {
  var pop = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.SpecialItemLootPopUp>(true);
  if (pop == null) return "no-popup";
  long p = (long)pop.Pointer;
  byte faded = System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)(p + 0xB8));
  byte accept = System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)(p + 0xB9));
  if (faded == 0 || accept == 0) return "not-ready(faded=" + faded + " accept=" + accept + ")";
  var m = pop.GetType().GetMethod("InputPrompt_OnInputDetected",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
  if (m == null) return "err:no accept handler";
  m.Invoke(pop, null);
  return "accepted";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// Limb states one-liner: "LH=Holding RH=Reaching ..." + a REACHING flag.
const LIMB_STATES = `
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no-pc";
  var sb = new System.Text.StringBuilder();
  bool anyReach = false;
  foreach (var t in new[]{ ("LH",pc.leftHand), ("RH",pc.rightHand), ("LF",pc.leftFoot), ("RF",pc.rightFoot) }) {
    var l = t.Item2; if (l == null) continue;
    sb.Append(t.Item1 + "=" + l.CurrentState + " ");
    if ((int)l.CurrentState == 3) anyReach = true;
  }
  return (anyReach ? "REACH " : "ok ") + sb.ToString().Trim();
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// Read jitter-snap rows (last 8) + count.
const READ_SNAP = `
try {
  if (!CairnDevTools.Eval.Globals.Bag.ContainsKey("jittersnaprows")) return "no-snap";
  var rows = (System.Collections.Generic.List<string>)CairnDevTools.Eval.Globals.Bag["jittersnaprows"];
  var sb = new System.Text.StringBuilder();
  sb.AppendLine("snaprows=" + rows.Count);
  int start = System.Math.Max(0, rows.Count - 8);
  for (int i = start; i < rows.Count; i++) sb.AppendLine(rows[i]);
  return sb.ToString();
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// The DISCRIMINATOR between a stable stuck-reach and the JITTER: the sustained target↔bone gap. A converged
// stuck-reach reads |tgt-bone|≈0.0001; a jittering one holds the target ~0.03-0.18 off the bone (and the
// target alternates). Returns the max |tgt-bone| seen over a short in-eval sample on the reaching limb.
const REACH_GAP = `
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no-pc";
  Il2Cpp.ClimbingV2PawnLimb r = null;
  foreach (var l in new[]{pc.leftHand, pc.rightHand, pc.leftFoot, pc.rightFoot})
    if (l != null && (int)l.CurrentState == 3) { r = l; break; }
  if (r == null) return "no-reach";
  var t = r.CurrentIKTargetPosition; var b = r.CurrentEndNodePosition;
  return "gap=" + (t-b).magnitude.ToString("F4");
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// Prompt-up signal (the SpecialItemLootPopUp exists).
const PROMPT_UP = `
try { return UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.SpecialItemLootPopUp>(true) != null ? "up" : "down"; }
catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// THE CORRECT PROBE (per JITTER-TASK.md): the jitter is a WHOLE-CLIMBER condition the revive sometimes
// leaves a climber in, where ANY limb jitters when it enters Reaching. So we don't wait for an organic
// stuck-reach — we INDUCE a reach on a named limb and measure whether it jitters. limbArg: 0=LH 1=RH 2=LF
// 3=RF. Forces the limb into Reaching via StartReachingWithLimb, samples |IK-target − end-bone| a few
// frames apart, and returns the max gap. A climber in the broken mode → gap >~0.02 on EVERY limb; a normal
// climber → ~0.0001 on every limb. (Survival must be frozen so the climber doesn't starve/clear meanwhile.)
const induceReach = (limbArg: number) => `
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no-pc";
  var limbs = new[]{ pc.leftHand, pc.rightHand, pc.leftFoot, pc.rightFoot };
  var limb = limbs[${limbArg}];
  if (limb == null) return "no-limb";
  pc.StartReachingWithLimb(limb);
  // sample the gap a few iterations (the eval blocks the main thread between reads only trivially; the IK
  // solve runs on the game loop, so consecutive return-reads here are same-frame — we instead report the
  // single post-induce gap and let the TS side re-call this verb across real frames for a time series).
  var t = limb.CurrentIKTargetPosition; var b = limb.CurrentEndNodePosition;
  return "state=" + limb.CurrentState + " gap=" + (t-b).magnitude.ToString("F4");
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// Read the current gap of a specific limb (no re-induce) — for the time series across real frames.
const limbGap = (limbArg: number) => `
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  var limb = new[]{ pc.leftHand, pc.rightHand, pc.leftFoot, pc.rightFoot }[${limbArg}];
  if (limb == null) return "no-limb";
  var t = limb.CurrentIKTargetPosition; var b = limb.CurrentEndNodePosition;
  return "state=" + limb.CurrentState + " gap=" + (t-b).magnitude.ToString("F4");
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// Whole-climber state in ONE read: ragdoll (IsRagdoll => Solve skips ALL limb IK), module, on-wall-ness.
const CLIMBER_STATE = `
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "no-pc";
  var rag = pc.ik != null ? pc.ik.ragdoll : null;
  int holding = 0;
  foreach (var l in new[]{pc.leftHand, pc.rightHand, pc.leftFoot, pc.rightFoot}) if (l != null && l.IsHolding) holding++;
  return "ragdoll=" + (rag != null ? rag.IsRagdoll.ToString() : "?")
    + " ragTimer=" + (rag != null ? rag.ragdollEnabledTimer.ToString("F2") : "?")
    + " module=" + (pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none")
    + " holding=" + holding + "/4";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

async function armSnap(): Promise<void> {
  for (const port of [HOST, JOINER]) await evalFile(port, "jitter-snap");
}
async function exhaustBoth(): Promise<void> {
  for (const port of [HOST, JOINER]) { await evalFile(port, "crank-stamina-drain"); await evalFile(port, "reach-to-exert"); }
}
async function resetDrain(): Promise<void> {
  for (const port of [HOST, JOINER]) await evalFile(port, "reset-stamina-drain").catch(() => {});
}

async function waitFor(fn: () => Promise<boolean>, timeoutMs: number, intervalMs = 600): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) { if (await fn().catch(() => false)) return true; await sleep(intervalMs); }
  return false;
}

async function oneCycle(n: number): Promise<boolean> {
  console.log(`\n──── cycle ${n} ────`);
  await armSnap(); // fresh rows each cycle
  await exhaustBoth();
  console.log("  exhausting both…");
  // wait for the edelweiss prompt
  const promptUp = await waitFor(async () => (await evalCs(HOST, PROMPT_UP)) === "up" || (await evalCs(JOINER, PROMPT_UP)) === "up", 40_000);
  if (!promptUp) { console.log("  no prompt within 40s — resetting drain, retrying"); await resetDrain(); return false; }
  console.log("  prompt up — accepting on both");
  // accept on both (retry until the fade gate opens)
  for (const port of [HOST, JOINER]) {
    await waitFor(async () => (await evalCs(port, ACCEPT)) === "accepted", 15_000, 500);
  }
  await resetDrain(); // stop draining so the revived climber is stable
  // wait for the revive to RESOLVE (popup gone)
  await waitFor(async () => (await evalCs(HOST, PROMPT_UP)) === "down" && (await evalCs(JOINER, PROMPT_UP)) === "down", 40_000);
  // WAIT FOR EACH CLIMBER TO SETTLE BACK ON THE WALL (ragdoll cleared, limbs Holding) before testing — a
  // climber still falling/ragdolling isn't the broken-mode-on-wall state. Log the ragdoll/holding timeline
  // (12s) so we SEE whether a broken climber ever clears ragdoll, or comes back stuck in it.
  for (const [who, port] of [["host", HOST], ["joiner", JOINER]] as const) {
    const tl: string[] = [];
    for (let k = 0; k < 12; k++) { tl.push(await evalCs(port, CLIMBER_STATE).catch((e) => `err:${e?.message}`)); await sleep(1000); }
    // dedupe consecutive identical lines for readability
    const dedup = tl.filter((l, i) => i === 0 || l !== tl[i - 1]);
    console.log(`  ${who} settle-timeline:\n${dedup.map((l) => "    " + l).join("\n")}`);
  }
  // Now TEST: on a climber that is on the wall (holding>=2, ragdoll cleared if it ever does), induce ONE
  // reach and read ragdoll IN THE SAME instant. The bug = sustained gap on a reaching limb. Capture ragdoll
  // AT the jitter moment (not seconds later, after the climber falls).
  let caught = false;
  for (const [who, port] of [["host", HOST], ["joiner", JOINER]] as const) {
    await evalCs(port, induceReach(1));
    const samples: { gap: number; reaching: boolean }[] = [];
    let ragAtJitter = "";
    for (let k = 0; k < 6; k++) {
      const g = await evalCs(port, limbGap(1));
      const m = g.match(/state=(\w+) gap=([\d.]+)/);
      const reaching = !!m && m[1] === "Reaching";
      const gap = m ? Number(m[2]) : 0;
      if (reaching && gap > 0.02 && gap < 2.0 && !ragAtJitter) ragAtJitter = await evalCs(port, CLIMBER_STATE); // ragdoll AT the jitter
      if (m) samples.push({ gap, reaching });
      await sleep(250);
    }
    const reaching = samples.filter((s) => s.reaching);
    const sane = reaching.filter((s) => s.gap < 2.0).map((s) => s.gap);
    const maxGap = sane.length ? Math.max(...sane) : 0;
    const isJitter = reaching.length >= 4 && sane.filter((g) => g > 0.02).length >= 3;
    const stateNow = await evalCs(port, CLIMBER_STATE);
    console.log(`  ${who}: induce-RH maxGap=${maxGap.toFixed(4)} reaching=${reaching.length}/6 | now: ${stateNow}`);
    if (!isJitter) continue;
    caught = true;
    console.log(`  ★ ${who} JITTERING — ragdoll AT jitter: ${ragAtJitter || "(not sampled)"}`);
    // IMMEDIATELY arm the safe per-frame recorder (induces RH + records target/bone/gap each frame + marks
    // any RewindController.Update/Reinject) — captures the on-wall jitter window before the climber falls.
    const armed = await evalFile(port, "jitter-frames");
    console.log(`    ${armed}`);
    await sleep(2500); // let the patches record ~60 frames
    const frames = await evalCs(port, `
try { var r=(System.Collections.Generic.List<string>)CairnDevTools.Eval.Globals.Bag["jf_frames"];
  var sb=new System.Text.StringBuilder(); sb.AppendLine("frames="+r.Count);
  int s=System.Math.Max(0,r.Count-20); for(int i=s;i<r.Count;i++) sb.AppendLine(r[i]); return sb.ToString();
} catch(System.Exception e){ return "err:"+e.Message; }`.trim());
    console.log(`  ${who} JF-FRAMES:\n${frames.split("\n").map((l) => "    " + l).join("\n")}`);
    const whoFired = await evalCs(port, `
try { var r=(System.Collections.Generic.List<string>)CairnDevTools.Eval.Globals.Bag["jf_who"];
  var sb=new System.Text.StringBuilder(); sb.AppendLine("who="+r.Count);
  int s=System.Math.Max(0,r.Count-12); for(int i=s;i<r.Count;i++) sb.AppendLine(r[i]); return sb.ToString();
} catch(System.Exception e){ return "err:"+e.Message; }`.trim());
    console.log(`  ${who} JF-WHO-FIRED:\n${whoFired.split("\n").map((l) => "    " + l).join("\n")}`);
    console.log(`  ${who}: BROKEN climber LEFT RUNNING (port ${port}).`);
  }
  return caught;
}

async function main(): Promise<void> {
  const cyclesArg = process.argv.indexOf("--cycles");
  const maxCycles = cyclesArg >= 0 ? Number(process.argv[cyclesArg + 1]) : 12;
  console.log(`jitter-hunt: up to ${maxCycles} fall→revive cycles, watching for a post-revive stuck reach`);
  for (let n = 1; n <= maxCycles; n++) {
    const caught = await oneCycle(n).catch((e) => { console.log(`  cycle error: ${e?.message ?? e}`); return false; });
    if (caught) { console.log(`\n★ STUCK REACH CAUGHT on cycle ${n} — jitter-snap captured above. Instances left running for deeper probing.`); return; }
  }
  console.log(`\nno stuck reach in ${maxCycles} cycles (the bug is ~20% — try more cycles).`);
}

main();
