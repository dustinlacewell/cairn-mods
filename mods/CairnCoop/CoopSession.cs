using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using CairnCoop.Net;
using CairnCoop.Steam;

namespace CairnCoop;

/// <summary>
/// The co-op role/session state machine: owns every transport (the in-process relay, the local UDP
/// bridge, the Steam rendezvous, the mod loopback) and the net-thread <see cref="NetPump"/>, and drives
/// host/join/local-join/stop. The WIRE runs off the game thread (see <see cref="PumpNet"/>); game-touching
/// side-effects are marshalled to the main thread via the pump's <c>Post</c>/<c>DrainToMain</c>.
///
/// Transport construction/teardown (main thread) is serialised against a pump iteration by
/// <see cref="_netLock"/>, so the pump can never observe a half-built or disposed bridge/relay/loopback.
///
/// The mod-private rope routing (which reads this session's transports) lives in <see cref="ModRopeRouter"/>;
/// this session exposes the narrow transport state the router needs and forwards the relay/loopback inbound
/// hooks to it.
/// </summary>
public sealed class CoopSession : IDisposable
{
    public enum Role { None, Host, Joiner, LocalJoiner }

    private const int ChannelControl = 0;
    private const int ChannelFrames = 1;
    private const int ChannelPing = 2;
    private static readonly byte[] PingPayload = { 0xC0 };
    private const float AutoStartAt = 25f; // give the game time to boot

    private readonly Action<string> _log;
    private GameDriver _driver;

    private Role _role = Role.None;
    private RelayServer _relay;
    private UdpBridge _bridge;
    private SteamRendezvous _rendezvous;
    private NetPump _net;
    // Serialises transport construction/teardown (main thread) against a net-thread pump iteration,
    // so the pump can never observe a half-built or disposed bridge/relay/loopback.
    private readonly object _netLock = new();
    private readonly ConcurrentDictionary<ulong, SteamLink> _steamLinks = new();
    private readonly ConcurrentDictionary<EndPoint, LocalLink> _localLinks = new();
    private CairnCoop.Net.ModLoopback _modLoopback;
    private EndPoint _joinerGameEndpoint;

    private ModRopeRouter _router;

    private string _autoStart = Environment.GetEnvironmentVariable("CAIRNCOOP_AUTOHOST") == "1" ? "host"
        : Environment.GetEnvironmentVariable("CAIRNCOOP_AUTOJOIN") == "local" ? "local"
        : null;

    public CoopSession(Action<string> log)
    {
        _log = log;
        _net = new NetPump(PumpNet, log); // the wire runs off the game thread (see PumpNet / NetPump)
    }

    /// <summary>Wire the mod-rope router in (constructed against this session; mutual reference).</summary>
    public void AttachRouter(ModRopeRouter router) => _router = router;

    /// <summary>Wire the game driver in. Deferred from the ctor to break the driver↔session↔router
    /// construction cycle: the driver is built with the router, which needs this session. The pump only
    /// touches the driver while a session is live (role != None), long after wiring completes.</summary>
    public void AttachDriver(GameDriver driver) => _driver = driver;

    // --- router-facing transport state (read by ModRopeRouter for per-role routing) -----------------

    public Role CurrentRole => _role;
    public CairnCoop.Net.ModLoopback ModLoopback => _modLoopback;
    public IEnumerable<SteamLink> SteamLinks => _steamLinks.Values;
    /// <summary>The host's Steam id when we're a Joiner with a live rendezvous, else null.</summary>
    public ulong? JoinedHostSteamId =>
        _rendezvous is { State: SteamRendezvous.Mode.Joined } ? _rendezvous.HostSteamId : (ulong?)null;

    /// <summary>Marshal a side-effect to the game main thread (drained each OnUpdate via this session).</summary>
    public void PostToMain(Action work) => _net?.Post(work);

    // --- transport plumbing ---------------------------------------------------

    /// <summary>Frames and heartbeats ride the unreliable channel; everything else reliable.</summary>
    private static int ChannelFor(byte opcode) =>
        opcode is Wire.CTSU_ALIVE or Wire.CTSU_FRAME or Wire.STCU_FRAMES ? ChannelFrames : ChannelControl;

    public void SendOverSteam(ulong peer, byte[] datagram)
    {
        int channel = ChannelFor(datagram[0]);
        SteamApi.SendMessageToUser(peer, datagram, channel == ChannelFrames
            ? SteamApi.SendUnreliableNoNagle
            : SteamApi.SendReliableNoNagle, channel);
    }

    private SteamLink LinkFor(ulong steamId)
        => _steamLinks.GetOrAdd(steamId, id => new SteamLink(id, SendOverSteam));

