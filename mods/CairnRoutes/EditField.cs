using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRoutes;

/// <summary>
/// Click-to-edit replacement for GUILayout.TextField, which is dead in this game
/// (IL2CPP stripped GUI.DoTextField and MelonLoader's unstripping fails on it).
/// Characters come from InputSystem's Keyboard.onTextInput (layout/shift handled
/// by the engine); enter/escape/backspace are polled in Update — never inside
/// OnGUI, which runs multiple passes per frame and would double-process keys.
///
/// Usage: call Update() once per frame, then in OnGUI
///     value = EditField.Draw(idObject, value, options);
/// Draw returns the committed text once on commit, otherwise the passed value.
/// Clicking the field starts editing; enter or clicking again commits; escape
/// cancels; clicking a different EditField commits the old one first.
/// </summary>
public static class EditField
{
    private static object _owner;
    private static string _buffer = "";

    private static object _resultOwner;
    private static string _resultValue;

    private static Keyboard _hookedKeyboard;
    private static Action<char> _onChar; // kept alive for the Il2Cpp delegate's sake
    private static GUIStyle _editingStyle;

    private static float _backspaceHeldSince = -1f;
    private static float _nextRepeatAt;

    public static bool IsEditing => _owner != null;

    public static void Update()
    {
        EnsureHook();
        if (_owner == null) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            Commit();
        else if (kb.escapeKey.wasPressedThisFrame)
            _owner = null;
        else
            HandleBackspace(kb);
    }

    public static string Draw(object id, string value, params GUILayoutOption[] options)
    {
        if (ReferenceEquals(_resultOwner, id))
        {
            value = _resultValue;
            _resultOwner = null;
        }

        bool editing = ReferenceEquals(_owner, id);
        string label = editing ? _buffer + "▏" : value;
        if (GUILayout.Button(label, editing ? EditingStyle() : GUI.skin.textField, options))
        {
            if (editing) Commit();
            else Begin(id, value);
        }
        return value;
    }

    private static void Begin(object id, string value)
    {
        if (_owner != null) Commit(); // switching fields keeps the old edit
        _owner = id;
        _buffer = value ?? "";
    }

    private static void Commit()
    {
        _resultOwner = _owner;
        _resultValue = _buffer;
        _owner = null;
    }

    private static void OnTextInput(char c)
    {
        if (_owner == null || char.IsControl(c)) return;
        _buffer += c;
    }

    private static void HandleBackspace(Keyboard kb)
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

    private static void Erase()
    {
        if (_buffer.Length > 0) _buffer = _buffer[..^1];
    }

    /// <summary>Keyboard.current can be replaced (device changes); re-hook when it does.</summary>
    private static void EnsureHook()
    {
        var kb = Keyboard.current;
        if (kb == null || ReferenceEquals(kb, _hookedKeyboard)) return;
        _onChar ??= OnTextInput;
        kb.add_onTextInput(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<char>>(_onChar));
        _hookedKeyboard = kb;
    }

    private static GUIStyle EditingStyle()
    {
        if (_editingStyle == null)
        {
            _editingStyle = new GUIStyle(GUI.skin.textField);
            _editingStyle.normal.textColor = Color.yellow;
            _editingStyle.hover.textColor = Color.yellow;
            _editingStyle.focused.textColor = Color.yellow;
        }
        return _editingStyle;
    }
}
