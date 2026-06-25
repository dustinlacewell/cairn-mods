using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnDevTools;

/// <summary>
/// Generic live-scene inspection commands served by the debug console. Everything here
/// is read-only and game-agnostic apart from the LogicalRope census (Cairn's rope class).
/// </summary>
internal static class Inspection
{
    /// <summary>
    /// THE belay-state dump. One command, the COMPLETE connection graph for the local
    /// climber's securing rope — so diagnosis never depends on remembering which slice to
    /// probe. Reports: climber + harness attach position, the securing rope identity/length/
    /// bounds/attachEnd, EVERY holder with its live attach-collider world position, EVERY rope
    /// part with both endpoint world positions + what each side is attached to (climber/robot/
    /// quickdraw), the rendered LineRenderer first/last point (the visual truth), the climbot
    /// state, and the piton/quickdraw. The whole picture, every time.
    /// </summary>
    internal static string Belay()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var climber = UnityEngine.Object.FindObjectOfType<ClimbingV2PawnController>();
            if (climber == null)
                return "belay: no local ClimbingV2PawnController (not in level?)";
            var harness = climber.harness;
            Vector3 pawnPos = harness != null ? harness.transform.position : default;
            sb.AppendLine($"CLIMBER pawn(harness)={pawnPos:F2} inFreeSolo={Safe(() => climber.InFreeSolo().ToString())}");
            if (harness != null)
                sb.AppendLine($"  harness.attachPos={Safe(() => harness.GetAttachPosition().ToString("F2"))}"
                    + $" attachEndCollider={ColliderPos(Safe2(() => harness.GetAttachEnd()))}"
                    + $" IsSecured(1)={Safe(() => harness.IsSecured(1).ToString())}");

            var lifeline = harness != null ? harness.PersonalLifeline : null;
            var rope = lifeline != null ? lifeline.securingRope : null;
            if (rope == null)
            {
                sb.AppendLine("SECURING ROPE: NULL");
            }
            else
            {
                rope.GetWorldBounds(out var rb);
                sb.AppendLine($"SECURING ROPE '{rope.gameObject.name}' @{HierarchyPath(rope.transform)}");
                sb.AppendLine($"  active={rope.gameObject.activeInHierarchy} vis={rope.Visible} init={rope.IsInitialized}"
                    + $" len={rope.GetLength():F2} maxLen={rope.MaxLengthMeters:F1} dangling={Safe(() => rope.IsDangling().ToString())}");
                sb.AppendLine($"  attachBegin={(rope.attachBegin != null ? "SET" : "NULL")}"
                    + $" attachEnd={(rope.attachEnd != null ? "SET" : "NULL")}"
                    + $"  worldBounds size={rb.size.magnitude:F1} center={rb.center:F1}");
                AppendFallDistance(sb, lifeline, rope, harness);
                AppendHolders(sb, rope);
                AppendParts(sb, rope);
                AppendRopeLine(sb, rope);
            }

