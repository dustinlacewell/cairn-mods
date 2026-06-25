using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CrossMenuLib.Core), "CrossMenuLib", "0.1.0", "dustin")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CrossMenuLib;

/// <summary>
/// Library mod: lets other mods add actions to Cairn's cross-menu, and adds new LT+chord
/// "extra menus" of their own. This Core handles one-time Il2Cpp/Harmony setup and pumps the
/// menu controller each frame. It adds NO buttons of its own — the built-in self-test menu
/// (a worked example exercising the full dispatch pipe) registers only when
/// CROSSMENULIB_SELFTEST=1, so it can't shadow a consumer mod's chord in normal use.
/// </summary>
public class Core : MelonMod
{
    private static bool _initDone;

    public override void OnInitializeMelon()
    {
        RoutingHandler.EnsureRegistered();           // inject our handler type before any instances are made
        HarmonyInstance.PatchAll(typeof(Core).Assembly);
        // The self-test is a DEV-ONLY worked example: it claims the LT+RT chord and four wedges,
        // which would shadow any consumer mod's menu on the same chord. Off unless explicitly asked
        // for (CROSSMENULIB_SELFTEST=1) so the shipped library adds no buttons of its own.
        if (Environment.GetEnvironmentVariable("CROSSMENULIB_SELFTEST") == "1")
        {
            RegisterSelfTest();
            MelonLogger.Msg("[CrossMenuLib] self-test menu registered (CROSSMENULIB_SELFTEST=1).");
        }
        _initDone = true;
        MelonLogger.Msg("[CrossMenuLib] initialised.");
    }

    public override void OnUpdate()
    {
        if (!_initDone) return;
        try { MenuController.Tick(); }
        catch (Exception ex) { MelonLogger.Error($"[CrossMenuLib] Tick error: {ex}"); }
    }

    // The HUD is streamed across many sub-scenes; we do NOT invalidate per scene (that would
    // thrash the clone). MenuController self-heals when its bound menu is destroyed.

    /// <summary>
    /// A harmless built-in action proving the whole pipe (inject → register → render →
    /// vtable dispatch). Sits in the LT+RT mod menu, Up. Logs + screen-flashes when run.
    /// Doubles as the canonical usage example for consumer mods.
    /// </summary>
    private static void RegisterSelfTest()
    {
        // LT+RT demo menu with four Lucide-iconed actions, proving chords, icons, and dispatch.
        CrossMenu.DefineMenu("crossmenulib.demo", Modifier.RightTrigger);

        Demo("crossmenulib.demo.up", "Anchor", "anchor", MenuDir.Up);
        Demo("crossmenulib.demo.right", "Flame", "flame", MenuDir.Right);
        Demo("crossmenulib.demo.down", "Snow", "mountain-snow", MenuDir.Down);
        Demo("crossmenulib.demo.left", "Bell", "bell", MenuDir.Left);
    }

    private static void Demo(string id, string label, string icon, MenuDir dir)
    {
        int presses = 0;
        CrossMenu.Register(new CrossMenuAction
        {
            Id = id,
            Label = label,
            Menu = "crossmenulib.demo",
            Direction = dir,
            IconName = icon,
            DisplayCount = true,
            OnExecute = () =>
            {
                presses++;
                MelonLogger.Msg($"[CrossMenuLib] DEMO '{label}' executed (press #{presses}).");
            },
            GetCount = () => presses,
        });
    }
}
