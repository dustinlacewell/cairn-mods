using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;

[assembly: MelonInfo(typeof(CairnDevTools.Core), "CairnDevTools", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnDevTools;

/// <summary>
/// Development tooling for Cairn modding, deliberately separate from gameplay mods:
/// - Localhost HTTP console (ports 14200-14209, one per instance):
///   GET /cmd?q=&lt;command&gt;, or POST raw C# for eval.
/// - `eval`: Roslyn-compiled C# against the LIVE game on the main thread. Every loaded
///   assembly is referenced, so other mods' statics are reachable
///   (e.g. CairnCoop.Core.Instance). `Bag` persists objects between evals.
/// - Scene inspection: find / inspect / renderers / ropes.
/// - Per-instance error log mirroring MelonLogger errors/warnings (the shared Latest.log
///   is locked by the first instance in multi-instance testing).
/// </summary>
public class Core : MelonMod
{
    private DebugConsole _console;
    private System.IO.StreamWriter _errorLog;
    private string _lastError;
    private int _errorRepeats;

    public override void OnInitializeMelon()
    {
        OpenErrorLog();
        HarmonyInstance.PatchAll(typeof(Core).Assembly); // Survival freeze getter-patches
        StartConsole();
        LoggerInstance.Msg("CairnDevTools loaded.");
    }

    public override void OnUpdate()
    {
        InputSim.Pump();        // re-assert any held synthetic input through the real InputSystem
        _console?.Tick();       // accept new /cmd requests; a parking async-eval STARTS its script here
        FramePump.Pump();       // THEN advance async eval: poll WaitFor predicates, drain await continuations
        Lifecycle.Tick();       // emit menu-ready / gameplay-ready / scene-changed for wait-event
    }

    private void StartConsole()
    {
        _console = new DebugConsole();
        if (!_console.Start(message => LoggerInstance.Msg(message)))
        {
            _console = null;
            return;
        }
        Eval.Console = _console;   // lets async scripts (Game.Do) and the parking flow reach the console
        _console.Register("help", _ =>
            "belay | eval <C#> (or POST body) | find <name> | inspect <name-or-path> | renderers [minSpanMeters] | ropes | input <ctrl> <val> | gameplay | survival [on|off] | edelweiss [on|off]\n" +
            "async eval: scripts can `await Game.WaitFor(()=>predicate)` and return when ready — one blocking call, level-triggered, no events.\n" +
            "  Game.EnterGameplay() — menu→continue→pawn-spawn→fully-loaded, in one await.\n" +
            "  Game.WaitForMenu/WaitForGameplay/WaitForFullyLoaded/WaitForScene(name)/WaitForEagleEye().\n" +
            "  Game.WaitFor(()=>bool, ms) | Game.Delay(ms) | Game.Continue() | Game.Do(verb, args).");
        _console.Register("eval", a => Eval.Run(string.Join(" ", a)));
        _console.Register("belay", _ => Inspection.Belay());
        _console.Register("find", a => Inspection.Find(string.Join(" ", a)));
        _console.Register("inspect", a => Inspection.Inspect(string.Join(" ", a)));
        _console.Register("renderers", a =>
            Inspection.Renderers(a.Length > 0 && float.TryParse(a[0], out var s) ? s : 300f));
        _console.Register("ropes", _ => Inspection.Ropes());
        _console.Register("input", InputSim.Command);
        _console.Register("gameplay", Gameplay.Continue);
        _console.Register("survival", Survival.Command);
        _console.Register("edelweiss", Edelweiss.Command);
        // emit <name> [payload…] — push an event onto the EventBus from outside (eval/CLI) without a
        // compile-time reference. The driver's /events long-poll then wakes on it. Game-internal emits go
        // straight through EventBus.Emit from CairnCoop's trace patches.
        _console.Register("emit", a =>
        {
            if (a.Length == 0) return "usage: emit <name> [payload]";
            EventBus.Emit(a[0], a.Length > 1 ? string.Join(" ", a[1..]) : "");
            return "emitted " + a[0];
        });
    }

    private void OpenErrorLog()
    {
        try
        {
            var dir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.MelonLoaderDirectory, "CairnDevTools");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir,
                $"{DateTime.Now:yyyyMMdd-HHmmss}_pid{Environment.ProcessId}_errors.log");
            _errorLog = new System.IO.StreamWriter(path, append: false) { AutoFlush = true };
            MelonLogger.ErrorCallbackHandler += (section, message) => Mirror("error", section, message);
            MelonLogger.WarningCallbackHandler += (section, message) => Mirror("warn", section, message);
        }
        catch (Exception e)
        {
            LoggerInstance.Warning("error log unavailable: " + e.Message);
        }
    }

    private void Mirror(string kind, string section, string message)
    {
        string head = message.Length > 400 ? message[..400] : message;
        if (head == _lastError)
        {
            _errorRepeats++;
            if (_errorRepeats % 100 != 0)
                return;
            _errorLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {kind} [{section}]: (x{_errorRepeats}) {head}");
            return;
        }
        _lastError = head;
        _errorRepeats = 0;
        _errorLog?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {kind} [{section}]: {head}");
    }
}

