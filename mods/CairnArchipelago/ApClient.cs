using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Packets;
using MelonLoader;

namespace CairnArchipelago;

// Wraps Archipelago.MultiClient.Net: connection lifecycle, location checks,
// and a main-thread-safe queue of received items. All MultiClient events fire
// on the socket thread; nothing Il2Cpp-touching happens there.
internal class ApClient
{
    internal class PendingItem
    {
        public int Index;
        public long ItemId;
        public string ItemName;
        public string SenderName;
    }

    private readonly MelonLogger.Instance log;
    private readonly ConcurrentQueue<PendingItem> pendingItems = new();
    private readonly HashSet<long> sentChecks = new();

    private ArchipelagoSession session;
    private DeathLinkService deathLink;
    private GrantLedger ledger;
    private long goalLocation = -1;
    private bool goalSent;
    private DateTime nextConnectAttempt = DateTime.MinValue;
    private int receivedIndex;

    public ApClient(MelonLogger.Instance log) => this.log = log;

    public bool Connected => session?.Socket.Connected == true;

    public void EnsureConnected()
    {
        if (Connected || DateTime.UtcNow < nextConnectAttempt)
            return;
        nextConnectAttempt = DateTime.UtcNow.AddSeconds(15);
        Connect();
    }

    private void Connect()
    {
        try
        {
            session = ArchipelagoSessionFactory.CreateSession(Core.Host.Value, Core.Port.Value);
            session.Items.ItemReceived += OnItemReceived;
            session.Socket.ErrorReceived += (e, msg) => log.Warning($"AP socket error: {msg}");

            var result = session.TryConnectAndLogin(
                "Cairn",
                Core.SlotName.Value,
                ItemsHandlingFlags.AllItems,
                password: string.IsNullOrEmpty(Core.Password.Value) ? null : Core.Password.Value);

            if (result is LoginFailure failure)
            {
                log.Warning($"AP login failed: {string.Join("; ", failure.Errors)}");
                session = null;
                return;
            }

            var success = (LoginSuccessful)result;
            ledger = GrantLedger.Load(session.RoomState.Seed, Core.SlotName.Value);
            receivedIndex = 0;

            if (success.SlotData.TryGetValue("goal_location", out var goal))
                goalLocation = Convert.ToInt64(goal);
            if (success.SlotData.TryGetValue("death_link", out var dl) && Convert.ToBoolean(dl))
                EnableDeathLink();

            log.Msg($"Connected to AP as '{Core.SlotName.Value}' (seed {session.RoomState.Seed}, " +
                    $"goal location {goalLocation})");
        }
        catch (Exception e)
        {
            log.Warning($"AP connection failed: {e.Message}");
            session = null;
        }
    }

    public void Disconnect()
    {
        try { session?.Socket.DisconnectAsync(); }
        catch { /* shutting down */ }
        session = null;
    }

    private void OnItemReceived(Archipelago.MultiClient.Net.Helpers.ReceivedItemsHelper helper)
    {
        while (helper.Any())
        {
            var item = helper.DequeueItem();
            var index = receivedIndex++;
            pendingItems.Enqueue(new PendingItem
            {
                Index = index,
                ItemId = item.ItemId,
                ItemName = item.ItemName ?? $"item {item.ItemId}",
                SenderName = item.Player.Name ?? "the multiworld",
            });
        }
    }

    // Main thread only. Grants queued items that the ledger hasn't seen yet.
    public void PumpReceivedItems(Action<long, string, string> grant)
    {
        while (pendingItems.TryDequeue(out var item))
        {
            if (ledger != null && item.Index < ledger.GrantedCount)
                continue; // already granted in an earlier session
            grant(item.ItemId, item.ItemName, item.SenderName);
            if (ledger != null)
            {
                ledger.GrantedCount = item.Index + 1;
                ledger.Save();
            }
        }
    }

    public void SendLocationCheck(long locationId, string debugName)
    {
        if (!Connected || !sentChecks.Add(locationId))
            return;
        if (!session.Locations.AllLocations.Contains(locationId))
        {
            log.Msg($"[check] {debugName} ({locationId}) not in this multiworld; ignored");
            return;
        }
        session.Locations.CompleteLocationChecks(locationId);
        log.Msg($"[check] {debugName} -> {locationId}");

        if (locationId == goalLocation && !goalSent)
        {
            goalSent = true;
            session.Socket.SendPacket(new StatusUpdatePacket { Status = ArchipelagoClientState.ClientGoal });
            log.Msg("Goal complete — summit reached!");
        }
    }

    private void EnableDeathLink()
    {
        deathLink = session.CreateDeathLinkService();
        deathLink.EnableDeathLink();
        deathLink.OnDeathLinkReceived += link =>
            log.Msg($"[deathlink] {link.Source} died ({link.Cause}) — received (not yet enforced)");
    }

    public void SendDeath(string cause)
    {
        if (deathLink == null || !Connected)
            return;
        deathLink.SendDeathLink(new DeathLink(Core.SlotName.Value, cause));
        log.Msg($"[deathlink] sent: {cause}");
    }
}
