using System;
using System.Collections.Generic;
using System.Threading;

namespace CairnDevTools;

/// <summary>
/// Edge-triggered event signaling for OUTSIDE tooling (the repro driver, a wait-event CLI). The game
/// pushes discrete named events — "room-formed", "fall-started", "prompt-up", "revive-resolved",
/// "outcome", … — and a client long-polls <c>GET /events?since=&lt;seq&gt;&amp;timeout=&lt;ms&gt;</c>
/// (wired in <see cref="DebugConsole"/>) which BLOCKS until the next event with a higher seq exists, then
/// returns it. This replaces interval polling of eval snippets: the client awaits the next real event and
/// reacts the instant it fires, or on timeout — exactly the blocking-CLI contract the harness wants.
///
/// Events are emitted from real game callbacks (CairnCoop's Harmony trace patches) or via the
/// <c>emit &lt;name&gt; &lt;payload&gt;</c> console command, so there is NO polling on the producer side
/// either. A small ring buffer + monotonically increasing seq means a client that tracks the last seq it
/// saw never MISSES an event that fired between two waits (the classic long-poll gap).
///
/// Thread model: producers (game main thread, console command thread) call <see cref="Emit"/>; consumers
/// (the listener thread, one per in-flight /events request) call <see cref="WaitNext"/>. A single lock +
/// Monitor.PulseAll wakes all waiters on each emit; they re-check the buffer. Plain BCL, no IL2CPP.
/// </summary>
public static class EventBus
{
    public readonly struct Event
    {
        public readonly long Seq;
        public readonly string Name;
        public readonly string Payload;
        public readonly long UnixMs;
        public Event(long seq, string name, string payload, long unixMs)
        {
            Seq = seq; Name = name; Payload = payload; UnixMs = unixMs;
        }
        /// <summary>One-line JSON (payload is escaped). `oldest` = the bus's oldest buffered seq, so a
        /// durable consumer can tell whether it skipped past evicted events. Stable shape for the client.</summary>
        public string ToJson(long oldest = 0) =>
            "{\"seq\":" + Seq + ",\"name\":" + Quote(Name) + ",\"payload\":" + Quote(Payload)
            + ",\"ts\":" + UnixMs + ",\"oldest\":" + oldest + "}";

        /// <summary>Minimal JSON string-escape (quotes, backslash, control chars). Shared by the /events route.</summary>
        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    private const int Capacity = 256;            // ring buffer: tolerate bursts + slow clients without missing events
    private static readonly object Gate = new();
    private static readonly Queue<Event> Buffer = new();
    private static long _seq;                     // monotonically increasing; the cursor clients track

    /// <summary>Emit an event. Thread-safe; never throws into the caller (a fire-and-forget signal must
    /// not break game logic). Wakes every waiting /events request.</summary>
    public static void Emit(string name, string payload = "")
    {
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            lock (Gate)
            {
                var ev = new Event(++_seq, name, payload ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Buffer.Enqueue(ev);
                while (Buffer.Count > Capacity) Buffer.Dequeue();
                Monitor.PulseAll(Gate);
            }
        }
        catch { /* signaling must never destabilize the game */ }
    }

    /// <summary>The current high-water seq — a client's first request passes this so it only receives
    /// events emitted AFTER it started watching (no replay of stale events).</summary>
    public static long CurrentSeq { get { lock (Gate) return _seq; } }

    /// <summary>The oldest seq still in the ring buffer (0 if empty). A durable consumer whose persisted
    /// cursor is &lt; this lost events to buffer eviction — it can detect the gap rather than skip silently.</summary>
    public static long OldestSeq
    {
        get
        {
            lock (Gate)
            {
                foreach (var ev in Buffer) return ev.Seq; // Queue enumerates oldest-first
                return 0;
            }
        }
    }

    /// <summary>
    /// Block until an event with <c>Seq &gt; since</c> exists (yielding the earliest such event still in the
    /// buffer in <paramref name="result"/>, returns true), or until <paramref name="timeoutMs"/> elapses
    /// (returns false). If <paramref name="name"/> is non-empty, only events with that exact name satisfy the
    /// wait; others are skipped (their seq is advanced past, so the caller's cursor moves and it won't
    /// re-block on them). (out-bool, not Nullable&lt;Event&gt;, to keep this nullable-disabled project clean.)
    /// </summary>
    public static bool TryWaitNext(long since, int timeoutMs, string name, out Event result)
    {
        result = default;
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        lock (Gate)
        {
            while (true)
            {
                // Find the earliest buffered event past `since` (and matching `name` if filtered).
                foreach (var ev in Buffer)
                {
                    if (ev.Seq <= since) continue;
                    if (!string.IsNullOrEmpty(name) && ev.Name != name)
                    {
                        // Not our event, but advance the cursor past it so the caller's next `since` skips it.
                        since = ev.Seq;
                        continue;
                    }
                    result = ev;
                    return true;
                }
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(Gate, (int)Math.Min(remaining, int.MaxValue));
            }
        }
    }
}
