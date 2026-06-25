#!/usr/bin/env tsx
// Autonomous local co-op edelweiss-fall flight-recorder.
//
// Launches two Cairn instances, freezes survival, gets both into free solo (continue-save jump), links the
// co-op rope, then EXHAUSTS both climbers on the game's own stamina-eject path into a real fall → the
// edelweiss prompt, snapshots FULL state on both at the prompt + logs each side's CanRewind verdict /
// IsInRoom / clientState (the co-op rewind-refusal diagnostic), ACCEPTS the prompt the way the player does,
// then snapshots + diffs again after the revive resolves. The diffs + the per-death CanRewind reason are the
// bug evidence. Each attempt loops automatically. No human input after launch.
//
//   tsx src/cli.ts                          # full run: launch, set up, loop up to 20 attempts
//   tsx src/cli.ts --no-launch              # instances already in free-solo gameplay; skip launch
//   tsx src/cli.ts --max 5                  # cap attempts
//   tsx src/cli.ts --port-host 14200 --port-joiner 14201
//
// IMPORTANT: this DRIVES live game instances and a bad eval can hang one. Run it yourself when the
// instances are free.

import { evalCs } from "./eval.ts";
import { resetEventCursor } from "./events.ts";
import { RunLog } from "./log.ts";
import { killCairn } from "./proc.ts";
import { captureBoth, sideBySide, type Snapshot } from "./snapshot.ts";
import * as cs from "./snippets.ts";
import {
  acceptPrompts,
  armEdelweiss,
  beginRun,
  bringUp,
  enterFreeSolo,
  exhaustClimbers,
  freezeSurvival,
  linkRope,
  markRunStart,
  resetStaminaDrain,
  waitInRoom,
  type Instance,
} from "./steps.ts";
import { poll, PollTimeout, sleep } from "./wait.ts";

interface Args {
  max: number;
  launch: boolean;
  portHost: number;
  portJoiner: number;
  watchSeconds: number;
}

function parseArgs(argv: string[]): Args {
  const a: Args = { max: 20, launch: true, portHost: 14200, portJoiner: 14201, watchSeconds: 30 };
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i];
    const val = () => argv[++i];
    switch (flag) {
      case "--max": a.max = Number.parseInt(val(), 10); break;
      case "--no-launch": a.launch = false; break;
      case "--port-host": a.portHost = Number.parseInt(val(), 10); break;
      case "--port-joiner": a.portJoiner = Number.parseInt(val(), 10); break;
      case "--watch-seconds": a.watchSeconds = Number.parseInt(val(), 10); break;
      case "--help": case "-h": printHelp(); process.exit(0);
      default: throw new Error(`unknown flag: ${flag}`);
    }
  }
  return a;
}

