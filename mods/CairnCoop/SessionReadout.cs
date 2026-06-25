using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// Read-only native-state dumps for the F4 "Game" tab and console eval: roster, gamemode, prefab,
/// spawners, spawned remotes, the catch-chain, the rope census, camera state. PURE diagnostics —
/// every method here only READS and formats; nothing drives the session. Extracted out of
/// <see cref="GameDriver"/> so the state machine isn't drowned by ~240 lines of readout.
///
/// NOTE: this is where the whole-scene <c>FindObjectsOfType</c> sweeps live (remotes, ropes,
/// renderers). They are ~12 ms–1 s each in this game, so they MUST stay strictly on-demand
/// (F4-tab refresh / console eval) and NEVER run on a per-frame or per-tick cadence.
/// </summary>
internal static class SessionReadout
{
    /// <summary>
    /// Native-side truth for the Game tab: roster, gamemode, prefab, spawners, spawned remotes.
    /// Pinpoints where the chain roster → connect → gamemode → spawn breaks.
    /// </summary>
    public static List<string> Describe(PartnerBelay belay)
    {
        var lines = new List<string>();
        try
        {
            var manager = MoSingleton<NetplayManager>.Instance;
            var client = manager?.client;
            if (client == null)
            {
                lines.Add("no native client");
                return lines;
            }
            bool registered = client.State is NetplayClient.StateEnum.REGISTERED
                or NetplayClient.StateEnum.PENDING_ROOM or NetplayClient.StateEnum.IN_ROOM;
            lines.Add($"client: {client.State}" + (registered ? $", local id #{client.LocalId}" : ""));
            if (!registered)
                return lines;

            var room = client.Room;
            lines.Add($"room: owner #{room.owner}, {room.gamemode}, {room.frequency} Hz");
            var members = room.members;
            if (members != null)
            {
                lines.Add($"members: {members.Length}");
                foreach (var member in members)
                {
                    var info = member.client;
                    lines.Add($"  #{info.id} '{info.name}' frames[{member.frames?.Length ?? 0}]");
                }
            }
            else
            {
                lines.Add("members: (none)");
            }

            lines.Add($"gamemode active: {(manager.Gamemode != null ? manager.Gamemode.GetType().Name : "NONE")}");
            lines.Add($"climber prefab: {(manager.NetplayClimberPrefab != null ? "loaded" : "NOT loaded")}");
            var spawners = manager.GetSpawnersForGamemode(NetplayManager.GamemodeEnum.SHARED_ROPE);
            lines.Add($"shared-rope spawners in scene: {spawners?.Count ?? 0}");
            var ropeGamemode = manager.Gamemode?.TryCast<SharedRopeGamemode>();
            if (ropeGamemode != null)
                lines.Add(DescribeSharedRope(ropeGamemode));
            var remotes = UnityEngine.Object.FindObjectsOfType<NetplayRemotePlayer>();
            lines.Add($"remote players spawned: {remotes.Length}");
            foreach (var remote in remotes)
            {
                var visual = remote.transform.Find("Visual");
                string pawn;
                try { pawn = $" pawn:{remote.PawnState} firm:{remote.IsHoldingFirmly}"; }
                catch (Exception e) { pawn = $" pawn:? ({e.Message})"; }
                lines.Add($"  '{remote.gameObject.name}' pos {remote.transform.position}"
                    + $" visual:{(visual != null ? (visual.gameObject.activeSelf ? "on" : "OFF") : "missing")}"
                    + pawn);
            }
            lines.Add($"location: {CurrentLocation() ?? "(none)"}");
            if (ropeGamemode != null)
            {
                lines.Add(DescribeAnchorState(client, ropeGamemode, belay));
                lines.Add(DescribeAllRopes(belay));
            }
            lines.Add(DescribeCameraState());
        }
        catch (Exception e)
        {
            lines.Add("diag error: " + e.Message);
        }
        return lines;
    }

    /// <summary>
    /// Shared-rope truth: the settings that govern attach range and fall catch (ropeLengthMeters is
    /// the free-fall payout before the rope goes taut), plus who is on the local rope.
    /// </summary>
    private static string DescribeSharedRope(SharedRopeGamemode gamemode)
    {
        try
        {
            var settings = gamemode.Settings;
            string members;
            try
            {
                var list = gamemode.GetLocalRopeMembers();
                var ids = new List<string>();
                for (int i = 0; i < list.Count; i++)
                    ids.Add(list[i].ToString());
                members = "[" + string.Join(",", ids) + "]";
            }
            catch (Exception e)
            {
                members = "? (" + e.Message + ")";
            }
            return $"shared-rope: ropeLen {settings.ropeLengthMeters}m"
                + $" recover {settings.recoverPitonDistanceMeters}m"
                + $" interact {settings.interactionDistanceMeters}m"
                + $" corpseMalus {settings.staminaMalusPerCorpse}"
                + $" | local rope members: {members}";
        }
        catch (Exception e)
        {
            return "shared-rope diag error: " + e.Message;
        }
    }

