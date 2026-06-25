using System;
using System.Collections.Generic;
using CairnAPI;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.UI;

namespace CairnCoop;

/// <summary>
/// The incoming-rope-request notification overlay: a real Unity uGUI (Canvas + UI.Text + layout groups)
/// list, anchored MIDDLE-LEFT, that mirrors <see cref="RopeHandshake"/>'s incoming set. When partner A
/// sends US a rope request, a persistent row appears here — "&lt;name&gt; wants to rope · &lt;secs&gt;s" —
/// counting down to the <see cref="RopeHandshake.RequestTimeoutSeconds"/> expiry. It is awareness-only: B
/// still ACCEPTS by the diegetic gesture (reaching at A's ghost, see <see cref="GhostRopeGesture"/>); there
/// is no clickable accept here. On timeout the entry vanishes (and A's outgoing request is symmetrically
/// cancelled by <see cref="RopeHandshake.ExpireStale"/>, so A must re-request).
///
/// Built ON the shared uGUI design-system layer (<see cref="CairnAPI.Ui"/>): legacy <see cref="Text"/> +
/// the builtin Arial font (TMP font-asset interop is avoided), <see cref="CairnAPI.Ui.Node"/>/<see
/// cref="CairnAPI.Ui.Panel"/>/<see cref="CairnAPI.Ui.VStack"/>/<see cref="CairnAPI.Ui.Label"/>, with
/// <see cref="ContentSizeFitter"/>. The only HUD-local builders are the explicit-band placement helpers
/// (<see cref="BandTop"/>/<see cref="BandBottom"/>) — a fixed two-line row whose lines are anchored, not
/// laid out, so neither can be squeezed to zero by an over-budget VStack (the "only the second line shows"
/// bug). Everything else composes from <see cref="CairnAPI.Ui"/>.
///
/// Lifecycle: the canvas is built LAZILY on the first frame with ≥1 incoming request and then kept. Rows
/// are rebuilt only when the SET of requester ids changes; the per-frame <see cref="Tick"/> just updates
/// each row's countdown text. All Il2Cpp UI calls are fail-open (a UI exception must never break the mod).
/// </summary>
internal sealed class RopeRequestHud
{
    private readonly RopeHandshake _handshake;

    /// <summary>How we read the live roster — id → display name resolution and the gamemode/manager walk
    /// both go through this, injected so the HUD owns no game-state lookup of its own.</summary>
    private readonly Func<Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer>> _roster;

    internal Action<string> Log;

    // Palette mirrored from CairnTools/Ui.cs (the live-verified design tokens).
    private static readonly Color RowBg   = new(0.10f, 0.10f, 0.12f, 0.88f);
    private static readonly Color Text    = new(0.90f, 0.91f, 0.94f, 1f);
    private static readonly Color TextDim = new(0.60f, 0.62f, 0.68f, 1f);
    private static readonly Color Accent  = new(1f, 1f, 1f, 1f); // bright white title (the dim blue was unreadable on the dark panel)

    private Canvas _canvas;
    private RectTransform _container; // the VStack the rows live under

    /// <summary>One built row, kept so the countdown text updates in place without a rebuild. Keyed by
    /// requester id in <see cref="_rows"/>.</summary>
    private sealed class Row
    {
        public GameObject Go;
        public UnityEngine.UI.Text Title; // "<name> wants to rope"
        public UnityEngine.UI.Text Count; // "<secs>s · reach to accept"
        public string Name;               // last resolved name (so a re-resolve only rewrites on change)
    }

    private readonly Dictionary<int, Row> _rows = new();
    // Scratch reused each Tick to diff the live id set against built rows without per-frame allocation churn.
    private readonly HashSet<int> _liveIds = new();

    internal RopeRequestHud(RopeHandshake handshake,
        Func<Il2CppSystem.Collections.Generic.List<NetplayRemotePlayer>> roster)
    {
        _handshake = handshake;
        _roster = roster;
    }

