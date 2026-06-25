using Il2Cpp;
using UnityEngine.SceneManagement;
using CairnUI = Il2CppTheGameBakers.Cairn.UI;

namespace CairnDevTools;

/// <summary>
/// Emits game-lifecycle EventBus signals so external drivers can <c>wait-event</c> on readiness instead of
/// sleep-polling state. Edge-triggered from the console Tick (one emit per transition):
///   - <c>menu-ready</c>    — a MainMenu exists in the scene (the continue-jump can fire).
///   - <c>gameplay-ready</c>— the climber pawn has spawned (PawnManager.MCSpawned true).
///   - <c>scene-changed</c> — the active scene name changed (payload = new scene).
/// This is the shared, durable home for "is the game ready" — previously re-derived as ad-hoc poll loops in
/// the repro driver. The driver now just awaits these events.
/// </summary>
internal static class Lifecycle
{
    private static bool _menuReady;
    private static bool _gameplayReady;
    private static string _scene = "";

    internal static void Tick()
    {
        TrackScene();
        TrackMenu();
        TrackGameplay();
    }

    private static void TrackScene()
    {
        var name = SceneManager.GetActiveScene().name ?? "";
        if (name == _scene) return;
        _scene = name;
        EventBus.Emit("scene-changed", name);
    }

    private static void TrackMenu()
    {
        bool present = UnityEngine.Object.FindObjectOfType<CairnUI.MainMenu>(true) != null;
        if (present && !_menuReady) { _menuReady = true; EventBus.Emit("menu-ready"); }
        else if (!present && _menuReady) { _menuReady = false; }
    }

    private static void TrackGameplay()
    {
        bool live = PawnManager.MCSpawned;
        if (live && !_gameplayReady) { _gameplayReady = true; EventBus.Emit("gameplay-ready"); }
        else if (!live && _gameplayReady) { _gameplayReady = false; }
    }
}
