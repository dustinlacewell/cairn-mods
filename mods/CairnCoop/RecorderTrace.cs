using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// COMPREHENSIVE control-flow trace of the rewind-TAPE lifecycle. Not a symptom probe: the symptom
/// (a ground-recovery on NotEnoughTape, tape 30s→0 across a 20s fall) is already known. This logs
/// WHICH recorder/rewind methods run, in what ORDER, with the live tape value at each tape-mutating
/// call, so the drain is pinned to the exact method that empties the buffer between reload and refuse.
///
/// Coverage — the whole tape machine, not a hand-picked few:
///   • EVERY declared instance method of the Recorder hierarchy (Recorder, RewindRecorder,
///     MultipartRecorder, ShareablePathRecorder). These are plain IDisposable classes (no Unity
///     lifecycle), so reflection-patching the full method set is safe.
///   • A curated tape/rewind subset of the two MonoBehaviours (PathRecorder, RewindController) — the
///     recording start/stop/halt/resume, fall handlers, save/checkpoint hooks, and the rewind entry/
///     exit/scrub. NOT Update/Awake/OnDestroy (lifecycle/inlined → the reflection-patch-all crash class).
///
/// Each call logs `recTrace fN Type.Method [tape=…]`. Identical consecutive lines collapse to a count
/// so the per-frame CaptureFrame does not flood the 20s window. Always armed once installed — the whole
/// reload→climb→fall→refuse span is the trace.
///
/// Skipped per-method (the Il2Cpp Harmony detour hazards that crashed the earlier blanket patch):
/// property getters/setters (inlined, noisy), generic methods, and by-ref / pointer / out parameters.
/// </summary>
internal static class RecorderTrace
{
    internal static Action<string> Log;

    // ---- the drain-pinpoint set: log the live tape value at these calls (they are the suspects) ----
    private static readonly HashSet<string> TapeMutators = new(StringComparer.Ordinal)
    {
        "Recorder.Clear", "RewindRecorder.Clear",
        "Recorder.CaptureFrame", "RewindRecorder.CaptureFrame", "MultipartRecorder.CaptureFrame",
        "Recorder.SaveAndClear", "RewindRecorder.SaveAndClear", "MultipartRecorder.SaveAndClear",
        "Recorder.PathRecorder_OnPathRecordingStarted", "RewindRecorder.PathRecorder_OnPathRecordingStarted",
        "MultipartRecorder.PathRecorder_OnPathRecordingStarted",
        "RewindRecorder.CutRewindRecordEnd", "RewindRecorder.Instance_OnTimeFastForwarded",
        "PathRecorder.BeginRecordingStatistics", "PathRecorder.EndRecordingStatistics",
        "PathRecorder.DiscardCurrentlyRecordingStatistics", "PathRecorder.HaltPath", "PathRecorder.ResumePath",
        "PathRecorder.Climbing_OnFall", "PathRecorder.ClimbingPawn_OnResetAfterFall",
        "PathRecorder.Rewind_OnRewindBegin", "PathRecorder.Rewind_OnRewindEnd",
        "PathRecorder.PawnControllerSwitcher_OnHitCheckpoint", "PathRecorder.GameEventManager_OnDeath",
        "PathRecorder.ValidateCurrentSegmentBeforeSave",
    };

    // ---- coalesce identical consecutive lines (per-frame CaptureFrame) ----
    // Per-frame capture methods are logged ONLY when the tape changes direction or steps hard — the
    // decisive event is "tape stops growing and starts draining", not each individual capture. The rare,
    // load-bearing methods (Clear, recording-restart, fall, cut, time-warp, load) log every call verbatim.
    private static readonly HashSet<string> HighFrequency = new(StringComparer.Ordinal)
    {
        "Recorder.CaptureFrame", "RewindRecorder.CaptureFrame",
        "Recorder.GetCurrentTimeMs", "RewindRecorder.GetCurrentTimeMs",
    };

    private static float _lastTape = float.NaN;
    private static int _tapeDir;           // -1 draining, +1 growing, 0 unknown
    private static int _suppressed;