    /// <summary>Per-frame refresh, called from <see cref="Core.OnUpdate"/>. Adds/removes rows when the set of
    /// requester ids changes and updates each row's countdown text every frame. Fail-open.</summary>
    internal void Tick()
    {
        try
        {
            float now = Time.realtimeSinceStartup;
            var pending = _handshake.PendingIncoming(now);

            _liveIds.Clear();
            foreach (var (id, _, secondsLeft) in pending)
            {
                _liveIds.Add(id);
                EnsureRow(id);
                UpdateRow(id, secondsLeft);
            }

            // Drop rows whose request is gone (accepted / rejected / cancelled / expired).
            if (_rows.Count > 0)
            {
                List<int> stale = null;
                foreach (var kv in _rows)
                    if (!_liveIds.Contains(kv.Key))
                        (stale ??= new List<int>()).Add(kv.Key);
                if (stale != null)
                    foreach (int id in stale)
                        RemoveRow(id);
            }

            // Hide the whole overlay when empty; show it the moment there's a request.
            if (_canvas != null)
                _canvas.gameObject.SetActive(_rows.Count > 0);
        }
        catch (Exception e)
        {
            Log?.Invoke($"hud: Tick failed — {e.Message}");
        }
    }

    // ── canvas / row construction (the CairnTools/Ui.cs idiom, inline) ──────────────────────────────────