    private LocalLink LinkFor(EndPoint gameEndpoint)
    {
        // Called only from the net thread (under _netLock), so a plain get-or-add is race-free here.
        if (_localLinks.TryGetValue(gameEndpoint, out var link))
            return link;
        link = new LocalLink(_bridge, gameEndpoint);
        _localLinks[gameEndpoint] = link;
        return link;
    }

    // --- session control --------------------------------------------------------

    private bool EnsureSteam()
    {
        if (!SteamApi.TryInit())
        {
            _log("Steam not ready yet");
            return false;
        }
        SteamApi.SessionRequested -= OnSteamSessionRequested; // idempotent re-subscribe
        SteamApi.SessionRequested += OnSteamSessionRequested;
        if (!SteamApi.InstallSessionRequestHandler())
            _log("steam: warning — session-request handler install failed");
        return true;
    }

    private void OnSteamSessionRequested(ulong peer)
    {
        bool expected = _role == Role.Host
            || (_role == Role.Joiner && _rendezvous != null && peer == _rendezvous.HostSteamId);
        if (!expected)
        {
            _log($"steam: ignoring session request from unexpected peer {peer}");
            return;
        }
        SteamApi.AcceptSessionWithUser(peer);
        if (_role == Role.Host)
            _rendezvous.PeerSeen(peer);
    }

    public void StartHost()
    {
        if (_role != Role.None || !EnsureSteam())
            return;
        lock (_netLock) // build the transport before the pump can observe Role.Host
        {
            _relay = new RelayServer(_log);
            // A peer's rope-intent surfaces here (the relay never routes it — mod-private). Re-fan to
            // the other members over their transports + apply to our own belay.
            _relay.ModRopeReceived += (msg, _) => _router.FanModRopeToMembers(msg);
            // A peer's rope-handshake event surfaces here (mod-private; the relay never routes it). Re-fan to
            // the addressed member over their transport + apply to our own handshake (filtered by target).
            _relay.ModRopeReqReceived += (msg, _) => _router.FanModRopeReqToMembers(msg);
            _bridge = new UdpBridge();
            _bridge.OnDatagram += (endpoint, datagram) => _relay.OnDatagram(LinkFor(endpoint), datagram);
            _modLoopback = new CairnCoop.Net.ModLoopback(host: true);
            _modLoopback.OnDatagram += _router.OnModLoopbackDatagram;
            _role = Role.Host;
        }

        _rendezvous = new SteamRendezvous(_log);
        _rendezvous.PeerJoined += peer =>
        {
            SteamApi.AcceptSessionWithUser(peer);
            SteamApi.SendMessageToUser(peer, PingPayload, SteamApi.SendReliableNoNagle, ChannelPing);
        };
        _rendezvous.PeerLeft += peer =>
        {
            _relay.DropLink(LinkFor(peer));
            SteamApi.CloseSessionWithUser(peer);
            _steamLinks.TryRemove(peer, out _);
        };
        _rendezvous.StartHost();

        _driver.Connect(SteamApi.MyName());
        _log("hosting: relay up on " + UdpBridge.BridgeAddress + ":14000");
    }

    public void StartJoin(ulong hostSteamId)
    {
        if (_role != Role.None || !EnsureSteam())
            return;
        lock (_netLock)
        {
            _bridge = new UdpBridge();
            _bridge.OnDatagram += (endpoint, datagram) =>
            {
                _joinerGameEndpoint = endpoint;
                if (_rendezvous is { State: SteamRendezvous.Mode.Joined })
                    SendOverSteam(_rendezvous.HostSteamId, datagram);
            };
            _role = Role.Joiner;
        }

        _rendezvous = new SteamRendezvous(_log);
        _rendezvous.JoinedHost += () =>
        {
            SteamApi.AcceptSessionWithUser(_rendezvous.HostSteamId);
            SteamApi.SendMessageToUser(_rendezvous.HostSteamId, PingPayload, SteamApi.SendReliableNoNagle, ChannelPing);
            _driver.Connect(SteamApi.MyName());
        };
        _rendezvous.Join(hostSteamId);
    }

