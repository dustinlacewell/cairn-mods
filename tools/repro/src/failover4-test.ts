#!/usr/bin/env tsx
// N=4 failover: prove the per-neighbour model generalizes past 3 — an INTERIOR node with a real (non-head)
// carry fails over, and a 2-climber BASE absorbs the cascade.
//
// On the 4-chain 1→2→3→4 (4 = head), id3 is a true interior node: carry=4 (above), dependent=2 (below), so it
// holds TWO pitons at rest. Fall the top pair (3+4) — the same trigger shape that makes a head die, but now the
// faller (3) is interior and the survivor base (1–2) is two climbers deep:
//   - id4 (head, no carry) goes fully unsecured and free-falls — correct by design (nothing above it).
//   - id3 (interior) had id4 as its CARRY; id4 going invalid sweeps id3's carry-4-side piton. id3 stays caught
//     on its DEPENDENT-2-side piton → pitons 2→1, secured stays True. This is failover with a NON-HEAD carry.
//   - id1 and id2 (the BASE) NEVER leave the wall — they stay secured; id2 ends up belaying the hanging id3.
//
// Assumes setup-n --instances 4 --no-rope launched 4 instances (ports 14200..14203). Self-contained: forms the
// chain, confirms id3 at pitons=2, falls the top pair, watches id3 fail over with the base intact.
//   tsx src/failover4-test.ts

import { evalCs, evalFile } from "./eval.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202, 4: 14203 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const driver = (expr: string) => `try { return CairnCoop.Core.Instance.Driver.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;
const EDGES: ReadonlyArray<[number, number]> = [[1, 2], [2, 3], [3, 4]];

async function call(id: number, expr: string): Promise<string> {
  return (await evalCs(PORTS[id], driver(expr)).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}
async function topo(id: number): Promise<string> {
  return (await evalFile(PORTS[id], "chain-topo").catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}
function parse(t: string): { pitons: number; secured: boolean; module: string } {
  const p = /pitons=(-?\d+)/.exec(t);
  const s = /secured=(True|False)/.exec(t);
  const m = /module=(\w+)/.exec(t);
  return { pitons: p ? Number(p[1]) : NaN, secured: s?.[1] === "True", module: m?.[1] ?? "?" };
}

async function ensureChain(): Promise<boolean> {
  for (let round = 0; round < 8; round++) {
    let ok = true;
    for (const [req, carry] of EDGES) if (!(await call(req, "CarryGraph()")).includes(`carry=${carry}`)) ok = false;
    if (ok && (await call(4, "CarryGraph()")).includes("deps=[3]")) return true;
    for (const [req, carry] of EDGES) await call(req, `RequestRope(${carry})`);
    await sleep(1200);
    for (const [req, carry] of EDGES) await call(carry, `AcceptRope(${req})`);
    await sleep(1200);
  }
  return false;
}

async function exhaust(id: number): Promise<void> {
  await evalFile(PORTS[id], "crank-stamina-drain").catch(() => {});
  for (let a = 0; a < 6; a++) {
    const r = await evalFile(PORTS[id], "reach-to-exert").catch((e) => `err:${e}`);
    process.stdout.write(`  exhaust id${id} attempt ${a + 1}: ${r.trim()}\n`);
    if (/Reaching/i.test(r)) return;
    await sleep(400);
  }
}

async function dumpAll(label: string): Promise<void> {
  process.stdout.write(`\n── topology: ${label} ──\n`);
  for (const id of [1, 2, 3, 4]) {
    const who = id === 1 ? "leaf" : id === 4 ? "head" : `mid${id}`;
    process.stdout.write(`  id${id} (${who}): ${await topo(id)}\n`);
  }
}

async function main(): Promise<void> {
  process.stdout.write("N=4 FAILOVER: fall top-pair (3+4); interior id3 fails over to dependent-2 side; base 1–2 stays on wall.\n");

  process.stdout.write("\n=== forming the 1→2→3→4 chain (idempotent) ===\n");
  if (!(await ensureChain())) { process.stdout.write("✗ chain did not form; aborting.\n"); process.exit(3); }
  process.stdout.write("✓ chain formed\n");
  await sleep(3000); // let per-neighbour anchors settle (id3 → pitons=2)

  await dumpAll("before");
  const b3 = parse(await topo(3));
  if (b3.pitons !== 2) { process.stdout.write(`⚠ id3 (interior) not at pitons=2 (got ${b3.pitons}); aborting.\n`); process.exit(2); }
  process.stdout.write(`\nid3 BEFORE: pitons=${b3.pitons} secured=${b3.secured} (expect 2 / True — a true interior node)\n`);

  process.stdout.write("\n=== falling the top-pair id3 + id4 ===\n");
  await exhaust(4);
  await exhaust(3);

  process.stdout.write("\n=== watching id3 fail over (2→1) while the 1–2 base stays on the wall ===\n");
  let result: "failover" | "dropped" | "base-fell" | "timeout" = "timeout";
  for (let i = 0; i < 24; i++) {
    const p1 = parse(await topo(1));
    const p2 = parse(await topo(2));
    const p3 = parse(await topo(3));
    const p4 = parse(await topo(4));
    process.stdout.write(`[${i}] id3: pitons=${p3.pitons} sec=${p3.secured} mod=${p3.module} | id4 mod=${p4.module} | base id1 sec=${p1.secured} id2 sec=${p2.secured}\n`);
    if (!p1.secured || (!p2.secured && p2.module !== "Idle" && p2.module !== "Reaching")) { result = "base-fell"; break; }
    if (p3.pitons === 1 && p3.secured) { result = "failover"; break; }
    if (!p3.secured && p3.module !== "Idle" && p3.module !== "Reaching") { result = "dropped"; break; }
    await sleep(1000);
  }

  // Durability: id3 must STAY secured AND the base must stay on the wall for several more ticks.
  if (result === "failover") {
    process.stdout.write("\n=== durability: id3 secured + base 1–2 on the wall, 8 more ticks ===\n");
    for (let i = 0; i < 8; i++) {
      const p1 = parse(await topo(1));
      const p2 = parse(await topo(2));
      const p3 = parse(await topo(3));
      process.stdout.write(`  hold[${i}] id3: pitons=${p3.pitons} sec=${p3.secured} mod=${p3.module} | base id1 sec=${p1.secured} id2 sec=${p2.secured}\n`);
      if (!p3.secured) { result = "dropped"; process.stdout.write("  ✗ id3 lost its catch — transient.\n"); break; }
      if (!p1.secured) { result = "base-fell"; process.stdout.write("  ✗ base climber id1 left the wall.\n"); break; }
      await sleep(1000);
    }
  }

  await dumpAll("after");
  process.stdout.write(`\nRESULT: ${result}\n`);
  if (result === "failover") process.stdout.write("✓ N=4 FAILOVER PROVEN: interior id3 dropped its carry-4 piton and stayed secured on the dependent-2 side; the 1–2 base never left the wall.\n");
  else if (result === "dropped") process.stdout.write("✗ id3 went UNSECURED instead of failing over.\n");
  else if (result === "base-fell") process.stdout.write("✗ a BASE climber (1 or 2) left the wall — the cascade was not absorbed.\n");
  else process.stdout.write("? timeout — id3 never reached pitons=1.\n");
  if (result !== "failover") process.exit(1);
}

main().catch((e) => {
  process.stderr.write(`\nfailover4-test failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
