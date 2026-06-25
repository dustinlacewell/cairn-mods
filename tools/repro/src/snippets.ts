// The C# (Roslyn) eval snippets, kept verbatim as string constants so they read exactly as they run in
// the game and stay diffable against the dump / RE docs. Each ends with `return <expr>;` so the console
// hands the value back as text. Types are referenced as Il2Cpp.<T> / UnityEngine.<T> (the eval pre-imports
// `System`, `System.Linq`, `UnityEngine`, `Il2Cpp`).
//
// Field offsets and types cited here are from dump.cs / re/systems/climber/fatigue-system.md:
//   ClimbingV2PawnLimb.CurrentStamina      — public { get; set; }  (dump.cs:52100) writes currentStaminaValue@0x3AC
//   ClimbingV2PawnLimb.DebugStaminaOverride— public Nullable<float> (dump.cs:51976) pins stamina in UpdateStamina
//   ClimbingV2PawnController.criticalStaminaTimer — float @0x290 (the eject countdown; verified)

/** STEP 0.5 verify: hunger pinned full after `survival on`. Returns the ratio (expect "1"). */
export const HUNGER_RATIO = `
try { return Il2Cpp.MoSingleton<Il2Cpp.GameDataManager>.Instance.HungerRatio.ToString("F2"); }
catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/** STEP 1 readiness: the MainMenu is up (past the splash), so the continue-save jump (START_FREESOLO) can
 * run. TransitionToContinueIfAvailable is gated on the menu existing and not mid-animation; we only need
 * the MainMenu object here. Returns "ready", "no-menu" (still on splash), or "err:..". */
export const MENU_READY = `
try {
  var mm = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true);
  if (mm == null) return "no-menu";
  return "ready";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/** STEP 1 launch-confirm: did the continue actually LEAVE the menu (load started), or did it no-op and
 * we're still sitting at the MainMenu? A single TransitionToContinueIfAvailable fire can no-op if it races
 * the menu's fireable window, so enterFreeSolo re-fires until this reports launched. "launched" = the
 * MainMenu object is gone (we left it) OR GlobalGameManager has entered the Loading pipeline (loadingState
 * Loading..PreGameStarted with no menu). Returns "launched" | "atMenu(step=N animating=B)" | "err:..". */
export const LAUNCH_CONFIRM = `
try {
  var mm = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true);
  if (mm == null) return "launched";
  return "atMenu(step=" + (int)mm.step + " animating=" + mm.isAnimating + ")";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/** STEP 1 probe: are we in a climbing scene AND in free solo? Returns "freesolo=<bool> pc=<bool>". */
export const IN_FREESOLO = `
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "freesolo=false pc=false";
  return "freesolo=" + pc.InFreeSolo() + " pc=true";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/**
 * STEP 1 (co-op distinct nonce free-solo save): make a FREE-SOLO nonce save the active story slot, so the
 * subsequent continue (START_FREESOLO) loads a free-solo WALL climb — and one whose slot differs from the
 * host's, so the two instances open DIFFERENT on-disk files (the same-save case wedges the 2nd at step=16
 * forever via the FileShare.None exclusive open; proven re/systems/save-load/save-file-open-fileshare.md).
 *
 * Free-solo nonce save signature (VERIFIED LIVE 2026-06-18 over this profile's 12 story saves):
 *   `gameSetup.Constraints == 2561` (PermaDeath|NoTutorials|NoAssistMode = the free-solo difficulty preset)
 *   AND `Continuable == true`.
 * This cleanly selects ONLY the throwaway free-solo nonce saves and PROTECTS the real save:
 *   - the user's real beaten-the-game save reads Continuable=FALSE (wt≈140h) → excluded;
 *   - the older "gym" save reads Constraints=512 (NoTutorials only) → excluded;
 *   - the 10 nonce free-solo saves read Constraints=2561, Continuable=true → eligible.
 * (`GameSetup.IsStoryDisabled` / NoStory was tried and REFUTED — all these saves read story-enabled.)
 *
 * Reads the MainMenu's already-cached Story previews via `PreviewsPerChannel[Story]` (a List<SavePreviewInfo>)
 * — NOT TryGetCachedPreview(out …), whose out-struct marshalling HARD-CRASHED the IL2CPP runtime (ECONNRESET).
 * Indexing the list returns each SavePreviewInfo as a materialized interop object; `.slot` (byte field),
 * `.Continuable` (bool getter), and `.gameSetup.Constraints` are all crash-safe (verified incrementally).
 * Picks the first eligible preview whose slot != `avoid` (pass -1 for the host to take any; the host's slot
 * for the joiner). Sets it active via ChangeActiveSlot(slot).
 *
 * Returns "selected=<slot>" | "no-distinct-freesolo-save" (no eligible save with a distinct slot — co-op
 * needs ≥2) | "previews-not-cached" (menu cache not built yet — caller retries) | "err:..".
 */
