#!/usr/bin/env tsx
// Stand up N local Cairn instances in ONE co-op room, then leave them running for hand/probe-driven
// testing (the N-climber chain work). NOT the revive flight-recorder (that's cli.ts) — this is just
// launch → free-solo each (distinct nonce saves) → wait the N-way room → link rope → STOP, instances up.
//
//   tsx src/setup-n.ts                 # 3 instances (host 14200, joiners 14201/14202)
//   tsx src/setup-n.ts --instances 4   # host + 3 joiners (14200..14203)
//   tsx src/setup-n.ts --no-rope       # form the room but skip the rope handshake (chain wiring is bespoke)
//
// Ports: console auto-probes 14200+; instance k (0-based) → 14200+k. Joiner ordinal (1-based) → k+1, which
// the mod reads as CAIRNCOOP_INSTANCE to bind its ModLoopback port (HostPort + ordinal-1). Each instance
// takes a DISTINCT free-solo nonce save (the same-save FileShare wedge needs N distinct saves on the profile).

import { resetEventCursor } from "./events.ts";
import { RunLog } from "./log.ts";
import { killCairn } from "./proc.ts";
import {
  beginRun,
  bringUp,
  enterFreeSolo,
  freezeSurvival,
  linkRope,
  waitInRoom,
  type Instance,
} from "./steps.ts";
import { sleep } from "./wait.ts";

interface Args {
  instances: number;
  rope: boolean;
}

function parseArgs(argv: string[]): Args {
  const a: Args = { instances: 3, rope: true };
  for (let i = 0; i < argv.length; i++) {
    switch (argv[i]) {
      case "--instances": a.instances = Number.parseInt(argv[++i], 10); break;
      case "--no-rope": a.rope = false; break;
      case "--help": case "-h":
        process.stdout.write("setup-n — stand up N local co-op instances and leave them running\n  --instances <n> (default 3)\n  --no-rope     form the room, skip rope handshake\n");
        process.exit(0);
        break;
      default: throw new Error(`unknown flag: ${argv[i]}`);
    }
  }
  if (a.instances < 2) throw new Error("--instances must be >= 2");
  return a;
}

/** Build the instance list: index 0 = host (port 14200, ordinal 1), the rest local joiners. */
function buildInstances(n: number): Instance[] {
  const out: Instance[] = [];
  for (let k = 0; k < n; k++) {
    const role = k === 0 ? "host" : "joiner";
    out.push({
      who: k === 0 ? "host" : `joiner${k}`,
      port: 14200 + k,
      role,
      index: k + 1, // 1-based ordinal: host=1, joiners 2,3,…
    });
  }
  return out;
}

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));
  const log = new RunLog();
  const insts = buildInstances(args.instances);
  log.line(`setup-n: ${args.instances} instances (${insts.map((i) => `${i.who}:${i.port}`).join(", ")})`);

  log.line("killing any running Cairn before launch…");
  await killCairn();
  await sleep(2000);
  for (const inst of insts) resetEventCursor(inst.port, "driver");

  // SEQUENTIAL bring-up. Host first (its pawn must exist before joiners autojoin), each on a DISTINCT
  // free-solo nonce save (avoid every slot already taken). The host must be fully in gameplay before the
  // next joiner continues (SavegameManager.CanLoad serializes), so we bring each up fully in turn.
  beginRun();
  const takenSlots: number[] = [];
  for (const inst of insts) {
    await bringUp(inst, log);
    await freezeSurvival(inst, log);
    const slot = await enterFreeSolo(inst, log, [...takenSlots]);
    takenSlots.push(slot);
  }

  // The co-op room forms once all are registered + spawned. Wait every instance's InRoom, then (optionally)
  // link the rope. NOTE: the rope handshake/state is still the 2-player bespoke layer — for N>2 the chain
  // wiring is the next phase; --no-rope stops here so the chain code can drive its own rope.
  await waitInRoom(insts, log);
  if (args.rope) {
    await linkRope(insts, log);
  } else {
    log.line("STEP 2: --no-rope; room formed, rope NOT linked (chain wiring drives its own).");
  }

  log.line("──────────── READY ────────────");
  log.line(`${args.instances} instances up in one room. Console ports: ${insts.map((i) => i.port).join(", ")}.`);
  log.line("Probe with: pwsh tools/eval.ps1 -Port <p> -File <script>.cs   (instances LEFT RUNNING)");
}

main().catch((e) => {
  process.stderr.write(`\nsetup-n failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
