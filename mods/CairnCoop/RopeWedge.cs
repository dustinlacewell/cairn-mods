using System;

namespace CairnCoop;

/// <summary>
/// Registers the co-op UNROPE shortcut on CairnAPI's CrossMenu LT+RT radial wheel: hold LT+RT and flick UP to
/// detach from the WHOLE chain (disconnect every rope — carry above and every dependent below). Connection is
/// formed ONLY diegetically (reach a partner's ghost → request/accept); the wedge is the inverse — the
/// non-diegetic way to get OUT of the rope. It shows greyed-out (visible but unavailable) whenever this climber
/// isn't roped to anyone. Registration is safe before the HUD exists; CairnAPI drives it once the wheel is
/// found.
///
/// CairnAPI CrossMenu glue, kept out of the MelonMod root so the orchestration layer is wiring-only.
/// </summary>
internal static class RopeWedge
{
    public static void Register(GameDriver driver, Action<string> log)
    {
        try
        {
            CairnAPI.CrossMenu.DefineMenu("cairncoop.rope", CairnAPI.CrossMenuModifier.RightTrigger);
            CairnAPI.CrossMenu.Register(new CairnAPI.CrossMenuAction
            {
                Id = "cairncoop.rope.unrope",
                Label = "Unrope",
                Menu = "cairncoop.rope",
                Direction = CairnAPI.CrossMenuDir.Up,
                IconName = "anchor",          // the belay anchor; lib auto-dims it when unavailable
                // greyed-out (visible but disabled) whenever this climber isn't roped to anyone
                IsAvailable = () => driver != null && driver.RopeConnected,
                OnExecute = () => driver?.UnropeAll(),
            });
            log?.Invoke("rope: registered LT+RT up-wedge (unrope from the whole chain)");
        }
        catch (Exception e)
        {
            log?.Invoke("rope: CrossMenu registration failed (is CairnAPI installed?): " + e.Message);
        }
    }
}
