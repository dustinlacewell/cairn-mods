using Il2Cpp;

namespace CairnAPI;

// Live state of the local pawn, routed through GameDataManager (MoSingleton<GameDataManager>).
//
// GameDataManager is the authoritative per-session singleton that owns the active difficulty
// packages (ClimbingPackage, SurvivalPackage) and the live physiological stat ratios. Any
// CairnAPI module that needs to read or tune these routes through here rather than reaching
// into GameDataManager directly.
//
// All properties return sensible defaults (null / false / 0f) when the singleton isn't ready.
public static class Pawn
{
    private static GameDataManager Mgr => GameDataManager.Instance;

    // True when GameDataManager is loaded and the singleton is available.
    public static bool IsReady => GameDataManager.IsReady;

    // --- Tweakables packages ---

    // The active climbing difficulty package for the local pawn. Contains stamina knobs,
    // hold-grip tuning, stability solver parameters, and character settings.
    // Null when not in a session.
    public static ClimbingTweakablesPackage ClimbingPackage
    {
        get { try { return Mgr?.ClimbingPackage; } catch { return null; } }
    }

    // The active survival difficulty package for the local pawn. Contains HP, hunger,
    // thirst, cold, injuries, boosts, and inventory tweakables sub-objects.
    // Null when not in a session.
    public static SurvivalTweakablesPackage SurvivalPackage
    {
        get { try { return Mgr?.SurvivalPackage; } catch { return null; } }
    }

    // --- HP ---

    public static float CurrentHp       { get { try { return Mgr?.CurrentHp ?? 0f; } catch { return 0f; } } }
    public static float CurrentMaxHp    { get { try { return Mgr?.CurrentMaxHp ?? 0f; } catch { return 0f; } } }
    public static float AbsoluteMaxHp   { get { try { return Mgr?.AbsoluteMaxHp ?? 0f; } catch { return 0f; } } }
    public static float HpRatio         { get { try { return Mgr?.HpRatio ?? 0f; } catch { return 0f; } } }
    public static bool  HPWarning       { get { try { return Mgr?.HPWarning ?? false; } catch { return false; } } }
    public static bool  HPCritical      { get { try { return Mgr?.HPCritical ?? false; } catch { return false; } } }

    // --- Hunger ---

    public static float HungerRatio     { get { try { return Mgr?.HungerRatio ?? 0f; } catch { return 0f; } } }
    public static bool  HungerWarning   { get { try { return Mgr?.HungerWarning ?? false; } catch { return false; } } }
    public static bool  HungerCritical  { get { try { return Mgr?.HungerCritical ?? false; } catch { return false; } } }

    // --- Thirst ---

    public static float ThirstRatio     { get { try { return Mgr?.ThirstRatio ?? 0f; } catch { return 0f; } } }
    public static bool  ThirstWarning   { get { try { return Mgr?.ThirstWarning ?? false; } catch { return false; } } }
    public static bool  ThirstCritical  { get { try { return Mgr?.ThirstCritical ?? false; } catch { return false; } } }

    // --- Cold ---

    public static float ColdRatio       { get { try { return Mgr?.ColdRatio ?? 0f; } catch { return 0f; } } }
    public static bool  ColdWarning     { get { try { return Mgr?.ColdWarning ?? false; } catch { return false; } } }
    public static bool  ColdCritical    { get { try { return Mgr?.ColdCritical ?? false; } catch { return false; } } }

    // --- Boosts ---

    public static float CurrentStaminaBoost   { get { try { return Mgr?.CurrentStaminaBoost ?? 0f; } catch { return 0f; } } }
    public static float CurrentStrengthBoost  { get { try { return Mgr?.CurrentStrengthBoost ?? 0f; } catch { return 0f; } } }
    public static float CurrentRestSpeedBoost { get { try { return Mgr?.CurrentRestSpeedBoost ?? 0f; } catch { return 0f; } } }
    public static float CurrentGripBoost      { get { try { return Mgr?.CurrentGripBoost ?? 0f; } catch { return 0f; } } }
    public static bool  HasGripBoost          { get { try { return Mgr?.HasGripBoost ?? false; } catch { return false; } } }
    public static bool  HasSuperGripBoost     { get { try { return Mgr?.HasSuperGripBoost ?? false; } catch { return false; } } }

    // --- Inventory ---

    // Which bag tier the pawn is carrying (None / Small / Medium / Large).
    public static InventoryTweakables.BagType BagType
    {
        get { try { return Mgr?.BagType ?? InventoryTweakables.BagType.Invalid; } catch { return InventoryTweakables.BagType.Invalid; } }
    }

    // --- Game state flags ---

    public static bool ArePitonsAllowed  { get { try { return Mgr?.ArePitonsAllowed ?? false; } catch { return false; } } }
    public static bool InResurrection    { get { try { return Mgr?.InResurrection ?? false; } catch { return false; } } }
    public static bool CanPhysiologicalStatsBeAffected { get { try { return Mgr?.CanPhysiologicalStatsBeAffected ?? false; } catch { return false; } } }
    public static bool HasShieldedHp     { get { try { return Mgr?.HasShieldedHp ?? false; } catch { return false; } } }
    public static bool HasShieldedStatuses { get { try { return Mgr?.HasShieldedStatuses ?? false; } catch { return false; } } }
    public static bool IsApplyingDamage  { get { try { return Mgr?.IsApplyingDamage ?? false; } catch { return false; } } }

    public static LootDifficulty LootDifficulty
    {
        get { try { return Mgr?.LootDifficulty ?? LootDifficulty.LootNormal; } catch { return LootDifficulty.LootNormal; } }
    }
}
