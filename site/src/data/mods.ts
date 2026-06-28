// Single source of truth for every Cairn mod the site documents.
// The landing listing, the directory, and each /mods/<slug>/ page read from here.
//
// CrossMenuLib is intentionally absent: its capabilities are folding into CairnAPI.
//
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import publishConfig from "../../../publish.json";
export const publishedSlugs = new Set<string>(publishConfig.mods.map((s: string) => s.toLowerCase()));

// A mod's required mods are derived from its csproj <ProjectReference> entries — the in-repo sibling
// mods it builds against are exactly the ones a player must also install. This keeps the site's
// dependency list in lockstep with the real build graph, with zero hand-maintenance.
const MODS_DIR = resolve(dirname(fileURLToPath(import.meta.url)), "../../../mods");
function requiresFromCsproj(modName: string): string[] {
  const csprojPath = resolve(MODS_DIR, modName, `${modName}.csproj`);
  if (!existsSync(csprojPath)) return [];
  const csproj = readFileSync(csprojPath, "utf8");
  return [...csproj.matchAll(/<ProjectReference\b[^>]*Include="[^"]*?([^"\\/]+)\.csproj"/g)]
    .map((m) => m[1].toLowerCase());
}

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
      "Configurable on the in-game Mods page",
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
    Log($"{info.Name}  {info.StoredIn}  {info.UnitWeight:F2}kg");

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
if (!result.Ok) LogWarning(result.Error);
else Log($"Added {result.Added}");

int nuts  = Inventory.Count(InventoryItemStringIdEnum.Food_Nuts);
float bag = Inventory.StorageWeight(StorageType.Bag);`,
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
          src: `foreach (var w in World.Worlds())
    foreach (var z in World.Zones(w))
        Log($"{w.name}/{z.name}");

var zone   = World.ResolveZone(World.Current, "01_FirstRidge");
var parent = World.WorldOf(zone);`,
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
          src: `if (!Beats.Available) return;

var beats = Beats.Snapshot();
foreach (var b in beats)
    Log($"{b.Label}  @{b.Position}");

Teleport.To(beats[0].Position, _ => { });`,
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
          src: `if (Screen.IsMenu)      Log($"step={Screen.MainMenuStep}");
if (Screen.PawnSpawned) Log("live in gameplay");

Screen.OnGameStateChanged +=
    (from, to) => Log($"{from} -> {to}");
Screen.OnMenuChanged +=
    menu => Log(menu?.name ?? "no menu");
Screen.OnTransitionStarted   += () => Log("transition started");
Screen.OnTransitionCompleted += () => Log("transition done");
Screen.OnCanvasOpened +=
    c => Log($"opened {c.GetType().Name}");`,
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
          src: `var h = ScreenPrompt.Show(
    "Open the hatch",
    Glyph.Action(GameAction.Interact));
ScreenPrompt.Move(h, new Vector2(0, -120));

// inside your own layout:
var h2 = ScreenPrompt.Show(
    "Confirm", Glyph.Key("e"),
    parent: myRow.transform);

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
          src: `var h = WorldPrompt.Show(
    thing.transform, "Activate", Glyph.Key("e"));

var style = new WorldPromptStyle {
    Radius = 0.5f, FollowsAnchor = false
};
var h2 = WorldPrompt.Show(
    fixedAnchor, "Examine", null, style: style);

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
          src: `var r = ReachPrompt.Show(
    partner.transform, "Rope up",
    Glyph.Action(GameAction.Interact),
    onInteract: () => AttachRope(),
    radius: 0.5f,
    localOffset: Vector3.up * 0.2f);

var r2 = ReachPrompt.Create(
    leverPos, "Pull", Glyph.Key("e"),
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

var g4 = Glyph.Custom(
    "MyAction",
    "<Keyboard>/g",
    "<Gamepad>/buttonWest");
if (g4.WasPerformedThisFrame()) DoThing();`,
        },
      },
    ],
  },
  {
    slug: "cairnmodoptions",
    name: "CairnModOptions",
    tagline: "One in-game settings page for every mod's options.",
    kind: "library",
    author: "ldlework",
    version: "0.1.0",
    description: [
      "Gives any mod a place to expose its settings inside Cairn's own Settings menu. Register a list of typed options and they appear on a <strong>Mods</strong> page — one mod at a time, picked from a dropdown at the top of the page.",
      "Authors write zero UI: declare toggles, sliders, text fields, dropdowns, and buttons; players get native-looking controls that read and write your values live.",
    ],
    features: [
      "Native \"Mods\" page in the game's Settings menu",
      "One mod at a time, via a selector dropdown",
      "Typed options: toggle, slider, text, list, dropdown, buttons, labels",
      "Back each option with a MelonPreferences entry or your own get/set",
      "Changes apply and persist the moment the player makes them",
    ],
    download: TODO_DOWNLOAD,
    sections: [
      {
        id: "register",
        title: "Registering options",
        intro:
          "Register a list of options under your mod's name. They become one section of the Mods page, selectable from the dropdown. Re-registering replaces your previous list. Consumer mods never touch the UI — the page reads this registry.",
        entries: [
          {
            signature: "ModOptions.Register(modName, options)",
            description:
              "Register or replace your mod's option list. <code>modName</code> labels your entry in the dropdown.",
            params: [
              { name: "modName", type: "string", description: "Display name shown in the mod selector." },
              { name: "options", type: "IEnumerable<ModOption>", description: "Rows to display, in declaration order." },
            ],
          },
          {
            signature: "ModOptions.Unregister(modName)",
            description: "Remove your mod's options from the page.",
            params: [
              { name: "modName", type: "string", description: "The name you registered under." },
            ],
          },
          {
            signature: "ModOptions.Sets",
            returns: "IReadOnlyList<ModOptionSet>",
            description: "Every registered mod's option block, in registration order.",
          },
        ],
        code: {
          lang: "csharp",
          src: `ModOptions.Register("MyMod", new[]
{
    ModOption.Toggle("God mode", godModeEntry),
    ModOption.Slider("Speed", 0f, 10f, speedEntry),
    ModOption.Dropdown("Difficulty",
        new[]{ "Easy", "Normal", "Hard" }, difficultyEntry),
    ModOption.Action("Reset to defaults", ResetAll),
});`,
        },
      },
      {
        id: "options",
        title: "Option types",
        intro:
          "Build each row with a ModOption factory. Every option takes a label and an optional tooltip. Stateful options (toggle, slider, text, list, dropdown) bind either to a MelonPreferences entry — persisted to disk automatically — or to your own get/set callbacks.",
        entries: [
          {
            signature: "ModOption.Toggle(label, entry, tooltip?)",
            returns: "ModOption",
            description: "Bool on/off. Backed by a <code>MelonPreferences_Entry&lt;bool&gt;</code>, or a <code>Func&lt;bool&gt;</code> get / <code>Action&lt;bool&gt;</code> set pair.",
            params: [
              { name: "label", type: "string", description: "Row label." },
              { name: "entry", type: "MelonPreferences_Entry<bool>", description: "Backing preference (or pass get/set callbacks)." },
              { name: "tooltip", type: "string", optional: true, description: "Hover help." },
            ],
          },
          {
            signature: "ModOption.Slider(label, min, max, entry, tooltip?)",
            returns: "ModOption",
            description: "Float slider in [min, max].",
            params: [
              { name: "label", type: "string", description: "Row label." },
              { name: "min", type: "float", description: "Lower bound." },
              { name: "max", type: "float", description: "Upper bound." },
              { name: "entry", type: "MelonPreferences_Entry<float>", description: "Backing preference (or pass get/set)." },
            ],
          },
          {
            signature: "ModOption.TextField(label, entry, contentType?, tooltip?)",
            returns: "ModOption",
            description: "Editable string. <code>contentType</code> sets the keyboard/validation mode (e.g. <code>IntegerNumber</code> for numeric-only).",
            params: [
              { name: "label", type: "string", description: "Row label." },
              { name: "entry", type: "MelonPreferences_Entry<string>", description: "Backing preference (or pass get/set)." },
              { name: "contentType", type: "TMP_InputField.ContentType", optional: true, description: "Input mode. Default Standard." },
            ],
          },
          {
            signature: "ModOption.ListArrows(label, choices, entry, tooltip?)",
            returns: "ModOption",
            description: "Pick from a string list with left/right arrows. Stores the selected index.",
            params: [
              { name: "label", type: "string", description: "Row label." },
              { name: "choices", type: "string[]", description: "Options to cycle through." },
              { name: "entry", type: "MelonPreferences_Entry<int>", description: "Stores the selected index (or pass get/set)." },
            ],
          },
          {
            signature: "ModOption.Dropdown(label, choices, entry, tooltip?)",
            returns: "ModOption",
            description: "Pick from a string list with a popup dropdown. Stores the selected index.",
            params: [
              { name: "label", type: "string", description: "Row label." },
              { name: "choices", type: "string[]", description: "Options to pick from." },
              { name: "entry", type: "MelonPreferences_Entry<int>", description: "Stores the selected index (or pass get/set)." },
            ],
          },
          {
            signature: "ModOption.ButtonDouble(label, leftLabel, invokeLeft, rightLabel, invokeRight, ...)",
            returns: "ModOption",
            description: "A row with two action buttons side by side. No persistent state. <code>leftActive</code> / <code>rightActive</code> toggle interactivity.",
            params: [
              { name: "leftLabel", type: "string", description: "Left button text." },
              { name: "invokeLeft", type: "Action", description: "Left button callback." },
              { name: "rightLabel", type: "string", description: "Right button text." },
              { name: "invokeRight", type: "Action", description: "Right button callback." },
            ],
          },
          {
            signature: "ModOption.Action(label, invoke, tooltip?)",
            returns: "ModOption",
            description: "A single action button. No persistent state.",
            params: [
              { name: "label", type: "string", description: "Button text." },
              { name: "invoke", type: "Action", description: "Pressed callback." },
            ],
          },
          {
            signature: "ModOption.Info(text, tooltip?)",
            returns: "ModOption",
            description: "Read-only display text. No interaction — use for section headers or status lines.",
            params: [
              { name: "text", type: "string", description: "Display text." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `// backed by a MelonPreferences entry (persists to disk)
ModOption.Toggle("Enable", enabledEntry);

// or your own get/set
ModOption.Toggle("Enable",
    () => _enabled, v => _enabled = v);

// numeric-only text field
ModOption.TextField("Max count", maxEntry,
    TMP_InputField.ContentType.IntegerNumber);

// a non-persistent action button
ModOption.Action("Reload config", ReloadConfig);`,
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
      "A localhost HTTP console that compiles and evals C# against the live game (<code>Roslyn</code>), plus scene inspection and per-instance logging. For writing mods, not playing them.",
      "Built-in verbs: <code>eval</code>, <code>belay</code>, <code>find</code>, <code>inspect</code>, <code>ropes</code>, <code>renderers</code>, <code>gameplay</code>, <code>survival</code>, <code>edelweiss</code>, <code>input</code>, <code>emit</code>.",
    ],
    features: [
      "HTTP console (ports 14200–14209)",
      "Live C# eval — sync and async (Game.WaitFor)",
      "Scene inspection: find, inspect, renderers, ropes, belay graph",
      "Per-instance error log (multi-instance safe)",
      "Synthetic gamepad input",
      "Survival freeze and infinite edelweiss for repro loops",
    ],
    download: TODO_DOWNLOAD,
    sections: [
      {
        id: "http",
        title: "HTTP Console",
        intro:
          "One instance per port, probed from 14200 up. The chosen port is logged at startup. Both GET and POST are supported.",
        entries: [
          {
            signature: "GET /cmd?q=<verb> [args]",
            description:
              "Run a console verb synchronously. Returns plain text. URL-encode spaces in args.",
          },
          {
            signature: "POST /cmd?q=eval",
            description:
              "Body is raw C# — no URL-encoding. Preferred for multi-line scripts. The <code>q</code> query param defaults to <code>eval</code> when omitted.",
          },
          {
            signature: "GET /events?since=<seq>&name=<filter>&timeout=<ms>",
            description:
              "Long-poll for edge events emitted by game patches or the <code>emit</code> verb. Blocks until the next event past <code>since</code> (default: now), optionally filtered by <code>name</code>, or until <code>timeout</code> ms (default <code>60000</code>). Returns one-line JSON <code>{seq,name,payload,ts}</code> or <code>{timeout:true,seq,oldest}</code>.",
          },
        ],
        code: {
          lang: "bash",
          src: `# run a verb
curl "http://127.0.0.1:14200/cmd?q=help"

# eval multi-line C#
curl -s -X POST "http://127.0.0.1:14200/cmd?q=eval" --data-binary @script.cs

# long-poll for events
curl "http://127.0.0.1:14200/events?since=0&name=revive-resolved"`,
        },
      },
      {
        id: "eval",
        title: "Eval",
        intro:
          "Roslyn-compiled C# against the live game. All loaded assemblies are referenced. Import namespaces are pre-added: <code>System</code>, <code>UnityEngine</code>, <code>Il2Cpp</code>, <code>CairnDevTools</code>. End with <code>return &lt;expr&gt;</code> to get a value back.",
        entries: [
          {
            signature: "Log(message)",
            description: "Alias for <code>MelonLogger.Msg</code>, available in every script body.",
            params: [{ name: "message", type: "string", description: "Text to log." }],
          },
          {
            signature: "Bag",
            returns: "Dictionary<string, object>",
            description: "Persists between evals. Store objects in one script, read them in another.",
          },
        ],
        code: {
          lang: "csharp",
          src: `// inline — returns the value
return Il2Cpp.PawnManager.MCSpawned;

// persist across evals
Bag["pawn"] = UnityEngine.Object.FindObjectOfType<Il2Cpp.ClimbingV2PawnController>();

// retrieve later
var pawn = (Il2Cpp.ClimbingV2PawnController)Bag["pawn"];`,
        },
      },
      {
        id: "async-eval",
        title: "Async Eval",
        group: "eval",
        intro:
          "Scripts can <code>await Game.WaitFor</code> — the POST blocks and returns only when the script settles. Level-triggered: if the predicate is already true it completes on the next frame, so no edge is ever missed. Timeout faults with a <code>TimeoutException</code> carrying the message.",
        entries: [
          {
            signature: "Game.WaitFor(predicate, timeoutMs?)",
            returns: "Task",
            description: "Await the first frame on which predicate is true.",
            params: [
              { name: "predicate", type: "Func<bool>", description: "Polled each frame on the main thread." },
              { name: "timeoutMs", type: "int", optional: true, description: "Default 30000." },
            ],
          },
          {
            signature: "Game.WaitFor<T>(sample, done, timeoutMs?)",
            returns: "Task<T>",
            description: "Sample a value each frame; complete when done(value) is true. Returns the value.",
            params: [
              { name: "sample", type: "Func<T>", description: "Reads game state each frame." },
              { name: "done", type: "Func<T, bool>", description: "Predicate over the sample." },
              { name: "timeoutMs", type: "int", optional: true, description: "Default 30000." },
            ],
          },
          {
            signature: "Game.Delay(ms)",
            returns: "Task",
            description: "Frame-based delay. Advances in lockstep with the game (paused when the pump is).",
            params: [{ name: "ms", type: "int", description: "Wall-clock ms to wait." }],
          },
          {
            signature: "Game.NextFrame()",
            returns: "Task",
            description: "Yield to the next frame.",
          },
          {
            signature: "Game.WaitForMenu(timeoutMs?)",
            returns: "Task",
            description: "Await the <code>MainMenu</code> being present.",
          },
          {
            signature: "Game.WaitForGameplay(timeoutMs?)",
            returns: "Task",
            description: "Await the climber pawn being spawned.",
          },
          {
            signature: "Game.WaitForFullyLoaded(timeoutMs?)",
            returns: "Task",
            description: "Await <code>loadingState == GameStarted</code> — stricter than pawn-spawned.",
          },
          {
            signature: "Game.WaitForScene(name, timeoutMs?)",
            returns: "Task",
            description: "Await the active scene having a given name.",
            params: [{ name: "name", type: "string", description: 'e.g. <code>CommonBaseScene</code>.' }],
          },
          {
            signature: "Game.WaitForEagleEye(timeoutMs?)",
            returns: "Task",
            description: "Await the eagle-eye warp view being open.",
          },
          {
            signature: "Game.EnterGameplay(timeoutMs?)",
            returns: "Task<string>",
            description:
              'The canonical "get me into a playable game": wait for menu → continue → pawn-spawn → fully-loaded. Idempotent: returns immediately if already in gameplay.',
          },
          {
            signature: "Game.Continue()",
            returns: "string",
            description: "Continue the most-recent save from the main menu (fires the <code>gameplay</code> verb inline).",
          },
          {
            signature: "Game.Do(verb, args)",
            returns: "string",
            description: "Dispatch any registered console verb by name from inside a script.",
            params: [
              { name: "verb", type: "string", description: "Registered verb name." },
              { name: "args", type: "string[]", description: "Argument strings." },
            ],
          },
        ],
        code: {
          lang: "csharp",
          src: `// drive the game to a state in one blocking POST
return await Game.EnterGameplay();

// wait for a specific object to appear
var ui = await Game.WaitFor(
    () => UnityEngine.Object.FindObjectOfType<Il2Cpp.EagleEyeUI>(true),
    ui => ui != null);

// frame-based timing
await Game.Delay(500);
await Game.NextFrame();

// dispatch a registered verb from inside a script
var result = Game.Do("survival", "on");`,
        },
      },
      {
        id: "inspection",
        title: "Inspection",
        intro: "Read-only scene queries. All run on the main thread and return plain text.",
        entries: [
          {
            signature: "belay",
            description:
              "Complete connection graph for the local climber's securing rope: harness attach, rope identity/length/bounds, every holder, every part with endpoint pins, <code>LineRenderer</code> truth, climbot state, pitons, and the causal fall-distance scalars.",
          },
          {
            signature: "find <name-substring>",
            description: "Search all scene <code>Transform</code>s (including inactive) by name. Returns up to 30 matches with hierarchy paths and positions.",
          },
          {
            signature: "inspect <name-or-hierarchy-path>",
            description:
              "Full component list for one <code>GameObject</code>: position, rotation, scale, per-component extras (rope length, renderer bounds), and child list.",
          },
          {
            signature: "ropes",
            description:
              "Census of every <code>LogicalRope</code> in the scene: hierarchy path, active/init/visible state, length, holder count, bounds.",
          },
          {
            signature: "renderers [minSpanMeters]",
            description:
              "Active renderers with bounds over the span threshold (default 300 m). Returns up to 25 entries.",
            params: [
              { name: "minSpanMeters", type: "float", optional: true, description: "Default 300." },
            ],
          },
        ],
        code: {
          lang: "bash",
          src: `curl "http://127.0.0.1:14200/cmd?q=belay"
curl "http://127.0.0.1:14200/cmd?q=find Quickdraw"
curl "http://127.0.0.1:14200/cmd?q=inspect Aava/bn_Head"
curl "http://127.0.0.1:14200/cmd?q=ropes"
curl "http://127.0.0.1:14200/cmd?q=renderers 10"`,
        },
      },
      {
        id: "verbs",
        title: "Other Verbs",
        intro: "Utility commands registered at startup.",
        entries: [
          {
            signature: "gameplay",
            description:
              "Continue the most-recent save from the main menu (<code>MainMenu</code> → <code>CommonBaseScene</code>, ~12 s). Only works when the <code>MainMenu</code> is in the scene.",
          },
          {
            signature: "survival [on|off]",
            description:
              "Freeze hunger, thirst, and cold so the climber doesn't starve mid-investigation. Blocks the drain at the source (<code>ClimberData.Set</code>), not just the ratio getters. Toggles when called with no argument.",
          },
          {
            signature: "edelweiss [on|off]",
            description:
              "Make the resurrection item non-consumable so it can revive repeatedly. Useful for repro loops that drive the death→revive cycle many times. Toggles when called with no argument.",
          },
          {
            signature: "input <control> <value> [...]",
            description:
              "Hold a synthetic gamepad control value. Drives the real Unity <code>InputSystem</code> so <code>InputAction</code> callbacks fire (hold-to-switch, chords). Controls: <code>lt</code>, <code>rt</code>, <code>lb</code>, <code>rb</code>, <code>lsx</code>, <code>lsy</code>.",
            params: [
              { name: "control", type: "string", description: "lt | rt | lb | rb | lsx | lsy" },
              { name: "value", type: "float", description: "0..1" },
            ],
          },
          {
            signature: "input clear",
            description: "Release all synthetic input and hand control back to the physical device.",
          },
          {
            signature: "input state",
            description: "Report current synthetic input values.",
          },
          {
            signature: "emit <name> [payload]",
            description:
              "Push a named event onto the <code>EventBus</code> from outside the process. Wakes any <code>/events</code> long-poll watching that name.",
          },
        ],
        code: {
          lang: "bash",
          src: `curl "http://127.0.0.1:14200/cmd?q=gameplay"
curl "http://127.0.0.1:14200/cmd?q=survival on"
curl "http://127.0.0.1:14200/cmd?q=edelweiss on"
curl "http://127.0.0.1:14200/cmd?q=input lt 1 rt 1"
curl "http://127.0.0.1:14200/cmd?q=input clear"
curl "http://127.0.0.1:14200/cmd?q=emit revive-resolved ok"`,
        },
      },
    ],
  },
];

// Derive each mod's requires from its csproj ProjectReferences (authoritative; overrides any literal).
for (const m of mods) m.requires = requiresFromCsproj(m.name);

export const kindLabel: Record<ModKind, string> = {
  player: "mod",
  library: "lib",
  dev: "dev",
};

export const publishedMods = mods.filter((m) => publishedSlugs.has(m.slug));
export const mod = (slug: string) => publishedMods.find((m) => m.slug === slug);
