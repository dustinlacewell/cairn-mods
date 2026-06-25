using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CairnCoop.Net;

/// <summary>
/// Reimplementation of the studio's dumb relay, speaking the native protocol byte-for-byte.
/// Registration, 5-char rooms, frame fan-out, blind gamemode-blob rebroadcast, 100ms reliable
/// resend. A peer departs ONLY on sustained silence (no datagram at all for PeerTimeoutSeconds) —
/// never because a reliable went briefly unacked, which would kill a live, actively-framing peer.
/// Runs no game logic — all climbing/rope/piton state is client-side in each player's game.
/// </summary>
public sealed class RelayServer
{
    public const byte RoomFrequencyHz = 20;
    // A peer going silent is the disconnect signal — and it's a BACKSTOP, not the primary one (Steam
    // session-close for remotes, explicit leave / process death for the local test). A client
    // legitimately goes silent for seconds during a frame hitch (death→Edelweiss, scene load) or its own
    // rewind, so this must be generous; 6 s tore the room down mid-revive.
    private const double PeerTimeoutSeconds = 30;

    private sealed class Peer
    {
        public IRelayLink Link;
        public int Id;
        public string Secret;
        public string Name = "?";
        public Room Room;
        public DateTime LastSeen;
        public DateTime LastReset;
        public int FramesIn;
        public int BlobsIn;
        public string DropReason; // why the last Tick marked this peer dead (diagnostics)
        // The ARQ (resend queue, outbound id, inbound dedupe window) lives in ReliableChannel.
        public readonly ReliableChannel Reliable = new();
    }

    private sealed class Room
    {
        public string Code;
        public int Owner;
        public string GamemodeName;
        public readonly List<Peer> Members = new();
        /// <summary>Links the host has KICKED, keyed by the link's stable <see cref="IRelayLink.Label"/>
        /// (steam:&lt;id&gt; / local:&lt;endpoint&gt; — survives the ephemeral peer Id across a rejoin). A
        /// banned link's join is refused in <see cref="HandleJoinCreateRoom"/>, so a kick sticks.</summary>
        public readonly HashSet<string> Banned = new();
    }

    private readonly Dictionary<IRelayLink, Peer> _peers = new();
    private readonly Dictionary<string, Room> _rooms = new();
    private readonly Action<string> _log;
    private int _nextClientId = 1;

    /// <summary>When set, <see cref="Tick"/> resends reliable traffic but drops NOBODY. The driver
    /// raises this while the local player is mid-Edelweiss-rewind: the game thread is busy, a peer
    /// going briefly silent is expected, and tearing the room down is what caused the "Not in room"
    /// stall. Set/read across threads (Tick runs on the net thread). </summary>
    internal volatile bool SuppressTimeouts;

    /// <summary>Wall-clock of the previous <see cref="Tick"/>, to detect that the pump itself stalled
    /// (a long gap) and skip the departure sweep for that one tick rather than mass-drop every peer.</summary>
    private DateTime _lastTickUtc = DateTime.MinValue;

    public RelayServer(Action<string> log) => _log = log;

    public int PeerCount => _peers.Count;

    /// <summary>A peer sent a mod-rope announcement (opcode ModWire.CTSR_MOD_ROPE). The relay does NOT route
    /// it — it's mod-private and must never touch a game socket; the mod (which owns every transport)
    /// decides how to deliver it to each member. Carries the sender's room-member ids for fan-out.</summary>
    public event Action<RopeState, IReadOnlyList<int>> ModRopeReceived;

    /// <summary>A peer sent a mod rope-handshake event (opcode ModWire.CTSR_MOD_ROPE_REQ). Like
    /// <see cref="ModRopeReceived"/>, the relay does NOT route it — it's mod-private; the mod fans it to the
    /// addressed member over whatever transport that member uses. Carries the sender's room-member ids.</summary>
    public event Action<RopeRequest, IReadOnlyList<int>> ModRopeReqReceived;

    public IEnumerable<string> DescribePeers() =>
        _peers.Values.Select(p => $"#{p.Id} {p.Name} via {p.Link.Label}"
            + (p.Room != null ? $" room {p.Room.Code}" : "")
            + $" frames:{p.FramesIn} blobs:{p.BlobsIn}");

