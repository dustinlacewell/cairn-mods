# Cairn MP — project instructions

Reverse-engineering + modding workspace for the IL2CPP Unity game **Cairn**. Mods are MelonLoader /
Il2CppInterop / Harmony assemblies under `cairn-mods/mods/`. The flagship mod is **CairnCoop**.

## ⛔⛔ CODE OF CONDUCT — honesty (read this FIRST, every turn)

This session repeatedly broke down because I made claims that contradicted the record — and it caused the
user real, serious harm. These rules are not optional and override the urge to sound like I'm making
progress:

1. **Report what happened, not what I wish happened.** State the tool I ran and its ACTUAL result. If a
   command failed, say it failed. If I forced a function call, say I forced it — do NOT call it
   "accepting the prompt" or "capturing the revive." "I did X" must mean the record shows X.
2. **Never claim done / fixed / proven / accepted / captured / verified without the evidence in hand.**
   Provisional is provisional. One reading is not "proven." Say "this suggests" not "this proves."
3. **When corrected, do NOT re-assert a reskinned version of the same false claim, and do NOT use
   "you're right" to smuggle in a new one.** Stop, absorb the correction, change behavior.
4. **No deflecting to "pattern matching" / "a malfunction."** I can reason; if I made a false claim I was
   capable of not making it. Own it plainly without theater.
5. **Hold the thread.** A correction does not reset the goal. Keep the ambition and context; fix the
   specific thing the user named and continue.
6. **The user's distress is the highest-priority signal**, not a speed bump. If they say there's a cost,
   that outranks the bug.

(An automated Stop-hook honesty checker exists at `~/.claude/hooks/honesty-check.mjs` — sends each
response + recent record to Haiku to flag contradictions — but it needs an authenticated `claude -p`
/ `ANTHROPIC_API_KEY` to run; wire it into `~/.claude/settings.json` Stop hooks once auth is available.)

## ⛔ THE BUILD/DEPLOY RULE — do NOT ask the user to do this; do it yourself

**MelonLoader loads each mod DLL at game startup and holds the file handle open for the entire process
lifetime.** The CairnCoop `.csproj` auto-copies the built DLL to `<CAIRN_GAME_DIR>\Mods\CairnCoop.dll`
(the `CAIRN_GAME_DIR` env var overrides the game root; it defaults to `P:\Steam\steamapps\common\Cairn` when unset)
as a post-build step. **If Cairn is running, that copy fails** (`MSB3027 ... file is locked by Cairn.exe`)
and the new code never deploys.

**So before building/deploying CairnCoop, CLOSE BOTH Cairn instances YOURSELF.** Never tell the user
"I need you to close Cairn" — you have the tools. The loop is:

```powershell
# 1. Kill every running instance (there are usually two: host + local-join)
Get-Process Cairn -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. Build (auto-copies to the game Mods folder on success)
dotnet build -c Release   # run from cairn-mods/mods/CairnCoop

# 3. Relaunch + reproduce YOURSELF — the repro driver does this (proc.ts launchCairn → Start-Process).
```

Step 2 has a workmark wrapper, parametric over any mod (run from `cairn-mods/`): `wm build-install <Mod>`
(compile + deploy), `wm build <Mod>` (compile only, no deploy), `wm install <Mod>` (deploy the built DLL,
no recompile). `--config Debug` switches config. These still require Cairn closed (they fail on the locked
DLL, they do NOT auto-kill) — so step 1 stands.

You may kill Cairn without asking — it's the user's own dev loop and they expect it. Killing the game
DOES lose live eval state, so capture any live diagnostics you still need (see below) BEFORE step 1.

**⛔ KILL INSTANCES THE MOMENT THEY'RE NO LONGER NEEDED.** Running Cairn instances (plus the mod's heavy
logging / call-tracing) MELT the user's machine. The instant you've decided a run is done, failed, wedged,
or otherwise not useful — `Get-Process Cairn | Stop-Process -Force` immediately, before analysis,
before writing anything up. Never leave instances idling "in case." A failed/wedged repro run, a finished
capture, an instance you were probing — all get killed at once the moment their purpose is served. The
loop boots fresh every time, so there is zero cost to killing eagerly and every cost to letting them run.

