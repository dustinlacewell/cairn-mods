using System;
using System.IO;

namespace CairnCoop.Net;

/// <summary>
/// A rope-state announcement from <see cref="SenderId"/> about their rope to <see cref="PartnerId"/>:
/// whether they are <see cref="Connected"/> (so roping is mutual — A ropes to B ⇒ B ropes back), and
/// whether the sender is currently <see cref="Hanging"/> (weighting the rope: caught/hanging/abseiling/
/// dead). The carrier can't detect a partner's hang locally — fall physics are local to the faller and
/// the net layer reports a caught hang as plain "Falling" — so the faller announces it here, and the
/// carrier drains stamina only while a roped partner reports Hanging.
///
/// <see cref="Seq"/> is a per-sender monotonic counter (FIX-2, see re/systems/coop/coop-fall-consistency.md
/// §0): the receiver drops any message whose Seq is ≤ the last accepted from that sender, so latest-WINS
/// means latest-SENT, not latest-arrived. Required because the local-test loopback transport is raw UDP
/// (no ordering/dedup) — without it a reordered stale snapshot latches a wrong state. The receiver MUST
/// reset its per-sender high-water mark when the sender leaves the roster (FIX-5), else a rejoining peer
/// whose counter restarts at 0 has every packet discarded forever.
/// </summary>
public readonly struct RopeState
{
    public readonly int SenderId;
    public readonly int PartnerId;
    public readonly bool Connected;
    public readonly bool Hanging;
    public readonly uint Seq;

    /// <summary>The sender's CARRY id — the climber it has roped UP to (its <c>MyCarry</c>), or -1 if it has
    /// no carry (chain head). This is a property of the SENDER, not of the addressed edge, so it is identical
    /// on every per-neighbour frame the sender emits this tick. It rides the snapshot stream purely so any
    /// client can assemble the room-wide chain topology (sender→carry) for the party HUD; the belay reconciler
    /// ignores it (it reads only the addressed Connected/Hanging facts). Recorded by <see cref="TopologyTracker"/>
    /// off every received frame, regardless of addressing.</summary>
    public readonly int Carry;

    public RopeState(int senderId, int partnerId, bool connected, bool hanging = false, uint seq = 0, int carry = -1)
    {
        SenderId = senderId;
        PartnerId = partnerId;
        Connected = connected;
        Hanging = hanging;
        Seq = seq;
        Carry = carry;
    }

    /// <summary>Wire body for the relay opcodes (Steam/local transports):
    /// [int sender][int partner][byte connected][byte hanging][uint seq][int carry].</summary>
    public byte[] ToBody()
    {
        int sender = SenderId, partner = PartnerId;
        byte connected = Connected ? (byte)1 : (byte)0;
        byte hanging = Hanging ? (byte)1 : (byte)0;
        uint seq = Seq;
        int carry = Carry;
        return Wire.Body(w =>
        {
            w.Write(sender);
            w.Write(partner);
            w.Write(connected);
            w.Write(hanging);
            w.Write(seq);
            w.Write(carry);
        });
    }

    public static RopeState FromReader(BinaryReader r)
        => new(r.ReadInt32(), r.ReadInt32(), r.ReadByte() != 0, r.ReadByte() != 0, r.ReadUInt32(), r.ReadInt32());
}

/// <summary>The verb of a <see cref="RopeRequest"/> — the four discrete events of the request/accept
/// handshake. Unlike <see cref="RopeState"/> (a latest-wins connection SNAPSHOT), each of these is a
/// one-shot EVENT delivered exactly once over the reliable class; it must NOT be seq-deduped as a stale
/// snapshot (an Accept dropped as "stale" would deadlock the handshake).</summary>
public enum RopeRequestVerb : byte
{
    /// <summary>I want to rope up with you (sent at the requester's gesture).</summary>
    Request = 0,
    /// <summary>I accept your rope request (sent at the requestee's gesture) — the rope forms on both sides.</summary>
    Accept = 1,
    /// <summary>I decline your rope request — clears the requester's outgoing pending.</summary>
    Reject = 2,
    /// <summary>I am dropping our existing rope (unrope), or cancelling a pending request — clears both sides.</summary>
    Cancel = 3,
}

