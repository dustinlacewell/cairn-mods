using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CairnNoCutscenes.Core), "CairnNoCutscenes", "1.0.0", "dustin")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnNoCutscenes;

// Removes cutscenes by driving the game's own skip pipeline:
// CutsceneStoryEvent.OnUpdate consumes Cutscene.RequestSkip each frame —
// it first jumps to any pending interaction loop (so required prompts/choices
// still happen), and only completes the skip when none remain, applying all
// end-of-cutscene state (teleports, items, saves, time-of-day) exactly as a
// player hold-to-skip would.
public class Core : MelonMod
{
    internal static MelonPreferences_Entry<bool> SkipCutscenes;
    internal static MelonPreferences_Entry<bool> SkipVideos;
    internal static MelonPreferences_Entry<bool> RemoveFades;
    internal static MelonPreferences_Entry<bool> SkipRadioCalls;
    internal static MelonPreferences_Entry<bool> SkipOpeningTitles;
    internal static MelonPreferences_Entry<bool> SkipTitleScreen;
    internal static MelonPreferences_Entry<bool> FixStuckBlackScreen;

    // True while a DialogStoryEvent (radio call / robot message) is playing its Ink story.
    internal static bool FastForwardDialog;

    private string lastSkipLogged;

    // Only CutsceneStoryEvent.OnUpdate consumes RequestSkip. Cutscenes played
    // outside a story event (e.g. the first-wall title sequence) never have the
    // flag read, so after a grace period we call Cutscene.Skip() ourselves. With
    // fades left enabled, a story event consuming the flag can spend seconds
    // fading before the cutscene stops — wait longer so we don't double-skip.
    private static float UnconsumedSkipGrace => RemoveFades.Value ? 1f : 4f;
    private Cutscene pendingSkip;
    private float pendingSkipSince;

    private float blackScreenCheckAt;
    private float blackScreenStuckSince = -1f;

