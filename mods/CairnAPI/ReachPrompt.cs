using System;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using MelonLoader;

namespace CairnAPI;

// A WORLD prompt gated by a reaching HAND overlapping the anchor — exactly like native interactables: the
// affordance shows ONLY when a hand physically reaches into it, and fires your callback on press. The reach
// gate is the GAME's own mechanism: the climber's hand bones carry small PawnInteractionSensor trigger spheres
// (layer 16 "Interactions", sensing only while climbing/reaching). We put our trigger collider on layer 16, so
// OnTriggerEnter/Exit fire when a hand sensor overlaps us — true reach-to-interact, no distance poll. DESIGN §5.
//
//   var r = ReachPrompt.Show(thing.transform, "Pull", Glyph.Action(GameAction.Interact),
//                            onInteract: () => Pull());                 // attach to a MOVING anchor
//   var r = ReachPrompt.Create(worldPos, "Pull the lever", glyph, onInteract: () => Pull());  // fixed point
//   ... later ...  r.Destroy();
//
// Built as a plain MonoBehaviour over WorldPrompt (NOT an injected InteractionProvider — Il2CppInterop cannot
// inject an InteractionProvider subclass: its base virtuals take the nested type InteractionProvider.InvalidReason,
// which RegisterTypeInIl2Cpp fails to resolve). So the prompt + glyph render like every other CairnAPI prompt.
public static class ReachPrompt
{
    /// <summary>
    /// Attach a reach prompt to a MOVING <paramref name="anchor"/> (e.g. the climbot, an NPC, a carried prop).
    /// Parented to the anchor, so it tracks it automatically — pass the transform of the thing the player
    /// reaches toward. Shows a <see cref="WorldPrompt"/> with <paramref name="text"/> + <paramref name="glyph"/>
    /// while a limb is REACHING into <paramref name="radius"/>; <paramref name="onInteract"/> fires on the
    /// glyph's button while reaching. <paramref name="localOffset"/> shifts the trigger from the anchor's pivot
    /// to the actual grab point. <paramref name="hands"/> true = only hands trigger it; false = any limb.
    /// </summary>
    public static ReachInteractable Show(
        Transform anchor, string text, InputAction glyph = null, Action onInteract = null,
        float radius = 0.35f, Vector3 localOffset = default, bool hands = true)
    {
        if (anchor == null) return null;
        var comp = Build(text, glyph, onInteract, radius, hands);
        comp.transform.SetParent(anchor, false);     // track the moving anchor
        comp.transform.localPosition = localOffset;
        return comp;
    }

    /// <summary>
    /// Spawn a reach prompt at a FIXED world <paramref name="position"/> (a lever, a wall panel — something that
    /// doesn't move). For anything that moves, use <see cref="Show"/> instead.
    /// </summary>
    public static ReachInteractable Create(
        Vector3 position, string text, InputAction glyph = null, Action onInteract = null,
        float radius = 0.35f, bool hands = true)
    {
        var comp = Build(text, glyph, onInteract, radius, hands);
        comp.transform.position = position;
        return comp;
    }

    private static ReachInteractable Build(string text, InputAction glyph, Action onInteract, float radius, bool hands)
    {
        ReachInteractable.EnsureRegistered();
        var go = new GameObject("CairnAPI.ReachPrompt");
        int layer = LayerMask.NameToLayer("Interactions");   // sensors live on layer 16; must match to overlap
        if (layer >= 0) go.layer = layer;
        var sphere = go.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = radius;
        var comp = go.AddComponent<ReachInteractable>();
        comp.Configure(text ?? string.Empty, glyph, onInteract, hands);
        return comp;
    }
}

/// <summary>
/// The MonoBehaviour behind <see cref="ReachPrompt.Show"/> / <see cref="ReachPrompt.Create"/>. Trigger-driven:
/// a limb sensor reaching into our collider shows a <see cref="WorldPrompt"/>; leaving hides it; pressing the
/// glyph button while reaching fires the callback.
/// </summary>
public class ReachInteractable : MonoBehaviour
{
    public ReachInteractable(IntPtr ptr) : base(ptr) { }
    public ReachInteractable() : base(ClassInjector.DerivedConstructorPointer<ReachInteractable>())
        => ClassInjector.DerivedConstructorBody(this);

