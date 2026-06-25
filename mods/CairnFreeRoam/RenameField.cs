using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnFreeRoam;

/// <summary>
/// Live text entry for renaming a bookmark, UI-agnostic. Characters come from InputSystem's
/// Keyboard.onTextInput (the engine resolves layout/shift); Enter/Escape/Backspace are POLLED in Update —
/// never elsewhere — exactly as CairnRoutes.EditField does (GUILayout.TextField is dead in this IL2CPP game).
/// The owner (BookmarkController) starts an edit with the current name, reads Buffer each frame to repaint the
/// row label, and reacts to Committed/Cancelled.
/// </summary>
public sealed class RenameField
{
    private bool _editing;
    private string _buffer = "";

    // The text-input hook is PROCESS-GLOBAL and installed exactly ONCE. Subscribing per-instance or
    // per-keyboard-change accumulated duplicate native subscribers every frame (one keystroke → "ffffff").
    // A single static subscription forwards every char to whichever RenameField is currently editing.
    private static bool _hooked;
    private static Keyboard _hookedKeyboard;
    private static Action<char> _onChar; // kept alive for the Il2Cpp delegate
    private static RenameField _active;  // the field receiving chars (set on Begin, cleared on end)

    private float _backspaceHeldSince = -1f;
    private float _nextRepeatAt;

    public bool IsEditing => _editing;
    public string Buffer => _buffer;

    /// <summary>Just committed this frame (consume once); carries the final text in <see cref="Buffer"/>.</summary>
    public bool Committed { get; private set; }
    /// <summary>Just cancelled this frame (consume once).</summary>
    public bool Cancelled { get; private set; }

    public void Begin(string initial)
    {
        _editing = true;
        _buffer = initial ?? "";
        _active = this;     // route incoming chars here
    }

    /// <summary>Poll edit controls. Returns nothing; check IsEditing/Buffer/Committed/Cancelled after.</summary>
    public void Update()
    {
        EnsureHook();
        Committed = false;
        Cancelled = false;
        if (!_editing) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
        {
            _editing = false;
            if (_active == this) _active = null;
            Committed = true;
        }
        else if (kb.escapeKey.wasPressedThisFrame)
        {
            _editing = false;
            if (_active == this) _active = null;
            Cancelled = true;
        }
        else
        {
            HandleBackspace(kb);
        }
    }

    // Static: the single hook forwards to whichever field is editing.
    private static void OnTextInput(char c)
    {
        var f = _active;
        if (f == null || !f._editing || char.IsControl(c)) return;
        f._buffer += c;
    }

    private void HandleBackspace(Keyboard kb)
    {
        if (kb.backspaceKey.wasPressedThisFrame)
        {
            Erase();
            _backspaceHeldSince = Time.unscaledTime;
            _nextRepeatAt = _backspaceHeldSince + 0.4f;
        }
        else if (kb.backspaceKey.isPressed && _backspaceHeldSince >= 0f && Time.unscaledTime >= _nextRepeatAt)
        {
            Erase();
            _nextRepeatAt = Time.unscaledTime + 0.05f;
        }
        else if (!kb.backspaceKey.isPressed)
        {
            _backspaceHeldSince = -1f;
        }
    }

    private void Erase()
    {
        if (_buffer.Length > 0) _buffer = _buffer[..^1];
    }

    /// <summary>
    /// Install the text-input subscription exactly once per process. We subscribe a single static handler and
    /// NEVER add again — re-adding (even when the keyboard wrapper looks unchanged) accumulated duplicate
    /// native subscribers and doubled/tripled every keystroke. Re-hook only if the keyboard object genuinely
    /// changes (device swap), removing the old subscription first.
    /// </summary>
    private static void EnsureHook()
    {
        if (_hooked) return;                 // subscribe ONCE, ever — never accumulate subscribers
        var kb = Keyboard.current;
        if (kb == null) return;
        _onChar ??= OnTextInput;
        kb.add_onTextInput(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<char>>(_onChar));
        _hookedKeyboard = kb;
        _hooked = true;
    }
}
