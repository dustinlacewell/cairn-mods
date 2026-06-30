using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CairnNoCutscenes.Core), "CairnNoCutscenes", "0.1.2", "ldlework")]
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
    internal static MelonPreferences_Entry<bool> SkipDreamSequence;

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

    private MainMenu mainMenu;
    private MainMenu.Step? titleStepLogged;

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("CairnNoCutscenes");
        SkipCutscenes = cat.CreateEntry("SkipCutscenes", true,
            description: "Skip story cutscenes.");
        SkipVideos = cat.CreateEntry("SkipVideos", true,
            description: "Skip intro videos and studio logos.");
        RemoveFades = cat.CreateEntry("RemoveFades", true,
            description: "Make transitions instant — no fade to black.");
        SkipRadioCalls = cat.CreateEntry("SkipRadioCalls", true,
            description: "Fast-forward radio calls and robot messages.");
        SkipOpeningTitles = cat.CreateEntry("SkipOpeningTitles", true,
            description: "Skip the opening title sequence on the first wall.");
        SkipTitleScreen = cat.CreateEntry("SkipTitleScreen", true,
            description: "Skip the title screen and go straight to the main menu.");
        SkipDreamSequence = cat.CreateEntry("SkipDreamSequence", true,
            description: "Skip the nightmare/dream sequence.");

        RegisterModOptions();
    }

    private static void RegisterModOptions()
    {
        CairnModOptions.ModOptions.Register("CairnNoCutscenes", new[]
        {
            CairnModOptions.ModOption.Toggle("Skip cutscenes", SkipCutscenes,
                tooltip: "Skip story cutscenes."),

            CairnModOptions.ModOption.Toggle("Skip videos", SkipVideos,
                tooltip: "Skip intro videos and studio logos."),

            CairnModOptions.ModOption.Toggle("Remove fades", RemoveFades,
                tooltip: "Make transitions instant — no fade to black."),

            CairnModOptions.ModOption.Toggle("Skip radio calls", SkipRadioCalls,
                tooltip: "Fast-forward radio calls and robot messages."),

            CairnModOptions.ModOption.Toggle("Skip opening titles", SkipOpeningTitles,
                tooltip: "Skip the opening title sequence on the first wall."),

            CairnModOptions.ModOption.Toggle("Skip title screen", SkipTitleScreen,
                tooltip: "Skip the title screen and go straight to the main menu."),

            CairnModOptions.ModOption.Toggle("Skip nightmare/dream sequence", SkipDreamSequence,
                tooltip: "Skip the nightmare/dream sequence."),
        });
    }

    public override void OnUpdate()
    {
        if (SkipCutscenes.Value) RequestSkipIfNeeded();
        if (SkipVideos.Value) SkipVideoIfPlaying();
        if (SkipTitleScreen.Value) SkipTitleScreenIfNeeded();
        if (FastForwardDialog) ClearDialogFastForwardWhenDone();
        if (SkipDreamSequence.Value) SkipDreamIfNeeded();
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

    // The nightmare/dream is driven by DreamSequenceManager. Its OnDisable is
    // byte-identical to the private Clear — the full safe teardown that unwinds
    // weather force / culling mask / climbot display / sprite speed and fires
    // OnExit to re-enable hidden renderers. So the surgical skip is to disable
    // the manager GameObject, which triggers OnDisable == Clear. The call is
    // idempotent: after disable, the singleton clears, so IsPlaying is false.
    private static void SkipDreamIfNeeded()
    {
        if (!DreamSequenceManager.IsPlaying)
            return;
        MelonLogger.Msg("Skipping nightmare/dream sequence");
        DreamSequenceManager.Instance.gameObject.SetActive(false);
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
//
// One CreditsTrigger type and one inCreditsSequence flag serve BOTH the opening
// titles (top of the first wall) AND the in-world end credits (post-summit stars
// climb), so suppressing them unconditionally would also blank the legitimate end
// credits. The discriminator is GameDataManager.InEndGame: it is false through the
// whole opening (isInEndGame starts false and is only raised when the end-game /
// summit transition cutscene STOPS, in CutsceneStoryEvent.OnCutsceneStop), and
// true throughout the post-summit segment that fires the end credits. So we only
// suppress while NOT in the end game.
internal static class OpeningTitles
{
    // Suppress the credits machinery only for the opening occurrence: option on
    // AND we are not in the end-game / summit-end segment. Fail-open (do not
    // suppress) if the singleton isn't ready, so we never block the end credits.
    internal static bool ShouldSuppress()
    {
        if (!Core.SkipOpeningTitles.Value)
            return false;
        try
        {
            if (!GameDataManager.IsReady)
                return false;
            return !GameDataManager.Instance.InEndGame;
        }
        catch { return false; }
    }
}

[HarmonyPatch(typeof(CreditsTrigger), "OnTriggerEntered")]
internal static class CreditsTriggerPatch
{
    private static bool Prefix() => !OpeningTitles.ShouldSuppress();
}

// Backstop for any other caller (e.g. the debug-menu toggle): allow clearing,
// block raising — but only during the opening, never during the end credits.
[HarmonyPatch(typeof(GameDataManager), nameof(GameDataManager.SetInCreditsSequence))]
internal static class GameDataManagerPatch
{
    private static bool Prefix(bool set) => !(set && OpeningTitles.ShouldSuppress());
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

// Hide an orphaned black screen at the exact moment a cutscene ends — no scanning.
//
// CutsceneStoryEvent.OnCutsceneStop is the game's own end-of-cutscene handler and it
// owns the black-screen hide, but only on the path where the final fade has a non-zero
// duration: it then Shows+Hides the overlay. On a skip with that duration zeroed it
// instead only registers a Hide-Done callback and never calls Hide() — and its lone
// rescue branch fires only when the completion action is DoNothing and no fade is mid-
// flight. RemoveFades zeroes that very duration (CutsceneStoryEventPatch), so a skipped
// cutscene whose completion action is GoToKami/RecapPath/RollEndCredits leaves the
// overlay fully black with the world running behind it.
//
// OnCutsceneStop runs for both a natural end and a skip (it is the Cutscene.OnStop
// callback Cutscene.Skip() fires), so a postfix here is the one place that always sees
// a cutscene finish. We reach the single shared overlay directly via GlobalUIs — the
// game's own singleton, the same one OnCutsceneStop dereferences — instead of scanning
// the scene. We only hide when the screen is left settled-open (a fade already running
// will resolve on its own; hiding mid-fade would be rejected by BlackScreen's
// re-entrancy guard anyway), mirroring the game's own !IsInProgress rescue condition.
[HarmonyPatch(typeof(CutsceneStoryEvent), nameof(CutsceneStoryEvent.OnCutsceneStop))]
internal static class CutsceneStoryEventStopPatch
{
    private static void Postfix()
    {
        if (!Core.SkipCutscenes.Value && !Core.RemoveFades.Value)
            return;

        var blackScreen = MoSingleton<GlobalUIs>.Instance?.blackScreen;
        if (blackScreen == null || blackScreen.IsInProgress() || !blackScreen.IsOpen)
            return;

        MelonLogger.Msg("Hiding orphaned black screen after skipped cutscene");
        blackScreen.Hide(BlackScreen.FadeType.Simple, 0.25f, "CairnNoCutscenes", true);
    }
}
