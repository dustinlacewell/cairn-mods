#!/usr/bin/env tsx
// setup-n, race-free: stand up N local Cairn instances in one co-op room using the CairnDevTools async-eval
// `Game.WaitFor` API instead of sleep-poll. The old setup-n raced the free-solo PREVIEW CACHE (it fills
// asynchronously at boot) and intermittently failed with "no distinct free-solo save appeared", leaving a
// half-launched room (a joiner stuck at the menu). Here each instance is driven menu→save-select→gameplay in a
// SINGLE blocking POST whose every step waits on a LEVEL (menu present, preview cache filled, pawn spawned,
// fully loaded), so it can't miss a transition. The co-op room is likewise awaited on the live InCoopSession
// level, not an edge event.
//
//   tsx src/setup-n2.ts                 # 3 instances
//   tsx src/setup-n2.ts --instances 4
//   tsx src/setup-n2.ts --no-rope       # form the room, skip rope (chain code drives its own) — DEFAULT here

import { evalCs } from "./eval.ts";
import { enterFreeSoloScript, POLL_GAMEPLAY } from "./enter-freesolo.ts";
import { RunLog } from "./log.ts";
import { killCairn, launchCairn } from "./proc.ts";
import { roleOf, indexOf, waitForConsole, freezeSurvival, type Instance } from "./steps.ts";
import { sleep } from "./wait.ts";

function parseInstances(argv: string[]): number {
  const i = argv.indexOf("--instances");
  if (i >= 0 && argv[i + 1]) return Math.max(2, Number.parseInt(argv[i + 1], 10));
  return 3;
}

function buildInstances(n: number): Instance[] {
  const out: Instance[] = [];
  for (let k = 0; k < n; k++)
    out.push({ who: k === 0 ? "host" : `joiner${k}`, port: 14200 + k, role: k === 0 ? "host" : "joiner", index: k + 1 });
  return out;
}

/** Select a distinct free-solo save + fire the jump (one WaitFor POST), then poll WaitForGameplay in short
 *  sub-28s POSTs until the pawn has spawned + the load finished (a long same-profile continue exceeds the 28s
 *  per-script reply cap, so the gameplay wait is chunked). Returns the chosen slot. */
async function enterFreeSolo(inst: Instance, log: RunLog, avoid: number[]): Promise<number> {
  const fired = (await evalCs(inst.port, enterFreeSoloScript(avoid), 60_000).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
  log.line(`STEP 1: ${inst.who} select+jump → ${fired}`);
  const m = /slot=(\d+)/.exec(fired);
  if (!fired.startsWith("jump-fired") || !m)
    throw new Error(`STEP 1: ${inst.who} failed to fire the free-solo jump: ${fired}`);

  // Poll gameplay readiness in short windows (each ≤20s wait + reply margin) until spawned + GameStarted.
  for (let attempt = 0; attempt < 8; attempt++) {
    const r = (await evalCs(inst.port, POLL_GAMEPLAY, 26_000).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
    if (r === "gameplay-ready") {
      log.line(`STEP 1: ${inst.who} in free-solo gameplay (slot ${m[1]}).`);
      return Number(m[1]);
    }
    log.state(inst.who, `gameplay-poll ${attempt}: ${r}`);
  }
  throw new Error(`STEP 1: ${inst.who} never reached gameplay after firing the jump (slot ${m[1]})`);
}

/** Wait for one instance's co-op session on the LIVE level (InCoopSession), via a small WaitFor POST. */
async function waitInRoom(inst: Instance, log: RunLog): Promise<void> {
  const script = `try {
    await Game.WaitFor(() => { try { return CairnCoop.Core.Instance.Driver.InCoopSession; } catch { return false; } }, 120000);
    return "in-room";
  } catch (System.Exception e) { return "err:" + e.Message; }`;
  const r = (await evalCs(inst.port, script, 130_000).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
  log.line(`STEP 2: ${inst.who} room → ${r}`);
  if (r !== "in-room") throw new Error(`STEP 2: ${inst.who} never entered the co-op room: ${r}`);
}

async function main(): Promise<void> {
  const argv = process.argv.slice(2);
  const n = parseInstances(argv);
  const log = new RunLog();
  const insts = buildInstances(n);
  log.line(`setup-n2: ${n} instances (${insts.map((i) => `${i.who}:${i.port}`).join(", ")}) — WaitFor-driven, race-free`);

  log.line("killing any running Cairn before launch…");
  await killCairn();
  await sleep(2000);

  // SEQUENTIAL bring-up: host fully in gameplay before the next joiner continues (SavegameManager.CanLoad
  // serialises the same-profile continue). Each instance picks a distinct free-solo nonce slot (avoid-set).
  const taken: number[] = [];
  for (const inst of insts) {
    log.line(`STEP 0: launching ${inst.who}…`);
    await launchCairn(roleOf(inst), indexOf(inst));
    await waitForConsole(inst, log, 120_000);
    log.line(`STEP 0: ${inst.who} console up.`);
    await freezeSurvival(inst, log);
    const slot = await enterFreeSolo(inst, log, [...taken]);
    taken.push(slot);
  }

  // The co-op room forms once all are in free solo (autohost/autojoin). Await each on the live session level.
  for (const inst of insts) await waitInRoom(inst, log);

  log.line("──────────── READY ────────────");
  log.line(`${n} instances up in one room. Console ports: ${insts.map((i) => i.port).join(", ")}.`);
  log.line("Instances LEFT RUNNING.");
}

main().catch((e) => {
  process.stderr.write(`\nsetup-n2 failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