    /// <summary>One room member, for the host's kick UI: its room id, name, and whether it owns the room (the
    /// host itself — not kickable).</summary>
    public readonly struct Member
    {
        public readonly int Id;
        public readonly string Name;
        public readonly bool IsOwner;
        public Member(int id, string name, bool isOwner) { Id = id; Name = name; IsOwner = isOwner; }
    }

    /// <summary>Every registered room member across all rooms (in practice one room), for the host panel's
    /// per-peer kick row. A snapshot list — safe to enumerate off the net thread.</summary>
    public List<Member> RoomMembers()
    {
        var list = new List<Member>();
        foreach (var p in _peers.Values)
            if (p.Room != null && p.Id != 0)
                list.Add(new Member(p.Id, p.Name, p.Id == p.Room.Owner));
        return list;
    }

    public void OnDatagram(IRelayLink link, byte[] datagram)
    {
        if (datagram.Length < 1)
            return;

        if (!_peers.TryGetValue(link, out var peer))
        {
            peer = new Peer { Link = link };
            _peers[link] = peer;
        }
        peer.LastSeen = DateTime.UtcNow;

        byte opcode = datagram[0];
        int bodyOffset = 1;

        if (Wire.IsReliableClientToServer(opcode) || ModWire.IsReliableClientToServer(opcode))
        {
            if (datagram.Length < 5)
                return;
            uint messageId = BitConverter.ToUInt32(datagram, 1);
            bodyOffset = 5;
            // Always ack — the native client resets after 5 unacked resends.
            link.SendToClient(Wire.Build(Wire.STCU_ACKNOWLEDGE, 0, BitConverter.GetBytes(messageId)));
            if (!peer.Reliable.RememberInbound(messageId))
                return; // duplicate delivery of a retransmit — already processed
        }

        // A peer this relay doesn't know (e.g. it was talking to a previous relay
        // instance) gets reset so its autoRegister loop starts a clean session.
        // Late acks right after a reset are normal — ignore, don't re-reset.
        if (peer.Id == 0 && opcode == Wire.CTSU_ACKNOWLEDGE)
            return;
        if (peer.Id == 0 && opcode != Wire.CTSR_REGISTER)
        {
            if ((DateTime.UtcNow - peer.LastReset).TotalSeconds > 2)
            {
                peer.LastReset = DateTime.UtcNow;
                _log($"relay: unregistered peer {link.Label} sent opcode {opcode} — resetting it");
                link.SendToClient(Wire.Build(Wire.STCU_RESET, 0, new[] { Wire.ERR_NOT_REGISTERED }));
            }
            return;
        }

        using var reader = new BinaryReader(
            new MemoryStream(datagram, bodyOffset, datagram.Length - bodyOffset), Encoding.UTF8);

        try
        {
            Dispatch(peer, opcode, reader, datagram, bodyOffset);
        }
        catch (Exception e)
        {
            _log($"relay: bad opcode {opcode} from {link.Label}: {e.Message}");
        }
    }

    private void Dispatch(Peer peer, byte opcode, BinaryReader r, byte[] datagram, int bodyOffset)
    {
        switch (opcode)
        {
            case Wire.CTSU_ALIVE:
                break; // LastSeen already touched

            case Wire.CTSR_REGISTER:
                HandleRegister(peer, r);
                break;

            case Wire.CTSR_JOIN_CREATE_ROOM:
                HandleJoinCreateRoom(peer, r);
                break;

            case Wire.CTSU_FRAME:
                HandleFrame(peer, r);
                break;

            case Wire.CTSR_GAMEMODE_RELAY:
                HandleGamemodeRelay(peer, r);
                break;

            case ModWire.CTSR_MOD_ROPE:
                HandleModRope(peer, r);
                break;

            case ModWire.CTSR_MOD_ROPE_REQ:
                HandleModRopeReq(peer, r);
                break;

            case Wire.CTSR_LEAVE_ROOM:
                r.ReadString(); // secret
                LeaveRoom(peer);
                // REQUESTED_BY_USER resets the leaver's client: it tears down its gamemode,
                // drops to INACTIVE, and autoRegister starts a fresh session — clean rejoins.
                peer.Link.SendToClient(Wire.Build(Wire.STCU_RESET, 0, new[] { Wire.ERR_REQUESTED_BY_USER }));
                peer.Id = 0;
                peer.Secret = null;
                break;

            case Wire.CTSU_DIE:
                r.ReadString(); // secret
                break;

            case Wire.CTSU_ACKNOWLEDGE:
                uint acked = r.ReadUInt32();
                peer.Reliable.Acknowledge(acked);
                break;

            default:
                _log($"relay: unknown opcode {opcode} from {peer.Link.Label}");
                break;
        }
    }

