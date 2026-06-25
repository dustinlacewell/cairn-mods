using System;
using System.Collections.Generic;
using System.Linq;

namespace CairnCoop.Steam;

/// <summary>
/// Callback-free, lobby-free Steam rendezvous:
///  - Host advertises its own SteamID in rich presence under "connect".
///  - Joiner scans friends' rich presence, finds the host id, and just starts sending;
///    the host admits it via the session-request config callback (see SteamApi).
///  - The host's peer roster is traffic-based: PeerSeen() on every datagram/session
///    request, timeout sweeps for departure.
/// No lobbies and no Steam call results anywhere — those race the game's own
/// SteamAPI.RunCallbacks pump (it consumes every completed call result first).
/// </summary>
public sealed class SteamRendezvous
{
    public const string ConnectPrefix = "cairncoop_";
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FriendScanInterval = TimeSpan.FromSeconds(3);

    public readonly struct FriendHost
    {
        public readonly ulong HostSteamId;
        public readonly string Name;
        public FriendHost(ulong hostSteamId, string name) { HostSteamId = hostSteamId; Name = name; }
    }

    public enum Mode { Idle, Hosting, Joined }

    public Mode State { get; private set; } = Mode.Idle;
    public ulong HostSteamId { get; private set; }
    public IReadOnlyCollection<ulong> Peers => _peerLastSeen.Keys;
    public IReadOnlyList<FriendHost> JoinableFriends => _joinableFriends;

    public event Action<ulong> PeerJoined;
    public event Action<ulong> PeerLeft;
    public event Action JoinedHost;

    private readonly Dictionary<ulong, DateTime> _peerLastSeen = new();
    private readonly List<FriendHost> _joinableFriends = new();
    private readonly Action<string> _log;
    private DateTime _nextFriendScan = DateTime.MinValue;

    public SteamRendezvous(Action<string> log) => _log = log;

    public void StartHost()
    {
        HostSteamId = SteamApi.MySteamId();
        if (!SteamApi.LoggedOn())
            _log("steam: warning — not logged on to the Steam backend; friends won't see the session");
        SteamApi.SetRichPresence("connect", ConnectPrefix + HostSteamId);
        SteamApi.SetRichPresence("status", "Hosting Cairn co-op");
        State = Mode.Hosting;
        _log($"steam: hosting as {SteamApi.MyName()} ({HostSteamId}), rich presence set");
    }

    public void Join(ulong hostSteamId)
    {
        HostSteamId = hostSteamId;
        State = Mode.Joined;
        _log($"steam: joining {SteamApi.FriendName(hostSteamId)} ({hostSteamId})");
        JoinedHost?.Invoke();
    }

    public void Stop()
    {
        SteamApi.ClearRichPresence();
        foreach (var peer in _peerLastSeen.Keys.ToList())
            PeerLeft?.Invoke(peer);
        _peerLastSeen.Clear();
        HostSteamId = 0;
        State = Mode.Idle;
    }

    /// <summary>Record traffic (or a session request) from a Steam peer; fires PeerJoined on first sight.</summary>
    public void PeerSeen(ulong steamId)
    {
        bool isNew = !_peerLastSeen.ContainsKey(steamId);
        _peerLastSeen[steamId] = DateTime.UtcNow;
        if (!isNew)
            return;
        _log($"steam: peer connected: {SteamApi.FriendName(steamId)} ({steamId})");
        PeerJoined?.Invoke(steamId);
    }

    public void Tick(bool scanFriends)
    {
        var now = DateTime.UtcNow;

        if (State == Mode.Hosting)
        {
            foreach (var (peer, lastSeen) in _peerLastSeen.ToList())
            {
                if (now - lastSeen <= PeerTimeout)
                    continue;
                _peerLastSeen.Remove(peer);
                _log($"steam: peer timed out: {SteamApi.FriendName(peer)}");
                PeerLeft?.Invoke(peer);
            }
        }

        if (scanFriends && State == Mode.Idle && now >= _nextFriendScan)
        {
            _nextFriendScan = now + FriendScanInterval;
            ScanFriends();
        }
    }

    private void ScanFriends()
    {
        _joinableFriends.Clear();
        int count = SteamApi.FriendCount();
        for (int i = 0; i < count; i++)
        {
            ulong friend = SteamApi.FriendByIndex(i);
            string connect = SteamApi.FriendRichPresence(friend, "connect");
            if (string.IsNullOrEmpty(connect) || !connect.StartsWith(ConnectPrefix))
                continue;
            if (ulong.TryParse(connect.Substring(ConnectPrefix.Length), out ulong hostId))
                _joinableFriends.Add(new FriendHost(hostId, SteamApi.FriendName(friend)));
        }
    }
}
