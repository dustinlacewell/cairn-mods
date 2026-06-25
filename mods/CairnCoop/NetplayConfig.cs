using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTheGameBakers.Cairn.Netplay;

namespace CairnCoop;

/// <summary>
/// The unsafe IL2CPP field pokes the netplay stack needs but exposes no managed setter for:
/// the <see cref="NetplayTweakables"/> connection knobs (serverAddress / playerName / autoRegister)
/// and <see cref="NetplayManager"/>.clientState. Concentrated here as named verbs so the state machine
/// (<see cref="GameDriver"/>) reads as strategy and the offset/pointer math lives behind a seam (mirrors
/// the static-seam idiom in <see cref="HangStaminaSeam"/> / <see cref="RopeLengthGuard"/>).
/// </summary>
internal static class NetplayConfig
{
    /// <summary>Point the tweakables at the local UDP bridge and turn on self-registration: writes
    /// serverAddress + playerName + autoRegister(true). The native client reads these when it (re)builds
    /// via OnNetplayTweakablesReady.</summary>
    internal static unsafe void ConfigureTweakables(NetplayTweakables tweakables, string serverAddress, string playerName)
    {
        IntPtr cls = Il2CppClassPointerStore<NetplayTweakables>.NativeClassPtr;
        IntPtr obj = IL2CPP.Il2CppObjectBaseToPtrNotNull(tweakables);

        SetStringField(obj, cls, "serverAddress", serverAddress);
        SetStringField(obj, cls, "playerName", playerName);
        SetBoolField(obj, cls, "autoRegister", true);
    }

    /// <summary>
    /// NetplayManager.clientState gates CreateJoinRoom/RegisterClient but nothing in the
    /// registration path writes it (the studio's UI presumably did) — mirror it from the client.
    /// </summary>
    internal static unsafe void MirrorClientState(NetplayManager manager, NetplayClient client)
        => WriteClientState(manager, (int)client.State);

    /// <summary>Write NetplayManager.clientState directly (no managed setter exists).</summary>
    internal static unsafe void WriteClientState(NetplayManager manager, int state)
    {
        IntPtr cls = Il2CppClassPointerStore<NetplayManager>.NativeClassPtr;
        IntPtr fieldInfo = IL2CPP.GetIl2CppField(cls, "clientState");
        IntPtr obj = IL2CPP.Il2CppObjectBaseToPtrNotNull(manager);
        *(int*)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(fieldInfo)) = state;
    }

    private static unsafe void SetStringField(IntPtr obj, IntPtr cls, string field, string value)
    {
        IntPtr fieldInfo = IL2CPP.GetIl2CppField(cls, field);
        IL2CPP.il2cpp_gc_wbarrier_set_field(obj,
            (IntPtr)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(fieldInfo)),
            IL2CPP.ManagedStringToIl2Cpp(value));
    }

    private static unsafe void SetBoolField(IntPtr obj, IntPtr cls, string field, bool value)
    {
        IntPtr fieldInfo = IL2CPP.GetIl2CppField(cls, field);
        *(bool*)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(fieldInfo)) = value;
    }
}
