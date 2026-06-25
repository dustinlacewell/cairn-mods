using System;
using Il2Cpp;

namespace CairnCoop;

/// <summary>
/// Read-only probe of the rewind TAPE — the figure <see cref="RewindController"/>.CanRewind tests for
/// <c>NotEnoughTape</c>.
///
/// <para><see cref="PathRecorder.GetRewindAvailableLength"/> is the EXACT gated value CanRewind reads
/// (the RewindRecorder's current-record length, returned only while <c>Path.IsRecording</c>);
/// <see cref="PathRecorder.AreStatisticsBeingRecordedRightNow"/> separates "record empty" (rec=True,
/// tape=0) from "recording gated off" (rec=False).</para>
///
/// <para>Mechanism (verified from the binary, supersedes the earlier wrong note): the rewind tape is a
/// rolling buffer FILLED by <c>RewindRecorder.CaptureFrame</c> while its <c>ShouldCapture</c> gate holds
/// — that gate returns false on dead/dying-and-not-passed-out plus several survival/scene states — and
/// EMPTIED by <c>RewindRecorder.Clear</c> on a recording (re)start. It is NOT cleared by the periodic
/// save: <c>RewindRecorder</c> overrides <c>SaveAndClear</c> to a no-op stub.</para>
///
/// <para>ALWAYS read the climber's OWN recorder. The scene's first <c>PathRecorder</c> is, in co-op,
/// frequently a remote ghost's empty one — reading it produced phantom NotEnoughTape diagnostics.</para>
/// </summary>
internal static class TapeProbe
{
    /// <summary>The local climber's PathRecorder via the PawnManager singleton (no scene scan).</summary>
    internal static PathRecorder Local()
    {
        try
        {
            var pm = MoSingleton<PawnManager>.Instance;
            var pcs = pm != null ? pm.PawnControllerSwitcher : null;
            return pcs != null ? pcs.PathRecorder : null;
        }
        catch { return null; }
    }

    /// <summary>One-line tape state for a SPECIFIC recorder (avail + whether recording is live).</summary>
    internal static string Of(PathRecorder pr)
    {
        if (pr == null) return "tape=noRecorder";
        try { return $"tape={pr.GetRewindAvailableLength():0.##}s rec={pr.AreStatisticsBeingRecordedRightNow}"; }
        catch (Exception e) { return "tape=?(" + e.Message + ")"; }
    }

    /// <summary>Local tape state, one line.</summary>
    internal static string LocalStr() => Of(Local());

    /// <summary>The available-tape figure alone; -1 on failure.</summary>
    internal static float Avail(PathRecorder pr)
    {
        try { return pr != null ? pr.GetRewindAvailableLength() : -1f; }
        catch { return -1f; }
    }

    private static bool _seen;
    private static float _lastAvail;
    private static bool _lastRec;

    /// <summary>Log the local tape on a meaningful change (≥2 s delta or recording flip). Call at ~1 Hz
    /// from the driver; quiet while the rolling buffer sits at its cap, loud when it drains.</summary>
    internal static void LogLocalOnChange(Action<string> log)
    {
        var pr = Local();
        if (pr == null) return;
        float avail = Avail(pr);
        bool rec;
        try { rec = pr.AreStatisticsBeingRecordedRightNow; }
        catch { return; }
        if (!_seen || Math.Abs(avail - _lastAvail) >= 2f || rec != _lastRec)
        {
            _seen = true;
            _lastAvail = avail;
            _lastRec = rec;
            log?.Invoke($"tape: avail={avail:0.##}s rec={rec}");
        }
    }
}
