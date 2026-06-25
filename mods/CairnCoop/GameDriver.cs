using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// Drives the game's own netplay stack: points NetplayTweakables.serverAddress at the
/// local UDP bridge (127.0.0.2), (re)builds the native NetplayClient through
/// NetplayManager.OnNetplayTweakablesReady, and walks register -> create/join room.
/// All climbing/rope/gamemode logic stays native; this class only starts the session.
/// </summary>
public sealed class GameDriver
{
    public const string RoomCode = "CAIRN";

    public enum Stage { Idle, WaitingForTweakables, BuildingClient, Registering, JoiningRoom, InRoom, Error }

    public Stage State { get; private set; } = Stage.Idle;
    public string Error { get; private set; }

    private readonly Action<string> _log;
    private readonly PartnerBelay _belay;
    private readonly RopeHandshake _handshake;
    private readonly SceneHygiene _hygiene;
    private string _playerName;
    private bool _clientRebuilt;
    private DateTime _joinSentAt;
    private DateTime _nextErrorRetry;

    internal GameDriver(Action<string> log, CairnCoop.Net.IModChannel modChannel, RopeHandshake handshake)
    {
        _log = log;
        _handshake = handshake;
        _belay = new PartnerBelay(log, new BelayRig(log), modChannel, handshake);
        _hygiene = new SceneHygiene(log);
        SharedRopeInitGate.Log = log;
        SharedRopeInitGate.OnInit = OnSharedRopeInit;
    }

    /// <summary>
    /// SharedRopeGamemode.Init tears the local belay rig down on every room join. Re-assert
    /// immediately after, in the same frame, before the torn-down state is observed. (The
    /// join-loop's duplicate-key throw that caused the id-churn is fixed by the gate's Prefix.)
    /// The 1 Hz EnsureBelayIntegrity inside PartnerBelay is only a backstop now. (Guarded so a
    /// restore can't throw out of native code.)
    /// </summary>
    private void OnSharedRopeInit()
    {
        try
        {
            var manager = MoSingleton<NetplayManager>.Instance;
            var gamemode = manager?.Gamemode?.TryCast<SharedRopeGamemode>();
            var client = manager?.client;
            if (gamemode != null && client != null)
                _belay.RestoreRig(gamemode, client.LocalId);
        }
        catch (Exception e)
        {
            _log("belay: post-Init restore failed: " + e.Message);
        }
    }

    public void Connect(string playerName)
    {
        _playerName = string.IsNullOrEmpty(playerName) ? "Climber" : playerName;
        State = Stage.WaitingForTweakables;
    }

    public void Disconnect()
    {
        try
        {
            // SendLeaveRoom gates on the client's own state; the relay answers with
            // STCU_RESET(REQUESTED_BY_USER) which makes the client tear down its
            // gamemode natively, drop to INACTIVE, and re-register on its own.
            MoSingleton<NetplayManager>.Instance?.client?.SendLeaveRoom();
        }
        catch (Exception e)
        {
            _log("driver: leave failed: " + e.Message);
        }
        // Leaving the room stops the InRoom tick that publishes/resets the hang-stamina factor, so
        // clear it here — a session-ended player must never keep a reduced-stamina envelope.
        HangStaminaSeam.Clear();
        // The local-controller cache is keyed on the gamemode's harness pointer, which goes away with
        // the session; drop it so the next session re-resolves against the fresh pawn.
        ClimberQuery.InvalidateLocalControllerCache();
        State = Stage.Idle;
    }

    private DateTime? _outOfLevelSince;
    private DateTime _nextLocationCheck;

