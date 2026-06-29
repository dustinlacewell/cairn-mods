using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// The diegetic rope-request/accept gesture (re/systems/coop/coop-rope-request-design.md §"Step 3+4 spec",
/// re/systems/interaction/remote-player-interaction-provider.md §"prompt-display vs verb-selection split").
/// Surfaces a controller-native prompt on a partner's ghost and routes the press to the request/accept
/// handshake — never the native attach. Bidirectional: the verb shown depends on the handshake state for that
/// ghost's <c>netPlayerId</c> — REQUEST (no relationship), ACCEPT (an incoming request from them), or UNROPE
/// (already connected). The press dispatches the matching <see cref="RopeHandshake"/> action.
///
/// Why two patches, proven from the bytes:
///   • <see cref="ArmGhostColliders"/> (postfix on <c>NetplayRemotePlayer.SetFrame</c>) — the per-frame ghost
///     applier. The ghost's provider ships with an EMPTY serialized <c>colliders[]</c>, so
///     <c>DetectColliders</c> builds no listeners and the ghost never enters the local handler's candidate set
///     → never becomes reachable → the base prompt-display gate (<c>get_ShouldDisplayPrompt</c>: requires
///     <c>playerInReachCounter>0 &amp;&amp; _ValidSensorCount!=0</c>) never fires. We populate <c>colliders[]</c>
///     from the on-GameObject trigger collider once and call public <c>DetectColliders()</c>. Idempotent
///     (latched per provider instance via <see cref="_armed"/>). PROVEN at clientState=3.
///   • <see cref="InjectInteraction"/> (postfix on
///     <c>NetplayRemotePlayerInteractionProvider.UpdateProvider</c>) — runs at the exact native write site for
///     <c>currentInteraction</c>/<c>interactionLocKey</c> (UpdateProvider.c:72-87), AFTER the base prompt pump
///     and the native verb selection. At clientState=3 all three native verbs' <c>isAvailable()</c> return
///     false (inlined <c>clientState&lt;5</c>), so native selection leaves <c>currentInteraction</c> cleared;
///     we overwrite it with OUR <see cref="RelevantInteraction"/> whose <c>isAvailable</c> is our reach rule and
///     whose <c>onInteract</c> dispatches the handshake. The next-frame <c>UpdateGameplayPromptValues</c> reads it.
///
/// The handshake is reached via <see cref="Core.Instance"/> (the same static-accessor idiom as
/// <c>Core.Instance.Driver</c>). The closures are identity-free and shared across ghosts; the per-press target
/// id is recovered from <see cref="_current"/> (the provider whose postfix is running), set each postfix and
/// read inside the synchronously-invoked press.
/// </summary>
internal static class GhostRopeGesture
{
    internal static Action<string> Log;

    /// <summary>The handshake the gesture drives (request/accept/unrope). Wired by <see cref="Core"/>.</summary>
    internal static RopeHandshake Handshake;

    /// <summary>The <c>netPlayerId</c> of the last ghost whose press we dispatched (diagnostic).</summary>
    internal static int LastPressTargetId = -1;

    // Cached Il2Cpp delegates — built once, reused for every injected interaction (constructing an Il2Cpp
    // delegate per frame would churn the GC bridge). They capture no ghost identity; the per-ghost target id
    // is read off __instance inside the closures via the provider the press came through.
    private static Il2CppSystem.Func<bool> _isAvailable;
    private static Il2CppSystem.Action _onInteract;
    // Label closure: returns a ParametrizedLocKey built from the ghost provider's OWN shipped rope lockeys
    // (attachRopeLockey → "Attach rope to {0}", detachRopeLockey → "Detach rope from {0}"), with the partner
    // name as the {0} parameter. The loc system is key-only (no runtime literal-string registration —
    // LocalizationManager.Get is key→table; ParametrizedLocKey has no raw-string field), so we REUSE the
    // game's keys rather than author custom text or our own prompt UI. Wording the player sees: "Attach rope
    // to Dustin" (request/accept) / "Detach rope from Dustin" (unrope) — localized in all 11 shipped languages
    // for free. ("accept" has no distinct shipped key; "Attach rope to {name}" is semantically fine on the
    // accepter side too.) Verdict + evidence: re/systems/coop/coop-rope-request-design.md §"prompt labels".
    private static Il2CppSystem.Func<Il2Cpp.ParametrizedLocKey> _label;

    // Per-provider "colliders already armed" latch. We key on the provider's native pointer so re-arming is
    // skipped without holding a managed ref to every ghost. A ghost that respawns gets a new pointer → re-armed.
    private static readonly System.Collections.Generic.HashSet<IntPtr> _armed = new();

