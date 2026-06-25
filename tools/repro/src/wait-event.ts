#!/usr/bin/env tsx
// Block until a named game event fires on an instance's /events channel, then exit — a normal foreground
// CLI so the LLM (or any caller) reacts on RETURN instead of sleep-polling a log. Prints the event JSON and
// exits 0 on the event; prints "timeout" and exits 2 on timeout; exits 1 on transport/gap error.
//
//   tsx src/wait-event.ts --port 14201 --name revive-resolved --timeout 120000
//   tsx src/wait-event.ts --port 14200                       # next event of ANY name, default 60s
//   tsx src/wait-event.ts --port 14200 --reset               # re-seed the cursor at "now" (start of a run)
//
// DURABLE CURSOR: the last-consumed seq persists to disk (.events/<channel>.<port>.cursor), so an event the
// game emits BETWEEN two of the LLM's inferences (when no wait-event process is alive) is REPLAYED to the
// next call from the bus's ring buffer — not missed. Each call consumes the next event past the cursor and
// advances it. Use --channel to keep an independent offset from the repro driver (default: shared "default").

import { awaitEvent, EventGapError, resetEventCursor } from "./events.ts";

interface Args {
  port: number;
  name: string | null;
  timeout: number;
  channel: string;
  reset: boolean;
}

function parseArgs(argv: string[]): Args {
  const a: Args = { port: 14200, name: null, timeout: 60000, channel: "default", reset: false };
  for (let i = 0; i < argv.length; i++) {
    const val = () => argv[++i];
    switch (argv[i]) {
      case "--port": a.port = Number.parseInt(val(), 10); break;
      case "--name": a.name = val(); break;
      case "--timeout": a.timeout = Number.parseInt(val(), 10); break;
      case "--channel": a.channel = val(); break;
      case "--reset": a.reset = true; break;
      case "-h": case "--help":
        process.stdout.write("wait-event --port <n> [--name <event>] [--timeout <ms>] [--channel <c>] [--reset]\n");
        process.exit(0);
        break;
      default: throw new Error(`unknown arg: ${argv[i]}`);
    }
  }
  return a;
}

async function main(): Promise<void> {
  const a = parseArgs(process.argv.slice(2));
  if (a.reset) resetEventCursor(a.port, a.channel);
  const ev = await awaitEvent(a.port, a.name, a.timeout, a.channel);
  if (ev === null) {
    process.stdout.write(`timeout (no ${a.name ?? "event"} on :${a.port} within ${a.timeout}ms)\n`);
    process.exit(2);
  }
  process.stdout.write(JSON.stringify(ev) + "\n");
  process.exit(0);
}

main().catch((e) => {
  if (e instanceof EventGapError) {
    process.stderr.write(`gap: ${e.message}\n`);
    process.exit(3); // distinct: events were evicted before we consumed them (cursor fell behind)
  }
  process.stderr.write(`error: ${e instanceof Error ? e.message : String(e)}\n`);
  process.exit(1);
});
