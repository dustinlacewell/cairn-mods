using System;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// The PURE functional core of the belay reconciler: given the facts the tick OBSERVED, DERIVE the one
/// true desired state. No IL2CPP, no game reads, no side effects — just the boolean algebra and the
/// teardown-strategy selection, so it is unit-testable off-game and reads as one place.
///
/// The single invariant the whole thing turns on:
///     MY ANCHOR SHOULD EXIST  ⟺  MY PARTNER IS A VALID WALL BELAYER (or I am belaying a hanging partner).
/// A partner who is hanging / free-falling / dead is not on the wall, so they cannot belay me — my anchor
/// must go and I fall. This dissolves the mutual-hang deadlock: when I'm weighting AND my partner is
/// hanging, iAmWallSupported is false, so both the belayer term and partnerValidBelayer collapse and
/// anchorDesired is false on BOTH sides symmetrically.
/// </summary>
internal static class BelayPolicy
{
    /// <summary>FIX-4b debounce window for the on-wall anchor teardown (a few ticks). When the anchor
    /// first becomes un-desired while I'm on the wall (a partner flickering across the hang/wall
    /// boundary), hold it through brief flicker rather than re-sorting the Obi rope every tick.</summary>
    internal static readonly TimeSpan AnchorDropDebounce = TimeSpan.FromSeconds(2);

    /// <summary>The exact facts the derive reads — every one OBSERVED from live game/net state by the
    /// reconciler's Observe phase, none re-read here. <see cref="AnchorPresent"/> is whether an anchor is
    /// currently up (the piton exists) going INTO this tick.</summary>
    internal struct ObservedFacts
    {
        public bool Enabled;               // rope toggled on
        public bool PartnerFound;          // a remote partner resolved this tick
        public int PartnerId;
        public Vector3 PartnerPos;
        public bool PartnerWallSupported;  // partner's net frame says firm/walking/climbing
        public bool PartnerDeadNet;        // partner's net PawnState == Dead (diagnostic only now: death is
                                           // handled as not-a-valid-belayer, never as a hang — see Derive)
        public bool PartnerHanging;        // partner's announced snapshot: they weight the rope (on us)
        public bool PartnerSnapshotStale;  // FIX-1: their last accepted announcement is older than the gate
        public bool WeightingRope;         // my local module says I weight my own rope (hanging/rappel/…)
        public bool IAmUnsecuredFalling;   // I'm in an unsecured fall / Edelweiss respawn rewind
        public bool AnchorPresent;         // the piton exists going into this tick
    }

    /// <summary>What to do with the anchor this tick.</summary>
    internal enum AnchorAction
    {
        Hold,               // anchor desired and present: keep it (re-assert integrity, reel, follow)
        Create,             // anchor desired and absent: build it
        DropToFall,         // not desired and I'm weighting the rope: drop me to an unsecured fall NOW
        TearDownPrompt,     // not desired and I'm in an unsecured fall/respawn: anchor off NOW (hands off)
        HoldDebounce,       // not desired (on-wall flicker), anchor present, still inside the debounce window
        TearDownDebounced,  // not desired (on-wall), debounce elapsed (or no anchor): anchor down
        None,               // roped off or no partner: not reached via Derive in production (the imperative
                            // guards handle the distinct seam clear), but defined so Derive is total/testable
    }

    /// <summary>The derived desired state for this tick. <see cref="AnchorUndesiredSince"/> is the threaded
    /// debounce-timer state to carry back to the reconciler (DateTime.MinValue = anchor currently desired).</summary>
    internal struct DesiredState
    {
        public bool PartnerValidBelayer;
        public bool AnchorDesired;
        public bool DrainDesired;
        public bool PartnerHangingOnMe;     // the projected fact (partnerHanging || partnerDeadNet)
        public AnchorAction Action;
        public DateTime AnchorUndesiredSince;
    }

