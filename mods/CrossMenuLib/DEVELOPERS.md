# CrossMenuLib — developer documentation

Internal architecture, the reverse-engineered game mechanism it rides on, and the gotchas that shaped
the design. For the user-facing API see [README.md](README.md). For the exhaustive RE write-up of the
game's cross-menu (every class/field/method, verified live), see `re/systems/ui/crossmenu/crossmenu-internals.md`.

---

## 1. Design principle: borrow the real menu, don't reimplement it

The library never draws its own wheel. It writes custom actions into the **vanilla `CrossMenuUI`** and
lets the game's own pipeline do everything: input → hold-fill animation → execute → handler dispatch →
audio → render. The result looks and feels native because it *is* native — only the action data and
the dispatch target are ours.

Two things make this work:

1. **A custom action shows up as a normal action** by giving it a synthetic `CrossMenuActionType` int
   (≥ 100; vanilla uses 0–9) and a `CrossMenuAction` ScriptableObject built at runtime
   (`IconFactory`/`So`), placed into a layer's `MappedAction[]`.
2. **A custom action dispatches to managed code** via one Il2Cpp-injected `CrossMenuActionHandler`
   subclass (`RoutingHandler`) registered into the menu's `handlers` dict under the synthetic int. Its
   vtable overrides (`OnExecute`/`IsAvailable`/`GetCount`/`OnFailedExecute`) route back to the managed
   `Registry` by that int.

## 2. File map

| File | Responsibility |
|---|---|
| `Core.cs` | MelonMod entry: one-time Il2Cpp/Harmony setup, per-frame pump, a built-in self-test menu. |
| `Api.cs` | Public surface: `CrossMenu`, `CrossMenuAction`, `Modifier`, `MenuDir`. |
| `Registry.cs` | Managed store of menus + actions; allocates the synthetic action-type ints; revision counter. |
| `MenuController.cs` | Finds/binds the live menu, applies base-menu registrations, keeps handlers healthy, pumps `ChordMenus`. Re-binds on HUD rebuild. |
| `ChordInput.cs` | Reads the pad each frame; resolves which menu the held chord selects. |
| `ChordMenus.cs` | Per-frame state machine: enter/exit a mod menu, keep it shown, keep the vanilla LT wheel alive. |
| `LayerSwap.cs` | **The load-bearing RE piece.** Borrows/restores the vanilla top layer and works the show-window fields. |
| `LayerOps.cs` | Read/mutate a layer's `MappedAction[]` (used for base-menu free-slot placement). |
| `HandlerOps.cs` | Insert/replace entries in the menu's `handlers` dict. |
| `RoutingHandler.cs` | The single Il2Cpp-injected `CrossMenuActionHandler`; routes vtable calls to `Registry`. |
| `Patches.cs` | Harmony: force custom types past `GoalManager.IsCrossMenuActionAllowed`; give them audio via `PawnAudioHandler.GetCrossMenuActionId`. |
| `MenuFinder.cs` | Picks the live, populated `CrossMenuUI` (two instances exist; the active one has a non-empty `handlers` dict). |
| `QuickInventoryLocator.cs` | Finds the `QuickInventoryUI` owning a menu (it owns on-screen show/hide). |
| `IconFactory.cs` / `So.cs` / `Glyph.cs` / `LucideIcons.cs` | Build the runtime `CrossMenuAction` SO + its icon sprite (Lucide atlas or supplied). |

## 3. The game mechanism we ride on (verified live)

Field offsets and behavior were confirmed against the running game; full detail in the white paper.

**Layers (`CrossMenuUI`).** Two serialized layers: `topLayer` (+0x40, the DEFAULT wheel:
piton/flask/puff/bag) and `bottomLayer` (+0x48, the LT wheel: rest/light/fingers/barometer).
`GetRelevantLayer` picks one from `forcedLayerValue` (+0xcc):

- `2` → top, `1` → bottom, `0` → **auto**: by `isOnBottomLayer` (+0xb8).
- The forced branches (1/2) also **write** `isOnBottomLayer`.
- **`SetForcedLayer(0)` zeroes `isOnBottomLayer` as a side effect** — the ordering trap (see §4).

**LT (layer toggle).** `inventorySwitchLayer` is a Button bound to `<Gamepad>/leftTrigger`. On its
**press edge** `InventorySwitchLayer_performed → SwitchLayer()` flips `isOnBottomLayer` (top→bottom).
It only fires once per press; it does not re-toggle while held.

**Show / fade (`QuickInventoryUI`).** On-screen visibility is owned here, separately from layer
selection:

- `forceDisplayCrossMenu` (+0x74) — when true, pins the wheel shown. **But its `Update` reap path runs
  `SetForcedLayer(0)` ~2×/frame** when no fresh show/switch edge is pending — see §4.
