using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// Owns the free-solo partner anchor: the local climber's belay to the remote partner, and the
/// fall behaviour that hangs from it. The partner IS the piton and the LOCAL CLIMBOT (via
/// <see cref="BelayRig"/>) is the belayer — every instance anchors itself symmetrically and
/// locally, so a catch can never be one-sided and nothing about the physics path is replicated.
///
/// The hang/fall logic is an idempotent reconciler (<see cref="Tick"/>, 1 Hz), not an edge machine:
/// each tick observes a few facts, derives the desired state from the invariant "my anchor exists
/// iff my partner is a valid wall belayer", and drives toward it — so a lost message or a cross-
/// client race self-corrects on the next tick. Concretely: find the closest partner, keep ONE local
/// quickdraw above their frame position on the personal rope and reel it to the climber↔partner gap
/// WHILE they're wall-supported, tear it down the instant they aren't (so a hang/fall/death drops me
/// onto the game's own fall path), and drain my limb stamina while I'm on the wall bearing a hanging
/// partner — the team-fall emerges from that drain. <see cref="GameDriver"/> drives this and supplies
/// the gamemode/client; the belay logic and its state live here.
///
/// Why the moving holder is teleported at only 1 Hz with a generous deadband: moving a rope holder
/// re-sorts/re-splits the Obi rope, and doing it at high frequency fights the solver and freezes the
/// game (verified). Fall-distance, length, and the climbot's irrelevance to the catch are documented
/// in re/systems/climber/fall-distance.md and re/systems/climbot/rope-length.md.
/// </summary>
internal sealed class PartnerBelay
{
    // Anchor sits at the partner's torso — low enough to hide against the body (was +1.0 m, which
    // floated it over their head), but still offset off the exact harness point so it never sits
    // EXACTLY on the rope's end attachment (a coincident holder makes the final rope part
    // zero-length). Teleported only on real drift — a holder moving every frame re-sorts the rope.
    private static readonly Vector3 AnchorOffset = new(0f, 0.4f, 0f);
    // Re-place the anchor once the partner has drifted >0.5 m from it (0.25 = 0.5²). Moving the
    // piton re-sorts/re-splits the Obi rope, so this is the deadband that caps how often that
    // happens; it fires at most once per 1 Hz anchor tick. A 15 Hz follow froze the game (high-rate
    // holder moves fight the solver — same failure class as the climbot-teleport freeze), so the
    // cadence stays 1 Hz and the deadband stays generous. Was 1.0 m (visibly laggy); 0.5 m tracks
    // tighter without thrashing the rope.
    private const float AnchorMoveThresholdSqr = 0.25f;

    private readonly Action<string> _log;
    private readonly BelayRig _belayRig;
    private readonly MutualRope _mutual;
    private readonly RopeHandshake _handshake;
    private readonly RopeReel _reel;

    internal PartnerBelay(Action<string> log, BelayRig belayRig, Net.IModChannel channel, RopeHandshake handshake)
    {
        _log = log;
        _belayRig = belayRig;
        _reel = new RopeReel(belayRig, log);
        _mutual = new MutualRope(channel, log);
        _handshake = handshake;
        // Connection is now driven SOLELY by the request/accept handshake (no proximity auto-rope, no
        // partner-toggle mirror): Enabled is DERIVED each Tick from whether the resolved partner is in the
        // handshake's connected set. MutualRope keeps doing the per-partner hang-snapshot/seq/freshness for
        // connected partners only.
    }

    /// <summary>Whether the rope is connected to the partner this Tick — DERIVED from the handshake's
    /// connected set (the resolved partner ∈ connected), NOT a toggle. There is no proximity auto-rope: a
    /// rope exists iff a request to that partner was explicitly accepted. Recomputed at the top of each
    /// <see cref="Tick"/>; defaults false (no partner resolved / not connected).</summary>
    internal bool Enabled { get; private set; }

    /// <summary>Announce our current rope-intent + hang-state to EVERY connected neighbor (carry AND every
    /// dependent), not just our effective-carry. The announce is ADDRESSED — the receiver records it only if
    /// it targets them (MutualRope.OnRemoteRopeState drops msg.PartnerId != localId) — so in a chain a
    /// dependent below me NEVER learns my state unless I announce to it directly. Without this, a pure
    /// dependent (e.g. the chain's bottom climber, whose carrier announces only toward ITS own carry) sees its
    /// carrier as a STALE snapshot → not a valid belayer → never anchors (the matrix doc's "change (b)":
    /// rope-state must reach all neighbours). One addressed RopeState per connected partner; the seq stamp is
    /// per-message in <see cref="MutualRope"/> (FIX-2).</summary>
    private void Announce()
    {
        var ids = _handshake.ConnectedIds();
        if (ids.Count == 0)
            return;
        // Carry is a SENDER property (my MyCarry), identical on every per-neighbour frame; the receiver records
        // it into the room-wide topology (TopologyTracker → party HUD) off whichever frame arrives.
        int carry = _handshake.MyCarry;
        foreach (var id in ids)
            _mutual.Announce(id, Enabled, _weightingRope, carry);
    }

    /// <summary>
    /// DEATH = bilateral unlink. Called at the resurrection-ENTER (the death moment) via
    /// <see cref="GameDriver.StandDownBelayForRespawn"/>, NOT from the 1 Hz <see cref="Tick"/> — GameDriver
    /// SKIPS the tick while the rewind is in flight (CoopRevive.Active, GameDriver.cs), so the tick can
    /// never observe the death itself; a guard there is dead code. Drops the handshake CONNECTION so the
    /// climber returns from the edelweiss rewind UNLINKED (request+accept by hand to reconnect) — Disconnect
    /// sends a Cancel so a live partner releases the rope (a partner ALSO severs us independently once our
    /// snapshot goes stale, so this holds even if they are mid-reload). Tears the anchor down so nothing stale
    /// fights the rewind OR re-anchors to a still-"Walking"-on-the-net dead partner on the first post-revive
    /// tick. Idempotent.
    /// </summary>
    internal void UnlinkForRevive()
    {
        var ids = _handshake.ConnectedIds();
        if (ids.Count > 0)
        {
            foreach (var id in ids)
            {
                _handshake.Disconnect(id); // remove from connected + Cancel → a live partner releases
                _mutual.ResetPartnerSnapshot(id);
            }
            _log("belay: local revive — unlinked from all climbers (request+accept to reconnect)");
        }
        TearDownAll();             // unconditionally removes every anchor + clears RopeLengthGuard / FreeSoloRecoveryGate / HangStaminaSeam / PartnerHanging
    }

