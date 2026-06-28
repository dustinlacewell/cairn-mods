# Cairn × Archipelago

An [Archipelago](https://archipelago.gg) multiworld randomizer for Cairn,
The Game Bakers' climbing game. Two halves:

| Half | Where | What |
|---|---|---|
| apworld | `ap/apworld/cairn/` | Python world package loaded by the Archipelago generator/server |
| client mod | `mods/CairnArchipelago/` | MelonLoader mod that connects the running game to the AP server |

## How it maps onto the game

- **Locations** — the game's numbered story-beat sensors
  (`StoryEventSensorStringIdEnum`, names like `_02010_FirstLanding`). They fire
  automatically as you climb past each milestone of the route; the mod patches
  `StoryEventSensor.OnTrigger` and reports `LOCATION_BASE + sensor value`.
- **Items** — the game's inventory items (`InventoryItemStringIdEnum`), granted
  on receipt via the same `InventoryDataBase.Add` path the game's loot flow
  uses, plus abstract **Progressive Altitude Permits**.
- **Regions** — sensor codes group into decade-chapters (01x, 1xx, 2xx, …).
  Chapter *k* logically requires *k* permits. (Mod-side gate enforcement is v2.)
- **Goal** — the final chapter's last story sensor; its AP id ships in
  slot_data as `goal_location` and the mod sends `ClientGoal` when it checks.

## ID scheme (no lookup tables anywhere)

```
AP item id     = 71_110_000 + InventoryItemStringIdEnum value
AP location id = 71_120_000 + StoryEventSensorStringIdEnum value
```

Both sides derive ids arithmetically from the game's own stable enums.

## Regenerating data tables

`ap/apworld/cairn/data.py` is generated from the Il2CppDumper output:

```
python ap/tools/extract_ap_data.py     # reads dump/dump.cs
```

## Testing generation

```
# paths below are relative to this ap/ dir (lives at cairn-mods/mods/CairnArchipelago/ap/);
# <your-archipelago-dir> is wherever you installed Archipelago (e.g. D:\ext\Archipelago)
cmd /c mklink /J <your-archipelago-dir>\worlds\cairn ap\apworld\cairn
cd <your-archipelago-dir>
.\.venv\Scripts\python Generate.py --player_files_path ap\test\Players ^
    --outputpath ap\test\output
```

(.venv is Python 3.13 — AP rejects 3.14 — with generation deps only, no kivy.)

## Building the mod

```
cd mods\CairnArchipelago && dotnet build -c Release
```

Compiles against the game's Il2Cpp proxy assemblies (so the build validates all
game API usage) and auto-installs: dll → `Mods/`,
Archipelago.MultiClient.Net + Newtonsoft.Json → `UserLibs/`.

In-game: set `[CairnArchipelago]` prefs in `UserData/MelonPreferences.cfg`
(`Enabled`, `Host`, `Port`, `SlotName`, `Password`). F8 dumps a world scan
(interaction providers with gpeIds, sensors, item library) to
`UserData/CairnArchipelago/scans/` — the dataset for v2 loot-pickup locations.

## Roadmap

- **v1 (this)** — story-sensor checks, item grants, goal, scanner, DeathLink send.
- **v2** — loot pickups as locations (suppress vanilla grant on `LootProvider.Interact`,
  send check instead); chapter-gate enforcement using permits; DeathLink receive
  (kill/pass-out the climber); in-game HUD toasts for sent/received items.
- **v3** — options (lootsanity, permit granularity, trap items), logic refinement
  from playtest scans, release packaging (`cairn.apworld` + mod zip).
