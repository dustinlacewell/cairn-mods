#!/usr/bin/env tsx
// N=4: form an ORDERED 4-climber carry chain 1→2→3→4 on four already-running instances and verify the carry
// graph. Assumes setup-n --instances 4 --no-rope launched 4 instances (ports 14200..14203, localIds 1..4).
// Directed requests build:
//   1 --req--> 2   (1's carry=2; 2 carries 1)
//   2 --req--> 3   (2's carry=3; 3 carries 2)
//   3 --req--> 4   (3's carry=4; 4 carries 3)
// => chain 1→2→3→4. Deps: 2=[1], 3=[2], 4=[3]. 4 is the HEAD (carry=-1, effectiveCarry=3).
//
//   tsx src/chain4.ts

import { evalCs } from "./eval.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202, 4: 14203 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const driver = (expr: string) => `try { return CairnCoop.Core.Instance.Driver.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;

async function call(id: number, expr: string): Promise<string> {
  return (await evalCs(PORTS[id], driver(expr)).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}

/** The directed edges that define the chain: requester → carry. */
const EDGES: ReadonlyArray<[number, number]> = [[1, 2], [2, 3], [3, 4]];

async function dumpGraph(label: string): Promise<void> {
  process.stdout.write(`\n── carry graph: ${label} ──\n`);
  for (const id of [1, 2, 3, 4]) process.stdout.write(`  id${id} (port ${PORTS[id]}): ${await call(id, "CarryGraph()")}\n`);
}

/** True once every requester has its intended carry and the head (4) shows deps=[3]. */
async function chainFormed(): Promise<boolean> {
  for (const [req, carry] of EDGES) if (!(await call(req, "CarryGraph()")).includes(`carry=${carry}`)) return false;
  return (await call(4, "CarryGraph()")).includes("deps=[3]");
}

async function main(): Promise<void> {
  process.stdout.write("N=4: forming ordered carry chain 1→2→3→4 …\n");
  await dumpGraph("before");

  for (let round = 0; round < 8; round++) {
    process.stdout.write(`\n[round ${round}] requests + accepts\n`);
    // Send every requester's request, let them propagate, then every carry accepts its incoming.
    for (const [req, carry] of EDGES) process.stdout.write(`  ${await call(req, `RequestRope(${carry})`)} (${req}→${carry})\n`);
    await sleep(1200);
    for (const [req, carry] of EDGES) process.stdout.write(`  ${await call(carry, `AcceptRope(${req})`)} (${carry} carries ${req})\n`);
    await sleep(1200);
    if (await chainFormed()) { process.stdout.write("\n✓ chain 1→2→3→4 formed\n"); break; }
  }

  await dumpGraph("after");
  process.stdout.write(
    "\nEXPECTED: id1 carry=2 deps=[]; id2 carry=3 deps=[1]; id3 carry=4 deps=[2]; " +
    "id4 carry=-1 effectiveCarry=3 deps=[3] (head anchors to dependent 3).\n",
  );
}

main().catch((e) => {
  process.stderr.write(`\nchain4 failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
