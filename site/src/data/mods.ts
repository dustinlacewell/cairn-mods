// Single source of truth for every Cairn mod the site documents.
// The landing listing, the directory, and each /mods/<slug>/ page read from here.
//
// CrossMenuLib folded into CairnAPI as its CrossMenu subsystem; its API is documented there.
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
const REPO = "dustinlacewell/cairn-mods"; // GitHub repo that hosts the per-mod release assets

function requiresFromCsproj(modName: string): string[] {
  const csprojPath = resolve(MODS_DIR, modName, `${modName}.csproj`);
  if (!existsSync(csprojPath)) return [];
  const csproj = readFileSync(csprojPath, "utf8");
  return [...csproj.matchAll(/<ProjectReference\b[^>]*Include="[^"]*?([^"\\/]+)\.csproj"/g)]
    .map((m) => m[1].toLowerCase());
}

function csprojVersion(modName: string): string | null {
  const csprojPath = resolve(MODS_DIR, modName, `${modName}.csproj`);
  if (!existsSync(csprojPath)) return null;
  const m = readFileSync(csprojPath, "utf8").match(/<Version>(.+?)<\/Version>/);
  return m ? m[1] : null;
}

// A mod's API reference, generated from its C# /// doc comments by `wm api-docs <Mod>`
// into ./api/<Mod>.json — so the site reference never drifts from the source. Returns
// undefined when not generated, in which case the mod falls back to hand-authored sections.
function generatedSections(modName: string): DocSection[] | undefined {
  // Anchored on MODS_DIR (repo-root-relative, correct from both src/ and the bundled
  // dist/ chunk) rather than import.meta.url, which Astro rewrites at build time.
  const path = resolve(MODS_DIR, "..", "site", "src", "data", "api", `${modName}.json`);
  if (!existsSync(path)) return undefined;
  return JSON.parse(readFileSync(path, "utf8")).sections as DocSection[];
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
    version: "0.1.2",
    description: [
      "Auto-skips cutscenes, videos, opening titles, radio calls, and the nightmare sequence.",
    ],
    features: [
      "Skip cutscenes, videos, titles, radio calls, and the nightmare sequence",
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
      "The shared foundation Cairn mods build on — clean, named APIs over the game's internals, so you spend your time on your mod instead of reverse-engineering the plumbing.",
      "Hook into the game's menus, build settings pages, show in-world prompts, read and change the inventory, teleport the climber, and react to game state.",
    ],
    features: [
      "Add your own buttons to the Settings and pause menus",
      "Build native settings pages — toggles, sliders, dropdowns, text fields, and buttons — usable with keyboard, mouse, or controller",
      "Show button prompts on the HUD or floating in the world, with icons that match the player's input device",
      "Make things interactable by walking up to them, or by reaching out and grabbing them while climbing",
      "Read and change the player's inventory, and look up any item in the game",
      "Teleport the climber anywhere — the target area streams in automatically",
      "Enumerate the game's worlds, zones, and story beats",
      "Know the current game state (menu, gameplay, cutscene…) and react to transitions",
    ],
    download: TODO_DOWNLOAD,
    sections: generatedSections("CairnAPI"),
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
    sections: generatedSections("CairnModOptions"),
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
      "Built-in verbs: <code>eval</code>, <code>belay</code>, <code>find</code>, <code>inspect</code>, <code>ropes</code>, <code>renderers</code>, <code>gameplay</code>, <code>survival</code>, <code>edelweiss</code>, <code>input</code>, <code>emit</code>, <code>help</code>.",
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
          "One instance per port, probed from 14200 up. The chosen port is logged at startup. Both GET and POST are supported. Each instance also mirrors MelonLogger errors and warnings to a per-PID log at <code>MelonLoader/CairnDevTools/&lt;timestamp&gt;_pid&lt;pid&gt;_errors.log</code> — safe with multiple instances open.",
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
          "Roslyn-compiled C# against the live game. All loaded assemblies are referenced. Import namespaces are pre-added: <code>System</code>, <code>System.Linq</code>, <code>System.Collections.Generic</code>, <code>UnityEngine</code>, <code>Il2Cpp</code>, <code>Il2CppTheGameBakers.Cairn.Netplay</code>, <code>CairnDevTools</code> — so LINQ and co-op types resolve unqualified. End with <code>return &lt;expr&gt;</code> to get a value back.",
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
          "Scripts can <code>await Game.WaitFor</code> — the POST blocks and returns only when the script settles. Level-triggered: if the predicate is already true it completes on the next frame, so no edge is ever missed. Timeout faults with a <code>TimeoutException</code> carrying the message. A single blocking POST is capped at ~28 s by the overall script deadline, so drive long sequences across multiple calls rather than one very long await.",
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
            signature: "help",
            description: "List every registered verb. The default when no <code>q</code> is given.",
          },
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

// Derive version, download URL, and requires from each mod's csproj at build time, so the site
// never drifts from what's actually built/released (replaces a brittle CI string-patcher). The
// download URL matches the per-mod release tag <Mod>-v<Version>; only published mods get one (others
// keep the "coming soon" placeholder → rendered as "not released yet").
for (const m of mods) {
  m.requires = requiresFromCsproj(m.name);
  const v = csprojVersion(m.name);
  if (v) m.version = v;
  if (v && publishedSlugs.has(m.slug))
    m.download = `https://github.com/${REPO}/releases/download/${m.name}-v${v}/${m.name}.dll`;
}

export const kindLabel: Record<ModKind, string> = {
  player: "mod",
  library: "lib",
  dev: "dev",
};

export const publishedMods = mods.filter((m) => publishedSlugs.has(m.slug));
export const mod = (slug: string) => publishedMods.find((m) => m.slug === slug);
