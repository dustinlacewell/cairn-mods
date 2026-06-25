// Transport to the CairnDevTools HTTP console (one per instance).
//
//   POST /cmd?q=eval  with the C# in the request BODY  → Roslyn eval, returns the `return` value as text.
//   GET  /cmd?q=<cmd>                                   → a registered console command (e.g. `survival on`).
//
// The body is RAW C# — never URL-encode it (the URL-encoded path locks the engine). The console wraps
// every reply in a trailing newline, so we trim. A not-yet-started instance refuses the connection; we
// treat that as transient and let callers retry via `poll`.

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

/** Connection refused / DNS / socket reset — the instance isn't accepting requests yet. */
export class ConsoleDownError extends Error {}

/** The eval compiled/ran but the game reported an error (compile error / exception / our own `err:` tag). */
export class EvalError extends Error {}

const ENDPOINT = (port: number) => `http://127.0.0.1:${port}/cmd`;

/** POST raw C# to the eval endpoint; returns the trimmed text reply. */
export async function evalCs(port: number, csharp: string, timeoutMs = 30000): Promise<string> {
  const url = `${ENDPOINT(port)}?q=eval`;
  const text = await post(url, csharp, timeoutMs);
  throwIfEvalError(text);
  return text;
}

/** Ship a verb that lives as a .cs file under tools/repro/cs/ (by stem, no extension) — same as
 *  `pwsh tools/eval.ps1 -File <stem>.cs`, just from the driver. No per-verb wrapper; the file IS the verb. */
export async function evalFile(port: number, stem: string, timeoutMs = 30000): Promise<string> {
  const path = fileURLToPath(new URL(`../cs/${stem}.cs`, import.meta.url));
  return evalCs(port, readFileSync(path, "utf8"), timeoutMs);
}

/** GET a registered console command (`q` is the command + args, e.g. "survival on"). */
export async function cmd(port: number, q: string, timeoutMs = 30000): Promise<string> {
  const url = `${ENDPOINT(port)}?q=${encodeURIComponent(q)}`;
  return get(url, timeoutMs);
}

async function post(url: string, body: string, timeoutMs: number): Promise<string> {
  const res = await request(url, { method: "POST", body, headers: { "Content-Type": "text/plain; charset=utf-8" } }, timeoutMs);
  return (await res.text()).trim();
}

async function get(url: string, timeoutMs: number): Promise<string> {
  const res = await request(url, { method: "GET" }, timeoutMs);
  return (await res.text()).trim();
}

async function request(url: string, init: RequestInit, timeoutMs: number): Promise<Response> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    return await fetch(url, { ...init, signal: ctrl.signal });
  } catch (e) {
    // fetch throws a TypeError for connection-refused; AbortError for our own timeout.
    if (isAbort(e)) throw new ConsoleDownError(`request to ${url} timed out after ${timeoutMs}ms`);
    throw new ConsoleDownError(`cannot reach ${url}: ${describe(e)}`);
  } finally {
    clearTimeout(timer);
  }
}

/** The eval handler returns errors as plain text rather than HTTP status; sniff the known prefixes. */
function throwIfEvalError(text: string): void {
  const lead = text.trimStart();
  if (
    lead.startsWith("compile error:") ||
    lead.startsWith("exception:") ||
    lead.startsWith("error:") ||
    lead.startsWith("err:")
  ) {
    throw new EvalError(text);
  }
}

function isAbort(e: unknown): boolean {
  return e instanceof Error && e.name === "AbortError";
}

function describe(e: unknown): string {
  if (e instanceof Error) return `${e.name}: ${e.message}${e.cause ? ` (${describe(e.cause)})` : ""}`;
  return String(e);
}
