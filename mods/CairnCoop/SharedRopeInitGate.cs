using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnCoop;

/// <summary>
/// Wraps SharedRopeGamemode.Init (the room-join setup that also tears down the local belay
/// rig). Two responsibilities:
///
/// PREFIX — fix the game's own re-entrancy bug. Init rebuilds the private `sharedRopes`
/// dictionary by Add()-ing every room member by id, but never clears it first. On a REJOIN
/// the dict still holds the prior entries, so Init throws
/// `ArgumentException: An item with the same key has already been added` (seen live, key 1).
/// That throw aborts the join, the relay re-registers the client under a NEW id, and Init runs
/// again → throws again: the client-id churn (#2 → #4) that poisoned every later rope lookup.
/// Clearing `sharedRopes` before the original runs makes Init idempotent — it rebuilds clean
/// from the current roster — and kills the churn at its source. Init is a full rebuild (it
/// loops all members unconditionally), so a pre-clear matches its intended semantics.
///
/// POSTFIX — re-assert the local belay rig that Init just disabled, in the same frame, before
/// any later code or the next fall observes the torn-down state. Runs once per (now-singular)
/// join; idempotent regardless.
/// </summary>
[HarmonyPatch(typeof(SharedRopeGamemode), nameof(SharedRopeGamemode.Init))]
internal static class SharedRopeInitGate
{
    internal static Action OnInit;
    internal static Action<string> Log;

    private static void Prefix(SharedRopeGamemode __instance)
    {
        try
        {
            var ropes = __instance.sharedRopes;
            if (ropes != null && ropes.Count > 0)
            {
                ropes.Clear();
                Log?.Invoke("belay: cleared stale sharedRopes before Init (prevents duplicate-key throw + id churn)");
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("belay: sharedRopes pre-clear failed: " + e.Message);
        }
    }

    private static void Postfix() => OnInit?.Invoke();
}