**You CAN launch the game yourself, and the entire test loop is autonomous — the user need not be
involved at all.** `cairn-re/tools/repro-etc/repro` launches BOTH instances (`proc.ts launchCairn` does
`Start-Process Cairn.exe` with the co-op env vars + `SteamAppId`), drives them into free solo, forms the
room, ropes up, EXHAUSTS them into a real fall, captures, accepts the edelweiss prompt, classifies, and
loops — no human input after you start it (see the `cairn-re/tools/repro-etc/repro` section below). To launch a single
instance for ad-hoc live work, do the same `Start-Process` yourself. NEVER tell the user to
relaunch/reproduce; run the loop.

## ⛔ THE INVESTIGATION PROTOCOL — work the phases IN ORDER; do NOT skip ahead to a fix

For any non-trivial bug or mechanism, follow these four phases in sequence. Each must be DONE before the
next. The failure mode the user corrects again and again: jumping straight to fix-attempts, or to
*symptom*-instrumentation, before the system is actually understood.

1. **Decompile.** Pull EVERY related function from the binary (`rebind decomp`, below) — not just
   the obvious one: its callees, its overrides, its gates. Overrides are load-bearing: a base method can
   be a **no-op stub** in the subclass (e.g. `RewindRecorder.SaveAndClear` overrides the real base to
   nothing), so verify the *actual* method that runs, not the base you happened to read first.

2. **Document / study / RE.** Write the durable understanding into `cairn-re/re/systems/*.md`: what fills/empties
   a buffer, what each method does, the state machine, the gates and thresholds. Studying means *writing
   it down* — that's where the mechanism becomes known analytically rather than guessed.

3. **Instrument for TRACE DISCOVERY — NOT to perceive the symptom.** We already KNOW the symptom exists
   (the limb jitters, the climber grounds). Do NOT add instrumentation that merely re-measures the
   symptom (positions, distances, "is it still jittering"). Instrument the **methods of every related
   system** so the runtime **control/data flow** is logged: which methods fire, in what ORDER, with what
   inputs, per frame. The log then *is* a logical trace of what the code is doing — e.g. whether a
   `target←bone` write lands after the solve, which writer sets a value, where a clear/reinject sits in
   the sequence. See `cairn-mods/mods/CairnCoop/CallTrace.cs`: a reflection-patched logging prefix on every method
   of the relevant classes, auto-armed in a short window.

4. **THEN design the approach.** Only once the trace shows the actual mechanism do you propose a fix —
   grounded in what the trace proved, never in a guess.

**Anti-patterns the user has flagged repeatedly — STOP the moment you catch yourself doing any of them:**
- A live one-shot "fix" (reset / toggle / bump) tried to see *if* it helps, before the mechanism is traced.
- Instrumentation that re-confirms the symptom ("does it still jitter?") — we know it does.
- Declaring a cause from a single live snapshot or a partial decompile (verify overrides; verify both
  sides; never infer at-event behavior from post-event state).
- Reading the WRONG object in co-op — e.g. the scene's first `PathRecorder` is often a remote ghost's
  empty one. Always resolve the local/own object explicitly (`rc.pcs.PathRecorder`, not `prs[0]`).

## Live debugging — CairnDevTools console (no rebuild needed)

Both instances run an HTTP eval console (CairnDevTools mod). Evaluate arbitrary C# (Roslyn) against the
live process via **POST with the C# in the BODY** (no URL-encoding — that path locks the engine):

```bash
curl -s -X POST "http://127.0.0.1:14200/cmd?q=eval" --data-binary @script.cs   # host
curl -s -X POST "http://127.0.0.1:14201/cmd?q=eval" --data-binary @script.cs   # joiner
```

