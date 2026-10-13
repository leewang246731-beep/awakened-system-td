using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-064][F-063] The frozen 19-verb tariff table (T-050): pricing
// (§11.4 建造 2/战斗 1/物流 1/巡逻 1/侦察 0), permission tiers (§11.4
// allow/ask split), unlock eras (appendix G.2) and the two danger flags
// (F.4: SellTower 永不自动允许; combat-only Boss surge). The table is the
// single data source for ledger AND gate — these tests pin every row.

/// <summary>[C-14][F-064] Tariff table: pricing, tiers, unlock eras, flags.</summary>
public class SystemActionTariffTableTests
{
    [Fact] // legal — the closed K-3 set has exactly 19 verbs and the table covers each exactly once
    public void Table_covers_every_closed_verb_exactly_once()
    {
        var kinds = Enum.GetValues<SystemActionKind>();
        Assert.Equal(19, kinds.Length);
        Assert.Equal(19, SystemActionTariffTable.Rows.Count);
        Assert.Equal(kinds.Length, SystemActionTariffTable.Rows.Select(row => row.Verb).Distinct().Count());

        foreach (var kind in kinds)
        {
            _ = SystemActionTariffTable.Get(kind); // must not throw for any closed verb
        }
    }

    [Fact] // illegal — an undefined enum value is refused, not silently priced
    public void Get_refuses_undefined_verb()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SystemActionTariffTable.Get((SystemActionKind)999));
    }

    [Theory] // legal — §11.4 pricing: 建造 2 / 战斗 1 / 物流 1 / 巡逻(SpawnScout) 1 / 侦察+管理 0
    [InlineData(SystemActionKind.LookAround, 0)]
    [InlineData(SystemActionKind.InspectTile, 0)]
    [InlineData(SystemActionKind.QueryWorld, 0)]
    [InlineData(SystemActionKind.BuildPlan, 2)]
    [InlineData(SystemActionKind.UpgradeTower, 2)]
    [InlineData(SystemActionKind.SellTower, 2)]
    [InlineData(SystemActionKind.SetRally, 2)]
    [InlineData(SystemActionKind.RerouteLogistics, 1)]
    [InlineData(SystemActionKind.SetStockpile, 1)]
    [InlineData(SystemActionKind.FocusFire, 1)]
    [InlineData(SystemActionKind.SetTowerMode, 1)]
    [InlineData(SystemActionKind.TriggerScroll, 1)]
    [InlineData(SystemActionKind.HeroOrder, 1)]
    [InlineData(SystemActionKind.AskPlayer, 0)]
    [InlineData(SystemActionKind.ReportToPlayer, 0)]
    [InlineData(SystemActionKind.TodoBoard, 0)]
    [InlineData(SystemActionKind.SpawnScout, 1)]
    [InlineData(SystemActionKind.SetAlarm, 0)]
    [InlineData(SystemActionKind.StopTask, 0)]
    public void Point_costs_match_the_frozen_pricing(SystemActionKind verb, int expectedCost)
    {
        Assert.Equal(expectedCost, SystemActionTariffTable.Get(verb).PointCost);
    }

    [Theory] // legal — §11.4 tier split: recon+management allow; construction/logistics/combat ask
    [InlineData(SystemActionKind.LookAround, PermissionTier.Allow)]
    [InlineData(SystemActionKind.InspectTile, PermissionTier.Allow)]
    [InlineData(SystemActionKind.QueryWorld, PermissionTier.Allow)]
    [InlineData(SystemActionKind.BuildPlan, PermissionTier.Ask)]
    [InlineData(SystemActionKind.UpgradeTower, PermissionTier.Ask)]
    [InlineData(SystemActionKind.SellTower, PermissionTier.Ask)]
    [InlineData(SystemActionKind.SetRally, PermissionTier.Ask)]
    [InlineData(SystemActionKind.RerouteLogistics, PermissionTier.Ask)]
    [InlineData(SystemActionKind.SetStockpile, PermissionTier.Ask)]
    [InlineData(SystemActionKind.FocusFire, PermissionTier.Ask)]
    [InlineData(SystemActionKind.SetTowerMode, PermissionTier.Ask)]
    [InlineData(SystemActionKind.TriggerScroll, PermissionTier.Ask)]
    [InlineData(SystemActionKind.HeroOrder, PermissionTier.Ask)]
    [InlineData(SystemActionKind.AskPlayer, PermissionTier.Allow)]
    [InlineData(SystemActionKind.ReportToPlayer, PermissionTier.Allow)]
    [InlineData(SystemActionKind.TodoBoard, PermissionTier.Allow)]
    [InlineData(SystemActionKind.SpawnScout, PermissionTier.Allow)]
    [InlineData(SystemActionKind.SetAlarm, PermissionTier.Allow)]
    [InlineData(SystemActionKind.StopTask, PermissionTier.Allow)]
    public void Default_tiers_match_the_f4_split(SystemActionKind verb, PermissionTier expectedTier)
    {
        Assert.Equal(expectedTier, SystemActionTariffTable.Get(verb).DefaultTier);
    }

    [Theory] // legal — appendix G.2 unlock-era column (SpawnScout=E2 巡逻级; hero floor E2)
    [InlineData(SystemActionKind.LookAround, 1)]
    [InlineData(SystemActionKind.InspectTile, 1)]
    [InlineData(SystemActionKind.QueryWorld, 1)]
    [InlineData(SystemActionKind.BuildPlan, 2)]
    [InlineData(SystemActionKind.UpgradeTower, 2)]
    [InlineData(SystemActionKind.SellTower, 2)]
    [InlineData(SystemActionKind.SetRally, 2)]
    [InlineData(SystemActionKind.RerouteLogistics, 2)]
    [InlineData(SystemActionKind.SetStockpile, 2)]
    [InlineData(SystemActionKind.FocusFire, 2)]
    [InlineData(SystemActionKind.SetTowerMode, 2)]
    [InlineData(SystemActionKind.TriggerScroll, 2)]
    [InlineData(SystemActionKind.HeroOrder, 2)]
    [InlineData(SystemActionKind.AskPlayer, 1)]
    [InlineData(SystemActionKind.ReportToPlayer, 1)]
    [InlineData(SystemActionKind.TodoBoard, 2)]
    [InlineData(SystemActionKind.SpawnScout, 2)]
    [InlineData(SystemActionKind.SetAlarm, 1)]
    [InlineData(SystemActionKind.StopTask, 2)]
    public void Unlock_eras_match_appendix_g2(SystemActionKind verb, int expectedEra)
    {
        Assert.Equal(expectedEra, SystemActionTariffTable.Get(verb).UnlockEra);
    }

    [Fact] // legal — the Boss-wave surge covers exactly the four combat verbs (§11.4 战斗类 ×2)
    public void Boss_surge_flags_exactly_the_combat_family()
    {
        var surged = SystemActionTariffTable.Rows
            .Where(row => row.DoubledOnBossWave)
            .Select(row => row.Verb)
            .ToArray();
        Assert.Equal(
            new[] { SystemActionKind.FocusFire, SystemActionKind.SetTowerMode, SystemActionKind.TriggerScroll, SystemActionKind.HeroOrder },
            surged);
    }

    [Fact] // legal — F.4 危险位: exactly SellTower may never enter an auto-approve whitelist
    public void Never_auto_approve_covers_exactly_sell_tower()
    {
        var never = SystemActionTariffTable.Rows
            .Where(row => row.NeverAutoApprove)
            .Select(row => row.Verb)
            .ToArray();
        Assert.Equal(new[] { SystemActionKind.SellTower }, never);
    }

    [Fact] // legal — E1-available verbs are exactly the recon trio plus AskPlayer/ReportToPlayer/SetAlarm
    public void E1_available_verbs_match_the_unlock_table()
    {
        var e1 = SystemActionTariffTable.Rows
            .Where(row => row.UnlockEra == 1)
            .Select(row => row.Verb)
            .OrderBy(verb => verb.ToString(), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                SystemActionKind.AskPlayer,
                SystemActionKind.InspectTile,
                SystemActionKind.LookAround,
                SystemActionKind.QueryWorld,
                SystemActionKind.ReportToPlayer,
                SystemActionKind.SetAlarm,
            },
            e1);
    }

    [Theory] // legal — group families line up with §11.3 (one spot check per group)
    [InlineData(SystemActionKind.LookAround, ActionGroup.Recon)]
    [InlineData(SystemActionKind.BuildPlan, ActionGroup.Construction)]
    [InlineData(SystemActionKind.RerouteLogistics, ActionGroup.Logistics)]
    [InlineData(SystemActionKind.FocusFire, ActionGroup.Combat)]
    [InlineData(SystemActionKind.SpawnScout, ActionGroup.Management)]
    public void Groups_match_the_five_families(SystemActionKind verb, ActionGroup expectedGroup)
    {
        Assert.Equal(expectedGroup, SystemActionTariffTable.Get(verb).Group);
    }
}
