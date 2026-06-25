using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnAccessories;

/// <summary>
/// Keyboard transform editor for the selected accessory. No mouse, no 3D handles:
///   mode    T / R / S                  set translate / rotate / scale
///   nudge   x X  y Y  z Z              apply current mode along that axis; lower=negative, upper=positive
///   step    [ ]                        halve / double the nudge step
/// Keys hold-to-repeat (tap once, or hold to keep nudging). All edits are in the accessory's
/// LOCAL space (it's parented to the bone), so what you set is what tracks the pose and what
/// gets saved. Active only while the editor panel is open and something is selected.
/// </summary>
public sealed class Gizmo
{
    public enum Mode { Translate, Rotate, Scale }

    public Mode CurrentMode { get; private set; } = Mode.Translate;

    // step is in mode-native units: metres (translate), degrees (rotate), fraction (scale).
    private float _moveStep = 0.005f;   // 5 mm
    private float _rotStep = 5f;        // 5 degrees
    private float _scaleStep = 0.05f;   // 5%

    private const float RepeatDelay = 0.30f;   // before hold-repeat kicks in
    private const float RepeatRate = 0.04f;    // seconds between repeats while held

    private readonly RepeatKey[] _nudges =
    {
        new(Key.X, 0, -1), new(Key.X, 0, +1, shift: true),
        new(Key.Y, 1, -1), new(Key.Y, 1, +1, shift: true),
        new(Key.Z, 2, -1), new(Key.Z, 2, +1, shift: true),
    };

    // --- button-driven API (the transform window) — shares the same state as the keyboard path -

    /// <summary>Scale all three mode steps by a factor (keyboard '[' ']').</summary>
    public void ScaleStep(float f) { _moveStep *= f; _rotStep *= f; _scaleStep *= f; }

    /// <summary>Scale just one mode's step (the per-column step −/+ in the transform window).</summary>
    public void ScaleStepFor(Mode mode, float f)
    {
        switch (mode)
        {
            case Mode.Translate: _moveStep *= f; break;
            case Mode.Rotate: _rotStep *= f; break;
            case Mode.Scale: _scaleStep *= f; break;
        }
    }

    /// <summary>Uniformly scale the accessory on all axes by one step (the Scale column's −/+).</summary>
    public void NudgeUniformScale(Accessory selected, int sign)
    {
        var go = selected?.Instance;
        if (go == null) return;
        go.transform.localScale *= 1f + _scaleStep * sign;
    }

    /// <summary>Snap the accessory onto its bone: zero local position and rotation so it sits exactly
    /// at the bone's origin and matches its orientation. Scale is left as-is.</summary>
    public void AlignToBone(Accessory selected)
    {
        var go = selected?.Instance;
        if (go == null) return;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
    }

    /// <summary>Apply a single nudge in an explicit mode (one button click = one step). Lets the
    /// pointer window's Move/Rotate/Scale columns each act in their own mode at once, independent
    /// of the keyboard's CurrentMode.</summary>
    public void Nudge(Accessory selected, Mode mode, int axis, int sign)
    {
        var go = selected?.Instance;
        if (go == null) return;
        Apply(go.transform, mode, axis, sign);
    }

    /// <summary>Current step text for a given mode (mm / ° / %).</summary>
    public string StepTextFor(Mode mode) => mode switch
    {
        Mode.Translate => $"{_moveStep * 1000f:0.#}mm",
        Mode.Rotate => $"{_rotStep:0.#}°",
        _ => $"{_scaleStep * 100f:0.#}%",
    };

    /// <summary>Call every frame while the editor is active. Handles mode keys, step keys,
    /// and hold-to-repeat axis nudges against the selected accessory.</summary>
    public void Tick(Accessory selected)
    {
        ReadModeKeys();
        ReadStepKeys();

        var go = selected?.Instance;
        if (go == null) return;
        var t = go.transform;

        foreach (var n in _nudges)
            if (n.Fired()) Apply(t, n.Axis, n.Sign);
    }

    private void ReadModeKeys()
    {
        if (KeyDown(Key.T)) CurrentMode = Mode.Translate;
        if (KeyDown(Key.R)) CurrentMode = Mode.Rotate;
        // S only sets Scale mode when Ctrl is NOT held (Ctrl+S = save, handled in Core).
        if (KeyDown(Key.S) && !Held(Key.LeftCtrl) && !Held(Key.RightCtrl)) CurrentMode = Mode.Scale;
    }

    private void ReadStepKeys()
    {
        float f = (KeyDown(Key.LeftBracket) ? 0.5f : 1f) * (KeyDown(Key.RightBracket) ? 2f : 1f);
        if (f != 1f) ScaleStep(f);
    }

    private void Apply(Transform t, int axis, int sign) => Apply(t, CurrentMode, axis, sign);

    private void Apply(Transform t, Mode mode, int axis, int sign)
    {
        Vector3 a = AxisVec(axis);
        switch (mode)
        {
            case Mode.Translate:
                t.localPosition += a * (_moveStep * sign);
                break;
            case Mode.Rotate:
                t.localRotation = Quaternion.AngleAxis(_rotStep * sign, a) * t.localRotation;
                break;
            case Mode.Scale:
                t.localScale *= 1f + _scaleStep * sign;
                break;
        }
    }

    private static Vector3 AxisVec(int axis) => axis switch
    {
        0 => Vector3.right,
        1 => Vector3.up,
        _ => Vector3.forward,
    };

    // --- input helpers (new Input System; legacy UnityEngine.Input throws in this build) ---

    private static bool KeyDown(Key k)
    {
        var kb = Keyboard.current;
        return kb != null && kb[k].wasPressedThisFrame;
    }

    private static bool Held(Key k)
    {
        var kb = Keyboard.current;
        return kb != null && kb[k].isPressed;
    }

    /// <summary>One axis+sign nudge key with tap + hold-to-repeat. Shift selects the +sign
    /// variant: a nudge fires only when shift state matches, so x and X don't both trigger.</summary>
    private sealed class RepeatKey
    {
        private readonly Key _key;
        private readonly bool _shift;
        public readonly int Axis;
        public readonly int Sign;
        private float _heldSince = -1f;
        private float _nextRepeat;

        public RepeatKey(Key key, int axis, int sign, bool shift = false)
        {
            _key = key; Axis = axis; Sign = sign; _shift = shift;
        }

        public bool Fired()
        {
            var kb = Keyboard.current;
            bool shiftNow = kb != null && (kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed);
            bool down = kb != null && kb[_key].isPressed && shiftNow == _shift;

            if (!down) { _heldSince = -1f; return false; }

            float now = Time.unscaledTime;
            if (_heldSince < 0f)                       // just pressed: fire once, arm repeat
            {
                _heldSince = now;
                _nextRepeat = now + RepeatDelay;
                return true;
            }
            if (now >= _nextRepeat)                     // held past delay: repeat at rate
            {
                _nextRepeat = now + RepeatRate;
                return true;
            }
            return false;
        }
    }
}