    private MainMenu mainMenu;
    private MainMenu.Step? titleStepLogged;

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("CairnNoCutscenes");
        SkipCutscenes = cat.CreateEntry("SkipCutscenes", true,
            description: "Auto-skip story cutscenes the moment they start playing.");
        SkipVideos = cat.CreateEntry("SkipVideos", true,
            description: "Auto-skip full-screen videos (logos/movies).");
        RemoveFades = cat.CreateEntry("RemoveFades", true,
            description: "Zero the fade-to-black durations around cutscenes so skips are instant.");
        SkipRadioCalls = cat.CreateEntry("SkipRadioCalls", true,
            description: "Fast-forward radio calls / robot messages (every Ink line still executes, so story state stays intact).");
        SkipOpeningTitles = cat.CreateEntry("SkipOpeningTitles", true,
            description: "Suppress the opening-titles credits sequence at the top of the first wall (logo, credits, music swell, time warp).");
        SkipTitleScreen = cat.CreateEntry("SkipTitleScreen", true,
            description: "Skip the title-screen camera fly-in and the 'press any key' gate — boot straight to the main menu.");
        FixStuckBlackScreen = cat.CreateEntry("FixStuckBlackScreen", true,
            description: "Hide the fullscreen fade overlay when a skipped cutscene orphans it (black screen with the game running behind).");
    }

    public override void OnUpdate()
    {
        if (SkipCutscenes.Value) RequestSkipIfNeeded();
        if (SkipVideos.Value) SkipVideoIfPlaying();
        if (SkipTitleScreen.Value) SkipTitleScreenIfNeeded();
        if (FastForwardDialog) ClearDialogFastForwardWhenDone();
        if (FixStuckBlackScreen.Value) HideStuckBlackScreenIfNeeded();
    }

    // On load the scene manager shows the fullscreen BlackScreen overlay and the FIRST
    // cutscene is responsible for hiding it (firstCutsceneShouldHandleBlackScreen) —
    // auto-skipping that cutscene can orphan the overlay: the world runs fine behind an
    // alpha-1 black quad. Watchdog: fully-shown BlackScreen with no cutscene/video
    // playing and no loading/menu scene up, stable for 5 s -> hide it.
    private void HideStuckBlackScreenIfNeeded()
    {
        if (Time.unscaledTime < blackScreenCheckAt)
            return;
        blackScreenCheckAt = Time.unscaledTime + 1f;

        var cutscene = Cutscene.CurrentCutscene;
        if ((cutscene != null && cutscene.IsPlaying) || MoviePlayerManager.IsPlaying || InLoadingOrMenu())
        {
            blackScreenStuckSince = -1f;
            return;
        }

        bool fullyBlack = false;
        var overlays = Object.FindObjectsOfType<BlackScreen>();
        foreach (var overlay in overlays)
        {
            if (overlay.GetCurrAlpha() > 0.99f && overlay.GetTargetAlpha() > 0.99f)
            {
                fullyBlack = true;
                break;
            }
        }
        if (!fullyBlack)
        {
            blackScreenStuckSince = -1f;
            return;
        }

        if (blackScreenStuckSince < 0f)
        {
            blackScreenStuckSince = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - blackScreenStuckSince < 5f)
            return;

        blackScreenStuckSince = -1f;
        MelonLogger.Msg("Hiding orphaned BlackScreen overlay (skipped cutscene never faded back in)");
        foreach (var overlay in overlays)
            if (overlay.GetCurrAlpha() > 0.99f)
                overlay.Hide(BlackScreen.FadeType.Simple, 0.25f, "CairnNoCutscenes", true);
    }

    // IL2CPP inlined the call sites that matter here (EnableCameraTravelling's
    // body is duplicated inside Activate, and the boot flow reaches PressStart
    // without a non-inlined TransitionToStep call), so Harmony detours never see
    // the title sequence. Instead, while a MainMenu scene is up, stop the
    // diorama's spline-cart camera travelling the moment it starts and drive the
    // menu out of the Disclaimer ("best played with a controller") and
    // PressStart steps ourselves — managed-to-IL2CPP calls always take effect.
    private void SkipTitleScreenIfNeeded()
    {
        if (!InMainMenuScene())
        {
            mainMenu = null;
            titleStepLogged = null;
            return;
        }

        var diorama = MainMenuBackgroundDiorama.Current;
        if (diorama != null && diorama.IsPlayingCameraTravelling())
        {
            MelonLogger.Msg("Stopping title-screen camera travelling");
            diorama.EnableCameraTravelling(false);
        }

        if (mainMenu == null)
            mainMenu = Object.FindObjectOfType<MainMenu>();
        if (mainMenu == null)
            return;

        var step = mainMenu.step;
        if (step != MainMenu.Step.PressStart && step != MainMenu.Step.Disclaimer)
        {
            titleStepLogged = null;
            return;
        }
        if (titleStepLogged != step)
        {
            titleStepLogged = step;
            MelonLogger.Msg($"Skipping title step '{step}'");
        }
        // No-ops while the menu is busy (animating / downloading save previews);
        // step still reads PressStart/Disclaimer then, so we retry next frame.
        mainMenu.TransitionToStep(MainMenu.Step.ModeSelect);
    }

    private static bool InMainMenuScene()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name.Contains("MainMenu"))
                return true;
        return false;
    }

    private static bool InLoadingOrMenu()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            string name = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name;
            if (name.Contains("Loading") || name.Contains("MainMenu"))
                return true;
        }
        return false;
    }

    private static void ClearDialogFastForwardWhenDone()
    {
        var dialogManager = DialogManager.Instance;
        if (dialogManager == null || !dialogManager.PlayingStory)
            FastForwardDialog = false;
    }

    private void RequestSkipIfNeeded()
    {
        var cutscene = Cutscene.CurrentCutscene;
        if (cutscene == null || !cutscene.IsPlaying)
            return;
        // Looping/bivouac cutscenes are ambient gameplay (camp, cooking), not story scenes.
        if (cutscene.loop || cutscene.isBivouacCutscene)
            return;
        // While an interaction prompt is live, let the player act; re-arm right after.
        var interaction = cutscene.InteractionHandler;
        if (interaction != null && interaction.InInteraction)
        {
            pendingSkip = null;
            return;
        }

        if (!cutscene.RequestSkip)
        {
            cutscene.SetRequestSkip(true);
            pendingSkip = cutscene;
            pendingSkipSince = Time.unscaledTime;
            if (lastSkipLogged != cutscene.name)
            {
                lastSkipLogged = cutscene.name;
                MelonLogger.Msg($"Skipping cutscene '{cutscene.name}'");
            }
            return;
        }

        if (pendingSkip != null && cutscene == pendingSkip
            && Time.unscaledTime - pendingSkipSince > UnconsumedSkipGrace)
        {
            pendingSkip = null;
            MelonLogger.Msg($"Force-skipping non-story cutscene '{cutscene.name}'");
            cutscene.Skip();
        }
    }

    private static void SkipVideoIfPlaying()
    {
        if (!MoviePlayerManager.IsPlaying)
            return;
        var manager = MoviePlayerManager.Instance;
        if (manager != null)
        {
            MelonLogger.Msg("Skipping video");
            manager.Skip();
        }
    }
}

