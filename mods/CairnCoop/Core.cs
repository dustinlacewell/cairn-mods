using System;
using CairnCoop.Steam;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppTheGameBakers.Cairn.Netplay;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CairnCoop.Core), "CairnCoop", "0.2.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]
// Load CrossMenuLib first: the rope connect/disconnect action lives on its LT+RT wheel.
[assembly: MelonOptionalDependencies("CrossMenuLib")]

namespace CairnCoop;

/// <summary>
/// Steam co-op for Cairn using the game's own shipped-but-disabled multiplayer.
/// One player hosts: their mod runs the dumb relay the native NetplayClient expects,
/// reached locally over UDP 127.0.0.2:14000 and by friends over Steam datagrams.
/// F4 opens the panel.
///
/// Test hooks: CAIRNCOOP_AUTOHOST=1 auto-hosts after boot; CAIRNCOOP_AUTOJOIN=local
/// auto-joins a host running in another instance on this machine (pure loopback, no Steam).
///
/// This is the composition root: it constructs and wires the collaborators (<see cref="CoopLog"/>,
/// <see cref="CoopSession"/>, <see cref="ModRopeRouter"/>, <see cref="GameDriver"/>, <see cref="CoopPanel"/>)
/// and forwards the MelonMod lifecycle to them. No transport / belay / UI logic lives here.
/// </summary>
public class Core : MelonMod
{
    private CoopLog _log;
    private CoopSession _session;
    private ModRopeRouter _modRopeRouter;
    private RopeHandshake _handshake;
    private GameDriver _driver;
    private CoopPanel _panel;
    private RopeRequestHud _ropeHud;
    private TopologyTracker _topology;
    private PartyHud _partyHud;

    public override void OnInitializeMelon()
    {
        Instance = this;
        _log = new CoopLog(LoggerInstance.Msg, LoggerInstance.Warning);

        // Break the driver↔session↔router construction cycle: the session owns the transports the router
        // routes over; the router is the driver's IModChannel; the session drives the driver. Build the
        // session first (no collaborators touched in its ctor), then the router against it, then the driver
        // with the router as its channel, then hand the driver back to the session.
        _session = new CoopSession(_log.Log);
        _modRopeRouter = new ModRopeRouter(_session, _log.Log);
        _session.AttachRouter(_modRopeRouter);
        // The request/accept handshake owns the connection set (replaces proximity auto-rope). It rides the
        // same IModChannel (the router) as the rope-state snapshot, subscribing to its OnRequest in its ctor.
        _handshake = new RopeHandshake(_modRopeRouter, _log.Log);
        _driver = new GameDriver(_log.Log, _modRopeRouter, _handshake); // inject the ROUTER as the channel
        _session.AttachDriver(_driver);

        CoopRevive.StandDownBelay = _driver.StandDownBelayForRespawn; // patch→domain seam, not a service locator
        FallTelemetry.Log = _log.Log;
        RecorderTrace.Log = _log.Log; // full tape-lifecycle control-flow trace (which method drains the rewind tape)
        ReviveSyncTrace.Log = _log.Log; // revive-sync trace: partner-ghost mode/pos + Resurrection lifecycle through a revive
        TapeGuard.Log = _log.Log; // ground-recovery fix: suppress the standalone time-skip tape Clear in the fall→revive window
        ReviveStateGuard.Log = _log.Log; // revive-desync fix: advertise last-valid PawnState (not Invalid) to the partner during the auto-rewind
        GhostRopeGesture.Log = _log.Log; // diegetic rope request/accept prompt on a partner ghost at clientState=3
        GhostRopeGesture.Handshake = _handshake; // the gesture reads/drives the connection handshake per ghost
        // An incoming request lights the accept-prompt on the requester's ghost; the gesture reads handshake
        // state each frame, so this subscription is for logging/observability (and a future HUD hook).
        _handshake.OnIncomingRequest += id => _log.Log($"rope: #{id} wants to rope up — approach/reach their ghost to accept");
        HarmonyInstance.PatchAll(typeof(Core).Assembly); // SocketBindPatch + fall telemetry
        // NOTE: the revive-era instrumentation (RecorderTrace / ReviveSyncTrace / RewindCoroutineInstr) is no
        // longer installed here — it patched per-frame SpendTime and flooded the log. Re-add behind a debug
        // flag if/when working the revive path again (the classes remain; only the Install calls were removed).
        GhostRopeGesture.Install(HarmonyInstance); // ghost colliders[] arm + UpdateProvider interaction inject (request/accept)
        RopeWedge.Register(_driver, _log.Log);

        _panel = new CoopPanel(_driver, _session, _log.Tail);

        // The incoming-rope-request HUD: a uGUI middle-left overlay mirroring the handshake's incoming set.
        // Awareness-only (B still accepts via the diegetic ghost gesture). It resolves requester names from the
        // live roster (gamemode → GetRemotePlayers → ghost.interactionProvider.netPlayerName), so it owns no
        // game-state plumbing of its own beyond this roster accessor.
        _ropeHud = new RopeRequestHud(_handshake, ReadRemoteRoster) { Log = _log.Log };

        // The party / chain HUD: TopologyTracker records every climber's broadcast carry edge off the SAME
        // IModChannel (the router) the snapshot stream uses; PartyHud renders the assembled chain(s). The local
        // climber's own edge isn't a remote broadcast, so the HUD reads it directly (id/carry/name accessors).
        _topology = new TopologyTracker(_modRopeRouter, _log.Log);
        _partyHud = new PartyHud(
            _topology.Snapshot, ReadRemoteRoster,
            LocalRoomId, () => _handshake.MyCarry, Steam.SteamApi.MyName) { Log = _log.Log };

        _log.Log("CairnCoop loaded. F4 for the co-op panel.");
    }