            AppendStockRope(sb, harness);
            AppendClimbot(sb, harness);
            AppendPitons(sb, lifeline);
        }
        catch (Exception e)
        {
            sb.AppendLine("belay: dump error: " + e);
        }
        return sb.ToString();
    }

    /// <summary>THE causal fall-distance block. SecuredFalling.FixedUpdate stops the fall when
    /// `Lifeline.GetRemainingSecuringRopeDistanceOnLifeline()` reaches 0; that scalar is
    /// ‖climber − GetLastSecuringPiton.pos‖, and the swing pivot is `GetLastHoldingPiton`. The
    /// climbot is type-filtered out of both. So fall distance == climber↔(last holding quickdraw
    /// piton). This prints exactly those values + EVERY quickdraw holder so an asymmetry (wrong/extra
    /// anchor piton, securingRope identity) is visible in one call. See
    /// re/systems/climber/fall-distance.md.
    private static void AppendFallDistance(System.Text.StringBuilder sb, Lifeline lifeline,
        LogicalRope rope, Harness harness)
    {
        sb.AppendLine("  FALL-DISTANCE (causal):");
        Vector3 climberPos = lifeline != null ? lifeline.transform.position : default;
        sb.AppendLine($"    climber(lifeline.transform)={climberPos:F2}");

        // The native GetRemainingSecuringRopeDistanceOnLifeline isn't exposed as a proxy method, but it
        // is exactly ‖climber − lastSecuringPiton‖ (see CAUSAL doc) — compute it from public members.
        try
        {
            var lsp = lifeline.GetLastSecuringPiton();
            if (lsp != null)
            {
                var mb = lsp.TryCast<MonoBehaviour>();
                Vector3 pp = mb != null ? mb.transform.position : default;
                sb.AppendLine($"    lastSecuringPiton @{pp:F2}  ==> climber→piton={(climberPos - pp).magnitude:F2}m  (THIS is the fall-distance scalar)");
            }
            else sb.AppendLine("    GetLastSecuringPiton=NULL");
        }
        catch (Exception e) { sb.AppendLine("    GetLastSecuringPiton ERR: " + e.Message); }

        // The swing pivot the fall hangs from (CheckNextPiton uses GetLastHoldingPiton).
        try
        {
            var lhp = rope.GetLastHoldingPiton();
            if (lhp == null)
                sb.AppendLine("    GetLastHoldingPiton=NULL (nothing to catch on!)");
            else
            {
                var mb = lhp.TryCast<MonoBehaviour>();
                Vector3 pp = mb != null ? mb.transform.position : default;
                sb.AppendLine($"    GetLastHoldingPiton(swing pivot) @{pp:F2}"
                    + $"  ==> climber→pivot={(climberPos - pp).magnitude:F2}m");
            }
        }
        catch (Exception e) { sb.AppendLine("    GetLastHoldingPiton ERR: " + e.Message); }

        // Every quickdraw holder + which would be chosen — reveals stray/extra anchors.
        var holders = rope.ropeHolders;
        int qd = 0;
        if (holders != null)
            for (int i = 0; i < holders.Count; i++)
            {
                var q = holders[i] != null ? holders[i].TryCast<LogicalQuickdraw>() : null;
                if (q == null) continue;
                qd++;
                var mb = q.TryCast<MonoBehaviour>();
                Vector3 qp = mb != null ? mb.transform.position : default;
                sb.AppendLine($"    quickdraw[{i}] @{qp:F2} climber→{(climberPos - qp).magnitude:F2}m");
            }
        sb.AppendLine($"    quickdrawHolderCount={qd} (IsSecured needs ≥1; >1 means MULTIPLE anchors — the LAST holding one wins)");
    }

    private static void AppendHolders(System.Text.StringBuilder sb, LogicalRope rope)
    {
        var holders = rope.ropeHolders;
        sb.AppendLine($"  HOLDERS ({(holders != null ? holders.Count.ToString() : "null")}):");
        if (holders == null) return;
        for (int i = 0; i < holders.Count; i++)
        {
            var h = holders[i];
            if (h == null) { sb.AppendLine($"    [{i}] null"); continue; }
            var obj = h.TryCast<Il2CppSystem.Object>();
            var mb = h.TryCast<MonoBehaviour>();
            string type = obj != null ? obj.GetIl2CppType().Name : "?";
            string mbPos = mb != null ? mb.transform.position.ToString("F2") : "?";
            string attachEnd = ColliderPos(Safe2(() => h.GetAttachEnd()));
            sb.AppendLine($"    [{i}] {type} transform={mbPos} attachEnd={attachEnd}");
        }
    }

    private static void AppendParts(System.Text.StringBuilder sb, LogicalRope rope)
    {
        var parts = rope.ropeParts;
        sb.AppendLine($"  PARTS ({(parts != null ? parts.Count.ToString() : "null")}):");
        if (parts == null) return;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (p == null) { sb.AppendLine($"    part{i} null"); continue; }
            sb.AppendLine($"    part{i} '{p.gameObject.name}' vis={p.Visible} active={p.gameObject.activeInHierarchy}");
            sb.AppendLine($"      Begin {SideDetail(p, RopeSide.Begin)}");
            sb.AppendLine($"      End   {SideDetail(p, RopeSide.End)}");
        }
    }

    /// <summary>The RAW pin state of one rope-part end: the tag (climber/robot/quickdraw/FREE),
    /// the actual pinned ObiCollider's name + owning-component type + world position (the pin
    /// constraint at LogicalBase.pinConstraintHandler 0x30), and the particle world position.
    /// This is the ground truth — no follow-up query needed to know what an end is bound to.</summary>
    private static string SideDetail(LogicalRopePart p, RopeSide side)
    {
        var tags = new List<string>();
        if (Safe(() => p.IsAttachedToClimber(side).ToString()) == "True") tags.Add("climber");
        if (Safe(() => p.IsAttachedToRobot(side).ToString()) == "True") tags.Add("robot");
        if (Safe(() => p.IsAttachedToQuickDraw(side).ToString()) == "True") tags.Add("quickdraw");
        string tag = tags.Count == 0 ? "FREE" : string.Join("+", tags);

        string pin = "pin=NONE";
        try
        {
            var handler = p.pinConstraintHandler;
            var collider = handler != null ? handler.GetAttachCollider(side) : null;
            if (collider != null)
            {
                string owner = "?";
                try { owner = collider.transform.root.name + "/" + collider.gameObject.name; } catch (Exception) { }
                pin = $"pin='{owner}'@{collider.transform.position:F2}";
            }
        }
        catch (Exception e) { pin = "pin=ERR:" + e.Message; }

        string particle = Safe(() =>
        {
            var v = p.GetPositionOnRopeAtDistance(side, 0f);
            return v.HasValue ? v.Value.ToString("F1") : "none";
        });
        return $"[{tag}] {pin} particle={particle}";
    }

    private static void AppendRopeLine(System.Text.StringBuilder sb, LogicalRope rope)
    {
        try
        {
            foreach (var lr in rope.GetComponentsInChildren<LineRenderer>(true))
            {
                int n = lr.positionCount;
                if (n < 2) continue;
                bool drawing = lr.enabled && lr.gameObject.activeInHierarchy;
                sb.AppendLine($"  LINE '{lr.gameObject.name}' drawing={drawing} pts={n}"
                    + $" first={lr.GetPosition(0):F2} last={lr.GetPosition(n - 1):F2}");
            }
        }
        catch (Exception e) { sb.AppendLine("  LINE probe failed: " + e.Message); }
    }

    private static void AppendStockRope(System.Text.StringBuilder sb, Harness harness)
    {
        // The bn_Rope / LogicalRopeBetweenStockAndArm — the SECOND rope on the climbot. Easy to
        // forget; report it so a "broken visual" is never misattributed.
        try
        {
            foreach (var rope in UnityEngine.Object.FindObjectsOfType<LogicalRope>(true))
            {
                var path = HierarchyPath(rope.transform);
                if (!path.Contains("Climbot") || !path.Contains("StockAndArm")) continue;
                rope.GetWorldBounds(out var b);
                sb.AppendLine($"STOCK ROPE '{rope.gameObject.name}' active={rope.gameObject.activeInHierarchy}"
                    + $" vis={rope.Visible} len={rope.GetLength():F2} holders={rope.GetRopeHolderGlobalCount()}"
                    + $" bounds={b.size.magnitude:F1}@{b.center:F0}");
            }
        }
        catch (Exception) { }
    }

    private static void AppendClimbot(System.Text.StringBuilder sb, Harness harness)
    {
        try
        {
            foreach (var bo in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppTheGameBakers.Cairn.RobotPawnController>(), true))
            {
                var b = bo.Cast<Il2CppTheGameBakers.Cairn.RobotPawnController>();
                if (!b.transform.root.name.StartsWith("Climbot")) continue;
                sb.AppendLine($"CLIMBOT '{b.transform.root.name}' state={Safe(() => b.CurrentState.ToString())}"
                    + $" attached={Safe(() => b.IsAttached.ToString())} active={b.gameObject.activeInHierarchy}"
                    + $" pos={b.transform.position:F2}");
            }
        }
        catch (Exception e) { sb.AppendLine("CLIMBOT probe failed: " + e.Message); }
    }

    private static void AppendPitons(System.Text.StringBuilder sb, Lifeline lifeline)
    {
        try
        {
            sb.AppendLine($"PLACED PITONS: {(lifeline != null && lifeline.PlacedPitons != null ? lifeline.PlacedPitons.Count.ToString() : "?")}");
            foreach (var po in UnityEngine.Object.FindObjectsOfType<Piton>(true))
            {
                if (!po.gameObject.name.Contains("ByPlayer")) continue;
                sb.AppendLine($"  piton '{po.gameObject.name}' pos={po.transform.position:F2} active={po.gameObject.activeInHierarchy}");
            }
        }
        catch (Exception e) { sb.AppendLine("PITON probe failed: " + e.Message); }
    }

    private static string ColliderPos(Component c)
        => c != null ? c.transform.position.ToString("F2") : "NULL";

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "null"; } catch (Exception e) { return "ERR:" + e.Message; }
    }

    private static T Safe2<T>(Func<T> f) where T : class
    {
        try { return f(); } catch (Exception) { return null; }
    }

    internal static string HierarchyPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null && path.Length < 200)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }

    internal static string Ropes()
    {
        var lines = new List<string>();
        foreach (var rope in UnityEngine.Object.FindObjectsOfType<LogicalRope>(includeInactive: true))
        {
            string entry;
            try
            {
                string bounds = rope.GetWorldBounds(out var b) ? $"{b.size.magnitude:0}m@{b.center:F0}" : "?";
                entry = $"{HierarchyPath(rope.transform)}"
                    + $" active:{rope.gameObject.activeInHierarchy} init:{rope.IsInitialized}"
                    + $" len:{rope.GetLength():0.#} vis:{rope.Visible} holders:{rope.GetRopeHolderGlobalCount()}"
                    + $" bounds:{bounds}";
            }
            catch (Exception e) { entry = $"{rope.gameObject.name} probe failed: {e.Message}"; }
            lines.Add(entry);
        }
        return lines.Count == 0 ? "(no LogicalRopes)" : string.Join("\n", lines);
    }

    internal static string Renderers(float minSpan)
    {
        var lines = new List<string>();
        foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            try
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                float span = renderer.bounds.size.magnitude;
                if (span < minSpan)
                    continue;
                lines.Add($"{HierarchyPath(renderer.transform)} [{renderer.GetIl2CppType().Name}]"
                    + $" span:{span:0}m center:{renderer.bounds.center:F0}");
                if (lines.Count >= 25)
                    break;
            }
            catch (Exception) { }
        }
        return lines.Count == 0 ? $"(no renderers with bounds over {minSpan}m)" : string.Join("\n", lines);
    }

    internal static string Find(string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return "usage: find <name-substring>";
        var lines = new List<string>();
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(includeInactive: true))
        {
            try
            {
                if (t.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                lines.Add($"{HierarchyPath(t)} active:{t.gameObject.activeInHierarchy} pos:{t.position:F1}");
                if (lines.Count >= 30)
                    break;
            }
            catch (Exception) { }
        }
        return lines.Count == 0 ? "(no matches)" : string.Join("\n", lines);
    }

    internal static string Inspect(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
            return "usage: inspect <name-or-hierarchy-path>";
        Transform target = null;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(includeInactive: true))
        {
            string path = HierarchyPath(t);
            if (path.Equals(nameOrPath, StringComparison.OrdinalIgnoreCase)
                || t.name.Equals(nameOrPath, StringComparison.OrdinalIgnoreCase))
            {
                target = t;
                break;
            }
        }
        if (target == null)
            return "(not found — try `find` first)";

        var lines = new List<string>
        {
            $"path: {HierarchyPath(target)}",
            $"activeSelf:{target.gameObject.activeSelf} activeInHierarchy:{target.gameObject.activeInHierarchy}",
            $"pos:{target.position:F2} localPos:{target.localPosition:F2} rot:{target.rotation.eulerAngles:F0} scale:{target.localScale:F2}",
        };
        foreach (var component in target.GetComponents<Component>())
        {
            try
            {
                string extra = "";
                var rope = component.TryCast<LogicalRope>();
                if (rope != null)
                    extra = $" len:{rope.GetLength():0.#} vis:{rope.Visible} holders:{rope.GetRopeHolderGlobalCount()} init:{rope.IsInitialized}";
                var renderer = component.TryCast<Renderer>();
                if (renderer != null)
                    extra = $" enabled:{renderer.enabled} bounds:{renderer.bounds.size.magnitude:0}m@{renderer.bounds.center:F0}";
                lines.Add($"  [{component.GetIl2CppType().Name}]{extra}");
            }
            catch (Exception e) { lines.Add($"  [component probe failed: {e.Message}]"); }
        }
        lines.Add($"children ({target.childCount}):");
        for (int i = 0; i < target.childCount && i < 20; i++)
        {
            var child = target.GetChild(i);
            lines.Add($"  {child.name} active:{child.gameObject.activeSelf}");
        }
        return string.Join("\n", lines);
    }
}
