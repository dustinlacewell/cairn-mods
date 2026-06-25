using System;

namespace CairnCoop.Net;

/// <summary>
/// CairnCoop's MOD-PRIVATE wire opcodes, kept OUT of <see cref="Wire"/> so the native codec stays pure
/// game-protocol framing and never knows a mod feature exists. The mod injects/consumes these; the relay
/// fans them out like a gamemode blob, and the mod's forwarder peels the S->C variant off BEFORE the game
/// socket sees it. Opcodes are chosen outside the game's ranges (game C->S = 1-9, S->C = 130-135) so they
/// can never collide with a native opcode. Carries the rope-connect intent so a fall is mutually roped:
/// see PartnerBelay / docs.
///
/// These values are ON THE WIRE — a changed value or reliability class breaks the protocol.
/// </summary>
public static class ModWire
{
    // C -> S: reliable, [int partnerId][byte connected][byte hanging][uint seq] (no secret; link-authenticated)
    public const byte CTSR_MOD_ROPE = 10;

    // S -> C: the relay's fan-out of CTSR_MOD_ROPE. The mod forwarder consumes this and never passes it
    // to the game's NetplayClient. reliable, [int senderId][int partnerId][byte connected][byte hanging][uint seq]
    public const byte STCR_MOD_ROPE = 136;

    // C -> S: reliable, [int targetId][byte verb][uint nonce] (no secret; link-authenticated). A discrete
    // rope-handshake EVENT (request/accept/reject/cancel) — the sibling of CTSR_MOD_ROPE's connection
    // SNAPSHOT. Reliable but NOT seq-deduped as a snapshot: each event is delivered exactly once.
    public const byte CTSR_MOD_ROPE_REQ = 11;

    // S -> C: the relay's fan-out of CTSR_MOD_ROPE_REQ. The mod forwarder consumes this and never passes it
    // to the game's NetplayClient. reliable, [int senderId][int targetId][byte verb][uint nonce]
    public const byte STCR_MOD_ROPE_REQ = 137;

    // NOTE: there is NO kick opcode. The host IS the relay (in-process on the host), and only the host can
    // kick, so the host mod calls RelayServer.KickMember(id) DIRECTLY — no wire round-trip. The eviction
    // itself is realised by the relay's standard STCU_RESET + roster re-broadcast, which the client handles.

    /// <summary>Whether a mod-private C->S opcode carries a reliable message id (matching the native
    /// reliability classification in <see cref="Wire.IsReliableClientToServer"/> for the same wire
    /// behavior — CTSR_MOD_ROPE and CTSR_MOD_ROPE_REQ are reliable, so the relay acks + dedupes them).</summary>
    public static bool IsReliableClientToServer(byte opcode) =>
        opcode is CTSR_MOD_ROPE or CTSR_MOD_ROPE_REQ;
}
