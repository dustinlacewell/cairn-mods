using System;
using System.Collections.Generic;
using CairnAPI;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.UI;

namespace CairnCoop;

/// <summary>
/// The party / chain HUD: a real uGUI overlay (anchored TOP-LEFT) that renders the room-wide rope CHAIN(S)
/// — who is roped to whom, in carry order, with each climber's live net state. It reads the directed carry
/// graph that <see cref="TopologyTracker"/> assembles from every climber's broadcast carry edge (sender→carry),
/// merges in the LOCAL climber's own edge (<see cref="RopeHandshake.MyCarry"/>, which we don't receive as a
/// remote broadcast), orders each connected component head→tail, and draws one row per climber.
///
/// Awareness-only, like <see cref="RopeRequestHud"/>: it never drives the handshake, owns no game-state plumbing
/// beyond the injected accessors, and is fail-open (a UI exception must never break the mod). Built on the shared
/// <see cref="CairnAPI.Ui"/> design-system layer — the same vocabulary as every other CairnCoop surface.
///
/// Per-climber STATE is the net-frame <see cref="NetFrame.PawnStateType"/> read off each remote ghost
/// (Walking/Climbing = on-wall, Falling, Dead, else Idle); the local climber is tagged "(you)". Hanging is NOT
/// shown — it isn't a room-wide broadcast fact (only addressed neighbours learn it via MutualRope), so the HUD
/// stays honest about what every client can actually know.
///
/// Lifecycle: the canvas is built lazily on the first frame with ≥1 chain edge and then kept. Rows are rebuilt
/// only when the chain STRUCTURE changes (the ordered id list differs); the per-frame <see cref="Tick"/> just
/// refreshes each row's name + state text.
/// </summary>
internal sealed class PartyHud
{
    /// <summary>Room-wide graph (senderId → (carry, hanging)) for every REMOTE climber, fresh as of `now`.</summary>
    private readonly Func<DateTime, Dictionary<int, TopologyTracker.Node>> _topology;
    /// <summary>The live remote-player roster — id → ghost (for net state + display name).</summary>
    private readonly Func<Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer>> _roster;
    /// <summary>The LOCAL climber's id, MyCarry edge, and display name — facts we don't receive as a broadcast.</summary>
    private readonly Func<int> _localId;
    private readonly Func<int> _localCarry;
    private readonly Func<string> _localName;

    internal Action<string> Log;

    // Palette (the shared design tokens, plus a couple of state accents).
    private static readonly Color HeaderText = new(1f, 1f, 1f, 1f);
    private static readonly Color StateWall   = new(0.55f, 0.80f, 0.55f, 1f); // on the wall — green
    private static readonly Color StateHang    = new(0.55f, 0.70f, 0.95f, 1f); // hanging on the rope — blue
    private static readonly Color StateFall    = new(0.95f, 0.70f, 0.35f, 1f); // falling (unsecured) — amber
    private static readonly Color StateDead    = new(0.90f, 0.45f, 0.45f, 1f); // dead — red
    private static readonly Color StateIdle    = Ui.TextDim;

    private Canvas _canvas;
    private RectTransform _container;
    private RectTransform _rowsHost;

    /// <summary>One built row, kept so its text refreshes in place. Keyed by climber id in <see cref="_rows"/>.</summary>
    private sealed class Row
    {
        public GameObject Go;
        public Text Name;   // "↳ John" / "John (you)"
        public Text State;  // "on wall" / "falling" / "dead" / "idle"
    }

    private readonly Dictionary<int, Row> _rows = new();
    // The ordered id list last rendered — a rebuild happens only when this sequence changes.
    private readonly List<int> _ordered = new();
    private string _lastSig = "";

    internal PartyHud(
        Func<DateTime, Dictionary<int, TopologyTracker.Node>> topology,
        Func<Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer>> roster,
        Func<int> localId, Func<int> localCarry, Func<string> localName)
    {
        _topology = topology;
        _roster = roster;
        _localId = localId;
        _localCarry = localCarry;
        _localName = localName;
    }

