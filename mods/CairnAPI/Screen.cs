using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppCairn.UI;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;

namespace CairnAPI;

// What screen Cairn is currently displaying, and events when screens change.
//
// Two axes:
//  1. Macro state — GlobalGameManager.GameState (Menu/InGame/Cutscene/GameOver/…)
//  2. UI stack   — UIManager.CurrentMenu / the active Menu canvas
//
// Rules:
//  - All properties are safe to poll from any MelonMod.OnUpdate(); they return defaults when
//    the singletons are not yet available (pre-initialization / mid-transition).
//  - GameState is a STACK; IsInGame etc. check whether the value is ANYWHERE in the stack.
//  - LoadingState.GameStarted is sticky across menu returns; use PawnSpawned for real gameplay.
//  - Events fire on the MelonLoader main thread; subscribers must not block.
//
// See: re/systems/ui/screen-navigation.md

public static class Screen
{
    // ── Macro state ───────────────────────────────────────────────────────────

    /// <summary>GlobalGameManager.GameState enum value at the top of the state stack.</summary>
    public static GlobalGameManager.GameState GameState
    {
        get
        {
            var gm = MoSingleton<GlobalGameManager>.Instance;
            return gm != null ? gm.CurrentGameState : GlobalGameManager.GameState.None;
        }
    }

    /// <summary>The main menu system is active (GameState stack contains Menu).</summary>
    public static bool IsMenu
    {
        get
        {
            var gm = MoSingleton<GlobalGameManager>.Instance;
            return gm != null && gm.IsInMenu;
        }
    }

    /// <summary>
    /// Gameplay is active (GameState stack contains InGame). This is true even when
    /// the pause menu or bivouac is on top.
    /// </summary>
    public static bool IsInGame
    {
        get
        {
            var gm = MoSingleton<GlobalGameManager>.Instance;
            return gm != null && gm.IsInGame;
        }
    }

    /// <summary>A cutscene is playing (GameState stack contains Cutscene).</summary>
    public static bool IsCutscene
    {
        get
        {
            var gm = MoSingleton<GlobalGameManager>.Instance;
            return gm != null && gm.IsInCutscene;
        }
    }

    /// <summary>The game-over screen is up (GameState stack contains GameOver).</summary>
    public static bool IsGameOver
    {
        get
        {
            var gm = MoSingleton<GlobalGameManager>.Instance;
            return gm != null && gm.IsGameOver;
        }
    }

    /// <summary>The bivouac rest menu is active (GameState == Bivouac).</summary>
    public static bool IsBivouac
    {
        get
        {
            var gm = MoSingleton<GlobalGameManager>.Instance;
            return gm != null && gm.CurrentGameState == GlobalGameManager.GameState.Bivouac;
        }
    }

    /// <summary>
    /// The pawn (Aava) is spawned in the world — the real "we are in live gameplay" test.
    /// Unlike LoadingState.GameStarted this is not sticky across menu returns.
    /// </summary>
    public static bool PawnSpawned => PawnManager.MCSpawned;

    /// <summary>
    /// The game's detailed load pipeline stage.
    /// ⚠ Sticky: retains its last value (e.g. GameStarted=8) after returning to the menu.
    /// The authoritative "fully in gameplay" signal is PawnSpawned, not GameStarted.
    /// </summary>
    public static GlobalGameManager.LoadingState LoadingState
    {
        get
        {
            var gm = MoSingleton<GlobalGameManager>.Instance;
            return gm != null ? gm.loadingState : GlobalGameManager.LoadingState.Invalid;
        }
    }

    // ── Scene transition state ────────────────────────────────────────────────

    /// <summary>A cross-world / cross-zone / menu scene transition is in flight.</summary>
    public static bool IsTransitioning
    {
        get
        {
            var csm = MoSingleton<CairnSceneManager>.Instance;
            return csm != null && csm.IsTraveling;
        }
    }

    /// <summary>Specifically a game→menu transition is in flight (subset of IsTransitioning).</summary>
    public static bool IsLoadingMenu
    {
        get
        {
            var csm = MoSingleton<CairnSceneManager>.Instance;
            return csm != null && csm.isLoadingMainMenu;
        }
    }

    // ── UI stack ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The foremost open Menu canvas, or null. Changes only at UIManager.OnMenuClosed /
    /// OnMenuStacked — observe via OnMenuChanged if you need a callback.
    /// </summary>
    public static Menu CurrentMenu
    {
        get
        {
            var ui = MoSingleton<UIManager>.Instance;
            return ui != null ? ui.CurrentMenu : null;
        }
    }

    /// <summary>
    /// The current MainMenu.Step, or null when the MainMenu scene is not loaded
    /// (i.e. during gameplay). Cast result to MainMenu.Step.
    /// </summary>
    public static MainMenu.Step? MainMenuStep
    {
        get
        {
            var mm = GameObject.FindObjectOfType<MainMenu>(true);
            if (mm == null) return null;
            return (MainMenu.Step)mm.step;
        }
    }

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires whenever GlobalGameManager.GameState changes. Arguments: (from, to).
    /// Sourced from GameEventManager.ChangeGameState — fires for every push/pop and direct set.
    /// </summary>
    public static event Action<GlobalGameManager.GameState, GlobalGameManager.GameState> OnGameStateChanged;

