using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppObi;
using Il2CppTheGameBakers.Cairn;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// Owns the local single-player belay rig (climbot + personal rope) and keeps it intact
/// across shared-rope room joins.
///
/// Decompiled ground truth (SharedRopeGamemode.Init, 0x1831642B0, called by
/// NetplayManager.OnRoomJoined_MT every join): the game DELIBERATELY tears the rig down so
/// the networked pair rope can be the securing rope. Three mutations, in order:
///   1. localPawn.associatedRobotPawn.gameObject.SetActive(false)  — kills the climbot.
///   2. harness.PersonalLifeline.securingRope = pair/spare rope     — repoints the lifeline.
///   3. (old securingRope).gameObject.SetActive(false)             — kills the rope object.
/// The climbot arm rope (Climbot/.../bn_Rope_Arm/LogicalRope) keeps its RobotPawnController +
/// Harness holders the whole time; only the GameObjects get disabled and the field gets
/// repointed. So "restore" is a small, deterministic set of writes that exactly invert the
/// teardown — NOT a tug-of-war.
///
/// Timing is the crux: Init re-runs on every rejoin, and the joiner's client-id churn rejoins
/// repeatedly. A 1 Hz reactivation loses that race. <see cref="SharedRopeInitGate"/> therefore
/// invokes <see cref="Restore"/> as a Harmony Postfix on Init — same frame, immediately after
/// the teardown, before anything observes the broken state. <see cref="Restore"/> is idempotent
/// so the 1 Hz <see cref="GameDriver"/> safety net and the patch can both call it freely.
/// </summary>
internal sealed class BelayRig
{
    // Park the belay bot a body-length below the pawn. Coincident with the pawn, both rope
    // strands degenerate into the character mesh and the rope reads as unattached.
    private static readonly Vector3 BotParkOffset = new(0f, -2f, 0f);

    private readonly Action<string> _log;

    internal BelayRig(Action<string> log) => _log = log;

    /// <summary>The climbot whose belay rope the last <see cref="Restore"/> wired. The driver
    /// reaches through this to set the REAL length authority — `RobotPawnController.idleRopeLength`
    /// (field 0x60), which the climbot ramps the rope's last part toward every frame via
    /// SetRopeLengthInternal→UpdateMaxLength, a path that bypasses SetLengthSafe and our guard.
    /// See re/systems/climbot/rope-length.md. Null until the first successful Restore.</summary>
    internal RobotPawnController Climbot { get; private set; }

    /// <summary>
    /// Restore the local climbot belay using the GAME'S OWN setup verb, not hand-built rope
    /// surgery. `RobotPawnController.SetSecureClimber(true, EnableClimbing)` runs the exact
    /// Alpinist path (decompiled SetAttachedToHarness, Robot_FD5960): RequestAttach(robot, Begin)
    /// + RequestAttach(harness, End) + lifeline.set_securingRope(robot.rope) + SetRopeActive +
    /// SetVisible — i.e. it pins BOTH rope ends to real colliders (climbot↔climber) and wires the
    /// lifeline, in one call. Proven live: produces a 1-part rope Begin[robot]→End[climber], both
    /// pinned, len 1.5 m, tight; a subsequent AddPiton+RequestAddQuickdraw then splits it correctly
    /// into climbot→quickdraw→climber. This replaces all prior ManualSetAttachEnd/ForceInitialize/
    /// pinConstraintHandler hacks, which corrupted the Obi solver.
    ///
    /// Idempotent: when already securing, SetSecureClimber re-asserts the same state cheaply.
    /// Returns the climbot's belay rope (robot.rope), now the lifeline's securingRope.
    /// </summary>
    internal LogicalRope Restore(ClimbingV2PawnController controller)
    {
        var harness = controller?.harness;
        if (harness == null)
            return null;

        var robot = FindLocalClimbot(harness, controller);
        if (robot == null)
            return null;
        Climbot = robot; // expose for length control (idleRopeLength)

        try
        {
            // The whole reason the rig needs restoring: room-join disables the climbot and the
            // SetSecureClimber gate bails if the GameObject is inactive. Wake it first; its
            // securing setup (incl. TeleportIfPossible) then positions it correctly.
            var botRoot = robot.transform.root.gameObject;
            if (!botRoot.activeSelf)
            {
                botRoot.SetActive(true);
                botRoot.transform.position = harness.transform.position + BotParkOffset;
                _log("belay: woke local climbot");
            }
            if (robot.AssociatedPawn == null)
                robot.AssociatedPawn = controller;

            if (robot.CurrentState != RobotPawnController.State.SecuringClimber || !robot.IsAttached)
            {
                robot.SetSecureClimber(true, RobotPawnController.SecureClimbingReason.EnableClimbing,
                    overrideSecuringOrder: true, checkPawnControllerSwitcherCurrentMode: true,
                    updateStateAtEnd: true);
                _log("belay: SetSecureClimber(true) — game wired climbot↔climber belay");
            }

            // SetSecureClimber points lifeline.securingRope at robot.GetRope(), but the
            // shared-rope gamemode redirects it back to the inactive NetplaySpareLogicalRope
            // every frame (live-verified: robot.GetRope() had 3 holders + the piton while the
            // lifeline pointed at the empty spare → IsSecured false). Re-assert it every tick so
            // IsSecured and the catch read the real climbot belay rope.
            var robotRope = robot.GetRope();
            var lifeline = harness.PersonalLifeline;
            if (robotRope != null && lifeline != null
                && (lifeline.securingRope == null || lifeline.securingRope.Pointer != robotRope.Pointer))
            {
                lifeline.securingRope = robotRope;
                _log("belay: re-pointed lifeline.securingRope at the climbot rope (gamemode had redirected it)");
            }
        }
        catch (Exception e)
        {
            _log("belay: SetSecureClimber failed: " + e.Message);
            return null;
        }

        return robot.GetRope();
    }

