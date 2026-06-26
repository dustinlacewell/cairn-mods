using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;

namespace CairnAPI;

// Named entry points for injecting items into the game's two BouncingButtons nav rails.
//
// Usage:
//   CairnMenu.Settings.AddEntry("Mods", () => OpenModsPage());
//   CairnMenu.PauseMenu.AddEntry("Mod Tools", () => OpenMyTool());
//
// Each returns an IDisposable handle. Dispose to remove the entry.
public static class CairnMenu
{
    public static readonly SettingsRail  Settings  = new SettingsRail();
    public static readonly PauseMenuRail PauseMenu = new PauseMenuRail();

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.PatchAll(typeof(SettingsRail.Patches));
        harmony.PatchAll(typeof(PauseMenuRail.Patches));
    }

    // ── Settings rail ─────────────────────────────────────────────────────────

    public sealed class SettingsRail
    {
        private readonly List<PendingEntry> _pending = new List<PendingEntry>();

        internal sealed class PendingEntry
        {
            internal string Label;
            internal Action OnClick;
            internal PendingEntry(string label, Action onClick) { Label = label; OnClick = onClick; }
        }

        public IDisposable AddEntry(string label, Action onClick)
        {
            _pending.Add(new PendingEntry(label, onClick));
            return new Handle(label, onClick, this);
        }

        internal void Remove(string label, Action onClick)
        {
            _pending.RemoveAll(e => e.Label == label && e.OnClick == onClick);
        }

        internal void Inject(SettingsMenu menu)
        {
            var parent = menu.transform.Find("Main/SettingsPageButtons");
            if (parent == null) { MelonLogger.Warning("[CairnMenu.Settings] SettingsPageButtons not found."); return; }
            var bb = parent.GetComponent<BouncingButtons>();
            if (bb == null) { MelonLogger.Warning("[CairnMenu.Settings] BouncingButtons not found."); return; }
            _ = bb.ButtonsData;

            foreach (var p in _pending)
            {
                try
                {
                    var entry = MenuRail.AddEntry(bb, parent, p.Label, p.OnClick, "CategoryReturnClickArea");
                    Patches.Track(entry);
                }
                catch (Exception ex) { MelonLogger.Error($"[CairnMenu.Settings] AddEntry '{p.Label}' failed: {ex}"); }
            }
        }

        internal static class Patches
        {
            private static readonly List<MenuRail.Entry> _entries = new List<MenuRail.Entry>();

            internal static void Track(MenuRail.Entry e) => _entries.Add(e);

            internal static void DisposeAll()
            {
                foreach (var e in _entries) e.Dispose();
                _entries.Clear();
            }

            [HarmonyPatch(typeof(SettingsMenu), "OnOpened")]
            [HarmonyPostfix]
            static void AfterOpened(SettingsMenu __instance)
            {
                DisposeAll();
                CairnMenu.Settings.Inject(__instance);
            }

            [HarmonyPatch(typeof(SettingsMenu), "OnClosed")]
            [HarmonyPostfix]
            static void AfterClosed(SettingsMenu __instance) => DisposeAll();
        }

        private sealed class Handle : IDisposable
        {
            private readonly string       _label;
            private readonly Action       _onClick;
            private readonly SettingsRail _rail;
            private bool                  _disposed;

            internal Handle(string label, Action onClick, SettingsRail rail)
            { _label = label; _onClick = onClick; _rail = rail; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _rail.Remove(_label, _onClick);
            }
        }
    }

    // ── Pause menu rail ───────────────────────────────────────────────────────

    public sealed class PauseMenuRail
    {
        private readonly List<PendingEntry> _pending = new List<PendingEntry>();

        internal sealed class PendingEntry
        {
            internal string Label;
            internal Action OnClick;
            internal PendingEntry(string label, Action onClick) { Label = label; OnClick = onClick; }
        }

        public IDisposable AddEntry(string label, Action onClick)
        {
            _pending.Add(new PendingEntry(label, onClick));
            return new Handle(label, onClick, this);
        }

        internal void Remove(string label, Action onClick)
        {
            _pending.RemoveAll(e => e.Label == label && e.OnClick == onClick);
        }

        internal void Inject(Il2CppCairn.UI.MainSubmenu submenu)
        {
            var bb = submenu.GetComponentInChildren<BouncingButtons>();
            if (bb == null) { MelonLogger.Warning("[CairnMenu.PauseMenu] BouncingButtons not found."); return; }
            _ = bb.ButtonsData;

            foreach (var p in _pending)
            {
                try
                {
                    var entry = MenuRail.AddEntry(bb, bb.transform, p.Label, p.OnClick);
                    Patches.Track(entry);
                }
                catch (Exception ex) { MelonLogger.Error($"[CairnMenu.PauseMenu] AddEntry '{p.Label}' failed: {ex}"); }
            }
        }

        internal static class Patches
        {
            private static readonly List<MenuRail.Entry> _entries = new List<MenuRail.Entry>();

            internal static void Track(MenuRail.Entry e) => _entries.Add(e);

            internal static void DisposeAll()
            {
                foreach (var e in _entries) e.Dispose();
                _entries.Clear();
            }

            [HarmonyPatch(typeof(Il2CppCairn.UI.MainSubmenu), "Awake")]
            [HarmonyPostfix]
            static void AfterAwake(Il2CppCairn.UI.MainSubmenu __instance)
            {
                DisposeAll();
                CairnMenu.PauseMenu.Inject(__instance);
            }

            [HarmonyPatch(typeof(Il2CppCairn.UI.SubmenuBehaviour), "Close")]
            [HarmonyPostfix]
            static void AfterClose(Il2CppCairn.UI.SubmenuBehaviour __instance)
            {
                if (__instance.TryCast<Il2CppCairn.UI.MainSubmenu>() != null)
                    DisposeAll();
            }
        }

        private sealed class Handle : IDisposable
        {
            private readonly string        _label;
            private readonly Action        _onClick;
            private readonly PauseMenuRail _rail;
            private bool                   _disposed;

            internal Handle(string label, Action onClick, PauseMenuRail rail)
            { _label = label; _onClick = onClick; _rail = rail; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _rail.Remove(_label, _onClick);
            }
        }
    }
}