    /// <summary>
    /// Fires when UIManager.CurrentMenu changes (a menu opens or the foreground menu closes).
    /// Argument is the NEW current menu (null when the stack empties).
    /// </summary>
    public static event Action<Menu> OnMenuChanged;

    /// <summary>
    /// Fires when any BasicCanvasHandlerBehaviour finishes opening (OnOpened).
    /// Fires for menus AND HUDs. Cast the argument to check the concrete type.
    /// </summary>
    public static event Action<BasicCanvasHandlerBehaviour> OnCanvasOpened;

    /// <summary>
    /// Fires when any BasicCanvasHandlerBehaviour finishes closing (OnClosed).
    /// Fires for menus AND HUDs. Cast the argument to check the concrete type.
    /// </summary>
    public static event Action<BasicCanvasHandlerBehaviour> OnCanvasClosed;

    /// <summary>
    /// Fires when a game→menu or menu→game scene transition starts.
    /// Sourced from CairnSceneManager.OnGameUnloadingAboutToStart.
    /// </summary>
    public static event Action OnTransitionStarted;

    /// <summary>
    /// Fires when a scene transition completes and all new scenes are ready.
    /// Sourced from CairnSceneManager.OnGameScenesReady.
    /// </summary>
    public static event Action OnTransitionCompleted;

    /// <summary>
    /// Fires specifically when entering the main menu scene (game→menu path only),
    /// just before the menu scene itself loads. CairnSceneManager.OnPreparingToLoadMenu.
    /// </summary>
    public static event Action OnEnteringMenu;

    // ── Internal wiring (installed by ScreenHooks.Setup) ─────────────────────

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Patch GameEventManager.ChangeGameState to emit OnGameStateChanged.
        harmony.PatchAll(typeof(ScreenHooks));

        // Subscribe to CairnSceneManager Action delegates once the instance is available.
        // CairnSceneManager is a MoSingleton; it exists before any gameplay begins.
        var csm = MoSingleton<CairnSceneManager>.Instance;
        if (csm != null) HookSceneManager(csm);
        else
        {
            // CairnSceneManager may not exist at mod init — defer.
            MelonCoroutines.Start(DeferSceneManagerHook());
        }
    }

    private static System.Collections.IEnumerator DeferSceneManagerHook()
    {
        CairnSceneManager csm = null;
        while (csm == null)
        {
            csm = MoSingleton<CairnSceneManager>.Instance;
            yield return null;
        }
        HookSceneManager(csm);
    }

    private static void HookSceneManager(CairnSceneManager csm)
    {
        csm.OnGameUnloadingAboutToStart += (Action)(() => OnTransitionStarted?.Invoke());
        csm.OnGameScenesReady += (Action)(() => OnTransitionCompleted?.Invoke());
        csm.OnPreparingToLoadMenu += (Action)(() => OnEnteringMenu?.Invoke());
    }

    internal static void RaiseGameStateChanged(
        GlobalGameManager.GameState from, GlobalGameManager.GameState to)
        => OnGameStateChanged?.Invoke(from, to);

    internal static void RaiseMenuChanged(Menu menu)
        => OnMenuChanged?.Invoke(menu);

    internal static void RaiseCanvasOpened(BasicCanvasHandlerBehaviour canvas)
        => OnCanvasOpened?.Invoke(canvas);

    internal static void RaiseCanvasClosed(BasicCanvasHandlerBehaviour canvas)
        => OnCanvasClosed?.Invoke(canvas);
}

// Harmony patches that feed the Screen event surface.
[HarmonyPatch]
internal static class ScreenHooks
{
    // GameEventManager.ChangeGameState(GameState from, GameState to) — the sole channel for
    // GameState change notification. Delegate stored at +0x368 on the singleton; Harmony patches
    // the method directly so we don't have to resolve the vtable offset.
    [HarmonyPatch(typeof(GameEventManager), nameof(GameEventManager.ChangeGameState))]
    [HarmonyPostfix]
    static void OnChangeGameState(GlobalGameManager.GameState from, GlobalGameManager.GameState to)
        => Screen.RaiseGameStateChanged(from, to);

    // UIManager.OnMenuClosed / OnMenuStacked — the only two moments CurrentMenu changes.
    [HarmonyPatch(typeof(UIManager), "OnMenuClosed")]
    [HarmonyPostfix]
    static void OnMenuClosed(UIManager __instance)
        => Screen.RaiseMenuChanged(__instance.CurrentMenu);

    [HarmonyPatch(typeof(UIManager), "OnMenuStacked")]
    [HarmonyPostfix]
    static void OnMenuStacked(UIManager __instance)
        => Screen.RaiseMenuChanged(__instance.CurrentMenu);

    // BasicCanvasHandlerBehaviour.OnOpened / OnClosed — global canvas lifecycle events.
    [HarmonyPatch(typeof(BasicCanvasHandlerBehaviour), "OnOpened")]
    [HarmonyPostfix]
    static void OnCanvasOpened(BasicCanvasHandlerBehaviour __instance)
        => Screen.RaiseCanvasOpened(__instance);

    [HarmonyPatch(typeof(BasicCanvasHandlerBehaviour), "OnClosed")]
    [HarmonyPostfix]
    static void OnCanvasClosed(BasicCanvasHandlerBehaviour __instance)
        => Screen.RaiseCanvasClosed(__instance);
}
