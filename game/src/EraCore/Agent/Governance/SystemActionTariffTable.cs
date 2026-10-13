using EraCore.Contracts.Actions;

namespace EraCore.Agent.Governance;

/// <summary>
/// Which of the five closed action groups a system verb belongs to
/// (K-3, v3.2 §11.3). The group is the pricing/permission family of the
/// verb; §11.4 prices by family (建造 2 / 战斗 1 / 物流 1 / 巡逻 1 / 侦察 0)
/// and the Boss-wave surge applies to the combat family only.
/// </summary>
public enum ActionGroup
{
    /// <summary>Recon family: 0 pt, allow tier (LookAround/InspectTile/QueryWorld).</summary>
    Recon,

    /// <summary>Construction family: 2 pt, ask tier (BuildPlan/UpgradeTower/SellTower/SetRally).</summary>
    Construction,

    /// <summary>Logistics family: 1 pt, ask tier (RerouteLogistics/SetStockpile).</summary>
    Logistics,

    /// <summary>Combat family: 1 pt, ask tier, doubled on Boss waves (FocusFire/SetTowerMode/TriggerScroll/HeroOrder).</summary>
    Combat,

    /// <summary>Management family: allow tier; per-verb cost from the registry metadata (0 pt except SpawnScout patrol 1 pt).</summary>
    Management,
}

/// <summary>
/// Default permission tier of a verb (v3.2 §11.4 / appendix F.4):
/// Allow runs without asking; Ask shows the plan card. Deny is not a
/// per-verb tier — it comes from the deny catalog (state-mutation class
/// requests) or from a missing danger bit, never from this table.
/// </summary>
public enum PermissionTier
{
    /// <summary>Free-running verbs: recon/query/planning/report/alarm/stop/patrol.</summary>
    Allow,

    /// <summary>Plan-card verbs: construction/logistics/combat — may be whitelisted
    /// "本关卡内自动允许" (level-scoped auto-approve) except where a danger bit
    /// forbids it (SellTower, F.4).</summary>
    Ask,
}

/// <summary>
/// One row of the frozen tariff table: pricing + permission + unlock
/// metadata for exactly one of the 19 closed verbs (K-3). Pure data —
/// produced only by <see cref="SystemActionTariffTable"/>, consumed by
/// <see cref="SystemPointLedger"/> (pricing) and <see cref="ApprovalGate"/>
/// (permission/unlock). Immutable value type with structural equality.
/// </summary>
/// <param name="Verb">The closed K-3 verb this row prices.</param>
/// <param name="Group">Action family (pricing/permission grouping, §11.3).</param>
/// <param name="PointCost">System-point price in a normal wave (§11.4 计价).</param>
/// <param name="DefaultTier">Default permission tier (§11.4 allow/ask split).</param>
/// <param name="UnlockEra">Era index (1..6) from which the verb may run (appendix G.2 解锁时代 column).</param>
/// <param name="DoubledOnBossWave">Whether the Boss-wave surge (战斗类 ×2, §11.4) applies to this verb.</param>
/// <param name="NeverAutoApprove">Whether the verb may never enter a level auto-approve whitelist (SellTower, F.4 危险位).</param>
public sealed record SystemActionTariff(
    SystemActionKind Verb,
    ActionGroup Group,
    int PointCost,
    PermissionTier DefaultTier,
    int UnlockEra,
    bool DoubledOnBossWave,
    bool NeverAutoApprove);