export const SELECT_FREESOLO_SLOT = (avoid: number[]): string => `
try {
  var mm = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true);
  if (mm == null) return "err:no-menu";
  var sgm = Il2CppTheGameBakers.Cairn.Save.SavegameManager.Instance;
  if (sgm == null) return "err:no-sgm";
  var avoid = new System.Collections.Generic.HashSet<int>(new int[] { ${avoid.length ? avoid.join(", ") : "-1"} });
  var lists = mm.PreviewsPerChannel;
  if (lists == null) return "previews-not-cached";
  var story = lists[(int)Il2CppTheGameBakers.Cairn.Save.SaveType.Story];
  if (story == null || story.Count == 0) return "previews-not-cached";
  for (int i = 0; i < story.Count; i++) {
    var p = story[i];
    if (!p.Continuable) continue;                 // excludes the real beaten-the-game save (Continuable=false)
    if ((int)p.gameSetup.Constraints != 2561) continue;  // free-solo nonce preset (excludes the gym/other)
    if (avoid.Contains((int)p.slot)) continue;     // skip every slot already taken by another instance
    sgm.ChangeActiveSlot(p.slot);
    return "selected=" + p.slot;
  }
  return "no-distinct-freesolo-save";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/**
 * STEP 1 (PRIMARY): drive straight from the MainMenu into gameplay by CONTINUING the latest save — no menu
 * navigation, no input simulation. This is the proven jump (memory cairn-jump-to-gameplay, verified live):
 *
 *     mm.mustContinueSavegame = true;
 *     mm.TransitionToContinueIfAvailable();   // MainMenu → CommonBaseScene (~12s), pawn spawns
 *
 * Both instances have a story save from prior climbs, so Continue lands them on the wall. (The old NEW-GAME
 * path — MainMenuLaunchGameElement.LaunchStoryMode() after populating nextGameStartOptions — required the
 * menu to be sitting at Step.LaunchNewStoryGame; from a fresh boot it parks at the save/mode-select screen
 * and never bootstraps, so we do NOT use it.) Idempotent: re-firing once already in gameplay is harmless.
 *
 * Dead ends, VERIFIED LIVE — do not revisit:
 *   - CairnSceneManager.LoadZone from a cold menu PARKS forever (uniqueToken==null Addressables wait).
 *   - CairnSceneManager.TravelToZone loads the scene but never bootstraps PawnManager (climber never spawns).
 *   - LaunchStoryMode()/Open(LaunchNewStoryGame) from a no-save boot at the save-select screen: no-op (it
 *     just sits at "pick a save"). Continuing the save is the reliable jump.
 */
export const START_FREESOLO = `
try {
  var mm = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true);
  if (mm == null) return "no-menu (already in gameplay?)";
  mm.mustContinueSavegame = true;
  mm.TransitionToContinueIfAvailable();
  return "continue-save launched";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/** STEP 2: ensure the co-op rope is linked. Returns "roped=<bool>" or a not-ready tag. */
/** STEP 2: drive the rope HANDSHAKE one beat (request/accept/disconnect against the closest partner) and
 * report the resulting connection state. The rope is now an accepted-request handshake (not a one-shot
 * toggle): ForceRope() sends a request the first time, and ACCEPTS the partner's incoming request the next
 * time — so calling it on both sides across a couple of rounds completes the link without the diegetic
 * ghost reach. Returns "action=<status> roped=<bool>". (ForceRope is the test entry added to GameDriver.) */
