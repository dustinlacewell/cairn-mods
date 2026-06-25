#!/usr/bin/env tsx
// Phase 5c: chain-HEAL on death/revive for the 3-chain (1 leaf → 2 middle → 3 head).
//
// A climber only DIES (vs self-catching into a Rappel hang) when its carry is already off the wall — so the
// death scenario is inherently the mutual TOP-PAIR fall: id2 + id3 both fall, id3 (head, no carry) goes fully
// unsecured and dies, id2 fails over to its dependent id1. With edelweiss armed, id3's death raises the revive
// prompt; we accept it, wait for the revive to resolve, then assert the CHAIN HEALED:
//   (a) id3 returns UNLINKED (carry=-1 deps=[] — UnlinkForRevive cleared its handshake), pawn alive.
//   (b) id2's stale carry-edge to the dead id3 is CLEARED (carry no longer =3) — no phantom carry.
//   (c) MANUAL re-rope rebuilds 1→2→3 cleanly (the shipped 're-rope by hand' model), id2 back to pitons=2.
//
// Assumes setup-n --no-rope launched 3 instances (ports 14200/14201/14202). Self-contained: forms the chain,
// arms edelweiss, drives the death+revive, verifies cleanup, re-ropes, verifies heal.
//   tsx src/chain-heal-test.ts

import { evalCs, evalFile } from "./eval.ts";
import { cmd } from "./eval.ts";
import { ACCEPT_PROMPT, REVIVE_SIGNAL } from "./snippets.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const driver = (expr: string) => `try { return CairnCoop.Core.Instance.Driver.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;