    /// <summary>The partner our announcements name — MY CARRY (the connected climber I requested). The
    /// handshake's carry graph decides; there is no proximity auto-rope and no closest-partner selection.</summary>
    private int _announceTargetId = -1;
    private DateTime _nextAnnounce;

    /// <summary>1 Hz gate for <see cref="RopeHandshake.RepublishPending"/> — the handshake's dropped-packet
    /// self-heal, a sibling to <see cref="_nextAnnounce"/>. Separate timer because pending handshake state
    /// (an outgoing request, our Accept) can outlive having a resolved announce target.</summary>
    private DateTime _nextHandshakeRepublish;

    /// <summary>Whether OUR local climber is currently weighting the securing rope (caught/hanging/
    /// abseiling/rope-ascending/dead). Recomputed each Tick; announced to the partner so THEY can drain
    /// while bearing us. An edge change forces an immediate announce (don't wait for the 1 Hz beat).</summary>
    private bool _weightingRope;

    /// <summary>The carry id we last ANNOUNCED, so a change in <c>MyCarry</c> (accept/disconnect) forces an
    /// immediate announce — the room-wide topology (party HUD) then reflects a new/dropped chain edge within a
    /// frame instead of waiting up to the 1 Hz heartbeat. Sentinel int.MinValue = nothing announced yet.</summary>
    private int _lastAnnouncedCarry = int.MinValue;

    /// <summary>The request/accept handshake that owns the connection set. The ghost gesture reads it per
    /// frame to pick which CONNECT interaction (request / accept) to surface on each partner's ghost, and
    /// drives it on a press. Unrope is NOT a ghost action — it's the LT+RT wedge (<see cref="UnropeAll"/>).</summary>
    internal RopeHandshake Handshake => _handshake;

    /// <summary>The LT+RT wedge's unrope action: DISCONNECT from every climber this one is roped to (carry above
    /// AND every dependent below) — a full "detach me from the chain". Each Disconnect sends a Cancel so each
    /// peer drops its side; Enabled goes false next tick (no connections) and the reconciler's !Enabled path tears
    /// every anchor down. Connection is formed only by the diegetic ghost reach, so the wedge is unrope-only.
    /// No-op when not connected to anyone.</summary>
    internal void UnropeAll()
    {
        var ids = _handshake.ConnectedIds();
        if (ids.Count == 0)
            return;
        foreach (var id in ids)
            _handshake.Disconnect(id);
    }

    /// <summary>TEST/automation entry (not the human path): resolve the partner straight from the live
    /// roster — NOT the Tick-resolved <see cref="_announceTargetId"/>, which can lag a beat — and run the
    /// gesture's three-way logic (connected → disconnect; incoming request from them → accept; else send a
    /// request). Calling it on BOTH sides completes the handshake without the diegetic ghost-reach: side A's
    /// call sends, side B's next call sees the incoming and accepts. Used by tools/repro to rope up
    /// programmatically. Returns a one-line status. No-op (returns "no-partner") when the roster is empty.</summary>
    internal string ForceRopeClosest(SharedRopeGamemode gamemode, NetplayClient client)
    {
        if (gamemode == null || client == null)
            return "no-session";
        int localId = client.LocalId;
        _handshake.SetLocalId(localId);
        Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> remotes;
        try { remotes = gamemode.GetRemotePlayers(); }
        catch (Exception) { return "roster-not-ready"; }
        int target = ClimberQuery.FirstRemoteId(remotes, localId);
        if (target < 0)
            return "no-partner";
        if (_handshake.IsConnected(target))
        {
            _handshake.Disconnect(target);
            return $"disconnect #{target}";
        }
        if (_handshake.HasIncoming(target))
        {
            _handshake.Accept(target);
            return $"accept #{target}";
        }
        _handshake.SendRequest(target);
        return $"request #{target}";
    }

    /// <summary>Live, read-only snapshot of the reconciler's last tick — every fact it OBSERVED and
    /// every state it DERIVED/DROVE — so the F4 "Belay" tab shows exactly what Tick decided this frame,
    /// not a re-derivation. Captured at the end of each <see cref="Tick"/>; defaults are the roped-off
    /// resting values. Pure diagnostics; nothing reads it back into the logic.</summary>
    internal BelaySnapshot Snapshot => _snapshot;
    private BelaySnapshot _snapshot;

    /// <summary>One tick's worth of reconciler state, for the diagnostic readout. Field names mirror the
    /// reconciler's locals so the tab and the code read the same.</summary>
    internal struct BelaySnapshot
    {
        public bool Enabled;               // rope toggled on
        public bool PartnerFound;          // a remote partner resolved this tick
        public int PartnerId;
        // OBSERVED facts
        public bool PartnerWallSupported;  // partner's net frame says firm/walking/climbing
        public bool PartnerDeadNet;        // partner's net PawnState == Dead
        public bool PartnerHanging;        // partner's announced snapshot: they weight the rope (on us)
        public bool IWeightRope;           // my local module says I weight my own rope (hanging/rappel/…)
        public string MyModule;            // my climbing-module type name
        // DERIVED desired state
        public bool PartnerValidBelayer;   // partnerWallSupported && !partnerHanging
        public bool AnchorDesired;
        public bool DrainDesired;
        // DRIVEN actuals
        public bool AnchorPresent;         // _anchors.Count > 0 after this tick (≥1 valid-belayer anchor up)
        public int AnchorCount;            // how many per-neighbor anchors are up after this tick
        public float DrainPerSecond;       // HangStaminaSeam.LoadPerSecond published
        public string LastAction;          // human label of the branch taken (created/torndown/held/fell/…)
    }

    // --- Anchor handles (the live pitons/quickdraws we own) -------------------------------------
    // PER-NEIGHBOR: one anchor per connected neighbor that is a valid belayer this tick. The invariant
    // is "a piton on my rope ⟺ that neighbor is a valid belayer right now", so failover is automatic:
    // when a neighbor goes invalid I RemoveAnchor(id) and native GetLastSecuringPiton falls over to a
    // surviving valid piton on the same securingRope (Phase 0 proven). Every piton present is a valid
    // catcher by construction, so it does not matter which one the substrate picks.
    /// <summary>One owned piton+quickdraw, keyed by the neighbor id it belays TO. The piton's transform
    /// is teleported to follow that neighbor's frame position.</summary>
    private sealed class Anchor
    {
        public Piton Piton;                // the moving anchor (teleported to follow its neighbor)
        public LogicalQuickdraw Quickdraw; // its clip on the personal securingRope
    }
    private readonly Dictionary<int, Anchor> _anchors = new();