    /// <summary>
    /// Join a host running in ANOTHER instance on this machine: the game talks straight
    /// to the host instance's relay at 127.0.0.2:14000 — no bridge, no Steam transport.
    /// </summary>
    public void StartJoinLocal()
    {
        if (_role != Role.None)
            return;
        // 1-based local instance ordinal: host is 1, joiners are 2, 3, … Drives both the loopback bind port
        // (HostPort + index-1) and the display-name suffix, so N local instances get distinct ports/names.
        int instanceIndex = 2;
        var idxEnv = Environment.GetEnvironmentVariable("CAIRNCOOP_INSTANCE");
        if (!string.IsNullOrEmpty(idxEnv) && int.TryParse(idxEnv, out var parsed) && parsed >= 2)
            instanceIndex = parsed;
        string name = SteamApi.TryInit() ? SteamApi.MyName() + "-" + instanceIndex : "Climber-" + instanceIndex;
        lock (_netLock)
        {
            _modLoopback = new CairnCoop.Net.ModLoopback(host: false, instanceIndex);
            _modLoopback.OnDatagram += _router.OnModLoopbackDatagram;
            _role = Role.LocalJoiner;
        }
        _driver.Connect(name);
        _log("local join: pointing native client at the host instance's relay");
    }

    public void StopSession()
    {
        _driver.Disconnect();
        _rendezvous?.Stop();
        lock (_netLock)
        {
            _role = Role.None; // stop the pump touching the transport before we tear it down
            foreach (var link in _steamLinks.Values)
                SteamApi.CloseSessionWithUser(link.SteamId);
            _steamLinks.Clear();
            _localLinks.Clear();
            _joinerGameEndpoint = null;
            _bridge?.Dispose();
            _bridge = null;
            _modLoopback?.Dispose();
            _modLoopback = null;
            _relay = null;
        }
        _rendezvous = null;
        _log("session stopped");
    }

    // --- per-frame (main thread) -------------------------------------------------

    /// <summary>Drain net-thread→main-thread side-effects (rope-state apply, rendezvous PeerSeen).
    /// Call once per frame from OnUpdate, BEFORE ticking the driver.</summary>
    public void DrainToMain() => _net?.DrainToMain();

    /// <summary>Run the autostart trigger (env-var driven host/local-join after the game has booted).</summary>
    public void TickAutostart(float realtimeSinceStartup)
    {
        if (_autoStart == null || realtimeSinceStartup <= AutoStartAt || _role != Role.None)
            return;
        string mode = _autoStart;
        _autoStart = null;
        _log($"autostart: {mode}");
        if (mode == "host")
            StartHost();
        else
            StartJoinLocal();
    }

    /// <summary>Tick the Steam friend discovery + rendezvous timeout sweep (MAIN thread — touches the
    /// friends API + rich presence + fires join/leave handlers). The datagram pump runs on the net thread.</summary>
    public void TickRendezvous(bool scanForFriends)
    {
        if (SteamApi.Ready || SteamApi.TryInit())
            _rendezvous?.Tick(scanFriends: scanForFriends);
    }

    /// <summary>The host's Steam peer count (for the panel). 0 when not hosting.</summary>
    public int HostPeerCount => _rendezvous?.Peers.Count ?? 0;

    /// <summary>True while a co-op session is live (any non-None role).</summary>
    public bool Active => _role != Role.None;

    /// <summary>HOST-ONLY: kick a member from the room by its room Id. The host IS the relay, so this calls the
    /// relay's in-process <see cref="RelayServer.KickMember"/> — marshalled onto the net thread, where relay
    /// state lives. The relay bans the target's link (rejoin refused) and resets its client so it disconnects.
    /// No-op when we aren't hosting (only the host owns a relay). The status is logged, not returned (the call
    /// is async on the net thread).</summary>
    public void Kick(int targetId)
    {
        if (_role != Role.Host)
        {
            _log("kick: ignored — only the host can kick");
            return;
        }
        _net?.Post(() =>
        {
            var relay = _relay;
            if (relay != null)
                _log(relay.KickMember(targetId));
        });
    }

    // --- panel-facing read surface -----------------------------------------------

    /// <summary>Friends currently advertising a joinable co-op session (idle-state discovery). Empty/null
    /// when no rendezvous exists.</summary>
    public IReadOnlyList<SteamRendezvous.FriendHost> JoinableFriends => _rendezvous?.JoinableFriends;

    /// <summary>The host's display name once we've joined (Joiner role).</summary>
    public string JoinedHostName => _rendezvous != null ? SteamApi.FriendName(_rendezvous.HostSteamId) : "?";

    /// <summary>Relay peer description lines, host-side. Empty when not hosting / no relay.</summary>
    public IEnumerable<string> DescribeRelayPeers() =>
        _relay != null ? _relay.DescribePeers() : System.Array.Empty<string>();

