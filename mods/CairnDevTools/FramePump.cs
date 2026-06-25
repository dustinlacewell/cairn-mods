using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CairnDevTools;

/// <summary>
/// The single-threaded async substrate for multi-frame eval scripts. Everything here runs on the
/// game's main thread (driven by <see cref="Pump"/>, called once per <c>Core.OnUpdate</c>).
///
/// Two responsibilities:
///   1. <see cref="FrameSyncContext"/> — a <see cref="SynchronizationContext"/> whose <c>Post</c> queues
///      a continuation to be run on the NEXT pump. Installing it as <c>SynchronizationContext.Current</c>
///      while a Roslyn script runs makes every <c>await</c> continuation resume on a later main-thread
///      frame instead of on a thread-pool thread (which would touch IL2CPP off-thread and crash).
///   2. <see cref="Waiters"/> — level-triggered per-frame predicate polls. <c>Game.WaitFor</c> registers
///      a predicate; each pump evaluates the still-pending ones and completes the ones that went true (or
///      faulted on their deadline). Polling on the main thread is what makes IL2CPP reads in the predicate
///      legal.
///
/// Ordering inside one frame (see <see cref="Pump"/>): poll waiters FIRST (so a predicate that is already
/// true completes its Task), THEN drain queued continuations (so the await that was unblocked resumes this
/// same frame). A continuation that awaits again simply re-posts / re-registers and is picked up next frame.
/// </summary>
internal static class FramePump
{
    private static FrameSyncContext _ctx;

    /// <summary>The process-wide main-thread context. Created lazily on first use.</summary>
    internal static FrameSyncContext Context => _ctx ??= new FrameSyncContext();

    // Level-triggered predicate polls, owned by Game.WaitFor. List (not queue): a waiter persists across
    // many frames until its predicate trips or its deadline passes.
    private static readonly List<Waiter> _waiters = new();

    internal static void Register(Waiter w) => _waiters.Add(w);

    /// <summary>
    /// Advance all main-thread async work by one frame. MUST be called from the main thread only.
    /// Poll registered predicates, then drain the continuation queue. Both are drained in a way that
    /// tolerates re-entrant scheduling (a completed waiter / a resumed continuation may register or post
    /// new work; that new work is intentionally deferred to the NEXT frame, never spun in this one).
    /// </summary>
    internal static void Pump()
    {
        PollWaiters();
        Context.Drain();
    }

    private static void PollWaiters()
    {
        if (_waiters.Count == 0) return;
        // Snapshot count: anything a completing waiter registers (rare) lands at the tail and is skipped
        // this frame, evaluated next frame. Iterate backwards so RemoveAt is O(1)-ish and index-stable.
        int n = _waiters.Count;
        for (int i = n - 1; i >= 0; i--)
        {
            var w = _waiters[i];
            if (w.Poll())            // true ⇒ settled (tripped, timed out, or faulted): retire it
                _waiters.RemoveAt(i);
        }
    }
}

/// <summary>
/// Main-thread SynchronizationContext. <c>Post</c> (async resume) enqueues; <c>Send</c> (sync) runs inline
/// when already on the main thread. The queue is drained by <see cref="Drain"/> once per frame.
/// </summary>
internal sealed class FrameSyncContext : SynchronizationContext
{
    private readonly object _gate = new();
    private Queue<(SendOrPostCallback cb, object state)> _queue = new();
    private Queue<(SendOrPostCallback cb, object state)> _draining = new();

    /// <summary>The managed thread we consider "the main thread" — set on first Drain.</summary>
    internal int MainThreadId { get; private set; } = -1;

    public override void Post(SendOrPostCallback d, object state)
    {
        lock (_gate) _queue.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object state)
    {
        // Inline iff we're already on the main thread; otherwise we have no safe way to marshal
        // synchronously without risking a deadlock, so fall back to posting + spin is NOT done here —
        // the script model never calls Send cross-thread. Inline is the only legal path we expect.
        if (Thread.CurrentThread.ManagedThreadId == MainThreadId || MainThreadId == -1)
            d(state);
        else
            base.Send(d, state); // defensive; not expected in this single-threaded model
    }

