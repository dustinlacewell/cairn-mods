using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnTools;

// Input seam. The game runs the new Input System exclusively — legacy UnityEngine.Input
// throws ("switched active Input handling to Input System package") — so all key/mouse reads
// go through Keyboard.current / Mouse.current here. One place to touch if device handling changes.
// (Same pattern as CairnAccessories.Inputs.)
public static class Inputs
{
    public static bool KeyDown(Key key)
    {
        var kb = Keyboard.current;
        return kb != null && kb[key].wasPressedThisFrame;
    }

    public static bool MouseDown
    {
        get { var m = Mouse.current; return m != null && m.leftButton.wasPressedThisFrame; }
    }

    public static bool MouseHeld
    {
        get { var m = Mouse.current; return m != null && m.leftButton.isPressed; }
    }

    // Screen position, origin bottom-left (matches overlay-canvas world coords).
    public static Vector2 MousePos
    {
        get { var m = Mouse.current; return m != null ? m.position.ReadValue() : Vector2.zero; }
    }
}
