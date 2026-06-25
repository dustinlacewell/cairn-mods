// Single source of truth for every Cairn mod the site documents.
// The landing listing, the directory, and each /mods/<slug>/ page read from here.
//
// CrossMenuLib is intentionally absent: its capabilities are folding into CairnAPI.

export type ModKind = "library" | "player" | "dev";

export interface ApiParam {
  name: string;
  type: string;
  description: string;
  optional?: boolean;
}

export interface ApiEntry {
  signature: string;
  returns?: string;
  description: string;
  params?: ApiParam[];
}

export interface DocSection {
  id: string;
  title: string;
  group?: string;
  intro?: string;
  entries?: ApiEntry[];
  code?: { lang: string; src: string };
  body?: string[];
}

export interface Mod {
  slug: string;
  name: string;
  tagline: string;
  kind: ModKind;
  author: string;
  version: string;
  description: string[];
  features: string[];
  bindings?: { key: string; does: string }[];
  requires?: string[];
  download?: string;
  sections?: DocSection[];
}

const TODO_DOWNLOAD = "#download-coming-soon";

export const mods: Mod[] = [
  // ── Player-facing mods ──────────────────────────────────────────────
  {
    slug: "cairncoop",
    name: "CairnCoop",
    tagline: "Steam co-op — rope up and catch each other's falls.",
    kind: "player",
    author: "ldlework",
    version: "0.2.0",
    description: [
      "Re-enables Cairn's shipped-but-disabled multiplayer. One player hosts; partners rope up and belay each other, so the rope catches real falls.",
    ],
    features: [
      "Steam co-op on Cairn's own (disabled) netplay",
      "Rope physics between climbers — falls caught by the line",
      "Party HUD: who's hanging, how the carry-graph is wired",
      "Host kick / temp-ban",
    ],
    bindings: [
      { key: "F4", does: "co-op panel" },
      { key: "LT+RT", does: "connect / disconnect rope" },
    ],
    requires: ["cairnapi"],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairnroutes",
    name: "CairnRoutes",
    tagline: "Record, name, draw, and re-climb the line you took.",
    kind: "player",
    author: "dustin",
    version: "0.1.0",
    description: [
      "Records the exact path you climbed, saves it under a name, draws it back into the world as a 3D overlay, and teleports you to its start. Persists across sessions.",
    ],
    features: [
      "Records the line you actually climb",
      "Named, saved routes — persist across sessions",
      "3D line overlays in-world",
      "Teleport to a route's start",
    ],
    bindings: [{ key: "F8", does: "routes window" }],
    requires: ["cairnapi"],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairnfreeroam",
    name: "CairnFreeRoam",
    tagline: "Eagle-eye fast-travel in any save, plus map bookmarks.",
    kind: "player",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "Unlocks the game's eagle-eye fast-travel in every save type, not just Free Roam. Adds custom bookmarks for warping to arbitrary map positions.",
    ],
    features: [
      "Eagle-eye fast-travel in every save",
      "Game's own warp list, pins, camera POV",
      "Custom bookmarks for arbitrary positions",
    ],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairnforage",
    name: "CairnForage",
    tagline: "Eat and drink world items in place, no bag.",
    kind: "player",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "Adds a Consume prompt to food, water, and stamina items in the world. Press the key and the effect applies on the spot — no pickup, no inventory step.",
    ],
    features: [
      "Consume world items in place",
      "Prompt on anything consumable",
      "Effect applied, item despawns",
    ],
    bindings: [{ key: "G", does: "consume prompted item" }],
    requires: ["cairnapi"],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairncarryweight",
    name: "CairnCarryWeight",
    tagline: "A heavier pack tires you faster.",
    kind: "player",
    author: "dustin",
    version: "1.0.0",
    description: [
      "Makes bag weight matter: per-limb climbing effort scales with how full your pack is (1× empty to 2× full, tunable). Items can carry weight overrides.",
    ],
    features: [
      "Bag fullness scales climbing effort",
      "Tunable scale (default 1×–2×)",
      "Per-item weight overrides",
    ],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairnaccessories",
    name: "CairnAccessories",
    tagline: "Wear and pose your own .glb accessories in photo mode.",
    kind: "player",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "Drop a .glb into the Accessories folder, open photo mode, and a gizmo pins it to a bone and positions it. Placement is saved back into the file.",
    ],
    features: [
      "Wear custom .glb accessories",
      "Bone-pick + position with a photo-mode gizmo",
      "Placement persisted into the .glb",
    ],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairnnocutscenes",
    name: "CairnNoCutscenes",
    tagline: "Auto-skip cutscenes, intros, and radio calls.",
    kind: "player",
    author: "dustin",
    version: "1.0.0",
    description: [
      "Auto-skips cutscenes, videos, opening titles, and radio calls via the game's own skip pipeline. Per-feature toggles; zero fade; black-screen watchdog.",
    ],
    features: [
      "Skip cutscenes, videos, titles, radio calls",
      "Per-feature toggles",
      "Instant (zero fade) + black-screen watchdog",
    ],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairnarchipelago",
    name: "CairnArchipelago",
    tagline: "Cairn client for the Archipelago multiworld randomizer.",
    kind: "player",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "Plays Cairn inside an Archipelago multiworld: story beats become checkable locations, and items received from the session are granted into your inventory.",
    ],
    features: [
      "Full Archipelago multiworld client",
      "Story beats as locations",
      "Received items granted to inventory",
    ],
    download: TODO_DOWNLOAD,
  },
  {
    slug: "cairntools",
    name: "CairnTools",
    tagline: "Spawn-anything item window.",
    kind: "player",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "An in-game window to spawn any item, with an option to unlock the backpack while climbing.",
    ],
    features: ["Spawn any item", "Optionally unlock the backpack mid-climb"],
    bindings: [{ key: "F9", does: "item window" }],
    requires: ["cairnapi"],
    download: TODO_DOWNLOAD,
  },

  // ── Libraries & developer tooling ───────────────────────────────────
  {
    slug: "cairnapi",
    name: "CairnAPI",
    tagline: "Shared C# layer the other mods build on.",
    kind: "library",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "Stable wrappers over Cairn's IL2CPP internals — items, inventory, world interaction, prompts, teleport — so mods don't re-derive the plumbing.",
    ],
    features: [
      "Item lookup + inventory manipulation",
      "Screen-space and world-space prompts with glyphs",
      "Proximity and reach-gated interactables",
      "Streaming-aware teleport (local zone + cross-world)",
      "Story beat enumeration",
      "World/zone catalog",
      "Screen state + transition events",
    ],
    download: TODO_DOWNLOAD,
    sections: [
      {
        id: "items",
        title: "Items",
        intro: "Read-only catalog of every item in the game.",
        entries: [
          {
            signature: "Items.All",
            returns: "IReadOnlyList<ItemInfo>",
            description: "Every item in the game (315 entries). Cached after first access.",
          },
          {
            signature: "Items.Get(id)",
            returns: "ItemInfo",
            description: "Resolve a single item config by enum id.",
            params: [
              { name: "id", type: "InventoryItemStringIdEnum", description: "The item's enum identifier." },
            ],
          },
          {
            signature: "ItemInfo",
            description: "Per-item metadata.",
            params: [
              { name: ".Id", type: "InventoryItemStringIdEnum", description: "Enum identifier." },
              { name: ".Name", type: "string", description: "Display name." },
              { name: ".StoredIn", type: "StorageType", description: "Which storage slot holds this item." },
              { name: ".MaxStack", type: "int", description: "Maximum stack count." },
              { name: ".UnitWeight", type: "float", description: "Weight per unit. NaN for non-physical items." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `foreach (var info in Items.All)
    MelonLogger.Msg($"{info.Name}  stored={info.StoredIn}  w={info.UnitWeight:F2}");

var rope = Items.Get(InventoryItemStringIdEnum.Rope_Standard);`,
        },
      },
      {
        id: "inventory",
        title: "Inventory",
        intro: "Add items to and query the player's inventory.",
        entries: [
          {
            signature: "Inventory.Add(id, count)",
            returns: "AddResult",
            description: "Add items to the appropriate storage slot. Stops cleanly at capacity.",
            params: [
              { name: "id", type: "InventoryItemStringIdEnum", description: "Item to add." },
              { name: "count", type: "int", description: "How many to add." },
            ],
          },
          {
            signature: "AddResult",
            description: "Return value from Inventory.Add.",
            params: [
              { name: ".Ok", type: "bool", description: "True if all requested items were added." },
              { name: ".Added", type: "int", description: "How many were actually added." },
              { name: ".Error", type: "string", description: "Failure reason when Ok is false." },
            ],
          },
          {
            signature: "Inventory.Count(id)",
            returns: "int",
            description: "How many of an item the player currently holds.",
            params: [
              { name: "id", type: "InventoryItemStringIdEnum", description: "Item to count." },
            ],
          },
          {
            signature: "Inventory.StorageWeight(storage)",
            returns: "float",
            description: "Current total weight in a storage slot.",
            params: [
              { name: "storage", type: "StorageType", description: "Which slot to query." },
            ],
          },
          {
            signature: "Inventory.MaxBagWeight()",
            returns: "float",
            description: "The bag's weight capacity.",
          },
        ],
        code: {
          lang: "csharp",
          src: `var result = Inventory.Add(InventoryItemStringIdEnum.Food_Nuts, 3);
if (!result.Ok) MelonLogger.Warning(result.Error);
else MelonLogger.Msg($"Added {result.Added}");

int count = Inventory.Count(InventoryItemStringIdEnum.Food_Nuts);
float load = Inventory.StorageWeight(StorageType.Bag);`,
        },
      },
      {
        id: "teleport",
        title: "Teleport",
        intro: "Warp the climber to any position in the current zone or across zones.",
        entries: [
          {
            signature: "Teleport.Busy",
            returns: "bool",
            description: "True while a warp is in flight.",
          },
          {
            signature: "Teleport.To(position, done)",
            description: "Warp the climber to a world position in the current zone.",
            params: [
              { name: "position", type: "Vector3", description: "Target world position." },
              { name: "done", type: "Action<bool>", description: "Callback — receives true on success." },
            ],
          },
          {
            signature: "Teleport.ToZone(zone, position, done)",
            description: "Cross-zone warp. Streams the target zone if needed, then warps.",
            params: [
              { name: "zone", type: "ZoneSceneData", description: "Target zone. Use World to resolve." },
              { name: "position", type: "Vector3", description: "Target world position inside that zone." },
              { name: "done", type: "Action<bool>", description: "Callback — receives true on success." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `Teleport.To(targetPos, ok => {
    if (!ok) MelonLogger.Warning("warp failed or busy");
});

var zone = World.ResolveZone(World.Current, "03_TheNeedle");
Teleport.ToZone(zone, targetPos, ok => { });`,
        },
      },
      {
        id: "world",
        title: "World",
        intro: "Enumerate authored worlds and zones, resolve zone assets by name.",
        entries: [
          {
            signature: "World.Current",
            returns: "WorldZoneData",
            description: "The active world currently being streamed.",
          },
          {
            signature: "World.Worlds()",
            returns: "IEnumerable<WorldZoneData>",
            description: "Every authored world in the game.",
          },
          {
            signature: "World.Zones(world)",
            returns: "IEnumerable<ZoneSceneData>",
            description: "All zones belonging to a world.",
            params: [
              { name: "world", type: "WorldZoneData", description: "World to enumerate." },
            ],
          },
          {
            signature: "World.ResolveZone(world, name)",
            returns: "ZoneSceneData",
            description: "Find a zone by its asset name.",
            params: [
              { name: "world", type: "WorldZoneData", description: "World to search within." },
              { name: "name", type: "string", description: 'Zone asset name, e.g. "01_FirstRidge".' },
            ],
          },
          {
            signature: "World.WorldOf(zone)",
            returns: "WorldZoneData",
            description: "Reverse-map a zone back to its owning world.",
            params: [
              { name: "zone", type: "ZoneSceneData", description: "Zone to look up." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `foreach (var world in World.Worlds())
    foreach (var zone in World.Zones(world))
        MelonLogger.Msg($"{world.name}/{zone.name}");

var zone = World.ResolveZone(World.Current, "01_FirstRidge");
var owningWorld = World.WorldOf(zone);`,
        },
      },
      {
        id: "beats",
        title: "Beats",
        intro: "Enumerate story-beat sensors in the current scene.",
        entries: [
          {
            signature: "Beats.Available",
            returns: "bool",
            description: "True when the story manager is live. Check before calling Snapshot.",
          },
          {
            signature: "Beats.Snapshot()",
            returns: "List<Beat>",
            description: "Every story-beat sensor in the current scene, sorted by label. Pure — no caching.",
          },
          {
            signature: "Beat",
            description: "A story-beat sensor location.",
            params: [
              { name: ".Label", type: "string", description: "Authored beat name." },
              { name: ".Position", type: "Vector3", description: "World-space center of the sensor trigger." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `if (Beats.Available)
{
    var beats = Beats.Snapshot();
    foreach (var b in beats)
        MelonLogger.Msg($"{b.Label}  @{b.Position}");

    if (beats.Count > 0)
        Teleport.To(beats[0].Position, _ => { });
}`,
        },
      },
      {
        id: "screen",
        title: "Screen",
        intro: "Query game state and subscribe to screen-lifecycle events.",
        entries: [
          {
            signature: "Screen.PawnSpawned",
            returns: "bool",
            description: 'True when the climber pawn is live. The reliable "in gameplay" check.',
          },
          {
            signature: "Screen.IsMenu",
            returns: "bool",
            description: "True while the main menu is active.",
          },
          {
            signature: "Screen.IsInGame",
            returns: "bool",
            description: "True while in a gameplay state, including when a pause menu overlaps.",
          },
          {
            signature: "Screen.IsCutscene",
            returns: "bool",
            description: "True during a cutscene.",
          },
          {
            signature: "Screen.IsGameOver",
            returns: "bool",
            description: "True on the game-over screen.",
          },
          {
            signature: "Screen.IsBivouac",
            returns: "bool",
            description: "True while the bivouac (rest) screen is active.",
          },
          {
            signature: "Screen.CurrentMenu",
            returns: "Menu?",
            description: "Foreground menu canvas, or null when no menu is open.",
          },
          {
            signature: "Screen.MainMenuStep",
            returns: "int",
            description: "Current sub-step within the main menu flow.",
          },
          {
            signature: "Screen.OnGameStateChanged",
            returns: "event",
            description: "Fires on every GameState push or pop.",
            params: [
              { name: "from", type: "GameState", description: "Previous state." },
              { name: "to", type: "GameState", description: "New state." },
            ],
          },
          {
            signature: "Screen.OnMenuChanged",
            returns: "event",
            description: "Fires when the foreground menu changes. Receives null when the stack empties.",
            params: [
              { name: "menu", type: "Menu?", description: "New foreground menu, or null." },
            ],
          },
          {
            signature: "Screen.OnCanvasOpened",
            returns: "event",
            description: "Fires when any menu or HUD canvas opens.",
            params: [
              { name: "canvas", type: "Canvas", description: "The opened canvas." },
            ],
          },
          {
            signature: "Screen.OnCanvasClosed",
            returns: "event",
            description: "Fires when any menu or HUD canvas closes.",
            params: [
              { name: "canvas", type: "Canvas", description: "The closed canvas." },
            ],
          },
          {
            signature: "Screen.OnTransitionStarted",
            returns: "event",
            description: "Fires at the start of any scene transition.",
          },
          {
            signature: "Screen.OnTransitionCompleted",
            returns: "event",
            description: "Fires when a scene transition finishes.",
          },
          {
            signature: "Screen.OnEnteringMenu",
            returns: "event",
            description: "Fires on the game→menu path, before the menu scene loads.",
          },
        ],
        code: {
          lang: "csharp",
          src: `if (Screen.IsMenu)      MelonLogger.Msg($"step={Screen.MainMenuStep}");
if (Screen.PawnSpawned) MelonLogger.Msg("live in gameplay");

Screen.OnGameStateChanged += (from, to) => MelonLogger.Msg($"{from} -> {to}");
Screen.OnMenuChanged      += menu => MelonLogger.Msg(menu?.name ?? "no menu");
Screen.OnTransitionStarted   += () => MelonLogger.Msg("transition started");
Screen.OnTransitionCompleted += () => MelonLogger.Msg("transition done");
Screen.OnCanvasOpened += c => MelonLogger.Msg($"opened {c.GetType().Name}");`,
        },
      },
      {
        id: "prompts",
        title: "Prompts",
        intro: "Four prompt types for different interaction contexts. All take a string label and an optional Glyph; all return a handle.",
      },
      {
        id: "screenprompt",
        title: "ScreenPrompt",
        group: "prompts",
        intro: "Fixed screen-space prompt row rendered above the HUD.",
        entries: [
          {
            signature: "ScreenPrompt.Show(text, glyph, parent?)",
            returns: "PromptHandle",
            description: "Render a [glyph] label row on the screen overlay. Pass parent to place it inside your own layout.",
            params: [
              { name: "text", type: "string", description: "Label text." },
              { name: "glyph", type: "InputAction", description: "Button icon from Glyph, or null." },
              { name: "parent", type: "Transform", optional: true, description: "Parent into a custom layout group." },
            ],
          },
          {
            signature: "ScreenPrompt.Move(handle, pos)",
            description: "Reposition on the screen overlay. Coordinates are screen-center-relative.",
            params: [
              { name: "handle", type: "PromptHandle", description: "Handle from Show." },
              { name: "pos", type: "Vector2", description: "Anchored position." },
            ],
          },
          {
            signature: "ScreenPrompt.SetActive(handle, active)",
            description: "Show or hide without destroying. Layout re-flows automatically.",
            params: [
              { name: "handle", type: "PromptHandle", description: "Handle from Show." },
              { name: "active", type: "bool", description: "Visible state." },
            ],
          },
          {
            signature: "ScreenPrompt.Hide(handle)",
            description: "Destroy the prompt.",
            params: [
              { name: "handle", type: "PromptHandle", description: "Handle from Show." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `var h = ScreenPrompt.Show("Open the hatch", Glyph.Action(GameAction.Interact));
ScreenPrompt.Move(h, new Vector2(0, -120));

var h2 = ScreenPrompt.Show("Confirm", Glyph.Key("e"), parent: myRow.transform);

ScreenPrompt.SetActive(h, false);
ScreenPrompt.Hide(h);`,
        },
      },
      {
        id: "worldprompt",
        title: "WorldPrompt",
        group: "prompts",
        intro: "Floating world-space prompt that billboards to the camera.",
        entries: [
          {
            signature: "WorldPrompt.Show(anchor, text, glyph, style?)",
            returns: "PromptHandle",
            description: "Float a [glyph] label over a world transform.",
            params: [
              { name: "anchor", type: "Transform", description: "World-space anchor." },
              { name: "text", type: "string", description: "Label text." },
              { name: "glyph", type: "InputAction", description: "Button icon, or null." },
              { name: "style", type: "WorldPromptStyle", optional: true, description: "Tune float radius, wall offset, max height, and anchor tracking." },
            ],
          },
          {
            signature: "WorldPrompt.Hide(handle)",
            description: "Destroy the prompt.",
            params: [
              { name: "handle", type: "PromptHandle", description: "Handle from Show." },
            ],
          },
          {
            signature: "WorldPromptStyle",
            description: "Display options for WorldPrompt.",
            params: [
              { name: ".Radius", type: "float", description: "Float distance from the anchor." },
              { name: ".WallOffset", type: "float", description: "Offset from the wall surface." },
              { name: ".MaxHeight", type: "float", description: "Maximum vertical position." },
              { name: ".FollowsAnchor", type: "bool", description: "Track a moving anchor each frame." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `var h = WorldPrompt.Show(thing.transform, "Activate", Glyph.Key("e"));

var style = new WorldPromptStyle { Radius = 0.5f, FollowsAnchor = false };
var h2 = WorldPrompt.Show(fixedAnchor, "Examine", null, style: style);

WorldPrompt.Hide(h);`,
        },
      },
      {
        id: "proximityprompt",
        title: "ProximityPrompt",
        group: "prompts",
        intro: "World prompt gated on the climber being within range on the ground (Walking mode only).",
        entries: [
          {
            signature: "ProximityPrompt.Show(anchor, distance, text, glyph, onInteract)",
            returns: "ProximityInteractable",
            description: "Show a prompt and fire a callback when the button is pressed in range.",
            params: [
              { name: "anchor", type: "Transform", description: "World-space anchor." },
              { name: "distance", type: "float", description: "Detection radius in metres." },
              { name: "text", type: "string", description: "Label text." },
              { name: "glyph", type: "InputAction", description: "Button icon, or null." },
              { name: "onInteract", type: "Action", description: "Called when the button is pressed in range." },
            ],
          },
          {
            signature: "ProximityInteractable.Destroy()",
            description: "Remove the prompt and interactable.",
          },
        ],
        code: {
          lang: "csharp",
          src: `var p = ProximityPrompt.Show(
    chest.transform, 3f,
    "Loot", Glyph.Action(GameAction.Interact),
    onInteract: () => OpenChest());

p.Destroy();`,
        },
      },
      {
        id: "reachprompt",
        title: "ReachPrompt",
        group: "prompts",
        intro: "World prompt gated on a hand physically reaching into the trigger sphere (Climbing mode only).",
        entries: [
          {
            signature: "ReachPrompt.Show(anchor, text, glyph, onInteract, radius?, localOffset?)",
            returns: "ReachInteractable",
            description: "Show a prompt and fire a callback when a hand enters the sphere and the button is pressed.",
            params: [
              { name: "anchor", type: "Transform", description: "Moving anchor (e.g. a partner climber)." },
              { name: "text", type: "string", description: "Label text." },
              { name: "glyph", type: "InputAction", description: "Button icon, or null." },
              { name: "onInteract", type: "Action", description: "Called when triggered." },
              { name: "radius", type: "float", optional: true, description: "Detection sphere radius." },
              { name: "localOffset", type: "Vector3", optional: true, description: "Offset from anchor in local space." },
            ],
          },
          {
            signature: "ReachPrompt.Create(position, text, glyph, onInteract, radius?)",
            returns: "ReachInteractable",
            description: "Fixed world-point variant.",
            params: [
              { name: "position", type: "Vector3", description: "Fixed world position." },
              { name: "text", type: "string", description: "Label text." },
              { name: "glyph", type: "InputAction", description: "Button icon, or null." },
              { name: "onInteract", type: "Action", description: "Called when triggered." },
              { name: "radius", type: "float", optional: true, description: "Detection sphere radius." },
            ],
          },
          {
            signature: "ReachInteractable.Destroy()",
            description: "Remove the prompt and interactable.",
          },
        ],
        code: {
          lang: "csharp",
          src: `var r = ReachPrompt.Show(partner.transform, "Rope up",
    Glyph.Action(GameAction.Interact),
    onInteract: () => AttachRope(),
    radius: 0.5f, localOffset: Vector3.up * 0.2f);

var r2 = ReachPrompt.Create(leverPos, "Pull", Glyph.Key("e"),
    onInteract: () => PullLever());

r.Destroy();`,
        },
      },
      {
        id: "glyph",
        title: "Glyph",
        intro: "Produces InputAction objects for prompt icons. Icons resolve per-device (keyboard vs. gamepad) automatically.",
        entries: [
          {
            signature: "Glyph.Action(action)",
            returns: "InputAction",
            description: "The player's live binding for a named game action. Respects their rebinds.",
            params: [
              { name: "action", type: "GameAction", description: "The game action to look up." },
            ],
          },
          {
            signature: "Glyph.Key(key)",
            returns: "InputAction",
            description: "A specific keyboard key.",
            params: [
              { name: "key", type: "string", description: 'Key name, e.g. "f" or "space".' },
            ],
          },
          {
            signature: "Glyph.Path(path)",
            returns: "InputAction",
            description: "A raw Unity Input System control path.",
            params: [
              { name: "path", type: "string", description: 'Control path, e.g. "&lt;Gamepad&gt;/buttonSouth".' },
            ],
          },
          {
            signature: "Glyph.Custom(name, ...paths)",
            returns: "InputAction",
            description: "A multi-binding action usable as both a glyph icon and a polled input trigger.",
            params: [
              { name: "name", type: "string", description: "Action name (arbitrary)." },
              { name: "paths", type: "string[]", description: "One or more control paths." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `var g  = Glyph.Action(GameAction.Interact);
var g2 = Glyph.Key("f");
var g3 = Glyph.Path("<Gamepad>/buttonSouth");

var g4 = Glyph.Custom("MyAction", "<Keyboard>/g", "<Gamepad>/buttonWest");
if (g4.WasPerformedThisFrame()) DoThing();`,
        },
      },
    ],
  },
  {
    slug: "cairndevtools",
    name: "CairnDevTools",
    tagline: "Live C# console wired into the running game.",
    kind: "dev",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "A localhost HTTP console that compiles and evals C# against the live game (Roslyn), plus scene inspection and per-instance logging. For writing mods, not playing them.",
    ],
    features: [
      "HTTP console (ports 14200–14209)",
      "Live C# eval against the running game",
      "Scene inspection + per-instance logs",
    ],
    download: TODO_DOWNLOAD,
  },
];

export const mod = (slug: string) => mods.find((m) => m.slug === slug);

export const kindLabel: Record<ModKind, string> = {
  player: "mod",
  library: "lib",
  dev: "dev",
};
