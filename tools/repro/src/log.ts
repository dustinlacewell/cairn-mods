// Run logging: every line goes to stdout AND to a per-run file under tools/repro/runs/<timestamp>/run.log,
// so a repro that fires overnight leaves a full trace. Per-instance state lines also append to
// host.log / joiner.log for easy side-by-side diffing.

import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const RUNS_DIR = fileURLToPath(new URL("../runs/", import.meta.url));

export class RunLog {
  readonly dir: string;

  constructor(stamp = timestamp()) {
    this.dir = join(RUNS_DIR, stamp);
    mkdirSync(this.dir, { recursive: true });
  }

  /** A timestamped line to stdout + run.log. */
  line(msg: string): void {
    const stamped = `[${clock()}] ${msg}`;
    process.stdout.write(stamped + "\n");
    appendFileSync(join(this.dir, "run.log"), stamped + "\n");
  }

  /** A raw state line for one instance → its own file (no stdout echo; the caller decides what to print). */
  state(who: string, msg: string): void {
    appendFileSync(join(this.dir, `${who}.log`), `[${clock()}] ${msg}\n`);
  }

  /** A full-state snapshot for one instance at one phase → its own append-only file, easy to diff. */
  snapshot(who: string, phase: string, text: string): void {
    const header = `\n========== ${who} @ ${phase}  [${clock()}] ==========\n`;
    appendFileSync(join(this.dir, `snap-${who}.log`), header + text + "\n");
  }

  /** A host-vs-joiner side-by-side diff for one phase → its own file. */
  diff(phase: string, text: string): void {
    appendFileSync(join(this.dir, `diff-${phase}.log`), text + "\n");
    process.stdout.write(text + "\n");
  }
}

function timestamp(): string {
  return new Date().toISOString().replace(/[:.]/g, "-").replace("T", "_").slice(0, 19);
}

function clock(): string {
  return new Date().toISOString().slice(11, 23);
}