    // Continuations created with TaskCreationOptions/Task.Yield carry CreateCopy semantics in some hosts.
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Run every continuation queued SO FAR this frame. Swaps the live queue out under the lock so a
    /// continuation that itself posts (awaits again) schedules onto the NEXT frame's queue rather than
    /// being run again in this drain — bounding the work per frame and guaranteeing forward progress.
    /// </summary>
    internal void Drain()
    {
        if (MainThreadId == -1) MainThreadId = Thread.CurrentThread.ManagedThreadId;

        Queue<(SendOrPostCallback, object)> batch;
        lock (_gate)
        {
            if (_queue.Count == 0) return;
            // Swap buffers: _draining is the (empty) spare, becomes the live queue; the old live queue
            // is what we run. New posts during this drain go onto the now-live (previously spare) queue.
            batch = _queue;
            _queue = _draining;
            _draining = batch;
        }

        // Install ourselves as Current for the duration so any await INSIDE a continuation also routes here.
        var prev = Current;
        SetSynchronizationContext(this);
        try
        {
            while (batch.Count > 0)
            {
                var (cb, state) = batch.Dequeue();
                try { cb(state); }
                catch (Exception e)
                {
                    // A faulting continuation must not kill the pump. Surface it; the awaiting Task’s own
                    // try/catch (in the script) or the runner’s completion handler reports script faults —
                    // this catch is the last-resort net for a continuation with no owner.
                    MelonLoader.MelonLogger.Warning("[eval pump] continuation threw: " + e);
                }
            }
        }
        finally
        {
            SetSynchronizationContext(prev);
        }
    }
}

/// <summary>
/// A level-triggered, deadline-bounded predicate poll. Created by <c>Game.WaitFor</c>; polled each frame by
/// <see cref="FramePump.PollWaiters"/>. Generic over the value the script wants back: the bool overload is
/// just <c>Waiter&lt;bool&gt;</c> with <c>done = v =&gt; v</c> and result <c>true</c>.
/// </summary>
internal abstract class Waiter
{
    /// <summary>Evaluate once on the main thread. Returns true when this waiter is SETTLED (completed,
    /// timed out, or faulted) and should be retired from the poll list.</summary>
    internal abstract bool Poll();
}

internal sealed class Waiter<T> : Waiter
{
    private readonly Func<T> _sample;        // reads game state on the main thread
    private readonly Func<T, bool> _done;    // predicate over the sample
    private readonly long _deadlineTicks;    // Environment.TickCount64 at which we time out
    private readonly int _timeoutMs;
    private readonly TaskCompletionSource<T> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<T> Task => _tcs.Task;

    internal Waiter(Func<T> sample, Func<T, bool> done, int timeoutMs)
    {
        _sample = sample;
        _done = done;
        _timeoutMs = timeoutMs;
        _deadlineTicks = Environment.TickCount64 + timeoutMs;
    }

    internal override bool Poll()
    {
        T value;
        try { value = _sample(); }
        catch (Exception e)
        {
            // A predicate that throws (e.g. FindObjectOfType returns null and the script dereferences it
            // before the object exists) is a transient miss, NOT a fault — UNTIL the deadline. Keep polling;
            // many predicates are "null until ready". Only surface the LAST error if we time out.
            if (Environment.TickCount64 >= _deadlineTicks)
            {
                _tcs.TrySetException(new TimeoutException(
                    $"timeout: {_timeoutMs}ms waiting (last predicate error: {e.Message})"));
                return true;
            }
            return false;
        }

        if (_done(value))
        {
            _tcs.TrySetResult(value);
            return true;
        }
        if (Environment.TickCount64 >= _deadlineTicks)
        {
            _tcs.TrySetException(new TimeoutException($"timeout: {_timeoutMs}ms waiting"));
            return true;
        }
        return false;
    }
}

/// <summary>
/// Script-visible async API. Roslyn scripts reach it as <c>Game.WaitFor(...)</c> / <c>Game.Continue()</c>
/// because <c>CairnDevTools</c> is in the script's import list (see <see cref="Eval"/>). Every method here
/// runs ON the main thread (the script body and all continuations do), so IL2CPP access in a predicate is
/// legal. <c>WaitFor</c> returns a Task wired to a per-frame <see cref="Waiter{T}"/>; the script awaits it.
/// </summary>
public static class Game
{
    /// <summary>
    /// Await the first frame on which <paramref name="predicate"/> is true. Level-triggered: if it is
    /// ALREADY true the returned Task completes on the very next pull (next frame), so there is no
    /// missed-edge. On timeout the Task faults with a <see cref="TimeoutException"/> carrying
    /// "timeout: &lt;n&gt;ms waiting" — the script can let it propagate (becomes the eval reply) or catch it.
    /// </summary>
    public static Task WaitFor(Func<bool> predicate, int timeoutMs = 30000)
    {
        var w = new Waiter<bool>(predicate, v => v, timeoutMs);
        FramePump.Register(w);
        return w.Task;
    }

