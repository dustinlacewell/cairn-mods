using System.Collections.Generic;

namespace CairnCoop;

/// <summary>
/// Formats the belay reconciler's last-tick <see cref="PartnerBelay.BelaySnapshot"/> for the F4
/// "Belay" tab: OBSERVED facts → DERIVED desired state → DRIVEN actuals, laid out so a two-instance
/// test is legible at a glance (read both clients' tabs side by side to see exactly where they
/// diverge). Pure projection of the snapshot struct — reads nothing live.
/// </summary>
internal static class BelayReadout
{
    public static List<string> Format(PartnerBelay.BelaySnapshot s)
    {
        string yn(bool b) => b ? "YES" : "no";
        return new List<string>
        {
            $"rope: {(s.Enabled ? "CONNECTED" : "disconnected")}   my module: {s.MyModule ?? "—"}",
            $"last action: {s.LastAction ?? "—"}",
            "",
            "— OBSERVED —",
            $"  partner found: {yn(s.PartnerFound)}" + (s.PartnerFound ? $"  (#{s.PartnerId})" : ""),
            $"  partner wall-supported (net): {yn(s.PartnerWallSupported)}",
            $"  partner dead (net):           {yn(s.PartnerDeadNet)}",
            $"  partner hanging on me (announced): {yn(s.PartnerHanging)}",
            $"  I weight my own rope:         {yn(s.IWeightRope)}",
            "",
            "— DERIVED —",
            $"  partner is valid belayer: {yn(s.PartnerValidBelayer)}",
            $"  anchor desired: {yn(s.AnchorDesired)}    drain desired: {yn(s.DrainDesired)}",
            "",
            "— DRIVEN —",
            $"  anchor present: {yn(s.AnchorPresent)}",
            $"  stamina drain/sec: {s.DrainPerSecond:0.###}",
        };
    }
}
