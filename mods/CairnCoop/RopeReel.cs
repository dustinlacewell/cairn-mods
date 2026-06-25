using System;
using Il2Cpp;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// The fall-latch + reel sub-machine for the partner anchor's belay rope, extracted out of
/// <see cref="PartnerBelay"/>. It is the single length authority for the climbot belay rope (it drives
/// idleRopeLength via <see cref="BelayRig.SetBelayLength"/>, the field the climbot's own ramp targets),
/// and it governs the CATCH DISTANCE — so its fall-edge freeze + climbing deadband are correctness, not
/// cosmetics:
///
///   • CLIMBING: track the live climber↔partner gap so the rope stays tight, deadbanded against the last
///     value we SET (not the physically-measured length, which settles lower under tension and would
///     re-fire forever).
///   • FALLING EDGE: latch idleRopeLength ONCE to the gap at the instant of the jump and HOLD it through
///     the whole fall/hang — feeding the growing gap back would pay rope out under the faller (live:
///     2.64→8.22 m, fatal).
///
/// Owns only its latch state (was-falling edge, frozen length, last-requested length, log cadence). The
/// live rope/controller/piton are passed in each tick by the reconciler, which owns the anchor handle.
/// </summary>
internal sealed class RopeReel
{
    // Re-request only when the working length has moved more than this since the last ask —
    // a player shuffling on the wall shouldn't repaint the rope every second.
    private const float RopeReelDeadband = 0.75f;
    // Slack reads as "rope dangling off the quickdraw, connected to nothing" past ~2 m —
    // the droop dwarfs the working strands. Keep it climbing-taut.
    private const float RopeSlackMeters = 0.5f;

    private readonly BelayRig _belayRig;
    private readonly Action<string> _log;

    internal RopeReel(BelayRig belayRig, Action<string> log)
    {
        _belayRig = belayRig;
        _log = log;
    }

    private DateTime _nextReelLog;
    // The length we last asked the rope to be. Reel decisions compare the geometric NEED against
    // THIS, not against the physically-measured GetLength() (which settles lower under tension and
    // would otherwise trigger a re-request every tick).
    private float _lastRequestedRopeLength;
    // Fall-entry length latch: the catch distance must be the climber↔partner gap MEASURED AT THE
    // INSTANT OF THE JUMP, held until recovery. The gap grows as the climber falls; if we kept
    // feeding it to idleRopeLength the rope would pay out under them (live: 2.64→8.22 m, fatal). So
    // on the falling edge we freeze it once and stop updating.
    private bool _wasFalling;
    private float _frozenBelayLength;

    /// <summary>Reset the latch to the at-rest state — called when an anchor is created or torn down so a
    /// stale frozen length / last-request never leaks across anchors.</summary>
    internal void Reset()
    {
        _wasFalling = false;
        _frozenBelayLength = 0f;
        _lastRequestedRopeLength = 0f;
    }

    /// <summary>Straight-line climber↔partner gap. The anchor quickdraw sits at the partner, so this
    /// IS the distance that governs how far a caught fall drops. Live position is the harness, NOT
    /// the controller transform (which reads ~1 km off).</summary>
    private static float ClimberGap(ClimbingV2PawnController controller, Piton piton)
    {
        Vector3 localPos = controller.harness.transform.position;
        Vector3 anchorPos = piton.transform.position;
        return (localPos - anchorPos).magnitude;
    }

    /// <summary>The belay length that makes a caught fall TIGHT — the climber↔partner gap plus a
    /// small fixed slack, clamped to [taut, rope max]. Deliberately excludes the climbot leg (it only
    /// bloats the drop and made the two sides asymmetric).</summary>
    internal float DesiredRopeLength(LogicalRope rope, ClimbingV2PawnController controller, Piton piton)
        => Mathf.Clamp(ClimberGap(controller, piton) + RopeSlackMeters, 1f, rope.MaxLengthMeters);

    /// <summary>
    /// One reel tick. Climbing: track the gap so the rope stays tight. FALLING EDGE: latch idleRopeLength
    /// ONCE to the gap at that instant and hold — feeding the growing gap back would pay rope out under
    /// the faller. <paramref name="rope"/>/<paramref name="controller"/>/<paramref name="piton"/> are the
    /// live anchor handle, supplied by the reconciler.
    /// </summary>
    internal void Tick(LogicalRope rope, ClimbingV2PawnController controller, Piton piton)
    {
        if (rope == null || controller == null)
            return;

        bool falling = IsFalling(controller);
        if (falling && !_wasFalling)
        {
            _frozenBelayLength = DesiredRopeLength(rope, controller, piton);
            _belayRig.SetBelayLength(_frozenBelayLength, immediate: true);
            _log($"belay: fall — froze belay length at {_frozenBelayLength:0.#}m (climber↔partner gap)");
        }
        _wasFalling = falling;

        // While falling or hanging, hold the frozen length — never feed rope mid-fall.
        if (falling || FreeSoloRecoveryGate.InSecuredHang)
        {
            if (_frozenBelayLength > 0f)
                _belayRig.SetBelayLength(_frozenBelayLength, immediate: false);
            return;
        }

        // Climbing: keep the rope tight to the live gap, deadbanded against the last value we SET
        // (not GetLength(), which settles lower under tension and would re-fire forever).
        _frozenBelayLength = 0f;
        float desired = DesiredRopeLength(rope, controller, piton);
        if (_lastRequestedRopeLength > 0f
            && Mathf.Abs(desired - _lastRequestedRopeLength) < RopeReelDeadband)
            return;
        bool snap = rope.GetLength() > desired * 3f + 2f; // self-heal a runaway-inflated rope
        _belayRig.SetBelayLength(desired, immediate: snap);
        if (snap)
            _log($"belay: rope was inflated to {rope.GetLength():0.#}m — snapped idleRopeLength to {desired:0.#}m");
        else if (DateTime.UtcNow >= _nextReelLog)
        {
            _nextReelLog = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            _log($"belay: belay length {_lastRequestedRopeLength:0.#}m -> {desired:0.#}m (measured {rope.GetLength():0.#}m)");
        }
        _lastRequestedRopeLength = desired;
    }

    /// <summary>The clean fall detectors on the controller — they trip at the instant the fall
    /// begins, unlike a velocity heuristic which only fires once the climber is already moving fast.</summary>
    private static bool IsFalling(ClimbingV2PawnController controller)
    {
        try { return controller.IsFalling || controller.IsSecureFalling; }
        catch (Exception) { return false; }
    }
}
