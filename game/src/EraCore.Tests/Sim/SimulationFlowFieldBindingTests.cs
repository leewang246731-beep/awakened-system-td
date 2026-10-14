using EraCore.Pathfinding;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-11][C-12] W4 门禁 F1 守卫（REV-20261015-wave4-gate）：Simulation 的
// 流场 tick 切片必须绑定 FlowFieldManager.UpdateDeterministic——T-067 DEBT
// 清账后的确定性入口；墙钟入口的负载相关切片会推迟重建完成 tick，使同种子
// 哈希在 CPU 争用下发散（T-086 发现，OffSystemRegression 两次发散实证）。
// 守卫走行为面而非文本面：观察位 LastAdvanceMode 由两条入口各自盖章（地基
// 断言见 FlowFieldManagerTests.LastAdvanceModeStampsTheBudgetEntryThatRan），
// Step 跑一拍后必须读到 Deterministic——未来把 Step 第 (5) 段改回
// _flows.Update(...)（或漏调任何入口）都会在本测试变红。
public class SimulationFlowFieldBindingTests
{
    [Fact]
    public void StepDrivesTheDeterministicFlowFieldEntry()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);

        // 构造期只 Acquire 不推进——观察位保持 None。
        Assert.Equal(FlowFieldAdvanceMode.None, sim.Flows.LastAdvanceMode);

        sim.Run(1);

        // Step 第 (5) 段必须盖 Deterministic 章：墙钟重载/漏调即红。
        Assert.Equal(FlowFieldAdvanceMode.Deterministic, sim.Flows.LastAdvanceMode);
    }

    [Fact]
    public void LongRunKeepsTheDeterministicBindingAcrossDirtyRebuilds()
    {
        // 脏区重建路径（MarkDirty → 下一切片续算）同样必须走确定性入口：
        // 建一座塔（3×3 脏标+占用失效）后多跑几拍，绑定不变。
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.Enqueue(SimCommands.BuildTower(1, "era1:tower-wood-arrow", SimE1Fixture.TowerCell));
        sim.Run(30);

        Assert.Equal(FlowFieldAdvanceMode.Deterministic, sim.Flows.LastAdvanceMode);
    }
}
