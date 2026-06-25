using System;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using CrossMenuActionHandler = Il2CppTheGameBakers.Cairn.UI.CrossMenuActionHandler;

namespace CrossMenuLib;

/// <summary>
/// The single Il2Cpp-injected <see cref="CrossMenuActionHandler"/> subclass. The
/// game's <c>CrossMenuUI</c> stores one handler per <c>CrossMenuActionType</c> in
/// its <c>handlers</c> dictionary and dispatches <c>OnExecute</c>/<c>IsAvailable</c>/
/// <c>GetCount</c> through the native vtable. We register ONE managed subclass and
/// give each instance a synthetic type-int; the virtual overrides route to the
/// managed <see cref="Registry"/> by that int, so consumer mods never touch Il2Cpp.
///
/// <para>This is the load-bearing assumption of the whole library: that a native
/// vtable call into the game's handler dict reaches these managed overrides. Proven
/// shaped-correct live (base exposes an (IntPtr) injectable ctor and OnExecute is
/// virtual); end-to-end dispatch is exercised by the in-game smoke test.</para>
/// </summary>
public class RoutingHandler : CrossMenuActionHandler
{
    /// <summary>Required by Il2CppInterop for objects created from the native side.</summary>
    public RoutingHandler(IntPtr ptr) : base(ptr) { }

    /// <summary>
    /// Managed construction of an INJECTED instance. Must NOT chain to a base game ctor —
    /// that would allocate a base-class native object whose vtable lacks our overrides (the
    /// dict would hold a plain CrossMenuActionHandler and OnExecute would be a no-op).
    /// DerivedConstructorPointer allocates the injected class; DerivedConstructorBody wires
    /// the managed↔native link so the injected vtable (with our overrides) is used.
    /// </summary>
    public RoutingHandler() : base(ClassInjector.DerivedConstructorPointer<RoutingHandler>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }

    /// <summary>
    /// The synthetic CrossMenuActionType int this instance answers for. Set right
    /// after construction (managed-side field; not visible to Il2Cpp, which is fine —
    /// only our overrides read it).
    /// </summary>
    public int TypeValue;

    private static bool TryEntry(int typeValue, out Registry.Entry e)
        => Registry.TryGetByType(typeValue, out e);

    public override void OnExecute()
    {
        if (TryEntry(TypeValue, out var e) && e.Action.OnExecute != null)
            Guard(e.Action.Id, "OnExecute", () => e.Action.OnExecute());
    }

    public override bool IsAvailable()
    {
        if (TryEntry(TypeValue, out var e) && e.Action.IsAvailable != null)
            return GuardBool(e.Action.Id, "IsAvailable", () => e.Action.IsAvailable());
        return true; // default-available, matching the game's treatment of plain handlers
    }

    public override int GetCount()
    {
        if (TryEntry(TypeValue, out var e) && e.Action.GetCount != null)
            return GuardInt(e.Action.Id, "GetCount", () => e.Action.GetCount());
        return 0;
    }

    public override void OnFailedExecute()
    {
        if (TryEntry(TypeValue, out var e) && e.Action.OnFailedExecute != null)
            Guard(e.Action.Id, "OnFailedExecute", () => e.Action.OnFailedExecute());
    }

    // Custom actions don't render a 3D item model; keep the base rotation neutral.
    public override Vector3 GetRendererBaseRotation() => Vector3.zero;

    // --- callbacks run inside the native call stack; never let a managed exception
    //     escape into Il2Cpp (it would crash the trampoline). Log and swallow. ---

    private static void Guard(string id, string what, System.Action body)
    {
        try { body(); }
        catch (Exception ex) { MelonLogger.Error($"[CrossMenuLib] '{id}'.{what} threw: {ex}"); }
    }

    private static bool GuardBool(string id, string what, Func<bool> body)
    {
        try { return body(); }
        catch (Exception ex) { MelonLogger.Error($"[CrossMenuLib] '{id}'.{what} threw: {ex}"); return false; }
    }

    private static int GuardInt(string id, string what, Func<int> body)
    {
        try { return body(); }
        catch (Exception ex) { MelonLogger.Error($"[CrossMenuLib] '{id}'.{what} threw: {ex}"); return 0; }
    }

    private static bool _registered;

    /// <summary>Register the injected type with Il2Cpp exactly once, at mod init.</summary>
    internal static void EnsureRegistered()
    {
        if (_registered) return;
        ClassInjector.RegisterTypeInIl2Cpp<RoutingHandler>();
        _registered = true;
        MelonLogger.Msg("[CrossMenuLib] RoutingHandler registered in Il2Cpp.");
    }
}
