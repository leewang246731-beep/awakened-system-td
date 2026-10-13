using EraCore.Balance;
using EraCore.Logistics;
using EraCore.Sim;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001] 完整局切片集成（T-040）：命令流建链（人力→采集→加工→民夫→
// 塔弹/资源池）+ 波次入场 + 塔开火 + 击杀 + 民夫交付 + 资源池增长——各系统
// 真实联动断言，关键值锚点手算抽查（F-030 数量公式 / F-034 宽限 / 采集 0.5 件/s /
// 威胁 HP 乘子 / 1 件弹药=10 发）。
//
// 时窗说明：F-034 冻结的首波宽限=1.5×波间隔，E1 内容真值下（t=1.2 → 72 s/波）
// 首敌入场在 tick 3240（=108 s），超出 60 s 里程碑窗——60 s 窗（1800 tick）由
// 确定性首证与金样覆盖，本集成测试延长到首波之后以完成"敌入场→开火→击杀"
// 的全链断言（08-task-plan T-040 验收原文的联动链）。[C-12][F-001]
public class SimIntegrationTests
{
    private const long FirstWaveTick = 3_240; // 手算锚点①：round(72×30)×1.5（F-034 宽限带下沿）
    private const long RunTicks = 4_200;      // 首波后 ~960 tick 的交战窗

    [Fact]
    public void Full_level_slice_links_every_subsystem()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(SimE1Fixture.MilestoneCommands());

        // —— 锚点②：采集产出 0.5 件/s（HarvestLaw tier1 锚点）。采集建筑在
        // tick 120 落地并当日开工；到 tick 719 共开工 600 tick = 20 s →
        // 恰好 10 整件。容差 ±1 件：每 tick 加 1/60 件的双精度累加误差
        //（容差来源：IEEE 754 累加，CS-3.3）。
        sim.Run(719);
        Assert.True(sim.TryGetProducer(new(3), out var harvester), "采集建筑 id=3 应已登记");
        Assert.InRange(harvester.CumulativeHarvested, 9, 11);

        sim.Run(RunTicks - 719);

        // —— 波次：首敌入场 tick = 宽限终点（手算锚点①），波内数量由
        // F-030 公式给出（(10+0)/2 × 2.0 = 10），批次节奏 4 批均布
        //（entry window 2160−450=1710，批距 570）→ tick 4200 前应有
        // 前两批 = 3+3 = 6 名敌入场。
        Assert.Equal(1, sim.CurrentWave);
        Assert.Contains(sim.Units, u => u.SpawnTick == FirstWaveTick);
        Assert.Equal(6, sim.Units.Count(u => u.SpawnTick >= FirstWaveTick));

        // —— 单位数值锚点：铠猪外散兵威胁 HP 乘子 t=1.2 → 0.5+(1.2−0.3)/0.9=1.5，
        // 兽崽 40 HP → ceil(40×1.5)=60（enemies.json + F.2 手算）。
        Unit? beastling = sim.Units.FirstOrDefault(u => u.ContentId == "era1:enemy-beastling");
        Assert.NotNull(beastling);
        Assert.Equal(60, beastling!.MaxHp);

        // —— 战斗：塔有开火、有命中、有击杀（弹药经 民夫→加工→民夫→塔 全链供给）。
        Assert.Equal(10, sim.AppliedCommandCount);
        Assert.True(sim.Towers.All(t => t.Weapon.FiredCount > 0), "两座塔都应已开火");
        Assert.True(sim.KillCount >= 1, $"应有击杀，实际 {sim.KillCount}");
        Assert.True(sim.ShotsLanded >= sim.KillCount * 2, "命中数应多于击杀数");

        // —— 弹药守恒锚点：1 件弹药=10 发（v3.2 §5）；塔1 余弹 = 交付件数×10 − 已发射。
        var tower1 = sim.Towers[0];
        var arrowPorter = sim.Carriers[1]; // 第二个民夫：converter → tower1（id 差 1）
        Assert.Equal(1L, arrowPorter.Id.Value - Simulation.CarrierEntityBase);
        Assert.Equal(
            (int)(arrowPorter.DeliveredItems * Carrier.ShotsPerItem) - tower1.Weapon.FiredCount,
            tower1.Weapon.AmmoShotsRemaining);

        // —— 物流：三条民夫线都有真实交付（木材→加工、箭矢→塔、木材→仓库）。
        Assert.True(sim.Carriers[0].DeliveredItems >= 2, "民夫1（木材→加工）应有交付");
        Assert.True(arrowPorter.DeliveredItems >= 1, "民夫2（箭矢→塔）应有交付");
        Assert.True(sim.Carriers[2].DeliveredItems >= 2, "民夫3（木材→仓库）应有交付");
        Assert.True(sim.Converters[0].CumulativeCrafts >= 1, "加工建筑应有成品");

        // —— 资源池增长：开局 100 − 塔成本 2×30 + 仓库交付 > 40（净增长为正）。
        double wood = sim.Stock.AmountOf(SimE1Fixture.Wood);
        Assert.True(wood > 40.0, $"资源池应超过花费后的 40，实际 {wood}");
        Assert.True(wood < 100.0 + sim.Carriers[2].DeliveredItems + 1, "资源池不应超过 100+交付（封顶检查）");

        // —— 快照收口：帧末视图与全量哈希一致，活单位入镜。
        Assert.Equal(RunTicks, sim.Snapshots.Latest!.Tick);
        Assert.Equal(sim.ComputeStateHash(), sim.Snapshots.Latest.StateHash);
        Assert.Equal(sim.Units.Count(u => u.Alive), sim.Snapshots.Latest.Units.Count);

        // —— 能源：劳工营 10/s 供给 4/s 需求 → 满足率恒 1（E1EnergyAnchors 手算）。
        Assert.Equal(1.0, sim.Ledger.Satisfaction, 9);
        Assert.Equal(1.5, Threat.Multipliers(1.2).Hp, 9); // 乘子锚点自查（防公式漂移）
    }
}
