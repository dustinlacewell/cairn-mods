using System;
using System.Collections.Generic;
using Il2Cpp;                       // InputManager (MoSingleton), PlayerInputActions
using UnityEngine.InputSystem;      // InputAction, InputActionAsset

namespace CairnAPI;

// Where a prompt's button ICON comes from. The game draws glyphs from a real UnityEngine.InputSystem
// InputAction: the InputImageManager maps action.bindings[i].effectivePath -> the device-correct sprite
// (keyboard vs gamepad, auto-swapping). So a glyph is just "an InputAction with a binding". This helper
// hands you one two ways, without you ever touching the Input System:
//
//   Glyph.Action(GameAction.Interact)   -> the player's CURRENT keybind for that game action (rebinds for free)
//   Glyph.Key("g")  /  Glyph.Path("<Gamepad>/buttonSouth")  -> a custom key, your own binding
//
// Verified: a standalone action on a STANDARD control path resolves to the stock glyph; no Enable, no
// game-asset membership needed (re/systems/ui/prompts/input-glyph-system.md).
public static class Glyph
{
    // The handful of game actions a prompt is most likely to echo. Each maps to a real action NAME in the
    // game's input asset; resolving it gives the player's live binding (their rebinds). Bare names (NOT
    // "Map/Action" — the map name is "Walking&Climbing" with an ampersand, which FindAction's "Map/Action"
    // form does not accept; bare names resolve uniquely and are what FindAction wants). LIVE-VERIFIED: every
    // name below resolves against the running asset. Add more as needed.
    private static readonly Dictionary<GameAction, string> ActionPaths = new()
    {
        { GameAction.Interact,  "Interact" },
        { GameAction.SharePath, "SharePath" },
        { GameAction.Confirm,   "Submit" },
        { GameAction.Cancel,    "Cancel" },
        { GameAction.Navigate,  "Navigate" },
        { GameAction.Pause,     "pauseAction" },
        { GameAction.Rest,      "restAction" },
        { GameAction.Rappel,    "Rappel" },
        { GameAction.AscendRope,"AscendRope" },
        { GameAction.GrabRope,  "grabRope" },
        { GameAction.Rewind,    "beginRewind" },
    };

    /// <summary>
    /// The player's live InputAction for a curated game action (reflects their current rebind), or null
    /// if the input system isn't ready yet. Hand the result to a Prompt as its glyph.
    /// </summary>
    public static InputAction Action(GameAction action)
        => ActionPaths.TryGetValue(action, out var path) ? Named(path) : null;

    /// <summary>
    /// Resolve any game action by its "Map/Action" (or bare "Action") name against the live input asset —
    /// the escape hatch for actions not in the <see cref="GameAction"/> enum. Null if not found / not ready.
    /// </summary>
    public static InputAction Named(string mapSlashAction)
    {
        var asset = Asset;
        if (asset == null || string.IsNullOrEmpty(mapSlashAction)) return null;
        try { return asset.FindAction(mapSlashAction, false); } catch { return null; }
    }

    /// <summary>
    /// A glyph for a raw keyboard key, e.g. <c>Glyph.Key("g")</c>. Special keys (enter/space/esc/tab/arrows…)
    /// resolve to a stock sprite; plain letter/number keys have no sprite in the icon DB, so they render as
    /// the human-readable key text ("G") — exactly as the game does. See <see cref="Glyph"/>.
    /// </summary>
    public static InputAction Key(string key) => Path($"<Keyboard>/{key}");