function printHelp(): void {
  process.stdout.write(
    [
      "repro — autonomous local co-op edelweiss-revive flight-recorder",
      "",
      "  --max <n>            max revive attempts before stopping (default 20)",
      "  --no-launch          skip launch; assume both instances already in free-solo gameplay",
      "  --port-host <n>      host console port (default 14200)",
      "  --port-joiner <n>    joiner console port (default 14201)",
      "  --watch-seconds <n>  per-phase wait window (default 30)",
      "  -h, --help           this help",
      "",
    ].join("\n"),
  );
}

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));
  const log = new RunLog();
  const host: Instance = { who: "host", port: args.portHost };
  const joiner: Instance = { who: "joiner", port: args.portJoiner };
  const insts = [host, joiner];

  log.line(`repro start — runs/${log.dir.split(/[\\/]/).pop()}  max=${args.max}`);

  if (args.launch) {
    log.line("killing any running Cairn before launch…");
    await killCairn();
    await sleep(2000);
    // Fresh run: forget the driver's durable event cursors so we don't replay a prior run's events. (The new
    // instances start their EventBus seq at 0 anyway, but a stale cursor file from a prior run could sit above
    // it; reset re-seeds at the new server's current seq on first await.)
    for (const inst of insts) resetEventCursor(inst.port, "driver");
    // SEQUENTIAL bring-up with DISTINCT free-solo nonce saves. Each instance selects a FREE-SOLO nonce save
    // (never the real/story save — the IsStoryDisabled filter excludes it) as its active slot, then
    // continues. The joiner picks a slot DIFFERENT from the host's: two instances continuing the SAME save
    // wedge the 2nd at MainMenu step=16 forever (the save opens FileShare.None / exclusive, held by the host
    // all session; proven in re/systems/save-load/). Host-then-joiner ordering also lets the host's pawn
    // exist before the joiner's autojoin forms the co-op room.
    beginRun();
    await bringUp(host, log);
    await freezeSurvival(host, log);
    const hostSlot = await enterFreeSolo(host, log); // host takes any free-solo nonce save
    await bringUp(joiner, log);
    await freezeSurvival(joiner, log);
    await enterFreeSolo(joiner, log, [hostSlot]);     // joiner takes a DIFFERENT free-solo nonce save
  } else {
    log.line("STEP 0: --no-launch; using already-running instances (assumed already in free-solo gameplay).");
    markRunStart(0); // pick up the most recent per-instance logs regardless of when they started
    for (const inst of insts) await freezeSurvival(inst, log);
  }

  // Both climbers now have live pawns. The co-op room forms once both are registered + spawned; wait for
  // each instance's own "InRoom" log signal, then link the rope.
  await waitInRoom(insts, log);
  await linkRope(insts, log);
  // Arm infinite-edelweiss only now (pawns live, scene loaded) — never during load, which wedges it.
  await armEdelweiss(insts, log);

  const summary = await recordRevive(insts, args, log);

  await resetStaminaDrain(insts, log); // leave the game's stamina knobs at defaults

  log.line("──────────── SUMMARY ────────────");
  for (const row of summary) log.line(`  attempt ${row.attempt}: host=${row.host}  joiner=${row.joiner}`);
  log.line(`full snapshots + diffs written under: ${log.dir}`);
  log.line("  snap-host.log / snap-joiner.log — full aava-state + rewind-deep at each phase");
  log.line("  diff-prompt-N.log / diff-resolved-N.log — host-vs-joiner side by side, per attempt");
}

/** One revive-signal sample, parsed. */
interface Signal {
  isAuto: boolean; // edelweiss auto-rewind active (prompt up / scrubbing)
  inRez: boolean; // resurrection sequence active
  dead: boolean; // currently dead
  hp: number; // current HP
  module: string; // current module name
  inRoom: string; // native NetplayManager.IsInRoom
  canRewind: string; // CanRewind verdict at this instant
  reason: string; // RefusedRewindReason
  raw: string;
}

function parseSignal(line: string): Signal {
  const kv = new Map<string, string>();
  for (const tok of line.split(/\s+/)) {
    const eq = tok.indexOf("=");
    if (eq > 0) kv.set(tok.slice(0, eq), tok.slice(eq + 1));
  }
  return {
    isAuto: kv.get("isAuto") === "True",
    inRez: kv.get("inRez") === "True",
    dead: kv.get("dead") === "True",
    hp: Number.parseFloat(kv.get("hp") ?? "NaN"),
    module: kv.get("module") ?? "?",
    inRoom: kv.get("inRoom") ?? "?",
    canRewind: kv.get("canRewind") ?? "?",
    reason: kv.get("reason") ?? "?",
    raw: line,
  };
}

async function sample(inst: Instance, log: RunLog): Promise<Signal> {
  const line = await evalCs(inst.port, cs.REVIVE_SIGNAL).catch(
    (e) => `isAuto=err(${e instanceof Error ? e.message : e})`,
  );
  log.state(inst.who, line);
  return parseSignal(line);
}

/** True once the edelweiss prompt / resurrection is up (kill raises it within ~1 frame). */
const promptUp = (s: Signal) => s.isAuto || s.inRez || s.dead;
/** True once control has been handed back (no prompt, no resurrection). */
const resolved = (s: Signal) => !s.isAuto && !s.inRez;
/** A climber that recovered onto the wall vs ground-recovered: module name is the cheap tell. */
const recoveredClimbing = (m: string) => /climb|rappel|hang|idle|rope|ascend/i.test(m);
/** Fully recovered + stable: alive, HP restored, settled on a climbing module. Gate the NEXT kill on this
 *  so we never kill a still-falling/half-revived pawn (that degrades it into a dead-limbo no-prompt state). */
const settled = (s: Signal) => !s.dead && !s.inRez && !s.isAuto && s.hp > 50 && recoveredClimbing(s.module);

interface AttemptRow {
  attempt: number;
  host: string;
  joiner: string;
}

