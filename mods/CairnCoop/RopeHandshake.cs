using System;
using System.Collections.Generic;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// The request/accept/reject CONNECTION machine — the production replacement for proximity auto-rope. A rope
/// to a partner forms ONLY after an explicit request is accepted; nothing auto-connects. A SIBLING to
/// <see cref="MutualRope"/> (which keeps doing the per-partner hang-snapshot / seq / freshness bookkeeping for
/// partners we ARE connected to); this owns WHO we are connected to, driven solely by the handshake events on
/// <see cref="Net.IModChannel"/>. See re/systems/coop/coop-rope-request-design.md §"Step 3+4 spec".
///
/// Three per-partner sets, all keyed by the remote partner's id:
///   • <see cref="_outgoing"/> — requests WE sent, awaiting their Accept/Reject (target id → our nonce).
///   • <see cref="_incoming"/> — requests we RECEIVED, awaiting OUR accept (sender id → their nonce). Drives
///     the diegetic "accept rope from X" prompt on that sender's ghost.
///   • <see cref="_connected"/> — the accepted, live, mutually-agreed ropes. THIS set replaces
///     <see cref="PartnerBelay.Enabled"/>-as-proximity: PartnerBelay ropes to a partner iff it is in here.
///
/// Connection is symmetric and lands only after the accept round-trips: the requester adds the target to
/// <see cref="_connected"/> on RECEIVING the Accept; the accepter adds the sender on SENDING it. A
/// Reject/Cancel/partner-leave removes from the relevant set.
///
/// Events: <see cref="OnIncomingRequest"/> (a new request to accept — the ghost-prompt layer lights up) and
/// <see cref="OnConnectionChanged"/> (the connected set changed — PartnerBelay/UI may refresh). Reads happen
/// on the main thread (gesture + reconciler); inbound events arrive already marshalled to main by
/// <see cref="ModRopeRouter"/>.
/// </summary>
internal sealed class RopeHandshake
{
    /// <summary>How long a pending request (incoming OR outgoing) lives before it ages out. Drives the HUD
    /// countdown and the symmetric expiry: the requester stops re-broadcasting at this age, so the receiver's
    /// entry ages out a beat later. See <see cref="ExpireStale"/>.</summary>
    internal const float RequestTimeoutSeconds = 25f;

    /// <summary>A pending request with the timing the HUD/expiry need: the correlation nonce plus a timestamp,
    /// in <see cref="Time.realtimeSinceStartup"/> (main-thread monotonic clock — all handshake reads/writes
    /// happen on main, so this is safe). Asymmetric by design: an INCOMING <c>At</c> is refreshed on every
    /// received (re-sent) Request, so it never ages out under a still-willing sender; an OUTGOING <c>At</c> is
    /// stamped ONCE at <see cref="SendRequest"/> and NOT refreshed by <see cref="RepublishPending"/> — that hard
    /// 25 s ceiling on the sender's side is what drives the symmetric drop (sender stops re-broadcasting →
    /// receiver's refreshed entry ages out a beat later).</summary>
    private struct Pending
    {
        public uint Nonce;
        public float At;
        public Pending(uint nonce, float at) { Nonce = nonce; At = at; }
    }

    private readonly Net.IModChannel _channel;
    private readonly Action<string> _log;

    /// <summary>Requests WE sent, awaiting the target's Accept/Reject — partner id → nonce + last-stamp time.</summary>
    private readonly Dictionary<int, Pending> _outgoing = new();

    /// <summary>Requests we RECEIVED, awaiting OUR accept — sender id → their nonce + receive time (echoed back
    /// on our Accept so they can correlate it to the request; the time drives the HUD countdown/expiry).</summary>
    private readonly Dictionary<int, Pending> _incoming = new();

    /// <summary>The accepted set: partner ids we hold a live, mutually-accepted rope to. Replaces
    /// proximity-derived connection.</summary>
    private readonly HashSet<int> _connected = new();

    /// <summary>Partner ids WE accepted (we sent the Accept). Distinguishes "we accepted them" from "they
    /// accepted us": the loopback transport is best-effort UDP, so a dropped Accept would leave a one-sided
    /// <see cref="_connected"/> with no self-heal. <see cref="RepublishPending"/> re-sends OUR Accept for
    /// these ids on the 1 Hz beat (idempotent — the peer re-runs its Accept branch, a no-op once connected).
    /// For partners WE requested and THEY accepted we re-send nothing — their Accept re-send heals that side.
    /// Populated in <see cref="Accept"/>, cleared in <see cref="ClearPartner"/>/<see cref="Disconnect"/> and
    /// on a Cancel-received.</summary>
    private readonly HashSet<int> _acceptedByUs = new();

