using System;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace CairnDevTools;

/// <summary>
/// Synthetic gamepad input for testing — drives the game's REAL Unity InputSystem so the game's
/// own input handlers react exactly as to a physical controller, INCLUDING InputAction edges
/// (<c>performed</c>/<c>canceled</c>), which is what makes hold-to-switch / chord bindings fire.
///
/// <para>Two things are load-bearing, both learned the hard way:
/// <list type="number">
/// <item><b>Queue + flush.</b> A held control is re-asserted every frame via
/// <see cref="InputSystem.QueueEvent"/> AND a synchronous <see cref="InputSystem.Update()"/> so
/// the event is processed THIS frame and InputActions observe the 0→1 / 1→0 transitions. Merely
/// writing the value (e.g. <c>InputState.Change</c>) latches the reading but fires NO action
/// callbacks, so layer-toggles never happen.</item>
/// <item><b>Own the device.</b> The physical pad keeps reporting its idle (0) state every frame;
/// those reports interleave with and overwrite ours. While simulating we mark the synthetic
/// values authoritative and re-assert post-update each frame so the physical idle can't win.
/// We do NOT <c>DisableDevice</c> the pad — a disabled device REJECTS queued events outright
/// (and we still want it to stay <c>Gamepad.current</c> so the game/mods read our values).</item>
/// </list></para>
///
/// <para>Controlled live via the <c>input</c> console command: <c>input lt 1</c> holds the left
/// trigger, <c>input clear</c> hands control back to the physical device.</para>
/// </summary>
public static class InputSim
{
    private static bool _active;
    private static float _lt, _rt, _lb, _rb;
    private static float _lsx, _lsy;
    // last values we drove an EDGE for — so we only queue an action-firing event on a transition
    private static float _pLt = -1, _pRt = -1, _pLb = -1, _pRb = -1;

    /// <summary>Re-assert the held synthetic state every frame. Two mechanisms, deliberately:
    /// <list type="bullet">
    /// <item><b>Latch (every frame):</b> <see cref="InputState.Change"/> writes the held values
    /// directly into device state. This SURVIVES the whole frame and is visible to EVERY reader
    /// regardless of mod update order (a queued event would only be visible after it's processed,
    /// so mods updating before our Pump would see the stale physical idle = the flicker we hit).</item>
    /// <item><b>Edge (on transition only):</b> when a control's value crosses, we ALSO queue a real
    /// event + <see cref="InputSystem.Update"/> so InputActions observe performed/canceled — that's
    /// what fires hold-to-switch / chord bindings. Latching alone updates readings but fires no
    /// action callbacks.</item>
    /// </list>
    /// Call once per frame from OnUpdate.</summary>
    public static void Pump()
    {
        if (!_active) return;
        var pad = Gamepad.current;
        if (pad == null) return;

        // 1) Latch held values into device state (visible to all readers this frame).
        InputEventPtr ev;
        var disp = StateEvent.From(pad, out ev);
        pad.leftTrigger.WriteValueIntoEvent(_lt, ev);
        pad.rightTrigger.WriteValueIntoEvent(_rt, ev);
        pad.leftShoulder.WriteValueIntoEvent(_lb, ev);
        pad.rightShoulder.WriteValueIntoEvent(_rb, ev);
        pad.leftStick.WriteValueIntoEvent(new UnityEngine.Vector2(_lsx, _lsy), ev);
        InputState.Change(pad, ev);
        disp.Dispose();

        // 2) On any transition, queue a real event so InputActions fire the edge.
        if (_lt != _pLt || _rt != _pRt || _lb != _pLb || _rb != _pRb)
        {
            InputEventPtr ev2;
            var disp2 = StateEvent.From(pad, out ev2);
            pad.leftTrigger.WriteValueIntoEvent(_lt, ev2);
            pad.rightTrigger.WriteValueIntoEvent(_rt, ev2);
            pad.leftShoulder.WriteValueIntoEvent(_lb, ev2);
            pad.rightShoulder.WriteValueIntoEvent(_rb, ev2);
            pad.leftStick.WriteValueIntoEvent(new UnityEngine.Vector2(_lsx, _lsy), ev2);
            InputSystem.QueueEvent(ev2);
            InputSystem.Update();
            disp2.Dispose();
            _pLt = _lt; _pRt = _rt; _pLb = _lb; _pRb = _rb;
        }
    }

    /// <summary>Console command: <c>input &lt;control&gt; &lt;value&gt;</c> | <c>input clear</c> | <c>input state</c>.</summary>
    public static string Command(string[] args)
    {
        if (args.Length == 0)
            return "usage: input <lt|rt|lb|rb|lsx|lsy> <0..1> [...] | input clear | input state";

        if (args[0] == "clear")
        {
            _active = false; _lt = _rt = _lb = _rb = _lsx = _lsy = 0;
            _pLt = _pRt = _pLb = _pRb = -1;   // force a fresh edge on the next hold
            return "input released to physical device";
        }
        if (args[0] == "state")
            return $"active={_active} lt={_lt} rt={_rt} lb={_lb} rb={_rb} lstick=({_lsx},{_lsy})";

        // pairs: control value control value ...
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (!float.TryParse(args[i + 1], out var v)) return $"bad value '{args[i + 1]}'";
            switch (args[i].ToLowerInvariant())
            {
                case "lt": _lt = v; break;
                case "rt": _rt = v; break;
                case "lb": _lb = v; break;
                case "rb": _rb = v; break;
                case "lsx": _lsx = v; break;
                case "lsy": _lsy = v; break;
                default: return $"unknown control '{args[i]}' (lt|rt|lb|rb|lsx|lsy)";
            }
        }
        _active = true;
        return $"holding: lt={_lt} rt={_rt} lb={_lb} rb={_rb} lstick=({_lsx},{_lsy}) — `input clear` to release";
    }
}