/**
 * The flight-recorder loop. Per attempt: KILL both on the game's death path → wait for the edelweiss prompt
 * → snapshot BOTH + log each side's CanRewind reason / IsInRoom / clientState (the 80/20 diagnostic) →
 * ACCEPT the prompt → wait for the revive to resolve → snapshot + diff again, and classify each side's
 * outcome by its resolved module (climbing-on-wall vs grounded). Loops --max times to collect the
 * success/ground-recovery split. Each attempt's diffs are written with the attempt number.
 */
async function recordRevive(insts: Instance[], args: Args, log: RunLog): Promise<AttemptRow[]> {
  const rows: AttemptRow[] = [];
  for (let attempt = 1; attempt <= args.max; attempt++) {
    log.line(`──────────── ATTEMPT ${attempt}/${args.max} ────────────`);
    // Before killing, make sure both climbers are fully recovered + stable on the wall (attempt 1: fresh
    // pawns already are). Killing a still-falling/half-revived pawn degrades it into a no-prompt dead-limbo.
    if (attempt > 1) {
      const stable = await waitFor(insts, settled, "settle", args.watchSeconds, log);
      if (!stable) log.line(`ATTEMPT ${attempt}: climbers not fully settled within ${args.watchSeconds}s — exhausting anyway.`);
    }
    await exhaustClimbers(insts, log);

    const sawPrompt = await waitFor(insts, promptUp, "prompt", args.watchSeconds, log);
    if (!sawPrompt) {
      log.line(`ATTEMPT ${attempt}: no edelweiss prompt within ${args.watchSeconds}s — retrying.`);
      rows.push({ attempt, host: "NO-PROMPT", joiner: "NO-PROMPT" });
      continue;
    }

    // At-prompt: snapshot + record the per-side CanRewind verdict (this is the divergence we're hunting).
    log.line("PROMPT UP — capturing full state + CanRewind verdict on both instances…");
    await captureAndDiff(insts, `prompt-${attempt}`, log);
    for (const inst of insts) {
      const s = await sample(inst, log);
      log.line(`  ${inst.who} @prompt: canRewind=${s.canRewind} reason=${s.reason} inRoom=${s.inRoom}`);
    }

    // Accept the prompt the way the player does, then watch the revive resolve.
    await acceptPrompts(insts, log);
    const handedBack = await waitFor(insts, resolved, "resolved", args.watchSeconds, log);
    log.line(handedBack ? "REVIVE RESOLVED — capturing post-revive state…" : "revive did NOT resolve in window — capturing anyway…");
    await captureAndDiff(insts, `resolved-${attempt}`, log);

    // Classify each side's outcome by its resolved module (recovered-on-wall vs ground/other).
    const row: AttemptRow = { attempt, host: "?", joiner: "?" };
    for (const inst of insts) {
      const s = await sample(inst, log);
      const outcome = recoveredClimbing(s.module) ? `RECOVERED(${s.module})` : `GROUND/OTHER(${s.module})`;
      if (inst.who === "host") row.host = outcome; else row.joiner = outcome;
    }
    log.line(`ATTEMPT ${attempt}: host=${row.host}  joiner=${row.joiner}`);
    rows.push(row);
  }
  return rows;
}

/** Poll both instances' revive signal until `cond` holds for EVERY instance, or the window elapses. */
async function waitFor(
  insts: Instance[],
  cond: (s: Signal) => boolean,
  what: string,
  seconds: number,
  log: RunLog,
): Promise<boolean> {
  try {
    await poll(
      async () => Promise.all(insts.map((i) => sample(i, log))),
      (sigs) => sigs.every(cond),
      {
        timeoutMs: seconds * 1000,
        intervalMs: 400,
        onTick: (sigs, ms) =>
          log.line(`  wait[${what}] @${ms}ms  ` + insts.map((i, k) => `${i.who}:{${sigs[k].raw}}`).join("  ")),
      },
    );
    return true;
  } catch (e) {
    if (e instanceof PollTimeout) return false;
    throw e;
  }
}

/** Snapshot both instances at one phase, write each dump, and emit a host-vs-joiner side-by-side diff. */
async function captureAndDiff(insts: Instance[], phase: string, log: RunLog): Promise<void> {
  const snaps = await captureBoth(insts, phase, log);
  const host = snaps.find((s) => s.who === "host");
  const joiner = snaps.find((s) => s.who === "joiner");
  if (host && joiner) log.diff(phase, sideBySide(host, joiner));
}

main().catch((e) => {
  process.stderr.write(`\nrepro failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