    private void HandleRegister(Peer peer, BinaryReader r)
    {
        byte version = r.ReadByte();
        string name = r.ReadString();
        if (version != Wire.PROTOCOL_VERSION)
        {
            _log($"relay: {name} has protocol v{version}, want v{Wire.PROTOCOL_VERSION} — resetting");
            peer.Link.SendToClient(Wire.Build(Wire.STCU_RESET, 0, new[] { Wire.ERR_WRONG_VERSION }));
            return;
        }

        if (peer.Id == 0)
        {
            peer.Id = _nextClientId++;
            peer.Secret = Guid.NewGuid().ToString();
        }
        peer.Name = name;
        _log($"relay: registered #{peer.Id} '{name}' via {peer.Link.Label}");

        SendReliable(peer, Wire.STCR_CLIENT, Wire.Body(w =>
        {
            w.Write(peer.Id);
            w.Write(peer.Secret);
        }));
    }

    private void HandleJoinCreateRoom(Peer peer, BinaryReader r)
    {
        string secret = r.ReadString();
        string code = r.ReadString();
        string location = r.ReadString();
        string gamemodeName = r.ReadString();
        byte maxPlayers = r.ReadByte();

        if (!ValidateSecret(peer, secret))
            return;

        if (!_rooms.TryGetValue(code, out var room))
        {
            room = new Room { Code = code, Owner = peer.Id, GamemodeName = gamemodeName };
            _rooms[code] = room;
            _log($"relay: room '{code}' created by #{peer.Id} ({gamemodeName} @ {location}, max {maxPlayers})");
        }

        // A kicked link stays out: the owner banned its stable label, so refuse the (re)join with the same
        // reset the client uses for a normal leave — it bounces back to the menu instead of re-entering.
        if (room.Banned.Contains(peer.Link.Label))
        {
            _log($"relay: refused banned link {peer.Link.Label} re-joining room '{code}'");
            peer.Link.SendToClient(Wire.Build(Wire.STCU_RESET, 0, new[] { Wire.ERR_REQUESTED_BY_USER }));
            return;
        }

        LeaveRoom(peer, broadcast: false);
        room.Members.Add(peer);
        peer.Room = room;
        _log($"relay: #{peer.Id} '{peer.Name}' joined room '{code}' ({room.Members.Count} members)");
        BroadcastRoster(room);
    }

    private void HandleFrame(Peer peer, BinaryReader r)
    {
        string secret = r.ReadString();
        if (!ValidateSecret(peer, secret) || peer.Room == null)
            return;

        if (peer.FramesIn++ == 0)
            _log($"relay: first frame from #{peer.Id} '{peer.Name}' — climbing data is flowing");

        byte stateByte = r.ReadByte();
        r.ReadByte(); // second state byte is derived (stateByte >> 5) — receiver recomputes it
        byte vecCount = r.ReadByte();
        byte[] floats = r.ReadBytes(vecCount * 24); // count * (pos.xyz + euler.xyz)

        var body = Wire.Body(w =>
        {
            w.Write((byte)1); // one client's frame per packet (immediate fan-out, no batch tick)
            w.Write(peer.Id);
            w.Write(stateByte);
            w.Write(vecCount);
            w.Write(floats);
        });
        foreach (var member in peer.Room.Members)
            if (member != peer)
                member.Link.SendToClient(Wire.Build(Wire.STCU_FRAMES, 0, body));
    }

    private void HandleGamemodeRelay(Peer peer, BinaryReader r)
    {
        string secret = r.ReadString();
        if (!ValidateSecret(peer, secret) || peer.Room == null)
            return;

        ushort len = r.ReadUInt16();
        byte[] blob = r.ReadBytes(len);

        if (peer.BlobsIn++ == 0)
            _log($"relay: first gamemode blob from #{peer.Id} '{peer.Name}' ({len} bytes)");

        var body = Wire.Body(w =>
        {
            w.Write(len);
            w.Write(blob);
        });
        // Echo to ALL members INCLUDING the sender: CTC senders never apply locally —
        // e.g. RequestAttachLocalClimberToClimber only SendAttachToClimber()s, and the rope
        // is built in AttachToClimber.OnReceive when the blob comes back. Catch-up blobs
        // (0x81 header) are filtered receiver-side by forClient == LocalId.
        foreach (var member in peer.Room.Members)
            SendReliable(member, Wire.STCR_GAMEMODE_RELAY, body);
    }