    /// <summary>The live remote-player roster (or null if the session/gamemode isn't up): the HUD's id→name
    /// source. Mirrors the gamemode walk the driver/belay use; fail-soft (null when anything is unresolved).</summary>
    private static Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> ReadRemoteRoster()
    {
        try
        {
            var gamemode = MoSingleton<NetplayManager>.Instance?.Gamemode?.TryCast<SharedRopeGamemode>();
            return gamemode != null ? gamemode.GetRemotePlayers() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The local climber's room id (the gamemode's LocalId), or -1 when no session is up. The party HUD
    /// needs it to place the local row and merge the local carry edge into the room-wide graph.</summary>
    private static int LocalRoomId()
    {
        try
        {
            var gamemode = MoSingleton<NetplayManager>.Instance?.Gamemode?.TryCast<SharedRopeGamemode>();
            return gamemode != null ? gamemode.LocalId : -1;
        }
        catch (Exception) { return -1; }
    }

    /// <summary>Reachable from CairnDevTools console eval: CairnCoop.Core.Instance.Driver…</summary>
    public static Core Instance { get; private set; }

    public GameDriver Driver => _driver;

    /// <summary>HOST-ONLY kick, reachable from the F4 panel and CairnDevTools eval
    /// (CairnCoop.Core.Instance.Kick(id)). Delegates to the session, which evicts + bans the member at the relay
    /// (no-op when not hosting). The target id is a room id (the same id the party HUD / roster shows).</summary>
    public void Kick(int targetId) => _session?.Kick(targetId);

    /// <summary>TEST/diagnostic (eval): the host relay's current member list — "members=&lt;n&gt;[ #id name … ]"
    /// — so the kick test can watch the roster shrink. Empty/"members=0" when not hosting.</summary>
    public string MemberCountDiag()
    {
        var members = _session != null ? _session.RelayMembers() : new System.Collections.Generic.List<Net.RelayServer.Member>();
        var sb = new System.Text.StringBuilder();
        sb.Append("members=" + members.Count + "[ ");
        foreach (var m in members)
            sb.Append("#" + m.Id + " " + m.Name + (m.IsOwner ? "(host)" : "") + " ");
        sb.Append("]");
        return sb.ToString();
    }

    /// <summary>TEST/diagnostic (eval: CairnCoop.Core.Instance.PartyDiag()): the room-wide carry graph the
    /// party HUD assembles — every climber's broadcast carry edge (sender→carry) PLUS the local edge merged
    /// in from MyCarry. Format: "local=&lt;id&gt; carry=&lt;c&gt; | edges[ id→carry … ]". Lets the autonomous
    /// loop confirm the topology replicates room-wide without seeing the rendered canvas.</summary>
    public string PartyDiag()
    {
        try
        {
            var nodes = _topology != null ? _topology.Snapshot(DateTime.UtcNow) : new System.Collections.Generic.Dictionary<int, TopologyTracker.Node>();
            int me = LocalRoomId();
            var sb = new System.Text.StringBuilder();
            sb.Append("local=" + me + " carry=" + (_handshake != null ? _handshake.MyCarry : -1) + " | edges[ ");
            foreach (var kv in nodes)
                sb.Append(kv.Key + "→" + kv.Value.Carry + (kv.Value.Hanging ? "(hang)" : "") + " ");
            sb.Append("]");
            return sb.ToString();
        }
        catch (Exception e) { return "err:" + e.Message; }
    }

    /// <summary>The request/accept rope handshake (connection set). Reachable from the CairnDevTools console
    /// for ad-hoc request/accept during testing: CairnCoop.Core.Instance.Handshake…</summary>
    internal RopeHandshake Handshake => _handshake;

    /// <summary>F10 / CairnDevTools console eval: recover a leaked MainCamera blur-and-freeze or a stuck
    /// BlackScreen fade overlay. Implementation in <see cref="ScreenRecovery"/>.</summary>
    public void ForceUnfreeze() => ScreenRecovery.ForceUnfreeze(_log.Log);

    public override void OnUpdate()
    {
        // Hitch tracker: the "smooth motion, then a big jump" remote-pose symptom is the
        // signature of long frames (interpolation starves, then snaps). Record the worst
        // frame per snapshot window so the 10 s log shows whether and how hard we hitch.
        float dt = Time.unscaledDeltaTime;
        if (dt > _worstFrameDelta)
            _worstFrameDelta = dt;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.f4Key.wasPressedThisFrame)
                _panel.Visible = !_panel.Visible;
            if (Keyboard.current.f10Key.wasPressedThisFrame)
                ForceUnfreeze();
            // Rope connect/disconnect is the LT+RT up-wedge (CrossMenu), not a key.
        }

