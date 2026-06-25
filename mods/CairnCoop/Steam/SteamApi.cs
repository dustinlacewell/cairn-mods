using System;
using System.Runtime.InteropServices;

namespace CairnCoop.Steam;

/// <summary>
/// Flat (C) Steamworks API, P/Invoked against the steam_api64.dll the game already
/// loaded and initialized (SteamManager ran SteamAPI.Init at boot).
///
/// Deliberately NOT the IL2CPP Steamworks.NET proxy, and deliberately NO call results:
///  - Generic Callback&lt;T&gt;/CallResult&lt;T&gt; instances for multiplayer callbacks were never
///    AOT-compiled into GameAssembly, so proxy callbacks can't be constructed.
///  - Flat-API call-result polling (ISteamUtils.GetAPICallResult) races the game's own
///    SteamAPI.RunCallbacks pump, which consumes every completed call result first
///    (observed: failure reason 2 = InvalidHandle). So nothing here issues async calls.
///
/// Everything is synchronous or polled, except one callback that bypasses the dispatcher
/// entirely: the ISteamNetworkingMessages session-request callback installed as a raw
/// function pointer via ISteamNetworkingUtils.SetConfigValue (invoked during the game's
/// RunCallbacks, no registration in the managed dispatcher).
///
/// Interface accessor versions verified against the shipped dll's exports.
/// </summary>
public static class SteamApi
{
    private const string Dll = "steam_api64"; // resolves to the module already loaded in-process

    // --- interface accessors -------------------------------------------------