    /// <summary>
    /// Drive the climbot's belay length to <paramref name="meters"/>. This is the REAL length
    /// authority: the climbot's SetRopeLengthInternal ramps the rope's last part toward
    /// `idleRopeLength` every frame (UpdateMaxLength, Rope_CEC720), bypassing SetLengthSafe and our
    /// RopeLengthGuard. Writing this field — instead of calling RequestSetLength/SetLengthSafe and
    /// being overwritten next frame — makes the game's own ramp produce our length. With
    /// <paramref name="immediate"/>, also raise `mustUpdateRopeLengthAsap` so the climbot applies it
    /// without the gradual ramp (needed at anchor creation and to clamp a fall the instant it starts).
    /// Returns false if there is no climbot to drive. See re/systems/climbot/rope-length.md.
    /// </summary>
    internal bool SetBelayLength(float meters, bool immediate)
    {
        var robot = Climbot;
        if (robot == null)
            return false;
        try
        {
            robot.idleRopeLength = meters;
            if (immediate)
                robot.mustUpdateRopeLengthAsap = true;
            return true;
        }
        catch (Exception e)
        {
            _log("belay: SetBelayLength failed: " + e.Message);
            return false;
        }
    }

    // The local climbot is the pawn's associated belay robot — stable for the pawn's life. Cache it
    // keyed on the controller pointer so the per-tick EnsureBelayIntegrity→Restore (which runs every
    // tick while an anchor is up) stops doing a full FindObjectsOfType<RobotPawnController> scan. A
    // respawn swaps the controller pointer → cache miss → one rescan. A room-join only DEACTIVATES the
    // climbot (same object); the cache stays valid and Restore re-wakes it via SetActive.
    private static RobotPawnController _cachedClimbot;
    private static IntPtr _cachedClimbotPawnPtr;