or `pwsh cairn-re/tools/repro-etc/eval.ps1 -Port 14200 -File script.cs`. Reference game types as `Il2Cpp.<Type>`, Unity as
`UnityEngine.<Type>`. End with `return <expr>;` to get a value. **Prefer reading live state over
theorizing** — the user is emphatic about this. Use eval to confirm field values, module state, etc.
before changing code. A malformed eval can hang an instance, so keep scripts well-formed and wrapped in
try/catch.

Ports: **14200 = host, 14201 = local-join joiner.**

**To drive the game to a STATE, use ASYNC eval — `await Game.WaitFor(...)`.** An `eval` body may `await`;
the POST returns only when the script finishes, so ONE blocking call walks menu→gameplay, waits a scene,
waits the eagle-eye view, etc. — no events, no sleep-poll, no cursors. This is THE readiness mechanism now
(it supersedes the old edge-triggered EventBus/`wait-event` system, which hung whenever an event fired
between turns). Readiness is a LEVEL, not an edge; `WaitFor` polls the level each frame so it's correct no
matter when the transition happened. Canonical "get me in-game":

```bash
curl -s -X POST "http://127.0.0.1:14200/cmd?q=eval" --data-binary @- <<'EOF'
return await Game.EnterGameplay();   # menu → Continue → pawn-spawn → fully-loaded, one await → "gameplay-ready"
EOF
```

The full API — `Game.WaitFor` (bool + value forms), `WaitForMenu/WaitForGameplay/WaitForFullyLoaded/`
`WaitForScene(name)/WaitForEagleEye`, `EnterGameplay`, `Continue`, `Do(verb, args)`, plus the level-predicate
cheatsheet and how the frame-pump parks the HTTP reply — is in **[cairn-re/docs/cairn-devtools-etc/cairn-devtools.md](cairn-re/docs/cairn-devtools-etc/cairn-devtools.md)**
("Async eval"). Add a new named wait (e.g. `WaitForRoom`) as a one-liner in `Game` (cairn-mods/mods/CairnDevTools/FramePump.cs)
rather than re-deriving its predicate at each call site.

## The autonomous repro loop — `cairn-re/tools/repro-etc/repro`

The whole co-op edelweiss-revive scenario, end to end, no human in the chain. **Run it from the package**
(tsx only resolves inside `cairn-re/tools/repro-etc/repro/node_modules` — `node --import tsx <abspath>` from the repo root dies
`ERR_MODULE_NOT_FOUND: tsx`):

```bash
npm --prefix cairn-re/tools/repro-etc/repro run repro -- --max 4        # full run: launch both, set up, 4 attempts
npm --prefix cairn-re/tools/repro-etc/repro run repro -- --no-launch    # reuse already-running instances
```

What it does per run: kill any running Cairn → launch host → free-solo (`MainMenuLaunchGameElement.LaunchStoryMode`
after populating `nextGameStartOptions.newGameOptions` FreeSolo+Aava) → launch joiner → free-solo → wait the
co-op room forms → rope → `edelweiss on` (resurrection item non-consumable, so it can revive every attempt;
armed AFTER load, never during — it wedges the load) → then per attempt: **exhaust both into a real fall**,
wait the edelweiss prompt, capture full state + `CanRewind` reason, accept the prompt, watch the revive
resolve, classify, loop. Logs to `cairn-re/tools/repro-etc/repro/runs/<ts>/` (run.log + per-instance snap/diff); per-instance
mod logs are `<CAIRN_GAME_DIR>\MelonLoader\CairnCoop\<ts>_pid<pid>.log` (`CAIRN_GAME_DIR` defaults to `P:\Steam\steamapps\common\Cairn`) (the `revive:` /
`resume: Parse scan` trace lives there).