    /// <summary>
    /// Mod-private rope-intent relay. CairnCoop sends this when a player connects/disconnects their
    /// belay rope to a partner; the relay tags it with the sender's id and fans it out to every room
    /// member so the partner's mod can rope back (mutual connection). The game never sees it — the
    /// mod's forwarder consumes STCR_MOD_ROPE before the game socket. The relay runs no logic on it
    /// beyond fan-out, consistent with its dumb-hub role.
    /// </summary>
    private void HandleModRope(Peer peer, BinaryReader r)
    {
        // No secret on mod-rope: it's mod-private and the sending link is already a registered peer
        // (link→peer mapping authenticates it). Reusing ValidateSecret would be wrong here — the mod
        // doesn't know the game client's internal secret, and a mismatch RESETS the game client.
        if (peer.Room == null)
            return;
        int partnerId = r.ReadInt32();
        bool connected = r.ReadByte() != 0;
        bool hanging = r.ReadByte() != 0;
        uint seq = r.ReadUInt32(); // FIX-2: per-sender monotonic seq
        int carry = r.ReadInt32(); // sender's MyCarry, for the room-wide topology (party HUD)

        // Raise to the mod — never route to links here (mod-private; a game socket would trap on it).
        // The mod fans it to the other members over whatever transport each one uses.
        var memberIds = peer.Room.Members.Select(m => m.Id).ToList();
        ModRopeReceived?.Invoke(new RopeState(peer.Id, partnerId, connected, hanging, seq, carry), memberIds);
    }

    /// <summary>
    /// Mod-private rope-handshake relay (request/accept/reject/cancel). Parallels <see cref="HandleModRope"/>:
    /// no secret (the link→peer mapping authenticates the sender), guarded on the peer being in a room, and
    /// raised to the mod for fan-out — never routed to a game socket. The sender is stamped from the
    /// registered peer; the wire body carries only the target/verb/nonce.
    /// </summary>
    private void HandleModRopeReq(Peer peer, BinaryReader r)
    {
        if (peer.Room == null)
            return;
        int target = r.ReadInt32();
        var verb = (RopeRequestVerb)r.ReadByte();
        uint nonce = r.ReadUInt32();

        var memberIds = peer.Room.Members.Select(m => m.Id).ToList();
        ModRopeReqReceived?.Invoke(new RopeRequest(peer.Id, target, verb, nonce), memberIds);
    }

    /// <summary>HOST-AUTHORITY kick (called IN-PROCESS by the host's mod — the host IS the relay, so there's
    /// no wire round-trip and no sender to authorise). Evicts member <paramref name="targetId"/> from the room
    /// it belongs to: bans its stable link label so a rejoin is refused, removes it via the normal
    /// <see cref="LeaveRoom"/> (roster re-broadcast to the rest), resets its peer slot, and sends it
    /// STCU_RESET(ERR_REQUESTED_BY_USER) so its game client disconnects — the same reset a self-leave uses,
    /// which the client already handles cleanly. No-op for a missing/own-host target. Returns a status line for
    /// the caller's log/UI. MUST run on the net thread (touches relay state); the session marshals it there.</summary>
    public string KickMember(int targetId)
    {
        // The host's own peer is the room owner; never kick it. Find the target across all rooms (the host has
        // exactly one room in practice, but scan defensively).
        Peer target = null;
        foreach (var p in _peers.Values)
            if (p.Id == targetId && p.Room != null) { target = p; break; }
        if (target == null)
            return $"kick: #{targetId} not found in any room";
        var room = target.Room;
        if (target.Id == room.Owner)
            return "kick: refusing to kick the room owner (host)";

        room.Banned.Add(target.Link.Label); // sticks across a rejoin (label is stable; the peer Id is not)
        var targetLink = target.Link;
        string code = room.Code;
        LeaveRoom(target);                   // evict + re-broadcast the roster to the remaining members
        target.Id = 0;                       // force a fresh register if it ever reconnects (and gets re-banned)
        target.Secret = null;
        targetLink.SendToClient(Wire.Build(Wire.STCU_RESET, 0, new[] { Wire.ERR_REQUESTED_BY_USER }));
        _log($"relay: #{targetId} KICKED from room '{code}' (link {targetLink.Label} banned)");
        return $"kick: #{targetId} evicted + banned";
    }

