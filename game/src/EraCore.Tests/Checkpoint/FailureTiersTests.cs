using EraCore.Checkpoint;
using EraCore.Combat;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Checkpoint;

// [C-04][F-004] 失败分档执行器（T-057）三场景：①普通关失败=检查点恢复+残骸
// 30% 折扣修复账目（首败再 ×0.8=24%，甲方已签认）；②终局关失败=只清本关存档+
// 检查点（资源池/科技零损失）+免费演习；③演习=敌人 ×0.5 标志位进 seed 参数。
public class FailureTiersTests : SqliteSavePortTestBase
{
    // ------------------------------------------------- ①普通关：检查点+残骸

    [Theory] // legal — 修复折扣表：残骸修复=原造价×30%（再败）/×24%（首败）
    [InlineData(true, 0.24)]  // 首败 ×0.8：30×0.30×0.80 = 7.2
    [InlineData(false, 0.30)] // 再败：30×0.30 = 9.0
    public void Normal_level_failure_restores_from_checkpoint_with_wreckage_repair_bill(
        bool firstDefeat, double expectedFraction)
    {
        SqliteSavePort port = CreatePort();
        var store = new WaveCheckpointStore(port, "level.e1.m01", 1.2); // 检查点 16
        Simulation live = CheckpointFixture.RunTo(700);
        store.SaveBoundary(live, store.CheckpointWave, SimE1Fixture.MilestoneCommands());

        NormalLevelFailureOutcome outcome = FailureTiers.FailNormalLevel(
            store, SimE1Fixture.Config(), new SimRng(9527), SimE1Fixture.Seed, firstDefeat);

        // 恢复=确定性重放到检查点：波/tick/seed 与活体一致，逐 tick 哈希相等
        Assert.Equal(FailureScenarioKind.NormalLevel, outcome.Tier);
        Assert.Equal(16, outcome.RestoredFromWave);
        Assert.Equal(700, outcome.RestoredAtTick);
        Assert.Equal(live.Seed, outcome.RunSeed);
        Assert.Equal(700, outcome.RestoredSimulation.CurrentTick);
        Assert.Equal(live.ComputeStateHash(), outcome.RestoredSimulation.ComputeStateHash());

        // 残骸账目：仓库除外（恢复锚点）——7 栋建筑（仓库+6），两座箭塔有内容造价
        TowerSpec arrow = SimEra1Content.WoodArrowTower();
        Assert.Equal(6, outcome.Wreckage.Count);
        Assert.DoesNotContain(outcome.Wreckage, entry => entry.BuildingId.Value == Simulation.WarehouseEntityValue);

        // 建筑 HP×30% 损毁（floor 计损）+ 修复成本=原造价×档位系数
        foreach (WreckEntry entry in outcome.Wreckage)
        {
            int expectedDamage = (int)Math.Floor(entry.HpBefore * FailureTiers.WreckageDamageFraction);
            Assert.Equal(expectedDamage, entry.DamageTaken);
            Assert.Equal(entry.HpBefore - expectedDamage, entry.WreckedHp);
            Assert.Equal(entry.OriginalCostPoints * expectedFraction, entry.RepairCostPoints, 6); // 容差=double 0.30/0.80 乘法链
        }

        // 内容锚点具体值：箭塔原造价 30（towers.json）→ 修复 7.2/9.0，两座合计
        WreckEntry firstTower = outcome.Wreckage.First(e => e.ContentId == arrow.Id);
        Assert.Equal(arrow.CostTotal, firstTower.OriginalCostPoints);
        Assert.Equal(arrow.CostTotal * expectedFraction, firstTower.RepairCostPoints, 6);
        Assert.Equal(2 * arrow.CostTotal * expectedFraction, outcome.TotalRepairCostPoints, 6);
        Assert.Equal(firstDefeat, outcome.FirstDefeat);
    }