    /// <summary>Room members for the host's kick UI (id + name + isOwner). Empty when not hosting / no relay.
    /// A snapshot list (the relay builds it fresh); the panel renders a kick button per non-owner member.</summary>
    public System.Collections.Generic.List<RelayServer.Member> RelayMembers() =>
        _relay != null ? _relay.RoomMembers() : new System.Collections.Generic.List<RelayServer.Member>();

    /// <summary>Ensure an idle-state rendezvous exists so the panel's friend scan can run while not in a
    /// session (matches the old None-tab lazy-init). No-op once a session owns one.</summary>
    public void EnsureIdleRendezvous()
    {
        if (_rendezvous == null)
            _rendezvous = new SteamRendezvous(_log);
    }

    // --- net thread --------------------------------------------------------------

    /// <summary>
    /// The WIRE pump — runs on the <see cref="NetPump"/> background thread, NOT the game thread.
    /// Pure bytes: poll the sockets, feed the relay, resend/timeout. It must touch no Unity/IL2CPP
    /// object; game-touching side-effects (rope-state apply, rendezvous PeerSeen) are marshalled to
    /// the main thread via <c>_net.Post</c>. Serialised against session construction/teardown by
    /// <see cref="_netLock"/> so a Start/Stop on the main thread can't swap fields mid-iteration.
    /// </summary>
    private void PumpNet()
    {
        lock (_netLock)
        {
            if (_role == Role.None)
                return;

            if (SteamApi.Ready || SteamApi.TryInit())
            {
                switch (_role)
                {
                    case Role.Host:
                        if (_relay != null)
                            _relay.SuppressTimeouts = CoopRevive.Active; // never GC the room mid-rewind
                        _bridge?.Poll();
                        SteamApi.ReceiveMessagesOnChannel(ChannelControl, OnSteamToRelay);
                        SteamApi.ReceiveMessagesOnChannel(ChannelFrames, OnSteamToRelay);
                        SteamApi.ReceiveMessagesOnChannel(ChannelPing, OnSteamPing);
                        _relay?.Tick();
                        break;

                    case Role.Joiner:
                        _bridge?.Poll();
                        SteamApi.ReceiveMessagesOnChannel(ChannelControl, OnSteamFromHost);
                        SteamApi.ReceiveMessagesOnChannel(ChannelFrames, OnSteamFromHost);
                        SteamApi.ReceiveMessagesOnChannel(ChannelPing, IgnoreSteamDatagram);
                        break;
                }
            }

            _modLoopback?.Poll(); // mod-private rope traffic (local-test path), independent of Steam

            // Joiner presence ping (~1 Hz): announce our loopback endpoint to the host so it registers us and
            // can RELAY frames to us — even when we never send rope traffic (a chain head requests nobody).
            // Without this a silent receiver is invisible to the host's self-registration. No-op on the host.
            if (_role == Role.LocalJoiner && _modLoopback != null
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastModLoopPingMs >= 1000)
            {
                _lastModLoopPingMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _modLoopback.RegisterPing();
            }
        }
    }

    private long _lastModLoopPingMs;

    private static void IgnoreSteamDatagram(ulong peer, byte[] data) { }

    /// <summary>Host: a ping is just liveness — record it (rendezvous is main-thread).</summary>
    private void OnSteamPing(ulong peer, byte[] data) => _net?.Post(() => _rendezvous?.PeerSeen(peer));

    private void OnSteamToRelay(ulong peer, byte[] data)
    {
        _net?.Post(() => _rendezvous?.PeerSeen(peer)); // rendezvous bookkeeping is main-thread
        _relay?.OnDatagram(LinkFor(peer), data);       // relay protocol state stays on the net thread
    }

    private void OnSteamFromHost(ulong peer, byte[] data)
    {
        if (_rendezvous == null || peer != _rendezvous.HostSteamId)
            return;
        // Peel mod-private rope traffic BEFORE the game socket — the game would trap on it.
        if (data.Length >= 1 && data[0] == ModWire.STCR_MOD_ROPE)
        {
            _router.ApplyModRopeDatagram(data);
            return;
        }
        if (data.Length >= 1 && data[0] == ModWire.STCR_MOD_ROPE_REQ)
        {
            _router.ApplyModRopeReqDatagram(data);
            return;
        }
        // Replay host game traffic to the joiner's local game socket (observed from its own
        // outbound datagrams). Before the game has sent anything there's nowhere to deliver —
        // safe, the relay retransmits reliable traffic.
        if (_joinerGameEndpoint == null)
            return;
        _bridge.SendTo(_joinerGameEndpoint, data);
    }

    public void Dispose()
    {
        StopSession();
        _net?.Dispose(); // stop the net thread before the caller closes the log it writes to
        _net = null;
    }
}
