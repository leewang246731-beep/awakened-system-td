using EraCore.Contracts.Sim;
using EraCore.Energy;
using EraCore.Sim;
using EraCore.Tests.Energy;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][C-08][F-010] Simulation 能源段 × SP-4 风险 tick（T-113）：E1 夹具 +
// 注入 EnergyRiskTuning 后，第 (2) 段 Settle 内推进过热通道——表值 tick 触发、
// 肇事机停机写回账本、同 seed 确定性、加产能降负载的对策可避免。表值来自
// Sp4RiskTables 夹具（era3.json 数据侧归 T-112，此处不读内容文件）。
public class EnergyRiskSimIntegrationTests
{
    // 劳工营（10 点/s）@tick 60 + 单采集器 10 坐席（需求 1×10=10 点/s）@tick 120
    // → 自 settle 120 起恰满载 100%。手算：热量 2/s × 50 s ⇒ 爆炸 tick
    // 120+1500−1=1619（首个满载结算计入计数）。
    private static readonly IReadOnlyList<SimCommand> OverloadCommands = new[]
    {
        SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell),
        SimCommands.BuildHarvester(120, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood, seats: 10),
    };

    private static SimulationConfig ConfigWithRisk(EnergyRiskTuning? tuning) => SimE1Fixture.Config() with
    {
        EnergyRisk = tuning,
    };

    private static Simulation BuildSim(long seed, EnergyRiskTuning? tuning)
    {
        var sim = new Simulation(ConfigWithRisk(tuning), new SimRng(seed), seed);
        sim.EnqueueRange(OverloadCommands);
        return sim;
    }

    [Fact] // legal —— 表值 tick 触发 + 后果写回账本（停机窗口内输出为 0）
    public void Overheat_fires_in_the_energy_segment_at_the_table_tick()
    {
        Simulation sim = BuildSim(SimE1Fixture.Seed, Sp4RiskTables.OverheatOnly());

        sim.Run(1618);
        Assert.Empty(sim.Ledger.RiskEvents); // N−1：前一 tick 未触发

        sim.Step(); // tick 1619
        EnergyRiskOutcome outcome = Assert.Single(sim.Ledger.RiskEvents);
        Assert.Equal(1619, outcome.Tick);
        Assert.Equal(EnergyRiskEventKind.OverheatExplosion, outcome.Kind);
        Assert.Equal(new EnergyNodeId(Simulation.LaborCampNodeBase), outcome.Culprit);
        Assert.Equal(4.0, outcome.RadiusCells);            // SP-4 后果列
        Assert.Equal(0.50, outcome.BuildingDamageMaxHpFraction);
        Assert.Equal(300.0, outcome.EnemyDamage);
        Assert.Equal(60.0, outcome.GeneratorDowntimeSeconds);
        Assert.Equal(0.50, outcome.GeneratorDerateFraction);
        Assert.Equal(30.0, outcome.GeneratorDerateSeconds);

        // 停机窗口（1620..3419）内：有效发电 0、满足度 0（需求 10 仍在册）
        sim.Run(80);
        Assert.Equal(0.0, sim.Ledger.TotalOutputPerSecond, 9);
        Assert.Equal(0.0, sim.Ledger.Satisfaction, 9);
    }

    [Fact] // legal —— 对策可避免（防"必然爆炸"）：加产能降负载 ⇒ 5000 tick 零事件
    public void Counterplay_extra_capacity_prevents_the_explosion()
    {
        var commands = new List<SimCommand>(OverloadCommands)
        {
            // tick 700 再建一营：输出 20、需求 10 → 负载 50%，窗口清零、热量回落
            SimCommands.BuildLaborCamp(700, SimE1Fixture.Harvester2Cell),
        };
        var sim = new Simulation(
            ConfigWithRisk(Sp4RiskTables.OverheatOnly()), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(commands);

        sim.Run(5000);

        Assert.Empty(sim.Ledger.RiskEvents);
        Assert.Equal(2, sim.Ledger.ProducerCount);
        Assert.Equal(0.5, sim.Ledger.LoadRatio, 9); // 末 tick 负载 10/20=50%（手算）
    }

    [Fact] // legal —— 未注入表（E1/E2 语义）：通道关闭，长满载也无事件
    public void Null_tuning_keeps_the_risk_channel_off()
    {
        Simulation sim = BuildSim(SimE1Fixture.Seed, tuning: null);

        sim.Run(3000);

        Assert.Null(sim.Ledger.Risk);
        Assert.Empty(sim.Ledger.RiskEvents);
    }

    [Fact] // legal —— 确定性：同 seed 同命令流 ⇒ 每 tick 哈希一致、触发序列一致
    public void Same_seed_same_command_stream_yields_identical_hashes_and_events()
    {
        Simulation RunFresh()
        {
            Simulation sim = BuildSim(SimE1Fixture.Seed, Sp4RiskTables.OverheatOnly());
            for (long i = 0; i < 2000; i++)
            {
                sim.Step();
            }

            return sim;
        }

        Simulation a = RunFresh();
        Simulation b = RunFresh();
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(
            a.Ledger.RiskEvents.Select(o => (o.Tick, o.Kind)),
            b.Ledger.RiskEvents.Select(o => (o.Tick, o.Kind)));
        Assert.Single(a.Ledger.RiskEvents);

        // 异 seed 分叉证明（同命令流）：地形/波次流不同 ⇒ 哈希远离
        Simulation c = BuildSim(SimE1Fixture.Seed + 1, Sp4RiskTables.OverheatOnly());
        for (long i = 0; i < 2000; i++)
        {
            c.Step();
        }

        Assert.NotEqual(a.ComputeStateHash(), c.ComputeStateHash());
    }
}
