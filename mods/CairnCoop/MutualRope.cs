using System;
using System.Collections.Generic;

namespace CairnCoop;

/// <summary>
/// The mutual-rope announce/seq/staleness protocol — the FIX-1/2/5 correctness machine extracted out of
/// <see cref="PartnerBelay"/>. Roping is MUTUAL: I announce my rope-intent + hang-state and the partner
/// reciprocates. This owns the wire bookkeeping the reconciler depends on but does NOT itself reconcile:
///
///   • FIX-2 — latest-SENT wins: each <see cref="Announce"/> stamps a monotonic outbound seq; inbound
///     messages with seq ≤ that partner's high-water mark are dropped (reorder/dup on the unreliable
///     loopback).
///   • FIX-5 — self-heal on rejoin: <see cref="ResetPartnerSnapshot"/> drops that partner's high-water
///     mark + freshness so a rejoining peer whose counter restarts at 0 re-syncs instead of being
///     discarded forever.
///   • FIX-1 — freshness: <see cref="SnapshotStale"/> reports whether the last accepted snapshot from a
///     given partner is older than the staleness threshold, so the reconciler can demote a frozen/silent
///     partner.
///
/// All inbound bookkeeping (FIX-1/2/5 + the hang fact) is held PER PARTNER in <see cref="_partners"/>,
/// keyed by the remote sender's id — the foundation for N-player rope chains, where the local client
/// tracks rope/hang state for multiple partners at once. The outbound seq counter and the local id stay
/// singular: there is one of us announcing. With exactly one partner this is byte-identical to the old
/// singular state (a missing entry is the old "never heard" sentinel).
///
/// Depends only on the injected <see cref="Net.IModChannel"/> and the local id (threaded per-Tick). The
/// CONNECTION decision is owned by <see cref="RopeHandshake"/> (request/accept), NOT by this snapshot
/// stream — this carries only the hang/freshness bookkeeping the reconciler reads for a partner already in
/// the connected set. Per re/systems/coop/coop-fall-consistency.md §0.
/// </summary>
internal sealed class MutualRope
{
    private readonly Net.IModChannel _channel;
    private readonly Action<string> _log;

    /// <summary>Per-partner inbound bookkeeping, keyed by the remote sender's id. An ABSENT entry is the
    /// "never heard from this partner" sentinel — equivalent to the old singular defaults
    /// (<c>HaveInSeq=false</c>, <c>InSeqHighWater=0</c>, <c>LastAnnounceUtc=DateTime.MinValue</c>,
    /// <c>Hanging=false</c>). A class (not a mutable struct) so its fields can be read-modify-written in
    /// place without the value-type dictionary copy gotcha; partners are few, so the per-partner
    /// allocation is irrelevant.</summary>
    private sealed class PartnerState
    {
        /// <summary>FIX-2/5: highest inbound seq accepted from this partner. A message with seq ≤ this is a
        /// stale/duplicate/reordered copy and is dropped. Reset to 0 (entry removed) when the partner leaves
        /// / we switch off them, so a rejoining peer whose counter restarts at 0 isn't discarded forever.</summary>
        public uint InSeqHighWater;

        /// <summary>FIX-2: false until the first message from this partner is accepted (the first always
        /// passes — a peer could legitimately send seq 0 as the uninitialised default). Absent entry ≡ false.</summary>
        public bool HaveInSeq;

        /// <summary>FIX-1: wall-clock of the last announcement we ACCEPTED from this partner. The reconciler
        /// treats a partner whose snapshot is older than <see cref="StaleSnapshot"/> as NOT a valid belayer.
        /// DateTime.MinValue (or an absent entry) = never heard.</summary>
        public DateTime LastAnnounceUtc = DateTime.MinValue;

        /// <summary>Latest snapshot of whether this partner is weighting our rope (hanging on us) — recorded
        /// verbatim from their announcement (latest-wins), NOT an edge. Cleared when they un-rope
        /// (Connected=false) or leave the room.</summary>
        public bool Hanging;
    }

    /// <summary>FIX-1/2/5 + hang fact, one record per partner we've heard from. See <see cref="PartnerState"/>;
    /// an absent key is the "never heard" sentinel.</summary>
    private readonly Dictionary<int, PartnerState> _partners = new();

    /// <summary>Our id in the room, threaded from the reconciler each Tick so the receive filter can tell
    /// our own echo from a real partner announcement. Updated via <see cref="SetLocalId"/>.</summary>
    private int _localId;

    internal MutualRope(Net.IModChannel channel, Action<string> log)
    {
        _channel = channel ?? Net.NullModChannel.Instance;
        _log = log;
        _channel.OnReceived += OnRemoteRopeState;
    }

    /// <summary>Set the local room id (threaded from the driver each Tick).</summary>
    internal void SetLocalId(int localId) => _localId = localId;

    /// <summary>FIX-2: our SINGLE outbound sequence number, bumped on every <see cref="Announce"/> — there
    /// is one of us announcing regardless of how many partners we track. Each receiver keeps its own
    /// per-partner high-water mark of OUR seq and discards anything ≤ it (latest-SENT wins, not
    /// latest-arrived). Per re/systems/coop/coop-fall-consistency.md §0.</summary>
    private uint _outSeq;

