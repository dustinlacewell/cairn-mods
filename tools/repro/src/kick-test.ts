#!/usr/bin/env tsx
// Verify HOST KICK: the host (id1, port 14200) is the relay; it evicts a member by room id, which bans the
// member's link (rejoin refused) and resets its game client so it leaves. On a 3-instance local room:
//   1. host RoomMembers() shows 3 (host + 2 joiners).
//   2. host kicks id3 (Core.Instance.Kick(3)).
//   3. id3's game client is reset → it leaves the room (clientState drops / remotes shrink); the host's roster
//      drops to 2; and id3 can't immediately rejoin (the relay refuses its banned link).
//
//   tsx src/kick-test.ts   (assumes setup-n --instances 3 --no-rope is running)

import { evalCs } from "./eval.ts";

const PORTS: Record<number, number> = { 1: 14200, 2: 14201, 3: 14202 };
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
/** Invoke a VOID Core method as a statement (a `return <void>` wrapper would fail to compile — e.g. Kick). */
const coreVoid = (port: number, stmt: string) =>
  evalCs(port, `try { CairnCoop.Core.Instance.${stmt}; return "ok"; } catch (System.Exception e) { return "err:" + e.Message; }`)
    .catch((e) => `err:${e instanceof Error ? e.message : e}`).then((s) => s.trim());

/** Host-side member count via the relay (RelayMembers count). */
const memberCount = (port: number) =>
  evalCs(port, `try { return CairnCoop.Core.Instance.MemberCountDiag(); } catch (System.Exception e) { return "err:" + e.Message; }`)
    .catch((e) => `err:${e instanceof Error ? e.message : e}`).then((s) => s.trim());

/** A climber's own view: clientState + how many remotes it sees (shrinks when it's kicked / others leave). */
const sessionView = (port: number) =>
  evalCs(port, `try {
    var nm = Il2CppTheGameBakers.Cairn.Netplay.NetplayManager.Instance;
    var gm = nm != null ? nm.Gamemode : null;
    var srg = gm != null ? gm.TryCast<Il2CppTheGameBakers.Cairn.Netplay.SharedRopeGamemode>() : null;
    int remotes = -1; try { if (srg != null) remotes = srg.GetRemotePlayers().Count; } catch {}
    string cs = "?"; try { cs = nm != null ? nm.clientState.ToString() : "?"; } catch (System.Exception ce) { cs = "INVALID"; }
    string inroom = "?"; try { inroom = nm != null ? nm.IsInRoom.ToString() : "?"; } catch {}
    return "clientState=" + cs + " nativeInRoom=" + inroom + " isSRG=" + (srg!=null) + " remotes=" + remotes;
  } catch (System.Exception e) { return "err:" + e.Message; }`)
    .catch((e) => `err:${e instanceof Error ? e.message : e}`).then((s) => s.trim());

async function dumpAll(label: string): Promise<void> {
  process.stdout.write(`\n── ${label} ──\n`);
  process.stdout.write(`  host members: ${await memberCount(PORTS[1])}\n`);
  for (const id of [1, 2, 3]) process.stdout.write(`  id${id}: ${await sessionView(PORTS[id])}\n`);
}

async function main(): Promise<void> {
  process.stdout.write("KICK test — host (id1) kicks id3; id3 leaves, roster drops to 2, rejoin refused.\n");
  await dumpAll("before kick");

  const before = await sessionView(PORTS[3]);
  process.stdout.write(`\n=== host kicks id3 → ${await coreVoid(PORTS[1], "Kick(3)")} ===\n`);

  // Watch the host roster shrink to 2 (the decisive signal — the relay evicted id3) AND id3 leave the room.
  let kicked = false;
  for (let i = 0; i < 14; i++) {
    const mc = await memberCount(PORTS[1]);
    const v3 = await sessionView(PORTS[3]);
    process.stdout.write(`[${i}] host members=${mc} | id3 ${v3}\n`);
    // Kicked = host roster down to 2 (id3 evicted). id3's own view (nativeInRoom=False) corroborates.
    if (/members=2\b/.test(mc)) { kicked = true; break; }
    await sleep(1000);
  }

  // Ban holds: confirm the host roster STAYS at 2 (id3's rejoin attempts are refused) over a few checks.
  let banHolds = kicked;
  if (kicked) {
    process.stdout.write("\n=== ban holds (host stays at 2 members; id3 rejoin refused) ===\n");
    for (let i = 0; i < 5; i++) {
      const mc = await memberCount(PORTS[1]);
      process.stdout.write(`  hold[${i}] host ${mc}\n`);
      if (!/members=2\b/.test(mc)) { banHolds = false; process.stdout.write("  ✗ roster changed — ban did not hold (id3 rejoined)\n"); break; }
      await sleep(1500);
    }
  }

  await dumpAll("after kick");
  process.stdout.write(`\nRESULT: ${kicked && banHolds ? "✓ id3 KICKED + BANNED (roster 3→2, rejoin refused)" : kicked ? "~ id3 kicked but ban did not hold" : "✗ id3 still in the room — kick did not take"}\n`);
  process.stdout.write(`  (id3 before: ${before})\n`);
  if (!(kicked && banHolds)) process.exit(1);
}

main().catch((e) => { process.stderr.write(`\nkick-test failed: ${e instanceof Error ? e.stack ?? e.message : e}\n`); process.exit(1); });