    /// <summary>
    /// Catch-chain ground truth: each step of the resolution that IsSecured performs at fall time
    /// (FallInternal 0x1828F6F80 → harness.get_Lifeline virtual → securingRope → GetRopeHolderCount),
    /// plus whether our anchor quickdraw is actually attached. This is the chain that silently broke
    /// twice; never trim it.
    /// </summary>
    private static string DescribeAnchorState(NetplayClient client, SharedRopeGamemode gamemode, PartnerBelay belay)
    {
        try
        {
            var controller = ClimberQuery.FindLocalClimbController(gamemode, client.LocalId);
            if (controller == null)
                return "anchor: local climb controller NOT FOUND";
            var harness = controller.harness;
            if (harness == null)
                return "anchor: controller has no harness";
            Lifeline lifeline;
            try { lifeline = harness.Lifeline; }
            catch (Exception e) { return "anchor: harness.Lifeline threw: " + e.Message; }
            if (lifeline == null)
                return "anchor: locally-relevant lifeline is NULL (not in rope dict?)";
            var rope = lifeline.securingRope;
            if (rope == null)
                return "anchor: lifeline.securingRope is NULL";
            string ropeState;
            try
            {
                ropeState = $"rope active:{rope.gameObject.activeInHierarchy}"
                    + $" init:{rope.IsInitialized} len:{rope.GetLength():0.#}m"
                    + $" holders:{rope.GetRopeHolderGlobalCount()}"
                    + $" holdingPitons:{rope.GetNumberOfHoldingPiton()}";
            }
            catch (Exception e) { ropeState = "rope probe threw: " + e.Message; }
            string secured;
            try { secured = harness.IsSecured(1).ToString(); }
            catch (Exception e) { secured = "? (" + e.Message + ")"; }
            string anchorAttach = "none";
            var ourQuickdraw = belay.Quickdraw;
            if (ourQuickdraw != null)
            {
                try { anchorAttach = rope.IsAttached(ourQuickdraw.Cast<IRopeHolder>()) ? "ATTACHED" : "NOT attached"; }
                catch (Exception e) { anchorAttach = "? (" + e.Message + ")"; }
            }
            return $"anchor: IsSecured:{secured} | {ropeState} | our quickdraw: {anchorAttach}";
        }
        catch (Exception e)
        {
            return "anchor diag error: " + e.Message;
        }
    }

    /// <summary>
    /// Census of every active LogicalRope: which objects exist, where they sit, how long they
    /// measure, and whether they render. The "rope into outer space" reports could not be attributed
    /// without object-level identity.
    /// </summary>
    private static string DescribeAllRopes(PartnerBelay belay)
    {
        try
        {
            var parts = new List<string>();
            foreach (var rope in UnityEngine.Object.FindObjectsOfType<LogicalRope>())
            {
                string entry;
                try
                {
                    entry = $"'{rope.gameObject.name}'@{rope.transform.position:F0}"
                        + $" len:{rope.GetLength():0.#} vis:{(rope.Visible ? "y" : "n")}"
                        + $" holders:{rope.GetRopeHolderGlobalCount()}"
                        + (belay.Rope != null && rope.Pointer == belay.Rope.Pointer ? " [OURS]" : "");
                }
                catch (Exception e) { entry = $"'{rope.gameObject.name}' probe failed: {e.Message}"; }
                parts.Add(entry);
            }
            return "ropes: " + (parts.Count == 0 ? "(none active)" : string.Join(" | ", parts));
        }
        catch (Exception e)
        {
            return "ropes diag error: " + e.Message;
        }
    }

    /// <summary>
    /// Render-side truth: main camera, live Cinemachine vcam, timescale. The joiner instance loses
    /// every shared game log (Player.log, TGBTools.*) to the host's file locks, so this snapshot line
    /// is the only visibility into its camera state.
    /// </summary>
    private static string DescribeCameraState()
    {
        try
        {
            var main = Camera.main;
            string mainDesc = main == null
                ? "Camera.main: NONE"
                : $"Camera.main: pos {main.transform.position} enabled:{main.enabled}"
                    + $" active:{main.gameObject.activeInHierarchy} cull:0x{main.cullingMask:X}";

            string vcam;
            try
            {
                var brain = Unity.Cinemachine.CinemachineBrain.GetActiveBrain(0);
                var live = brain != null ? brain.ActiveVirtualCamera : null;
                if (live == null)
                    vcam = $"live vcam: NONE (brain {(brain != null ? "present" : "MISSING")})";
                else
                {
                    var behaviour = (live as Il2CppObjectBase)?.TryCast<MonoBehaviour>();
                    vcam = $"live vcam: '{(behaviour != null ? behaviour.gameObject.name : live.Name)}'";
                }
            }
            catch (Exception e)
            {
                vcam = "live vcam: ? (" + e.Message + ")";
            }

            string frozen;
            try { frozen = MainCamera.IsMainCameraRenderingBlurAndFreeze ? "FROZEN" : "no"; }
            catch (Exception e) { frozen = "? (" + e.Message + ")"; }

            string blackScreen;
            try
            {
                var overlay = UnityEngine.Object.FindObjectOfType<BlackScreen>();
                blackScreen = overlay != null
                    ? $"{overlay.GetCurrAlpha():0.##}->{overlay.GetTargetAlpha():0.##}"
                    : "none";
            }
            catch (Exception e) { blackScreen = "? (" + e.Message + ")"; }

            return $"{mainDesc} | {vcam} | cameras:{Camera.allCamerasCount}"
                + $" | timeScale:{Time.timeScale:0.##} | blurFreeze:{frozen} | blackScreen:{blackScreen}";
        }
        catch (Exception e)
        {
            return "camera diag error: " + e.Message;
        }
    }

    private static string CurrentLocation()
    {
        try { return MoSingleton<CairnSceneManager>.Instance?.GetMultiSceneLocationString(); }
        catch { return null; }
    }
}
