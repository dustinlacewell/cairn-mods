using System;
using System.Net;
using System.Net.Sockets;

namespace CairnCoop.Net;

/// <summary>
/// Loopback UDP endpoint the native NetplayClient talks to.
///
/// The game's client Connect()s to serverAddress:14000, so inbound packets are
/// kernel-filtered to source serverAddress:14000 exactly. We bind 127.0.0.2:14000
/// (distinct IP, same port — coexists with anything on the wildcard) and point
/// NetplayTweakables.serverAddress at 127.0.0.2; everything we send leaves from
/// 127.0.0.2:14000 and passes the filter.
///
/// Datagrams are keyed by source endpoint, so multiple local game instances
/// (each on its own ephemeral port — see SocketBindPatch) are distinct clients.
/// </summary>
public sealed class UdpBridge : IDisposable
{
    public const string BridgeAddress = "127.0.0.2";
    public const int NetplayPort = 14000;

    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[2048];

    public event Action<EndPoint, byte[]> OnDatagram;

    public UdpBridge()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Suppress WSAECONNRESET delivery on UDP (ICMP port-unreachable from a game
        // instance that isn't up yet would otherwise fault our receives).
        _socket.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null);
        _socket.Bind(new IPEndPoint(IPAddress.Parse(BridgeAddress), NetplayPort));
        _socket.Blocking = false;
    }

    /// <summary>Drain pending datagrams from local game instances. Call every frame.</summary>
    public void Poll()
    {
        while (true)
        {
            int len;
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                if (_socket.Available == 0)
                    return;
                len = _socket.ReceiveFrom(_buffer, ref remote);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                return;
            }
            if (len <= 0)
                return;
            var datagram = new byte[len];
            Array.Copy(_buffer, datagram, len);
            OnDatagram?.Invoke(remote, datagram);
        }
    }

    public void SendTo(EndPoint gameEndpoint, byte[] datagram)
    {
        try { _socket.SendTo(datagram, gameEndpoint); }
        catch (SocketException) { /* transient; the native ARQ covers reliable traffic */ }
    }

    public void Dispose() => _socket.Close();
}
