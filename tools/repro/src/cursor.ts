// Durable consumer offset (Kafka-style) for the event stream. The problem this solves: a `wait-event`
// process dies between the LLM's inferences, so an in-memory `since` cursor is lost and any event the game
// emits in the gap would be silently skipped (the next process would seed `since` from "now" and block on an
// event that already fired). The fix is a persisted offset: the last seq we CONSUMED is written to a file;
// the next invocation passes since=<that>, and the server replays anything emitted in the gap from its ring
// buffer. The seq is monotonic + durable on the bus (ring buffer) and the offset is durable on the consumer
// (this file), so delivery is exactly-once-after-offset regardless of consumer liveness — the LLM's realtime
// latency between calls becomes irrelevant (it's just "consumer offline", which the offset handles).
//
// One cursor per (port, channel): the driver and the wait-event CLI can keep independent offsets by using
// different channel names if needed; default channel keeps them on the same offset (one logical consumer).

import { mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const DIR = fileURLToPath(new URL("../.events", import.meta.url));

function cursorPath(port: number, channel: string): string {
  return `${DIR}/${channel}.${port}.cursor`;
}

/** Read the persisted last-consumed seq for (port, channel). Returns null if no cursor exists yet (first use). */
export function readCursor(port: number, channel = "default"): number | null {
  try {
    const raw = readFileSync(cursorPath(port, channel), "utf8").trim();
    const n = Number.parseInt(raw, 10);
    return Number.isFinite(n) ? n : null;
  } catch {
    return null; // ENOENT (first use) or unreadable → treat as no cursor
  }
}

/** Atomically persist the last-consumed seq (temp file + rename, so a killed process can't corrupt it). */
export function writeCursor(port: number, seq: number, channel = "default"): void {
  mkdirSync(DIR, { recursive: true });
  const path = cursorPath(port, channel);
  const tmp = `${path}.tmp`;
  writeFileSync(tmp, String(seq), "utf8");
  renameSync(tmp, path);
}

/** Forget the cursor so the next await re-seeds from the server's current seq (use at the start of a run). */
export function resetCursor(port: number, channel = "default"): void {
  try { writeFileSync(cursorPath(port, channel), "", "utf8"); } catch { /* dir may not exist; fine */ }
}
