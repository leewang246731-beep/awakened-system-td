using EraCore.Contracts.Actions;

namespace EraCore.Agent.Actions;

/// <summary>
/// Which of the five closed action groups a system verb belongs to
/// (K-3, v3.2 §11.3). The group is the pricing/permission family of the
/// verb; §11.4 prices by family (建造 2 / 战斗 1 / 物流 1 / 巡逻 1 / 侦察 0)
/// and the Boss-wave surge applies to the combat family only. Lives here
/// since T-070: <see cref="ActionCatalog"/> rows carry the typed group and
/// the Governance tariff view derives from them (one-way dependency
/// Governance → Actions, no cycle).
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

/// <summary>Permission tier of a system action (K-3, correction #20/A12).</summary>
public enum ActionPermissionTier
{
    /// <summary>Executes without an approval gate (points still charged).</summary>
    Allow,

    /// <summary>Goes through the approval gate (danger bits never auto-approve).</summary>
    Ask,
}

/// <summary>
/// Era ordinals used by the unlock table (N9/A2). E1=1 … E5=5, finale=6;
/// the ordinal feeds the era-dependent BuildPlan cell cap (4 + 2 × era).
/// </summary>
public static class EraOrdinals
{
    /// <summary>E1 ancient (index 1).</summary>
    public const int E1 = 1;

    /// <summary>E2 classical (index 2).</summary>
    public const int E2 = 2; // ci-allow: R7 T-051 时代序号=E2（附录 G.2 解锁时代列量纲，非内容平衡数值）

    /// <summary>E3 industrial (index 3).</summary>
    public const int E3 = 3; // ci-allow: R7 T-051 时代序号=E3（附录 G.2 解锁时代列量纲，非内容平衡数值）

    /// <summary>E4 modern (index 4).</summary>
    public const int E4 = 4;

    /// <summary>E5 future (index 5).</summary>
    public const int E5 = 5;

    /// <summary>Finale (index 6).</summary>
    public const int Finale = 6; // ci-allow: R7 T-051 时代序号=终章 6（K-3 cells 上限 4+2×era 的量纲端点，非内容平衡数值）
}

/// <summary>
/// Registry metadata of one system action (K-3, C-03): group, permission
/// tier, point price, unlock era, the Boss-wave surge bit and the
/// explicit-confirmation danger bit. Frozen sources: v2.1 §11.3 field
/// table recovered in docs/engineering/10-recovered-v21-architecture.md
/// §三 with the v3.2 overwrites applied (permission tiers #20/A12, unlock
/// eras #10/N9/A2, point shorthand recon 0 / construction 2 / logistics 1 /
/// combat 1 / patrol 1 — full pricing per v3.2 §11.4 lives in the C-14
/// ledger). Since T-070 this record is the SINGLE row source of the 19
/// verbs: the Governance tariff table is a derived view over it, never a
/// second literal table (DEBT closure, REV-20261014-wave2-round1 #3).
/// </summary>
/// <param name="Kind">Which of the 19 closed verbs this entry describes.</param>
/// <param name="Verb">Wire name of the verb (matches <see cref="SystemActionKind"/> names).</param>
/// <param name="Group">Action family: recon/construction/logistics/combat/management (§11.3).</param>
/// <param name="Tier">Allow (no approval gate) or ask (approval gate).</param>
/// <param name="PointCost">Declared system-point price; charging itself is the C-14 ledger's job.</param>
/// <param name="UnlockEra">First era ordinal the action may legally run in (E1=1 …).</param>
/// <param name="DoubledOnBossWave">Whether the Boss-wave surge (战斗类 ×2, §11.4) applies to this verb.</param>
/// <param name="RequiresExplicitConfirmation">True when a confirm_* danger bit must be present (never auto-approvable).</param>
public sealed record ActionDescriptor(
    SystemActionKind Kind,
    string Verb,
    ActionGroup Group,
    ActionPermissionTier Tier,
    int PointCost,
    int UnlockEra,
    bool DoubledOnBossWave,
    bool RequiresExplicitConfirmation);

/// <summary>
/// The frozen 19-entry metadata table behind the action registry (C-03,
/// T-051) — since T-070 the UNIQUE row source of the 19 closed verbs:
/// every verb of <see cref="SystemActionKind"/> has exactly one row;
/// names outside this table never resolve (the closed-verb rule — mods
/// may widen parameter spaces, never this list). The C-14 tariff view
/// (<c>Governance.SystemActionTariffTable</c>) is derived from these rows,
/// so pricing/permission/unlock metadata exists exactly once in the
/// codebase (single-source DEBT closure).
/// </summary>
public static class ActionCatalog
{
    // §11.4 point prices (K-3 frozen; charging itself is the C-14 ledger's job).
    private const int Free = 0;
    private const int ConstructionPoints = 2; // ci-allow: R7 T-051 v3.2 §11.4 计价=建造 2 点（冻结域常量，非内容平衡数值）
    private const int LogisticsPoints = 1;
    private const int CombatPoints = 1;
    private const int PatrolPoints = 1;

