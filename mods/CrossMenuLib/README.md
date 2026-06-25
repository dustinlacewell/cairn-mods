# CrossMenuLib

A MelonLoader library mod for **Cairn** that lets other mods add actions to the in-game cross-menu
(the radial LT / D-pad quick-action wheel) — and add **their own extra menus** selected by holding
**LT + a chord** (e.g. LT + RT).

It does this by *borrowing the game's own cross-menu* rather than drawing a parallel UI: your actions
render with the real icons, hold-to-confirm fill, availability greying, count badges, and audio,
because the vanilla `CrossMenuUI`/`QuickInventoryUI` machinery drives them end-to-end.

## What you get

- **Add actions to the vanilla LT wheel's free slots** (the base menu).
- **Define new chord menus** — `LT + RightTrigger`, `LT + LeftBumper`, multi-button chords, etc. — each
  with up to four directional actions.
- **Built-in Lucide icon set** (1900+ glyphs by name) or supply your own `Sprite`.
- Per-action **availability**, **count badge**, **execute** and **failed-execute** callbacks.
- Zone-restriction (Tenzing) and audio are handled for you so custom actions aren't silently blocked
  or mute.

## Install

Drop `CrossMenuLib.dll` into `Cairn/Mods/`. Load it before (or alongside) any mod that depends on it.
Built against net6 / Il2Cpp proxies; same `GameDir` convention as the other Cairn mods.

## Quick start (for a consumer mod)

```csharp
using CrossMenuLib;

// 1) Define a menu selected by LT + RT:
CrossMenu.DefineMenu("mymod.combat", Modifier.RightTrigger);

// 2) Register up to four directional actions into it:
CrossMenu.Register(new CrossMenuAction {
    Id        = "mymod.grapple",      // stable unique id; re-registering replaces
    Label     = "Grapple",
    Menu      = "mymod.combat",
    Direction = MenuDir.Up,
    IconName  = "anchor",             // a Lucide icon name
    DisplayCount = true,
    OnExecute   = () => DoGrapple(),
    IsAvailable = () => HaveRope(),   // optional — greys out when false
    GetCount    = () => RopeCount(),  // optional — the badge number
});
```

To add an action to the **vanilla LT wheel** instead, omit `Menu` (it defaults to `CrossMenu.BaseMenu`)
and pick a `Direction` that vanilla leaves free — occupied vanilla slots are not overwritten.

Everything is safe to call at mod-init, before the HUD exists; the library binds to the live menu when
it appears and re-binds if the HUD is rebuilt (save load, scene change). `CrossMenu.Ready` tells you
when it's actively driving the menu.

## API surface

| Member | Purpose |
|---|---|
| `CrossMenu.DefineMenu(id, Modifier chord)` | Create/refresh a chord-selected menu (LT + chord). |
| `CrossMenu.Register(CrossMenuAction)` | Add or replace an action (by `Id`). |
| `CrossMenu.Unregister(id)` | Remove an action. |
| `CrossMenu.Ready` | True once the library is driving the live menu. |
| `CrossMenu.BaseMenu` | The vanilla LT wheel id (default `Menu`). |
| `Modifier` | `RightTrigger`, `LeftBumper`, `RightBumper` — combine with `|`. |
| `MenuDir` | `Up`, `Right`, `Down`, `Left`. |
| `CrossMenuAction` | Your action: identity, menu/direction, icon, callbacks. |

## Notes & limits

- **Right trigger collides while climbing.** RT is free only while walking; while climbing the game
  also binds RT to hang-mode / ice-gear. A `LT + RT` menu is reliable on the ground; in-wall use needs
  care (a different chord, or suppressing the collision).
- One chord = one menu; the first mod to claim a chord wins (a warning is logged for collisions).
- Custom action types are allocated ≥ 100 (vanilla uses 0–9).

For how it works internally — the layer-borrow mechanism, the show/fade fields, and the gotchas that
shaped this design — see [DEVELOPERS.md](DEVELOPERS.md) and the full reverse-engineering write-up at
`re/systems/ui/crossmenu/crossmenu-internals.md`.