    /// <summary>
    /// Lift the reconciler's boolean algebra + teardown-strategy selection, byte-for-byte. <paramref
    /// name="now"/> and <paramref name="anchorUndesiredSince"/> thread the debounce timer in; the returned
    /// <see cref="DesiredState.AnchorUndesiredSince"/> threads it back out.
    /// </summary>
    internal static DesiredState Derive(in ObservedFacts f, DateTime now, DateTime anchorUndesiredSince)
    {
        // Degenerate cases: not roped, or no partner. The production reconciler handles these with
        // imperative guards (distinct seam handling), but Derive stays total so it is fully testable.
        if (!f.Enabled || !f.PartnerFound)
            return new DesiredState
            {
                PartnerHangingOnMe = f.PartnerHanging && !f.PartnerSnapshotStale,
                Action = AnchorAction.None,
                AnchorUndesiredSince = anchorUndesiredSince,
            };

        // FACT 2 — is a LIVING, STILL-ANNOUNCING partner hanging ON ME? Only a fresh weighting snapshot
        // counts. A DEAD/silent partner is deliberately NOT a hanger: death severs the link both ways. We
        // gate on !PartnerSnapshotStale because a partner who dies/disconnects/save-reloads stops ticking
        // its announce while its last "I'm hanging" snapshot lingers — without this gate that stale hang
        // would drain the survivor forever (the reported bug). The 1 Hz heartbeat is unconditional, so a
        // genuinely-hanging live partner never goes stale; only a gone one does. (Net PawnState==Dead is an
        // unreliable signal — a save-reloading corpse reports Walking — so staleness, not PartnerDeadNet,
        // is what severs the drain.)
        // FACT 3 — am I myself wall-supported (NOT weighting my own rope, NOT in an unsecured fall)?
        bool partnerHangingOnMe = f.PartnerHanging && !f.PartnerSnapshotStale;
        bool iAmWallSupported = !f.WeightingRope && !f.IAmUnsecuredFalling;

        // A partner is a VALID WALL BELAYER only if the net frame says they're on the wall AND their own
        // snapshot does not say they're weighting the rope AND that snapshot is FRESH (FIX-1).
        bool partnerValidBelayer = f.PartnerWallSupported && !partnerHangingOnMe && !f.PartnerSnapshotStale;

        // Keep my anchor if EITHER I have a valid belayer OR I'm belaying a hanging partner; drop it only
        // when neither of us can belay the other. Never keep it while I'm being rewound/respawned.
        bool iAmBelayingHangingPartner = iAmWallSupported && partnerHangingOnMe;
        bool anchorDesired = !f.IAmUnsecuredFalling && (partnerValidBelayer || iAmBelayingHangingPartner);
        bool drainDesired = iAmBelayingHangingPartner;

        var desired = new DesiredState
        {
            PartnerValidBelayer = partnerValidBelayer,
            AnchorDesired = anchorDesired,
            DrainDesired = drainDesired,
            PartnerHangingOnMe = partnerHangingOnMe,
            AnchorUndesiredSince = anchorUndesiredSince,
        };

        if (!anchorDesired)
        {
            // Safety-critical: my belayer is gone and I'm hanging — drop NOW, no debounce.
            if (f.WeightingRope)
            {
                desired.Action = AnchorAction.DropToFall;
                desired.AnchorUndesiredSince = DateTime.MinValue;
                return desired;
            }

            // Unsecured fall / Edelweiss respawn rewind: belay OFF me NOW, no debounce.
            if (f.IAmUnsecuredFalling)
            {
                desired.Action = AnchorAction.TearDownPrompt;
                desired.AnchorUndesiredSince = DateTime.MinValue;
                return desired;
            }

            // FIX-4b: on the wall, holding an anchor for a partner who flickered invalid. Start the timer
            // on entry; hold the anchor (no integrity work) while inside the window; otherwise drop.
            if (anchorUndesiredSince == DateTime.MinValue)
                anchorUndesiredSince = now;
            if (f.AnchorPresent && now - anchorUndesiredSince < AnchorDropDebounce)
            {
                desired.Action = AnchorAction.HoldDebounce;
                desired.AnchorUndesiredSince = anchorUndesiredSince; // timer keeps running
                return desired;
            }
            desired.Action = AnchorAction.TearDownDebounced;
            desired.AnchorUndesiredSince = DateTime.MinValue;
            return desired;
        }

        // Anchor is desired — clear any pending debounce, then create or hold.
        desired.AnchorUndesiredSince = DateTime.MinValue;
        desired.Action = f.AnchorPresent ? AnchorAction.Hold : AnchorAction.Create;
        return desired;
    }
}