    /// <summary>Per-frame refresh (from <see cref="Core.OnUpdate"/>): assemble the room-wide carry graph (remote
    /// edges + the local edge), order it head→tail, rebuild rows only on a structure change, and refresh each
    /// row's name + state every frame. Fail-open.</summary>
    internal void Tick()
    {
        try
        {
            var nodes = BuildNodes();             // id → (carry, hanging) for every roped climber (incl. local)
            var edges = CarryEdges(nodes);        // id → carryId, for ordering
            var order = OrderChains(edges);       // head→tail across every connected component

            string sig = string.Join(",", order);
            if (sig != _lastSig)
            {
                Rebuild(order);
                _lastSig = sig;
            }
            RefreshRows(nodes, edges);

            if (_canvas != null)
                _canvas.gameObject.SetActive(_rows.Count > 0);
        }
        catch (Exception e) { Log?.Invoke($"partyhud: Tick failed — {e.Message}"); }
    }

    // ── data model: assemble + order the carry graph ────────────────────────────────────────────────────

    /// <summary>The room-wide graph this client can see: every REMOTE climber's broadcast node (carry+hanging)
    /// plus the LOCAL climber's own node (carry from MyCarry, hanging from its live controller — we don't receive
    /// our own broadcast). Only roped climbers appear (a node with no carry and nobody depending on it is dropped
    /// by OrderChains' reachability).</summary>
    private Dictionary<int, TopologyTracker.Node> BuildNodes()
    {
        var nodes = _topology(DateTime.UtcNow) ?? new Dictionary<int, TopologyTracker.Node>();
        int me = _localId();
        if (me > 0)
        {
            int myCarry = _localCarry();
            if (myCarry >= 0 || DependsOnExists(nodes, me))
                nodes[me] = new TopologyTracker.Node(myCarry, LocalHanging());
        }
        return nodes;
    }

    /// <summary>Project the node map to a plain id→carry map for the ordering pass.</summary>
    private static Dictionary<int, int> CarryEdges(Dictionary<int, TopologyTracker.Node> nodes)
    {
        var edges = new Dictionary<int, int>(nodes.Count);
        foreach (var kv in nodes)
            edges[kv.Key] = kv.Value.Carry;
        return edges;
    }

    /// <summary>True if any known node names <paramref name="id"/> as its carry (someone hangs below it).</summary>
    private static bool DependsOnExists(Dictionary<int, TopologyTracker.Node> nodes, int id)
    {
        foreach (var kv in nodes)
            if (kv.Value.Carry == id)
                return true;
        return false;
    }

