using System;
using MelonLoader;
using UnityEngine;

namespace CairnAPI;

/// <summary>
/// Extra gamepad modifiers — held <em>in addition to the left trigger</em> — that select a
/// menu. LT alone is the base (vanilla) wheel; LT + a chord of these selects a mod menu.
/// Combine with '|' (e.g. <c>CrossMenuModifier.RightTrigger</c>, or RB|LB for a two-button chord).
/// </summary>
[Flags]
public enum CrossMenuModifier
{
    None = 0,
    RightTrigger = 1 << 0,
    LeftBumper = 1 << 1,
    RightBumper = 1 << 2,
    // room to grow (face buttons, stick clicks) without breaking the enum
}

/// <summary>One of the four radial directions. Mirrors the game's CrossMenuUI.Direction.</summary>
public enum CrossMenuDir
{
    Up = 1,
    Right = 2,
    Down = 3,
    Left = 4,
}

/// <summary>
/// A custom cross-menu action a mod registers. The library owns all Il2Cpp/render/dispatch
/// plumbing; a mod supplies identity, which menu + direction it occupies, an icon, and the
/// behaviour callbacks.
/// </summary>
public sealed class CrossMenuAction
{
    /// <summary>Stable unique id, e.g. "mymod.grapple". Re-registering the same id replaces it.</summary>
    public string Id;

    /// <summary>Shown in logs and (future) tooltips.</summary>
    public string Label;

    /// <summary>
    /// The menu this action lives in — a menu id previously passed to
    /// <see cref="CrossMenu.DefineMenu"/>. Defaults to <see cref="CrossMenu.BaseMenu"/> (the
    /// vanilla LT wheel — only its free slots may be used).
    /// </summary>
    public string Menu = CrossMenu.BaseMenu;

    public CrossMenuDir Direction = CrossMenuDir.Up;

    /// <summary>A built-in Lucide icon name (e.g. "anchor", "flame"). Used when <see cref="Icon"/> is null.</summary>
    public string IconName;

    /// <summary>Explicit icon sprite; takes precedence over <see cref="IconName"/>. Null → placeholder.</summary>
    public Sprite Icon;
    public Sprite IconUnavailable;

    /// <summary>If true a numeric badge (from <see cref="GetCount"/>) is drawn on the icon.</summary>
    public bool DisplayCount;

    /// <summary>At or below this count the badge turns to the warning colour. Only when DisplayCount.</summary>
    public int CountWarningMaxValue;

    // --- behaviour callbacks (run on the Unity main thread) ---

    /// <summary>Invoked when the action is executed (after the hold-to-confirm). Required.</summary>
    public Action OnExecute;

    /// <summary>Return false to grey-out / block. Optional — default always-available.</summary>
    public Func<bool> IsAvailable;

    /// <summary>Numeric badge value when <see cref="DisplayCount"/> is set. Optional — default 0.</summary>
    public Func<int> GetCount;

    /// <summary>Invoked when attempted-but-unavailable. Optional.</summary>
    public Action OnFailedExecute;
}

/// <summary>
/// CairnAPI cross-menu subsystem. Lets other mods add actions to Cairn's cross-menu, and adds
/// new LT+chord "extra menus" of their own. Define menus by chord, then register actions into them.
/// <code>
/// CrossMenu.DefineMenu("mymod.combat", CrossMenuModifier.RightTrigger);   // LT+RT
/// CrossMenu.Register(new CrossMenuAction {
///     Id = "mymod.grapple", Label = "Grapple", IconName = "anchor",
///     Menu = "mymod.combat", Direction = CrossMenuDir.Up,
///     OnExecute = () => DoGrapple(),
/// });
/// </code>
///
/// <para>Static façade in the CairnAPI per-subsystem idiom: <see cref="Install"/> does the one-time
/// Il2Cpp/Harmony setup (called from <c>Core.OnInitializeMelon</c>); <see cref="Tick"/> pumps the
/// menu controller every frame (called from <c>Core.OnUpdate</c>).</para>
/// </summary>
public static class CrossMenu
{
    /// <summary>The vanilla LT wheel (level-0). Actions here use only its free slots.</summary>
    public const string BaseMenu = "vanilla";

    private static bool _initDone;

    /// <summary>
    /// One-time setup: inject the routing handler type into Il2Cpp before any instances are made,
    /// then apply this subsystem's Harmony patches. Mirrors the other CairnAPI subsystems' Install.
    /// </summary>
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        RoutingHandler.EnsureRegistered();           // inject our handler type before any instances are made
        harmony.PatchAll(typeof(CrossMenu).Assembly);
        _initDone = true;
        MelonLogger.Msg("[CairnAPI:CrossMenu] initialised.");
    }

    /// <summary>
    /// Per-frame pump of the chord-menu state machine. Must run every frame or the menu won't function.
    /// Called from <c>Core.OnUpdate</c>. The HUD is streamed across many sub-scenes; we do NOT
    /// invalidate per scene (that would thrash the clone) — MenuController self-heals when its bound
    /// menu is destroyed.
    /// </summary>
    internal static void Tick()
    {
        if (!_initDone) return;
        try { MenuController.Tick(); }
        catch (Exception ex) { MelonLogger.Error($"[CairnAPI:CrossMenu] Tick error: {ex}"); }
    }

    /// <summary>
    /// Define (or update) a menu selected by holding LT + the given modifier chord. Two mods
    /// must not claim the same chord; the first wins and a warning is logged. The base menu
    /// (LT alone) is implicit and need not be defined.
    /// </summary>
    public static void DefineMenu(string menuId, CrossMenuModifier chord) => Registry.DefineMenu(menuId, chord);

    /// <summary>Register (or replace, by Id) a custom action. Safe to call before the HUD exists.</summary>
    public static void Register(CrossMenuAction action) => Registry.Register(action);

    /// <summary>Remove a previously-registered action by Id. No-op if unknown.</summary>
    public static void Unregister(string id) => Registry.Unregister(id);

    /// <summary>True once the live menu is found and the library is driving it.</summary>
    public static bool Ready => MenuController.Ready;
}