    // SINGULAR (one securingRope; N quickdraws split it — Phase 0 proven). The rope, lifeline and
    // controller are the local climber's own and shared by every anchor; only the piton/quickdraw pair
    // is per-neighbor.
    private Lifeline _lifeline;
    private LogicalRope _rope;
    private ClimbingV2PawnController _controller;

    internal LogicalRope Rope => _rope;
    /// <summary>Any one owned quickdraw (or null). Used by SessionReadout's IsAttached probe and stray-
    /// cleanup, both of which only need a representative holder on our rope — any present anchor serves.</summary>
    internal LogicalQuickdraw Quickdraw
    {
        get
        {
            foreach (var a in _anchors.Values)
                return a.Quickdraw;
            return null;
        }
    }
    internal bool Active => _anchors.Count > 0;

    // The fall-latch + reel sub-machine (length authority + catch-distance freeze) lives in
    // <see cref="RopeReel"/> (_reel). Only the anchor-create failure-log cadence stays here.
    private DateTime _nextAnchorFailLog;

    /// <summary>FIX-4b: when the anchor first became un-desired while I was NOT weighting the rope (a
    /// partner flickering across the hang/wall boundary). Tearing the anchor down + recreating it each
    /// tick re-sorts the Obi rope (expensive, can freeze — see the class header). So when I'm on the wall
    /// (not hanging on it myself), debounce the teardown: hold the anchor through brief flicker, drop only
    /// after <see cref="BelayPolicy.AnchorDropDebounce"/>. NOT applied when I'm weighting the rope — there
    /// the drop is safety-critical (my belayer is gone and I'm hanging) and must be prompt. DateTime.MinValue
    /// = anchor currently desired. Threaded in/out of <see cref="BelayPolicy.Derive"/> (the timer logic is
    /// pure; only this stored value persists across ticks). PER-NEIGHBOR: keyed by the neighbor id the
    /// anchor belays to, so each neighbor flickers on its own debounce clock. An id absent from the dict
    /// reads as DateTime.MinValue (anchor currently desired) going into Derive.</summary>
    private readonly Dictionary<int, DateTime> _anchorUndesiredSinceUtc = new();

    /// <summary>Per-connected-partner "first observed absent from the roster" timestamp, for the debounced
    /// consent sweep (<see cref="SweepDepartedConnections"/>). The handshake connection lives on its own UDP
    /// sidechannel, a SEPARATE timeline from the netplay roster — a peer's net frame can be transiently absent
    /// (scene transition, a late/dropped frame, connection-formed-before-roster-replication on a rejoin) while
    /// the consented rope is perfectly live. So we do NOT drop the consent edge on a single roster miss (that
    /// permanently severed a valid carry — adversary bug #1); we drop it only after the peer stays absent for
    /// <see cref="ConnectionLeaveGrace"/>. Cleared the instant a peer reappears.</summary>
    private readonly Dictionary<int, DateTime> _connectedMissingSinceUtc = new();

    /// <summary>How long a connected partner must be CONTINUOUSLY absent from the roster before we drop the
    /// consent edge (ClearPartner). Covers transient frame gaps + scene transitions; a genuine leave persists
    /// well past it. Anchor teardown on a carry-miss is immediate (safe); only the consent-drop is debounced.</summary>
    private static readonly TimeSpan ConnectionLeaveGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Re-assert the local climbot belay after the game tears it down on a room join. Invoked from
    /// the SharedRopeGamemode.Init Postfix (same frame, before the torn-down state is observed) and
    /// as a 1 Hz backstop. Idempotent.
    /// </summary>
    internal void RestoreRig(SharedRopeGamemode gamemode, int localId)
    {
        var controller = ClimberQuery.FindLocalClimbController(gamemode, localId);
        if (controller != null)
            _belayRig.Restore(controller);
    }

