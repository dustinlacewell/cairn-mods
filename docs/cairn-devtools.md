# CairnDevTools — live game interrogation (READ THIS BEFORE DEBUGGING ANYTHING)

`mods/CairnDevTools/` is a standalone MelonLoader mod that gives out-of-process tooling
(i.e. Claude in a shell) direct access to the RUNNING game. It exists because inferring
live state from 10-second log snapshots repeatedly produced wrong theories; one live query
ended hours of speculation more than once. **Default to the console for any "what is the
game doing right now" question.**

## The console

Each game instance serves HTTP on localhost, ports probed **14200-14209** (first instance
up grabs 14200, next 14201, …). The chosen port is logged at startup
(`console: live at http://127.0.0.1:14200/cmd?q=help`). **Port order ≠ host/joiner order**
— identify instances by asking them (`state`, or eval the player name), not by port.

```
GET  http://127.0.0.1:14200/cmd?q=<command+args>     # url-encoded
POST http://127.0.0.1:14200/  (raw C# body)          # implies eval — best for real code
```

Commands run **on the game main thread** (queued; the HTTP thread waits up to 30 s).
Built-ins:

| command | what |
|---|---|
| `belay` | **THE belay-state dump** — the complete climber/rope/climbot/piton connection graph in one call (see below) |
| `eval <C#>` | Roslyn-compiled C# against the live game (see below). **Scripts may `await Game.WaitFor(...)`** to drive the game to a state in one blocking call — see "Async eval" below. |
| `find <substr>` | all transforms matching name, with full hierarchy path + active + pos |
| `inspect <name-or-path>` | one object: transform, components (rope/renderer extras), children |
| `renderers [minSpan]` | every enabled Renderer with world bounds over N meters (default 300) — **this is how you find "thing rendering to outer space", whatever class it is** |
| `ropes` | every LogicalRope incl. inactive: path, active, init, len, vis, holders, world bounds |

### belay — the one command for "is the rope actually connected"

