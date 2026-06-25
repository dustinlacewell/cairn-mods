using System;
using System.Collections.Generic;

namespace CairnCoop;

/// <summary>
/// The room-wide CHAIN TOPOLOGY recorder: a map of <c>climberId → theirCarryId</c> for EVERY climber in the
/// room (not just the ones we are roped to), assembled purely so the party HUD can render the full chain(s).
///
/// It is the BROADCAST sibling of <see cref="MutualRope"/>. Both subscribe to the same
/// <see cref="Net.IModChannel.OnReceived"/> stream, but their contracts are opposites:
///   • <see cref="MutualRope"/> records ADDRESSED facts — it early-returns on <c>PartnerId != localId</c>,
///     keeping hang/freshness only for partners roped to US.
///   • This records BROADCAST facts — it reads the sender's <see cref="Net.RopeState.Carry"/> off EVERY frame
///     regardless of addressing. The host already fans every <see cref="Net.RopeState"/> to all room members
///     (the Phase-4 joiner→joiner relay), so a single sender's announce reaches every client, and each client
///     converges to the same room-wide carry graph.
///
/// Mixing this into MutualRope would break that class's "addressed-to-me only" invariant, so it stays a
/// sibling. The local client's OWN carry is NOT recorded here (we don't receive our own broadcast except via
/// host-loopback); the HUD reads it straight from <see cref="RopeHandshake.MyCarry"/>.
///
/// Staleness: a sender that stops announcing (left the room / frozen / unroped-to-nobody) ages out of the map
/// after <see cref="MutualRope.StaleSnapshot"/> — the same freshness window the reconciler uses — so a departed
/// climber's edge disappears from the HUD on its own, no roster sweep needed.
/// </summary>
internal sealed class TopologyTracker
{
    /// <summary>What we know room-wide about one climber: the carry they roped UP to (-1 = head) and whether
    /// they are currently HANGING (weighting their own rope — caught/hanging/abseiling/dead). Both are SENDER
    /// properties identical on every per-neighbour frame, so any arriving frame carries them. Returned by
    /// <see cref="Snapshot"/>.</summary>
    internal readonly struct Node
    {
        public readonly int Carry;
        public readonly bool Hanging;
        public Node(int carry, bool hanging) { Carry = carry; Hanging = hanging; }
    }

    /// <summary>Per-sender topology record: their announced carry edge + hang fact + the bookkeeping to keep it
    /// fresh and reorder-safe. A class (not a struct) so fields read-modify-write in place without the dictionary
    /// copy gotcha; senders are few.</summary>
    private sealed class Edge
    {
        public int Carry = -1;                       // the sender's MyCarry (the climber it roped UP to), -1 = head
        public bool Hanging;                         // the sender's own weighting fact (already on every RopeState)
        public uint InSeqHighWater;                  // FIX-2: highest accepted seq from this sender
        public bool HaveInSeq;                       // false until the first frame is accepted
        public DateTime LastSeenUtc = DateTime.MinValue; // freshness: prune when older than StaleSnapshot
    }

    /// <summary>senderId → their carry edge + hang + freshness. An absent key = never heard / aged out.</summary>
    private readonly Dictionary<int, Edge> _edges = new();
    private readonly Action<string> _log;

    internal TopologyTracker(Net.IModChannel channel, Action<string> log)
    {
        _log = log;
        (channel ?? Net.NullModChannel.Instance).OnReceived += OnRemoteRopeState;
    }

    /// <summary>Record the sender's carry edge off EVERY received frame (no addressed filter — that is the whole
    /// point). Per-sender seq dedup (latest-SENT wins, like MutualRope) + freshness stamp. Carry is identical on
    /// every per-neighbour frame a sender emits, so any arriving frame carries the full fact.</summary>
    private void OnRemoteRopeState(Net.RopeState msg)
    {
        if (msg.SenderId <= 0)
            return; // a malformed / un-stamped sender; nothing to record

        if (!_edges.TryGetValue(msg.SenderId, out var e))
            _edges[msg.SenderId] = e = new Edge();

        // FIX-2: drop stale/reordered copies by this sender's high-water mark (the first accepted frame passes).
        if (e.HaveInSeq && msg.Seq <= e.InSeqHighWater)
            return;
        e.InSeqHighWater = msg.Seq;
        e.HaveInSeq = true;
        e.Carry = msg.Carry;
        // Hanging is meaningful only while the sender is actually roped (Connected); an unroped sender can't be
        // weighting a rope. Mirrors MutualRope's `msg.Connected && msg.Hanging`.
        e.Hanging = msg.Connected && msg.Hanging;
        e.LastSeenUtc = DateTime.UtcNow;
    }

    /// <summary>The fresh room-wide graph: <c>senderId → (carry, hanging)</c> for every climber whose last
    /// announce is within <see cref="MutualRope.StaleSnapshot"/>. Stale senders are PRUNED from the backing store
    /// as they are encountered, so a departed climber self-evicts. The local client's own node is NOT here —
    /// merge it in from <see cref="RopeHandshake.MyCarry"/> + the local weighting at the read site.</summary>
    internal Dictionary<int, Node> Snapshot(DateTime now)
    {
        var result = new Dictionary<int, Node>();
        List<int> stale = null;
        foreach (var kv in _edges)
        {
            if (now - kv.Value.LastSeenUtc > MutualRope.StaleSnapshot)
                (stale ??= new List<int>()).Add(kv.Key);
            else
                result[kv.Key] = new Node(kv.Value.Carry, kv.Value.Hanging);
        }
        if (stale != null)
            foreach (int id in stale)
                _edges.Remove(id);
        return result;
    }
}
