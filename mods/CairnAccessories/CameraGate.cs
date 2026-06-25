using Il2CppTGBTools.PhotoMode;

namespace CairnAccessories;

/// <summary>
/// Reactive bridge to the game's PHOTO MODE. The player opens photo mode normally — it already
/// frees the cursor for UI and gates camera LOOK behind right-mouse with the game's own
/// sensitivity (Camera.Rotation is a OneModifier composite: &lt;Mouse&gt;/rightButton +
/// &lt;Mouse&gt;/delta). So we do NOT touch input or the cursor at all. We only observe whether
/// photo mode is open, which drives whether the accessory editor panel is shown.
///
/// PhotoModeManager exposes static OnOpened / OnClosed (Action) events — we subscribe once and let
/// them flip <see cref="Active"/>.
/// </summary>
public sealed class CameraGate
{
    private bool _subscribed;

    /// <summary>True while the game's photo mode is open — drives panel visibility.</summary>
    public bool Active { get; private set; }

    /// <summary>Subscribe to photo-mode open/close once the IL2CPP domain is up. Idempotent.</summary>
    public void Subscribe()
    {
        if (_subscribed) return;
        PhotoModeManager.add_OnOpened((Il2CppSystem.Action)OnPhotoOpened);
        PhotoModeManager.add_OnClosed((Il2CppSystem.Action)OnPhotoClosed);
        _subscribed = true;
    }

    private void OnPhotoOpened() => Active = true;
    private void OnPhotoClosed() => Active = false;
}
