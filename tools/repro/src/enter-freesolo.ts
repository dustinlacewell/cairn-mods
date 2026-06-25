// The async-eval script that selects a distinct free-solo save and FIRES the menu→gameplay jump, in a single
// blocking POST — no sleep-poll, no preview-cache race. Uses the CairnDevTools `Game.WaitFor` API (docs:
// docs/cairn-devtools.md §"Async eval"): readiness is a LEVEL, so the cache-fill step completes the first frame
// a usable save is cached, whether that already happened or is about to.
//
// NOTE — it does NOT await full gameplay here: the per-script reply deadline is 28s, and a slow same-profile
// continue (the 3rd instance) loads longer than that, which would time out the reply even though the load
// succeeds. So this script returns once the jump is FIRED (Game.Continue()); the caller (setup-n2) then polls
// WaitForGameplay in short sub-28s POSTs until the pawn has spawned + the load finished.
//
// Steps, awaited in-process on the game main thread:
//   1. WaitForMenu — the MainMenu exists.
//   2. WaitFor(a USABLE free-solo save is cached) — Constraints==2561 && Continuable && slot ∉ avoid. (Waiting
//      for the exact thing we select, NOT "list non-empty" — the cache fills incrementally, so the weaker level
//      passed before the free-solo saves appeared → no-distinct-freesolo-save.)
//   3. ChangeActiveSlot(slot) → Game.Continue() (fire the jump). Returns "jump-fired slot=<n>".

/** Build the per-instance script. `avoid` = slots already taken by earlier instances (so each picks a distinct save). */
export function enterFreeSoloScript(avoid: number[]): string {
  const avoidList = avoid.length ? avoid.join(", ") : "-1";
  return `
try {
  await Game.WaitForMenu(60000);
  // Wait until a USABLE free-solo save (2561 && Continuable && not-avoided) is actually CACHED — NOT just
  // "the list is non-empty". The preview cache fills INCREMENTALLY at boot (the gym/512 slot can cache before
  // the free-solo ones), so "Count > 0" is too weak a level — it passed before the free-solo saves appeared
  // and the scan found nothing (the no-distinct-freesolo-save race). Waiting for the exact thing we're about
  // to select is the correct level. Predicate may throw while menu/list is null — a transient miss.
  var avoid = new System.Collections.Generic.HashSet<int>(new int[] { ${avoidList} });
  System.Func<Il2CppTheGameBakers.Cairn.Save.SavegameManager.SavePreviewInfo, bool> usable = p =>
    p.Continuable && (int)p.gameSetup.Constraints == 2561 && !avoid.Contains((int)p.slot);
  await Game.WaitFor(() => {
    var mm = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true);
    var lists = mm != null ? mm.PreviewsPerChannel : null;
    if (lists == null) return false;
    var story = lists[(int)Il2CppTheGameBakers.Cairn.Save.SaveType.Story];
    if (story == null) return false;
    for (int i = 0; i < story.Count; i++) if (usable(story[i])) return true;
    return false;
  }, 60000);

  var mm2 = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true);
  var sgm = Il2CppTheGameBakers.Cairn.Save.SavegameManager.Instance;
  if (mm2 == null || sgm == null) return "err:menu/sgm null after wait";
  var story2 = mm2.PreviewsPerChannel[(int)Il2CppTheGameBakers.Cairn.Save.SaveType.Story];
  int chosen = -1;
  for (int i = 0; i < story2.Count; i++) {
    var p = story2[i];
    if (!usable(p)) continue;
    sgm.ChangeActiveSlot(p.slot);
    chosen = (int)p.slot;
    break;
  }
  if (chosen < 0) return "err:no-distinct-freesolo-save";

  Game.Continue(); // fire the menu→gameplay jump; the caller polls WaitForGameplay (load can exceed the 28s reply cap)
  return "jump-fired slot=" + chosen;
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();
}

/** Poll-for-gameplay script: a SHORT (≤20s) WaitFor on pawn-spawned + fully-loaded, run in a retry loop by the
 *  caller so a long load doesn't trip the 28s reply cap. Returns "gameplay-ready" once spawned + GameStarted,
 *  else "waiting" (predicate not yet true within this short window — caller re-POSTs). */
export const POLL_GAMEPLAY = `
try {
  // Use the DevTools named waits (they encode the correct predicates — pawn spawned + fully loaded). A short
  // window so the reply never trips the 28s per-script cap; "waiting" on a transient miss → caller re-POSTs.
  try {
    await Game.WaitForGameplay(20000);
    await Game.WaitForFullyLoaded(8000);
    return "gameplay-ready";
  } catch (System.Exception) { return "waiting"; }
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();