    /// <summary>The provider whose UpdateProvider postfix is currently running — so the shared, identity-free
    /// <see cref="_onInteract"/> closure can recover WHICH ghost the press targets. Set each postfix, read
    /// only inside the (synchronously-invoked) onInteract.</summary>
    private static NetplayRemotePlayerInteractionProvider _current;

    internal static void Install(HarmonyLib.Harmony h)
    {
        BuildDelegates();
        int ok = 0, fail = 0;
        void Patch(Type t, string method, string handler, Type[] args = null)
        {
            try
            {
                var m = args != null ? AccessTools.Method(t, method, args) : AccessTools.Method(t, method);
                if (m == null) { Log?.Invoke($"gesture: NOT FOUND {t?.Name}.{method}"); fail++; return; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(GhostRopeGesture), handler));
                ok++;
            }
            catch (Exception e) { Log?.Invoke($"gesture: FAILED {t?.Name}.{method}: {e.Message}"); fail++; }
        }

        // SetFrame(int, string, NetFrame) — the 3-arg overload the capture path uses (same one ReviveSyncTrace
        // patches). Postfix arms the colliders[] once per provider instance.
        Patch(typeof(NetplayRemotePlayer), nameof(NetplayRemotePlayer.SetFrame), nameof(ArmGhostColliders),
            args: new[] { typeof(int), typeof(string), typeof(NetFrame) });
        Patch(typeof(NetplayRemotePlayerInteractionProvider),
            nameof(NetplayRemotePlayerInteractionProvider.UpdateProvider), nameof(InjectInteraction));

