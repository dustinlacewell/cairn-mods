using System;
using CairnCoop.Net;

namespace CairnCoop;

/// <summary>
/// The mod-to-mod rope-intent bus (<see cref="CairnCoop.Net.IModChannel"/>). It routes a rope-state to the
/// OTHER room member(s) over whatever transport each uses — and the game's NetplayClient never sees it.
/// <see cref="PartnerBelay"/> depends only on the interface; this is the natural router because the
/// transports it routes over are owned by <see cref="CoopSession"/>, from which it reads them.
///
/// Mod traffic must NEVER hit the game socket: every send here goes over the Steam datagram link, the
/// in-process relay fan-out, or the local-test loopback, and every inbound mod-rope frame is peeled off
/// before the game socket by the session's pump (which forwards it here).
/// </summary>
public sealed class ModRopeRouter : CairnCoop.Net.IModChannel
{
    private readonly CoopSession _session;
    private readonly Action<string> _log;
    private uint _nextModId = 1;

    public event Action<CairnCoop.Net.RopeState> OnReceived;
    public event Action<CairnCoop.Net.RopeRequest> OnRequest;

    public ModRopeRouter(CoopSession session, Action<string> log)
    {
        _session = session;
        _log = log;
    }

    // --- IModChannel: mod-to-mod rope-intent, never on the game socket -----------------------
    // Broadcast routes by role to the OTHER member(s); the game's NetplayClient never sees it.

    void CairnCoop.Net.IModChannel.Broadcast(CairnCoop.Net.RopeState msg)
    {
        try
        {
            switch (_session.CurrentRole)
            {
                case CoopSession.Role.Host:
                    // Host owns the relay: deliver straight to each remote member's mod transport,
                    // and apply to our own belay in-process (no socket).
                    FanModRopeToMembers(msg);
                    break;
                case CoopSession.Role.Joiner:
                    // Send up to the host's relay; it raises ModRopeReceived → the host fans out.
                    if (_session.JoinedHostSteamId is ulong hostId)
                        _session.SendOverSteam(hostId,
                            Wire.Build(ModWire.CTSR_MOD_ROPE, NextModId(), ModRopeBody(msg)));
                    break;
                case CoopSession.Role.LocalJoiner:
                    _session.ModLoopback?.Send(Wire.Build(ModWire.STCR_MOD_ROPE, 0, msg.ToBody()));
                    break;
            }
        }
        catch (Exception e) { _log("rope: broadcast failed: " + e.Message); }
    }

    // Send a discrete rope-handshake event (request/accept/reject/cancel) to the addressed peer, routed by
    // role exactly like Broadcast — host fan-out / joiner CTSR-up / localjoiner loopback — off the game socket.
    void CairnCoop.Net.IModChannel.Send(CairnCoop.Net.RopeRequest msg)
    {
        try
        {
            switch (_session.CurrentRole)
            {
                case CoopSession.Role.Host:
                    // Host owns the relay: deliver straight to each remote member's mod transport, and apply
                    // to our own handshake in-process (the addressed-filter keeps it for the target only).
                    FanModRopeReqToMembers(msg);
                    break;
                case CoopSession.Role.Joiner:
                    // Send up to the host's relay; it raises ModRopeReqReceived → the host fans out.
                    if (_session.JoinedHostSteamId is ulong hostId)
                        _session.SendOverSteam(hostId,
                            Wire.Build(ModWire.CTSR_MOD_ROPE_REQ, NextModId(), ModRopeReqBody(msg)));
                    break;
                case CoopSession.Role.LocalJoiner:
                    _session.ModLoopback?.Send(Wire.Build(ModWire.STCR_MOD_ROPE_REQ, 0, msg.ToBody()));
                    break;
            }
        }
        catch (Exception e) { _log("rope: send request failed: " + e.Message); }
    }

    // C->S mod-rope body: [int partnerId][byte connected][byte hanging][uint seq][int carry]. No secret — the
    // relay authenticates by the registered sending link (the mod can't see the game client's internal secret).
    private static byte[] ModRopeBody(CairnCoop.Net.RopeState msg) => Wire.Body(w =>
    {
        w.Write(msg.PartnerId);
        w.Write(msg.Connected ? (byte)1 : (byte)0);
        w.Write(msg.Hanging ? (byte)1 : (byte)0);
        w.Write(msg.Seq);   // FIX-2: per-sender monotonic seq for latest-sent-wins / reorder drop
        w.Write(msg.Carry); // sender's MyCarry, for the room-wide topology (TopologyTracker / party HUD)
    });

    // C->S mod-rope-request body: [int targetId][byte verb][uint nonce]. No secret — same authentication as
    // the rope-state path (the registered sending link). The sender is stamped by the relay, not on the wire.
    private static byte[] ModRopeReqBody(CairnCoop.Net.RopeRequest msg) => Wire.Body(w =>
    {
        w.Write(msg.TargetId);
        w.Write((byte)msg.Verb);
        w.Write(msg.Nonce);
    });

    private uint NextModId() => _nextModId++;

