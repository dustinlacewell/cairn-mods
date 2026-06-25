using System;
using HarmonyLib;
using UnityEngine;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnCoop;

/// <summary>
/// REVIVE-SYNC trace. The observed problem: after a co-op edelweiss revive, the two players are not
/// properly synced (the partner ghost on one side sits far from where the partner really is). RE
/// established (binary, re/native):
///   • RewindController.Resurrection is set true at EnterRewind(resurrection) and NEVER cleared on the
///     completion path (CleanupRewind/ExitRewind don't touch it) — so Resurrection=True post-revive is
///     NORMAL, not the bug.
///   • A peer's PawnState is the VERBATIM byte the partner broadcast: NetplayPawnCapture.GetPlayerPawnState
///     → CaptureFrame packs it into flags[0]&7 → NetplayRemotePlayer.SetFrame copies it. GetPlayerPawnState
///     returns Invalid(0) when the LOCAL PawnControllerSwitcher.mode (CurrentMode, +0xd4) is neither
///     Walking(1) nor Climbing(2) (i.e. None/Flying/Hovering), and not dead/falling.
///   • NetplayRemotePlayer.UpdateCurrentFrame lerps the ghost transform toward currentFrame.positions[0].
///
/// So the question this trace answers, over TIME (not one paused snapshot): does the partner ghost SETTLE
/// to the correct position after the revive completes, or stay wrong — and is the Invalid PawnState a
/// transient mid-transition value or a stuck one? It logs, in a window opened at the resurrection EnterRewind
/// and held ~25 s:
///   • every Resurrection write (EnterRewind / ExitRewind bracket / set_Resurrection) — flag lifecycle
///   • every PawnControllerSwitcher.TransitionToMode(mode) on the LOCAL switcher — the mode the revive sets
///   • once per ~0.25 s: the LOCAL mode + GetPlayerPawnState branch result, and the PARTNER GHOST's position
///     vs the local pawn position — so the log shows whether the ghost converges or drifts after settle.
///
/// Always-on once installed but self-quiet outside the window. Mirrors CallTrace's per-method prefix harness.
/// </summary>
internal static class ReviveSyncTrace
{
    internal static Action<string> Log;

    private static DateTime _windowEnd = DateTime.MinValue;
    private static bool Armed => DateTime.UtcNow < _windowEnd;
    private static DateTime _nextSample;

    private static int F { get { try { return Time.frameCount; } catch { return -1; } } }
    private static string V(Vector3 p) => $"({p.x:F2},{p.y:F2},{p.z:F2})";

    private static ClimbingV2PawnController Local()
    {
        try { return MoSingleton<PawnManager>.Instance?.ClimbingPawnController; } catch { return null; }
    }

    // ── Resurrection write lifecycle ──────────────────────────────────────────────────────────────────
    private static void EnterPre(bool resurrection, RewindController __instance)
    {
        if (resurrection)
        {
            _windowEnd = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            _nextSample = DateTime.MinValue;
            Log?.Invoke($"sync f{F} EnterRewind(resurrection=TRUE) — window OPEN 60s");
            // EVENT: the resurrection auto-rewind has begun (the player accepted the edelweiss prompt). The
            // driver awaits this to know the revive is in flight, then awaits "revive-resolved".
            Signal.Emit("revive-rewind-begin", $"frame={F}");
        }
        else if (Armed)
            Log?.Invoke($"sync f{F} EnterRewind(resurrection=false)");
    }

    private static void ExitPre(RewindController __instance)
    {
        if (!Armed) return;
        bool rez = false; try { rez = __instance.Resurrection; } catch { }
        Log?.Invoke($"sync f{F} ExitRewind ENTER — Resurrection(before)={rez}");
    }
    private static void ExitPost(RewindController __instance)
    {
        if (!Armed) return;
        bool rez = false; try { rez = __instance.Resurrection; } catch { }
        Log?.Invoke($"sync f{F} ExitRewind EXIT — Resurrection(after)={rez}");
        // EVENT: the revive auto-rewind has finished resolving (the rewind controller exited). The driver
        // awaits this after "revive-rewind-begin" to know the outcome can be classified — no sleep-poll.
        Signal.Emit("revive-resolved", $"frame={F} resurrection={rez}");
    }

    private static void SetResPre(bool value)
    {
        if (Armed) Log?.Invoke($"sync f{F} set_Resurrection({value})");
    }

    // ── the mode the revive transitions the switcher to ──────────────────────────────────────────────
    private static void TransitionToModePre(PawnControllerSwitcher.Mode mode, PawnControllerSwitcher __instance)
    {
        if (!Armed) return;
        // Only the LOCAL pawn's switcher matters (remote ghosts have their own).
        try
        {
            var local = Local();
            var sw = local != null ? local.PawnControllerSwitcher : null;
            string scope = (sw != null && __instance != null && sw.Pointer == __instance.Pointer) ? "LOCAL" : "other";
            Log?.Invoke($"sync f{F} TransitionToMode({mode}) [{scope}]");
        }
        catch { }
    }