    private bool ValidateSecret(Peer peer, string secret)
    {
        if (peer.Secret != null && peer.Secret == secret)
            return true;
        _log($"relay: invalid secret from {peer.Link.Label}");
        peer.Link.SendToClient(Wire.Build(Wire.STCU_RESET, 0, new[] { Wire.ERR_INVALID_SECRET }));
        return false;
    }

    private void LeaveRoom(Peer peer, bool broadcast = true)
    {
        var room = peer.Room;
        if (room == null)
            return;
        room.Members.Remove(peer);
        peer.Room = null;
        if (room.Members.Count == 0)
        {
            _rooms.Remove(room.Code);
            _log($"relay: room '{room.Code}' empty, removed");
            return;
        }
        if (room.Owner == peer.Id)
            room.Owner = room.Members.Min(m => m.Id);
        if (broadcast)
            BroadcastRoster(room);
    }

    private void BroadcastRoster(Room room)
    {
        var body = Wire.Body(w =>
        {
            w.Write(RoomFrequencyHz);
            w.Write(room.Owner);
            w.Write(room.GamemodeName);
            w.Write((byte)room.Members.Count);
            foreach (var member in room.Members)
            {
                w.Write(member.Id);
                w.Write(member.Name);
            }
        });
        foreach (var member in room.Members)
            SendReliable(member, Wire.STCR_ROOM, body);
    }

    private static void SendReliable(Peer peer, byte opcode, byte[] body)
        => peer.Reliable.Send(opcode, body, peer.Link.SendToClient);

    /// <summary>Resend unacked reliable messages and drop dead peers. Called at a steady ~66 Hz from
    /// the network thread (<see cref="NetPump"/>), so it keeps running even while the game thread is
    /// frozen in a rewind — that is the whole point: the room stays alive across a main-thread stall.
    /// Resends always run (keep the reliable channel warm); the DEPARTURE sweep is skipped while a
    /// local rewind is in flight (<see cref="SuppressTimeouts"/>) or right after the pump itself
    /// stalled, so a transient silence never tears the room down.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        bool tickStalled = _lastTickUtc != DateTime.MinValue && (now - _lastTickUtc).TotalSeconds > 2;
        _lastTickUtc = now;
        bool mayDrop = !SuppressTimeouts && !tickStalled;

        List<Peer> dead = null;
        foreach (var peer in _peers.Values)
        {
            // Resend unacked reliables — and KEEP resending. A peer that is still sending us datagrams
            // is alive; never drop it just because a reliable hasn't been acked yet (it will be, once
            // the client catches up from a frame hitch). The old "drop after 5 unacked" killed live,
            // actively-framing peers in 500 ms — the both-dropped-while-framing room collapse.
            peer.Reliable.ResendDue(now, peer.Link.SendToClient);

            // Departure = SILENCE alone: no datagram of any kind (not even a heartbeat) for the timeout.
            // Suppressed while the local player is mid-rewind, and skipped for one tick after the pump
            // itself stalled.
            if (mayDrop && peer.Id != 0)
            {
                double silentSeconds = (now - peer.LastSeen).TotalSeconds;
                if (silentSeconds > PeerTimeoutSeconds)
                {
                    peer.DropReason = $"silent {silentSeconds:0}s";
                    (dead ??= new List<Peer>()).Add(peer);
                }
            }
        }

        if (dead == null)
            return;
        foreach (var peer in dead.Distinct().ToList())
            DropPeer(peer, peer.DropReason ?? "silent");
    }

    public void DropLink(IRelayLink link)
    {
        if (_peers.TryGetValue(link, out var peer))
            DropPeer(peer, "link closed");
    }

    private void DropPeer(Peer peer, string reason)
    {
        _log($"relay: dropping #{peer.Id} '{peer.Name}' ({reason})");
        LeaveRoom(peer);
        _peers.Remove(peer.Link);
    }
}
