#!/usr/bin/env tsx
// Verify the CrossMenu UNROPE action (driver.UnropeAll) detaches a climber from the WHOLE chain in one call,
// and that BOTH neighbours drop their side. Form chain 1→2→3, then UnropeAll() on the MIDDLE (id2, which has a
// carry above + a dependent below). Expect:
//   - id2: carry=-1 deps=[] enabled=False (fully detached)
//   - id1: loses its carry (id2) → carry=-1 (its only neighbour was id2)
//   - id3: loses its dependent (id2) → deps=[] (its only neighbour was id2)
//
//   tsx src/unrope-test.ts   (assumes setup-n --instances 3 --no-rope is running)

import { evalCs } from "./eval.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const driver = (expr: string) => `try { return CairnCoop.Core.Instance.Driver.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;
const call = async (id: number, expr: string) => (await evalCs(PORTS[id], driver(expr)).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
/** Invoke a VOID driver method as a statement (the `driver()` wrapper returns its value, which fails on void). */
const callVoid = async (id: number, stmt: string) =>
  (await evalCs(PORTS[id], `try { CairnCoop.Core.Instance.Driver.${stmt}; return "ok"; } catch (System.Exception e) { return "err:" + e.Message; }`)
    .catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();

async function ensureChain(): Promise<boolean> {
  for (let round = 0; round < 6; round++) {
    if ((await call(2, "CarryGraph()")).includes("carry=3") && (await call(3, "CarryGraph()")).includes("deps=[2]")) return true;
    await call(1, "RequestRope(2)"); await call(2, "RequestRope(3)");
    await sleep(1200);
    await call(2, "AcceptRope(1)"); await call(3, "AcceptRope(2)");
    await sleep(1200);
  }
  return (await call(2, "CarryGraph()")).includes("carry=3");
}

async function dump(label: string): Promise<Record<number, string>> {
  process.stdout.write(`\n── carry graph: ${label} ──\n`);
  const g: Record<number, string> = {};
  for (const id of [1, 2, 3]) { g[id] = await call(id, "CarryGraph()"); process.stdout.write(`  id${id}: ${g[id]}\n`); }
  return g;
}

async function main(): Promise<void> {
  process.stdout.write("UNROPE test — UnropeAll() on the MIDDLE (id2) detaches the whole chain; both neighbours drop.\n");
  if (!(await ensureChain())) { process.stdout.write("✗ chain did not form; aborting.\n"); process.exit(3); }
  await sleep(2000);
  await dump("before unrope");

  process.stdout.write(`\n=== UnropeAll() on id2 (middle) → ${await callVoid(2, "UnropeAll()")} ===\n`);
  await sleep(2500); // Cancel propagates over ModLoopback (~1Hz) + a reconciler tick

  const after = await dump("after unrope");
  const id2Detached = /carry=-1/.test(after[2]) && /deps=\[\]/.test(after[2]);
  const id1LostCarry = /carry=-1/.test(after[1]);   // id1's only neighbour was id2
  const id3LostDep = /deps=\[\]/.test(after[3]);     // id3's only neighbour was id2

  process.stdout.write("\n── VERDICT ──\n");
  process.stdout.write(`  id2 fully detached (carry=-1 deps=[]): ${id2Detached ? "✓" : "✗"}\n`);
  process.stdout.write(`  id1 dropped its carry to id2 (carry=-1): ${id1LostCarry ? "✓" : "✗"}\n`);
  process.stdout.write(`  id3 dropped its dependent id2 (deps=[]): ${id3LostDep ? "✓" : "✗"}\n`);
  const pass = id2Detached && id1LostCarry && id3LostDep;
  process.stdout.write(`\nRESULT: ${pass ? "✓ WHOLE-CHAIN UNROPE WORKS" : "✗ unrope incomplete — see ✗ above"}\n`);
  if (!pass) process.exit(1);
}

main().catch((e) => { process.stderr.write(`\nunrope-test failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`); process.exit(1); });
