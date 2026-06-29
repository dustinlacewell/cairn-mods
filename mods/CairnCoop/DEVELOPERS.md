# CairnCoop — developer documentation

How the mod is built, how the pieces fit, and where the deep knowledge lives. CairnCoop is a
MelonLoader (net6, Il2CppInterop) mod that runs Cairn's own netplay stack and adds a free-solo
partner belay on top. It writes no game files; everything is transport + session-start +
client-side belay driving.

> **Read first for the mechanics:** `re/systems/coop/belay-design.md` (how the catch is manufactured),
> then `re/systems/climber/fall-distance.md` (what governs fall distance) and
> `re/systems/climbot/rope-length.md` (what governs rope length). This file is the *map*; those
> are the *territory*.

---

## The shape (layers, top-down)

```
Core (MelonMod)                     session lifecycle, UI, transport ownership, IModChannel router
 ├─ GameDriver                      netplay state machine (register → join room → in-room tick)
 │   ├─ PartnerBelay                the co-op anchor + fall behaviour (rope connect/fall/reel)
 │   │   └─ BelayRig                drives the game's climbot belay via its OWN verbs
 │   └─ SceneHygiene                cosmetic cleanup (stray ropes, ghost visibility)
 ├─ RelayServer (host only)         reimplementation of the studio's dumb relay
 ├─ UdpBridge / SteamRendezvous     transport legs (loopback / Steam datagrams)
 └─ ModLoopback                     mod-private sidechannel for the local 2-instance test
```

The driver only orchestrates; the belay logic and its state live in `PartnerBelay`. A change to
"how the rope behaves" is a `PartnerBelay`/`BelayRig` change, not a `GameDriver` one.

## File map

| File | Responsibility |
|---|---|
| `Core.cs` | MelonMod entry, F4 panel, role/session control (Host/Joiner/LocalJoiner), Steam/UDP transport plumbing, **`IModChannel` router** for mutual-rope, and wiring of the diegetic ghost-reach rope gesture. |
| `GameDriver.cs` | The netplay **state machine** (`Stage` enum + `TickInner`): build client, register, join the room **only once the climber pawn has spawned**, then the 1 Hz in-room tick that delegates to `PartnerBelay` + `SceneHygiene`. Also `DescribeGameState` (the diagnostic dump) and the tweakables/clientState field-poking. |
| `PartnerBelay.cs` | **The belay system.** Resolve the closest partner from the roster, create/keep/tear-down a quickdraw anchor at the partner's frame position on the personal rope, reel the rope length, latch the fall length at fall entry, and the **mutual-rope** toggle (announce + reciprocate via `IModChannel`). Owns all `_partnerAnchor*`/fall-latch state. |
| `BelayRig.cs` | Drives the **game's climbot belay** using its own verbs (`SetSecureClimber`), re-pins the quickdraw-split rope ends, sets the climbot's `idleRopeLength` (the real length authority), keeps the bot near the pawn. Also `SharedRopeInitGate` (Harmony on `SharedRopeGamemode.Init`) and `RopeLengthGuard` (Harmony on `SetLengthSafe`). |
| `SceneHygiene.cs` | Park/hide the now-cosmetic netplay ropes; activate + position remote-player ghosts. Pure visual. |
| `FreeSoloAnchor.cs` | `FreeSoloRecoveryGate` (scoped `InFreeSolo` override so a caught climber can auto-recover to hang-idle) + fall telemetry hooks. |
| `Patches.cs` | `SocketBindPatch` — rebinds the native client's `0.0.0.0:14000` bind to an ephemeral port so two game instances can run on one machine. |
| `Net/Wire.cs` | The native netplay wire protocol (opcodes, framing). |
| `Net/ModWire.cs` | The mod-private opcodes `CTSR_MOD_ROPE`/`STCR_MOD_ROPE` (kept out of `Net/Wire.cs` so mod traffic never reaches the game socket). |
| `Net/RelayServer.cs` | The relay: registration, rooms, frame fan-out, blind gamemode-blob rebroadcast, ARQ. Raises `ModRopeReceived` for mod-rope (never routes it — see below). |
| `Net/UdpBridge.cs` | Loopback UDP endpoint (`127.0.0.2:14000`) the native client talks to. |
| `Net/ModChannel.cs` | `IModChannel` + `RopeState` — the SOLID seam for mutual-rope. `PartnerBelay` depends only on this. |
| `Net/ModLoopback.cs` | Mod-only UDP (`127.0.0.3`, host:14001 ↔ joiner:14002) so mutual-rope is testable on one machine. |
| `Steam/SteamApi.cs`, `Steam/SteamRendezvous.cs` | Steamworks interop + friend discovery/handshake. |