    /// <summary>
    /// One maintenance tick (1 Hz): resolve the partner, create/keep/tear-down the anchor, and reel.
    /// Driven by <see cref="GameDriver"/> in the InRoom state, which supplies the live gamemode/client.
    /// </summary>
    internal void Tick(SharedRopeGamemode gamemode, NetplayClient client)
    {
        if (gamemode == null || client == null)
            return;
        int localId = client.LocalId;
        _mutual.SetLocalId(localId);

        // Partner = the CLOSEST remote player in the room (by frame position). The native
        // shared-rope attach is deliberately NOT involved (AttachClimberToClimber re-points both
        // sides' securingRope at a pair rope and evicts the climbot belayer); being in the room
        // together IS being roped. Position comes from the network-frame Position, NOT
        // GetHarness(partnerId).transform — remote pawns run with physics off and their harness
        // transform sits at far-away defaults.
        // Pull the roster once — the reconciler scans it below; roped OFF, this is the only work needed.
        Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> remotes;
        try { remotes = gamemode.GetRemotePlayers(); }
        catch (Exception) { return; } // roster not ready; the health probe handles persistent breakage
        _handshake.SetLocalId(localId);

        // Debounced consent sweep: drop the handshake connection for ANY connected partner (carry OR
        // dependent) that has been absent from the roster past the grace window. This is the ONLY general
        // departure detector — without it a dependent that crashes/drops without sending Cancel leaks its
        // _connected entry forever, and a rejoin with the same id auto-ropes with no fresh consent (FIX-5;
        // adversary bug #2). Debounced so a transient frame-miss never severs a live carry (bug #1).
        SweepDepartedConnections(remotes, localId);

        // CARRY GRAPH (announce target only): EFFECTIVE CARRY is who I name in the announce-target diagnostic.
        // It is NO LONGER who I anchor to — under the per-neighbor model I place a piton toward EVERY connected
        // neighbor that is a valid belayer (my carry AND each dependent on the wall), so failover is automatic.
        // (Dependents I bear the hang load for via drain, below; that's independent of who I anchor to.)
        _announceTargetId = _handshake.EffectiveCarry;

        // I am "enabled" (roped, eligible to hold anchors) iff I have at least one connected neighbor. Connected
        // by construction; re-check defensively against the connected set.
        Enabled = _handshake.ConnectedIds().Count > 0;

        // Publish OUR current rope-intent + whether we're weighting the rope, as a continuous SNAPSHOT
        // (latest-wins), not an edge event: every belay beat (1 Hz) plus immediately on change. The 1 Hz
        // republish is the idempotent heartbeat — a lost packet self-heals within a second and a
        // (re)joiner resyncs; the on-change publish is pure latency reduction over the same value. The
        // RECEIVER (OnRemoteRopeState) records it as a snapshot and the reconciler below reads it fresh
        // each tick, so a dropped or stale packet can only delay the next tick's truth, never desync it.
        bool weighting = Enabled && ClimberQuery.LocalClimberWeightingRope(gamemode, localId);
        // A change in either my weighting OR my carry edge forces an immediate announce (don't wait for the
        // 1 Hz beat): weighting drives the partner's drain; carry drives the room-wide topology / party HUD.
        bool snapshotChanged = weighting != _weightingRope || _handshake.MyCarry != _lastAnnouncedCarry;
        _weightingRope = weighting;
        // Announce to all connected neighbours whenever I have any (EffectiveCarry>=0 ⇔ I have a carry or a
        // dependent, i.e. at least one connection); on-change or on the 1 Hz heartbeat.
        if (_announceTargetId >= 0 && (snapshotChanged || DateTime.UtcNow >= _nextAnnounce))
        {
            _nextAnnounce = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            _lastAnnouncedCarry = _handshake.MyCarry;
            Announce();
        }

        // Heal a dropped Request/Accept on the best-effort loopback transport: the handshake's analogue of
        // the 1 Hz Announce republish above. A handshake event is one-shot (no per-tick snapshot to read
        // fresh), so without this re-send a dropped Accept leaves a permanent one-sided connection. Gated on
        // its own 1 Hz beat (separate from _nextAnnounce, which is suppressed once _announceTargetId < 0,
        // whereas pending handshake state can outlive having a resolved announce target). Idempotent + bounded
        // (see RopeHandshake.RepublishPending). Cheap: a handful of re-Sends, only while something is pending.
        if (DateTime.UtcNow >= _nextHandshakeRepublish)
        {
            _nextHandshakeRepublish = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            _handshake.RepublishPending();
        }

        // ── RECONCILER ──────────────────────────────────────────────────────────────────────────────
        // The hang/fall logic is a per-tick idempotent daemon, not an edge machine. Each tick: OBSERVE
        // the facts that matter, DERIVE the one true desired state from them (the pure boolean algebra in
        // <see cref="BelayPolicy"/>), and DRIVE toward it. No "remember what I did last", no announce edges
        // to react to — a lost message or a cross-client race just self-corrects on the next tick.
        //
        // The single invariant the whole thing turns on, now PER-NEIGHBOR:
        //     A PITON ON MY ROPE TOWARD NEIGHBOR X  ⟺  X IS A VALID WALL BELAYER RIGHT NOW.
        // I place one piton toward EVERY connected neighbor that is a valid belayer (my carry AND each
        // dependent on the wall). When a neighbor goes invalid (hangs / falls / dies / departs) I REMOVE its
        // piton; the native GetLastSecuringPiton then falls over to a surviving valid piton on the same rope —
        // automatic failover, every present piton a valid catcher by construction (Phase 0 proven). The
        // mutual-hang deadlock still dissolves: if I'm weighting the rope and NO neighbor is a valid belayer,
        // I DropToFall — computed from MY state, once, around the loop.

        // Diagnostic snapshot, stamped ONCE at the end as a projection of (facts, desired) — the F4
        // "Belay" tab then shows exactly what this tick observed/derived/drove. With multiple anchors the
        // carry-focused fields reflect the LAST neighbor the loop drove; AnchorCount is the true fleet size.
        var s = new BelaySnapshot { Enabled = Enabled, MyModule = ClimberQuery.LocalModuleName(gamemode, localId) };
        s.IWeightRope = _weightingRope;
        void Finish(string action)
        {
            s.AnchorPresent = _anchors.Count > 0;
            s.AnchorCount = _anchors.Count;
            s.DrainPerSecond = HangStaminaSeam.LoadPerSecond;
            s.LastAction = action;
            _snapshot = s;
        }

        // FACT 1 — am I roped? No connected neighbor collapses every desired state to "nothing": every anchor
        // gone, no drain. Handled first so an unconnected player does no per-neighbor work. TearDownAll clears
        // the seam; clear it explicitly when no anchors were up.
        if (!Enabled)
        {
            if (_anchors.Count > 0)
                TearDownAll();
            else
                HangStaminaSeam.Clear();
            _anchorUndesiredSinceUtc.Clear();
            Finish("roped off");
            return;
        }

        // MY GLOBAL state — computed ONCE, around the loop. The mutual-hang dissolution and the falling/respawn
        // hands-off are MY-state facts (not per-neighbor): if I'm weighting with no valid belayer I drop; if I'm
        // unsecured-falling/respawning the whole fleet comes off NOW.
        bool iAmUnsecuredFalling = ClimberQuery.LocalClimberUnsecuredFalling(gamemode, localId);
        var now = DateTime.UtcNow;

        // Roster index by id (one scan) so each desired-neighbor lookup is O(1). A neighbor absent from the
        // roster THIS tick is not a candidate belayer (its anchor, if any, gets removed below as failover); the
        // debounced SweepDepartedConnections owns dropping the consent edge on a sustained absence.
        var frames = new Dictionary<int, (bool wallSupported, bool deadNet, Vector3 pos)>();
        try
        {
            for (int r = 0; r < remotes.Count; r++)
            {
                var remote = remotes[r];
                if (remote == null)
                    continue;
                bool wall = remote.IsHoldingFirmly
                    || remote.PawnState == NetFrame.PawnStateType.Walking
                    || remote.PawnState == NetFrame.PawnStateType.Climbing;
                frames[remote.Id] = (wall, remote.PawnState == NetFrame.PawnStateType.Dead, remote.Position);
            }
        }
        catch (Exception)
        {
            Finish("scan failed");
            return; // roster went away mid-scan; recover next tick
        }

        // DESIRED NEIGHBORS = the connected neighbors that are VALID WALL BELAYERS this tick. The set is
        // {MyCarry if valid} ∪ {each Dependent that is a valid wall belayer}. Per neighbor I gather the SAME
        // facts the old single-partner path computed, build ObservedFacts, Derive (with a PER-NEIGHBOR debounce
        // timer from the dict), and Drive its action over _anchors[id]. NB: validity here is the FULL derive
        // (in roster + wall-supported + !hanging-on-me + !stale + !dead-as-hanger), so a neighbor in the
        // debounce-hold window still counts as desired and its anchor is held, not removed by the failover sweep.
        var desiredNeighbors = new HashSet<int>();
        bool iHaveAnyValidBelayer = false;
        bool snapshotProjected = false;
        string lastAction = "no neighbor desired";

        // Carry first, then dependents — deterministic order; the snapshot's carry-focused fields end up
        // reflecting whichever neighbor the loop drove last.
        var neighborIds = new List<int>();
        int myCarry = _handshake.MyCarry;
        if (myCarry >= 0)
            neighborIds.Add(myCarry);
        foreach (var depId in _handshake.Dependents())
            if (depId != myCarry)
                neighborIds.Add(depId);

        foreach (int nid in neighborIds)
        {
            bool found = frames.TryGetValue(nid, out var frame);

            // ★ TRANSIENT-MISS GRACE: a connected neighbor absent from the roster THIS tick is NOT instantly
            // invalid. The handshake connection rides its own UDP timeline, separate from the netplay roster, so
            // a neighbour's net frame can blink out for a tick (scene transition, a late/dropped frame, rejoin
            // replication lag) while the consented rope is perfectly live. Treating that as "invalid" would (a)
            // sweep away my still-good catch and (b) — if I'm hanging — force me into a real unsecured fall on a
            // single 1 Hz miss (the adversary-caught regression; same class as the Phase-3 carry-miss). So while
            // a missing neighbour is still inside ConnectionLeaveGrace (the same window SweepDepartedConnections
            // uses before dropping the consent edge), PRESUME it still valid: keep its anchor and count it as a
            // belayer. Past the grace, SweepDepartedConnections has ClearPartner'd it and it falls out naturally.
            if (!found)
            {
                bool withinGrace = _connectedMissingSinceUtc.TryGetValue(nid, out var missingSince)
                    && now - missingSince < ConnectionLeaveGrace;
                if (withinGrace)
                {
                    // Presume valid: hold this anchor (don't sweep it), and treat me as having a valid belayer
                    // so the global DropToFall can't fire on a transient blink. No anchor MOVE (no frame to
                    // chase) and no Derive — just keep what we have until the neighbour reappears or the grace
                    // elapses. We do NOT create a NEW anchor on a missing frame (no position to place it at).
                    if (_anchors.ContainsKey(nid))
                        desiredNeighbors.Add(nid);
                    iHaveAnyValidBelayer = true;
                    continue;
                }
                // Past grace (or never connected): fall through with found=false → not desired → swept.
            }

            // OBSERVE this neighbor's facts (mirrors the old single-partner Observe).
            var facts = new BelayPolicy.ObservedFacts
            {
                Enabled = true,
                PartnerFound = found,
                PartnerId = nid,
                PartnerPos = found ? frame.pos : default,
                PartnerWallSupported = found && frame.wallSupported,
                PartnerDeadNet = found && frame.deadNet,
                PartnerHanging = _mutual.PartnerHanging(nid),
                PartnerSnapshotStale = _mutual.SnapshotStale(nid, now),
                WeightingRope = _weightingRope,
                IAmUnsecuredFalling = iAmUnsecuredFalling,
                AnchorPresent = _anchors.ContainsKey(nid),
            };

            // DERIVE with this neighbor's own debounce timer (threaded in/out of the per-neighbor dict).
            _anchorUndesiredSinceUtc.TryGetValue(nid, out var since); // absent ⇒ default(DateTime) == MinValue
            var desired = BelayPolicy.Derive(facts, now, since);
            if (desired.AnchorUndesiredSince == DateTime.MinValue)
                _anchorUndesiredSinceUtc.Remove(nid);
            else
                _anchorUndesiredSinceUtc[nid] = desired.AnchorUndesiredSince;

            if (desired.PartnerValidBelayer)
                iHaveAnyValidBelayer = true;
            // KEEP this anchor this tick iff the anchor is desired OR we're in the on-wall debounce-hold window
            // (HoldDebounce: not-desired but held through brief flicker so we don't thrash the Obi rope). Both
            // exclude the neighbor from the failover sweep below; only a genuinely-undesired neighbor (debounce
            // elapsed, or off-wall) is dropped — that's TearDownDebounced, realised by the sweep removing it.
            if (desired.AnchorDesired || desired.Action == BelayPolicy.AnchorAction.HoldDebounce)
                desiredNeighbors.Add(nid);

            // DRIVE this neighbor's action over its own anchor. The MY-GLOBAL actions (DropToFall /
            // TearDownPrompt) are NOT driven here — they're handled once, below, from my state. A per-neighbor
            // Derive can still RETURN those (it reads my WeightingRope/IAmUnsecuredFalling), so we only act on
            // the per-neighbor verbs here and let the global block own the drop/hands-off.
            lastAction = DriveNeighbor(desired, gamemode, localId, nid, found ? frame.pos : default);

            // Carry-focused snapshot fields: project the FIRST neighbor (carry when present, neighborIds is
            // carry-first), so the F4 tab shows the carry's derive; AnchorCount carries the true fleet size.
            if (!snapshotProjected)
            {
                snapshotProjected = true;
                s.PartnerFound = found;
                s.PartnerId = nid;
                s.PartnerWallSupported = facts.PartnerWallSupported;
                s.PartnerDeadNet = facts.PartnerDeadNet;
                s.PartnerHanging = desired.PartnerHangingOnMe;
                s.PartnerValidBelayer = desired.PartnerValidBelayer;
                s.AnchorDesired = desired.AnchorDesired;
                s.DrainDesired = desired.DrainDesired;
            }
        }

        // FAILOVER — remove every anchor whose neighbor is NOT desired this tick (went invalid / departed). The
        // native catch then falls over to a surviving valid piton. Snapshot the keys first (we mutate _anchors).
        if (_anchors.Count > 0)
        {
            var present = new List<int>(_anchors.Keys);
            foreach (int id in present)
                if (!desiredNeighbors.Contains(id))
                {
                    RemoveAnchor(id);
                    _anchorUndesiredSinceUtc.Remove(id);
                    lastAction = $"neighbor #{id} invalid — anchor removed (failover)";
                }
        }

        // MY-GLOBAL actions, once. These are about MY plummet, not any single neighbor:
        //   • unsecured-falling / respawn rewind → the whole fleet off NOW (a lingering anchor fights the rewind).
        //   • weighting the rope with NO valid belayer at all → DropToFall (the mutual-hang dissolution), then
        //     TearDownAll so nothing stale hangs me on a phantom. Next tick re-creates anchors if I re-stabilise.
        if (iAmUnsecuredFalling)
        {
            if (_anchors.Count > 0)
                TearDownAll();
            _anchorUndesiredSinceUtc.Clear();
            lastAction = "falling/respawning — all anchors down (hands off)";
        }
        else if (_weightingRope && !iHaveAnyValidBelayer)
        {
            DropFromRope(gamemode, localId);
            if (_anchors.Count > 0)
                TearDownAll();
            _anchorUndesiredSinceUtc.Clear();
            lastAction = "no valid belayer — dropping to unsecured fall";
        }

        // DRAIN — sums over ALL my DEPENDENTS hanging on me (per-N, unchanged). 0 clears the seam.
        PublishStaminaLoad(gamemode, localId, HangingDependentCount());

        Finish(lastAction);
    }

