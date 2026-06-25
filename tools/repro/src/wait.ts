// Polling primitives. The whole driver is "do a thing, then poll live state until it settles" — so the
// loop shape lives here once and the steps just supply the probe + the predicate.

export const sleep = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));

export interface PollOptions<T> {
  /** Total budget before giving up. */
  timeoutMs: number;
  /** Gap between attempts. */
  intervalMs: number;
  /** Called once per attempt with the latest probe value (for live logging). */
  onTick?: (value: T, elapsedMs: number) => void;
}

export class PollTimeout extends Error {
  readonly last: unknown;
  constructor(last: unknown) {
    super(`poll timed out; last value: ${typeof last === "string" ? last : JSON.stringify(last)}`);
    this.last = last;
  }
}

/**
 * Repeatedly run `probe`, feed its result to `done`, and return the value once `done` is true.
 * Probe errors are surfaced as a thrown PollTimeout only if they persist to the deadline — a probe that
 * throws transiently is retried (the instance may not be ready). A probe that wants its own error captured
 * (rather than retried) should catch internally and return a value `done` can read.
 */
export async function poll<T>(
  probe: () => Promise<T>,
  done: (value: T) => boolean,
  opts: PollOptions<T>,
): Promise<T> {
  const deadline = Date.now() + opts.timeoutMs;
  let last: T | undefined;
  let lastErr: unknown = "(no probe yet)";
  while (Date.now() < deadline) {
    const elapsed = opts.timeoutMs - (deadline - Date.now());
    try {
      last = await probe();
    } catch (e) {
      lastErr = e instanceof Error ? `${e.name}: ${e.message}` : String(e);
      await sleep(opts.intervalMs);
      continue;
    }
    opts.onTick?.(last, elapsed);
    if (done(last)) return last;
    await sleep(opts.intervalMs);
  }
  throw new PollTimeout(last ?? lastErr);
}
