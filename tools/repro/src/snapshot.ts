// The flight-recorder's capture primitive: take a FULL live-state dump of an instance and persist it.
//
// We deliberately reuse the existing, battle-tested eval tools in tools/*.cs as the single source of
// truth for "what full state looks like" — aava-state.cs (controller/module/rewind/ragdoll/limbs/body/
// rope/bot/net) and rewind-deep.cs (the rewind hand-back: puppet + componentsToToggle vs restore state).
// Re-inlining that C# here would just drift. We read the files at runtime and POST them verbatim.

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { evalCs } from "./eval.ts";
import type { RunLog } from "./log.ts";
import type { Instance } from "./steps.ts";

const TOOLS = (name: string) => fileURLToPath(new URL(`../../${name}`, import.meta.url));

/** The eval-tool scripts that together make up a full revive snapshot, in dump order. */
const DUMPS = [
  { tag: "aava-state", file: "aava-state.cs" },
  { tag: "rewind-deep", file: "rewind-deep.cs" },
] as const;

function loadScript(file: string): string {
  return readFileSync(TOOLS(file), "utf8");
}

/** One full-state capture of one instance at one moment. */
export interface Snapshot {
  who: string;
  phase: string; // e.g. "prompt", "resolved"
  text: string; // the concatenated dump bodies
}

/**
 * Capture the full state of one instance: run each dump script and concatenate the outputs under a header.
 * Each script is independently guarded inside its own C#, so one failing read never blanks the rest; here
 * we additionally catch a transport/eval failure per-script so a single bad dump can't lose the whole snapshot.
 */
export async function capture(inst: Instance, phase: string): Promise<Snapshot> {
  const parts: string[] = [];
  for (const d of DUMPS) {
    const out = await evalCs(inst.port, loadScript(d.file), 20_000).catch(
      (e) => `[${d.tag}] EVAL-FAILED ${e instanceof Error ? e.message : e}`,
    );
    parts.push(`----- ${d.tag} -----\n${out}`);
  }
  return { who: inst.who, phase, text: parts.join("\n\n") };
}

/** Capture both instances at the same phase, in parallel, and write each to the run dir. */
export async function captureBoth(insts: Instance[], phase: string, log: RunLog): Promise<Snapshot[]> {
  const snaps = await Promise.all(insts.map((i) => capture(i, phase)));
  for (const s of snaps) log.snapshot(s.who, phase, s.text);
  return snaps;
}

/**
 * Write a side-by-side host-vs-joiner diff for a phase: the two dumps interleaved by section, so the first
 * divergence (e.g. host limb GOAL==ACTUAL but joiner GOAL stranded 120m away) reads off the page. This is a
 * line-level alignment, not a semantic diff — the dumps are line-stable so equal sections line up.
 */
export function sideBySide(host: Snapshot, joiner: Snapshot): string {
  const h = host.text.split("\n");
  const j = joiner.text.split("\n");
  const n = Math.max(h.length, j.length);
  const W = 64;
  const pad = (s: string) => (s.length > W ? s.slice(0, W - 1) + "…" : s.padEnd(W));
  const out: string[] = [`===== DIFF @ ${host.phase}  (left=HOST  right=JOINER  ‖ marks differing lines) =====`];
  for (let i = 0; i < n; i++) {
    const a = h[i] ?? "";
    const b = j[i] ?? "";
    const mark = a.trim() === b.trim() ? "  " : "‖ ";
    out.push(`${mark}${pad(a)} | ${b}`);
  }
  return out.join("\n");
}
