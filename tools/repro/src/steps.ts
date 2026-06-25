// The four scenario steps, each as a small function over an Instance pair. Orchestration (the loop, the
// CLI) lives in cli.ts; the C# lives in snippets.ts; the transport in eval.ts. A step here is just
// "send the right snippet/command, then poll the right probe until it settles".

import { cmd, evalCs, evalFile } from "./eval.ts";
import { awaitEvent } from "./events.ts";
import type { RunLog } from "./log.ts";
import { launchCairn } from "./proc.ts";
import * as cs from "./snippets.ts";
import { poll, PollTimeout } from "./wait.ts";

/** One game instance: a console port, a human label, and its co-op role + 1-based ordinal (host=1).
 *  `role`/`index` default to the legacy 2-instance meaning (host vs joiner) when omitted. */
export interface Instance {
  readonly who: string;
  readonly port: number;
  readonly role?: "host" | "joiner";
  readonly index?: number; // 1-based: host=1, joiners 2,3,…
}

/** The co-op role for an instance (host=1, everyone else a local joiner). */
export const roleOf = (inst: Instance): "host" | "joiner" =>
  inst.role ?? (inst.who === "host" ? "host" : "joiner");
/** The 1-based ordinal (host=1, joiners 2,3,…) used for CAIRNCOOP_INSTANCE + loopback port. */
export const indexOf = (inst: Instance): number =>
  inst.index ?? (roleOf(inst) === "host" ? 1 : 2);

/** Wall-clock ms at which this run started launching — used to pick THIS run's per-instance log files
 *  (the mod writes a fresh <ts>_pid<pid>.log per launch). Set by launchPair / set externally for --no-launch. */
export let runStartMs = Date.now();
export function markRunStart(ms: number): void { runStartMs = ms; }

// ── STEP 0: launch ────────────────────────────────────────────────────────────────────────────────

/** Mark the start of THIS run (so per-instance log discovery picks up the right files). Call once, before
 *  launching the host. Separate from launch so the caller can sequence host-then-joiner bring-up. */
export function beginRun(): void {
  markRunStart(Date.now());
}

/** Launch one instance and block until its console answers an eval (the real readiness signal). */
export async function bringUp(inst: Instance, log: RunLog): Promise<void> {
  log.line(`STEP 0: launching ${inst.who}…`);
  await launchCairn(roleOf(inst), indexOf(inst));
  await waitForConsole(inst, log, 120_000);
  log.line(`STEP 0: ${inst.who} console up.`);
}

// ── STEP 0.75: wait for the co-op room to form (autojoin settles) ─────────────────────────────────────
// EDGE-TRIGGERED: each instance's CairnCoop driver emits "room-formed" the instant it reaches the native
// IN_ROOM state (GameDriver). We await that event per instance — no log-grep, no interval polling. The
// `room-formed` may have fired before this await began (the host forms its room early), so we also check
// the live session flag first; if already in session, we don't wait for a (possibly missed) event.
export async function waitInRoom(insts: Instance[], log: RunLog): Promise<void> {
  for (const inst of insts) {
    const already = await evalCs(inst.port, cs.IN_COOP_SESSION).catch(() => "insession=false");
    if (already === "insession=True") {
      log.line(`STEP 0.75: ${inst.who} already in co-op session.`);
      continue;
    }
    const ev = await awaitEvent(inst.port, "room-formed", 180_000, "driver").catch((e) => {
      throw new Error(`STEP 0.75: ${inst.who} room-formed await failed (${e instanceof Error ? e.message : e}).`);
    });
    if (ev === null) {
      throw new Error(`STEP 0.75: ${inst.who} never emitted room-formed within 180s — autojoin handshake stalled.`);
    }
    log.line(`STEP 0.75: ${inst.who} room-formed (event seq ${ev.seq}).`);
  }
  log.line("STEP 0.75: both InRoom (co-op room formed).");
}

/** Block until the instance answers a trivial eval (console listening + main-thread pumping). */
export async function waitForConsole(inst: Instance, log: RunLog, timeoutMs: number): Promise<void> {
  await poll(
    () => evalCs(inst.port, 'return "pong";', 8000),
    (v) => v === "pong",
    { timeoutMs, intervalMs: 500, onTick: (v, ms) => log.state(inst.who, `console-probe @${ms}ms: ${v}`) },
  );
}

// ── STEP 0.5: freeze survival ───────────────────────────────────────────────────────────────────────

