using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-070] C-03 LookAround handler：战局摘要数据正确性（波次/资源/威胁口径的在场敌数与敌 HP——真 Simulation 公共视图）
public class LookAroundActionTests
{
    [Fact]
    public void Summary_before_any_step_reports_quiet_state()
    {
        // legal：开局态（tick 0、波次未开、初始库存 100 木、无敌人）
        var (_, view) = RegistryFixture.Create();
        var summary = new LookAroundAction().Run(view);

        Assert.Equal(0, summary.Tick);
        Assert.Equal(0, summary.Wave);
        Assert.Equal(0, summary.EnemiesPresent);
        Assert.Equal(0, summary.EnemyHpPresent);
        ResourceStockLine wood = Assert.Single(summary.Resources, r => r.Resource.Value == "era1:res-wood");
        Assert.Equal(100.0, wood.Amount);
    }

    [Fact]
    public void Summary_matches_simulation_state_after_first_wave_entry()
    {
        // legal：跑到首波入场后（首敌入场 tick=3240，F-034 宽限锚点，Sim 模块同口径）
        // ——摘要与 Simulation 公共视图逐项一致（走的是注册表视图适配器路径，非同源直读）
        var (simulation, view) = RegistryFixture.Create(stepToTick: 3_300);
        var summary = new LookAroundAction().Run(view);

        Assert.Equal(simulation.CurrentTick, summary.Tick);
        Assert.Equal(simulation.CurrentWave, summary.Wave);
        Assert.Equal(1, summary.Wave);

        int alive = simulation.Units.Count(u => u.Alive);
        Assert.True(alive > 0, "首波应已有敌人入场（seed 20261014 固定）");
        Assert.Equal(alive, summary.EnemiesPresent);
        Assert.Equal(simulation.Units.Where(u => u.Alive).Sum(u => u.Hp), summary.EnemyHpPresent);

        foreach (ResourceKey resource in simulation.Stock.Resources)
        {
            ResourceStockLine line = Assert.Single(summary.Resources, r => r.Resource.Equals(resource));
            Assert.Equal(simulation.Stock.AmountOf(resource), line.Amount);
        }
    }

    [Fact]
    public void Run_rejects_null_view()
    {
        // illegal：空视图立即拒绝（防静默空摘要）
        Assert.Throws<ArgumentNullException>(() => new LookAroundAction().Run(null!));
    }
}
