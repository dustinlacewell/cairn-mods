using System;
using System.Reflection;

namespace CairnCoop;

/// <summary>
/// Fire-and-forget event emitter that pushes named events onto CairnDevTools' EventBus WITHOUT a
/// compile-time reference to that mod. The repro driver long-polls <c>GET /events</c> (served by
/// CairnDevTools) and wakes the instant one of these fires — edge-triggered signaling that replaces the
/// driver's interval polling of eval snippets.
///
/// CairnCoop and CairnDevTools are independent mods (no .csproj reference), so we resolve
/// <c>CairnDevTools.EventBus.Emit(string, string)</c> by reflection across the loaded assemblies ONCE and
/// cache the delegate. If CairnDevTools isn't loaded (e.g. shipping CairnCoop standalone), every emit is a
/// silent no-op — signaling must never affect game logic. All calls are guarded; a signal can never throw
/// into a Harmony patch or the belay tick.
/// </summary>
internal static class Signal
{
    private static Action<string, string> _emit;
    private static bool _resolved;

    private static Action<string, string> Resolve()
    {
        if (_resolved) return _emit;
        _resolved = true;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("CairnDevTools.EventBus", throwOnError: false);
                if (t == null) continue;
                var mi = t.GetMethod("Emit", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string), typeof(string) }, null);
                if (mi == null) continue;
                _emit = (Action<string, string>)Delegate.CreateDelegate(typeof(Action<string, string>), mi);
                break;
            }
        }
        catch { _emit = null; }
        return _emit;
    }

    /// <summary>Emit a named event with an optional payload. No-op if CairnDevTools/EventBus is absent.</summary>
    internal static void Emit(string name, string payload = "")
    {
        try { Resolve()?.Invoke(name, payload ?? ""); } catch { }
    }
}