    /// <summary>Host-side fan-out of a rope-state to every other room member over their own transport
    /// (Steam peer or local loopback), plus optionally apply to our own belay. Never a game socket.</summary>
    public void FanModRopeToMembers(CairnCoop.Net.RopeState msg)
    {
        // To the local-test joiner (loopback) — the host can't reach its mod any other way.
        _session.ModLoopback?.Send(Wire.Build(ModWire.STCR_MOD_ROPE, 0, msg.ToBody()));
        // To Steam peers' mods.
        foreach (var link in _session.SteamLinks)
            _session.SendOverSteam(link.SteamId, Wire.Build(ModWire.STCR_MOD_ROPE, 0, msg.ToBody()));
        // Apply to our own belay (marshalled to the main thread). When this fan-out is the host relaying
        // a PEER's announcement (msg.SenderId = that peer), this is how the host ropes back. When it's
        // the host's own toggle (SenderId = us), PartnerBelay early-returns on the self-id — harmless.
        RaiseReceived(msg);
    }

    /// <summary>Raise an inbound rope-state to the belay system. ALWAYS marshalled to the main thread:
    /// it can arrive on the net thread (relay fan-out / loopback / Steam) and PartnerBelay state is read
    /// by the main-thread reconciler.</summary>
    private void RaiseReceived(CairnCoop.Net.RopeState msg) => _session.PostToMain(() => OnReceived?.Invoke(msg));

    /// <summary>Host-side fan-out of a rope-handshake event to every other room member over their own
    /// transport (Steam peer or local loopback), plus apply to our own handshake. The handshake's addressed
    /// filter (TargetId == our id) keeps only the events meant for us, so a self-echo is harmless. Never a
    /// game socket. Parallels <see cref="FanModRopeToMembers"/>.</summary>
    public void FanModRopeReqToMembers(CairnCoop.Net.RopeRequest msg)
    {
        _session.ModLoopback?.Send(Wire.Build(ModWire.STCR_MOD_ROPE_REQ, 0, msg.ToBody()));
        foreach (var link in _session.SteamLinks)
            _session.SendOverSteam(link.SteamId, Wire.Build(ModWire.STCR_MOD_ROPE_REQ, 0, msg.ToBody()));
        RaiseRequestReceived(msg);
    }

    /// <summary>Raise an inbound rope-handshake event to the handshake. ALWAYS marshalled to the main thread
    /// (it can arrive on the net thread; handshake state is read by the main-thread gesture/reconciler).</summary>
    private void RaiseRequestReceived(CairnCoop.Net.RopeRequest msg) => _session.PostToMain(() => OnRequest?.Invoke(msg));

    /// <summary>A mod loopback datagram (local-test path): a full STCR_MOD_ROPE or STCR_MOD_ROPE_REQ frame.</summary>
    public void OnModLoopbackDatagram(byte[] data)
    {
        if (data.Length < 1)
            return;
        // HUB RELAY (N-peer, ≥3 local instances): the host is the ModLoopback hub — joiners send only to it.
        // A frame from joiner B addressed to joiner C arrives here on the HOST; without re-fanning, C never
        // sees it (the host would only apply it to its OWN handshake/belay, which filters it out as not-for-me).
        // So on the host, re-broadcast every received frame to all joiners. One hop only (this fires solely on
        // genuinely-received datagrams, never on Send), and the original sender / wrong-target peers drop their
        // own echo via the self-ignore + TargetId filters downstream — no loop. For 2 instances this is a
        // harmless single echo back to the one joiner. Joiners never relay (they have only the host as a peer).
        // Presence pings (joiner→host registration only) are consumed by the host's self-registration in
        // Poll — never relayed or applied. Drop them here before the relay/apply.
        if (data[0] == CairnCoop.Net.ModLoopback.PresencePing)
            return;

        if (_session.CurrentRole == CoopSession.Role.Host)
            _session.ModLoopback?.Send(data);

        if (data[0] == ModWire.STCR_MOD_ROPE)
            ApplyModRopeDatagram(data);
        else if (data[0] == ModWire.STCR_MOD_ROPE_REQ)
            ApplyModRopeReqDatagram(data);
    }

    /// <summary>Decode an STCR_MOD_ROPE frame ([opcode][int sender][int partner][byte]) and raise it
    /// to the belay system. Ignores our own announcements (sender == our local id).</summary>
    public void ApplyModRopeDatagram(byte[] data)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(data, 1, data.Length - 1);
            using var r = new System.IO.BinaryReader(ms);
            var msg = CairnCoop.Net.RopeState.FromReader(r);
            RaiseReceived(msg);
        }
        catch (Exception e) { _log("rope: bad mod-rope datagram: " + e.Message); }
    }

    /// <summary>Decode an STCR_MOD_ROPE_REQ frame ([opcode][int sender][int target][byte verb][uint nonce])
    /// and raise it to the handshake. The handshake filters by TargetId, so events not addressed to us are
    /// dropped there (a self-echo from the host fan-out is harmless).</summary>
    public void ApplyModRopeReqDatagram(byte[] data)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(data, 1, data.Length - 1);
            using var r = new System.IO.BinaryReader(ms);
            var msg = CairnCoop.Net.RopeRequest.FromReader(r);
            RaiseRequestReceived(msg);
        }
        catch (Exception e) { _log("rope: bad mod-rope-req datagram: " + e.Message); }
    }
}