// Radio calls / robot messages are DialogStoryEvents driving the Ink DialogManager.
// DialogManager.Update advances one line per frame whenever the HUD's CanPlayNext()
// is true, and hard-blocks on player choices (makingAChoice) and scripted waits
// (preventContinue) — so forcing CanPlayNext fast-forwards the call line by line,
// executing every Ink command/variable, without breaking story state.
[HarmonyPatch(typeof(DialogStoryEvent), nameof(DialogStoryEvent.StartDialog))]
internal static class DialogStoryEventPatch
{
    private static void Postfix(DialogStoryEvent __instance)
    {
        if (!Core.SkipRadioCalls.Value)
            return;
        Core.FastForwardDialog = true;
        MelonLogger.Msg($"Fast-forwarding radio dialog '{__instance.dialogPath?.value}'");
    }
}

[HarmonyPatch(typeof(DialogHud), nameof(DialogHud.CanPlayNext))]
internal static class DialogHudPatch
{
    private static void Postfix(ref bool __result)
    {
        if (Core.FastForwardDialog && Core.SkipRadioCalls.Value)
            __result = true;
    }
}

// The opening-titles moment (Cairn logo + scrolling credits + music swell + time
// warp at the top of the first wall) is not a Cutscene: a CreditsTrigger world
// trigger sets GameDataManager.inCreditsSequence, and CoreCreditsHud /
// CoreCreditsTimeScaler / music all key off that flag. The HUD itself clears the
// flag when the last credit fades, so never raising it leaves nothing waiting.
[HarmonyPatch(typeof(CreditsTrigger), "OnTriggerEntered")]
internal static class CreditsTriggerPatch
{
    private static bool Prefix() => !Core.SkipOpeningTitles.Value;
}

// Backstop for any other caller (e.g. the debug-menu toggle): allow clearing,
// block raising.
[HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.SetInCreditsSequence))]
internal static class GameDataManagerPatch
{
    private static bool Prefix(bool set) => !(set && Core.SkipOpeningTitles.Value);
}

// Best-effort companion to Core.SkipTitleScreenIfNeeded: when a step change
// does go through a non-inlined TransitionToStep call (e.g. the
// ForceStepTransition path in MainMenu.Update), redirect title steps to
// ModeSelect before they ever open, with no one-frame flash.
[HarmonyPatch(typeof(MainMenu), nameof(MainMenu.TransitionToStep))]
internal static class MainMenuTransitionPatch
{
    private static void Prefix(ref MainMenu.Step step)
    {
        if (!Core.SkipTitleScreen.Value)
            return;
        if (step == MainMenu.Step.PressStart || step == MainMenu.Step.Disclaimer)
            step = MainMenu.Step.ModeSelect;
    }
}

// skipAllowed only gates the hold-to-skip HUD, not the RequestSkip consumer —
// forcing it on makes every cutscene manually skippable even with auto-skip off.
[HarmonyPatch(typeof(CutsceneStoryEvent), nameof(CutsceneStoryEvent.OnTrigger))]
internal static class CutsceneStoryEventPatch
{
    private static void Prefix(CutsceneStoryEvent __instance)
    {
        __instance.skipAllowed = true;
        if (!Core.RemoveFades.Value)
            return;
        __instance.fadeFromGameplayToBlackDuration = 0f;
        __instance.fadeFromBlackToCutsceneDuration = 0f;
        __instance.fadeFromCutsceneToBlackDuration = 0f;
        __instance.fadeFromBlackToGameplayDuration = 0f;
    }
}
