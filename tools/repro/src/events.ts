// Edge-triggered event signaling client with a DURABLE consumer offset. The game (CairnCoop trace patches +
// CairnDevTools `emit`) pushes named events onto the EventBus; CairnDevTools serves them at
// GET /events?since=<seq>&name=<f>&timeout=<ms>, which returns the first buffered event past `since`
// IMMEDIATELY if one exists (gap replay), else BLOCKS until the next one or the timeout.
//
// The cursor is persisted to disk (see cursor.ts), NOT held in process memory — so an event emitted while no
// consumer process is alive (between the LLM's inferences) is replayed to the next process from the bus's ring
// buffer rather than lost. The bus reports `oldest` (its oldest buffered seq) on every reply; if our cursor is
// below that, events were evicted before we consumed them — we surface that as a gap rather than skipping
// silently. With events seconds apart and a 256-deep buffer, eviction never happens in practice; it fails loud.

import { ConsoleDownError } from "./eval.ts";
import { readCursor, resetCursor, writeCursor } from "./cursor.ts";

export interface GameEvent {
  seq: number;
  name: string;
  payload: string;
  ts: number;
}

interface EventsReply {
  seq?: number;
  name?: string;
  payload?: string;
  ts?: number;
  oldest?: number;
  timeout?: boolean;
  error?: string;
}

/** Thrown when our durable cursor is below the bus's oldest buffered seq — events were evicted before we
 *  consumed them (a real gap). Never silently skipped. */
export class EventGapError extends Error {}

/** Reset a channel's durable cursor so the next await seeds from the server's current seq (call at run start). */
export function resetEventCursor(port: number, channel = "default"): void {
  resetCursor(port, channel);
}

/**
 * Block until the next event named `name` (or any event if null) arrives on `port`, or until `timeoutMs`
 * elapses. Uses the DURABLE per-(port,channel) cursor: replays any event emitted since we last consumed one
 * (including across process restarts / inference gaps), then long-polls for new ones. Returns the event, or
 * null on timeout. Advances + persists the cursor to each event's seq as it is consumed. Throws EventGapError
 * if the cursor fell behind the bus's ring buffer (events lost to eviction).
 */
export async function awaitEvent(
  port: number,
  name: string | null,
  timeoutMs: number,
  channel = "default",
  segmentMs = 60000,
): Promise<GameEvent | null> {
  const deadline = Date.now() + timeoutMs;

  // Seed the durable cursor on first-ever use so we only get events from now on (not the whole ring buffer).
  let since = readCursor(port, channel);
  if (since === null) {
    since = await currentSeq(port, segmentMs);
    writeCursor(port, since, channel);
  }

  while (Date.now() < deadline) {
    const remaining = Math.min(segmentMs, deadline - Date.now());
    const reply = await pollEvents(port, since, name, remaining);

    // Overflow guard: a non-empty buffer whose oldest seq is past our cursor means we missed events.
    if (typeof reply.oldest === "number" && reply.oldest > 0 && since < reply.oldest - 1) {
      throw new EventGapError(
        `event gap on :${port}/${channel}: cursor at ${since} but bus oldest is ${reply.oldest} — ` +
        `${reply.oldest - 1 - since} event(s) evicted before consumption.`,
      );
    }

    if (reply.timeout) {
      // No matching event this segment; advance past skipped (name-filtered) events the server stepped over.
      if (typeof reply.seq === "number") {
        since = Math.max(since, reply.seq);
        writeCursor(port, since, channel);
      }
      continue;
    }
    if (reply.error) throw new Error(`/events error on :${port}: ${reply.error}`);
    if (typeof reply.seq === "number") {
      since = reply.seq;
      writeCursor(port, since, channel); // persist BEFORE returning — a crash post-return won't re-deliver
      return { seq: reply.seq, name: reply.name ?? "", payload: reply.payload ?? "", ts: reply.ts ?? 0 };
    }
  }
  return null;
}

/** The server's current high-water seq (so a first await only sees events after now). */
async function currentSeq(port: number, requestTimeoutMs: number): Promise<number> {
  const reply = await pollEvents(port, Number.MAX_SAFE_INTEGER, null, 0, requestTimeoutMs);
  return typeof reply.seq === "number" ? reply.seq : 0;
}

/** One long-poll request. `serverTimeoutMs` is how long the SERVER blocks; the fetch abort is a bit longer. */
async function pollEvents(
  port: number,
  since: number,
  name: string | null,
  serverTimeoutMs: number,
  requestTimeoutMs = serverTimeoutMs + 10000,
): Promise<EventsReply> {
  const qs = new URLSearchParams({ since: String(since), timeout: String(serverTimeoutMs) });
  if (name) qs.set("name", name);
  const url = `http://127.0.0.1:${port}/events?${qs.toString()}`;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), requestTimeoutMs);
  try {
    const res = await fetch(url, { method: "GET", signal: ctrl.signal });
    const text = (await res.text()).trim();
    return JSON.parse(text) as EventsReply;
  } catch (e) {
    if (e instanceof Error && e.name === "AbortError") return { timeout: true };
    throw new ConsoleDownError(`cannot reach ${url}: ${e instanceof Error ? e.message : String(e)}`);
  } finally {
    clearTimeout(timer);
  }
}
