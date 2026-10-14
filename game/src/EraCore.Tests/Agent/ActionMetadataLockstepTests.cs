using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using Xunit;

namespace EraCore.Tests.Agent;

// [F-062][C-03][C-14] 单源守卫（T-070 收口 REV-20261014-wave2-round1 #3）。
// 自 T-070 起 Actions.ActionCatalog 是 19 动作元数据的唯一行源（组/权限档/
// 计价/解锁时代/Boss 加倍/危险位六字段全量持有），Governance.
// SystemActionTariffTable 是它的派生视图（一行 Select 投影，零手写字面量）；
// 原 PermissionTier 双枚举已并入 ActionPermissionTier。本守卫盯两件事：
// ① Catalog 对封闭枚举的完备性（19 行、恰好一次）；② 派生视图与源的逐字段
// 一致（若有人把视图改回手写表并改错一行，立即红）。

/// <summary>[F-062] Single-source guard: the catalog is the only row source; the tariff view derives from it.</summary>
public class ActionMetadataLockstepTests
{
    [Fact] // structural — the catalog (single source) covers the closed enum exactly once
    public void Catalog_covers_the_closed_19_verbs_exactly_once()
    {
        var enumVerbs = Enum.GetValues<SystemActionKind>().OrderBy(v => v).ToArray();
        var catalogVerbs = ActionCatalog.All.Select(d => d.Kind).ToArray();

        Assert.Equal(19, enumVerbs.Length);
        Assert.Equal(19, catalogVerbs.Length);
        Assert.Equal(enumVerbs, catalogVerbs.OrderBy(v => v).ToArray());
        Assert.Equal(catalogVerbs.Length, catalogVerbs.Distinct().Count()); // no duplicates
    }

    [Fact] // single source — every row carries all six metadata fields explicitly (no defaulted gaps)
    public void Every_catalog_row_carries_explicit_group_and_surge_metadata()
    {
        foreach (ActionDescriptor descriptor in ActionCatalog.All)
        {
            Assert.True(Enum.IsDefined(descriptor.Group), $"{descriptor.Kind}: group undefined");
            Assert.True(Enum.IsDefined(descriptor.Tier), $"{descriptor.Kind}: tier undefined");

            // The §11.4 invariant the explicit field replaced: Boss surge is
            // exactly the combat family — now data on the row, not a string compare.
            Assert.Equal(descriptor.Group == ActionGroup.Combat, descriptor.DoubledOnBossWave);
        }
    }

    [Fact] // derived view — tariff rows are the projection of the catalog rows, field by field, in order
    public void Tariff_rows_are_the_derived_view_of_the_catalog()
    {
        Assert.Equal(ActionCatalog.All.Count, SystemActionTariffTable.Rows.Count);

        var mismatches = new List<string>();
        for (int i = 0; i < ActionCatalog.All.Count; i++)
        {
            ActionDescriptor source = ActionCatalog.All[i];
            SystemActionTariff view = SystemActionTariffTable.Rows[i];

            if (view.Verb != source.Kind) mismatches.Add($"{i}: Verb {view.Verb} != {source.Kind}");
            if (view.Group != source.Group) mismatches.Add($"{source.Kind}: Group {view.Group} != {source.Group}");
            if (view.PointCost != source.PointCost) mismatches.Add($"{source.Kind}: PointCost {view.PointCost} != {source.PointCost}");
            if (view.DefaultTier != source.Tier) mismatches.Add($"{source.Kind}: Tier {view.DefaultTier} != {source.Tier}");
            if (view.UnlockEra != source.UnlockEra) mismatches.Add($"{source.Kind}: UnlockEra {view.UnlockEra} != {source.UnlockEra}");
            if (view.DoubledOnBossWave != source.DoubledOnBossWave) mismatches.Add($"{source.Kind}: Boss surge {view.DoubledOnBossWave} != {source.DoubledOnBossWave}");
            if (view.NeverAutoApprove != source.RequiresExplicitConfirmation) mismatches.Add($"{source.Kind}: NeverAutoApprove {view.NeverAutoApprove} != danger bit {source.RequiresExplicitConfirmation}");
        }

        Assert.Empty(mismatches);
    }

    [Fact] // derived view — the per-verb lookup answers the same derived rows
    public void Tariff_lookup_answers_the_derived_rows()
    {
        foreach (ActionDescriptor source in ActionCatalog.All)
        {
            SystemActionTariff row = SystemActionTariffTable.Get(source.Kind);
            Assert.Equal(source.PointCost, row.PointCost);
            Assert.Equal(source.Tier, row.DefaultTier);
        }
    }
}
