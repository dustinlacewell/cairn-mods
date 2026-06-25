#!/usr/bin/env tsx
// Verify the party HUD's DATA layer: the room-wide carry-graph topology replicates to EVERY client. The HUD
// rendering is the proven Ui kit; what's new is TopologyTracker assembling sender→carry off every broadcast.
// Form chain 1→2→3, then read CairnCoop.Core.Instance.PartyDiag() on all three — each must show the FULL graph
// (edges 1→2, 2→3, 3→-1), not just its own edge. That proves topology replicates room-wide.
//
//   tsx src/party-test.ts   (assumes setup-n --instances 3 --no-rope is running)

import { evalCs } from "./eval.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const driver = (expr: string) => `try { return CairnCoop.Core.Instance.Driver.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;
const core = (expr: string) => `try { return CairnCoop.Core.Instance.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;
const call = async (id: number, expr: string) => (await evalCs(PORTS[id], driver(expr)).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
const party = async (id: number) => (await evalCs(PORTS[id], core("PartyDiag()")).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();

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

/** Parse "edges[ a→b c→d ]" into a normalized "a→b,c→d" sorted set for comparison. */
function edges(diag: string): string {
  const m = /edges\[([^\]]*)\]/.exec(diag);
  if (!m) return "";
  return m[1].trim().split(/\s+/).filter(Boolean).sort().join(",");
}

async function main(): Promise<void> {
  process.stdout.write("PARTY topology test — each client must assemble the FULL room-wide carry graph.\n");
  if (!(await ensureChain())) { process.stdout.write("✗ chain did not form; aborting.\n"); process.exit(3); }
  await sleep(3000); // let the carry announces propagate + each TopologyTracker fill

  const diags: Record<number, string> = {};
  process.stdout.write("\n── PartyDiag() per instance ──\n");
  for (const id of [1, 2, 3]) { diags[id] = await party(id); process.stdout.write(`  id${id}: ${diags[id]}\n`); }

  // Expected room-wide edge set (every client should converge to the same): 1→2, 2→3, 3→-1.
  const expect = ["1→2", "2→3", "3→-1"].sort().join(",");
  process.stdout.write(`\nexpected edges (sorted): ${expect}\n`);

  let pass = true;
  for (const id of [1, 2, 3]) {
    const got = edges(diags[id]);
    const ok = got === expect;
    process.stdout.write(`  id${id} sees: ${got}  ${ok ? "✓" : "✗ (expected " + expect + ")"}\n`);
    if (!ok) pass = false;
  }
  process.stdout.write(`\nRESULT: ${pass ? "✓ TOPOLOGY REPLICATES ROOM-WIDE (every client has the full chain)" : "✗ topology incomplete on ≥1 client"}\n`);
  if (!pass) process.exit(1);
}

main().catch((e) => { process.stderr.write(`\nparty-test failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`); process.exit(1); });