/// <summary>
/// The frozen 19-verb tariff table (T-050, C-14): the single data source
/// the budget ledger and the approval gate both read. Mapping is data
/// (one row per verb), never a switch statement that could drift per call
/// site — the enum and this table are kept in lockstep by a completeness
/// test over <see cref="SystemActionKind"/> values. Anchors: v3.2 §11.3
/// (groups/fields), §11.4 (pricing 建造 2/战斗 1/物流 1/巡逻 1/侦察 0 and
/// the allow/ask split incl. SpawnScout allow + 照常扣点), appendix G.2
/// (unlock-era column; SpawnScout=E2 巡逻级, HeroOrder floor=E2 with the
/// per-era hero unlock refined by the C-03 registry), F.4 (危险位:
/// SellTower never auto-approved). Mods may widen parameter spaces but
/// never this table (RL6).
/// </summary>
public static class SystemActionTariffTable
{
    /// <summary>
    /// All 19 rows, one per closed verb. Boss surge doubles the combat
    /// family only; SpawnScout is management/patrol (1 pt, no surge);
    /// all other management verbs are free (0 pt) per §11.4
    /// "照常扣点"（点数按注册表计价——仅巡逻有价）.
    /// </summary>
    public static readonly IReadOnlyList<SystemActionTariff> Rows = new SystemActionTariff[]
    {
        // ---- recon: 0 pt, allow, E1 -------------------------------------
        new(SystemActionKind.LookAround, ActionGroup.Recon, 0, PermissionTier.Allow, 1, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.InspectTile, ActionGroup.Recon, 0, PermissionTier.Allow, 1, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.QueryWorld, ActionGroup.Recon, 0, PermissionTier.Allow, 1, DoubledOnBossWave: false, NeverAutoApprove: false),

        // ---- construction: 2 pt, ask, E2 --------------------------------
        new(SystemActionKind.BuildPlan, ActionGroup.Construction, 2, PermissionTier.Ask, 2, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.UpgradeTower, ActionGroup.Construction, 2, PermissionTier.Ask, 2, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.SellTower, ActionGroup.Construction, 2, PermissionTier.Ask, 2, DoubledOnBossWave: false, NeverAutoApprove: true),
        new(SystemActionKind.SetRally, ActionGroup.Construction, 2, PermissionTier.Ask, 2, DoubledOnBossWave: false, NeverAutoApprove: false),

        // ---- logistics: 1 pt, ask, E2 -----------------------------------
        new(SystemActionKind.RerouteLogistics, ActionGroup.Logistics, 1, PermissionTier.Ask, 2, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.SetStockpile, ActionGroup.Logistics, 1, PermissionTier.Ask, 2, DoubledOnBossWave: false, NeverAutoApprove: false),

        // ---- combat: 1 pt (×2 on Boss waves), ask, E2 --------------------
        new(SystemActionKind.FocusFire, ActionGroup.Combat, 1, PermissionTier.Ask, 2, DoubledOnBossWave: true, NeverAutoApprove: false),
        new(SystemActionKind.SetTowerMode, ActionGroup.Combat, 1, PermissionTier.Ask, 2, DoubledOnBossWave: true, NeverAutoApprove: false),
        new(SystemActionKind.TriggerScroll, ActionGroup.Combat, 1, PermissionTier.Ask, 2, DoubledOnBossWave: true, NeverAutoApprove: false),
        new(SystemActionKind.HeroOrder, ActionGroup.Combat, 1, PermissionTier.Ask, 2, DoubledOnBossWave: true, NeverAutoApprove: false),

        // ---- management: allow, per-registry cost (all 0 except patrol) --
        new(SystemActionKind.AskPlayer, ActionGroup.Management, 0, PermissionTier.Allow, 1, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.ReportToPlayer, ActionGroup.Management, 0, PermissionTier.Allow, 1, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.TodoBoard, ActionGroup.Management, 0, PermissionTier.Allow, 2, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.SpawnScout, ActionGroup.Management, 1, PermissionTier.Allow, 2, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.SetAlarm, ActionGroup.Management, 0, PermissionTier.Allow, 1, DoubledOnBossWave: false, NeverAutoApprove: false),
        new(SystemActionKind.StopTask, ActionGroup.Management, 0, PermissionTier.Allow, 2, DoubledOnBossWave: false, NeverAutoApprove: false),
    };

    private static readonly IReadOnlyDictionary<SystemActionKind, SystemActionTariff> ByVerb =
        Rows.ToDictionary(row => row.Verb);

    /// <summary>
    /// Looks up the tariff row of a closed verb. Every
    /// <see cref="SystemActionKind"/> value has exactly one row — the
    /// completeness is guarded by tests, so this never misses.
    /// </summary>
    /// <param name="verb">One of the 19 closed verbs.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An undefined enum value slipped through (defensive; the closed set
    /// makes this unreachable in practice).</exception>
    public static SystemActionTariff Get(SystemActionKind verb)
    {
        if (ByVerb.TryGetValue(verb, out var row))
        {
            return row;
        }

        throw new ArgumentOutOfRangeException(
            nameof(verb), verb, "Verb is outside the closed 19-verb tariff table (K-3).");
    }
}