    /// <summary>
    /// Sample <paramref name="sample"/> each frame and return the first value for which
    /// <paramref name="done"/> holds. Useful when you want the value, not just the edge —
    /// e.g. <c>await Game.WaitFor(() =&gt; FindObjectOfType&lt;EagleEyeUI&gt;(true), ui =&gt; ui != null)</c>.
    /// </summary>
    public static Task<T> WaitFor<T>(Func<T> sample, Func<T, bool> done, int timeoutMs = 30000)
    {
        var w = new Waiter<T>(sample, done, timeoutMs);
        FramePump.Register(w);
        return w.Task;
    }

    /// <summary>Frame-based delay: complete after <paramref name="ms"/> wall-clock ms, polled each frame
    /// (so it advances in lockstep with the game and is paused exactly when the pump is).</summary>
    public static Task Delay(int ms)
    {
        long start = Environment.TickCount64;
        // Reuse the waiter machinery: predicate trips when enough wall time has elapsed. timeout is set
        // generously beyond the delay so Delay never reports a spurious timeout.
        var w = new Waiter<bool>(
            () => Environment.TickCount64 - start >= ms,
            v => v,
            ms + 60000);
        FramePump.Register(w);
        return w.Task;
    }

    /// <summary>Yield to the next frame (one pump cycle). Equivalent to awaiting a single frame.</summary>
    public static Task NextFrame() => Delay(0);

    // --- named state waits: the common readiness levels, so scripts read as intent not interop plumbing ---

    /// <summary>Await the MainMenu being present (the continue-jump can fire).</summary>
    public static Task WaitForMenu(int timeoutMs = 60000)
        => WaitFor(() => UnityEngine.Object.FindObjectOfType<Il2CppTheGameBakers.Cairn.UI.MainMenu>(true) != null, timeoutMs);

    /// <summary>Await the climber pawn being spawned (gameplay live).</summary>
    public static Task WaitForGameplay(int timeoutMs = 60000)
        => WaitFor(() => Il2Cpp.PawnManager.MCSpawned, timeoutMs);

    /// <summary>Await the load being fully finished (loadingState == GameStarted) — stricter than pawn-spawned.</summary>
    public static Task WaitForFullyLoaded(int timeoutMs = 60000)
        => WaitFor(() =>
        {
            var gm = Il2Cpp.MoSingleton<Il2Cpp.GlobalGameManager>.Instance;
            return gm != null && gm.loadingState == Il2Cpp.GlobalGameManager.LoadingState.GameStarted;
        }, timeoutMs);

    /// <summary>Await the active scene having a given name (e.g. "CommonBaseScene").</summary>
    public static Task WaitForScene(string name, int timeoutMs = 60000)
        => WaitFor(() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == name, timeoutMs);

    /// <summary>Await the eagle-eye fast-travel warp view being open.</summary>
    public static Task WaitForEagleEye(int timeoutMs = 30000)
        => WaitFor(() =>
        {
            var eye = UnityEngine.Object.FindObjectOfType<Il2Cpp.EagleEyeUI>(true);
            return eye != null && eye.isInFreeRoamView;
        }, timeoutMs);

    /// <summary>
    /// The whole menu→gameplay dance in one call: wait for the menu, fire Continue, wait for the pawn to
    /// spawn AND the load to fully finish. This is the canonical "get me into a playable game" entry — the
    /// thing every test needs. Returns "gameplay-ready". Idempotent: if already in gameplay, returns at once.
    /// </summary>
    public static async Task<string> EnterGameplay(int timeoutMs = 90000)
    {
        if (Il2Cpp.PawnManager.MCSpawned) return "gameplay-ready (already in)";
        await WaitForMenu(timeoutMs);
        Continue();
        await WaitForGameplay(timeoutMs);
        await WaitForFullyLoaded(timeoutMs);
        return "gameplay-ready";
    }

    // --- synchronous action helpers: thin wrappers over existing console verbs, run inline on the pump ---

    /// <summary>Continue the most-recent save into gameplay (MainMenu → CommonBaseScene). Returns the
    /// verb's status string. Safe to call only from the main menu (the verb reports if no MainMenu).</summary>
    public static string Continue() => Gameplay.Continue(Array.Empty<string>());

    /// <summary>Run any registered console verb by name with raw args, inline on the main thread, and get
    /// its string result. Lets a script orchestrate existing verbs (<c>Game.Do("survival","on")</c>)
    /// without re-implementing them. Async/parking verbs are not supported here (returns their sentinel).</summary>
    public static string Do(string verb, params string[] args)
        => Eval.Console != null && Eval.Console.TryGetCommand(verb, out var h)
            ? (h(args) ?? "(null)")
            : $"no such verb: {verb}";
}
