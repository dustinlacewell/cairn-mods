using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CairnDevTools;

/// <summary>
/// Localhost HTTP console so tooling OUTSIDE the process can interrogate the LIVE game:
/// GET http://127.0.0.1:PORT/cmd?q=command+args. IL2CPP objects are main-thread-only, so
/// the listener thread queues commands and waits (3 s cap) for the main-thread pump
/// (Tick) to execute them. One port per instance, probed from 14200 up; the chosen port
/// is logged at startup and greppable from the instance log.
/// </summary>
public sealed class DebugConsole
{
    private HttpListener _listener;
    private readonly ConcurrentQueue<(string cmd, TaskCompletionSource<string> reply)> _pending = new();
    private readonly Dictionary<string, Func<string[], string>> _commands = new(StringComparer.OrdinalIgnoreCase);

    public int Port { get; private set; }

    /// <summary>A handler returns this to PARK its request: the pump will NOT complete the reply — the
    /// handler (via CurrentReply) owns it and completes it later (used by the multi-tick script runner).</summary>
    public const string Parked = " PARKED ";

    /// <summary>The in-flight request's reply, valid only during a handler invocation on the main thread.
    /// A parking handler captures this to complete asynchronously across later ticks.</summary>
    public TaskCompletionSource<string> CurrentReply { get; private set; }

    public void Register(string name, Func<string[], string> handler) => _commands[name] = handler;

    /// <summary>Look up a registered verb (for the script runner's <c>do</c> dispatch).</summary>
    public bool TryGetCommand(string name, out Func<string[], string> handler)
        => _commands.TryGetValue(name, out handler);

    // Verbs whose argument is a RAW tail (may contain spaces/newlines), not space-split args.
    private static readonly HashSet<string> RawTailVerbs = new(StringComparer.OrdinalIgnoreCase) { "eval", "run" };

    public bool Start(Action<string> log)
    {
        // A fresh HttpListener per attempt: one that failed Start() cannot be reused,
        // which silently broke the probe for the second instance.
        for (int port = 14200; port < 14210; port++)
        {
            var candidate = new HttpListener();
            try
            {
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                candidate.Start();
                _listener = candidate;
                Port = port;
                break;
            }
            catch (Exception)
            {
                try { candidate.Close(); } catch (Exception) { }
            }
        }
        if (_listener == null)
        {
            log("console: no port available in 14200-14209");
            return false;
        }
        new Thread(Serve) { IsBackground = true, Name = "CairnDevTools" }.Start();
        log($"console: live at http://127.0.0.1:{Port}/cmd?q=help");
        return true;
    }

    private void Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) { break; }

            // /events — edge-triggered long-poll for OUTSIDE tooling (the repro driver / a wait-event CLI).
            // Blocks on EventBus until the next event past ?since (optionally filtered by ?name), or ?timeout
            // ms. This runs ON the listener thread (NOT the main-thread pump) so a long wait never stalls the
            // /cmd eval queue — and a separate HttpListener request gets its own Serve-loop iteration. Each
            // in-flight /events request occupies one Serve iteration; GetContext keeps accepting others.
            if (ctx.Request.Url != null && ctx.Request.Url.AbsolutePath == "/events")
            {
                // Handle on a worker thread: a long-poll must NOT block the Serve loop from accepting the
                // next request (the driver may /cmd while another /events is parked).
                ThreadPool.QueueUserWorkItem((object state) => ServeEvents(ctx));
                continue;
            }

            string q = ctx.Request.QueryString["q"] ?? "help";
            if (ctx.Request.HttpMethod == "POST")
            {
                // POST body = code/argument payload (saves URL-encoding multi-line C#).
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
                string body = reader.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(body))
                    q = (ctx.Request.QueryString["q"] ?? "eval") + " " + body;
            }
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((q, reply));
            string text;
            try
            {
                // generous: first Roslyn eval compiles cold (~seconds)
                text = reply.Task.Wait(30000) ? reply.Task.Result : "(timeout: game main thread busy)";
            }
            catch (Exception e)
            {
                text = "error: " + e.Message;
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // client went away; nothing to do
            }
        }
    }

    /// <summary>
    /// Long-poll the EventBus: <c>GET /events?since=&lt;seq&gt;&amp;name=&lt;filter&gt;&amp;timeout=&lt;ms&gt;</c>.
    /// Blocks until the next event past <c>since</c> (default: events from now on — current seq), optionally
    /// filtered by name, or until <c>timeout</c> ms (default 60000). On an event: returns its one-line JSON
    /// ({seq,name,payload,ts}). On timeout: returns {"timeout":true,"seq":&lt;currentSeq&gt;} so the client
    /// can re-poll from the right cursor without missing anything. Runs off the listener thread.
    /// </summary>
    private static void ServeEvents(HttpListenerContext ctx)
    {
        try
        {
            var qs = ctx.Request.QueryString;
            // since omitted ⇒ start from "now" so the client only gets events emitted after it began watching.
            long since = long.TryParse(qs["since"], out var s) ? s : EventBus.CurrentSeq;
            int timeout = int.TryParse(qs["timeout"], out var t) ? Math.Max(0, Math.Min(t, 600000)) : 60000;
            string name = qs["name"];

            // `oldest` lets a durable consumer detect a buffer-eviction gap (since < oldest ⇒ events lost).
            long oldest = EventBus.OldestSeq;
            string body = EventBus.TryWaitNext(since, timeout, name, out var ev)
                ? ev.ToJson(oldest)
                : "{\"timeout\":true,\"seq\":" + EventBus.CurrentSeq + ",\"oldest\":" + oldest + "}";
            Write(ctx, body);
        }
        catch (Exception e)
        {
            try { Write(ctx, "{\"error\":" + EventBus.Event.Quote(e.Message) + "}"); } catch { }
        }
    }

    private static void Write(HttpListenerContext ctx, string text)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch (Exception) { /* client went away */ }
    }

    /// <summary>Main-thread pump — call every frame.</summary>
    public void Tick()
    {
        while (_pending.TryDequeue(out var item))
        {
            string result;
            CurrentReply = item.reply;       // a parking handler may capture this and own the reply
            try
            {
                // First token = verb; the rest is the argument tail.
                int sp = item.cmd.IndexOf(' ');
                string verb = sp < 0 ? item.cmd : item.cmd[..sp];
                string tail = sp < 0 ? "" : item.cmd[(sp + 1)..];

                if (!_commands.TryGetValue(verb, out var handler))
                    result = "commands: " + string.Join(", ", _commands.Keys);
                else if (RawTailVerbs.Contains(verb))
                    // Pass the raw tail as a single arg so spaces/newlines (multi-line scripts/C#) survive.
                    result = handler(tail.Length > 0 ? new[] { tail } : Array.Empty<string>()) ?? "(null)";
                else
                {
                    var args = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    result = handler(args) ?? "(null)";
                }
            }
            catch (Exception e)
            {
                result = "exception: " + e;
            }
            finally
            {
                CurrentReply = null;
            }

            // A parked handler completes its own reply later (across ticks); don't complete it here.
            if (!ReferenceEquals(result, Parked))
                item.reply.TrySetResult(result);
        }
    }
}
