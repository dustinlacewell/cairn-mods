using System;
using System.Net;

namespace CairnCoop.Net;

/// <summary>A transport leg the relay can push datagrams down (local UDP or a Steam peer).</summary>
public interface IRelayLink
{
    void SendToClient(byte[] datagram);
    string Label { get; }
}

/// <summary>A relay link over the local UDP bridge: the host writes datagrams straight to a local game
/// instance's endpoint. Used both for same-machine joiners and for any in-process delivery.</summary>
public sealed class LocalLink : IRelayLink
{
    private readonly UdpBridge _bridge;
    private readonly EndPoint _gameEndpoint;
    public LocalLink(UdpBridge bridge, EndPoint gameEndpoint) { _bridge = bridge; _gameEndpoint = gameEndpoint; }
    public void SendToClient(byte[] datagram) => _bridge.SendTo(_gameEndpoint, datagram);
    public string Label => $"local:{_gameEndpoint}";
}

/// <summary>A relay link over a Steam datagram peer. The send mechanism is injected (the session owns the
/// channel routing + the flat Steam send), so this adapter knows only the peer id and how to push bytes.</summary>
public sealed class SteamLink : IRelayLink
{
    public readonly ulong SteamId;
    private readonly Action<ulong, byte[]> _send;
    public SteamLink(ulong steamId, Action<ulong, byte[]> send) { SteamId = steamId; _send = send; }
    public void SendToClient(byte[] datagram) => _send(SteamId, datagram);
    public string Label => $"steam:{SteamId}";
}