    /// <summary>
    /// DRIVE one neighbor's anchor: a flat switch over its derived <see cref="BelayPolicy.AnchorAction"/>,
    /// scoped to <c>_anchors[nid]</c>. Handles ONLY the per-neighbor verbs (Create / Hold / the on-wall
    /// debounce / debounced teardown); the MY-GLOBAL drop/hands-off verbs (DropToFall / TearDownPrompt) are
    /// owned by the caller's global block (they depend on MY state, not this neighbor's) and are no-ops here.
    /// Returns the diagnostic label.
    /// </summary>
    private string DriveNeighbor(BelayPolicy.DesiredState desired, SharedRopeGamemode gamemode, int localId,
        int nid, Vector3 partnerPos)
    {
        switch (desired.Action)
        {
            case BelayPolicy.AnchorAction.HoldDebounce:
                // On the wall, neighbor flickered invalid but inside the debounce window — hold its anchor
                // untouched (no Obi re-sort). It stays in desiredNeighbors so the failover sweep won't remove it.
                return $"#{nid} invalid — holding anchor (debounce)";

            case BelayPolicy.AnchorAction.Create:
                CreateAnchor(gamemode, localId, nid, partnerPos);
                return $"#{nid} anchor created";

            case BelayPolicy.AnchorAction.Hold:
                try
                {
                    EnsureBelayIntegrity();
                    var a = _anchors[nid];
                    _reel.Tick(_rope, _controller, a.Piton);
                    FollowPartner(a, partnerPos); // neighbor is wall-supported here, so chasing them is safe
                    return $"#{nid} anchor held";
                }
                catch (Exception)
                {
                    RemoveAnchor(nid); // piton died (level reload, despawn) — recreated next tick
                    return $"#{nid} anchor recreate (piton died)";
                }

            // TearDownDebounced (on-wall, debounce elapsed) is realised by the failover sweep: the neighbor is
            // simply NOT in desiredNeighbors, so RemoveAnchor(nid) runs there. DropToFall / TearDownPrompt are
            // MY-GLOBAL and owned by the caller. None: roped-off, unreachable here.
            default:
                return $"#{nid} —";
        }
    }