    [DllImport(Dll)] private static extern IntPtr SteamAPI_SteamUser_v023();
    [DllImport(Dll)] private static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Dll)] private static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
    [DllImport(Dll)] private static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

    public static IntPtr User { get; private set; }
    public static IntPtr Friends { get; private set; }
    public static IntPtr Messages { get; private set; }
    public static IntPtr NetworkingUtils { get; private set; }

    public static bool Ready { get; private set; }

    /// <summary>Grab interface pointers. Safe to retry; the game inits Steam at boot.</summary>
    public static bool TryInit()
    {
        if (Ready)
            return true;
        try
        {
            User = SteamAPI_SteamUser_v023();
            Friends = SteamAPI_SteamFriends_v017();
            Messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
            NetworkingUtils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        Ready = User != IntPtr.Zero && Friends != IntPtr.Zero && Messages != IntPtr.Zero
                && NetworkingUtils != IntPtr.Zero;
        return Ready;
    }

    // --- ISteamUser / ISteamFriends ------------------------------------------

    [DllImport(Dll)] private static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr self);
    [DllImport(Dll)] private static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll)] private static extern void SteamAPI_ISteamFriends_ClearRichPresence(IntPtr self);
    [DllImport(Dll)] private static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int friendFlags);
    [DllImport(Dll)] private static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int friendFlags);
    [DllImport(Dll)] private static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong steamId);
    [DllImport(Dll)] private static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong steamId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key);

    private const int FriendFlagImmediate = 0x04;

    public static ulong MySteamId() => SteamAPI_ISteamUser_GetSteamID(User);
    public static bool LoggedOn() => SteamAPI_ISteamUser_BLoggedOn(User);
    public static string MyName() => Utf8(SteamAPI_ISteamFriends_GetPersonaName(Friends));
    public static bool SetRichPresence(string key, string value) => SteamAPI_ISteamFriends_SetRichPresence(Friends, key, value);
    public static void ClearRichPresence() => SteamAPI_ISteamFriends_ClearRichPresence(Friends);
    public static int FriendCount() => SteamAPI_ISteamFriends_GetFriendCount(Friends, FriendFlagImmediate);
    public static ulong FriendByIndex(int i) => SteamAPI_ISteamFriends_GetFriendByIndex(Friends, i, FriendFlagImmediate);
    public static string FriendName(ulong id) => Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(Friends, id));
    public static string FriendRichPresence(ulong id, string key) => Utf8(SteamAPI_ISteamFriends_GetFriendRichPresence(Friends, id, key));

    // --- ISteamNetworkingMessages ----------------------------------------------

    public const int SendReliableNoNagle = 8 | 1;
    public const int SendUnreliableNoNagle = 0 | 1;

    [StructLayout(LayoutKind.Sequential, Size = 136)]
    public struct NetworkingIdentity
    {
        public int Type;       // 16 = k_ESteamNetworkingIdentityType_SteamID
        public int Size;       // 8 for a steamID64
        public ulong SteamId64;

        public static NetworkingIdentity For(ulong steamId) => new() { Type = 16, Size = 8, SteamId64 = steamId };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NetworkingMessage
    {
        public IntPtr Data;                   // 0
        public int Size;                      // 8
        public uint Connection;               // 12
        public NetworkingIdentity Peer;       // 16 (136 bytes)
        public long ConnectionUserData;       // 152
        public long TimeReceived;             // 160
        public long MessageNumber;            // 168
        public IntPtr FreeDataFn;             // 176
        public IntPtr ReleaseFn;              // 184
        public int Channel;                   // 192
        public int Flags;                     // 196
        public long UserData;                 // 200
    }

    [DllImport(Dll)] private static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self,
        ref NetworkingIdentity peer, IntPtr data, uint len, int sendFlags, int channel);
    [DllImport(Dll)] private static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self,
        int channel, [Out] IntPtr[] messages, int maxMessages);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, ref NetworkingIdentity peer);
    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, ref NetworkingIdentity peer);
    [DllImport(Dll)] private static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);

    public static unsafe int SendMessageToUser(ulong steamId, byte[] data, int sendFlags, int channel)
    {
        var identity = NetworkingIdentity.For(steamId);
        fixed (byte* p = data)
            return SteamAPI_ISteamNetworkingMessages_SendMessageToUser(Messages, ref identity, (IntPtr)p, (uint)data.Length, sendFlags, channel);
    }

    private static readonly IntPtr[] _messageBuffer = new IntPtr[64];

    public static void ReceiveMessagesOnChannel(int channel, Action<ulong, byte[]> sink)
    {
        while (true)
        {
            int n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(Messages, channel, _messageBuffer, _messageBuffer.Length);
            for (int i = 0; i < n; i++)
            {
                var msg = Marshal.PtrToStructure<NetworkingMessage>(_messageBuffer[i]);
                var data = new byte[msg.Size];
                Marshal.Copy(msg.Data, data, 0, msg.Size);
                SteamAPI_SteamNetworkingMessage_t_Release(_messageBuffer[i]);
                sink(msg.Peer.SteamId64, data);
            }
            if (n < _messageBuffer.Length)
                return;
        }
    }

    public static void AcceptSessionWithUser(ulong steamId)
    {
        var identity = NetworkingIdentity.For(steamId);
        SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(Messages, ref identity);
    }

    public static void CloseSessionWithUser(ulong steamId)
    {
        var identity = NetworkingIdentity.For(steamId);
        SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(Messages, ref identity);
    }

    // --- session-request callback (raw function pointer; bypasses the dispatcher) ----

    [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamNetworkingUtils_SetConfigValue(IntPtr self,
        int configValue, int scopeType, IntPtr scopeObj, int dataType, IntPtr arg);

    private const int ConfigCallbackMessagesSessionRequest = 205; // k_ESteamNetworkingConfig_Callback_MessagesSessionRequest
    private const int ConfigScopeGlobal = 1;
    private const int ConfigDataTypePtr = 5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SessionRequestDelegate(IntPtr request);

    private static SessionRequestDelegate _sessionRequestKeepAlive;

    /// <summary>Fired (on the game's RunCallbacks pump) when a remote peer wants a messages session.</summary>
    public static event Action<ulong> SessionRequested;

    /// <summary>Install the session-request handler. The host needs this to admit joiners.</summary>
    public static unsafe bool InstallSessionRequestHandler()
    {
        if (_sessionRequestKeepAlive != null)
            return true;
        _sessionRequestKeepAlive = OnSessionRequest;
        IntPtr fn = Marshal.GetFunctionPointerForDelegate(_sessionRequestKeepAlive);
        return SteamAPI_ISteamNetworkingUtils_SetConfigValue(NetworkingUtils,
            ConfigCallbackMessagesSessionRequest, ConfigScopeGlobal, IntPtr.Zero, ConfigDataTypePtr, (IntPtr)(&fn));
    }

    private static void OnSessionRequest(IntPtr request)
    {
        // SteamNetworkingMessagesSessionRequest_t = { SteamNetworkingIdentity m_identityRemote }
        ulong steamId = (ulong)Marshal.ReadInt64(request, 8);
        SessionRequested?.Invoke(steamId);
    }

    private static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p);
}
