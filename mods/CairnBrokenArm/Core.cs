using MelonLoader;
using Il2Cpp;
using LimbState = Il2Cpp.ClimbingV2PawnLimb.LimbState;
using DropCause = Il2Cpp.ClimbingV2PawnLimb.DropCause;

[assembly: MelonInfo(typeof(CairnBrokenArm.Core), "CairnBrokenArm", "1.0.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnBrokenArm;

// CairnBrokenArm — robustly disables one of Aava's arms.
//
// Design + per-requirement chokepoints:
//   cairn-re/re/systems/climber/limb-control/BROKEN-ARM-VERDICT.md
//
// The broken arm is excluded from selection (Layer A), vetoed at attach + state-clamped
// (Layer B), and held Idle by a per-frame safety net (Layer C). R5 (effort) needs no patch:
// a limb that never reaches Holding(2) reads isHolding==false → zero effort, zero drain.
public class Core : MelonMod
{
    internal static MelonPreferences_Entry<bool> Enabled;
    internal static MelonPreferences_Entry<string> DisabledHand;
    internal static MelonPreferences_Entry<string> DisabledFoot;

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("CairnBrokenArm");
        Enabled = cat.CreateEntry("Enabled", true,
            description: "Master switch for the broken-arm effect.");
        DisabledHand = cat.CreateEntry("DisabledHand", "Right",
            description: "Which hand to disable: None, Left, or Right.");
        DisabledFoot = cat.CreateEntry("DisabledFoot", "None",
            description: "Which foot to disable: None, Left, or Right.");

        HarmonyInstance.PatchAll(typeof(Core).Assembly);

        try { RegisterModOptions(); }
        catch (System.Exception e) { LoggerInstance.Warning("CairnModOptions not available; in-game options skipped: " + e.Message); }

        LoggerInstance.Msg("CairnBrokenArm loaded.");
    }

    private static readonly string[] SideChoices = { "None", "Left", "Right" };

    // stored string -> dropdown index. Mirrors BrokenArm.Parse: case-insensitive, default None(0).
    private static int SideToIndex(string v)
    {
        switch (v?.Trim().ToLowerInvariant())
        {
            case "left":  return 1;
            case "right": return 2;
            default:      return 0;
        }
    }

    private void RegisterModOptions()
    {
        CairnModOptions.ModOptions.Register("Broken Arm", new[]
        {
            CairnModOptions.ModOption.Toggle("Enable broken limb", Enabled,
                tooltip: "Turn the broken-limb effect on or off."),
            CairnModOptions.ModOption.Dropdown("Disabled hand", SideChoices,
                get: () => SideToIndex(DisabledHand.Value),
                set: i => { DisabledHand.Value = SideChoices[i]; DisabledHand.Category.SaveToFile(false); },
                tooltip: "Which hand cannot grip."),
            CairnModOptions.ModOption.Dropdown("Disabled foot", SideChoices,
                get: () => SideToIndex(DisabledFoot.Value),
                set: i => { DisabledFoot.Value = SideChoices[i]; DisabledFoot.Category.SaveToFile(false); },
                tooltip: "Which foot cannot grip."),
        });
    }

    // Layer C — per-frame Idle safety net. Reverts anything that slipped past Layer A/B
    // (e.g. an inline writer whose patch failed to bind). Cheap: bails immediately if
    // disabled or the local broken arm can't be resolved; only acts when the arm is in a
    // forbidden state, which is rare in normal play.
    public override void OnUpdate()
    {
        if (Enabled == null || !Enabled.Value)
            return;

        var limbs = BrokenArm.LocalBrokenLimbs();
        for (int i = 0; i < limbs.Count; i++)
        {
            var limb = limbs[i];
            if (limb == null)
                continue;
            var s = limb.CurrentState;
            if (s == LimbState.Reaching || s == LimbState.Holding || s == LimbState.GoingToHold
                || s == LimbState.GoingToHoldIceClimbing || s == LimbState.RestoreIceClimbing)
            {
                // A clean let-go: clears currentGrab and routes to Idle through the game's own path.
                limb.Drop(DropCause.UserInput, UnityEngine.Vector3.zero, false);
            }
        }
    }
}