    /// <summary>All 19 descriptors in enum declaration order.</summary>
    public static readonly IReadOnlyList<ActionDescriptor> All = new[]
    {
        // ------------------------------------------------ recon (0 pt, allow, E1+)
        new ActionDescriptor(SystemActionKind.LookAround, "LookAround", ActionGroup.Recon, ActionPermissionTier.Allow, Free, EraOrdinals.E1, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.InspectTile, "InspectTile", ActionGroup.Recon, ActionPermissionTier.Allow, Free, EraOrdinals.E1, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.QueryWorld, "QueryWorld", ActionGroup.Recon, ActionPermissionTier.Allow, Free, EraOrdinals.E1, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),

        // ---------------------------------------- construction (2 pt, ask, E2+)
        new ActionDescriptor(SystemActionKind.BuildPlan, "BuildPlan", ActionGroup.Construction, ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.UpgradeTower, "UpgradeTower", ActionGroup.Construction, ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.SellTower, "SellTower", ActionGroup.Construction, ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: true),
        new ActionDescriptor(SystemActionKind.SetRally, "SetRally", ActionGroup.Construction, ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),

        // ------------------------------------------- logistics (1 pt, ask, E2+)
        new ActionDescriptor(SystemActionKind.RerouteLogistics, "RerouteLogistics", ActionGroup.Logistics, ActionPermissionTier.Ask, LogisticsPoints, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.SetStockpile, "SetStockpile", ActionGroup.Logistics, ActionPermissionTier.Ask, LogisticsPoints, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),

        // ------------------------------ combat (1 pt ×2 on Boss waves, ask, E2+)
        new ActionDescriptor(SystemActionKind.FocusFire, "FocusFire", ActionGroup.Combat, ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, DoubledOnBossWave: true, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.SetTowerMode, "SetTowerMode", ActionGroup.Combat, ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, DoubledOnBossWave: true, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.TriggerScroll, "TriggerScroll", ActionGroup.Combat, ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, DoubledOnBossWave: true, RequiresExplicitConfirmation: false),
        // Hero unlock is per-era 3rd level (F-039); the finer gate lands with T-070.
        new ActionDescriptor(SystemActionKind.HeroOrder, "HeroOrder", ActionGroup.Combat, ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, DoubledOnBossWave: true, RequiresExplicitConfirmation: false),

        // ---------------------------------- management (allow; prices per table)
        new ActionDescriptor(SystemActionKind.AskPlayer, "AskPlayer", ActionGroup.Management, ActionPermissionTier.Allow, Free, EraOrdinals.E1, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.ReportToPlayer, "ReportToPlayer", ActionGroup.Management, ActionPermissionTier.Allow, Free, EraOrdinals.E1, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.TodoBoard, "TodoBoard", ActionGroup.Management, ActionPermissionTier.Allow, Free, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        // Patrol tier, 1 pt (N9/A2): E1 calls return ErrCapabilityLocked.
        new ActionDescriptor(SystemActionKind.SpawnScout, "SpawnScout", ActionGroup.Management, ActionPermissionTier.Allow, PatrolPoints, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.SetAlarm, "SetAlarm", ActionGroup.Management, ActionPermissionTier.Allow, Free, EraOrdinals.E1, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
        new ActionDescriptor(SystemActionKind.StopTask, "StopTask", ActionGroup.Management, ActionPermissionTier.Allow, Free, EraOrdinals.E2, DoubledOnBossWave: false, RequiresExplicitConfirmation: false),
    };

    /// <summary>Verb-name lookup (exact, ordinal — the wire names are case-sensitive).</summary>
    public static bool TryResolveName(string? name, out SystemActionKind kind)
    {
        foreach (ActionDescriptor descriptor in All)
        {
            if (string.Equals(descriptor.Verb, name, StringComparison.Ordinal))
            {
                kind = descriptor.Kind;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>Descriptor of one kind (every enum member has exactly one row).</summary>
    /// <exception cref="ArgumentOutOfRangeException">No row exists (cannot happen while the table covers the whole enum).</exception>
    public static ActionDescriptor Descriptor(SystemActionKind kind)
    {
        foreach (ActionDescriptor descriptor in All)
        {
            if (descriptor.Kind == kind)
            {
                return descriptor;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "No catalog row for this action kind.");
    }
}