    /// <summary>
    /// Drop the LOCAL climber off the securing rope into a real unsecured fall via the engine's own
    /// <c>ToFallingModuleUnsecure</c>. NOT <c>Fall(force)</c>: live tracing proved <c>Fall()</c> throws an
    /// NRE in its OWN debug-log preamble (the object[5] string-interpolation block it builds before it ever
    /// delegates to FallInternal — dump/decomp-fatigue-full/ClimbingV2PawnController_Fall.c), so every
    /// Fall(force) the reconciler issued silently aborted and the faller floated forever. The trace showed
    /// no FallInternal/TransitionToModule frame under Fall at all — the throw is upstream of the actual
    /// transition machinery.
    ///
    /// <c>ToFallingModuleUnsecure</c> skips that preamble and IS proven to drop a settled RAH/Rappel hang to
    /// unsecured Falling (live: Rappel → Falling, IsFalling=true): it releases the local climbot
    /// (SetSecureClimber(false) + SetSecureLifeline(null)) and TransitionToModule(Falling). That transition
    /// runs RAH.OnModuleStop on the outgoing module, which NREs on the now-null securingRope
    /// (re/systems/coop/rah-onmodulestop-nre.md) — that NRE is swallowed by <see cref="RahOnModuleStopGuard"/> so
    /// the transition completes. Lands in unsecured Falling → get_IsFalling true → the climbot's per-frame
    /// gate flips false → climbot releases; next tick observes !_weightingRope and the not-weighting path
    /// tears the mod anchor down. Idempotent across ticks (a climber already Falling just re-drops harmlessly).
    /// Only called while <c>_weightingRope</c> (we ARE on the rope), so we never force a fall off the wall.
    /// </summary>
    private void DropFromRope(SharedRopeGamemode gamemode, int localId)
    {
        try
        {
            var controller = ClimberQuery.FindLocalClimbController(gamemode, localId);
            if (controller == null)
                return;
            var type = controller.CurrentModule?.type;
            controller.ToFallingModuleUnsecure(
                FallReason.Exhausted, new Il2CppSystem.Nullable<Vector3>(), null);
            _log($"belay: belayer gone while {type} — dropping to unsecured fall; "
                + $"now {controller.CurrentModule?.type}");
        }
        catch (Exception e)
        {
            _log("belay: DropFromRope failed: " + e.Message);
        }
    }