    /// <summary>Partner ids WE requested a rope FROM (we sent the Request). This is the directed carry edge:
    /// in the chain model, requesting-to-attach to X means X is MY CARRY (X catches me if I fall). The
    /// requested-bit is otherwise LOST when the connection forms (the Accept-received branch does
    /// <c>_outgoing.Remove</c> then <c>_connected.Add</c>, so <see cref="_connected"/> alone can't tell carry
    /// from dependent). We record it explicitly here, populated in <see cref="SendRequest"/> (the only path
    /// that sends a Request), mirror-cleared everywhere <see cref="_acceptedByUs"/> is. <see cref="MyCarry"/> reads
    /// it; each climber requests ≤1 carry so the connected ∩ requested set is a singleton (or empty for the
    /// chain head).</summary>
    private readonly HashSet<int> _requestedByUs = new();

    /// <summary>Our id in the room, threaded from the reconciler each Tick so the inbound filter can tell an
    /// event addressed to us from one about another pair. Updated via <see cref="SetLocalId"/>.</summary>
    private int _localId;

    /// <summary>Our single outgoing nonce, bumped per request — correlates an accept/reject back to the
    /// request it answers. Not a dedup high-water (each event is delivered once); purely a correlation tag.</summary>
    private uint _nextNonce = 1;

    /// <summary>Raised when a NEW rope request arrives addressed to us — the gesture/prompt layer lights an
    /// "accept rope from X" prompt on that sender's ghost. The argument is the requester's id.</summary>
    internal event Action<int> OnIncomingRequest;

    /// <summary>Raised whenever the <see cref="_connected"/> set changes (accept landed, or a partner
    /// dropped) so PartnerBelay/UI can react. No argument: consumers read the set fresh.</summary>
    internal event Action OnConnectionChanged;

    /// <summary>Raised whenever the <see cref="_incoming"/> SET changes membership (a new request staged, or
    /// one accepted/rejected/cancelled/expired). NOT raised for a mere timestamp refresh on a re-sent duplicate
    /// (membership unchanged — only the countdown moves). Currently UNSUBSCRIBED: the HUD polls
    /// <see cref="PendingIncoming"/> every frame and diffs by id rather than reacting to this. Kept as a cheap
    /// push seam for any future consumer that wants edge-notification instead of polling. No argument.</summary>
    internal event Action OnIncomingChanged;

    internal RopeHandshake(Net.IModChannel channel, Action<string> log)
    {
        _channel = channel ?? Net.NullModChannel.Instance;
        _log = log;
        _channel.OnRequest += OnRemoteRequest;
    }

    /// <summary>Set the local room id (threaded from the driver each Tick, like <see cref="MutualRope"/>).</summary>
    internal void SetLocalId(int localId) => _localId = localId;

    // --- read surface (gesture + reconciler) -------------------------------------------------------

    /// <summary>Whether we hold a live, mutually-accepted rope to <paramref name="partnerId"/>.</summary>
    internal bool IsConnected(int partnerId) => _connected.Contains(partnerId);

    /// <summary>Whether <paramref name="partnerId"/> has an UNANSWERED request to us (we can accept it).</summary>
    internal bool HasIncoming(int partnerId) => _incoming.ContainsKey(partnerId);

    /// <summary>Whether WE have an unanswered request out to <paramref name="partnerId"/>.</summary>
    internal bool HasOutgoing(int partnerId) => _outgoing.ContainsKey(partnerId);

    /// <summary>The HUD read-API: every incoming (unaccepted) request with its remaining lifetime, so the
    /// overlay can list "&lt;name&gt; wants to rope · &lt;secsLeft&gt;s". <paramref name="now"/> is the caller's
    /// <see cref="Time.realtimeSinceStartup"/> sample (the HUD ticks per-frame). <c>secondsLeft</c> can briefly
    /// go slightly negative between an expiry-due entry and the next <see cref="ExpireStale"/>; callers should
    /// clamp for display. Yields a snapshot-safe copy (no live dictionary enumeration).</summary>
    internal IEnumerable<(int id, uint nonce, float secondsLeft)> PendingIncoming(float now)
    {
        var rows = new List<(int, uint, float)>(_incoming.Count);
        foreach (var kv in _incoming)
            rows.Add((kv.Key, kv.Value.Nonce, RequestTimeoutSeconds - (now - kv.Value.At)));
        return rows;
    }