    /// <summary>The local climbot: a RobotPawnController under a Climbot root whose belay rope
    /// holds OUR harness, or — if not yet attached — the nearest active Climbot to the pawn. Cached
    /// across ticks, keyed on the controller pointer (see fields above).</summary>
    private static RobotPawnController FindLocalClimbot(Harness harness, ClimbingV2PawnController controller)
    {
        if (_cachedClimbot != null && _cachedClimbotPawnPtr == controller.Pointer)
            return _cachedClimbot;

        RobotPawnController byAssociation = null;
        RobotPawnController nearestActive = null;
        float bestSqr = float.MaxValue;
        Vector3 pawnPos = harness.transform.position;
        foreach (var bo in UnityEngine.Object.FindObjectsOfType<RobotPawnController>(true))
        {
            if (!bo.transform.root.name.StartsWith("Climbot"))
                continue; // ghost copies live under NETPLAY_*
            if (bo.AssociatedPawn != null && bo.AssociatedPawn.Pointer == controller.Pointer)
                byAssociation = bo;
            if (bo.gameObject.activeInHierarchy)
            {
                float d = (bo.transform.position - pawnPos).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; nearestActive = bo; }
            }
        }
        var result = byAssociation ?? nearestActive
            ?? FirstClimbotHoldingHarness(harness);
        if (result != null)
        {
            _cachedClimbot = result;
            _cachedClimbotPawnPtr = controller.Pointer;
        }
        return result;
    }

    private static RobotPawnController FirstClimbotHoldingHarness(Harness harness)
    {
        foreach (var rope in UnityEngine.Object.FindObjectsOfType<LogicalRope>(true))
        {
            if (!rope.transform.root.name.StartsWith("Climbot") || !RopeHoldsHarness(rope, harness))
                continue;
            var bot = rope.transform.root.GetComponentInChildren<RobotPawnController>(true);
            if (bot != null)
                return bot;
        }
        return null;
    }

    /// <summary>
    /// Pin the rope's two ENDS to the climbot and the climber, idempotently. SetSecureClimber
    /// wires the bare rope's ends, but RequestAddQuickdraw then SPLITS it and the split's outer
    /// ends come up FREE (pin=NONE) — SetSecureClimber does not re-pin an already-split rope —
    /// so the rope droops 16-18 m from both ends into the floor. This restores them via each
    /// part's ObiPinConstraintsHandler.AttachTo: first-part.Begin → robot collider,
    /// last-part.End → harness collider. Proven live on BOTH instances: a drooping len-20,
    /// bounds-22 m rope with FREE ends became len-4, bounds-3 m with Begin=robot, End=climber,
    /// IsSecured true, stable across frames. Idempotent: skips a side already pinned.
    /// </summary>
    internal void PinRopeEnds(LogicalRope rope, Harness harness)
    {
        try
        {
            var parts = rope.ropeParts;
            if (parts == null || parts.Count == 0)
                return;
            var first = parts[0];
            var last = parts[parts.Count - 1];

            if (!first.IsAttachedToRobot(RopeSide.Begin))
            {
                var robotCol = FindRobotCollider(rope);
                if (robotCol != null)
                {
                    first.pinConstraintHandler.AttachTo(robotCol, RopeSide.Begin);
                    _log("belay: pinned rope Begin → climbot");
                }
            }
            if (!last.IsAttachedToClimber(RopeSide.End))
            {
                var harnessCol = harness.GetAttachEnd();
                if (harnessCol != null)
                {
                    last.pinConstraintHandler.AttachTo(harnessCol, RopeSide.End);
                    _log("belay: pinned rope End → climber");
                }
            }
        }
        catch (Exception e)
        {
            _log("belay: PinRopeEnds failed: " + e.Message);
        }
    }

    /// <summary>The climbot's attach collider, via the rope's robot holder (the non-harness,
    /// non-quickdraw holder).</summary>
    private static ObiCollider FindRobotCollider(LogicalRope rope)
    {
        var holders = rope.ropeHolders;
        if (holders == null)
            return null;
        for (int i = 0; i < holders.Count; i++)
        {
            var h = holders[i];
            if (h == null || h.TryCast<Harness>() != null || h.TryCast<LogicalQuickdraw>() != null)
                continue;
            return h.GetAttachEnd();
        }
        return null;
    }

    /// <summary>Keep the belay bot near the pawn. Its netplay-parked AI doesn't follow, and
    /// it holds the rope's far end, so a runaway bot is a runaway rope. Caller gates this off
    /// during a hang or a drop — yanking the belayer mid-catch defeats the catch.</summary>
    internal void KeepBotNearPawn(LogicalRope arm, Harness harness)
    {
        var botRoot = arm.transform.root;
        Vector3 pawnPos = harness.transform.position;
        // Only rescue a genuinely RUNAWAY bot (a holder of the rope's far end; a netplay-parked one
        // can launch into the void). Do NOT pull it tight every tick — a small threshold fires
        // constantly and fights the robot's own AI movement, which froze the game. The catch
        // distance is kept symmetric by capping the climbot leg in DesiredRopeLength, not by
        // teleporting the bot.
        const float RunawayDistance = 15f;
        if ((botRoot.position - pawnPos).sqrMagnitude <= RunawayDistance * RunawayDistance)
            return;
        botRoot.position = pawnPos + BotParkOffset;
        _log("belay: runaway climbot (>15m) pulled back below the pawn");
    }

    private static bool RopeHoldsHarness(LogicalRope rope, Harness harness)
    {
        try
        {
            var holders = rope.ropeHolders;
            if (holders == null)
                return false;
            for (int i = 0; i < holders.Count; i++)
            {
                var holder = holders[i]?.TryCast<Harness>();
                if (holder != null && holder.Pointer == harness.Pointer)
                    return true;
            }
        }
        catch (Exception) { }
        return false;
    }
}

// SharedRopeInitGate → SharedRopeInitGate.cs and RopeLengthGuard → RopeLengthGuard.cs (one Harmony
// patch per file, matching the rest of the mod's idiom).
