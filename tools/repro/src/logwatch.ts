// Gate steps on the mod's OWN emitted signals instead of blind sleeps / fragile eval probes. Each Cairn
// instance writes its own per-instance log (MelonLoader/CairnCoop/<ts>_pid<pid>.log); the header line marks
// host vs joiner (autostart=True/ vs autostart=…/local). CairnCoop already logs the lifecycle we need:
//   "autostart: local"                         joiner pointed at the relay
//   "-- InRoom (native: IN_ROOM)"              co-op session live (the InCoopSession signal)
//   "-- Registering (native: REGISTERED)"      join handshake stalled here = a real failure to surface
//   "first frame from #N … climbing data is flowing"
//   "fall module: …"  /  "revive: …"            the fall + edelweiss markers
// We tail the right file and resolve when a line matches — event-driven, no arbitrary timing.

import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { poll, PollTimeout } from "./wait.ts";

const LOG_DIR = "P:\\Steam\\steamapps\\common\\Cairn\\MelonLoader\\CairnCoop";

export type Role = "host" | "joiner";

/** The newest per-instance log for `role` whose mtime is at/after `sinceMs` (this run's launch). */
export function findInstanceLog(role: Role, sinceMs: number): string | null {
  let best: { path: string; mtime: number } | null = null;
  let files: string[];
  try {
    files = readdirSync(LOG_DIR).filter((f) => f.endsWith(".log"));
  } catch {
    return null;
  }
  for (const f of files) {
    const path = join(LOG_DIR, f);
    let mtime: number;
    try {
      mtime = statSync(path).mtimeMs;
    } catch {
      continue;
    }
    if (mtime < sinceMs - 5_000) continue; // tolerate small clock skew
    let header: string;
    try {
      header = readFileSync(path, "utf8").split("\n", 1)[0] ?? "";
    } catch {
      continue;
    }
    const isHost = header.includes("autostart=True");
    const isJoiner = header.includes("/local") || (header.includes("autostart=False") && header.includes("local"));
    const matches = role === "host" ? isHost : isJoiner;
    if (!matches) continue;
    if (!best || mtime > best.mtime) best = { path, mtime };
  }
  return best?.path ?? null;
}

/** Read a log file's full text (empty string if unreadable). */
export function readLog(path: string): string {
  try {
    return readFileSync(path, "utf8");
  } catch {
    return "";
  }
}

/** Does the log already contain a line matching `re`? */
export function logHas(path: string, re: RegExp): boolean {
  return re.test(readLog(path));
}

export interface LogWaitResult {
  /** The full line that matched `want` (or the last line touched, on a `fail` hit). */
  line: string;
  /** True if `want` matched; false if a `fail` pattern matched first. */
  ok: boolean;
}

/**
 * Tail `path` until a line matches `want` (success) or `fail` (a known stall/error to surface early).
 * Returns the matched line. Throws PollTimeout if neither appears within the budget. `onTick` gets the
 * tail's last line each poll so the run log shows live progress.
 */
export async function waitForLog(
  path: string,
  want: RegExp,
  opts: { timeoutMs: number; intervalMs?: number; fail?: RegExp; onTick?: (lastLine: string, ms: number) => void },
): Promise<LogWaitResult> {
  const probe = async (): Promise<string> => readLog(path);
  let hit: LogWaitResult | null = null;
  try {
    await poll(
      probe,
      (text) => {
        const lines = text.split("\n");
        for (const ln of lines) {
          if (want.test(ln)) { hit = { line: ln.trim(), ok: true }; return true; }
          if (opts.fail && opts.fail.test(ln)) { hit = { line: ln.trim(), ok: false }; return true; }
        }
        return false;
      },
      {
        timeoutMs: opts.timeoutMs,
        intervalMs: opts.intervalMs ?? 1500,
        onTick: (text, ms) => {
          const lines = text.trimEnd().split("\n");
          opts.onTick?.(lines[lines.length - 1] ?? "", ms);
        },
      },
    );
  } catch (e) {
    if (e instanceof PollTimeout) throw e;
    throw e;
  }
  return hit!;
}