    private static void Hit(MethodBase __originalMethod)
    {
        try
        {
            string key = (__originalMethod.DeclaringType?.Name ?? "?") + "." + __originalMethod.Name;
            int f;
            try { f = Time.frameCount; } catch { f = -1; }

            if (HighFrequency.Contains(key))
            {
                // Sample the tape; emit only on a direction flip (grow↔drain) or a ≥0.5s jump.
                if (f <= 1) return;
                float tape = TapeProbe.Avail(TapeProbe.Local());
                if (float.IsNaN(_lastTape)) { _lastTape = tape; return; }
                float d = tape - _lastTape;
                int dir = d > 0.02f ? 1 : (d < -0.02f ? -1 : _tapeDir);
                bool flip = dir != 0 && _tapeDir != 0 && dir != _tapeDir;
                bool bigStep = Math.Abs(d) >= 0.5f;
                if (flip || bigStep)
                {
                    string why = flip ? (dir < 0 ? "TAPE NOW DRAINING" : "tape now growing") : "tape step";
                    Log?.Invoke($"recTrace f{f} {key}  tape={tape:0.##}s ({why}; +{_suppressed} suppressed)");
                    _suppressed = 0;
                }
                else _suppressed++;
                _lastTape = tape;
                _tapeDir = dir;
                return;
            }

            // Rare, decisive methods: log every call, with the live tape for the tape mutators.
            string line = (TapeMutators.Contains(key) && f > 1)
                ? $"recTrace f{f} {key}  {TapeProbe.LocalStr()}"
                : $"recTrace f{f} {key}";
            Log?.Invoke(line);
        }
        catch { /* never throw out of native */ }
    }

    /// <summary>
    /// CURATED target set — NOT a blanket class patch. The earlier blanket patch installed fine
    /// ("patched 66") but crashed at runtime the instant a patched method RAN
    /// (recTrace f0 ShareablePathRecorder.SaveAndClear): some Il2Cpp virtual-override detours corrupt the
    /// native call even though Patch() succeeded. So we patch ONLY the methods that touch the REWIND tape,
    /// and only on the two recorders that own it (RewindRecorder + its base Recorder). The other recorders
    /// (ShareablePathRecorder = route sharing, MultipartRecorder = ghost export) do not hold the rewind
    /// tape and are excluded. SaveAndClear is excluded everywhere — proven a no-op for the rewind tape.
    /// </summary>
    private static readonly string[] FullClasses = System.Array.Empty<string>();

    /// <summary>
    /// The tape-WEDGE is already proven (Scrub→Instance_OnTimeFastForwarded→Clear per frame, tape pinned
    /// at 0, never exits). The remaining question is the ROOT: bestAutoRewindScrubPosition01 == NONE — the
    /// resumable-frame scan (<c>Parse</c>) found NO resumable frame, so the auto-rewind has no target and
    /// scrubs off the end. So this pass instruments the DECISION, not the tape: bracket <c>Parse</c> (the
    /// backward frame scan that sets bestAutoRewindScrubPosition01) and log every <c>CanResumeFromFrame</c>
    /// verdict, so the next failed revive shows how many frames were scanned and how many were resumable —
    /// distinguishing "co-op mis-stamps the frames' holding state" from "genuinely no stable frame".
    /// The per-frame recorder/scrub noise is dropped — we have that part.
    /// </summary>
    private static readonly string[] FullClasses2 = System.Array.Empty<string>(); // (kept for symmetry)

    private static readonly (string type, string[] methods)[] CuratedClasses =
    {
        // Bracketed by Parse via a dedicated prefix/postfix below (reset + summary). Listed here only so
        // the resolver finds the type; Parse itself is patched specially in Install.
    };

