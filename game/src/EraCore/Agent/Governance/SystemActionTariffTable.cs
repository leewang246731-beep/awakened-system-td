using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;

namespace EraCore.Agent.Governance;

/// <summary>
/// One row of the tariff view: pricing + permission + unlock metadata for
/// exactly one of the 19 closed verbs (K-3). Pure data — derived ONLY from
/// <see cref="ActionCatalog"/> rows by <see cref="SystemActionTariffTable"/>
/// (never hand-written literals, T-070 single-source DEBT closure),
/// consumed by <see cref="SystemPointLedger"/> (pricing) and
/// <see cref="ApprovalGate"/> (permission/unlock). Immutable value type
/// with structural equality.
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
    ActionPermissionTier DefaultTier,
    int UnlockEra,
    bool DoubledOnBossWave,
    bool NeverAutoApprove);

/// <summary>
/// The frozen 19-verb tariff VIEW (T-050, C-14): what the budget ledger
/// and the approval gate both read. Since T-070 this is a DERIVED view of
/// the single row source <see cref="ActionCatalog"/> — one projection
/// over the catalog rows, never a second literal table (REV-20261014-
/// wave2-round1 #3 single-source DEBT closure; the former duplicate
/// <c>PermissionTier</c> enum merged into
/// <see cref="ActionPermissionTier"/> at the same time). Mapping is data
/// (one row per verb), never a switch statement that could drift per call
/// site — the enum, the catalog and the derived view are kept in lockstep
/// by completeness tests over <see cref="SystemActionKind"/> values.
/// Anchors: v3.2 §11.3 (groups/fields), §11.4 (pricing 建造 2/战斗 1/
/// 物流 1/巡逻 1/侦察 0 and the allow/ask split incl. SpawnScout allow +
/// 照常扣点), appendix G.2 (unlock-era column; SpawnScout=E2 巡逻级,
/// HeroOrder floor=E2 with the per-era hero unlock refined by the C-03
/// registry), F.4 (危险位: SellTower never auto-approved). Mods may widen
/// parameter spaces but never this view (RL6). Dependency direction is
/// one-way Governance → Actions (the Actions namespace never references
/// Governance).
/// </summary>
public static class SystemActionTariffTable
{
    /// <summary>
    /// All 19 rows, one per closed verb — derived from
    /// <see cref="ActionCatalog.All"/> (the unique row source). Boss surge
    /// doubles the combat family only; SpawnScout is management/patrol
    /// (1 pt, no surge); all other management verbs are free (0 pt) per
    /// §11.4 "照常扣点"（点数按注册表计价——仅巡逻有价）.
    /// </summary>
    public static readonly IReadOnlyList<SystemActionTariff> Rows = ActionCatalog.All
        .Select(descriptor => new SystemActionTariff(
            descriptor.Kind,
            descriptor.Group,
            descriptor.PointCost,
            descriptor.Tier,
            descriptor.UnlockEra,
            descriptor.DoubledOnBossWave,
            descriptor.RequiresExplicitConfirmation))
        .ToArray();

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
