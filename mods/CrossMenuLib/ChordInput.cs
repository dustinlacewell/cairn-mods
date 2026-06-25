using UnityEngine;
using UnityEngine.InputSystem;

namespace CrossMenuLib;

/// <summary>
/// Reads the gamepad and resolves which menu the currently-held chord selects.
///
/// <para>The left trigger is the base "open the wheel" input (vanilla owns that). A mod menu is
/// selected by LT <em>plus</em> an additional chord of modifiers (see <see cref="Modifier"/>). LT
/// alone resolves to no mod menu — the vanilla wheel shows. When several defined chords are all
/// satisfied, the most specific one (most modifier bits) wins.</para>
/// </summary>
internal static class ChordInput
{
    /// <summary>A frame's relevant pad state: whether LT is held, and the selected mod menu (or null).</summary>
    internal readonly struct State
    {
        internal readonly bool LeftTriggerHeld;
        internal readonly string Menu;        // selected mod-menu id, or null for the vanilla wheel
        internal State(bool lt, string menu) { LeftTriggerHeld = lt; Menu = menu; }
    }

    /// <summary>Sample the pad and resolve this frame's state.</summary>
    internal static State Read()
    {
        var pad = Gamepad.current;
        if (pad == null) return new State(false, null);

        bool lt = pad.leftTrigger.isPressed;
        string menu = lt ? ResolveMenu(pad) : null;
        return new State(lt, menu);
    }

    /// <summary>Most-specific defined chord (beyond LT) whose modifiers are all held; else null.</summary>
    private static string ResolveMenu(Gamepad pad)
    {
        Modifier have = Modifier.None;
        if (pad.rightTrigger.isPressed) have |= Modifier.RightTrigger;
        if (pad.leftShoulder.isPressed) have |= Modifier.LeftBumper;
        if (pad.rightShoulder.isPressed) have |= Modifier.RightBumper;
        if (have == Modifier.None) return null;        // LT alone → vanilla wheel, not ours

        string best = null;
        int bestBits = 0;
        foreach (var kv in Registry.Menus)
        {
            if ((have & kv.Value) != kv.Value) continue;   // not all of this menu's modifiers held
            int bits = PopCount((int)kv.Value);
            if (bits > bestBits) { bestBits = bits; best = kv.Key; }
        }
        return best;
    }

    private static int PopCount(int v)
    {
        int n = 0;
        while (v != 0) { n += v & 1; v >>= 1; }
        return n;
    }
}