    /// <summary>
    /// Rooms are location-bound: SharedRopeGamemode initializes against the location you
    /// were in when the roster landed â€” and it keys ALL rope state by client id, so a
    /// mid-session leave/rejoin re-keys us and poisons every peer's rope graph with our
    /// stale id (stale SharedRope nodes, phantom remotes). Therefore rejoin ONLY on real
    /// level transitions, which always pass through MainMenu/LoadingScreen. In-level
    /// multi-scene string churn (LOD/region streaming while climbing) is ignored entirely.
    /// Leaving the room promptly on menu exit also gives peers a clean OnPlayerLeft.
    /// </summary>
    private void TickLocationRejoin()
    {
        if (State is not (Stage.InRoom or Stage.JoiningRoom) || DateTime.UtcNow < _nextLocationCheck)
            return;
        _nextLocationCheck = DateTime.UtcNow + TimeSpan.FromSeconds(1);

        string location = CurrentLocation();
        bool outOfLevel = string.IsNullOrEmpty(location)
            || location.Contains("MainMenu") || location.Contains("LoadingScreen");
        if (!outOfLevel)
        {
            _outOfLevelSince = null;
            return;
        }
        _outOfLevelSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _outOfLevelSince.Value).TotalSeconds < 2)
            return;

        _outOfLevelSince = null;
        _log("driver: left the level â€” leaving room (rejoins on next level arrival)");
        try { MoSingleton<NetplayManager>.Instance?.client?.SendLeaveRoom(); }
        catch (Exception e) { _log("driver: leave failed: " + e.Message); }
        State = Stage.Registering;
    }

    private static string CurrentLocation()
    {
        try { return MoSingleton<CairnSceneManager>.Instance?.GetMultiSceneLocationString(); }
        catch { return null; }
    }

    /// <summary>True once the local climber pawn (a controller with a harness) is instantiated in
    /// the scene — the precondition base Gamemode.Init needs before CreateJoinRoom can run without
    /// dereferencing a null pawn chain (see the join gate).</summary>
    private static bool LocalClimberSpawned()
    {
        try
        {
            foreach (var c in UnityEngine.Object.FindObjectsOfType<ClimbingV2PawnController>())
                if (c != null && c.harness != null)
                    return true;
        }
        catch (Exception) { }
        return false;
    }

    private DateTime _nextVisibilityPass;
    private DateTime _nextStrayCleanup;
    private int _lastRemoteCount = -1;
    private int _strayCleanupBurst;
    private DateTime _enteredRoomAt;

    /// <summary>
    /// SharedRopeGamemode keeps Dictionary&lt;int, SharedRope&gt; sharedRopes keyed by client id,
    /// populated by Init from the roster and required (it indexes the LOCAL id, throwing
    /// KeyNotFound) by the capture path and every rope verb. Session churn can leave the
    /// local entry missing â€” CanLocalClimberBeAttached does the same throwing lookup, so it
    /// doubles as a health probe; on KeyNotFound, leave+rejoin to force a clean Init.
    /// Grace period: Init runs via the main-thread callback queue a few frames after the
    /// client reaches IN_ROOM, so probing too early false-positives.
    /// </summary>
    private void ProbeGamemodeHealth()
    {
        if ((DateTime.UtcNow - _enteredRoomAt).TotalSeconds < 3)
            return;
        var manager = MoSingleton<NetplayManager>.Instance;
        var gamemode = manager?.Gamemode?.TryCast<SharedRopeGamemode>();
        if (gamemode == null)
            return;
        try
        {
            gamemode.CanLocalClimberBeAttached();
        }
        catch (Exception)
        {
            _log("driver: gamemode missing local climber state â€” rejoining for a clean init");
            try { manager.client?.SendLeaveRoom(); }
            catch (Exception e) { _log("driver: leave failed: " + e.Message); }
            State = Stage.Registering;
        }
    }

    /// <summary>Advance the session state machine. Call every frame while connecting/connected.</summary>
    public void Tick()
    {
        try
        {
            // While a co-op resurrection rewind is in flight, do NOTHING: no room rejoin, no belay
            // reconcile, no hygiene. A transient relay room-drop during the rewind would otherwise
            // make us leave/rejoin (SharedRopeGamemode.Init churn) mid-revive and corrupt the
            // climber. The reconciler rebuilds the anchor on the next tick once the revive lands.
            // CoopRevive.Active is always cleared by ExitRewind (or the coroutine-death fallback that
            // forces ExitRewind), so this can never stick.
            if (CoopRevive.Active)
                return;
            TickLocationRejoin();
            TickInner();
        }
        catch (Exception e)
        {
            Error = e.ToString();
            State = Stage.Error;
            _nextErrorRetry = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            _log("driver: " + e);
        }
    }

    private void TickInner()
    {
        switch (State)
        {
            case Stage.Error:
            {
                // A transient game/netplay exception parked us here. Don't stay stuck: after a short
                // backoff, rebuild the client and re-register — the same path a fresh connect takes.
                if (DateTime.UtcNow < _nextErrorRetry)
                    return;
                _log("driver: recovering from error — rebuilding client");
                Error = null;
                State = Stage.BuildingClient;
                return;
            }

            case Stage.WaitingForTweakables:
            {
                EnsureManager(); // its Init kicks the tweakables load if not already loaded
                var tweakables = TweakableBase<NetplayTweakables>.Instance;
                if (tweakables == null)
                    return; // asset not loaded yet
                NetplayConfig.ConfigureTweakables(tweakables, Net.UdpBridge.BridgeAddress, _playerName);
                _log($"driver: tweakables set (server {Net.UdpBridge.BridgeAddress}, player '{_playerName}', autoRegister on)");
                State = Stage.BuildingClient;
                return;
            }

            case Stage.BuildingClient:
            {
                var manager = EnsureManager();
                if (manager == null)
                    return;

                var client = manager.client;
                if (client != null && (!_clientRebuilt || client.State == NetplayClient.StateEnum.BROKEN))
                {
                    // Built before we repointed serverAddress (boot), or wedged â€” rebuild.
                    _log("driver: disposing stale NetplayClient");
                    try { client.Dispose(); }
                    catch (Exception e) { _log("driver: Dispose threw: " + e.Message); }
                    client = null;
                }
                if (client == null)
                {
                    manager.OnNetplayTweakablesReady();
                    _clientRebuilt = true;
                    client = manager.client;
                    _log("driver: NetplayClient built (server " + Net.UdpBridge.BridgeAddress + ":14000)");
                    if (client == null)
                        return;
                }
                else if (client.State is NetplayClient.StateEnum.IN_ROOM or NetplayClient.StateEnum.PENDING_ROOM)
                {
                    // Leftover session (e.g. reconnect): leave; the relay's reset takes the
                    // client back to INACTIVE and autoRegister starts over.
                    _log("driver: leaving stale room before reconnecting");
                    client.SendLeaveRoom();
                }
                State = Stage.Registering; // autoRegister=true: the client self-registers via SynchronousTick
                return;
            }

            case Stage.Registering:
            {
                var manager = MoSingleton<NetplayManager>.Instance;
                var client = manager?.client;
                if (client == null)
                {
                    State = Stage.BuildingClient;
                    return;
                }
                if (client.State == NetplayClient.StateEnum.REGISTERED)
                {
                    // Never join from the menu/loading: SharedRopeGamemode.Init builds its
                    // per-client state (sharedRopes) from the roster against the CURRENT
                    // scene, and menu-time joins leave it inconsistent (KeyNotFound on the
                    // local id later). Wait until we're standing in a real location.
                    string location = CurrentLocation();
                    if (string.IsNullOrEmpty(location)
                        || location.Contains("MainMenu") || location.Contains("LoadingScreen"))
                        return;
                    // AND wait until the local climber pawn exists. CreateJoinRoom runs
                    // SharedRopeGamemode.Init → base Gamemode.Init, which dereferences the local
                    // player's pawn-state chain (NetplayManager → client → pawn, decompiled
                    // Gamemode_Init_Base.c +0x1c8); joining while the scene is loaded but the pawn
                    // isn't yet instantiated traps with the NRE. Gate on the pawn being present.
                    if (!LocalClimberSpawned())
                        return;
                    _log($"driver: registered, joining room '{RoomCode}' in-level");
                    NetplayConfig.MirrorClientState(manager, client);
                    manager.CreateJoinRoom(RoomCode, NetplayManager.GamemodeEnum.SHARED_ROPE, 8);
                    _joinSentAt = DateTime.UtcNow;
                    State = Stage.JoiningRoom;
                }
                return;
            }

            case Stage.JoiningRoom:
            {
                var client = MoSingleton<NetplayManager>.Instance?.client;
                if (client == null)
                {
                    State = Stage.BuildingClient;
                    return;
                }
                if (client.State == NetplayClient.StateEnum.IN_ROOM)
                {
                    _log("driver: in room â€” co-op session live");
                    _enteredRoomAt = DateTime.UtcNow;
                    _lastRemoteCount = -1; // force one stray-cleanup burst on the first InRoom tick
                    State = Stage.InRoom;
                    // EVENT: co-op room formed (native IN_ROOM). The driver awaits this instead of grepping
                    // the per-instance log for "-- InRoom (native: IN_ROOM)" on a timer.
                    Signal.Emit("room-formed", "");
                }
                else if (client.State == NetplayClient.StateEnum.REGISTERED
                         && (DateTime.UtcNow - _joinSentAt).TotalSeconds > 5)
                {
                    State = Stage.Registering; // join timed out natively; retry
                }
                return;
            }

            case Stage.InRoom:
            {
                var manager = MoSingleton<NetplayManager>.Instance;
                var client = manager?.client;
                if (client == null || client.State != NetplayClient.StateEnum.IN_ROOM)
                {
                    State = Stage.Registering; // dropped; autoRegister + our join loop recover
                    return;
                }
                if (DateTime.UtcNow >= _nextVisibilityPass)
                {
                    _nextVisibilityPass = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // Hold the clientState MIRROR at REGISTERED(3) so NetplayManager.IsInRoom stays FALSE.
                    // PROVEN 2026-06-18 (RewindController.CanRewind, line 101-104): the native game HARD-DISABLES
                    // edelweiss rewind when IsInRoom (clientState>4) → RefusedRewindReason.Netplay. The co-op
                    // revive depends on that rewind, so the mirror MUST stay <5. (The diegetic ghost-interaction
                    // verbs want >=5, but we drive them at the DISPATCH level instead — they don't need the
                    // native isAvailable gate; see re/systems/coop/coop-rope-request-design.md.) Confirmed: a
                    // clientState=5 repro showed CanRewind=False reason=Netplay, both climbers stuck, no prompt.
                    NetplayConfig.WriteClientState(manager, (int)NetplayClient.StateEnum.REGISTERED);
                    long tMirror = sw.ElapsedMilliseconds;
                    // TAPE PROBE: the rewind tape feeds CanRewind's NotEnoughTape gate. Log the LOCAL
                    // recorder's available tape on a meaningful change so the fall→resurrection log shows
                    // when/why it drains (quiet while the rolling buffer sits at its cap).
                    TapeProbe.LogLocalOnChange(_log);
                    // Resolve the gamemode + its authoritative remote roster ONCE: ghost visibility and
                    // partner resolution both read it, and it replaces the old FindObjectsOfType scan.
                    var gamemode = manager.Gamemode?.TryCast<SharedRopeGamemode>();
                    Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> remotes = null;
                    if (gamemode != null)
                        try { remotes = gamemode.GetRemotePlayers(); }
                        catch (Exception) { } // roster not ready; the health probe handles persistent breakage
                    if (remotes != null)
                        _hygiene.EnsureRemotePlayersVisible(remotes);
                    long tVis = sw.ElapsedMilliseconds;
                    ProbeGamemodeHealth();
                    long tProbe = sw.ElapsedMilliseconds;
                    _belay.Tick(gamemode, client);
                    long tAnchor = sw.ElapsedMilliseconds;
                    // Stray cosmetic netplay ropes ("rope into outer space") are spawned ONLY when a
                    // remote ghost appears/disappears — a roster change. A single FindObjectsOfType<
                    // LogicalRope> is ~17 ms here (Unity walks every object in the scene), so a census
                    // on a clock is a dropped frame on a clock. Instead, police strays only on a roster
                    // delta, in a short burst (ropes can instantiate a beat after the roster updates),
                    // and otherwise NEVER scan — so the steady-state tick does zero FindObjectsOfType.
                    int remoteCount = remotes != null ? remotes.Count : 0;
                    if (remoteCount != _lastRemoteCount)
                    {
                        _lastRemoteCount = remoteCount;
                        _strayCleanupBurst = 3;
                        _nextStrayCleanup = DateTime.UtcNow;
                    }
                    if (_strayCleanupBurst > 0 && DateTime.UtcNow >= _nextStrayCleanup)
                    {
                        _strayCleanupBurst--;
                        _nextStrayCleanup = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                        _hygiene.TickStrayRopeCleanup(_belay.Rope);
                    }
                    long tStray = sw.ElapsedMilliseconds;
                    if (tStray > 15)
                        _log($"driver: SLOW 1Hz tick {tStray}ms (mirror:{tMirror} vis:{tVis - tMirror}"
                            + $" probe:{tProbe - tVis} anchor:{tAnchor - tProbe} stray:{tStray - tAnchor})");
                }
                return;
            }
        }
    }

    private NetplayManager EnsureManager()
    {
        var manager = MoSingleton<NetplayManager>.Instance;
        if (manager != null)
            return manager;
        _log("driver: no NetplayManager â€” creating one");
        var go = new GameObject("CairnCoop_NetplayManager");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<NetplayManager>();
    }

    /// <summary>Whether the rope is currently connected to the partner (derived from the accepted-request
    /// handshake). Read by the UI + the LT+RT up-wedge icon.</summary>
    public bool RopeConnected => _belay.Enabled;

    /// <summary>Detach from the WHOLE chain: disconnect every rope this climber is part of (carry above AND
    /// every dependent below). Driven by the LT+RT up-wedge as the non-diegetic "get me out of the rope" action
    /// — connection is formed only by the diegetic ghost reach, so the wedge's job is the inverse. Each peer
    /// drops its side on the Cancel; the reconciler tears our anchors down on the next tick. No-op when unroped.</summary>
    public void UnropeAll() => _belay.UnropeAll();

    /// <summary>TEST/automation entry: complete the rope handshake programmatically (no diegetic ghost
    /// reach). Resolves the live gamemode/client and runs the gesture's request/accept/disconnect logic
    /// against the closest partner. Call on BOTH sides to connect: one sends the request, the other accepts
    /// its incoming. Reachable from the CairnDevTools eval console as
    /// <c>CairnCoop.Core.Instance.Driver.ForceRope()</c>; used by tools/repro's linkRope. Returns a status
    /// string ("request #2" / "accept #1" / "disconnect #n" / "no-partner" / "no-session").</summary>
    public string ForceRope()
    {
        var manager = MoSingleton<NetplayManager>.Instance;
        var gamemode = manager?.Gamemode?.TryCast<SharedRopeGamemode>();
        var client = manager?.client;
        return _belay.ForceRopeClosest(gamemode, client);
    }

    /// <summary>TEST/automation entry: send a DIRECTED carry request to a specific climber id — "attach me to
    /// #targetId" (targetId becomes MY CARRY once they accept). The directed analogue of <see cref="ForceRope"/>,
    /// needed to build an ORDERED chain A→B→C deterministically (vs ForceRope's closest-partner pick). Reachable
    /// as <c>CairnCoop.Core.Instance.Driver.RequestRope(id)</c>. Returns a status string.</summary>
    public string RequestRope(int targetId)
    {
        if (_handshake == null) return "no-handshake";
        _handshake.SendRequest(targetId);
        return "requested #" + targetId;
    }

    /// <summary>TEST/automation entry: ACCEPT an incoming carry request from #senderId (I agree to carry them;
    /// they become my dependent). Pairs with <see cref="RequestRope"/> on the other side. Reachable as
    /// <c>CairnCoop.Core.Instance.Driver.AcceptRope(id)</c>.</summary>
    public string AcceptRope(int senderId)
    {
        if (_handshake == null) return "no-handshake";
        _handshake.Accept(senderId);
        return "accepted #" + senderId;
    }

    /// <summary>TEST/automation read: the belay reconciler's last-tick diagnostic (what it observed/derived/
    /// drove) — for diagnosing why an anchor did or didn't form.</summary>
    public string BelayDiag()
    {
        var s = _belay.Snapshot;
        return $"action='{s.LastAction}' enabled={s.Enabled} partnerFound={s.PartnerFound} partnerId={s.PartnerId} "
            + $"partnerWall={s.PartnerWallSupported} partnerHang={s.PartnerHanging} partnerDeadNet={s.PartnerDeadNet} "
            + $"validBelayer={s.PartnerValidBelayer} anchorDesired={s.AnchorDesired} drainDesired={s.DrainDesired} "
            + $"anchorPresent={s.AnchorPresent} iWeight={s.IWeightRope} module={s.MyModule}";
    }

    /// <summary>TEST/automation read: the DOWN-edge drain diagnostic — per-dependent hang/freshness + the
    /// summed hanger count and published stamina load. Complements <see cref="BelayDiag"/> (which shows the
    /// up-edge / carry relationship).</summary>
    public string DependentDiag() => _belay.DependentDiag();

    /// <summary>TEST/automation read: the carry-graph state on this instance — my carry, effective carry, and
    /// dependents — for verifying an ordered chain formed correctly.</summary>
    public string CarryGraph()
    {
        if (_handshake == null) return "no-handshake";
        var deps = _handshake.Dependents();
        var sb = new System.Text.StringBuilder();
        sb.Append("carry=" + _handshake.MyCarry + " effectiveCarry=" + _handshake.EffectiveCarry + " deps=[");
        for (int i = 0; i < deps.Count; i++) sb.Append((i > 0 ? "," : "") + deps[i]);
        sb.Append("] enabled=" + _belay.Enabled);
        return sb.ToString();
    }

    /// <summary>Synchronously UNLINK the local mod belay — called from the rewind hook
    /// (<see cref="RewindEnterInstr"/>) at the resurrection-ENTER, i.e. the death moment, BEFORE the native
    /// Edelweiss rewind (RewindController) runs. It (a) keeps the mod's anchor from being attached when the
    /// rewind runs (else it NREs/stalls halfway — the "stuck halfway during the rewind" bug), and (b) drops
    /// the rope-intent so death = unlink: the climber returns from the rewind UNLINKED and re-ropes by hand,
    /// rather than the reconciler auto-re-linking on the first post-revive tick (which re-linked to a dead
    /// partner still reporting "Walking" on the net). The 1 Hz tick can't do this — GameDriver skips it
    /// while the rewind is in flight, the only window where the death is observable.</summary>
    public void StandDownBelayForRespawn() => _belay.UnlinkForRevive();

    /// <summary>True while a co-op session is live (in a room). Reachable from the CairnDevTools
    /// console for quick checks; the revive reconciler keys off the live rewind state, not this.</summary>
    public bool InCoopSession => State == Stage.InRoom;

    public string Describe()
    {
        var client = MoSingleton<NetplayManager>.Instance?.client;
        string native = client != null ? client.State.ToString() : "no client";
        return $"{State} (native: {native})";
    }

    /// <summary>
    /// The reconciler readout for the F4 "Belay" tab: the last tick's OBSERVED facts, DERIVED desired
    /// state, and DRIVEN actuals — laid out so a two-instance test is legible at a glance (read both
    /// clients' tabs side by side to see exactly where they diverge). Pure diagnostics.
    /// </summary>
    public List<string> DescribeBelay() => BelayReadout.Format(_belay.Snapshot);

    /// <summary>
    /// Native-side truth for the Game tab: roster, gamemode, prefab, spawners, spawned
    /// remotes. Pinpoints where the chain roster -> connect -> gamemode -> spawn breaks.
    /// </summary>
    public List<string> DescribeGameState() => SessionReadout.Describe(_belay);
}