export const FORCE_ROPE = `
try {
  var d = CairnCoop.Core.Instance.Driver;
  if (d == null) return "no driver";
  if (!d.InCoopSession) return "not in session";
  string action = d.RopeConnected ? "already" : d.ForceRope();
  return "action=" + action + " roped=" + d.RopeConnected;
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/** STEP 2 probe: just the rope-connected flag. Returns "roped=<bool>". */
export const ROPE_CONNECTED = `
try {
  var d = CairnCoop.Core.Instance.Driver;
  if (d == null) return "roped=false";
  return "roped=" + d.RopeConnected;
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/** STEP 2 probe: just the co-op session flag. Returns "insession=<bool>". */
export const IN_COOP_SESSION = `
try {
  var d = CairnCoop.Core.Instance.Driver;
  if (d == null) return "insession=false";
  return "insession=" + d.InCoopSession;
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

// STEP 3 (force the death) now lives as standalone .cs verbs under tools/repro/cs/ — crank-stamina-drain.cs
// + detach-limb.cs produce a REAL exhaustion FALL (the in-place TryKillClimber was removed: it killed
// without a fall, which never exercised the rewind scrub-back-up the co-op revive bug depends on). See
// re/systems/climber/stamina-eject-chain.md.

/**
 * STEP 3b: ACCEPT the edelweiss prompt the way the player does — invoke the popup's accept-input handler.
 * The SpecialItemLootPopUp (raised by KillIfNonResurrectable) holds until the player presses accept; that
 * button calls SpecialItemLootPopUp.InputPrompt_OnInputDetected(), which fires the onChose callback →
 * b__0(accepted:true) → (if resurrectionItemTriggersAutoRewind && CanRewind) StartRewindIfPossible auto path,
 * else the in-place HP-restore resurrection.
 *
 * CRITICAL GATE (decomp dump/decomp-popup/SpecialItemLootPopUp_InputPrompt_OnInputDetected.c): the handler
 * is a NO-OP unless popupFadedIn (0xB8) AND acceptInput (0xB9) are both set — they go true only after the
 * fade-in animation completes (~a beat after the popup opens). Calling accept too early silently does
 * nothing, which left both climbers stuck at the prompt. So we (a) read the two gate bytes, (b) only invoke
 * the handler once both are set. The interop wrapper doesn't expose those private bools, so read them by
 * offset via Marshal.ReadByte.
 *
 * Returns: "no-popup" | "not-ready(faded=N accept=N)" (gate not yet satisfied — caller should retry) |
 *          "accepted".  VERIFIED LIVE: once the gate is satisfied, invoking the handler revives both
 *          climbers (hp restored, module back on the wall).
 */
export const ACCEPT_PROMPT = `
try {
  var pop = UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.SpecialItemLootPopUp>(true);
  if (pop == null) return "no-popup";
  long p = (long)pop.Pointer;
  byte faded = System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)(p + 0xB8));
  byte accept = System.Runtime.InteropServices.Marshal.ReadByte((System.IntPtr)(p + 0xB9));
  if (faded == 0 || accept == 0) return "not-ready(faded=" + faded + " accept=" + accept + ")";
  var m = pop.GetType().GetMethod("InputPrompt_OnInputDetected",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
  if (m == null) return "err:no accept handler";
  m.Invoke(pop, null);
  return "accepted";
} catch (System.Exception e) { return "err:" + e.Message; }
`.trim();

/**
 * STEP 4 GATE + DIAGNOSTIC: the revive lifecycle signal AND the co-op rewind-refusal diagnostic.
 *
 * The edelweiss revive succeeds ~80% of the time for both climbers; the ~20% ground-recoveries are the
 * deaths where the rewind is REFUSED and the game falls through to the checkpoint/ground respawn. CanRewind
 * (decomp dump/decomp-accept/RewindController_CanRewind_2.c) refuses with reason Netplay(1) when, AT THE
 * DEATH FRAME, NetplayManager is alive AND IsInRoom (= clientState > 4). NOTE (live): native
 * NetplayManager.IsInRoom can read False even while the MOD considers the pair in-room (mod-InRoom ≠ native
 * [NET]inRoom), which is consistent with the rewind usually being ALLOWED. This signal logs CanRewind's
 * reason + IsInRoom at every sample so the loop captures which gate the failing 20% hit AT THE PROMPT —
 * that IS the ground-recovery cause, captured live.
 *
 * Returns: "isAuto=<b> inRez=<b> dead=<b> module=<n> inRoom=<b> canRewind=<b> reason=<enum>"
 *   isAuto  — IsAutoRewinding (inRewind && automaticRewind): the death-driven auto-rewind is active.
 *   inRez   — GameDataManager.InResurrection (the resurrection sequence/prompt is active). PROMPT-UP signal.
 *   dead    — GameDataManager.IsDead.
 *   inRoom  — native NetplayManager.IsInRoom (the Netplay gate's input).
 *   canRewind/reason — CanRewind(bypassAssist:true) verdict + RefusedRewindReason at this instant.
 *   Revive RESOLVED when isAuto==false && inRez==false.
 */
export const REVIVE_SIGNAL = `
try {
  var pc = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance?.ClimbingPawnController;
  if (pc == null) return "isAuto=false inRez=false dead=? module=none inRoom=? clientState=? canRewind=? reason=NoPawn";
  var rw = pc.PawnControllerSwitcher != null ? pc.PawnControllerSwitcher.Rewind : null;
  var g = Il2Cpp.GameDataManager.Instance;
  var nm = Il2CppTheGameBakers.Cairn.Netplay.NetplayManager.Instance;
  bool isAuto = rw != null && rw.IsAutoRewinding;
  bool inRez = g != null && g.InResurrection;
  bool dead = g != null && g.IsDead;
  string hp = g != null ? g.CurrentHp.ToString("F0") : "?";
  string module = pc.CurrentModule != null ? pc.CurrentModule.type.ToString() : "none";
  string inRoom = nm != null ? nm.IsInRoom.ToString() : "?";
  string canR = "?"; string reason = "?";
  if (rw != null) {
    try {
      Il2Cpp.RewindController.RefusedRewindReason rr;
      bool can = rw.CanRewind(out rr, true);   // bypassAssistMode=true (only bypasses the assist gate, NOT Netplay)
      canR = can.ToString(); reason = rr.ToString();
    } catch (System.Exception ce) { reason = "canRewindErr(" + ce.Message + ")"; }
  }
  return "isAuto=" + isAuto + " inRez=" + inRez + " dead=" + dead + " hp=" + hp + " module=" + module
    + " inRoom=" + inRoom + " canRewind=" + canR + " reason=" + reason;
} catch (System.Exception e) { return "isAuto=err(" + e.Message + ")"; }
`.trim();