        Log?.Invoke($"gesture: installed — patched {ok}, failed {fail}");
    }

    private static void BuildDelegates()
    {
        _isAvailable = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<bool>>((Func<bool>)ReachRule);
        _onInteract = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OnInteract);
        _label = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<Il2Cpp.ParametrizedLocKey>>(
            (Func<Il2Cpp.ParametrizedLocKey>)LabelKey);
    }

    /// <summary>The prompt label, per handshake state for the current ghost: connected → "Detach rope from
    /// {name}" (the shipped detachRopeLockey); otherwise → "Attach rope to {name}" (attachRopeLockey, used for
    /// both request and accept). The provider supplies both keys; we just fill {0} with the partner name. Null
    /// only if the provider/name is unavailable (the pump then falls back to the base locKey, as before).</summary>
    private static Il2Cpp.ParametrizedLocKey LabelKey()
    {
        try
        {
            var p = _current;
            if (p == null) return null;
            var hs = Handshake;
            bool connected = hs != null && hs.IsConnected(p.netPlayerId);
            var key = connected ? p.detachRopeLockey : p.attachRopeLockey;
            string name = p.netPlayerName ?? "";
            return new Il2Cpp.ParametrizedLocKey(key, new Il2CppStringArray(new[] { name }));
        }
        catch { return null; }
    }

    // ── The injected interaction's closures ──────────────────────────────────────────────────────────────

    /// <summary>Availability: a real partner ghost always surfaces a rope action when reached — CONNECT when we
    /// are not yet roped to them (request / accept), or DETACH when we already are. The verb shown and dispatched
    /// keys on the handshake state for that ghost's id (see <see cref="LabelKey"/> / <see cref="OnInteract"/>).
    /// The reach/range gating is still the stock sensor path (the prompt only displays when in reach, via the
    /// colliders[] arm); this just confirms it's a real partner.</summary>
    private static bool ReachRule()
    {
        try
        {
            var p = _current;
            return p != null && p.netPlayerId != 0; // any reached real partner offers a rope action (connect or detach)
        }
        catch { return false; }
    }

    /// <summary>The press: pick the verb from the ghost's handshake state and dispatch it — CONNECTED → Disconnect
    /// (unrope this partner), INCOMING request from them → Accept, otherwise → SendRequest. Diegetic and
    /// per-partner: detaching here drops only the reached partner's rope (<see cref="RopeHandshake.Disconnect"/>,
    /// the per-partner equivalent of unrope-all for the 2-player case). Never fires the native attach. Runs on the
    /// main thread (the input dispatch), where handshake state is read.</summary>
    private static void OnInteract()
    {
        try
        {
            int id = _current != null ? _current.netPlayerId : -1;
            var hs = Handshake;
            if (id < 0 || hs == null)
                return;
            LastPressTargetId = id;
            if (hs.IsConnected(id))
            {
                hs.Disconnect(id);
                Log?.Invoke($"gesture: PRESS → detach rope from #{id}");
            }
            else if (hs.HasIncoming(id))
            {
                hs.Accept(id);
                Log?.Invoke($"gesture: PRESS → accept rope from #{id}");
            }
            else
            {
                hs.SendRequest(id);
                Log?.Invoke($"gesture: PRESS → request rope with #{id}");
            }
        }
        catch (Exception e) { Log?.Invoke("gesture: onInteract error " + e.Message); }
    }

    // ── Patch 1: arm the ghost provider's colliders[] so it becomes a reachable candidate ────────────────

    private static void ArmGhostColliders(NetplayRemotePlayer __instance)
    {
        try
        {
            var prov = __instance != null ? __instance.interactionProvider : null;
            if (prov == null) return;
            IntPtr key = prov.Pointer;
            if (_armed.Contains(key)) return;

            var on = prov.GetComponents<Collider>();
            if (on == null || on.Length == 0) return; // no collider yet → try again next frame (not latched)

            // THE CRITICAL FIX (re/systems/interaction/remote-player-interaction-provider.md §"wrong physics
            // layer"): the ghost provider collider ships on layer 7 (Pawn) — inherited from the pawn body — but
            // the interaction sensors are on layer 16 (Interactions), and the layer matrix has 7<->16 set to
            // IGNORE, so the sensor sphere NEVER generates OnTriggerEnter for the ghost (live-proven: zero
            // sensorEnter events while waving a hand over the ghost). Re-layer each collider's GameObject onto
            // Interactions so discovery's physics overlap can fire. Resolve the layer by name (don't hardcode
            // the index across builds); fall back to a no-op if the layer is absent.
            int interactionsLayer = LayerMask.NameToLayer("Interactions");
            var arr = new Il2CppReferenceArray<Collider>(on.Length);
            for (int i = 0; i < on.Length; i++)
            {
                arr[i] = on[i];
                if (interactionsLayer >= 0) on[i].gameObject.layer = interactionsLayer;
            }
            prov.colliders = arr;
            // Accept HANDS only (InteractionSensorType.Hands=1), NOT Walking(8). The provider-side discovery
            // gate (OnTriggerEnterCollisionListener.c:83-96) SKIPS TryAddProvider for a Walking-accepting
            // provider while the local switcher is in Climbing mode — so arming Hands|Walking blocked
            // registration on the wall (live-proven: listenerEnter fired but TryAddProvider never did). On the
            // ground the walking sensor isn't gated this way; if a ground modality is needed later, set Walking
            // only when the local pawn is NOT climbing. For now the climbing hand-reach is the path.
            try { prov.acceptedSensorTypes = (InteractionSensorType)1; } catch { }
            prov.DetectColliders();
            _armed.Add(key);
            Log?.Invoke($"gesture: armed ghost colliders (npid={prov.netPlayerId}, n={on.Length}, layer→{interactionsLayer})");
        }
        catch (Exception e) { Log?.Invoke("gesture: ArmGhostColliders error " + e.Message); }
    }

    // ── Patch 2: inject our interaction after native selection (which is cleared at clientState=3) ────────

    private static void InjectInteraction(NetplayRemotePlayerInteractionProvider __instance)
    {
        try
        {
            if (__instance == null || __instance.netPlayerId == 0) return;
            _current = __instance; // so the shared onInteract/label/isAvailable closures know the target

            // Overwrite currentInteraction with ours (native left it cleared at clientState=3). The setter
            // CopyBlocks the value-type, matching the native write at UpdateProvider.c:72-87. The label closure
            // returns the provider's shipped rope lockey + partner name → "Attach/Detach rope to/from {name}".
            __instance.currentInteraction = new NetplayRemotePlayerInteractionProvider.RelevantInteraction(
                _isAvailable, _onInteract, _label) { isValid = true };

            // CRITICAL: the prompt renders from interactionLocKey, NOT from currentInteraction.localizationKey.
            // The native UpdateProvider resolves localizationKey()→interactionLocKey only INSIDE its verb-select
            // loop (UpdateProvider.c:81-87) — which at clientState=3 finds no available native verb and never
            // runs. So WE must resolve + stamp it ourselves (live-proven: labelFunc was set but interactionLocKey
            // stayed null → [none_string]). This mirrors exactly what the native code does for a selected verb.
            try
            {
                var pk = LabelKey();
                if (pk != null) __instance.interactionLocKey = pk;
            }
            catch { /* leave interactionLocKey as-is; prompt falls back to base locKey */ }
        }
        catch (Exception e) { Log?.Invoke("gesture: InjectInteraction error " + e.Message); }
    }
}