/**
 * Turn on the survival freeze for one instance (no starving mid-run), then verify HungerRatio pinned to 1.
 * NOTE: infinite-edelweiss is NOT armed here — it patches InventoryDataBase.Remove, and arming it before the
 * climbing scene / inventory has loaded wedged the free-solo load (the Remove prefix can fire during
 * inventory init). It's armed later, after the pawn is live, via {@link armEdelweiss}.
 */
export async function freezeSurvival(inst: Instance, log: RunLog): Promise<void> {
  const sr = await cmd(inst.port, "survival on");
  log.line(`STEP 0.5: ${inst.who} survival on → ${sr}`);
  const v = await poll(() => evalCs(inst.port, cs.HUNGER_RATIO), (x) => x === "1.00", {
    timeoutMs: 10_000,
    intervalMs: 500,
  }).catch((e) => `(${e instanceof Error ? e.message : e})`);
  log.line(`STEP 0.5: ${inst.who} HungerRatio=${v}`);
}

/**
 * Arm infinite-edelweiss (resurrection item not consumed → revive every attempt). Done AFTER the climbers
 * are in gameplay, never during scene/inventory load — the Remove-skipping patch can wedge the load if armed
 * earlier. The patch is otherwise inert (only fires for a ResurrectionItem Remove while the toggle is on).
 */
export async function armEdelweiss(insts: Instance[], log: RunLog): Promise<void> {
  for (const inst of insts) {
    const r = await cmd(inst.port, "edelweiss on");
    log.line(`STEP 2.5: ${inst.who} edelweiss on → ${r}`);
  }
}

// ── STEP 1: into free solo ────────────────────────────────────────────────────────────────────────

/**
 * Get ONE instance into a free-solo climb and BLOCK until the climber pawn actually exists in the scene.
 * We enter by CONTINUING the latest savegame (always a throwaway nonce free-solo save) — the proven jump
 * (see START_FREESOLO): wait for the MainMenu to exist, fire `mustContinueSavegame=true;
 * TransitionToContinueIfAvailable()`, then poll until the climber reports free solo (pawn live). The caller
 * brings the host FULLY into gameplay (loadingState==GameStarted, via waitFullyLoaded) BEFORE launching the
 * joiner: SavegameManager.CanLoad rejects the joiner's continue while the host's load is still in flight
 * (continuing the SAME save), and the joiner's autojoin only forms the co-op room once the host is live.
 */
export async function enterFreeSolo(inst: Instance, log: RunLog, avoidSlots: number[] = []): Promise<number> {
  // Wait for the MainMenu to be up (the continue jump needs it).
  await poll(() => evalCs(inst.port, cs.MENU_READY), (v) => v === "ready", {
    timeoutMs: 120_000,
    intervalMs: 600,
    onTick: (v, ms) => log.state(inst.who, `menu-probe @${ms}ms: ${v}`),
  });
  // SELECT A FREE-SOLO NONCE SAVE as the active slot before continuing. This (a) guarantees a free-solo WALL
  // climb (not the gym/story), (b) NEVER touches the real beaten-the-game save (free-solo filter excludes
  // it), and (c) for the joiner, picks a slot DISTINCT from the host's so the two open different on-disk
  // files — the same-save case wedges the 2nd at step=16 forever (FileShare.None; re/systems/save-load/).
  const chosenSlot = await selectFreeSoloSlot(inst, log, avoidSlots);
  // FIRE-CONFIRM-RETRY: a single TransitionToContinueIfAvailable fire can NO-OP if it races the menu's
  // fireable window (gated on !isAnimating). So fire, confirm we left the menu, and re-fire if not.
  await fireContinueUntilLaunched(inst, log);
  const ok = await reachFreeSolo(inst, log, 120_000);
  if (!ok) throw new Error(`STEP 1: ${inst.who} never reached free solo after launch`);
  log.line(`STEP 1: ${inst.who} in free-solo gameplay (pawn live, slot ${chosenSlot}).`);
  return chosenSlot;
}

/** Make a free-solo nonce save (slot != avoidSlot) the active story slot; returns the chosen slot byte.
 *  KEEPS RETRYING on "no-distinct-freesolo-save" / "previews-not-cached": the menu builds its Story preview
 *  cache asynchronously at boot (CacheEverySavePreview), so a PARTIALLY-built cache transiently looks like
 *  "no eligible save". Only `selected=` and a hard `err:` are terminal; everything else re-polls until the
 *  cache fills (or the timeout — which on a profile with genuinely <2 free-solo saves is the correct failure). */