    /// <summary>Age-out pass for pending requests, called each frame from the lifecycle (<see cref="Core"/>).
    /// Symmetric expiry, driven by the SENDER going quiet:
    ///   • Drop any <see cref="_outgoing"/> Request older than <see cref="RequestTimeoutSeconds"/>. This stops
    ///     <see cref="RepublishPending"/> re-broadcasting it, so the RECEIVER stops getting refreshed and its
    ///     entry then ages out too — A must re-request.
    ///   • Drop any <see cref="_incoming"/> entry older than the timeout (the sender stopped re-sending) and
    ///     fire <see cref="OnIncomingChanged"/> so the HUD drops its row.
    /// <paramref name="now"/> is <see cref="Time.realtimeSinceStartup"/>. Cheap: two small-dictionary sweeps.</summary>
    internal void ExpireStale(float now)
    {
        List<int> dropOut = null;
        foreach (var kv in _outgoing)
            if (now - kv.Value.At >= RequestTimeoutSeconds)
                (dropOut ??= new List<int>()).Add(kv.Key);
        if (dropOut != null)
            foreach (int id in dropOut)
            {
                _outgoing.Remove(id);
                _log($"rope: outgoing request to #{id} expired ({RequestTimeoutSeconds:0}s) — re-request to retry");
            }

        List<int> dropIn = null;
        foreach (var kv in _incoming)
            if (now - kv.Value.At >= RequestTimeoutSeconds)
                (dropIn ??= new List<int>()).Add(kv.Key);
        if (dropIn != null)
        {
            foreach (int id in dropIn)
            {
                _incoming.Remove(id);
                _log($"rope: incoming request from #{id} timed out");
            }
            OnIncomingChanged?.Invoke();
        }
    }

    // --- local actions (the gesture / wedge drive these) -------------------------------------------

    /// <summary>Send a rope REQUEST to <paramref name="targetId"/> and record it as outgoing. Idempotent in
    /// spirit: re-requesting an already-pending or already-connected partner just re-sends (a lost request
    /// re-fires). Never requests ourselves.</summary>
    internal void SendRequest(int targetId)
    {
        if (targetId < 0 || targetId == _localId)
            return;
        uint nonce = _nextNonce++;
        _outgoing[targetId] = new Pending(nonce, Time.realtimeSinceStartup);
        _requestedByUs.Add(targetId); // directed carry edge: the one I request IS my carry
        _channel.Send(new Net.RopeRequest(_localId, targetId, Net.RopeRequestVerb.Request, nonce));
        _log($"rope: requested rope with #{targetId} (nonce {nonce})");
    }

