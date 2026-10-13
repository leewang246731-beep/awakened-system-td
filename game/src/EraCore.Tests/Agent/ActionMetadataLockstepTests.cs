using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using Xunit;

namespace EraCore.Tests.Agent;

// [F-062] C-03 注册表 ↔ C-14 关税表 lockstep 守卫（REV-20261014-wave2-round1 #3）。
// 已知双源：Actions.ActionCatalog（T-051）与 Governance.SystemActionTariffTable
// （T-050）各写一份 19 动作元数据，另 PermissionTier 与 ActionPermissionTier
// 双枚举同构——结构性收口（Catalog 升格唯一行源+派生视图+枚举合并）挂
// T-070 DEBT。单源化落地前，本守卫把"单侧改动→静默分叉"变成"立即红"：
// 逐 19 动作对计价/权限档/解锁时代/Boss 加倍语义/危险位五字段 cross-check，
// 任一不一致即失败。

/// <summary>[F-062] TariffTable ↔ Catalog lockstep guard over the 19 closed verbs.</summary>
public class ActionMetadataLockstepTests
{
    [Fact] // structural — both tables describe the same closed verb set, each kind exactly once
    public void Both_tables_cover_the_same_19_verbs_exactly_once()
    {
        var enumVerbs = Enum.GetValues<SystemActionKind>().OrderBy(v => v).ToArray();
        var tariffVerbs = SystemActionTariffTable.Rows.Select(r => r.Verb).OrderBy(v => v).ToArray();
        var catalogVerbs = ActionCatalog.All.Select(d => d.Kind).OrderBy(v => v).ToArray();

        Assert.Equal(19, enumVerbs.Length);
        Assert.Equal(enumVerbs, tariffVerbs);
        Assert.Equal(enumVerbs, catalogVerbs);
        Assert.Equal(tariffVerbs.Length, tariffVerbs.Distinct().Count());
        Assert.Equal(catalogVerbs.Length, catalogVerbs.Distinct().Count());
    }

    [Fact] // lockstep — per-verb five-field cross-check (计价/权限档/解锁时代/Boss 加倍/危险位)
    public void Every_verb_agrees_on_all_five_metadata_fields()
    {
        var mismatches = new List<string>();
        foreach (SystemActionKind verb in Enum.GetValues<SystemActionKind>())
        {
            SystemActionTariff tariff = SystemActionTariffTable.Get(verb);
            ActionDescriptor catalog = ActionCatalog.Descriptor(verb);

            // ① 计价（§11.4 点数）
            if (tariff.PointCost != catalog.PointCost)
            {
                mismatches.Add($"{verb}: PointCost tariff={tariff.PointCost} catalog={catalog.PointCost}");
            }

            // ② 权限档（两套同构枚举：PermissionTier ↔ ActionPermissionTier）
            ActionPermissionTier expectedTier = tariff.DefaultTier == PermissionTier.Allow
                ? ActionPermissionTier.Allow
                : ActionPermissionTier.Ask;
            if (expectedTier != catalog.Tier)
            {
                mismatches.Add($"{verb}: Tier tariff={tariff.DefaultTier} catalog={catalog.Tier}");
            }

            // ③ 解锁时代（附录 G.2 解锁时代列，E1=1 … 终章=6）
            if (tariff.UnlockEra != catalog.UnlockEra)
            {
                mismatches.Add($"{verb}: UnlockEra tariff={tariff.UnlockEra} catalog={catalog.UnlockEra}");
            }

            // ④ Boss 加倍语义（§11.4 战斗类 ×2——目录侧无显式位，派生自 group=combat）
            bool catalogBossSurge = string.Equals(catalog.Group, "combat", StringComparison.Ordinal);
            if (tariff.DoubledOnBossWave != catalogBossSurge)
            {
                mismatches.Add(
                    $"{verb}: Boss surge tariff={tariff.DoubledOnBossWave} catalog=(group={catalog.Group})");
            }

            // ⑤ 危险位（F.4 永不自动允许 ↔ confirm_* 必填危险位）
            if (tariff.NeverAutoApprove != catalog.RequiresExplicitConfirmation)
            {
                mismatches.Add(
                    $"{verb}: Danger bit tariff={tariff.NeverAutoApprove} catalog={catalog.RequiresExplicitConfirmation}");
            }
        }

        Assert.Empty(mismatches);
    }
}
