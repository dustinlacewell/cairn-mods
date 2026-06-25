using System;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnCoop;

/// <summary>
/// Root-cause fix for the intermittent co-op Edelweiss "stuck halfway during the rewind" bug.
///
/// The auto-rewind coroutine (RewindController.PerformAutoRewind) replays the climber back along its
/// recorded path. As it scrubs it re-crosses weather-zone triggers, which fire the co-op weather sync
/// <c>Gamemode.WeatherManager_OnTriggerNewWeatherState → Gamemode.IsLocalClientAuthoritative()</c>. That
/// predicate THROWS <c>System.Exception("Not in room")</c> when the client isn't currently in a room — and
/// during a rewind the netplay client briefly drops out of the room (it stops acking while the game stalls
/// through the rewind's multi-second frame hitches, so the relay times it out and it re-registers a few
/// seconds later). The unhandled throw propagates out of MoveNext and KILLS the coroutine, freezing the
/// climber mid-rewind. It's intermittent because it only fires if the scrub crosses a weather trigger
/// during that no-room window. Confirmed live via a stack trace captured by RewindCoroutineInstr:
///   System.Exception: Not in room
///     at Gamemode.IsLocalClientAuthoritative ()
///     at Gamemode.WeatherManager_OnTriggerNewWeatherState (WeatherZoneData+WeatherType)
///     at RewindController+&lt;PerformAutoRewind&gt;d__57.MoveNext ()
///
/// A predicate answering "is the local client authoritative" should never throw: with no room the answer
/// is simply "no" (nothing to be authoritative over, nothing to broadcast to). This finalizer makes it
/// return false instead of throwing, so authority-gated netplay calls hit during the rewind no-op and the
/// coroutine survives the transient no-room window. (Reducing the relay's room churn during a rewind is a
/// separate, lower-priority concern — this is what actually kills the revive.)
/// </summary>
[HarmonyPatch(typeof(Gamemode), nameof(Gamemode.IsLocalClientAuthoritative))]
internal static class NetplayAuthorityGuard
{
    private static Exception Finalizer(Exception __exception, ref bool __result)
    {
        if (__exception == null)
            return null;      // normal return — leave the result untouched
        __result = false;     // no room → not authoritative; swallow the "Not in room" throw
        return null;
    }
}
