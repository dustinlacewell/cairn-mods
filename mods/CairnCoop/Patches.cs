using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppSystem.Net;
using Il2CppSystem.Net.Sockets;

namespace CairnCoop;

/// <summary>
/// The native NetplayClient binds 0.0.0.0:14000 before connecting out, which makes a
/// second game instance on the same machine fail its bind (receive thread dies).
/// The local port is irrelevant — the server replies to the observed source endpoint and
/// the client's inbound filter only checks the REMOTE (serverAddress:14000) — so rebind
/// port 14000 requests to an ephemeral port. Enables multi-instance local testing.
/// </summary>
[HarmonyPatch(typeof(Socket), nameof(Socket.Bind))]
internal static class SocketBindPatch
{
    private static unsafe void Prefix(EndPoint localEP)
    {
        var ip = localEP?.TryCast<IPEndPoint>();
        if (ip == null || ip.Port != Net.UdpBridge.NetplayPort)
            return;
        // IPEndPoint.set_Port is stripped from the proxy; write the backing field.
        var cls = Il2CppClassPointerStore<IPEndPoint>.NativeClassPtr;
        var field = IL2CPP.GetIl2CppField(cls, "_port");
        var obj = IL2CPP.Il2CppObjectBaseToPtrNotNull(ip);
        *(int*)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(field)) = 0;
    }
}