    private string _text = "";
    private InputAction _glyph;
    private Action _onInteract;
    private bool _handsOnly = true;
    private WorldPromptHandle _prompt = WorldPromptHandle.Invalid;
    private int _reachCount;   // number of limb sensors currently overlapping (reaching into) us

    internal void Configure(string text, InputAction glyph, Action onInteract, bool handsOnly)
    {
        _text = text;
        _glyph = glyph;
        _onInteract = onInteract;
        _handsOnly = handsOnly;
        try { if (glyph != null && !glyph.enabled) glyph.Enable(); } catch { }
    }

    private void OnTriggerEnter(Collider other)
    {
        try { if (IsWantedSensor(other)) _reachCount++; } catch { }
    }

    private void OnTriggerExit(Collider other)
    {
        try { if (IsWantedSensor(other) && --_reachCount < 0) _reachCount = 0; } catch { }
    }

    // A HAND sensor overlapping us IS the reach signal: a hand sensor only reaches the target when the hand
    // physically reaches it. So we gate purely on "a wanted (hand) sensor is overlapping" — no global
    // reach-state check (that was wrong: it returned true when ANY hand reached ANYWHERE, even with feet
    // overlapping this target). The callback fires on the glyph's button while a wanted sensor overlaps.
    private void Update()
    {
        try
        {
            // Climbing-only: a hand-reach affordance belongs to the wall, not the ground. Gate on the game's
            // authoritative locomotion mode — PawnControllerSwitcher.CurrentMode == Climbing(2). Enum (dump.cs):
            // None=0, Walking=1, Climbing=2, Flying=3, Hovering=4. So a hand sensor overlapping us on the GROUND
            // (Walking) never shows the prompt; only a real on-wall reach does.
            bool reaching = _reachCount > 0 && IsClimbing();
            bool shown = _prompt != null && _prompt.Valid;
            // In-world prompt anchored AT the interactable (points at the target), gated by hand-reach.
            if (reaching && !shown) _prompt = WorldPrompt.Show(transform, _text, _glyph);
            else if (!reaching && shown) Hide();

            if (reaching && _glyph != null && _glyph.triggered)
                _onInteract?.Invoke();
        }
        catch (Exception e) { MelonLogger.Error("[CairnAPI] ReachPrompt.Update: " + e); }
    }

    // True only while Aava is on the wall (Climbing mode) — the game's own locomotion FSM, not a heuristic.
    // PawnManager.PawnControllerSwitcher.CurrentMode == Climbing(2). Defaults to NOT climbing on any null.
    private static bool IsClimbing()
    {
        try
        {
            if (!Il2Cpp.PawnManager.MCSpawned) return false;
            var pm = Il2Cpp.MoSingleton<Il2Cpp.PawnManager>.Instance;
            var sw = pm != null ? pm.PawnControllerSwitcher : null;
            return sw != null && (int)sw.CurrentMode == 2;   // Climbing
        }
        catch { return false; }
    }

    // Only HAND sensors (type flag Hands=1) count when handsOnly; otherwise any limb sensor. Feet/Walking/
    // Offbelay/Rappel sensors are explicitly excluded in hands mode (they overlap a nearby target constantly).
    private bool IsWantedSensor(Collider other)
    {
        if (other == null) return false;
        var sensor = other.GetComponent<Il2Cpp.PawnInteractionSensor>();
        if (sensor == null) return false;
        if (!_handsOnly) return true;
        // EXACT Hands (==1), not a bitwise & : composite sensors (Walking type=523, Offbelay=35) include the
        // Hands bit but aren't hand sensors. Only the pure hand sensors report type == Hands(1) (dump.cs:97708).
        return (int)sensor.type == 1;
    }

    private void Hide()
    {
        WorldPrompt.Hide(_prompt);
        _prompt = WorldPromptHandle.Invalid;
    }

    /// <summary>Remove this prompt and its GameObject.</summary>
    public void Destroy()
    {
        Hide();
        try { UnityEngine.Object.Destroy(gameObject); } catch { }
    }

    private void OnDestroy() => WorldPrompt.Hide(_prompt);

    private static bool _registered;
    internal static void EnsureRegistered()
    {
        if (_registered) return;
        ClassInjector.RegisterTypeInIl2Cpp<ReachInteractable>();
        _registered = true;
    }
}