    [Fact] // legal — 修复折扣比例自检：首败/再败修复价之比恰为 0.8（乘法叠加）
    public void First_defeat_discount_is_exactly_zero_point_eight()
    {
        SqliteSavePort port = CreatePort();
        var store = new WaveCheckpointStore(port, "level.e1.m01", 1.2);
        Simulation live = CheckpointFixture.RunTo(700);
        store.SaveBoundary(live, store.CheckpointWave, SimE1Fixture.MilestoneCommands());

        NormalLevelFailureOutcome first = FailureTiers.FailNormalLevel(
            store, SimE1Fixture.Config(), new SimRng(1), SimE1Fixture.Seed, firstDefeat: true);
        NormalLevelFailureOutcome again = FailureTiers.FailNormalLevel(
            store, SimE1Fixture.Config(), new SimRng(2), SimE1Fixture.Seed, firstDefeat: false);

        Assert.Equal(FailureTiers.FirstDefeatMultiplier,
            first.TotalRepairCostPoints / again.TotalRepairCostPoints, 6); // 容差=double 除法舍入
    }

    [Fact] // boundary — 未跨越检查点波（<10）即失败：无检查点 → 关卡从头开始
    public void Normal_level_failure_without_a_checkpoint_restarts_the_level()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 1.2);

        NormalLevelFailureOutcome outcome = FailureTiers.FailNormalLevel(
            store, SimE1Fixture.Config(), new SimRng(7), SimE1Fixture.Seed, firstDefeat: true);

        Assert.Equal(0, outcome.RestoredFromWave);
        Assert.Equal(0, outcome.RestoredAtTick);
        Assert.Equal(0, outcome.RestoredSimulation.CurrentTick);
        Assert.Empty(outcome.Wreckage); // 初始状态只有仓库（恢复锚点，不成残骸）
        Assert.Equal(0.0, outcome.TotalRepairCostPoints, 6);
    }

    // ------------------------------------------------- ②终局关：清域+演习

    [Fact] // legal — 终局关失败：只清本关存档+检查点；campaign 资源池/科技零损失
    public void Final_level_failure_clears_only_the_level_domain()
    {
        SqliteSavePort port = CreatePort();
        port.WriteCampaign(SampleCampaign(worldSeed: 424_242), SaveVersion.Current);
        var store = new WaveCheckpointStore(port, "level.e6.finale", 1.2);
        Simulation live = CheckpointFixture.RunTo(700);
        store.SaveBoundary(live, store.CheckpointWave, SimE1Fixture.MilestoneCommands());
        store.SaveBoundary(live, store.CheckpointWave + 1, SimE1Fixture.MilestoneCommands());

        FinalLevelFailureOutcome outcome = FailureTiers.FailFinalLevel(store);

        Assert.Equal(FailureScenarioKind.FinalLevel, outcome.Tier);
        Assert.True(outcome.LevelDomainCleared);
        Assert.True(outcome.CampaignDomainIntact);
        Assert.True(outcome.FreeExerciseGranted); // 一次免费演习

        // 本关存档+检查点已清：两个恢复面都无快照
        Assert.False(store.TryRestoreCheckpoint(out _));
        Assert.False(store.TryRestoreLatestBoundary(out _));

        // campaign 域原样可读：资源池/科技逐项零损失（具体值断言）
        ICampaignMeta campaign = port.ReadCampaign(SaveVersion.Current);
        Assert.Equal(424_242, campaign.WorldSeed);
        Assert.Equal(150, campaign.ResourcePool["steel"]);
        Assert.Equal(9, campaign.ResourcePool["intel"]);
        Assert.Contains("tech.agri", campaign.UnlockedTechs);
        Assert.Contains("tech.smith", campaign.UnlockedTechs);
    }

    [Fact] // illegal — 空参拒绝
    public void Tier_executors_reject_null_arguments()
    {
        var store = new WaveCheckpointStore(CreatePort(), "level.e1.m01", 1.2);

        Assert.Throws<ArgumentNullException>(
            () => FailureTiers.FailNormalLevel(null!, SimE1Fixture.Config(), new SimRng(1), 5, true));
        Assert.Throws<ArgumentNullException>(
            () => FailureTiers.FailNormalLevel(store, null!, new SimRng(1), 5, true));
        Assert.Throws<ArgumentNullException>(
            () => FailureTiers.FailNormalLevel(store, SimE1Fixture.Config(), null!, 5, true));
        Assert.Throws<ArgumentNullException>(() => FailureTiers.FailFinalLevel(null!));
    }
}
