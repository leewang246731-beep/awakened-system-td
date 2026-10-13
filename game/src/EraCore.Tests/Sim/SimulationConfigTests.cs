using EraCore.Pathfinding;
using EraCore.Sim;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001][F-154] SimulationConfig：关卡配置的默认值与 Simulation 构造
// 校验（record + 消费端校验，同 WaveSpawnConfig 模式）。数值全部来自已冻结
// 锚点/内容 JSON（CS-2.3）。T-040。
public class SimulationConfigTests
{
    [Fact] // legal — 默认值来自冻结锚点：流场预算 8ms、lane 0、经济建筑 HP 结构值 1
    public void Defaults_come_from_frozen_anchors()
    {
        var config = SimE1Fixture.Config();

        Assert.Equal(FlowFieldManager.DefaultFrameBudgetMs, config.FlowFrameBudgetMs);
        Assert.Equal(8.0, config.FlowFrameBudgetMs); // design v3.2 §6 锚点字面量
        Assert.Equal(0, config.Lane);
        Assert.Equal(1, config.EconomyBuildingHp); // [NEW-原型校准] 结构值，T-049 内容行落地
        Assert.Equal(SimE1Fixture.WarehouseCell, config.WarehouseCell); // 默认带仓库
        Assert.NotNull(config.Costs);
        Assert.Equal(SimE1Fixture.Wood, config.Costs!.Currency);
        Assert.Equal(0.0, config.Costs.LaborCampCostPoints); // 无内容锚点 → 0 直到 T-049
    }

    [Fact] // legal — 完整配置可构造出 Simulation（夹具即金样/确定性基线）
    public void Fixture_config_builds_a_simulation()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        Assert.Equal(0, sim.CurrentTick);
        Assert.Equal(SimE1Fixture.Seed, sim.Seed);
        Assert.Single(sim.Buildings); // 仓库
        Assert.Equal(SimBuildingKind.Warehouse, sim.Buildings[0].Kind);
    }

    public static TheoryData<string> InvalidConfigs => new()
    {
        // illegal — 维度越界/为零（GridWorld 上限 511）
        "dimensions",
        // illegal — 入场格为空
        "entry-empty",
        // illegal — 缺塔表/敌表/波次配置/初始库存清单（null 注解违规）
        "null-towers",
        "null-enemies",
        "null-waves",
        "null-stock",
        // illegal — 流场预算非正
        "budget",
        // illegal — 经济建筑 HP < 1
        "hp",
        // illegal — 基地/入场格/仓库出格
        "base-out",
        "entry-out",
        "warehouse-out",
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void Constructor_rejects_invalid_configs(string kind) // illegal — 逐项拒绝
    {
        var config = kind switch
        {
            "dimensions" => SimE1Fixture.Config() with { Width = 0 },
            "entry-empty" => SimE1Fixture.Config() with
            {
                EntryCells = Array.Empty<EraCore.Contracts.Foundation.CellCoord>(),
            },
            "null-towers" => SimE1Fixture.Config() with { TowerCatalog = null! },
            "null-enemies" => SimE1Fixture.Config() with { Enemies = null! },
            "null-waves" => SimE1Fixture.Config() with { Waves = null! },
            "null-stock" => SimE1Fixture.Config() with { InitialStock = null! },
            "budget" => SimE1Fixture.Config() with { FlowFrameBudgetMs = 0 },
            "hp" => SimE1Fixture.Config() with { EconomyBuildingHp = 0 },
            "base-out" => SimE1Fixture.Config() with { BaseCell = new(16, 23) },
            "entry-out" => SimE1Fixture.Config() with
            {
                EntryCells = new[] { new EraCore.Contracts.Foundation.CellCoord(8, 24) },
            },
            "warehouse-out" => SimE1Fixture.Config() with { WarehouseCell = new(-1, 14) },
            _ => throw new ArgumentException($"unknown kind {kind}"),
        };

        Assert.ThrowsAny<ArgumentException>(() =>
            new Simulation(config, new SimRng(1), 1));
    }

    [Fact] // illegal — null config / null rng 被拒
    public void Constructor_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new Simulation(null!, new SimRng(1), 1));
        Assert.Throws<ArgumentNullException>(() => new Simulation(SimE1Fixture.Config(), null!, 1));
    }

    [Fact] // boundary — 初始库存为空清单（零资源开局）合法
    public void Empty_initial_stock_is_legal()
    {
        var config = SimE1Fixture.Config(initialStock: new List<KeyValuePair<EraCore.Contracts.Foundation.ResourceKey, double>>());
        var sim = new Simulation(config, new SimRng(1), 1);
        Assert.Equal(0.0, sim.Stock.TotalAmount);
    }
}