/// <summary>
/// A discrete rope-handshake EVENT from <see cref="SenderId"/> addressed to <see cref="TargetId"/>:
/// a <see cref="Verb"/> (request / accept / reject / cancel) tagged with a <see cref="Nonce"/> that
/// correlates an accept/reject back to the request it answers. This is the EVENT sibling of the
/// connection-state SNAPSHOT <see cref="RopeState"/> — it routes identically over <see cref="IModChannel"/>
/// but is delivered ONCE (reliability rides the existing CTSR reliable class), never seq-deduped: a dropped
/// accept must not be silently discarded as "stale".
///
/// The connection model is request→accept: a peer holding multiple pending requests can accept each
/// independently (the per-partner sets in <see cref="CairnCoop.RopeHandshake"/>), so large lobbies fall out
/// naturally. See re/systems/coop/coop-rope-request-design.md §"Step 3+4 spec".
/// </summary>
public readonly struct RopeRequest
{
    public readonly int SenderId;
    public readonly int TargetId;
    public readonly RopeRequestVerb Verb;
    public readonly uint Nonce;

    public RopeRequest(int senderId, int targetId, RopeRequestVerb verb, uint nonce)
    {
        SenderId = senderId;
        TargetId = targetId;
        Verb = verb;
        Nonce = nonce;
    }

    /// <summary>Wire body for the S->C relay opcode (Steam/local transports):
    /// [int sender][int target][byte verb][uint nonce].</summary>
    public byte[] ToBody()
    {
        int sender = SenderId, target = TargetId;
        byte verb = (byte)Verb;
        uint nonce = Nonce;
        return Wire.Body(w =>
        {
            w.Write(sender);
            w.Write(target);
            w.Write(verb);
            w.Write(nonce);
        });
    }

    public static RopeRequest FromReader(BinaryReader r)
        => new(r.ReadInt32(), r.ReadInt32(), (RopeRequestVerb)r.ReadByte(), r.ReadUInt32());
}

/// <summary>
/// Mod-to-mod message bus, independent of HOW the bytes move (in-process relay, Steam datagram, or
/// the local-test loopback). <see cref="PartnerBelay"/> depends on this narrow interface only — it
/// announces its own rope-intent and reacts to a partner's, knowing nothing about the transport. A
/// new transport is a new implementor with zero changes to the belay logic.
///
/// Never carried on the game's protocol: every implementor keeps mod traffic off the game socket
/// (the game's NetplayClient traps on opcodes/sub-keys it doesn't recognise).
/// </summary>
public interface IModChannel
{
    /// <summary>Announce my rope-intent to the other room member(s).</summary>
    void Broadcast(RopeState msg);

    /// <summary>A peer's rope-intent arrived (already peeled off the wire; never reaches the game).</summary>
    event Action<RopeState> OnReceived;

    /// <summary>Send a discrete rope-handshake event (request/accept/reject/cancel) to the addressed peer.
    /// Routes by role exactly like <see cref="Broadcast"/>, on its own opcode pair, off the game socket.</summary>
    void Send(RopeRequest msg);

    /// <summary>A peer's rope-handshake event arrived (already peeled off the wire; never reaches the game).</summary>
    event Action<RopeRequest> OnRequest;
}

/// <summary>The no-op channel used before a session exists / when solo. Broadcast is dropped; nothing
/// is ever received. Lets PartnerBelay hold a non-null channel unconditionally.</summary>
public sealed class NullModChannel : IModChannel
{
    public static readonly NullModChannel Instance = new();
    public void Broadcast(RopeState msg) { }
    public event Action<RopeState> OnReceived { add { } remove { } }
    public void Send(RopeRequest msg) { }
    public event Action<RopeRequest> OnRequest { add { } remove { } }
}
