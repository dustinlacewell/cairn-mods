using System;
using System.IO;
using System.Text;

namespace CairnCoop.Net;

/// <summary>
/// Cairn native netplay wire protocol constants and framing helpers.
/// Byte layouts are Ghidra-confirmed from GameAssembly.dll
/// (see re/systems/netplay/wire-protocol.md in the analysis repo).
///
/// Outer framing: [byte opcode][uint messageID if reliable][body].
/// Reliability is per-opcode: a nonzero messageID is present for
/// C->S opcodes 2,3,4,9 and S->C opcodes 130,131,135.
/// Strings are .NET BinaryWriter strings (7-bit-encoded length + UTF-8).
/// All integers little-endian.
/// </summary>
public static class Wire
{
    public const byte PROTOCOL_VERSION = 5;

    // C -> S
    public const byte CTSU_ALIVE = 1;            // unreliable, empty body
    public const byte CTSR_REGISTER = 2;         // reliable, [byte version][string playerName]
    public const byte CTSR_JOIN_CREATE_ROOM = 3; // reliable, [string secret][string code5][string location][string gamemodeName][byte maxPlayers]
    public const byte CTSR_LEAVE_ROOM = 4;       // reliable, [string secret]
    public const byte CTSU_DIE = 5;              // unreliable, [string secret]
    public const byte CTSU_FRAME = 6;            // unreliable, [string secret][byte state][byte state>>5][byte count][count * 6 floats]
    public const byte CTSU_ACKNOWLEDGE = 8;      // unreliable, [uint ackedMessageID]
    public const byte CTSR_GAMEMODE_RELAY = 9;   // reliable, [string secret][ushort len][len bytes]

    // CairnCoop's mod-private opcodes (CTSR_MOD_ROPE / STCR_MOD_ROPE) live in ModWire — kept out of the
    // native codec so this stays pure game-protocol framing. The relay ORs ModWire's reliability
    // predicate into this one (see RelayServer.OnDatagram).

    // S -> C
    public const byte STCR_CLIENT = 130;         // reliable, [int id][string secret]
    public const byte STCR_ROOM = 131;           // reliable, [byte frequency][int owner][string gamemodeName][byte count] count*([int id][string name])
    public const byte STCU_FRAMES = 132;         // unreliable, [byte clientCount] x ([int id][byte state][byte vec3Count][vec3Count * 6 floats])
    public const byte STCU_RESET = 133;          // unreliable, [byte errorCode] (+ [ushort n][n chars] if 255)
    public const byte STCU_ACKNOWLEDGE = 134;    // [uint ackedMessageID]
    public const byte STCR_GAMEMODE_RELAY = 135; // reliable, [ushort len][len bytes]

    // STCU_RESET error codes
    public const byte ERR_REQUESTED_BY_USER = 1;
    public const byte ERR_LOST_RELIABLE_COMMANDS = 2;
    public const byte ERR_NOT_REGISTERED = 3;
    public const byte ERR_NOT_IN_ROOM = 4;
    public const byte ERR_INVALID_SECRET = 5;
    public const byte ERR_UNKNOWN_ROOM = 6;
    public const byte ERR_WRONG_VERSION = 7;
    public const byte ERR_CUSTOM_SERVER = 255;

    public static bool IsReliableClientToServer(byte opcode) =>
        opcode is CTSR_REGISTER or CTSR_JOIN_CREATE_ROOM or CTSR_LEAVE_ROOM or CTSR_GAMEMODE_RELAY;

    /// <summary>Build a server->client datagram. messageID == 0 means unreliable (no id on the wire).</summary>
    public static byte[] Build(byte opcode, uint messageID, byte[] body)
    {
        int headerLen = messageID == 0 ? 1 : 5;
        var datagram = new byte[headerLen + (body?.Length ?? 0)];
        datagram[0] = opcode;
        if (messageID != 0)
            BitConverter.GetBytes(messageID).CopyTo(datagram, 1);
        body?.CopyTo(datagram, headerLen);
        return datagram;
    }

    public static byte[] Body(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        write(w);
        w.Flush();
        return ms.ToArray();
    }
}