- `crossMenuShowTimeRemaining` (+0x70) — the show window. `QuickInventoryUI.Refresh` fades the wheel
  **out** once this hits 0 and nothing else requests show. It is re-armed (`= showCrossMenuForSeconds`,
  +0x60) only by `ShowCrossMenuUI`, on the **LT press edge**.

## 4. The chord lifecycle (`ChordMenus.Tick` → `LayerSwap`)

```
read pad (ChordInput) → { leftTriggerHeld, selectedMenu }

if selectedMenu changed:
    if was active: LayerSwap.Restore(leftTriggerHeld)   // put vanilla top layer back, hand to auto
    if now active: LayerSwap.Enter(menu)                // snapshot top layer, write ours, force top

if a mod menu is active:  LayerSwap.KeepShown()         // re-force + re-render each frame
elif LT is held:          LayerSwap.KeepVanillaWheelAlive()  // re-arm the show window
```

**`Enter`**: snapshot `topLayer.actions`, write the menu's `MappedAction[]`, `SetForcedLayer(2)`,
`Refresh()`. The menu never polls, so a forced `Refresh` is the only thing that re-reads the layer and
re-commits sprites/counts/fill — hence `KeepShown` repeats it each frame.

**`Restore`**: write the saved actions back, then hand selection to auto. Because `SetForcedLayer(0)`
zeroes `isOnBottomLayer`, **re-assert it AFTER**:

```csharp
SetForcedLayer(0);                 // auto — ALSO zeroes isOnBottomLayer
isOnBottomLayer = leftTriggerHeld; // mirror physical LT: held → LT wheel; released → default
Refresh();
```

This is what makes "release RT while holding LT" fall back to the **LT wheel** rather than the default.

**`KeepVanillaWheelAlive`**: the subtle one. After a chord exit (or any long hold) the show window
(+0x70) has decayed, because LT only armed it on its press edge — long consumed. Left alone,
`QuickInventoryUI.Refresh` takes its fade-out branch and the LT wheel fades **while LT is still held**.
We re-arm the window each frame (`crossMenuShowTimeRemaining = showCrossMenuForSeconds`) — exactly what
`ShowCrossMenuUI` does — driving the game's **own** show mechanism. When LT releases we stop, the
window decays, and vanilla fades it normally.

### Why NOT `forceDisplayCrossMenu`

An earlier "keep it shown" attempt pinned `forceDisplayCrossMenu(true)` every frame while LT was held.
That tripped `QuickInventoryUI.Update`'s reap path (`SetForcedLayer(0)` ~2×/frame), which zeroed
`isOnBottomLayer` continuously — producing a one-frame LT-wheel flash that snapped back to default, and
a fade as the reap disabled the wheel, plus a stuck-open wheel because nothing reliably cleared the
flag on release. The fix was to stop fighting `Update` and instead feed its show-window input. **Touch
the show window, not the force flag, for keep-alive.**

## 5. Lifecycle, threading, robustness

- Everything runs on the Unity main thread, pumped from `Core.OnUpdate → MenuController.Tick`.
- `MenuController` self-heals: if its bound menu is destroyed (HUD rebuild) it re-finds and re-creates
  `ChordMenus`; `Teardown` restores any borrowed layer first so the vanilla menu is never left modded.
- Il2Cpp proxy pokes can throw across the managed↔native seam; `LayerSwap` wraps them so a single bad
  frame can't abort the tick.
- `RoutingHandler` callbacks run inside the native call stack; they guard-and-swallow managed
  exceptions (an escape would crash the trampoline).

## 6. Harmony touchpoints (`Patches.cs`)

- `GoalManager.IsCrossMenuActionAllowed` → force `true` for custom types (Tenzing zone whitelists
  otherwise reject unknown enums and block every custom action). A mod that wants zone-restriction
  returns `false` from its own `IsAvailable` instead.
- `PawnAudioHandler.GetCrossMenuActionId` → map custom types to the SAVEMARKER Wwise switch (resolved
  from the generated switches type, not hardcoded) so custom actions get begin/hold/complete feedback.

## 7. Testing this mod

The behavioral surface (layer fallback, show-window keep-alive) is verified by driving synthetic input
through CairnDevTools (`input lt 1`, `input lt 1 rt 1`, …) and reading
`CrossMenuUI.isOnBottomLayer` / `GetRelevantLayer()` / `QuickInventoryUI.crossMenuShowTimeRemaining`
live. Note: synthetic input does **not** faithfully reproduce the on-screen *fade* (the visible alpha
runs through `CanvasGroupToRenderTexture`, a separate path), so the final fade behavior must be
confirmed on a real controller — the show-window field is the correct proxy to instrument, but the
last word is the player's eyes.