async function selectFreeSoloSlot(inst: Instance, log: RunLog, avoidSlots: number[]): Promise<number> {
  const avoidStr = avoidSlots.length ? avoidSlots.join(",") : "none";
  const sel = await poll(
    () => evalCs(inst.port, cs.SELECT_FREESOLO_SLOT(avoidSlots)).catch((e) => `err:${e instanceof Error ? e.message : e}`),
    (v) => v.startsWith("selected=") || v.startsWith("err:"),
    { timeoutMs: 60_000, intervalMs: 1000, onTick: (v, ms) => log.state(inst.who, `select-freesolo @${ms}ms (avoid ${avoidStr}): ${v}`) },
  ).catch((e) => (e instanceof PollTimeout ? "timeout(no distinct free-solo save appeared)" : `err:${e instanceof Error ? e.message : e}`));
  if (!sel.startsWith("selected=")) {
    throw new Error(`STEP 1: ${inst.who} could not select a free-solo save${avoidSlots.length ? ` distinct from slots ${avoidStr}` : ""} (${sel}). ` +
      `N-instance co-op needs ≥${avoidSlots.length + 1} free-solo nonce saves on this profile.`);
  }
  const slot = Number.parseInt(sel.slice("selected=".length), 10);
  log.line(`STEP 1: ${inst.who} selected free-solo slot ${slot}${avoidSlots.length ? ` (avoided ${avoidStr})` : ""}.`);
  return slot;
}

/** Fire the continue-save jump and re-fire until the instance has left the MainMenu (load started). */
async function fireContinueUntilLaunched(inst: Instance, log: RunLog): Promise<void> {
  await poll(
    async () => {
      const confirm = await evalCs(inst.port, cs.LAUNCH_CONFIRM).catch((e) => `err:${e instanceof Error ? e.message : e}`);
      if (confirm === "launched") return confirm;
      // Still at the menu. Re-fire only when not mid-animation (else the fire no-ops).
      if (confirm.includes("animating=False")) {
        const r = await evalCs(inst.port, cs.START_FREESOLO).catch((e) => `err:${e instanceof Error ? e.message : e}`);
        log.state(inst.who, `continue-fire → ${r}`);
      }
      return confirm;
    },
    (v) => v === "launched",
    { timeoutMs: 60_000, intervalMs: 1500, onTick: (v, ms) => log.state(inst.who, `launch-confirm @${ms}ms: ${v}`) },
  );
  log.line(`STEP 1: ${inst.who} continue-save launched (left menu).`);
}

async function reachFreeSolo(inst: Instance, log: RunLog, timeoutMs: number): Promise<boolean> {
  try {
    await poll(() => evalCs(inst.port, cs.IN_FREESOLO), (v) => v.startsWith("freesolo=True"), {
      timeoutMs,
      intervalMs: 500,
      onTick: (v, ms) => log.state(inst.who, `freesolo-probe @${ms}ms: ${v}`),
    });
    return true;
  } catch (e) {
    if (e instanceof PollTimeout) return false;
    throw e;
  }
}

// ── STEP 2: link rope ───────────────────────────────────────────────────────────────────────────────

/**
 * Wait for both to be in a co-op session, then COMPLETE THE ROPE HANDSHAKE programmatically. The rope is
 * an accepted-request handshake now (commits fcca117/bc08e1e/609b72d), not a one-shot toggle: a single
 * ToggleRope only SENDS a request — the partner must ACCEPT (via the diegetic ghost reach, or here via the
 * ForceRope test entry). So we drive ForceRope on BOTH sides in rounds — one side's request is accepted by
 * the other's next call — and poll RopeConnected until both report roped. Also waits for the partner roster
 * to populate first (GetRemotePlayers can lag the room-formed event by a few seconds; the old 15s toggle
 * timeout fired before it filled). See re/systems/coop/BLOCKER-repro-rope-handshake.md.
 */
