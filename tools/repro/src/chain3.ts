#!/usr/bin/env tsx
// Phase 4: form an ORDERED 3-climber carry chain 1→2→3 on three already-running instances and verify the
// carry graph + anchors. Assumes setup-n --no-rope already launched 3 instances (ports 14200/14201/14202,
// localIds 1/2/3). Drives the directed RequestRope/AcceptRope verbs to build:
//   id1 --requests--> id2   (id1's carry = 2; id2 carries 1)
//   id2 --requests--> id3   (id2's carry = 3; id3 carries 2)
// => chain 1→2→3. EffectiveCarry: 1→2, 2→3, 3→2(head anchors to its dependent). Deps: 2=[1], 3=[2].
//
//   tsx src/chain3.ts

import { evalCs } from "./eval.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

const driver = (expr: string) => `try { return CairnCoop.Core.Instance.Driver.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;

async function call(localId: number, expr: string): Promise<string> {
  return (await evalCs(PORTS[localId], driver(expr)).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}

/** Read the carry graph on every instance. */
async function dumpGraph(label: string): Promise<void> {
  process.stdout.write(`\n── carry graph: ${label} ──\n`);
  for (const id of [1, 2, 3]) {
    const g = await call(id, "CarryGraph()");
    process.stdout.write(`  id${id} (port ${PORTS[id]}): ${g}\n`);
  }
}

async function main(): Promise<void> {
  process.stdout.write("Phase 4: forming ordered carry chain 1→2→3 …\n");
  await dumpGraph("before");

  // Build the chain with directed requests; the handshake datagrams ride ModLoopback (the N-peer transport).
  // Drive request+accept in rounds — the accept needs the request's datagram to have arrived (async).
  // Round the requests first, let them propagate, then the accepts, then let those propagate.
  for (let round = 0; round < 6; round++) {
    // id1 wants id2 as carry; id2 wants id3 as carry.
    process.stdout.write(`\n[round ${round}] sending requests + accepts\n`);
    process.stdout.write(`  ${await call(1, "RequestRope(2)")} (1→2)\n`);
    process.stdout.write(`  ${await call(2, "RequestRope(3)")} (2→3)\n`);
    await sleep(1200); // requests propagate over ModLoopback (~1Hz)
    // id2 accepts id1's incoming; id3 accepts id2's incoming.
    process.stdout.write(`  ${await call(2, "AcceptRope(1)")} (2 carries 1)\n`);
    process.stdout.write(`  ${await call(3, "AcceptRope(2)")} (3 carries 2)\n`);
    await sleep(1200); // accepts propagate

    // Check whether the chain has formed: 1.carry=2, 2.carry=3, 3 has dep [2] (head).
    const g1 = await call(1, "CarryGraph()");
    const g2 = await call(2, "CarryGraph()");
    const g3 = await call(3, "CarryGraph()");
    if (g1.includes("carry=2") && g2.includes("carry=3") && g3.includes("deps=[2]")) {
      process.stdout.write("\n✓ chain 1→2→3 formed\n");
      break;
    }
  }

  await dumpGraph("after");
  process.stdout.write(
    "\nEXPECTED: id1 carry=2 effectiveCarry=2 deps=[]; id2 carry=3 effectiveCarry=3 deps=[1]; " +
    "id3 carry=-1 effectiveCarry=2 deps=[2] (head anchors to dependent 2).\n",
  );
}

main().catch((e) => {
  process.stderr.write(`\nchain3 failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
