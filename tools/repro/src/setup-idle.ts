// One-off HANDS-ON setup: drive two ALREADY-LAUNCHED instances into a formed co-op free-solo session and
// then STOP — no rope-link, no revive, no fall attempts. Leaves both instances idle on the wall, in-room, so
// a human can walk up to the partner ghost and test the diegetic rope request/accept gesture by hand.
//
// Differs from the full loop (cli.ts) exactly by OMITTING linkRope/armEdelweiss/recordRevive — under the new
// accepted-only model the rope is formed by the player's gesture, not the auto-toggle, so we must NOT auto-link.
//
// Assumes both instances are ALREADY running (launched separately with the co-op env vars). Run from the
// package:  npm --prefix tools/repro run setup-idle
import { RunLog } from "./log.ts";
import { bringUp, freezeSurvival, enterFreeSolo, waitInRoom, beginRun, type Instance } from "./steps.ts";
import { killCairn } from "./proc.ts";

const host: Instance = { who: "host", port: 14200 };
const joiner: Instance = { who: "joiner", port: 14201 };

const log = new RunLog();

// bringUp LAUNCHES its own instances (launchCairn). So this script OWNS the process lifecycle: kill any
// running Cairn FIRST (a stale instance holds port 14000 → the new host dies with SocketException 10048),
// then launch fresh. Do NOT pre-launch instances by hand before running this.
log.line("HANDS-ON setup: kill any running Cairn, then bring both up into free-solo, form the room, then idle.");
await killCairn();
beginRun(); // mark run start = now, so per-instance log scraping picks up the instances we're about to launch
await bringUp(host, log);
await freezeSurvival(host, log);
const hostSlot = await enterFreeSolo(host, log);
await bringUp(joiner, log);
await freezeSurvival(joiner, log);
await enterFreeSolo(joiner, log, [hostSlot]);

await waitInRoom([host, joiner], log);

log.line("──────────── READY FOR HANDS-ON ────────────");
log.line("Both climbers are on the wall and in the co-op room. NO rope is connected (accepted-only model).");
log.line("Walk one climber's reach/approach toward the other's ghost and press Interact:");
log.line("  • on an unconnected partner → 'request rope' → sends a request");
log.line("  • the other instance then shows 'accept rope from X' on the requester's ghost → press to accept");
log.line("  • once accepted, the bespoke rope forms on both sides.");
log.line("Mod logs: P:\\Steam\\steamapps\\common\\Cairn\\MelonLoader\\CairnCoop\\<newest two>.log (grep 'gesture:' / 'rope:').");
