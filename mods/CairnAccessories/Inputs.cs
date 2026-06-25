using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnAccessories;

/// <summary>
/// Input seam. The game runs the new Input System exclusively — legacy UnityEngine.Input
/// throws — so all key/mouse reads go through Keyboard.current / Mouse.current here. One
/// place to touch if device handling changes.
/// </summary>
public static class Inputs
{
    public static bool KeyDown(Key key)
    {
        var kb = Keyboard.current;
        return kb != null && kb[key].wasPressedThisFrame;
    }

    public static bool KeyHeld(Key key)
    {
        var kb = Keyboard.current;
        return kb != null && kb[key].isPressed;
    }
}
