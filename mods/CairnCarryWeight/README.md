# CairnCarryWeight

Makes a heavier bag tire you faster while climbing, and lets you override any item's weight.

## What it does

Cairn already computes a per-item weight (`InventoryItem.GetWeight`) and sums it into
`ClimbingV2PawnController.CurrentInventoryWeight`, but vanilla only uses that for **bag
capacity** — it is never wired into stamina. (Verified from the binary: the weight getter
has zero callers in the stamina path; drain is driven purely by effort, consumption speed,
and the stamina-ratio cost curve.)

This mod connects the two: it postfixes the per-limb `CurrentEffortCostMultiplier` — the
same multiplier the game uses to make holds cost more as you tire — and scales it by how
full your bag is:

```
fullness   = currentInventoryWeight / maxInventoryWeight
effortCost *= 1 + (FullBagEffortMultiplier - 1) * fullness
```

So an **empty** bag is exactly vanilla (1x), and a **full** bag (at the game's
`maxInventoryWeight`) costs `FullBagEffortMultiplier`x — the single number you tune.
"Full" tracks the game's live `InventoryTweakables.maxInventoryWeight` (ctor default 20,
Addressable-overridable), so the knob stays meaningful if that cap is ever retuned.

Weight is per-pawn, so each climber is taxed only by their **own** bag. Co-op safe: it has
no interaction with CairnCoop (which never touches stamina or inventory weight).

## Config (`UserData/MelonPreferences.cfg`, category `CairnCarryWeight`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `FullBagEffortMultiplier` | `2.0` | Climbing effort cost at a full bag. `1` = weight has no effect; `2` = a full bag makes holds cost twice the stamina. Scales linearly to 1x at empty. |
| `ApplyWeightOverrides` | `true` | Apply the per-item overrides below. |
| `WeightOverrides` | `""` | Per-item weight multipliers (see below). |

### Per-item weight overrides

A **true override**: the multiplier scales the item's vanilla weight everywhere the game
uses it — bag capacity, the in-game weight readout, and this mod's drain. Format is a
comma-separated list of `ITEM_ID=multiplier`, using the `InventoryItemStringIdEnum` names:

```
WeightOverrides = "ITEM_PITON=2.0, ITEM_FLASK=0.5, ITEM_NUTS=0"
```

- `2.0` = twice as heavy, `0.5` = half, `0` = weightless.
- Multiplier (not absolute) so stack counts and container contents still scale correctly.
- Unknown item names or bad numbers are logged and skipped.
- Edits to this line are re-parsed live.

Item names are the `ITEM_*` constants from `InventoryItemStringIdEnum` (e.g. `ITEM_PITON`,
`ITEM_FLASK`, `ITEM_CRYSTAL_PITON`); see `dump/dump.cs` for the full list of 315.

## Tuning

The knob is the full-bag multiplier itself, so tuning is direct: pick what a maxed bag
should feel like (`2.0` = double effort) and the curve fills in. No calibration against raw
weight units is needed — the mod normalizes by the live `maxInventoryWeight`.

Game constants this is built on (binary defaults, from the tweakable ctors):
`maxInventoryWeight = 20`, `baseStamina = 10`, `staminaLossSpeed = 0.5`,
`criticalStaminaStateDuration = 5` (eject grace window). These can be overridden by the
shipped Addressable asset; to read the live values, query via CairnDevTools:

```
return Il2Cpp.InventoryTweakables.Instance.maxInventoryWeight;
return <controller>.CurrentInventoryWeight;   // a real loadout's current weight
```