    // ── PawnState computation: which branch yields Invalid, reading EXACTLY what GetPlayerPawnState reads.
    // GetPlayerPawnState branches on PawnManager+0x48 = the PawnControllerSwitcher, then its +0xd4 = mode int
    // (1=Walking,2=Climbing else Invalid), gated by op_Implicit(PawnManager) and IsFalling/IsDead.
    // We log on a 250ms beat for non-Invalid, but EVERY Invalid (the bug) verbatim, with the RAW mode int
    // from PawnManager+0x48+0xd4 — so a "localMode=Climbing yet broadcast=Invalid" contradiction is impossible
    // to misread (we read the same byte the game does).
    private static void GetPawnStatePost(NetFrame.PawnStateType __result)
    {
        if (!Armed) return;
        bool invalid = __result == NetFrame.PawnStateType.Invalid;
        if (!invalid)
        {
            if (DateTime.UtcNow < _nextSample) return;     // throttle the noisy Walking/Climbing samples
            _nextSample = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        }
        // else: log every Invalid (the bug) — but still rate-limit so a stuck-Invalid doesn't flood 60/s:
        else
        {
            if (DateTime.UtcNow < _nextInvalid) return;
            _nextInvalid = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        }

        string rawMode = "?", csmode = "?", ghost = "?", localPos = "?", falling = "?", dead = "?", pmExists = "?";
        try
        {
            // EXACT GetPlayerPawnState path: PawnManager.Instance (op_Implicit) → +0x48 switcher → +0xd4 mode.
            var pm = MoSingleton<PawnManager>.Instance;
            pmExists = (pm != null).ToString();
            if (pm != null)
            {
                long pmp = (long)pm.Pointer;
                IntPtr swPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr((IntPtr)(pmp + 0x48));
                if (swPtr != IntPtr.Zero)
                    rawMode = System.Runtime.InteropServices.Marshal.ReadInt32((IntPtr)((long)swPtr + 0xd4)).ToString();
                var sw = pm.PawnControllerSwitcher;
                csmode = sw != null ? sw.CurrentMode.ToString() : "no-sw";
            }
            var local = Local();
            if (local != null)
            {
                localPos = V(((Component)local).transform.position);
                falling = local.IsFalling.ToString();
            }
            var gdm = GameDataManager.Instance;
            dead = gdm != null ? gdm.IsDead.ToString() : "?";
        }
        catch { }
        try
        {
            var nm = MoSingleton<NetplayManager>.Instance;
            var gm = nm != null ? nm.Gamemode : null;
            if (gm != null)
            {
                var rps = gm.GetRemotePlayers();
                if (rps != null && rps.Count > 0 && rps[0] != null)
                    ghost = $"id{rps[0].Id}:{rps[0].PawnState}";
            }
        }
        catch { }
        string tag = invalid ? "broadcastState=INVALID(bug)" : $"broadcastState={__result}";
        Log?.Invoke($"sync f{F} {tag} rawMode+0xd4={rawMode} CurrentMode={csmode} pmExists={pmExists} "
            + $"falling={falling} dead={dead} localPos={localPos} partnerGhost={ghost}");
    }
    private static DateTime _nextInvalid;

    // ── what the partner-ghost frame APPLY receives (the position the ghost lerps toward) ─────────────
    private static void SetFramePost(NetplayRemotePlayer __instance)
    {
        if (!Armed) return;
        if (DateTime.UtcNow < _nextSample) return; // share the same 250ms gate as GetPawnStatePost
        // (no separate reset — GetPawnStatePost owns the gate; this only fires when that one let through)
        try
        {
            // PawnState the received frame carries (flags[0] & 7) is surfaced via get_PawnState.
            var st = __instance.PawnState;
            Log?.Invoke($"sync f{F} SetFrame→ghost id{__instance.Id} recvState={st} pos={V(__instance.Position)}");
        }
        catch { }
    }

    internal static void Install(HarmonyLib.Harmony h)
    {
        int ok = 0, fail = 0;
        void Patch(System.Type t, string method, string handler, bool postfix = false, System.Type[] args = null)
        {
            try
            {
                var m = args != null ? AccessTools.Method(t, method, args) : AccessTools.Method(t, method);
                if (m == null) { Log?.Invoke($"sync: NOT FOUND {t?.Name}.{method}"); fail++; return; }
                var hm = new HarmonyMethod(typeof(ReviveSyncTrace), handler);
                if (postfix) h.Patch(m, postfix: hm); else h.Patch(m, prefix: hm);
                ok++;
            }
            catch (Exception e) { Log?.Invoke($"sync: FAILED {t?.Name}.{method}: {e.Message}"); fail++; }
        }

        Patch(typeof(RewindController), nameof(RewindController.EnterRewind), nameof(EnterPre));
        Patch(typeof(RewindController), nameof(RewindController.ExitRewind), nameof(ExitPre));
        Patch(typeof(RewindController), nameof(RewindController.ExitRewind), nameof(ExitPost), postfix: true);
        // set_Resurrection: property setter.
        try
        {
            var setRes = AccessTools.PropertySetter(typeof(RewindController), "Resurrection");
            if (setRes != null) { h.Patch(setRes, prefix: new HarmonyMethod(typeof(ReviveSyncTrace), nameof(SetResPre))); ok++; }
            else { Log?.Invoke("sync: NOT FOUND set_Resurrection"); fail++; }
        }
        catch (Exception e) { Log?.Invoke("sync: FAILED set_Resurrection: " + e.Message); fail++; }

        Patch(typeof(PawnControllerSwitcher), nameof(PawnControllerSwitcher.TransitionToMode), nameof(TransitionToModePre));
        Patch(typeof(NetplayPawnCapture), "GetPlayerPawnState", nameof(GetPawnStatePost), postfix: true);
        // SetFrame(int, string, NetFrame) — the 3-arg overload the capture path uses.
        Patch(typeof(NetplayRemotePlayer), nameof(NetplayRemotePlayer.SetFrame), nameof(SetFramePost), postfix: true,
            args: new[] { typeof(int), typeof(string), typeof(NetFrame) });

        Log?.Invoke($"sync: revive-sync trace installed — patched {ok}, failed {fail}");
    }
}