---

## Subsystem 1 — netplay (get two clients into a shared-rope room)

The game is **client-authoritative star topology**, not P2P: every client connects to one relay (the
host's, reimplemented in `RelayServer`). The relay runs no game logic — all climbing/rope state is
client-side. Steam (or local UDP) is just the transport leg into the relay.

- **Transport seam**: the native `NetplayClient` `Connect()`s to `serverAddress:14000`; the mod points
  `NetplayTweakables.serverAddress` at `127.0.0.2` and runs the relay there. `SocketBindPatch` frees the
  bind port so multiple local instances coexist.
- **State machine** (`GameDriver.TickInner`): `WaitingForTweakables → BuildingClient → Registering →
  JoiningRoom → InRoom`. **Join is gated on (a) being in a real location and (b) the local climber pawn
  existing** — joining before the pawn spawns makes the game's `Gamemode.Init` deref a null pawn chain
  and trap (decompiled `Gamemode_Init_Base.c`; this is the fix for the first-join NRE).
- **Relay gotchas** (in `RelayServer`): gamemode blobs (`CTSR_GAMEMODE_RELAY 9` → `STCR 135`) are echoed
  to **all** members **including the sender** — CTC senders never apply locally, the effect runs when the
  blob comes back. Frames stay sender-excluded.
- Protocol details: `re/systems/netplay/wire-protocol.md` (byte layouts) and `re/systems/netplay/netplay-internals.md`.

## Subsystem 2 — the belay (the heart)

Free solo has **no belay rope**; the mod manufactures one by driving the game's own Alpinist
machinery, then anchors it at the partner. Three layers, each a **game verb** (never hand-built Obi
state):

1. **Climbot belay** — `RobotPawnController.SetSecureClimber(true, EnableClimbing)` wires the entire
   belay (attach both rope ends + set `securingRope` + visible) in one call. (`BelayRig.Restore`.)
2. **Anchor** — `lifeline.AddPiton(partnerPos, …)` + `rope.RequestAddQuickdraw(qd)` splits the rope
   into climbot → quickdraw(at partner) → climber. (`PartnerBelay.CreateAnchor`.)
3. **The catch** — `Harness.IsSecured(1)` = (#`LogicalQuickdraw` holders on `securingRope` ≥ 1).

Three non-obvious fixes are each load-bearing (full detail in `re/systems/coop/belay-design.md`):
- **Re-assert `lifeline.securingRope = robot.GetRope()` every tick** — the gamemode redirects it to the
  inactive spare rope every frame.
- **`PinRopeEnds`** — the quickdraw split leaves the new outer ends FREE (droop into the floor);
  re-pin first-part.Begin → robot collider and last-part.End → harness collider.
- **Length authority is the climbot's `idleRopeLength`**, NOT `SetLengthSafe` — the climbot ramps the
  rope's last part toward `idleRopeLength` every frame, bypassing our `RopeLengthGuard`. Drive that
  field; freeze it at fall entry. See `re/systems/climbot/rope-length.md`.

### Fall distance (what actually governs how far you drop)

`re/systems/climber/fall-distance.md` is authoritative. Summary: fall distance =
‖climber − last-holding-piton‖ at the moment the rope goes taut (`SecuredFalling.FixedUpdate` reads
`Lifeline.GetRemainingSecuringRopeDistanceOnLifeline`). The **climbot leg is type-filtered out** of
that calculation — it cannot affect fall distance. The remaining overshoot is `JumpDownFromWall`
free-falling through its jump arc before `SecuredFalling` engages (slips catch tight; jumps overshoot).

## Subsystem 3 — mutual rope (auto-reciprocate)

Roping is a shared two-party fact: when A ropes to B, B's mod ropes back; either toggling off drops
both. The SOLID seam is **`IModChannel`** (`Net/ModChannel.cs`) — `PartnerBelay` only `Broadcast`s its
rope-intent and reacts to `OnReceived`, knowing nothing about transport. `Core` is the single router
implementing it (it owns every transport).

**Why a mod-private opcode, never the game protocol:** the game's `NetplayClient` **traps** on
unknown opcodes, and `SharedRopeGamemode.OnReceiveData` **traps** on an unknown gamemode-blob sub-key
(decompiled `SharedRopeGamemode_OnReceiveData.c` — dict-miss → `swi(3)`). So mod traffic must never
reach the game socket. Opcodes `CTSR_MOD_ROPE=10` / `STCR_MOD_ROPE=136` are mod-only.

Per transport:
- **Host**: the relay raises `ModRopeReceived` (it never routes mod-rope to a link — that could be a
  game socket); `Core` fans it to the other members and applies it locally (host ropes back).
- **Steam joiner**: `Core.OnSteamFromHost` peels `STCR_MOD_ROPE` **before** forwarding to the game
  socket.
- **Local 2-instance test**: the joiner's GAME talks straight to the relay (no mod inbound), so a
  `ModLoopback` (mod-only UDP, fixed host/joiner port pair) carries it.

No secret on mod-rope (the relay authenticates by the registered link; reusing `ValidateSecret` would
RESET the game client on mismatch, since the mod can't see the client's internal secret). State
**self-heals** via a 1 Hz re-announce (covers a lost toggle + syncs a late joiner). `OnRemoteRopeState`
mirrors without re-announcing → no ping-pong.

---

## Build, deploy, test

- **Build/install**: `dotnet build mods/CairnCoop/CairnCoop.csproj -c Release` auto-copies the dll to
  `Cairn/Mods/`. Compile-only (game running, dll locked): `-p:NoInstall=1`. The dll is locked while
  instances run — **kill → build → verify → launch**.
- **Verify the deploy** (a silent failed install shipped nothing for three rounds once): grep the build
  output for `error CS|error MSB|error NU` (NOT bare "error" — it false-positives on "0 Error(s)") AND
  check the installed dll's `LastWriteTime` is fresh. A forced rebuild (`-t:Rebuild`) guarantees the
  install target runs even when nothing changed.
- **Two-instance local test**: `tools/run-local-coop-test.ps1` launches a host and a local-joiner
  instance. They start at **MainMenu** — drive each into gameplay (see `cairn-jump-to-gameplay` memory:
  `MainMenu.mustContinueSavegame = true; mm.TransitionToContinueIfAvailable()`), then they auto-join
  the room once the pawn spawns. Consoles are on ports 14200/14201 (CairnDevTools); **port ≠ host/joiner
  order — identify by asking** (`Driver.DescribeGameState()`).
- **Live debugging**: CairnDevTools (`docs/cairn-devtools.md`) — the `belay` command dumps the full
  connection graph in one call; eval `CairnCoop.Core.Instance.Driver.…`. Default to it over log-reading.
- Env hooks: `CAIRNCOOP_AUTOHOST=1` auto-hosts ~25 s after boot; `CAIRNCOOP_AUTOJOIN=local` auto-joins a
  local host.

## Gotchas (hard-won)

- **Never hand-build Obi rope state** (`ManualSetAttachEnd`/`ForceInitialize`) — it over-constrains the
  solver and diverges the rope to kilometres, unrecoverable in place. Use the game's verbs.
- **Never move a load-bearing rope holder at high frequency** — re-sorting the Obi rope every frame
  freezes the game. The anchor follow is 1 Hz with a 0.5 m deadband.
- **Always check BOTH instances by geometry**, never one side by a flag — `secured`/`len` can read fine
  while the rope renders into the floor or the other side is broken.
- The local-joiner's game talks **directly** to the relay (`127.0.0.2:14000`) — the mod cannot intercept
  its inbound game traffic. That's why mod-rope needs the `ModLoopback` for local testing.
- `[MelonOptionalDependencies("CairnAPI")]` orders the load so CairnAPI's UI layer is ready before
  CairnCoop builds its HUDs.

## Documentation index

| Doc | What |
|---|---|
| `re/systems/coop/belay-design.md` | **Read first.** How the free-solo catch is manufactured (the 3 game-verb layers + 3 load-bearing fixes), mistakes-not-to-repeat, the `belay` tool. |
| `re/systems/climber/fall-distance.md` | What governs how far a caught fall travels; the `JumpDownFromWall` overshoot diagnosis + fix direction. |
| `re/systems/climbot/rope-length.md` | The climbot's `idleRopeLength` is the real length authority (bypasses `SetLengthSafe`). |
| `re/systems/climber/secured-falling.md` | The SecuredFalling recovery surface (auto-exit to hang-idle). |
| `re/systems/netplay/wire-protocol.md`, `re/systems/netplay/netplay-internals.md` | The native netplay wire protocol the relay reimplements. |
| `docs/cairn-devtools.md` | The live-interrogation console (the `belay` command, eval). |