    internal static void Install(HarmonyLib.Harmony h)
    {
        var prefix = new HarmonyMethod(typeof(RecorderTrace), nameof(Hit));
        int ok = 0, skip = 0, fail = 0;

        foreach (var typeName in FullClasses)
        {
            var t = Resolve(typeName);
            if (t == null) { Log?.Invoke($"recTrace: type NOT FOUND {typeName}"); continue; }
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!Patchable(m)) { skip++; continue; }
                if (TryPatch(h, m, prefix)) ok++; else fail++;
            }
        }

        foreach (var (typeName, methods) in CuratedClasses)
        {
            var t = Resolve(typeName);
            if (t == null) { Log?.Invoke($"recTrace: type NOT FOUND {typeName}"); continue; }
            foreach (var name in methods)
            {
                MethodInfo m = null;
                try { m = AccessTools.Method(t, name); } catch { }
                if (m == null) { Log?.Invoke($"recTrace: method NOT FOUND {t.Name}.{name}"); continue; }
                if (!Patchable(m)) { skip++; continue; }
                if (TryPatch(h, m, prefix)) ok++; else fail++;
            }
        }

        InstallResumabilityScan(h, ref ok, ref fail);
        InstallTapeClearTrace(h, ref ok, ref fail);

        Log?.Invoke($"recTrace: installed — patched {ok}, skipped {skip}, failed {fail}");
    }

    // ── tape-CLEAR trace: catch the instant 25s→0 collapse at the death frame (the ground-recovery cause).
    // The rewind tape is zeroed only by RewindRecorder.Clear and the recording-restart handler
    // PathRecorder_OnPathRecordingStarted (which builds a fresh empty LiveRecord when hasLoadedData==false).
    // Log every call, with the LOCAL tape value BEFORE it and a managed stack hint, so the next ground
    // recovery shows WHICH zeroer fired on the grounded climber (and confirms it did NOT fire on the partner).
    private static void InstallTapeClearTrace(HarmonyLib.Harmony h, ref int ok, ref int fail)
    {
        var rr = Resolve("Il2Cpp.Il2CppTheGameBakers.Cairn.Ghost.RewindRecorder")
              ?? Resolve("Il2CppTheGameBakers.Cairn.Ghost.RewindRecorder");
        if (rr == null) { Log?.Invoke("recTrace: RewindRecorder NOT FOUND for tape-clear trace"); fail++; return; }
        // Clear + OnPathRecordingStarted: the tape-zeroer + the recording-restart. Instance_OnTimeFastForwarded
        // is the static-proven native Clear dispatcher (slot 0x228 when hasLoadedData==false) — patch it so a
        // run shows whether IT brackets the death-frame Clear-storm on the grounded climber.
        foreach (var name in new[] { "Clear", "PathRecorder_OnPathRecordingStarted", "Instance_OnTimeFastForwarded", "CutRewindRecordEnd" })
        {
            try
            {
                var m = AccessTools.Method(rr, name);
                if (m == null) { Log?.Invoke($"recTrace: RewindRecorder.{name} NOT FOUND"); fail++; continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(RecorderTrace), nameof(TapeClearPre)));
                ok++;
            }
            catch (Exception e) { Log?.Invoke($"recTrace: FAILED RewindRecorder.{name}: {e.Message}"); fail++; }
        }

        // Pin WHAT raises the standalone death-window OnTimeFastForwarded: log every SpendTimeCinematic with
        // its managed-stack caller. The raiser is the SpendTimeCR coroutine; the caller tells us whether the
        // death-window skip is incidental (vs a real bivouac/wait time jump).
        var tm = Resolve("Il2Cpp.TimeManager") ?? Resolve("Il2CppTheGameBakers.Cairn.TimeManager") ?? Resolve("TimeManager");
        if (tm != null)
        {
            foreach (var m in tm.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "SpendTimeCinematic" && m.Name != "SpendTime") continue;
                try { h.Patch(m, prefix: new HarmonyMethod(typeof(RecorderTrace), nameof(SpendTimePre))); ok++; }
                catch (Exception e) { Log?.Invoke($"recTrace: FAILED TimeManager.{m.Name}: {e.Message}"); fail++; }
            }
        }
        else Log?.Invoke("recTrace: TimeManager NOT FOUND for SpendTime trace");
    }

    private static void SpendTimePre(System.Reflection.MethodBase __originalMethod)
    {
        try
        {
            int f; try { f = Time.frameCount; } catch { f = -1; }
            string stack;
            try { stack = new System.Diagnostics.StackTrace(2, false).ToString().Replace("\r", "").Replace("\n", " | "); }
            catch { stack = "?"; }
            Log?.Invoke($"recTrace f{f} TIME-SKIP {__originalMethod.Name}  caller≈ {stack}");
        }
        catch { }
    }

    private static void TapeClearPre(System.Reflection.MethodBase __originalMethod)
    {
        try
        {
            int f; try { f = Time.frameCount; } catch { f = -1; }
            string tape = TapeProbe.LocalStr(); // the LOCAL recorder's tape right now, before the clear
            string stack;
            try { stack = new System.Diagnostics.StackTrace(2, false).ToString().Replace("\r", "").Replace("\n", " | "); }
            catch { stack = "?"; }
            Log?.Invoke($"recTrace f{f} TAPE-ZEROER {__originalMethod.Name}  {tape}  stack≈ {stack}");
        }
        catch { }
    }

    // ── the resumable-frame scan: WHY bestAutoRewindScrubPosition01 == NONE ───────────────────────────
    // Parse scans the exported ghost stream backward and calls CanResumeFromFrame per frame; a frame is
    // resumable iff (timer running) && (walking || (>=2 limbs holding && a hand on a hold)). bestTarget is
    // set from the first resumable frame found. NONE ⇒ every scanned frame failed. We count per Parse run.
    private static int _scanned, _resumable;
    private static bool _inParse;

    private static void InstallResumabilityScan(HarmonyLib.Harmony h, ref int ok, ref int fail)
    {
        var rc = Resolve("Il2Cpp.RewindController");
        if (rc == null) { Log?.Invoke("recTrace: RewindController NOT FOUND for resumability scan"); return; }

        var parse = AccessTools.Method(rc, "Parse");
        if (parse != null)
        {
            try
            {
                h.Patch(parse,
                    prefix: new HarmonyMethod(typeof(RecorderTrace), nameof(ParsePrefix)),
                    postfix: new HarmonyMethod(typeof(RecorderTrace), nameof(ParsePostfix)));
                ok++;
            }
            catch (Exception e) { Log?.Invoke("recTrace: FAILED Parse: " + e.Message); fail++; }
        }
        else Log?.Invoke("recTrace: Parse NOT FOUND");

        var canResume = AccessTools.Method(rc, "CanResumeFromFrame");
        if (canResume != null)
        {
            try
            {
                h.Patch(canResume, postfix: new HarmonyMethod(typeof(RecorderTrace), nameof(CanResumePostfix)));
                ok++;
            }
            catch (Exception e) { Log?.Invoke("recTrace: FAILED CanResumeFromFrame: " + e.Message); fail++; }
        }
        else Log?.Invoke("recTrace: CanResumeFromFrame NOT FOUND");
    }

    private static void ParsePrefix()
    {
        _scanned = 0; _resumable = 0; _inParse = true;
        Log?.Invoke("resume: Parse scan START (counting resumable frames)");
    }

    private static void CanResumePostfix(bool __result)
    {
        if (!_inParse) return;
        _scanned++;
        if (__result) _resumable++;
    }

    private static void ParsePostfix(Il2Cpp.RewindController __instance)
    {
        _inParse = false;
        string best = "?", budget = "?", readerLen = "?";
        try
        {
            var b = __instance != null ? __instance.bestAutoRewindScrubPosition01 : default;
            best = (__instance != null && b.HasValue) ? b.Value.ToString("0.###") : "NONE";
        }
        catch (Exception e) { best = "?(" + e.Message + ")"; }
        // readerLengthMs (the exported ghost stream's End-Start) drives MaxSecondsRewindAvailable, which is
        // the SCAN BUDGET: Parse breaks once a frame is farther back than it. If this is ~0 the scan never
        // reaches the resumable frames (which live ~6-12s back behind the all-0 fall tail) → bestTarget=NONE.
        try { readerLen = __instance != null ? __instance.readerLengthMs.ToString("0") : "?"; } catch (Exception e) { readerLen = "?(" + e.Message + ")"; }
        try { budget = __instance != null ? __instance.MaxSecondsRewindAvailable.ToString("0.##") : "?"; } catch (Exception e) { budget = "?(" + e.Message + ")"; }
        Log?.Invoke($"resume: Parse scan END — scanned={_scanned} resumable={_resumable} bestTarget={best} "
            + $"readerLengthMs={readerLen} budgetSec={budget}");
    }

    private static bool TryPatch(HarmonyLib.Harmony h, MethodInfo m, HarmonyMethod prefix)
    {
        try { h.Patch(m, prefix: prefix); return true; }
        catch (Exception e) { Log?.Invoke($"recTrace: FAILED {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); return false; }
    }

    /// <summary>Detour-hazard filter: skip property accessors, generics, abstract/no-body, and by-ref /
    /// pointer / out params — the categories that crash Il2Cpp Harmony detours.</summary>
    private static bool Patchable(MethodInfo m)
    {
        if (m.IsAbstract || m.IsGenericMethod || m.IsGenericMethodDefinition) return false;
        if (m.IsSpecialName) return false; // get_/set_/add_/remove_/op_
        var n = m.Name;
        if (n.StartsWith("get_") || n.StartsWith("set_") || n.StartsWith("add_") || n.StartsWith("remove_")) return false;
        if (n == "Dispose" || n == "Finalize" || n.StartsWith("<")) return false; // ctor lambdas / teardown
        foreach (var p in m.GetParameters())
        {
            var pt = p.ParameterType;
            if (pt.IsByRef || pt.IsPointer || p.IsOut) return false;
        }
        return true;
    }

    private static Type Resolve(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            try { t = asm.GetType(fullName, false); } catch { }
            if (t != null) return t;
        }
        return null;
    }
}