export async function linkRope(insts: Instance[], log: RunLog): Promise<void> {
  for (const inst of insts) {
    await poll(() => evalCs(inst.port, cs.IN_COOP_SESSION), (v) => v === "insession=True", {
      timeoutMs: 60_000,
      intervalMs: 500,
      onTick: (v, ms) => log.state(inst.who, `session-probe @${ms}ms: ${v}`),
    });
    log.line(`STEP 2: ${inst.who} in co-op session.`);
  }
  // Drive the handshake in rounds until BOTH report roped (the request on one side, the accept on the other).
  const roped = async (inst: Instance) => (await evalCs(inst.port, cs.ROPE_CONNECTED)) === "roped=True";
  const deadline = Date.now() + 45_000;
  while (Date.now() < deadline) {
    for (const inst of insts) {
      const r = await evalCs(inst.port, cs.FORCE_ROPE);
      log.state(inst.who, `force-rope: ${r}`);
    }
    await new Promise((res) => setTimeout(res, 1200)); // let the request/accept datagrams round-trip (~1Hz)
    const states = await Promise.all(insts.map(roped));
    if (states.every(Boolean)) {
      for (const inst of insts) log.line(`STEP 2: ${inst.who} roped=True`);
      log.line("STEP 2: both roped.");
      return;
    }
  }
  const last = await Promise.all(insts.map((i) => evalCs(i.port, cs.ROPE_CONNECTED)));
  throw new Error(`rope handshake did not complete; last: ${insts.map((i, k) => `${i.who}=${last[k]}`).join(" ")}`);
}

// ── STEP 3: force a REAL exhaustion fall (not an in-place kill) ─────────────────────────────────────

/**
 * Eject both climbers via the game's own exhaustion path so they FREE-FALL down the wall — the scenario the
 * co-op edelweiss-revive bug actually needs (the old TryKillClimber killed in place and never produced a
 * fall, which doesn't exercise the rewind scrub-back-up). Two steps, both live-verified 2026-06-17
 * (re/systems/climber/stamina-eject-chain.md):
 *   1. crank-stamina-drain.cs — raise the game's own stamina difficulty knobs so a load-bearing climber
 *      tires in seconds via the real Tiring → drain path (NOT a poke at a derived field).
 *   2. detach-limb.cs — Drop one holding limb so the climber is genuinely load-bearing on the rest; the
 *      remaining limbs enter the Tiring branch, drain to empty, the 5s critical-stamina grace elapses, and
 *      the controller's own Fall(Exhausted) sends the climber off the wall.
 * The fall + edelweiss prompt then evaluate on the natural path. Errors are logged and re-thrown.
 */
export async function exhaustClimbers(insts: Instance[], log: RunLog): Promise<void> {
  for (const inst of insts) {
    try {
      const crank = await evalFile(inst.port, "crank-stamina-drain");
      const reach = await evalFile(inst.port, "reach-to-exert");
      log.line(`STEP 3: ${inst.who} exhaust → ${crank} | ${reach}`);
    } catch (e) {
      log.line(`STEP 3: ${inst.who} exhaust FAILED → ${e instanceof Error ? e.message : e}`);
      throw e;
    }
  }
}

/** Restore the stamina difficulty knobs to defaults (so a run leaves the game clean). Best-effort. */
export async function resetStaminaDrain(insts: Instance[], log: RunLog): Promise<void> {
  for (const inst of insts) {
    const r = await evalFile(inst.port, "reset-stamina-drain").catch((e) => `err:${e instanceof Error ? e.message : e}`);
    log.line(`reset stamina drain: ${inst.who} → ${r}`);
  }
}

/**
 * Accept the edelweiss prompt on both instances the way the player does. The popup's accept handler is a
 * no-op until its fade-in completes (popupFadedIn && acceptInput), so a single early call silently does
 * nothing — we POLL ACCEPT_PROMPT until each side reports "accepted" or "no-popup" (popup already gone /
 * never raised). VERIFIED LIVE: once the gate is satisfied the handler revives the climber. Per-instance,
 * bounded; a side that never gets ready times out (logged, not thrown — must not abort the capture).
 */
export async function acceptPrompts(insts: Instance[], log: RunLog, timeoutMs = 8000): Promise<void> {
  for (const inst of insts) {
    const done = (r: string) => r === "accepted" || r === "no-popup";
    const r = await poll(() => evalCs(inst.port, cs.ACCEPT_PROMPT).catch((e) => `err:${e instanceof Error ? e.message : e}`), done, {
      timeoutMs,
      intervalMs: 300,
      onTick: (v, ms) => log.state(inst.who, `accept @${ms}ms: ${v}`),
    }).catch((e) => `timeout(${e instanceof Error ? e.message : e})`);
    log.line(`STEP 3b: ${inst.who} accept → ${r}`);
  }
}