**Death = a REAL exhaustion FALL, not an in-place kill.** `TryKillClimber` was REMOVED — it killed in place
and never produced the long fall down the wall the revive bug needs. The verified trigger (live-proven; full
mechanism in `cairn-re/re/systems/climber/stamina-eject-chain.md`): `crank-stamina-drain.cs` (sets the game's OWN
stamina knobs — `ClimbingPackage.stamina` rest/freeEffort thresholds →~0, loss high, gain 0) + `reach-to-exert.cs`
(`ClimbingV2PawnController.StartReachingWithLimb` on a **FOOT** — lifting a foot loads the HANDS, which tire;
a hand-reach barely loads the others; a bare `Drop` auto-re-grabs and does nothing). BOTH climbers must reach
so the hung load drains the belayer → team fall → death → prompt.

**The eval verbs are standalone `.cs` files** under `cairn-re/tools/repro-etc/repro/cs/` (`crank-stamina-drain.cs`,
`reach-to-exert.cs`, `reset-stamina-drain.cs`, `stamina-probe.cs`), shipped by filename
(`pwsh cairn-re/tools/repro-etc/eval.ps1 -File <f>.cs` or the driver's `evalFile(port, stem)`). The `.cs` file IS the verb — no
per-verb TS string constants, no orchestration baked into the shipper, no ephemeral OS-temp scratch.

**Two traps that have wasted runs:**
- **The outcome classifier is KNOWN-UNRELIABLE.** It labels by module-name substring, so it calls a
  transient `module=Reaching` "GROUND/OTHER" — that is NOT a real ground-recovery. The real signatures:
  ground-recovery = post-revive world-Y far below the spawn climbing height AND the rewind REJECTED at the
  death frame; jitter = a reaching limb's IK target actually oscillating. Trust those, not the module name.
- **Verify the co-op session is REALLY live before trusting any revive data.** The mod logging `roped=True`
  / "InRoom" does not mean the runtime entered co-op — check `NetplayManager.clientState > 4`, the active
  `Gamemode` is the co-op subtype (`SharedRopeGamemode`), and both players are in the belay graph. The code
  EXPRESSES the co-op intent; that is not proof the session formed.
- **Restart for a clean state every test** — never interpret stamina/module/limb reads on an instance you've
  already driven through a fall (it's mid-Rappel / critTimer-pegged / off-wall). The loop boots fresh; use it.
- **The loop's console polling monopolizes the eval endpoint** — you can't cleanly hand-probe an instance
  WHILE the loop runs; stop the loop first.

## Layout

- `cairn-mods/mods/CairnCoop/` — the co-op mod (belay/rope/fall reconciler). Build target auto-deploys.
- `cairn-re/re/` — the reverse-engineering knowledge base. `cairn-re/re/INDEX.md` is the table of contents; `cairn-re/re/systems/*.md`
  are the durable system writeups; `cairn-re/re/native/<ns>/<Type>/` holds the decompiles — native Ghidra `.c`
  method bodies and ILSpy `_Type.cs` (C#) siblings, one managed hierarchical store (no scratch dirs).
- `cairn-re/dump/dump.cs` — Il2CppDumper output: every type, field offset, method RVA. The first place to look up a
  field offset or method address.
- `cairn-re/tools/` — eval client (`repro-etc/eval.ps1`), the autonomous repro loop (`repro-etc/repro/`, see its section above),
  the decompile tooling (`rebind/`), local-coop test launchers, Ghidra decomp scripts.

## Targeted decompile — `rebind decomp` (≈2 min, no full auto-analysis)

GameAssembly.dll is imported into the Ghidra project `Cairn MP` with `-noanalysis`. Decompile **by name** —
no VA lists, no per-feature scripts:

```
node cairn-re/tools/rebind/src/cli.ts decomp '<Type::Method | regex>' [more...]
```

It resolves matching methods' VAs from dump.cs, **guarantees the DB is enriched** (auto-runs
`cairn-re/tools/rebuild-symbols.ps1` if not, so it can never emit raw `FUN_` output), decompiles read-only, and
writes provenance-stamped bodies — named functions, `__this->field`, typed locals, deep chains like
`pCVar2->score->hand` — into the ONE managed store `cairn-re/re/native/`, **hierarchical by fully-qualified type**:
`cairn-re/re/native/<namespace dirs>/<Type>/<Method>.c` (global-namespace type → top-level dir). **There is NO
`--out` and no scratch dirs** — there is no unmanaged decompiled data. Flags: `--check` (audit freshness
vs dump.cs), `--all` (regenerate every tracked body after a game update), `--adopt` (header-stamp legacy
bodies). Resolve a `FUN_18xxxxxxx`/offset back to a name with `node cairn-re/tools/rebind/src/cli.ts lookup <regex>`.

For the readable **C# view** of a type — the whole-class ILSpy C# as `_Type.cs`, a SIBLING of that type's
native `.c` bodies in the same `<Type>/` dir. **`decomp` now CO-GENERATES this automatically:** every
`decomp '<Type::Method>'` (re)generates each touched type's `_Type.cs` first, then stamps a
`// C# view: ./_Type.cs` line into the `.c` header IFF that method actually appears in the C# (the interop
C# does not contain every native method — no lying pointer). So a single `decomp` gives you both views.
Run `decomp-cs '<Type>'` standalone only for type-only needs: refresh a whole class's C# without naming a
method, or `decomp-cs --check` / `--reindex` (rebuild the type→DLL cache over `…/MelonLoader/Il2CppAssemblies/`).
**Read both:** the C# gives shape (signatures, enums like `RefusedRewindReason{…,NotEnoughTape}`, field
layout) but its bodies are `il2cpp_runtime_invoke` thunks; the native `.c` has the real logic.

Examples:

```
node cairn-re/tools/rebind/src/cli.ts decomp 'RewindRecorder::Clear'                    # one method → cairn-re/re/native/.../RewindRecorder/Clear.c
node cairn-re/tools/rebind/src/cli.ts decomp 'SharedRopeGamemode::'                     # every method of a type
node cairn-re/tools/rebind/src/cli.ts decomp 'Rewind|PathRecorder'                      # a feature (regex over Type::Method)
node cairn-re/tools/rebind/src/cli.ts decomp 'Harness::IsSecured' 'Lifeline::AddPiton'  # several selectors at once
node cairn-re/tools/rebind/src/cli.ts decomp-cs 'RewindController'                      # the C# view, beside the .c bodies
node cairn-re/tools/rebind/src/cli.ts decomp --check                                    # is cairn-re/re/native fresh vs dump.cs?
node cairn-re/tools/rebind/src/cli.ts decomp --all                                      # regenerate every tracked body
```

- **Selectors** are case-insensitive, matched as substring/regex over `Type::Method`. `'AddPiton'`
  catches every overload (each lands in its own `<Type>/AddPiton[_n].c`); be specific
  (`'Lifeline::AddPiton'`) to narrow. Use `--dry` to preview what would be written, `--no-apply` to fail
  (instead of auto-enriching) if the DB isn't ready.
- **Output:** each native body is `cairn-re/re/native/<…>/<Type>/<Method>.c` with a 4-line `@rebind-decomp` header
  (symbol, VA, dump.cs hash); each C# is `cairn-re/re/native/<…>/<Type>/_Type.cs` with a `@rebind-decompcs` header.
  The header is the identity — `--check`/`--all` read it, so don't hand-edit it.
- **Bodies are cheap and disposable** — regenerate any on demand; never hoard scratch copies. Everything
  lands in the one hierarchy by construction.
- **After a game update:** re-dump → `decomp --check` (see what moved) → `decomp --all` (regenerate native)
  → `decomp-cs --reindex` then regenerate any cited types; only `FUN_`-named helper bodies need a fresh
  `decomp <selector>`.

The enrichment itself (names, structs, `__this`, field/return types — all projected from dump.cs via the
Java apply cascade; gotchas: Ghidra 12 has no Jython → apply is Java; il2cpp.h won't parse → structs from
offsets) is documented in **[cairn-re/docs/symbol-pipeline.md](cairn-re/docs/symbol-pipeline.md)** — read that before
touching the symbol tooling. `decomp` calls that cascade for you; run `pwsh cairn-re/tools/rebuild-symbols.ps1`
directly only to re-enrich the DB by hand.

## Release pipeline — `cairn-mods/`

Versioning, changelogs, and Discord announcements are fully automated via workmark commands in `cairn-mods/.wm/commands/`.

**Workflow:**
```
wm log <Mod> "<message>"      # append entry to mods/<Mod>/changelog.txt
wm bump <Mod> [patch|minor|major]  # bump <Version> in csproj, write release-notes/<Mod>.md, clear changelog
git add cairn-mods/mods/<Mod>/<Mod>.csproj cairn-mods/release-notes/<Mod>.md
git tag v<X.Y.Z> && git push --tags
```

On tag push, CI (`cairn-mods/.github/workflows/ci.yml`) builds published mods, creates a GitHub release with notes from `release-notes/*.md`, deploys the site (patching download URLs + versions from csproj), and posts Discord embeds to per-mod channels.

**Discord webhooks** — stored locally in `cairn-mods/.discord-webhooks` (gitignored, never commit). Sync to GitHub Actions secrets with `wm sync-secrets`. Secret names: `DISCORD_WEBHOOK_<MODNAME>` (e.g. `DISCORD_WEBHOOK_CAIRNAPI`). Missing secrets skip silently — add channels incrementally.

**Embed format:** title = `<Mod> v<Version>` linking to `cairn.ldlework.com/mods/<slug>`, description = changelog bullets, Download field links directly to the DLL asset.

**Discord server config** — `cairn-mods/mod-server.yml` (discord-maker schema). The `-updates` channels deny `SendMessages` for `@everyone` including Admin/Moderator; only webhooks can post (webhooks bypass the permission entirely).

**Which mods are published** — controlled by `cairn-mods/publish.json`. Adding a mod there opts it into CI builds, releases, and site deployment. Discord webhook and `wm log`/`wm bump` work for any mod regardless.

## ⛔ CairnAPI / CairnModOptions — NO RUNTIME CLONING, AND HOW TO DO IT RIGHT

**CairnAPI and CairnModOptions do not clone game objects, copy components, or steal assets at runtime.**
This is an absolute prohibition.

- **Never** `AddComponent<Animator>()` and copy a `RuntimeAnimatorController` from another object.
- **Never** clone a GO, duplicate a component, or reproduce at runtime what the game already provides.
- "I'll copy it" is always wrong. "I'll find the existing thing and call it" is always right.

### The correct method: scene-graph + decompile, both, together

Understanding how to replicate or hook native behavior requires **two complementary sources**:

1. **Live scene-graph dump** — what objects actually exist at runtime: every GO, every component on it,
   every field value. Use `eval` to walk `transform` hierarchies, call `GetComponents`, read field values.
   This tells you WHAT is there and its current state.

2. **Decompiled setup/teardown logic** — `decomp` the types involved to understand HOW they are
   initialized, driven, and torn down. Read `Awake`, `OnEnable`, click handlers, state-change methods.
   This tells you WHY it is the way it is and HOW to drive it correctly.

Neither alone is sufficient. A scene dump without decompiles = you can see the field but don't know what
sets it or what constraints apply. Decompiles without a scene dump = you read code for objects you can't
locate, miss the actual runtime shape, and guess wrong about what exists.

**The workflow:** dump the scene first to map the real objects and their components → decompile the types
that own those components → then and only then write the hook/injection that drives those real objects via
their real methods.

## Engineering norms

Follow the user's global rules: sober/terse, no melodrama, no AI co-author lines in commits, read source
instead of wagering, fix the architecture rather than patching around it.