    /// <summary>
    /// A glyph for an arbitrary control path, e.g. <c>Glyph.Path("&lt;Gamepad&gt;/buttonSouth")</c>. Buttons
    /// + special keys resolve to a stock sprite; anything without a DB sprite renders as human-readable text.
    /// </summary>
    public static InputAction Path(string controlPath)
    {
        if (string.IsNullOrEmpty(controlPath)) return null;
        try
        {
            // Reuse an already-built action for this path: AddAction on a map that already has an action of
            // the same name THROWS (duplicate name) — so a second Glyph.Key("b") would return null. The map
            // is keyed by controlPath as the action name, so FindAction makes repeated calls idempotent.
            var existing = CustomActions.FindAction(controlPath, false);
            if (existing != null) return existing;

            // A map must be DISABLED to add actions to it. PRESERVE its prior enabled state — other actions in
            // the same shared map (e.g. Glyph.Custom's enabled trigger actions) must keep firing afterward.
            bool wasEnabled = CustomActions.enabled;
            if (wasEnabled) CustomActions.Disable();

            // Build the action INSIDE a persistent throwaway InputActionMap. A bare standalone InputAction has
            // a null actionMap, and the glyph component's resolution lifecycle (InputManager.ResolveAction)
            // dereferences action.actionMap.id → NullReferenceException (live-proven). Giving it a map (kept
            // alive in CustomActions) avoids the NRE; the glyph reads the binding's effectivePath either way.
            var act = InputActionSetupExtensions.AddAction(
                CustomActions, name: controlPath, type: InputActionType.Button,
                binding: controlPath, interactions: null, processors: null, groups: null,
                expectedControlLayout: null);
            if (wasEnabled) CustomActions.Enable();
            return act;
        }
        catch { return null; }
    }

    /// <summary>
    /// A cross-device action with MULTIPLE bindings (e.g. a keyboard key AND a gamepad button), enabled so it
    /// fires on either device, and usable BOTH as a prompt glyph (the glyph + adaptiveBinding auto-picks the
    /// active device's binding, so the keycap swaps to the pad button on a controller) AND as live input
    /// (poll <c>action.WasPerformedThisFrame()</c>). This is the parity path: bind <c>"&lt;Keyboard&gt;/b"</c> +
    /// <c>"&lt;Gamepad&gt;/buttonWest"</c> and the same action drives the icon and the trigger on both devices.
    /// <paramref name="name"/> must be unique per logical action (it keys the action in the shared map).
    /// </summary>
    public static InputAction Custom(string name, params string[] bindingPaths)
    {
        if (string.IsNullOrEmpty(name) || bindingPaths == null || bindingPaths.Length == 0) return null;
        try
        {
            var existing = CustomActions.FindAction(name, false);
            if (existing != null) return existing;

            if (CustomActions.enabled) CustomActions.Disable();   // map must be disabled to mutate it
            var act = InputActionSetupExtensions.AddAction(
                CustomActions, name: name, type: InputActionType.Button,
                binding: bindingPaths[0], interactions: null, processors: null, groups: null,
                expectedControlLayout: null);
            for (int i = 1; i < bindingPaths.Length; i++)
                if (!string.IsNullOrEmpty(bindingPaths[i]))
                    InputActionSetupExtensions.AddBinding(act, bindingPaths[i], null, null, null);
            CustomActions.Enable();    // ENABLE so the action actually fires (unlike glyph-only Path/Key)
            return act;
        }
        catch { return null; }
    }

    // A persistent map that owns every mod-built custom-key action, so each action has a live actionMap
    // (required by the glyph component's resolution path) and isn't GC'd.
    private static InputActionMap _customActions;
    private static InputActionMap CustomActions
        => _customActions ??= new InputActionMap("CairnAPI.CustomGlyphs");

    /// <summary>The live InputActionAsset the game is driving (player's bindings live here), or null pre-game.</summary>
    public static InputActionAsset Asset
    {
        get
        {
            var im = MoSingleton<Il2Cpp.InputManager>.Instance;
            var actions = im != null ? im.InputActions : null;
            return actions != null ? actions.asset : null;
        }
    }
}

/// <summary>Friendly names for the game actions a custom prompt most commonly echoes. See <see cref="Glyph.Action"/>.</summary>
public enum GameAction
{
    Interact,
    SharePath,
    Confirm,
    Cancel,
    Navigate,
    Pause,
    Rest,
    Rappel,
    AscendRope,
    GrabRope,
    Rewind,
}