        // Run side-effects the network thread marshalled over (rope-state apply, rendezvous PeerSeen).
        _session.DrainToMain();

        // Age out stale rope requests (incoming AND outgoing, symmetrically) and refresh the request HUD.
        // Each-frame is fine and keeps the countdown responsive; both are cheap small-set sweeps and fail-open.
        _handshake.ExpireStale(Time.realtimeSinceStartup);
        _ropeHud.Tick();
        _partyHud.Tick(); // render the room-wide rope chain(s) from the assembled carry graph

        _session.TickAutostart(Time.realtimeSinceStartup);

        // Steam friend DISCOVERY + the rendezvous timeout sweep stay on the main thread (they touch the
        // friends API + rich presence + fire join/leave handlers). The DATAGRAM pump — sockets, relay,
        // resend/timeout — runs on the net thread; see CoopSession.PumpNet. That separation is the whole
        // fix: a multi-second main-thread freeze (a rewind) no longer freezes the relay and drops the room.
        _session.TickRendezvous(scanForFriends: _panel.Visible && !_session.Active);

        if (_session.Active)
        {
            _driver.Tick();
            if (DateTime.UtcNow >= _nextSnapshot)
            {
                _nextSnapshot = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                // Periodic heartbeat: driver state + the worst frame seen this window. Must stay CHEAP —
                // it runs on the game thread during play. DescribeGameState() does whole-scene
                // FindObjectsOfType sweeps (renderers, ropes, remotes); it was the ~120 ms–1 s hitch
                // every 10 s, so it now runs ONLY on-demand from the F4 Game tab, never on this path.
                string snap = $"[{DateTime.Now:HH:mm:ss.fff}] -- {_driver.Describe()} | "
                    + $"worstFrame:{_worstFrameDelta * 1000f:0}ms";
                _log.WriteFileLine(snap);
                _log.Log($"perf: worstFrame:{_worstFrameDelta * 1000f:0}ms");
                _worstFrameDelta = 0f;
            }
        }

        if (_panel.Visible)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    private DateTime _nextSnapshot;
    private float _worstFrameDelta;

    public override void OnApplicationQuit()
    {
        _session?.Dispose(); // stops the net thread before we close the log it writes to
        _session = null;
        _log?.Dispose();
        _log = null;
    }

    public override void OnGUI() => _panel?.OnGUI();
}