    /// <summary>FIX-1 staleness threshold: ≈2× the 1 Hz heartbeat, so a single lost packet still
    /// self-heals on the next beat, but a genuinely silent/frozen partner is demoted to invalid-belayer
    /// within this bound. Must stay &gt; the heartbeat interval, and the heartbeat must remain
    /// UNCONDITIONAL (a change-only beat would trip this on a stable hang).</summary>
    internal static readonly TimeSpan StaleSnapshot = TimeSpan.FromSeconds(2.5);

    /// <summary>Latest snapshot of whether <paramref name="partnerId"/> is weighting the rope (hanging on
    /// us) — recorded verbatim from their announcement (latest-wins), NOT an edge we react to once. The
    /// reconciler reads it fresh each tick to derive the drain; a stale/lost packet only delays the next
    /// tick's truth. Cleared when they un-rope (their snapshot says Connected=false) or leave the room.
    /// FALSE when we've never heard from that partner (no entry) — the old singular default.</summary>
    internal bool PartnerHanging(int partnerId)
        => _partners.TryGetValue(partnerId, out var p) && p.Hanging;

    /// <summary>FIX-1 staleness gate: whether the last accepted snapshot from <paramref name="partnerId"/>
    /// is older than <see cref="StaleSnapshot"/> (or none was ever heard — no entry). The reconciler treats
    /// a stale partner as NOT a valid belayer. <paramref name="now"/> is supplied so the caller controls the
    /// clock. A missing entry is the "never heard" sentinel, identical to the old DateTime.MinValue case.</summary>
    internal bool SnapshotStale(int partnerId, DateTime now)
        => !_partners.TryGetValue(partnerId, out var p)
           || p.LastAnnounceUtc == DateTime.MinValue
           || now - p.LastAnnounceUtc > StaleSnapshot;

    /// <summary>Broadcast our current rope-intent + hang-state so the partner reciprocates the rope and
    /// knows whether we are weighting it. <paramref name="targetId"/> is the partner we are (or would be)
    /// roped to; -1 when none, which the partner side ignores. <paramref name="carry"/> is our <c>MyCarry</c>
    /// (the climber we roped UP to, or -1) — a SENDER property identical on every per-neighbour frame, carried
    /// purely so <see cref="TopologyTracker"/> can assemble the room-wide chain for the party HUD. Stamps a
    /// monotonic <see cref="_outSeq"/> (FIX-2) so the receiver can drop stale/reordered copies.</summary>
    internal void Announce(int targetId, bool enabled, bool weighting, int carry)
        => _channel.Broadcast(new Net.RopeState(_localId, targetId, enabled, weighting, ++_outSeq, carry));

    /// <summary>A partner announced their rope-state. If it targets US: record whether they are hanging on us
    /// (drives the drain) and stamp freshness. Connection is NOT decided here — that is the handshake's job
    /// (<see cref="RopeHandshake"/>); this only tracks the hang/freshness facts for a connected partner. FIX-2:
    /// drop stale/reordered copies by seq; FIX-1: stamp freshness so the reconciler can demote a silent
    /// partner.</summary>
    private void OnRemoteRopeState(Net.RopeState msg)
    {
        if (msg.SenderId == _localId || msg.PartnerId != _localId)
            return; // our own echo, or not about us

        if (!_partners.TryGetValue(msg.SenderId, out var p))
            _partners[msg.SenderId] = p = new PartnerState();

        // FIX-2: latest-SENT wins. Drop anything not newer than THIS partner's high-water mark (a
        // reorder/dup on the unreliable loopback). The first accepted message always passes (FIX-5 removes
        // the entry on a partner leave, so a rejoining peer restarting its counter re-syncs from its next
        // message).
        if (p.HaveInSeq && msg.Seq <= p.InSeqHighWater)
            return;
        p.InSeqHighWater = msg.Seq;
        p.HaveInSeq = true;

        // FIX-1: this snapshot is fresh as of now — the reconciler's staleness gate keys off this.
        p.LastAnnounceUtc = DateTime.UtcNow;

        p.Hanging = msg.Connected && msg.Hanging; // only a roped partner can hang on us
    }

    /// <summary>Clear ONLY <paramref name="partnerId"/>'s hang fact (not the seq high-water / freshness) —
    /// "anchor gone → nobody hangs on us". Called from anchor teardown, which must not reset the seq
    /// tracking (that is the heavier <see cref="ResetPartnerSnapshot"/>, reserved for a partner leave /
    /// switch). No-op when we've never heard from that partner.</summary>
    internal void ClearPartnerHanging(int partnerId)
    {
        if (_partners.TryGetValue(partnerId, out var p))
            p.Hanging = false;
    }

    /// <summary>FIX-5: drop <paramref name="partnerId"/>'s inbound seq high-water mark + freshness stamp +
    /// hang fact (the whole per-partner entry), so a rejoining peer (whose seq counter restarts) re-syncs
    /// instead of having every packet discarded. Removing the entry restores the "never heard" sentinel,
    /// identical to the old reset-to-defaults. Called when that partner leaves the roster / we switch off
    /// them / we tear that anchor down.</summary>
    internal void ResetPartnerSnapshot(int partnerId) => _partners.Remove(partnerId);
}
