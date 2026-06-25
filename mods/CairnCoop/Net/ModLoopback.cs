using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace CairnCoop.Net;

/// <summary>
/// A tiny mod-ONLY UDP channel for the local N-instance test, where each joiner's GAME talks straight
/// to the host relay and the mod has no inbound on the game path. Distinct IP (127.0.0.3) from the game
/// bridge so it never reaches the game's NetplayClient. This exists purely so mutual-rope is testable
/// across multiple instances on one machine.
///
/// Topology: a HUB-AND-SPOKE around the host. The host binds <see cref="HostPort"/>; each local joiner
/// binds <c>HostPort + instanceIndex</c> (instance 2 → 14002, instance 3 → 14003, …) and sends to the
/// host. The host LEARNS each joiner's endpoint from inbound datagrams (self-registration — no joiner-port
/// config) and FANS OUT every <see cref="Send"/> to all learned peers. A joiner sends only to the host;
/// the host re-fans host-originated / Steam-relayed rope frames to all joiners (the relay + router already
/// address frames by sender/partner id, so a broadcast hub is correct — each peer self-filters downstream).
///
/// Steam co-op does NOT use this (the Steam datagram link carries mod traffic, peeled before the game
/// socket). Separate ports per process because a shared UDP port can't fan to multiple same-machine
/// processes.
/// </summary>
public sealed class ModLoopback : IDisposable
{
    public const string Address = "127.0.0.3";
    public const int HostPort = 14001;

    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[512];
    private readonly bool _host;

    /// <summary>Host: every distinct joiner endpoint we've heard from (self-registered on first inbound).
    /// Joiner: the single fixed host endpoint.</summary>
    private readonly HashSet<EndPoint> _peers = new HashSet<EndPoint>();

    /// <summary>Raw mod datagram arrived (a full Wire S->C frame, e.g. STCR_MOD_ROPE).</summary>
    public event Action<byte[]> OnDatagram;

    /// <param name="host">true on the host (binds HostPort, fans out to all learned joiners).</param>
    /// <param name="instanceIndex">on a joiner, its 1-based local instance ordinal (2, 3, …); the joiner
    /// binds <c>HostPort + (instanceIndex - 1)</c> so instance 2 → 14002, 3 → 14003. Ignored on the host.</param>
    public ModLoopback(bool host, int instanceIndex = 2)
    {
        _host = host;
        int bindPort = host ? HostPort : HostPort + (instanceIndex - 1);
        if (!host)
            _peers.Add(new IPEndPoint(IPAddress.Parse(Address), HostPort)); // joiner's only target = host
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null);
        _socket.Bind(new IPEndPoint(IPAddress.Parse(Address), bindPort));
        _socket.Blocking = false;
    }

    public void Poll()
    {
        while (true)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int len;
            try
            {
                if (_socket.Available == 0)
                    return;
                len = _socket.ReceiveFrom(_buffer, ref from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                return;
            }
            if (len <= 0)
                return;
            // Host self-registration: remember every distinct joiner we hear from, so Send fans to all.
            if (_host)
                _peers.Add(from);
            var datagram = new byte[len];
            Array.Copy(_buffer, datagram, len);
            OnDatagram?.Invoke(datagram);
        }
    }

    /// <summary>Host: fan the datagram to every learned joiner. Joiner: send to the host. Best-effort UDP.</summary>
    public void Send(byte[] datagram)
    {
        foreach (var peer in _peers)
        {
            try { _socket.SendTo(datagram, peer); }
            catch (SocketException) { /* transient; same-machine, best-effort */ }
        }
    }

    /// <summary>Marker byte for a presence ping — a joiner's "I exist" datagram so the host LEARNS its
    /// endpoint even when the joiner never sends rope traffic (a chain HEAD requests nobody, so without this
    /// the host's self-registration never sees it and can't relay frames TO it). Outside the STCR opcode set,
    /// so the router's <c>OnModLoopbackDatagram</c> ignores it for application — it only serves registration.</summary>
    public const byte PresencePing = 0xFE;

    /// <summary>Joiner-only: ping the host so it registers our endpoint. No-op on the host (it has no upstream
    /// peer to announce to; it learns joiners from their inbound datagrams, including this ping).</summary>
    public void RegisterPing()
    {
        if (_host)
            return;
        try { _socket.SendTo(new byte[] { PresencePing }, new IPEndPoint(IPAddress.Parse(Address), HostPort)); }
        catch (SocketException) { /* best-effort */ }
    }

    public void Dispose() => _socket.Close();
}