async function call(id: number, expr: string): Promise<string> {
  return (await evalCs(PORTS[id], driver(expr)).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}
async function topo(id: number): Promise<string> {
  return (await evalFile(PORTS[id], "chain-topo").catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}
function parse(t: string): { pitons: number; secured: boolean; module: string; carry: number; deps: string } {
  const p = /pitons=(-?\d+)/.exec(t);
  const s = /secured=(True|False)/.exec(t);
  const m = /module=(\w+)/.exec(t);
  const c = /carry=(-?\d+)/.exec(t);
  const d = /deps=(\[[^\]]*\])/.exec(t);
  return { pitons: p ? Number(p[1]) : NaN, secured: s?.[1] === "True", module: m?.[1] ?? "?", carry: c ? Number(c[1]) : NaN, deps: d?.[1] ?? "?" };
}

/** Form the ordered 1→2→3 carry chain if not already (idempotent). */
async function ensureChain(): Promise<boolean> {
  for (let round = 0; round < 6; round++) {
    if ((await call(2, "CarryGraph()")).includes("carry=3") && (await call(3, "CarryGraph()")).includes("deps=[2]")) return true;
    await call(1, "RequestRope(2)");
    await call(2, "RequestRope(3)");
    await sleep(1200);
    await call(2, "AcceptRope(1)");
    await call(3, "AcceptRope(2)");
    await sleep(1200);
  }
  return (await call(2, "CarryGraph()")).includes("carry=3");
}

/** crank-drain + reach-until-Reaching (the verified exhaustion trigger). */
async function exhaust(id: number): Promise<void> {
  await evalFile(PORTS[id], "crank-stamina-drain").catch(() => {});
  for (let a = 0; a < 6; a++) {
    const r = await evalFile(PORTS[id], "reach-to-exert").catch((e) => `err:${e}`);
    process.stdout.write(`  exhaust id${id} attempt ${a + 1}: ${r.trim()}\n`);
    if (/Reaching/i.test(r)) return;
    await sleep(400);
  }
}

async function reviveSignal(id: number): Promise<string> {
  return (await evalCs(PORTS[id], REVIVE_SIGNAL).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}

async function dumpAll(label: string): Promise<void> {
  process.stdout.write(`\n── topology: ${label} ──\n`);
  for (const id of [1, 2, 3]) {
    const t = await topo(id);
    const who = id === 1 ? "leaf" : id === 2 ? "MIDDLE" : "head";
    process.stdout.write(`  id${id} (${who}): ${t}\n`);
  }
}

async function main(): Promise<void> {
  process.stdout.write("Phase 5c: chain-HEAL test — id3 dies (top-pair fall) → edelweiss revive → verify cleanup → re-rope → verify heal.\n");

  process.stdout.write("\n=== forming the 1→2→3 chain (idempotent) ===\n");
  if (!(await ensureChain())) { process.stdout.write("✗ chain did not form; aborting.\n"); process.exit(3); }
  process.stdout.write("✓ chain formed\n");
  await sleep(2500);

  process.stdout.write("\n=== arming edelweiss on all three (so the death revives) ===\n");
  for (const id of [1, 2, 3]) process.stdout.write(`  id${id}: ${await cmd(PORTS[id], "edelweiss on")}\n`);

  await dumpAll("before death");
  const before2 = parse(await topo(2));
  if (before2.pitons !== 2) { process.stdout.write(`⚠ id2 not at pitons=2 (got ${before2.pitons}); chain not in two-piton state; aborting.\n`); process.exit(2); }

  process.stdout.write("\n=== falling the mutual top-pair id2+id3 so id3 (head) dies ===\n");
  await exhaust(3);
  await exhaust(2);

  // Wait for id3's edelweiss prompt (inRez true), then ACCEPT it.
  process.stdout.write("\n=== waiting for id3's edelweiss prompt (inRez) ===\n");
  let promptUp = false;
  for (let i = 0; i < 25; i++) {
    const sig = await reviveSignal(3);
    process.stdout.write(`[${i}] id3 revive-signal: ${sig}\n`);
    if (/inRez=True/i.test(sig)) { promptUp = true; break; }
    await sleep(1000);
  }
  if (!promptUp) { process.stdout.write("✗ id3 never raised the edelweiss prompt — it may not have died (re-check the fall). Aborting.\n"); await dumpAll("no-prompt"); process.exit(4); }

  process.stdout.write("\n=== accepting id3's revive prompt ===\n");
  for (let i = 0; i < 10; i++) {
    const r = (await evalCs(PORTS[3], ACCEPT_PROMPT).catch((e) => `err:${e}`)).trim();
    process.stdout.write(`  accept attempt ${i + 1}: ${r}\n`);
    if (r === "accepted") break;
    await sleep(500);
  }

  // Wait for the revive to RESOLVE (isAuto=false && inRez=false).
  process.stdout.write("\n=== waiting for id3's revive to resolve (isAuto=false inRez=false) ===\n");
  let resolved = false;
  for (let i = 0; i < 30; i++) {
    const sig = await reviveSignal(3);
    process.stdout.write(`[${i}] id3 revive-signal: ${sig}\n`);
    if (/isAuto=False/i.test(sig) && /inRez=False/i.test(sig)) { resolved = true; break; }
    await sleep(1000);
  }
  if (!resolved) { process.stdout.write("✗ id3's revive never resolved. Aborting.\n"); await dumpAll("unresolved"); process.exit(5); }

  await sleep(2000); // let the post-revive reconciler ticks run on all three
  await dumpAll("after revive (pre re-rope)");

  // ── CLEANUP ASSERTIONS ──
  process.stdout.write("\n=== HEAL check (a/b): dead climber unlinked, survivor's stale carry cleared ===\n");
  const a3 = parse(await topo(3));
  const a2 = parse(await topo(2));
  const a1 = parse(await topo(1));
  const idle3 = /module=(Idle|Climbing|Walking|HangIdle|Rappel)/.test(await topo(3));
  const id3Unlinked = a3.carry === -1 && a3.deps === "[]";
  const id2NoStaleCarry = a2.carry !== 3; // id3 was id2's carry; it must be cleared
  process.stdout.write(`  id3: carry=${a3.carry} deps=${a3.deps} module=${a3.module} → unlinked=${id3Unlinked}\n`);
  process.stdout.write(`  id2: carry=${a2.carry} deps=${a2.deps} module=${a2.module} → noStaleCarryToId3=${id2NoStaleCarry}\n`);
  process.stdout.write(`  id1: carry=${a1.carry} deps=${a1.deps} module=${a1.module}\n`);

  // ── RE-ROPE (manual, shipped model) ──
  process.stdout.write("\n=== re-roping (manual): rebuild 1→2→3 ===\n");
  // First clear any residual state on id1/id2 toward the chain we want to rebuild from scratch, then re-form.
  const healed = await ensureChain();
  await sleep(3000); // let anchors re-settle to two-piton on id2
  await dumpAll("after re-rope");
  const h2 = parse(await topo(2));
  const chainRestored = healed && h2.carry === 3 && h2.pitons === 2;

  process.stdout.write("\n── HEAL VERDICT ──\n");
  process.stdout.write(`  (a) id3 returned unlinked: ${id3Unlinked ? "✓" : "✗"} (carry=${a3.carry} deps=${a3.deps})\n`);
  process.stdout.write(`  (b) id2 stale carry-to-id3 cleared: ${id2NoStaleCarry ? "✓" : "✗"} (carry=${a2.carry})\n`);
  process.stdout.write(`  (c) manual re-rope restored 1→2→3 (id2 carry=3 pitons=2): ${chainRestored ? "✓" : "✗"} (carry=${h2.carry} pitons=${h2.pitons})\n`);
  const pass = id3Unlinked && id2NoStaleCarry && chainRestored;
  process.stdout.write(`\nRESULT: ${pass ? "✓ CHAIN HEALED" : "✗ HEAL INCOMPLETE — see the ✗ above"}\n`);
  if (!pass) process.exit(1);
}

main().catch((e) => {
  process.stderr.write(`\nchain-heal-test failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