    /// <summary>Whether the LOCAL climber is currently weighting its own rope — the SAME fact PartnerBelay
    /// announces to remotes as Hanging (ClimberQuery.LocalClimberWeightingRope), recomputed here off the local
    /// module type so the local row matches what remotes see for us. The net frame doesn't describe us, so this
    /// reads the controller's module directly; the weighting module set mirrors LocalClimberWeightingRope.</summary>
    private bool LocalHanging()
    {
        try
        {
            var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
            var module = pc?.CurrentModule;
            if (module == null)
                return false;
            switch (module.type)
            {
                case ClimbingV2PawnControllerModule.ClimbingModule.SecuredFalling:
                case ClimbingV2PawnControllerModule.ClimbingModule.HangIdle:
                case ClimbingV2PawnControllerModule.ClimbingModule.Abseil:
                case ClimbingV2PawnControllerModule.ClimbingModule.RopeAscending:
                case ClimbingV2PawnControllerModule.ClimbingModule.Rappel:
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    /// <summary>Order every connected component HEAD→TAIL: a head is a climber that is someone's carry but whose
    /// own carry is absent from the set (or -1); we walk down the dependent chain from each head. Robust to the
    /// distributed graph being momentarily partial (a missing middle just starts a new fragment). Cycles (which
    /// the carry model forbids) are broken by a visited set so we never loop forever.</summary>
    private List<int> OrderChains(Dictionary<int, int> edges)
    {
        // dependents[c] = the climbers whose carry is c (the people hanging below c).
        var dependents = new Dictionary<int, List<int>>();
        var allIds = new HashSet<int>();
        foreach (var kv in edges)
        {
            allIds.Add(kv.Key);
            if (kv.Value >= 0)
            {
                allIds.Add(kv.Value);
                if (!dependents.TryGetValue(kv.Value, out var list))
                    dependents[kv.Value] = list = new List<int>();
                list.Add(kv.Key);
            }
        }

        // A head: nobody it carries... no — a head is a climber whose OWN carry is not present (top of a chain).
        // i.e. id has no edge, OR its carry id isn't itself a known climber edge. Walk down from each head.
        var ordered = new List<int>();
        var visited = new HashSet<int>();
        var heads = new List<int>();
        foreach (int id in allIds)
        {
            bool hasCarry = edges.TryGetValue(id, out int c) && c >= 0 && allIds.Contains(c);
            if (!hasCarry)
                heads.Add(id);
        }
        heads.Sort();
        foreach (int head in heads)
            WalkDown(head, dependents, visited, ordered);
        // Any leftover (a pure cycle, forbidden by the model but defended against) — append in id order.
        if (ordered.Count < allIds.Count)
            foreach (int id in allIds)
                if (visited.Add(id))
                    ordered.Add(id);
        return ordered;
    }

    private static void WalkDown(int id, Dictionary<int, List<int>> dependents, HashSet<int> visited, List<int> ordered)
    {
        if (!visited.Add(id))
            return;
        ordered.Add(id);
        if (dependents.TryGetValue(id, out var below))
        {
            below.Sort();
            foreach (int d in below)
                WalkDown(d, dependents, visited, ordered);
        }
    }

    // ── rendering ───────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild(List<int> order)
    {
        EnsureCanvas();
        // Drop rows no longer in the chain.
        if (_rows.Count > 0)
        {
            List<int> stale = null;
            foreach (var kv in _rows)
                if (!order.Contains(kv.Key))
                    (stale ??= new List<int>()).Add(kv.Key);
            if (stale != null)
                foreach (int id in stale)
                    RemoveRow(id);
        }
        // Add/realign rows in chain order: destroy+rebuild keeps sibling order trivially correct.
        foreach (int id in order)
            if (!_rows.ContainsKey(id))
                AddRow(id);
        // Re-parent in order so the VStack draws head→tail (rows added later than an existing one would
        // otherwise sit at the bottom; a forced sibling-index sort keeps the visual order = chain order).
        for (int i = 0; i < order.Count; i++)
            if (_rows.TryGetValue(order[i], out var row) && row.Go != null)
                row.Go.transform.SetSiblingIndex(i);
        LayoutRebuilder.ForceRebuildLayoutImmediate(_rowsHost);
    }

    private void RefreshRows(Dictionary<int, TopologyTracker.Node> nodes, Dictionary<int, int> edges)
    {
        var roster = SafeRoster();
        int me = _localId();
        foreach (var kv in _rows)
        {
            int id = kv.Key;
            Row row = kv.Value;
            bool isMe = id == me;
            bool hasCarry = edges.TryGetValue(id, out int c) && c >= 0;
            // "↳" marks a climber that hangs below a carry; the head (no carry) gets no arrow.
            string prefix = hasCarry ? "↳ " : "";
            string name = isMe ? $"{_localName()} (you)" : ResolveName(roster, id);
            row.Name.text = prefix + name;

            bool hanging = nodes.TryGetValue(id, out var node) && node.Hanging;
            var (label, color) = StateBadge(roster, id, isMe, hanging);
            row.State.text = label;
            row.State.color = color;
        }
    }

    /// <summary>A climber's state badge. HANGING (the announced weighting fact) WINS over the net frame: a caught
    /// hang net-reports as "Falling" (re/.../ModChannel.cs), so without this a hanging climber would mislabel as
    /// falling. Else: dead/falling from the remote net frame (or the local controller for me), else on-wall.</summary>
    private (string, Color) StateBadge(Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> roster, int id, bool isMe, bool hanging)
    {
        if (hanging)
            return ("hanging", StateHang);

        if (isMe)
        {
            try
            {
                var pc = MoSingleton<PawnManager>.Instance?.ClimbingPawnController;
                if (pc == null) return ("…", StateIdle);
                if (pc.IsFalling || pc.IsSecureFalling) return ("falling", StateFall);
                return ("on wall", StateWall);
            }
            catch { return ("…", StateIdle); }
        }

        var ghost = FindGhost(roster, id);
        if (ghost == null) return ("…", StateIdle);
        switch (ghost.PawnState)
        {
            case NetFrame.PawnStateType.Walking:
            case NetFrame.PawnStateType.Climbing: return ("on wall", StateWall);
            case NetFrame.PawnStateType.Falling:  return ("falling", StateFall);
            case NetFrame.PawnStateType.Dead:     return ("dead", StateDead);
            default:                              return ("idle", StateIdle);
        }
    }

    // ── canvas / row construction (the shared Ui kit) ───────────────────────────────────────────────────

    private void EnsureCanvas()
    {
        if (_canvas != null)
            return;
        Ui.Init();

        var canvasGo = new GameObject("CairnCoop_PartyHud");
        UnityEngine.Object.DontDestroyOnLoad(canvasGo);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 5001; // beside the rope-request HUD (5000)
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        // Top-left anchored panel: a titled card with a VStack of rows under it.
        _container = Ui.Node("Container", canvasGo.transform);
        _container.anchorMin = new Vector2(0f, 1f);
        _container.anchorMax = new Vector2(0f, 1f);
        _container.pivot     = new Vector2(0f, 1f);
        _container.anchoredPosition = new Vector2(24f, -24f);
        _container.sizeDelta = new Vector2(260f, 0f);
        Ui.Panel(_container, Ui.Bg);
        Ui.VStack(_container, 2f, new RectOffset(8, 8, 6, 8));
        _container.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var title = Ui.Label(_container, "Rope party", 15, HeaderText);
        Ui.Sized(title.rectTransform, h: 20f, flexW: true);

        _rowsHost = Ui.Node("Rows", _container);
        Ui.VStack(_rowsHost, 2f, new RectOffset(0, 0, 2, 0));
        Ui.Sized(_rowsHost, flexW: true);
        _rowsHost.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    private void AddRow(int id)
    {
        EnsureCanvas();
        var rowRt = Ui.Node($"Party_{id}", _rowsHost);
        Ui.Panel(rowRt, Ui.RowBg);
        Ui.Sized(rowRt, h: 24f, flexW: true);
        var h = Ui.HStack(rowRt, 6f, new RectOffset(6, 6, 0, 0));
        h.childForceExpandWidth = false;

        var name = Ui.Label(rowRt, "", 14, Ui.Text);
        Ui.Sized(name.rectTransform, flexW: true).flexibleWidth = 1f;

        var state = Ui.Label(rowRt, "", 12, StateIdle, TextAnchor.MiddleRight);
        Ui.Sized(state.rectTransform, w: 64f);

        _rows[id] = new Row { Go = rowRt.gameObject, Name = name, State = state };
    }

    private void RemoveRow(int id)
    {
        if (!_rows.TryGetValue(id, out var row))
            return;
        _rows.Remove(id);
        try { if (row.Go != null) UnityEngine.Object.Destroy(row.Go); } catch { }
    }

    // ── roster / name resolution (mirrors RopeRequestHud) ───────────────────────────────────────────────

    private Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> SafeRoster()
    {
        try { return _roster?.Invoke(); } catch { return null; }
    }

    private static NetplayRemotePlayer FindGhost(Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> roster, int id)
    {
        if (roster == null) return null;
        for (int r = 0; r < roster.Count; r++)
        {
            var g = roster[r];
            if (g != null && g.Id == id) return g;
        }
        return null;
    }

    private static string ResolveName(Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer> roster, int id)
    {
        var ghost = FindGhost(roster, id);
        try
        {
            var prov = ghost != null ? ghost.interactionProvider : null;
            string n = prov != null ? prov.netPlayerName : null;
            if (!string.IsNullOrEmpty(n)) return n;
        }
        catch { }
        return $"Player {id}";
    }
}
