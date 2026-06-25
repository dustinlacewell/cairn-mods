# CairnCoop

Steam co-op for **Cairn**, built on the game's own shipped-but-disabled multiplayer. Two (or more)
climbers share a mountain; each can rope to the nearest partner so a fall is caught at the partner's
position — a free-solo belay where your partner is the anchor.

No game files are modified. The mod drives Cairn's native `SharedRopeGamemode` and supplies the
transport (a reimplementation of the studio's relay) over Steam datagrams or local loopback.

---

## Install

1. Install [MelonLoader](https://melonloader.co/) for Cairn (net6).
2. Install **[CrossMenuLib](../CrossMenuLib/)** — CairnCoop's rope toggle lives on its radial menu.
3. Drop `CairnCoop.dll` in `Cairn/Mods/`. (Building from source auto-installs it — see DEVELOPERS.md.)

Load order is handled automatically (`[MelonOptionalDependencies("CrossMenuLib")]`).

## Use

- **F4** — open the co-op panel.
- **Host**: click *Host co-op*. **Join**: a friend running the mod and hosting appears under
  *Friends hosting co-op* — click to join. Be **on a mountain** when you host/join; remote climbers
  spawn into your level.
- **Rope to your partner**: hold **LT + RT** and flick the wheel **UP**. This connects your belay
  rope to the **closest** remote player. Roping is **mutual** — when you connect, your partner's
  rope connects back automatically; either of you flicking up again disconnects both.
  - The wedge is **greyed out** when no partner is in range.
  - The rope is **off by default** — you connect it on demand, you are not auto-roped.
- **F10** — recover a stuck black screen / frozen camera (rare focus-juggling artifact).

## What "roped" does

While roped, a fall is caught at your partner's position with the game's own piton/swing physics.
The catch distance is your distance from the partner at the moment you fell. A partner who is
themselves falling/dead stops being an anchor, so a two-person fall drops both (the team-fall rule).

## Status & limits

- Free-solo partner catch works end-to-end, both directions (user-verified).
- Known rough edge: a deliberate **jump off the wall** (`JumpDownFromWall`) free-falls through its
  jump arc before the belay engages, so a *jumped* fall overshoots; a plain *slip* catches tight.
  See `re/systems/climber/fall-distance.md` for the diagnosis and the planned fix.
- Tested primarily via a two-instance loopback rig on one machine; Steam play uses the same paths.

## For developers

Architecture, internals, the netplay/belay/fall mechanics, the doc index, and the build/test
workflow are in **[DEVELOPERS.md](DEVELOPERS.md)**.