/// <summary>Roslyn-backed live eval. First call compiles cold (~seconds); later calls are fast.
/// Public: the dynamically emitted script submissions must be able to access Globals.
///
/// SYNC vs ASYNC. A script with no <c>await</c> runs to completion inside <c>Run</c> on the calling Tick
/// and its reply is returned inline (the fast path, unchanged from before). A script that <c>await</c>s
/// <c>Game.WaitFor(...)</c> cannot finish on one frame; <c>Run</c> then PARKS — captures the in-flight
/// <see cref="DebugConsole.CurrentReply"/>, returns <see cref="DebugConsole.Parked"/> so the pump leaves
/// the reply open, and the script's continuations are driven across later frames by
/// <see cref="FramePump"/> (installed as the main-thread <see cref="SynchronizationContext"/>). When the
/// script's Task settles, a completion handler formats the result/exception and completes the parked reply.
/// </summary>
public static class Eval
{
    /// <summary>Script-visible roots. `Bag` persists between evals.</summary>
    public class Globals
    {
        public Action<string> Log;
        public static readonly Dictionary<string, object> Bag = new();
    }

    /// <summary>Set at startup. Lets <c>Game.Do</c> dispatch registered console verbs from a script.</summary>
    internal static DebugConsole Console;

    private static Microsoft.CodeAnalysis.Scripting.ScriptOptions _options;

    /// <summary>Default per-script deadline. Kept BELOW DebugConsole's reply.Task.Wait cap so a script that
    /// never settles faults with a clean "script timeout" reply before the HTTP side gives up — the parked
    /// reply is always completed by us, never abandoned. (The HTTP cap is 30000ms; we settle at 28000ms.)</summary>
    private const int ScriptDeadlineMs = 28000;

    internal static string Run(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "usage: eval <C# expression or statements>";

        _options ??= Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default
            .AddReferences(AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => !asm.IsDynamic && !string.IsNullOrEmpty(asm.Location)))
            // CairnDevTools import = the script sees the static `Game` async API (Game.WaitFor/Continue/Delay).
            .AddImports("System", "System.Linq", "System.Collections.Generic",
                "UnityEngine", "Il2Cpp", "Il2CppTheGameBakers.Cairn.Netplay", "CairnDevTools");

        // Run with the FRAME context current, so any await inside the script posts its continuation to the
        // per-frame pump (resuming on a later main-thread Tick) rather than onto a thread-pool thread.
        var prevCtx = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(FramePump.Context);

        // RunScript starts the script SYNCHRONOUSLY (compilation + body up to the first real suspension run
        // inline on THIS Tick under the frame context). Faults — compile errors AND runtime exceptions —
        // propagate into the returned Task, so Settle() formats them uniformly. The task is already-completed
        // iff nothing ever suspended on a Game.WaitFor.
        Task<object> task = RunScript(code);

        // The script body ran synchronously up to its first suspension; restore the ambient context for the
        // rest of Tick. The FramePump context is re-installed per-drain for the continuations themselves.
        SynchronizationContext.SetSynchronizationContext(prevCtx);

        // SYNC FAST PATH: nothing suspended ⇒ the Task is already settled; return its reply inline.
        if (task.IsCompleted)
            return Settle(task);

        // ASYNC PARK PATH: capture this request's reply and hand ownership to the completion handler.
        var reply = Console?.CurrentReply;
        if (reply == null)
            // No reply to park onto (shouldn't happen for the eval verb); block minimally as a fallback.
            return Settle(task);

        // A per-script deadline guard: if the script never settles (e.g. a WaitFor with no timeout that
        // never trips — though our WaitFor always carries one), fault it cleanly rather than hang the reply.
        var deadline = Game.Delay(ScriptDeadlineMs);
        Task.WhenAny(task, deadline).ContinueWith(_ =>
        {
            string text = task.IsCompleted
                ? Settle(task)
                : $"script timeout: {ScriptDeadlineMs}ms (script still awaiting)";
            reply.TrySetResult(text);
        }, TaskContinuationOptions.ExecuteSynchronously);

        return DebugConsole.Parked;
    }

    /// <summary>
    /// Start the script and unwrap its return value. Authored async so that BOTH a synchronous compile
    /// error (thrown by RunAsync before the first await) and a runtime exception inside the script land as a
    /// fault on the returned Task — Settle() then formats them uniformly. If the script never suspends, this
    /// Task is already completed when RunScript returns (the await of an already-completed inner Task runs
    /// synchronously), preserving the inline fast path.
    /// </summary>
    private static async Task<object> RunScript(string code)
    {
        var state = await Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript
            .RunAsync<object>(code, _options, new Globals { Log = MelonLogger.Msg });
        return state.ReturnValue;
    }

    /// <summary>Format a SETTLED script Task into a reply string — the single formatter used by both the
    /// sync fast path and the async completion handler, so a value formats identically either way.</summary>
    private static string Settle(Task<object> task)
    {
        if (task.IsFaulted)
        {
            var ex = task.Exception?.GetBaseException();
            // A WaitFor/Delay deadline surfaces as TimeoutException carrying the "timeout: …" message.
            if (ex is TimeoutException) return ex.Message;
            if (ex is Microsoft.CodeAnalysis.Scripting.CompilationErrorException ce)
                return "compile error:\n" + string.Join("\n", ce.Diagnostics);
            return "exception: " + ex;
        }
        if (task.IsCanceled)
            return "(canceled)";
        return Format(task.Result);
    }

    /// <summary>The original value→string projection (null / string / IEnumerable / ToString), unchanged.</summary>
    private static string Format(object result)
    {
        if (result == null)
            return "(null)";
        if (result is string s)
            return s;
        if (result is System.Collections.IEnumerable seq)
        {
            var lines = new List<string>();
            foreach (var item in seq)
            {
                lines.Add(item?.ToString() ?? "(null)");
                if (lines.Count >= 200) { lines.Add("...(truncated)"); break; }
            }
            return string.Join("\n", lines);
        }
        return result.ToString();
    }
}
