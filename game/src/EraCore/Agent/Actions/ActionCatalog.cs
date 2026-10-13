using EraCore.Contracts.Actions;

namespace EraCore.Agent.Actions;

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
/// tier, point price, unlock era and the explicit-confirmation danger bit.
/// Frozen sources: v2.1 §11.3 field table recovered in
/// docs/engineering/10-recovered-v21-architecture.md §三 with the v3.2
/// overwrites applied (permission tiers #20/A12, unlock eras #10/N9/A2,
/// point shorthand recon 0 / construction 2 / logistics 1 / combat 1 /
/// patrol 1 — full pricing per v3.2 §11.4 lives in the C-14 ledger).
/// </summary>
/// <param name="Kind">Which of the 19 closed verbs this entry describes.</param>
/// <param name="Verb">Wire name of the verb (matches <see cref="SystemActionKind"/> names).</param>
/// <param name="Group">Action group: recon/construction/logistics/combat/management.</param>
/// <param name="Tier">Allow (no approval gate) or ask (approval gate).</param>
/// <param name="PointCost">Declared system-point price; charging itself is the C-14 ledger's job.</param>
/// <param name="UnlockEra">First era ordinal the action may legally run in (E1=1 …).</param>
/// <param name="RequiresExplicitConfirmation">True when a confirm_* danger bit must be present (never auto-approvable).</param>
public sealed record ActionDescriptor(
    SystemActionKind Kind,
    string Verb,
    string Group,
    ActionPermissionTier Tier,
    int PointCost,
    int UnlockEra,
    bool RequiresExplicitConfirmation);

/// <summary>
/// The frozen 19-entry metadata table behind the action registry (C-03,
/// T-051): every verb of <see cref="SystemActionKind"/> has exactly one
/// row; names outside this table never resolve (the closed-verb rule —
/// mods may widen parameter spaces, never this list).
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
        new ActionDescriptor(SystemActionKind.LookAround, "LookAround", "recon", ActionPermissionTier.Allow, Free, EraOrdinals.E1, false),
        new ActionDescriptor(SystemActionKind.InspectTile, "InspectTile", "recon", ActionPermissionTier.Allow, Free, EraOrdinals.E1, false),
        new ActionDescriptor(SystemActionKind.QueryWorld, "QueryWorld", "recon", ActionPermissionTier.Allow, Free, EraOrdinals.E1, false),

        // ---------------------------------------- construction (2 pt, ask, E2+)
        new ActionDescriptor(SystemActionKind.BuildPlan, "BuildPlan", "construction", ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, false),
        new ActionDescriptor(SystemActionKind.UpgradeTower, "UpgradeTower", "construction", ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, false),
        new ActionDescriptor(SystemActionKind.SellTower, "SellTower", "construction", ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, true),
        new ActionDescriptor(SystemActionKind.SetRally, "SetRally", "construction", ActionPermissionTier.Ask, ConstructionPoints, EraOrdinals.E2, false),

        // ------------------------------------------- logistics (1 pt, ask, E2+)
        new ActionDescriptor(SystemActionKind.RerouteLogistics, "RerouteLogistics", "logistics", ActionPermissionTier.Ask, LogisticsPoints, EraOrdinals.E2, false),
        new ActionDescriptor(SystemActionKind.SetStockpile, "SetStockpile", "logistics", ActionPermissionTier.Ask, LogisticsPoints, EraOrdinals.E2, false),

        // ---------------------------------------------- combat (1 pt, ask, E2+)
        new ActionDescriptor(SystemActionKind.FocusFire, "FocusFire", "combat", ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, false),
        new ActionDescriptor(SystemActionKind.SetTowerMode, "SetTowerMode", "combat", ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, false),
        new ActionDescriptor(SystemActionKind.TriggerScroll, "TriggerScroll", "combat", ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, false),
        // Hero unlock is per-era 3rd level (F-039); the finer gate lands with T-070.
        new ActionDescriptor(SystemActionKind.HeroOrder, "HeroOrder", "combat", ActionPermissionTier.Ask, CombatPoints, EraOrdinals.E2, false),

        // ---------------------------------- management (allow; prices per table)
        new ActionDescriptor(SystemActionKind.AskPlayer, "AskPlayer", "management", ActionPermissionTier.Allow, Free, EraOrdinals.E1, false),
        new ActionDescriptor(SystemActionKind.ReportToPlayer, "ReportToPlayer", "management", ActionPermissionTier.Allow, Free, EraOrdinals.E1, false),
        new ActionDescriptor(SystemActionKind.TodoBoard, "TodoBoard", "management", ActionPermissionTier.Allow, Free, EraOrdinals.E2, false),
        // Patrol tier, 1 pt (N9/A2): E1 calls return ErrCapabilityLocked.
        new ActionDescriptor(SystemActionKind.SpawnScout, "SpawnScout", "management", ActionPermissionTier.Allow, PatrolPoints, EraOrdinals.E2, false),
        new ActionDescriptor(SystemActionKind.SetAlarm, "SetAlarm", "management", ActionPermissionTier.Allow, Free, EraOrdinals.E1, false),
        new ActionDescriptor(SystemActionKind.StopTask, "StopTask", "management", ActionPermissionTier.Allow, Free, EraOrdinals.E2, false),
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