Built because piecemeal `eval` probes kept missing things — each one looked at a different slice,
so the full picture was never visible twice. `belay` dumps EVERYTHING for the local climber's
securing rope in one call: climber/harness attach pos + `IsSecured`; the securing rope's
identity/len/bounds/**attachEnd**; every HOLDER with its live attach-collider world pos; every PART
with both ends' **pin tag (climber/robot/quickdraw/FREE) + the actual pinned ObiCollider's
owner+position + particle pos**; the rendered LINE first/last points; the stock rope; the climbot
state; and the pitons.

A GOOD rope reads: each part `Begin [robot] pin='Climbot/attach begin'`, `End [climber]
pin='MC_Aava/AttachPoint'` (middle = quickdraw), bounds a few metres, LINE first≈climbot,
last≈climber. **FREE ends or bounds >10 m = broken.** The pin-tag + pinned-collider fields are the
ground truth — never infer connection from `secured`/`len` scalars (they lie while particles
diverge). Run `belay` on BOTH instances, every time — the two sides fail differently.

Source: `mods/CairnDevTools/Inspection.cs` (`Belay()`). Needs the `Il2CppObi` reference (GetAttachEnd
returns ObiCollider). If you ever need a follow-up query to understand the rope, EXTEND the tool.

### eval

- Every loaded assembly is referenced — game proxies, Unity modules, interop, all mods.
  Reach mod internals via statics: `CairnCoop.Core.Instance.Driver.DescribeGameState()`,
  `CairnCoop.Core.Instance.ForceUnfreeze()`.
- Imports preloaded: System, System.Linq, System.Collections.Generic, UnityEngine,
  Il2Cpp, Il2CppTheGameBakers.Cairn.Netplay. Anything else: fully qualify.
- `Globals.Bag` (static `Dictionary<string,object>`) persists between evals.
- First eval compiles cold (~2-5 s); later ones are fast.
- Multi-line scripts: POST the raw body (no URL-encoding pain). From bash:
  `curl -s --data 'var x = GameObject.Find("..."); return x.transform.position.ToString();' http://127.0.0.1:14201/`
- Return value: string passes through; IEnumerable joins per line (capped 200); anything
  else ToString. `return` statements work (script semantics).

### Async eval — `await Game.WaitFor(...)` (drive the game to a STATE in one POST)

**This is THE way to reach any game state from a script.** A `eval` body may now `await`, and the call
returns only when the script finishes — so one blocking POST can drive menu→gameplay, wait for a scene, wait
for the eagle-eye view, etc., with no events, no cursors, no sleep-poll. It **supersedes** the old
edge-triggered EventBus / `wait-event` readiness mechanism, which was useless across an agent's discrete turns
(an event that fired between turns was unrecoverable and hung forever). Readiness is a **level** (is the menu
up? has the pawn spawned?), not an edge — `WaitFor` samples the level each frame, so it's correct whether the
transition happened a frame ago or a minute ago. The missed-edge problem cannot exist by construction.

The script-visible API is the static `Game` class (in `CairnDevTools`, already imported):

| call | does |
|---|---|
| `await Game.WaitFor(() => bool, ms=30000)` | level-triggered poll on the main thread; completes the first frame the predicate is true (already-true → next frame). A predicate that THROWS (e.g. `FindObjectOfType<X>().f` before X exists) is a transient miss, not a fault, until the deadline. Faults `TimeoutException("timeout: <n>ms waiting")` on deadline. |
| `await Game.WaitFor(() => sample, v => done(v), ms)` | value-returning variant — returns the first sample for which `done` holds (e.g. wait for an object to exist AND grab it). |
| `await Game.Delay(ms)` / `Game.NextFrame()` | frame-pumped wall-clock delay / single-frame yield. |
| `await Game.WaitForMenu(ms=60000)` | MainMenu present (the continue-jump can fire). |
| `await Game.WaitForGameplay(ms=60000)` | climber pawn spawned (`PawnManager.MCSpawned`). |
| `await Game.WaitForFullyLoaded(ms=60000)` | load fully finished (`GlobalGameManager.loadingState == GameStarted`) — stricter than pawn-spawned. |
| `await Game.WaitForScene("CommonBaseScene", ms)` | active scene has that name. |
| `await Game.WaitForEagleEye(ms=30000)` | eagle-eye fast-travel warp view open. |
| `await Game.EnterGameplay(ms=90000)` | **the canonical "get me into a playable game"**: wait menu → `Continue()` → wait pawn-spawned → wait fully-loaded, all in one await. Idempotent (returns at once if already in gameplay). Returns `"gameplay-ready"`. |
| `Game.Continue()` | fire the menu→gameplay jump (= the `gameplay` verb). Synchronous; safe only from the menu. |
| `Game.Do("survival", "on")` | run any registered console verb inline by name, get its string result — orchestrate existing verbs without re-implementing. (Async/parking verbs aren't supported here.) |

**Canonical recipe — menu→gameplay in ONE blocking POST:**

```bash
curl -s -X POST "http://127.0.0.1:14200/cmd?q=eval" --data-binary @- <<'EOF'
return await Game.EnterGameplay();
EOF
```

Returns `gameplay-ready` the instant the game is fully loaded. (The longhand — `WaitForMenu` / `Continue` /
`WaitForGameplay` — is exactly what `EnterGameplay` does, if you need to interleave steps.)

**Useful level predicates** (all main-thread reads): MainMenu present
`FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true)!=null`; gameplay live `Il2Cpp.PawnManager.MCSpawned`;
fully loaded `MoSingleton<GlobalGameManager>.Instance.loadingState == LoadingState.GameStarted`; not streaming
`MoSingleton<CairnSceneManager>.Instance.IsBusy()==false`; free-solo
`MoSingleton<PawnManager>.Instance.ClimbingPawnController.InFreeSolo()`; scene
`SceneManager.GetActiveScene().name`; eagle-eye open `FindObjectOfType<EagleEyeUI>(true).isInFreeRoamView`;
co-op `CairnCoop.Core.Instance.Driver.InCoopSession`.

**How it works** (`FramePump.cs` + `Core.cs` `Eval.Run`): a `FrameSyncContext : SynchronizationContext`
installed while the Roslyn script runs routes every `await` continuation onto the main-thread pump
(`FramePump.Pump()`, called once per `Core.OnUpdate`). A **sync** script (no `await`) returns inline — the
old fast path, unchanged. An **async** one PARKS its HTTP reply (`DebugConsole.Parked` sentinel +
`CurrentReply`); the frame-pump then polls `WaitFor` predicates and drains await-continuations each frame
until the script's Task settles, at which point the parked reply is completed. Per-script deadline 28000ms <
the 30000ms HTTP wait cap, so a never-settling script faults cleanly instead of abandoning the reply.
**Add a new named wait** (e.g. `WaitForRoom`) as a one-liner in `Game` rather than re-deriving its predicate
at each call site.

### The workflow that actually works

1. Reproduce the symptom in-game.
2. `renderers 200` / `ropes` to identify offending OBJECTS by hierarchy path.
3. `inspect` them; then **test the fix live via eval** (SetVisible, SetActive, move
   transforms, call game methods) and confirm the symptom changes on screen.
4. Only then bake the verified fix into mod code.
   Example: the ghost-climbot rope streak was identified, confirmed, and its fix
   (SetVisible(false)) validated live in under a minute after days of log-based guessing.
5. Vanilla fights back: if you SetActive(false)/change something and it reverts within
   seconds (eval again to check!), a per-frame vanilla path owns that state — patch the
   data it reads (or its inputs), don't arm-wrestle the state itself.

## Implementation notes / gotchas (hard-won)

- **Roslyn version is pinned to 4.4.0.** 4.9.x depends on System.Collections.Immutable
  8.0 which does NOT load on MelonLoader's .NET 6 runtime (FileLoadException). 4.4.0's
  dependency chain is net6-native.
- csproj needs `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` or NuGet
  assemblies never reach the build output; the InstallToGame target then copies
  everything except the mod dll into the game's `UserLibs\` (MelonLoader's mod-dependency
  resolution dir).
- `Eval` and its `Globals` class must be **public** — Roslyn's emitted submission
  assembly accesses them across assembly boundaries (TypeAccessException otherwise).
- One `HttpListener` that failed `Start()` is poisoned; create a **fresh listener per
  port attempt** or the second instance never gets a console.
- IL2CPP objects are main-thread-only: ALL command execution goes through the
  `Tick()` pump in `OnUpdate`. Never touch game objects from the HTTP thread.
- `UnityEngine.Application.logMessageReceived` is **stripped** from the interop proxies.
  For exception visibility use `MelonLogger.ErrorCallbackHandler`/`WarningCallbackHandler`
  — that stream carries the IL2CPP native→managed trampoline exceptions (the important
  ones). CairnDevTools mirrors them (deduped) to
  `MelonLoader/CairnDevTools/<ts>_pid<N>_errors.log` per instance.
- UnityExplorer does NOT work here: the MelonLoader net6preview build ships a
  UniverseLib that still wants UnhollowerBaseLib (FileNotFound at init). Don't retry;
  this console replaces it.

## Build & deploy discipline (burned twice)

- The dll is locked while instances run: **kill → build → verify → launch**. Compile-only:
  `dotnet build -p:NoInstall=1`.
- Background deploys MUST check the build output for `error CS|error MSB|error NU`
  (NOT bare "error" — it false-positives on "0 Error(s)") **and** verify the installed
  dll's LastWriteTime is fresh. Two "deploys" once shipped nothing for three rounds
  because a compile error scrolled by unread; the canonical deploy one-liner:

```powershell
try { Stop-Process -Name Cairn -Force -ErrorAction Stop } catch {}; Start-Sleep -Seconds 3;
$out = dotnet build <csproj> -c Release 2>&1;
$e = $out | Select-String "error CS|error MSB|error NU"; if ($e) { $e | Select-Object -First 5; throw "BUILD FAILED" };
$dll = Get-Item "P:\Steam\steamapps\common\Cairn\Mods\<Mod>.dll";
if ((Get-Date) - $dll.LastWriteTime -gt [TimeSpan]::FromMinutes(2)) { throw "DLL STALE" };
powershell -ExecutionPolicy Bypass -File "D:\code\ghidra\cairn-mp\tools\run-local-coop-test.ps1"
```

- C# string literals in dlls are UTF-16: to check whether a deployed dll contains a
  string, read bytes and search with `[Text.Encoding]::Unicode` (or `strings -el`),
  never plain `strings`/ASCII grep.
- Multiple instances may exist beyond the two the script launches (user-started ones) —
  check `Get-Process Cairn` count before reasoning about "the two instances".