    private void EnsureCanvas()
    {
        if (_canvas != null)
            return;
        Ui.Init(); // resolves the shared builtin Arial font once (Ui.Label reads Ui.Font)

        var canvasGo = new GameObject("CairnCoop_RopeRequestHud");
        UnityEngine.Object.DontDestroyOnLoad(canvasGo);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 5000; // above the game's own HUD
        // The CanvasScaler MUST be configured (ScaleWithScreenSize + a reference resolution) — a bare
        // AddComponent leaves it in a state where the overlay canvas' rect is never driven into screen
        // space (live probe: every node, incl. the root, collapsed to world corners BL(0,0) TR(0,0), so
        // all text rendered as zero area at the origin = invisible). Mirror CairnTools/SpawnWindow.Build.
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>(); // harmless; the HUD has no interactables

        // Middle-left anchored container: anchor + pivot on the left-middle edge, nudged in from the screen
        // edge. Rows stack top-down via a VStack; a ContentSizeFitter grows it with row count. This is the
        // proven CSF+VStack combo from Ui.ScrollList's content node (anchored, VStack, CSF PreferredSize).
        _container = Ui.Node("Container", canvasGo.transform);
        _container.anchorMin = new Vector2(0f, 0.5f);
        _container.anchorMax = new Vector2(0f, 0.5f);
        _container.pivot     = new Vector2(0f, 0.5f);
        _container.anchoredPosition = new Vector2(24f, 0f);
        _container.sizeDelta = new Vector2(320f, 0f);

        var v = Ui.VStack(_container, 6f, new RectOffset(0, 0, 0, 0));
        v.childAlignment = TextAnchor.UpperLeft;
        var fit = _container.gameObject.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    // Row geometry. The two text lines are placed by EXPLICIT anchored rects inside the row (no inner layout
    // group), so neither line can ever be squeezed/collapsed by an over-budget VStack. The row's own height is
    // the sum of these bands and so always fits its content:  Pad + Title + Gap + Sub + Pad.
    private const float RowPad    = 8f;
    private const float TitleH    = 22f;
    private const float LineGap   = 2f;
    private const float SubH      = 16f;
    private const float RowHeight = RowPad + TitleH + LineGap + SubH + RowPad; // = 56

    private void EnsureRow(int id)
    {
        if (_rows.ContainsKey(id))
            return;
        EnsureCanvas();

        // The row is a plain panel sized by the OUTER container VStack (via its LayoutElement) — exactly how
        // CairnTools/SpawnWindow.BuildRow lets _listContent's VStack drive each row's height. But INSIDE the
        // row we do NOT add another layout group: a fixed two-line row has a known shape, so we place the two
        // labels with explicit anchored rects. This is the robust path — a nested VStack whose children's
        // preferred heights exceed the row height squeezes the FIRST child to zero, and a Text with
        // VerticalWrapMode.Truncate then renders blank (the exact "only the second line shows" bug). Explicit
        // bands can't be squeezed and don't depend on a layout pass landing the frame the row is created.
        var rowRt = Ui.Node($"Request_{id}", _container);
        Ui.Panel(rowRt, RowBg);
        Ui.Sized(rowRt, h: RowHeight, flexW: true);

        var name = ResolveName(id);

        // Title band: pinned to the TOP of the row, inset by the padding. Anchoring to the top edge with a
        // fixed pixel height (anchored top-down) means the title's rect is identical regardless of the row's
        // realized width/height — no collapse, no order dependence.
        var title = Ui.Label(rowRt, $"{name} wants to rope", 17, Accent);
        BandTop(title.rectTransform, top: RowPad, height: TitleH, padX: RowPad);

        // Subtitle band: pinned to the BOTTOM of the row, inset by the padding.
        var count = Ui.Label(rowRt, "", 14, TextDim);
        BandBottom(count.rectTransform, bottom: RowPad, height: SubH, padX: RowPad);

        _rows[id] = new Row { Go = rowRt.gameObject, Title = title, Count = count, Name = name };

        // One forced rebuild so the OUTER container VStack+CSF realize the new row's rect immediately (the row
        // children are anchored, not laid out, so this only resolves the container chain — the CairnTools idiom).
        LayoutRebuilder.ForceRebuildLayoutImmediate(_container);
        Log?.Invoke($"hud: showing rope request from #{id} ({name})");
    }

    private void UpdateRow(int id, float secondsLeft)
    {
        if (!_rows.TryGetValue(id, out Row row))
            return;
        // Set BOTH labels' text here every frame (not just count). The title was previously written once at
        // build time, before the first layout pass — its text mesh didn't regenerate and rendered blank while
        // the count (written here each frame) showed fine. Re-resolve the name (it can arrive a beat after the
        // request) and re-apply both each tick; cheap and always-correct.
        string name = ResolveName(id);
        row.Name = name;
        row.Title.text = $"{name} wants to rope up";
        int secs = Mathf.Max(0, Mathf.CeilToInt(secondsLeft));
        row.Count.text = $"{secs}s · reach to accept";
    }

    private void RemoveRow(int id)
    {
        if (!_rows.TryGetValue(id, out Row row))
            return;
        _rows.Remove(id);
        try { if (row.Go != null) UnityEngine.Object.Destroy(row.Go); }
        catch { /* fail-open: a destroy race must not break the mod */ }
    }

    /// <summary>Resolve a requester id to a display name from the live roster: match the ghost by
    /// <c>NetplayRemotePlayer.Id</c> and read its interaction provider's <c>netPlayerName</c> (the same name
    /// source the diegetic prompt uses). Falls back to "Player {id}" when the roster/name is unavailable.</summary>
    private string ResolveName(int id)
    {
        try
        {
            var remotes = _roster?.Invoke();
            if (remotes != null)
                for (int r = 0; r < remotes.Count; r++)
                {
                    var ghost = remotes[r];
                    if (ghost == null || ghost.Id != id)
                        continue;
                    var prov = ghost.interactionProvider;
                    string n = prov != null ? prov.netPlayerName : null;
                    if (!string.IsNullOrEmpty(n))
                        return n;
                    break;
                }
        }
        catch { /* roster momentarily null/torn — fall through to the id label */ }
        return $"Player {id}";
    }

    // ── HUD-local placement helpers (NOT in the shared Ui kit) ──────────────────────────────────────────
    // A fixed-shape two-line row places its lines as explicit ANCHORED BANDS (top-pinned title, bottom-pinned
    // subtitle) rather than via a nested layout group: an over-budget VStack squeezes its FIRST child to zero,
    // and a Truncate-overflow Text then renders blank (the "only the second line shows" bug). Bands can't be
    // squeezed and don't depend on a layout pass landing the frame the row is created. The shared Ui.* builds
    // everything else (Node/Panel/VStack/Sized/Label); only this bespoke band placement stays here.

    // Anchor a child as a horizontal band pinned to the TOP of its parent: stretched full width (inset by
    // padX), `height` px tall, `top` px down from the parent's top edge.
    private static void BandTop(RectTransform rt, float top, float height, float padX)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot     = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(padX, -(top + height)); // left inset; bottom edge = top + height below the top
        rt.offsetMax = new Vector2(-padX, -top);           // right inset; top edge = top below the parent top
    }

    // Anchor a child as a horizontal band pinned to the BOTTOM of its parent.
    private static void BandBottom(RectTransform rt, float bottom, float height, float padX)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot     = new Vector2(0f, 0f);
        rt.offsetMin = new Vector2(padX, bottom);          // left inset; bottom edge = bottom above the parent bottom
        rt.offsetMax = new Vector2(-padX, bottom + height);// right inset; top edge = bottom + height above the bottom
    }
}
