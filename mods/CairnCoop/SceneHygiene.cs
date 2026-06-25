using System;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// Per-tick scene cleanup that keeps the co-op session looking right once the physics moved onto the
/// personal belay rope: park/hide the now-cosmetic netplay ropes, and make remote players render
/// where they actually are. Pure visual/parking hygiene — no belay or netplay logic. Driven by
/// <see cref="GameDriver"/> at 1 Hz; <see cref="PartnerBelay"/> supplies the one rope to spare.
/// </summary>
internal sealed class SceneHygiene
{
    private readonly Action<string> _log;

    internal SceneHygiene(Action<string> log) => _log = log;

    /// <summary>
    /// All Net* ropes are cosmetic-or-worse now that the physics lives on the personal rope. The
    /// holderless ones streak to the prefab staging spot ("rope into outer space"): hide and re-park
    /// them. The held ones (the native pair rope between players — 'NetplaySpareLogicalRope' locally,
    /// 'NetLogicalRope' on the ghost) are born with parked particle geometry and draw the same
    /// streaks, and nobody manages their length anymore: hide them too, but leave them ACTIVE (the
    /// attach state machine owns their lifecycle). Each player still sees a rope to their partner:
    /// their own personal rope threading the anchor quickdraw. Scene/cutscene ropes are left alone.
    /// </summary>
    internal void TickStrayRopeCleanup(LogicalRope spareRope)
    {
        try
        {
            foreach (var rope in UnityEngine.Object.FindObjectsOfType<LogicalRope>())
            {
                if (!rope.gameObject.name.StartsWith("Net"))
                    continue;
                if (spareRope != null && rope.Pointer == spareRope.Pointer)
                    continue;
                if (rope.GetRopeHolderGlobalCount() == 0)
                {
                    if (!rope.Visible && !rope.gameObject.activeSelf)
                        continue;
                    // Hide BEFORE deactivating: the LogicalRopeRenderer lives on its own object and
                    // otherwise keeps drawing the last geometry.
                    rope.SetVisible(false);
                    rope.gameObject.SetActive(false);
                    _log($"hygiene: parked stray netplay rope '{rope.gameObject.name}' (zero holders)");
                }
                else if (rope.Visible)
                {
                    rope.SetVisible(false);
                    _log($"hygiene: hid cosmetic netplay rope '{rope.gameObject.name}' (physics is on the personal rope)");
                }
            }
        }
        catch (Exception)
        {
            // census-style sweep; never let it disturb the session
        }
    }

    /// <summary>
    /// The ghost prefab ships with its "Visual" and "NetplayClimbot" children DISABLED; the native
    /// spawn path leaves them off, so the remote player exists and receives frames but renders
    /// nothing. Flip them on. Also: frames drive the ghost's BONES in world space but never its
    /// transforms — the root stays parked at the prefab staging spot, so every rope anchored to it
    /// spans hundreds of metres ("ropes into outer space"). Keep the root at the frame position, and
    /// keep the ghost climbot's belay ropes invisible (mirroring the partner's hidden local copy).
    /// Driven off the gamemode's authoritative remote-player roster (<c>GetRemotePlayers()</c>),
    /// supplied by <see cref="GameDriver"/> — not a per-tick FindObjectsOfType scan.
    /// </summary>
    internal void EnsureRemotePlayersVisible(Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> remotes)
    {
        for (int i = 0; i < remotes.Count; i++)
        {
            var remote = remotes[i];
            if (remote == null)
                continue;
            var root = remote.transform;
            ActivateChild(remote, root, "Visual");
            // The ghost climbot belongs (vanilla SetFrame re-activates it every frame — deactivating
            // it is a losing tug-of-war). What does NOT belong is its belay ROPE: the ghost's copy
            // renders with garbage particle positions (the "rope into outer space" streak). Mirror
            // the partner's state: keep ghost climbot ropes invisible.
            var climbot = root.Find("NetplayClimbot");
            if (climbot != null && climbot.gameObject.activeInHierarchy)
            {
                foreach (var rope in climbot.GetComponentsInChildren<LogicalRope>(true))
                {
                    if (!rope.Visible)
                        continue;
                    rope.SetVisible(false);
                    _log($"hygiene: hid ghost climbot rope under '{remote.gameObject.name}'");
                }
            }
            try
            {
                Vector3 framePos = remote.Position;
                if (framePos != default && (framePos - root.position).sqrMagnitude > 4f)
                {
                    root.position = framePos;
                    _log($"hygiene: moved ghost root '{remote.gameObject.name}' to frame position {framePos:F1}");
                }
            }
            catch (Exception) { } // no frame yet
        }
    }

    private void ActivateChild(NetplayRemotePlayer remote, Transform root, string childName)
    {
        var child = root.Find(childName);
        if (child == null || child.gameObject.activeSelf)
            return;
        child.gameObject.SetActive(true);
        _log($"hygiene: activated '{childName}' on remote player '{remote.gameObject.name}' at {root.position}");
    }
}
