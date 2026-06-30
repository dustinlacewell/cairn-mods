using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine.InputSystem;
using TGB = Il2CppTheGameBakers.Cairn;
using CairnUI = Il2CppTheGameBakers.Cairn.UI;

[assembly: MelonInfo(typeof(CairnFreeRoam.Core), "CairnFreeRoam", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnFreeRoam;

// Unlock the game's own eagle-eye fast-travel ("Free Roam teleport") in ANY save — campaign, free-solo,
// anything — without being on a Free Roam difficulty.
//
// The entire fast-travel feature (the warp list, world pins, eagle-eye camera POV, and the warp itself) is
// shipped and fully functional; it is gated behind exactly one flag, GameSetup.CanWarp
// (GamemodeConstraints.CanWarp = 1024). That flag is checked in ONE place that matters —
// EagleEyeUI.ToggleFreeRoamTeleport_OnInputDetected refuses to open the view unless CanWarp — and NOWHERE on
// the warp execution path (adversarially verified; see re/systems/world-streaming/freeroam-discovery-and-canwarp.md
// and freeroam-warp.md). So forcing get_CanWarp → true is the whole unlock: the existing input handler then
// opens the eagle-eye view and the player fast-travels to the warp points the game has registered.
//
// We do NOT mutate the difficulty's constraint asset (a shared ScriptableObject); we postfix the getter, which
// is a pure UI unlock — reversible via the preference toggle, no game data touched.
//
// A second patch (RevealPinsPatch) forces FreeRoamWarpPoint.IsKnown → true so EVERY warp point shows as a
// reachable destination, not just the bivouacs you've already rested at — again a pure selection unlock, no
// save state touched.
public class Core : MelonMod
{
    internal static MelonPreferences_Entry<bool> Enabled;
    internal static MelonPreferences_Entry<bool> RevealAllPins;
    internal static MelonPreferences_Entry<Key> AddBookmarkKey;
    internal static MelonPreferences_Entry<Key> DeleteBookmarkKey;
    internal static MelonPreferences_Entry<Key> RenameBookmarkKey;

    // Reachable from the static eagle-eye Harmony patches below (they hand it the captured refs / row binds).
    internal static BookmarkController Bookmarks;

    private BookmarkStore _store;
    private bool _registered;

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("CairnFreeRoam");
        Enabled = cat.CreateEntry("Enabled", true,
            description: "Unlock the eagle-eye fast-travel teleport in any save.");
        RevealAllPins = cat.CreateEntry("RevealAllPins", false,
            description: "Reveal every destination, including bivouacs you haven't discovered yet. Off by default so it doesn't spoil places you haven't found.");
        AddBookmarkKey = cat.CreateEntry("AddBookmarkKey", Key.B,
            description: "In the eagle-eye view, bookmark the current position as a new warp point.");
        DeleteBookmarkKey = cat.CreateEntry("DeleteBookmarkKey", Key.Delete,
            description: "In the eagle-eye view, delete the selected custom bookmark.");
        RenameBookmarkKey = cat.CreateEntry("RenameBookmarkKey", Key.R,
            description: "In the eagle-eye view, rename the selected custom bookmark (Enter confirms, Esc cancels).");

        _store = new BookmarkStore();
        Bookmarks = new BookmarkController(_store, AddBookmarkKey, DeleteBookmarkKey, RenameBookmarkKey);
        RegisterModOptions();
        LoggerInstance.Msg($"CairnFreeRoam loaded — eagle-eye unlocked, {_store.Bookmarks.Count} bookmark(s).");
    }

    // Surface the two behaviour toggles in the in-game Mods settings page (both bound live to their prefs).
    private static void RegisterModOptions()
    {
        CairnModOptions.ModOptions.Register("CairnFreeRoam", new[]
        {
            CairnModOptions.ModOption.Toggle("Enable fast-travel", Enabled,
                tooltip: "Unlock the eagle-eye fast-travel teleport in any save."),

            CairnModOptions.ModOption.Toggle("Reveal all destinations", RevealAllPins,
                tooltip: "Show every destination, including bivouacs you haven't discovered yet. Off keeps unfound places hidden so they aren't spoiled."),
        });
    }

    public override void OnUpdate()
    {
        // Bookmarks are FreeRoamWarpPoints; register them once the pawn is in and a FreeRoamManager exists, and
        // on gameplay teardown explicitly unregister + destroy them so the manager never keeps a dangling entry
        // (the GOs are inactive, so their OnDestroy never self-cleans when a scene unload kills them). We then
        // re-register fresh on the next gameplay load.
        bool inGame = Il2Cpp.PawnManager.MCSpawned
                      && Il2Cpp.MoSingleton<TGB.FreeRoamManager>.Instance != null;
        if (inGame && !_registered) { _store.RegisterAll(); _registered = true; }
        else if (!inGame && _registered) { _registered = false; _store.UnregisterAll(); Bookmarks.Teardown(); }

        // The view-open/close, ref-capture, and label-stamp are all Harmony-driven (no per-frame scene work).
        // The only per-frame cost is this gated input read, and it early-returns the instant the view is closed.
        if (Bookmarks.ViewOpen) Bookmarks.TickInput();
    }
}

// Force GameSetup.CanWarp true so EagleEyeUI opens its fast-travel view regardless of difficulty.
[HarmonyPatch(typeof(TGB.GameSetup), nameof(TGB.GameSetup.CanWarp), MethodType.Getter)]
internal static class CanWarpPatch
{
    private static void Postfix(ref bool __result)
    {
        if (Core.Enabled != null && Core.Enabled.Value)
            __result = true;
    }
}

// Reveal every warp point. Vanilla, a bivouac point is "known" only after you've rested at it
// (FreeRoamWarpPoint.IsKnown → InteractionProvidersManager.GetInteractionCount(bivouac) > 0); the eagle-eye
// list greys out, skips, and refuses to warp to unknown points. Forcing IsKnown → true surfaces all of them
// as selectable destinations. Pure UI/selection unlock — no save state touched, so no bivouac counts are
// mutated and nothing persists; reversible via the RevealAllPins toggle.
[HarmonyPatch(typeof(TGB.FreeRoamWarpPoint), nameof(TGB.FreeRoamWarpPoint.IsKnown))]
internal static class RevealPinsPatch
{
    private static void Postfix(ref bool __result)
    {
        if (Core.RevealAllPins != null && Core.RevealAllPins.Value)
            __result = true;
    }
}

// ── Event-driven bookmark UI (replaces the old per-frame FindObjectOfType scans) ────────────────────────────
//
// The warp list opens/closes through the UIManager content lifecycle — OnContentActivated / OnContentDeactivated
// on FreeRoamEagleEyeWarpPointListUI (the RB "Choose a destination" path runs through these, NOT through
// EagleEyeUI.EnterFreeRoamDisplayWarps — trace-proven). The postfix hands us the list (__instance); the
// controller resolves the EagleEyeUI once on open for the prompt line. While closed it holds no refs and does
// zero per-frame work.

// View opened: the warp list's content was activated. __instance is the list. Capture it and show the prompts.
[HarmonyPatch(typeof(CairnUI.FreeRoamEagleEyeWarpPointListUI), nameof(CairnUI.FreeRoamEagleEyeWarpPointListUI.OnContentActivated))]
internal static class ListContentActivatedPatch
{
    private static void Postfix(CairnUI.FreeRoamEagleEyeWarpPointListUI __instance)
    {
        if (Core.Enabled != null && Core.Enabled.Value)
            Core.Bookmarks?.OnViewOpened(__instance);
    }
}

// View closed: the warp list's content was deactivated. Dispose the prompts, restore input, drop refs.
[HarmonyPatch(typeof(CairnUI.FreeRoamEagleEyeWarpPointListUI), nameof(CairnUI.FreeRoamEagleEyeWarpPointListUI.OnContentDeactivated))]
internal static class ListContentDeactivatedPatch
{
    private static void Postfix()
    {
        Core.Bookmarks?.OnViewClosed();
    }
}
