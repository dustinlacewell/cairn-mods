#!/usr/bin/env tsx
// Phase 5b: live-prove anchor FAILOVER on a formed 3-chain (1 leaf → 2 middle → 3 head).
//
// WHY the mutual TOP-PAIR fall (not a single fall): in the everyone-has-two-pitons model, an exhausting
// climber catches ITSELF on its own piton and settles into a Rappel hang that NET-ADVERTISES as wall-
// supported — so a lone hang never invalidates a neighbour as a belayer. The ONLY clean way to make id2's
// CARRY (id3) read invalid from id2's side is to have id3 ANNOUNCE Hanging (id3 weights its own rope). For
// id2 to then need failover, id2 must ALSO be hanging (so it's not just standing on the wall). Hence: fall
// id2 AND id3 together. Expected end-state (traced through BelayPolicy.Derive):
//   - id3 (id2's carry) hangs → announces Hanging → from id2: partnerValidBelayer(id3)=false → id3-side piton SWEPT.
//   - id1 (id2's dependent) stays on the wall → partnerValidBelayer(id1)=true → iHaveAnyValidBelayer=true.
//   - So id2's global DropToFall does NOT fire; id2 stays IsSecured=True on the id1-SIDE piton.
//   => id2 transitions pitons 2 → 1, secured stays True, survivor = the dependent (id1) side. THAT is failover.
//
// Assumes chain3 already formed the 1→2→3 chain on running instances (ports 14200/14201/14202).
//   tsx src/failover-test.ts

import { evalCs, evalFile } from "./eval.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const driver = (expr: string) => `try { return CairnCoop.Core.Instance.Driver.${expr}; } catch (System.Exception e) { return "err:" + e.Message; }`;

async function topo(id: number): Promise<string> {
  return (await evalFile(PORTS[id], "chain-topo").catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}

async function call(id: number, expr: string): Promise<string> {
  return (await evalCs(PORTS[id], driver(expr)).catch((e) => `err:${e instanceof Error ? e.message : e}`)).trim();
}

/** Form the ordered 1→2→3 carry chain if it isn't already (idempotent; the handshake datagrams ride
 *  ModLoopback at ~1 Hz, so request/accept run in rounds). Returns true once id2.carry=3 && id3.deps=[2]. */
async function ensureChain(): Promise<boolean> {
  for (let round = 0; round < 6; round++) {
    const g2 = await call(2, "CarryGraph()");
    const g3 = await call(3, "CarryGraph()");
    if (g2.includes("carry=3") && g3.includes("deps=[2]")) return true;
    await call(1, "RequestRope(2)");
    await call(2, "RequestRope(3)");
    await sleep(1200);
    await call(2, "AcceptRope(1)");
    await call(3, "AcceptRope(2)");
    await sleep(1200);
  }
  const g2 = await call(2, "CarryGraph()");
  return g2.includes("carry=3");
}

/** Parse `pitons=N` and `secured=True/False` out of the chain-topo first line. */
function parse(t: string): { pitons: number; secured: boolean; module: string } {
  const p = /pitons=(-?\d+)/.exec(t);
  const s = /secured=(True|False)/.exec(t);
  const m = /module=(\w+)/.exec(t);
  return { pitons: p ? Number(p[1]) : NaN, secured: s?.[1] === "True", module: m?.[1] ?? "?" };
}

/** crank-drain + reach-until-Reaching on one instance (the verified exhaustion trigger). */
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
  for (const id of [1, 2, 3]) {
    const t = await topo(id);
    const who = id === 1 ? "leaf" : id === 2 ? "MIDDLE" : "head";
    process.stdout.write(`  id${id} (${who}): ${t}\n`);
  }
}

async function main(): Promise<void> {
  process.stdout.write("Phase 5b: FAILOVER test — fall the mutual top-pair (id2+id3), watch id2 fail over to id1-side.\n");

  process.stdout.write("\n=== forming the 1→2→3 chain (idempotent) ===\n");
  if (!(await ensureChain())) {
    process.stdout.write("✗ chain did not form (id2.carry != 3). Re-check the session; aborting.\n");
    process.exit(3);
  }
  process.stdout.write("✓ chain formed (id2.carry=3, id3.deps=[2])\n");
  await sleep(2500); // let the per-neighbour anchors settle (1 Hz reconciler) so id2 reaches pitons=2

  await dumpAll("before");

  const before2 = parse(await topo(2));
  process.stdout.write(`\nid2 BEFORE: pitons=${before2.pitons} secured=${before2.secured} (expect pitons=2 secured=True)\n`);
  if (before2.pitons !== 2) {
    process.stdout.write("⚠ id2 is not holding 2 pitons — chain not in the expected two-piton state; aborting.\n");
    process.exit(2);
  }

  process.stdout.write("\n=== exhausting the mutual top-pair id2 + id3 ===\n");
  await exhaust(3); // the carry first
  await exhaust(2); // then the middle, so both weight their ropes

  process.stdout.write("\n=== watching id2 fail over (pitons 2→1, secured stays True, survivor = id1-side) ===\n");
  let result: "failover" | "dropped" | "timeout" = "timeout";
  for (let i = 0; i < 24; i++) {
    const p2 = parse(await topo(2));
    const p3 = parse(await topo(3));
    process.stdout.write(`[${i}] id2: pitons=${p2.pitons} secured=${p2.secured} module=${p2.module} | id3 module=${p3.module}\n`);
    if (p2.pitons === 1 && p2.secured) { result = "failover"; break; }
    if (!p2.secured && p2.module !== "Idle") { result = "dropped"; break; }
    await sleep(1000);
  }

  // DURABILITY (adversary check): a transient catch that falls a beat later would read as "failover" at the
  // first pitons=1 but the climber actually plummets. So after a failover, hold-watch several more ticks and
  // require id2 stays secured the WHOLE time. A single secured tick is not proof.
  if (result === "failover") {
    process.stdout.write("\n=== durability: id2 must STAY secured for 8 more ticks (not a transient catch) ===\n");
    for (let i = 0; i < 8; i++) {
      const p2 = parse(await topo(2));
      process.stdout.write(`  hold[${i}] id2: pitons=${p2.pitons} secured=${p2.secured} module=${p2.module}\n`);
      if (!p2.secured) { result = "dropped"; process.stdout.write("  ✗ id2 lost its catch — the failover was TRANSIENT, not durable.\n"); break; }
      await sleep(1000);
    }
  }

  await dumpAll("after");
  process.stdout.write(`\nRESULT: ${result}\n`);
  if (result === "failover") process.stdout.write("✓ FAILOVER PROVEN (durable): id2 dropped the carry-side piton and stayed secured on the dependent (id1) side across the hold window.\n");
  else if (result === "dropped") process.stdout.write("✗ id2 went UNSECURED — it dropped to a fall instead of failing over (or the catch was transient). Investigate the DropToFall vs iHaveAnyValidBelayer gate.\n");
  else process.stdout.write("? timeout — id2 never reached pitons=1; the top-pair may not have both hung (re-read the topology above).\n");
}

main().catch((e) => {
  process.stderr.write(`\nfailover-test failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`);
  process.exit(1);
});
