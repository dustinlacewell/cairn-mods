using System;
using System.Collections.Generic;

namespace CairnCoop.Net;

/// <summary>
/// Per-peer ARQ: the reliable-delivery half of the relay's per-peer state, extracted from
/// <see cref="RelayServer"/> so the orchestrator keeps only fan-out + timeout policy. Owns the
/// outbound retransmit queue (<see cref="_awaitingAck"/>), the outbound message-id counter, the
/// inbound dedupe window (<see cref="_seenIds"/>/<see cref="_seenOrder"/>), and the resend timer.
///
/// CRITICAL wire invariants (preserved byte-for-byte from the pre-split relay):
///   • Resend forever, drop NOBODY: a reliable that goes briefly unacked is retried indefinitely at
///     <see cref="ResendMs"/>; nothing here ever decides a peer is dead. Liveness is silence-only and
///     lives in <see cref="RelayServer.Tick"/>.
///   • Always-ack + dedupe: the relay acks every inbound reliable (so the native client's 5-resend
///     reset never trips), then suppresses duplicate delivery of a retransmit via a sliding
///     <see cref="DedupeWindow"/> of recently-seen message ids.
///   • Monotonic outbound ids start at 1 (pre-increment of a zero-initialised counter).
/// </summary>
internal sealed class ReliableChannel
{
    private const double ResendMs = 100;
    private const int DedupeWindow = 128;

    private sealed class Pending
    {
        public uint Id;
        public byte[] Datagram;
        public DateTime LastSent;
        public int Attempts;
    }

    private readonly List<Pending> _awaitingAck = new();
    private readonly HashSet<uint> _seenIds = new();
    private readonly Queue<uint> _seenOrder = new();
    private uint _nextOutboundId;

    /// <summary>Frame a reliable datagram with the next outbound id, queue it for resend, and send it
    /// once now. The caller supplies the send sink (the peer's link) so this owns no transport.</summary>
    public void Send(byte opcode, byte[] body, Action<byte[]> sendToClient)
    {
        uint id = ++_nextOutboundId;
        var datagram = Wire.Build(opcode, id, body);
        _awaitingAck.Add(new Pending { Id = id, Datagram = datagram, LastSent = DateTime.UtcNow, Attempts = 1 });
        sendToClient(datagram);
    }

    /// <summary>An ack arrived for <paramref name="messageId"/> — stop resending it.</summary>
    public void Acknowledge(uint messageId) => _awaitingAck.RemoveAll(p => p.Id == messageId);

    /// <summary>Record an inbound reliable message id; returns false if it's a duplicate (already in the
    /// dedupe window) so the caller skips re-processing a retransmit. Maintains the sliding window.</summary>
    public bool RememberInbound(uint id)
    {
        if (!_seenIds.Add(id))
            return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > DedupeWindow)
            _seenIds.Remove(_seenOrder.Dequeue());
        return true;
    }

    /// <summary>Resend any unacked reliable whose resend interval has elapsed — and KEEP resending
    /// forever. Never drops anything; a peer that is still sending datagrams is alive and the silence
    /// timeout (relay-level) is the only departure signal.</summary>
    public void ResendDue(DateTime now, Action<byte[]> sendToClient)
    {
        foreach (var pending in _awaitingAck)
        {
            if ((now - pending.LastSent).TotalMilliseconds < ResendMs)
                continue;
            pending.LastSent = now;
            sendToClient(pending.Datagram);
        }
    }
}
