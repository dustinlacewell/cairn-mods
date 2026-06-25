using System;
using Il2Cpp;                       // PawnManager, MoSingleton
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using MelonLoader;

namespace CairnAPI;

// A WORLD prompt gated by Aava being within distance D of the anchor. There is NO engine "auto-prompt within
// range D" to reuse — the only in-range notion (InteractionProvider.IsPlayerInReach) is itself trigger-overlap
// driven, not distance — so a per-frame Vector3.Distance(aavaPos, anchor) < D gate is both simplest and correct
// (DESIGN §5). Live Aava position is the canonical MoSingleton<PawnManager>.Instance.MCGameObject.transform
// .position, guarded by PawnManager.MCSpawned (the pattern in CairnRoutes/Core.cs:112-120).
//
//   var p = ProximityPrompt.Show(thing.transform, 3f, "Examine", Glyph.Key("e"),
//                                onInteract: () => Examine());
//   ... later ...  p.Destroy();
public static class ProximityPrompt
{
    /// <summary>
    /// Show a <see cref="WorldPrompt"/> over <paramref name="anchor"/> while Aava is within
    /// <paramref name="distance"/> of it. <paramref name="onInteract"/> fires on the glyph's button while in
    /// range. Returns the component; call <see cref="ProximityInteractable.Destroy"/> to remove it.
    /// </summary>
    public static ProximityInteractable Show(
        Transform anchor, float distance, string text, InputAction glyph = null, Action onInteract = null)
    {
        if (anchor == null) return null;
        ProximityInteractable.EnsureRegistered();
        var go = new GameObject("CairnAPI.ProximityPrompt");
        go.transform.SetParent(anchor, false);     // ride the anchor so its transform is the prompt's anchor
        var comp = go.AddComponent<ProximityInteractable>();
        comp.Configure(anchor, distance, text ?? string.Empty, glyph, onInteract);
        return comp;
    }
}

/// <summary>
/// The MonoBehaviour behind <see cref="ProximityPrompt.Show"/>. Polls Aava's distance to the anchor each frame:
/// in range → shows a <see cref="WorldPrompt"/>; out of range → hides it; pressing the glyph button while in
/// range fires the callback.
/// </summary>
public class ProximityInteractable : MonoBehaviour
{
    public ProximityInteractable(IntPtr ptr) : base(ptr) { }
    public ProximityInteractable() : base(ClassInjector.DerivedConstructorPointer<ProximityInteractable>())
        => ClassInjector.DerivedConstructorBody(this);

    private Transform _anchor;
    private float _distance = 3f;
    private string _text = "";
    private InputAction _glyph;
    private Action _onInteract;
    private WorldPromptHandle _prompt = WorldPromptHandle.Invalid;

    internal void Configure(Transform anchor, float distance, string text, InputAction glyph, Action onInteract)
    {
        _anchor = anchor;
        _distance = distance;
        _text = text;
        _glyph = glyph;
        _onInteract = onInteract;
        try { if (glyph != null && !glyph.enabled) glyph.Enable(); } catch { }
    }

    private void Update()
    {
        try
        {
            // Grounded-only: a proximity affordance belongs to ground traversal, not the wall. Gate on the game's
            // authoritative locomotion mode — PawnControllerSwitcher.CurrentMode == Walking(1). Enum (dump.cs):
            // None=0, Walking=1, Climbing=2, Flying=3, Hovering=4. So while climbing/rappelling the prompt never
            // shows (and hides if Aava leaves the ground while in range).
            bool inRange = IsGrounded()
                        && _anchor != null && TryAavaPos(out var aava)
                        && Vector3.Distance(aava, _anchor.position) < _distance;
            bool shown = _prompt != null && _prompt.Valid;
            if (inRange && !shown) _prompt = WorldPrompt.Show(_anchor, _text, _glyph);
            else if (!inRange && shown) Hide();

            if (inRange && _glyph != null && _glyph.triggered)
                _onInteract?.Invoke();
        }
        catch (Exception e) { MelonLogger.Error("[CairnAPI] ProximityPrompt.Update: " + e); }
    }

    // True only while Aava is on the ground (Walking mode) — the game's own locomotion FSM, not a heuristic.
    // PawnManager.PawnControllerSwitcher.CurrentMode is the live mode; Walking(1) is on-ground traversal, anything
    // else (Climbing/Flying/Hovering/None) suppresses a proximity prompt. Defaults to NOT grounded on any null.
    private static bool IsGrounded()
    {
        try
        {
            if (!PawnManager.MCSpawned) return false;
            var pm = MoSingleton<PawnManager>.Instance;
            var sw = pm != null ? pm.PawnControllerSwitcher : null;
            return sw != null && (int)sw.CurrentMode == 1;   // Walking
        }
        catch { return false; }
    }

    // Canonical live Aava position (CairnRoutes/Core.cs:112-120). Guard MCSpawned — MCGameObject is null pre-spawn.
    private static bool TryAavaPos(out Vector3 pos)
    {
        pos = default;
        if (!PawnManager.MCSpawned) return false;
        var mc = MoSingleton<PawnManager>.Instance?.MCGameObject;
        if (mc == null) return false;
        pos = mc.transform.position;
        return true;
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
        ClassInjector.RegisterTypeInIl2Cpp<ProximityInteractable>();
        _registered = true;
    }
}