    /// <summary>Debounced roster-departure detector for the consent graph. For every connected partner (carry
    /// or dependent): if its id is in this tick's roster, clear any pending "missing-since" stamp; if it's
    /// absent, stamp it (first miss) and, once it has been continuously absent past
    /// <see cref="ConnectionLeaveGrace"/>, ClearPartner it + reset its mutual snapshot — dropping the consent
    /// edge so a rejoin must re-request (FIX-5). The grace window absorbs transient frame gaps so a live rope
    /// is never severed by a single missed frame. Also prunes stamps for ids no longer connected.</summary>
    private void SweepDepartedConnections(
        Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> remotes, int localId)
    {
        var now = DateTime.UtcNow;
        var connected = _handshake.ConnectedIds();
        foreach (var id in connected)
        {
            bool present = false;
            for (int r = 0; r < remotes.Count; r++)
            {
                var remote = remotes[r];
                if (remote != null && remote.Id == id) { present = true; break; }
            }
            if (present)
            {
                _connectedMissingSinceUtc.Remove(id); // reappeared (or never missed) — reset the grace clock
                continue;
            }
            if (!_connectedMissingSinceUtc.TryGetValue(id, out var since))
            {
                _connectedMissingSinceUtc[id] = now; // first tick we've seen this connected peer absent
                continue;
            }
            if (now - since >= ConnectionLeaveGrace)
            {
                _mutual.ResetPartnerSnapshot(id);
                _handshake.ClearPartner(id); // genuine leave — drop consent so a rejoin re-requests
                _connectedMissingSinceUtc.Remove(id);
                _log($"belay: connection to #{id} dropped — absent from roster > {ConnectionLeaveGrace.TotalSeconds:0}s");
            }
        }
        // Prune stamps for ids that are no longer connected at all (disconnected by some other path).
        if (_connectedMissingSinceUtc.Count > 0)
        {
            var stale = new List<int>();
            foreach (var kv in _connectedMissingSinceUtc)
                if (!_handshake.IsConnected(kv.Key))
                    stale.Add(kv.Key);
            foreach (var id in stale)
                _connectedMissingSinceUtc.Remove(id);
        }
    }

    /// <summary>TEST/diagnostic: per-dependent hang+freshness readout (the DOWN-edge drain side, which
    /// BelaySnapshot's carry-focused fields don't show). Format: "dep#&lt;id&gt;:hang=&lt;b&gt;/stale=&lt;b&gt;".</summary>
    internal string DependentDiag()
    {
        var now = DateTime.UtcNow;
        var sb = new System.Text.StringBuilder();
        sb.Append("hangerCount=" + HangingDependentCount() + " load/s=" + HangStaminaSeam.LoadPerSecond.ToString("0.###") + " deps[");
        foreach (var depId in _handshake.Dependents())
            sb.Append("#" + depId + ":hang=" + _mutual.PartnerHanging(depId) + "/stale=" + _mutual.SnapshotStale(depId, now) + " ");
        sb.Append("]");
        return sb.ToString();
    }

    /// <summary>Count MY DEPENDENTS (climbers who requested me — I carry them) that are CURRENTLY HANGING on
    /// my rope, using the same announced-hang + freshness facts BelayPolicy reads for the carry. A dependent
    /// whose hang snapshot is stale (frozen / dropped packets) is NOT counted (fail-safe: don't drain for a
    /// silent peer). This is the chain's summed load — config 2/3 of the three-climber matrix.</summary>
    private int HangingDependentCount()
    {
        int n = 0;
        var now = DateTime.UtcNow;
        foreach (var depId in _handshake.Dependents())
            if (_mutual.PartnerHanging(depId) && !_mutual.SnapshotStale(depId, now))
                n++;
        return n;
    }

    /// <summary>
    /// Resolve the local belaying climber and publish the per-limb stamina DRAIN to
    /// <see cref="HangStaminaSeam"/>: each hanging partner adds <see cref="HangStaminaSeam.PerHangerLoad"/>
    /// × CurrentMaxStamina per second of extra consumption, so the carrier tires naturally into the
    /// critical-stamina panic. <paramref name="hangerCount"/> is the number of roped partners currently
    /// weighting our rope — today 0 or 1, but the rate generalizes to N. Clears the seam (no drain) when
    /// no partner hangs or the local controller can't be resolved.
    /// </summary>
    private void PublishStaminaLoad(SharedRopeGamemode gamemode, int localId, int hangerCount)
    {
        if (hangerCount <= 0)
        {
            HangStaminaSeam.Clear();
            return;
        }
        var controller = ClimberQuery.FindLocalClimbController(gamemode, localId);
        if (controller == null)
        {
            HangStaminaSeam.Clear();
            return;
        }
        HangStaminaSeam.LocalController = controller.Pointer;
        HangStaminaSeam.LoadPerSecond = HangStaminaSeam.PerHangerLoad * hangerCount;
    }

    /// <summary>Teleport ONE anchor's piton after its drifted neighbor, gated by the deadband so it never
    /// re-sorts the rope on sub-threshold jitter.</summary>
    private void FollowPartner(Anchor a, Vector3 partnerPos)
    {
        Vector3 target = partnerPos + AnchorOffset;
        if ((target - a.Piton.transform.position).sqrMagnitude > AnchorMoveThresholdSqr)
            a.Piton.transform.position = target;
    }

    /// <summary>
    /// Create the anchor toward neighbor <paramref name="neighborId"/>: restore the climbot belay, then clip
    /// ONE quickdraw onto the personal (climbot↔harness) rope just above that neighbor — like
    /// AddPiton.AddPitonOnSharedRope but local and minus the network. IsSecured(1) reads true off this rope and
    /// a fall catches with full vanilla piton physics. Item id 3 = standard piton. The new Anchor is stored in
    /// <c>_anchors[neighborId]</c>; the rope/lifeline/controller stay SINGULAR (one securingRope shared by every
    /// anchor — N quickdraws split it). The OwnedRope / PartnerAnchorActive seams are SINGULAR and idempotent —
    /// fine to (re)set once any anchor exists.
    /// </summary>
    private void CreateAnchor(SharedRopeGamemode gamemode, int localId, int neighborId, Vector3 partnerPos)
    {
        try
        {
            var controller = ClimberQuery.FindLocalClimbController(gamemode, localId);
            var lifeline = controller?.harness?.PersonalLifeline;
            var rope = _belayRig.Restore(controller);
            if (lifeline == null || rope == null)
            {
                LogAnchorBlocked(controller == null ? "no local climb controller"
                    : lifeline == null ? "personal lifeline null"
                    : "no climbot arm rope holding the local harness");
                return;
            }
            if (!rope.gameObject.activeInHierarchy)
            {
                LogAnchorBlocked("climbot arm rope still inactive after restore");
                return;
            }

            // Un-stow: free solo keeps the personal rope strung (Climbot ↔ Harness) but invisible.
            // SetVisible is sticky both ways — nothing in free solo restores it.
            if (!rope.Visible)
                rope.SetVisible(true);

            var setting = new Lifeline.ClimbingSetting(controller);
            var quickdraw = lifeline.AddPiton(
                partnerPos + AnchorOffset, Quaternion.identity,
                PitonExecutionQuality.Perfect, 1000, new InventoryItemStringId(3), setting);
            rope.RequestAddQuickdraw(quickdraw, true);

            var piton = lifeline.GetLastPiton();
            _anchors[neighborId] = new Anchor { Piton = piton, Quickdraw = quickdraw };
            _lifeline = lifeline;          // singular — one securingRope shared by every anchor
            _rope = rope;
            _controller = controller;
            RopeLengthGuard.OwnedRope = rope.Pointer; // we own this rope's length now (singular, idempotent)
            FreeSoloRecoveryGate.PartnerAnchorActive = true;
            // Set the climbot's idleRopeLength to the working length and snap it immediately so the
            // game's own ramp settles the rope tight at once instead of crawling from the 70 m payout.
            _reel.Reset();
            _belayRig.SetBelayLength(_reel.DesiredRopeLength(rope, controller, piton), immediate: true);
            _log($"belay: anchor toward #{neighborId} created at {partnerPos + AnchorOffset} "
                + $"on personal rope (anchors now {_anchors.Count}, len {rope.GetLength():0.#}m, max {rope.MaxLengthMeters:0.#}m)");
        }
        catch (Exception e)
        {
            _log("belay: partner anchor creation failed: " + e.Message);
        }
    }