    /// <summary>Accept an incoming request from <paramref name="senderId"/>: send Accept (echoing their
    /// nonce) and move them from <see cref="_incoming"/> into <see cref="_connected"/>. The rope forms on our
    /// side now; the requester forms theirs on receiving the Accept. No-op if there is no incoming request.</summary>
    internal void Accept(int senderId)
    {
        if (!_incoming.TryGetValue(senderId, out Pending pending))
            return;
        _incoming.Remove(senderId);
        OnIncomingChanged?.Invoke(); // an incoming request left the set — the HUD drops its row
        _acceptedByUs.Add(senderId); // OUR Accept — RepublishPending re-sends it at 1 Hz until they ack
        _channel.Send(new Net.RopeRequest(_localId, senderId, Net.RopeRequestVerb.Accept, pending.Nonce));
        if (_connected.Add(senderId))
        {
            _log($"rope: accepted #{senderId} — connected");
            OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>Reject an incoming request from <paramref name="senderId"/>: send Reject (echoing their
    /// nonce) and drop it from <see cref="_incoming"/>. No-op if there is no incoming request.</summary>
    internal void Reject(int senderId)
    {
        if (!_incoming.TryGetValue(senderId, out Pending pending))
            return;
        _incoming.Remove(senderId);
        OnIncomingChanged?.Invoke(); // an incoming request left the set — the HUD drops its row
        _channel.Send(new Net.RopeRequest(_localId, senderId, Net.RopeRequestVerb.Reject, pending.Nonce));
        _log($"rope: rejected #{senderId}");
    }

    /// <summary>Disconnect (unrope) from <paramref name="partnerId"/>: send Cancel and remove from
    /// <see cref="_connected"/>. The partner drops their side on receiving the Cancel. Also clears any
    /// pending outgoing request to them (Cancel doubles as a request-cancel).</summary>
    internal void Disconnect(int partnerId)
    {
        bool wasConnected = _connected.Remove(partnerId);
        _outgoing.Remove(partnerId);
        _acceptedByUs.Remove(partnerId); // stop re-sending OUR Accept — the rope is gone
        _requestedByUs.Remove(partnerId); // clear the carry edge — the rope is gone
        _channel.Send(new Net.RopeRequest(_localId, partnerId, Net.RopeRequestVerb.Cancel, 0));
        if (wasConnected)
        {
            _log($"rope: disconnected from #{partnerId}");
            OnConnectionChanged?.Invoke();
        }
    }

    /// <summary>Idempotent 1 Hz heartbeat that heals a dropped Request or Accept on the best-effort loopback
    /// transport — the handshake's analogue of <see cref="MutualRope.Announce"/>'s 1 Hz snapshot republish.
    /// Re-sends only what WE authoritatively own:
    ///   • every pending <see cref="_outgoing"/> Request (peer must learn of the request; cleared once it
    ///     resolves to Accept/Reject, so the re-send is bounded);
    ///   • our Accept for every id in <see cref="_acceptedByUs"/> (peer must learn we accepted; cleared on
    ///     Cancel/Disconnect/ClearPartner).
    /// Both are no-ops on the peer once it has processed them (OnRemoteRequest re-runs the same branch:
    /// Accept just re-adds to a set already holding the id; a duplicate Request is dropped without re-firing
    /// the prompt — see the Request branch's new-only guard). We do NOT re-send incoming requests (not ours)
    /// nor an Accept for a partner WHO accepted US (they re-send their own Accept). Called from
    /// <see cref="PartnerBelay.Tick"/>'s 1 Hz beat. Cheap: a handful of re-Sends.</summary>
    internal void RepublishPending()
    {
        foreach (var kv in _outgoing)
            _channel.Send(new Net.RopeRequest(_localId, kv.Key, Net.RopeRequestVerb.Request, kv.Value.Nonce));
        foreach (int senderId in _acceptedByUs)
            _channel.Send(new Net.RopeRequest(_localId, senderId, Net.RopeRequestVerb.Accept, 0));
    }

    // --- inbound ----------------------------------------------------------------------------------

    /// <summary>A rope-handshake event arrived (already marshalled to main). Drop anything not addressed to
    /// us, then dispatch by verb:
    ///   • Request → record it as incoming + fire <see cref="OnIncomingRequest"/> (light the accept-prompt).
    ///   • Accept  → move the sender from outgoing into connected + fire <see cref="OnConnectionChanged"/>.
    ///   • Reject  → drop the sender from outgoing (request declined).
    ///   • Cancel  → drop the sender from connected/incoming/outgoing + fire <see cref="OnConnectionChanged"/>
    ///               if a live rope went away (partner unroped or cancelled).
    /// </summary>
    private void OnRemoteRequest(Net.RopeRequest msg)
    {
        if (msg.SenderId == _localId || msg.TargetId != _localId)
            return; // our own echo, or not addressed to us

        switch (msg.Verb)
        {
            case Net.RopeRequestVerb.Request:
                // A re-sent Request from a partner we ALREADY accepted (in _connected) is their RepublishPending
                // healing a dropped Accept — they don't yet know we're connected. Re-send OUR Accept and drop
                // it; never re-stage it as a fresh incoming.
                if (_connected.Contains(msg.SenderId))
                {
                    _channel.Send(new Net.RopeRequest(_localId, msg.SenderId, Net.RopeRequestVerb.Accept, msg.Nonce));
                    break;
                }
                // Fire OnIncomingRequest only on a GENUINELY-NEW request: RepublishPending re-sends the
                // requester's Request at 1 Hz to heal a dropped packet, so a duplicate must be a no-op and
                // MUST NOT re-light the accept-prompt / re-spam the log every second. A re-sent Request with
                // a new nonce just refreshes the stored nonce (the latest correlation tag wins) AND refreshes
                // the timestamp — an actively-re-broadcast request must not age out on the receiver while the
                // sender still wants it. Expiry is therefore driven by the SENDER going quiet (their outgoing
                // expired → they stopped re-sending → this entry ages out ~1 beat later in ExpireStale).
                bool newIncoming = !_incoming.ContainsKey(msg.SenderId);
                _incoming[msg.SenderId] = new Pending(msg.Nonce, Time.realtimeSinceStartup);
                if (newIncoming)
                {
                    _log($"rope: incoming request from #{msg.SenderId}");
                    OnIncomingRequest?.Invoke(msg.SenderId);
                    OnIncomingChanged?.Invoke(); // a new incoming entered the set — the HUD adds its row
                }
                break;

            case Net.RopeRequestVerb.Accept:
                _outgoing.Remove(msg.SenderId);
                if (_connected.Add(msg.SenderId))
                {
                    _log($"rope: #{msg.SenderId} accepted — connected");
                    OnConnectionChanged?.Invoke();
                }
                break;

            case Net.RopeRequestVerb.Reject:
                _requestedByUs.Remove(msg.SenderId); // our carry request was refused — no longer my carry
                if (_outgoing.Remove(msg.SenderId))
                    _log($"rope: #{msg.SenderId} rejected our request");
                break;

            case Net.RopeRequestVerb.Cancel:
                if (_incoming.Remove(msg.SenderId))
                    OnIncomingChanged?.Invoke(); // a pending incoming was cancelled — the HUD drops its row
                _outgoing.Remove(msg.SenderId);
                _acceptedByUs.Remove(msg.SenderId); // they unroped — stop re-sending OUR Accept
                _requestedByUs.Remove(msg.SenderId); // they unroped — clear the carry edge too
                if (_connected.Remove(msg.SenderId))
                {
                    _log($"rope: #{msg.SenderId} unroped — releasing");
                    OnConnectionChanged?.Invoke();
                }
                break;
        }
    }

    /// <summary>Roster-leave cleanup: drop <paramref name="partnerId"/> from all three sets so a departed
    /// peer leaves no stale pending/connected state behind. Fires <see cref="OnConnectionChanged"/> only if a
    /// live rope was removed.</summary>
    internal void ClearPartner(int partnerId)
    {
        if (_incoming.Remove(partnerId))
            OnIncomingChanged?.Invoke(); // a departed peer's pending request left the set — the HUD drops its row
        _outgoing.Remove(partnerId);
        _acceptedByUs.Remove(partnerId); // departed peer — stop re-sending OUR Accept
        _requestedByUs.Remove(partnerId); // departed peer — clear the carry edge
        if (_connected.Remove(partnerId))
            OnConnectionChanged?.Invoke();
    }

    // ── Carry graph (the directed chain edges) ──────────────────────────────────────────────────────────

    /// <summary>MY CARRY — the single connected partner I requested-to-attach to (X catches me if I fall), or
    /// -1 if I'm the chain head (I requested nobody). The connected ∩ requested set is a singleton by the
    /// model (each climber requests ≤1 carry); returns the first such id defensively.</summary>
    internal int MyCarry
    {
        get
        {
            foreach (var id in _connected)
                if (_requestedByUs.Contains(id))
                    return id;
            return -1;
        }
    }

    /// <summary>MY DEPENDENTS — the connected partners who requested ME (I accepted them; I carry them). The
    /// drain I bear sums over those of these that are hanging on my rope. Allocates a small list per call;
    /// callers read it once per tick.</summary>
    internal System.Collections.Generic.List<int> Dependents()
    {
        var deps = new System.Collections.Generic.List<int>();
        foreach (var id in _connected)
            if (_acceptedByUs.Contains(id))
                deps.Add(id);
        return deps;
    }

    /// <summary>The climber I ANCHOR to. Normally <see cref="MyCarry"/> (the one I requested — it catches me).
    /// For the chain HEAD (no carry), it falls back to a connected DEPENDENT — the climber roped to me from
    /// below — so the head is belayed by the second climber rather than free-falling. (User decision: since
    /// co-op is local, the head depends on the second climber.) For a 2-player pair A→B this makes B anchor
    /// back to A, restoring the old symmetric mutual catch. Returns -1 only if I have neither a carry nor any
    /// dependent (truly alone). Picks the lowest-id dependent for determinism when several exist.</summary>
    internal int EffectiveCarry
    {
        get
        {
            int carry = MyCarry;
            if (carry >= 0)
                return carry;
            int best = -1;
            foreach (var id in _connected)
                if (_acceptedByUs.Contains(id) && (best < 0 || id < best))
                    best = id;
            return best;
        }
    }

    /// <summary>Snapshot of every currently-connected partner id (carry + all dependents). Used by the
    /// reconciler's debounced roster-departure sweep. Allocates a small list per call.</summary>
    internal System.Collections.Generic.List<int> ConnectedIds()
    {
        var ids = new System.Collections.Generic.List<int>();
        foreach (var id in _connected)
            ids.Add(id);
        return ids;
    }
}