    private void LogAnchorBlocked(string why)
    {
        if (DateTime.UtcNow < _nextAnchorFailLog)
            return;
        _nextAnchorFailLog = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        _log("belay: partner anchor blocked: " + why);
    }

    /// <summary>
    /// Remove ONE anchor (the one toward <paramref name="neighborId"/>): the ONLY correct removal is
    /// <see cref="LogicalRope.ExternalRemoveQuickdraw"/> (Lifeline.RemovePiton DESYNCs — but it is still
    /// needed to free the piton object, so it follows the quickdraw removal), then destroy the piton object.
    /// The native GetLastSecuringPiton then falls over to any surviving valid piton on the same securingRope —
    /// automatic failover (Phase 0 proven). When this empties <see cref="_anchors"/>, the SINGULAR seams are
    /// cleared (the old TearDown seam-clear + rope re-stow); otherwise the rope stays owned and visible for the
    /// remaining anchors. Idempotent: a no-op when no anchor toward that neighbor exists.
    /// </summary>
    private void RemoveAnchor(int neighborId)
    {
        if (_anchors.TryGetValue(neighborId, out var a))
        {
            try { _rope?.ExternalRemoveQuickdraw(a.Quickdraw); } catch (Exception) { }
            try { _lifeline?.RemovePiton(a.Piton); } catch (Exception) { }
            try { UnityEngine.Object.Destroy(a.Piton.gameObject); } catch (Exception) { }
            _anchors.Remove(neighborId);
            _mutual.ClearPartnerHanging(neighborId); // this anchor gone → that neighbor no longer hangs on us
            _log($"belay: anchor toward #{neighborId} removed (anchors now {_anchors.Count})");
        }
        if (_anchors.Count == 0)
            ClearSingularSeams();
    }

    /// <summary>
    /// Tear down EVERY anchor and clear the singular seams unconditionally — the !Enabled / DropToFall /
    /// unsecured-falling / UnlinkForRevive path. Removes each anchor via <see cref="RemoveAnchor"/> (the last
    /// one clears the seams), then runs <see cref="ClearSingularSeams"/> unconditionally so a half-acquired
    /// bundle (OwnedRope/PartnerAnchorActive set but no piton) can never strand those seams for the session.
    /// Idempotent.
    /// </summary>
    internal void TearDownAll()
    {
        if (_anchors.Count > 0)
        {
            foreach (var id in new List<int>(_anchors.Keys))
                RemoveAnchor(id);
        }
        ClearSingularSeams(); // unconditional: even with no anchor, a half-acquired bundle's seams must clear
    }

    /// <summary>
    /// Clear the SINGULAR seams + re-stow the one shared rope to its vanilla free-solo state. Runs when the
    /// LAST anchor goes (from <see cref="RemoveAnchor"/>) and unconditionally from <see cref="TearDownAll"/>:
    /// any of OwnedRope / PartnerAnchorActive can land set even if no piton ever materialised (GetLastPiton
    /// null, or a throw mid-acquire after OwnedRope was claimed), so this seam-clear is unconditional and
    /// idempotent — keying it on the rope would strand those seams for the session.
    /// </summary>
    private void ClearSingularSeams()
    {
        // Re-stow to the vanilla free-solo state (short, invisible) via the real length authority
        // (idleRopeLength) so the climbot's own ramp settles it short — a RequestSetLength alone
        // would be overwritten next frame. Safe even when this teardown IS the team-fall trigger:
        // a quickdraw-less Climbot↔Harness rope never load-bears in free solo.
        try
        {
            if (_rope != null)
            {
                _belayRig.SetBelayLength(2f, immediate: true);
                _rope.SetVisible(false);
            }
        }
        catch (Exception) { }

        bool hadAnchor = RopeLengthGuard.OwnedRope != IntPtr.Zero
            || FreeSoloRecoveryGate.PartnerAnchorActive;
        _lifeline = null;
        _rope = null;
        RopeLengthGuard.OwnedRope = IntPtr.Zero; // hand length authority back to the game
        _controller = null;
        _reel.Reset();
        FreeSoloRecoveryGate.PartnerAnchorActive = false;
        HangStaminaSeam.Clear();  // no anchor → no hang-stamina drain
        if (hadAnchor)
            _log("belay: all partner anchors removed");
    }

    /// <summary>
    /// Per-tick safety net for an existing anchor: re-assert the rig (idempotent — the authoritative
    /// restore is the SharedRopeGamemode.Init Postfix; this catches any path that disables the rig
    /// WITHOUT going through Init), re-pin the quickdraw-split rope ends, and keep the belay bot near
    /// the pawn except during a hang or drop where moving the belayer defeats the catch.
    /// </summary>
    private void EnsureBelayIntegrity()
    {
        var controller = _controller;
        if (controller == null)
            return;
        var arm = _belayRig.Restore(controller);
        if (arm == null)
            return;
        _belayRig.PinRopeEnds(arm, controller.harness);
        if (!FreeSoloRecoveryGate.InSecuredHang && !ClimberQuery.LocalPawnDropping(controller))
            _belayRig.KeepBotNearPawn(arm, controller.harness);
    }
}
