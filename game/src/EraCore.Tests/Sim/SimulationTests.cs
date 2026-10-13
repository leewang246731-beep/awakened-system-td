using EraCore.Contracts.Sim;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001][F-154] Simulation：固定序 tick 管线、命令应用（全走子系统公共
// API）、非法命令拒绝且状态哈希不受污染（T-040 边界验收）、双缓冲快照产出。
// 确定性首证在 SimDeterminismTests，金样在 SimGoldenTests。
public class SimulationTests
{
    [Fact] // legal — Run/Step 推进 tick；Run(0) 为 no-op；命令在指定 tick 生效
    public void Steps_advance_ticks_and_commands_apply_at_their_tick()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.Run(0);
        Assert.Equal(0, sim.CurrentTick);

        sim.Enqueue(SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell));
        sim.Run(59);
        Assert.Empty(sim.Buildings.Skip(1)); // 劳工营尚未到 tick 60

        sim.Run(1); // tick 60：labor camp 落地
        Assert.Equal(60, sim.CurrentTick);
        Assert.Equal(2, sim.Buildings.Count);
        Assert.Equal(SimBuildingKind.LaborCamp, sim.Buildings[1].Kind);
        Assert.Equal(2L, sim.Buildings[1].Id.Value);
        Assert.Equal(1, sim.AppliedCommandCount);
        Assert.All(sim.CommandResults, r => Assert.True(r.Accepted));
    }

    [Fact] // legal — 全部九条里程碑命令在 60~540 tick 间依序被接受
    public void Milestone_command_stream_applies_completely()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(SimE1Fixture.MilestoneCommands());
        sim.Run(600);

        Assert.Equal(10, sim.AppliedCommandCount);
        Assert.Equal(10, sim.CommandResults.Count);
        Assert.Equal(7, sim.Buildings.Count); // 仓库 + 6 座命令建筑
        Assert.Equal(2, sim.Towers.Count);
        Assert.Equal(2, sim.Producers.Count);
        Assert.Single(sim.Converters);
        Assert.Equal(4, sim.Carriers.Count);
        Assert.Equal(1, sim.Ledger.ProducerCount); // 一个劳工营节点（10/s 输出由锚点表钉死）
        Assert.Equal(3, sim.Ledger.ConsumerCount); // 2 采集(1/s) + 1 加工(2/s)

        // 命令确实走了子系统公共 API：占用登记 + 能源节点 + 资源扣款；
        // 池内木材守恒：100 − 2×30（塔成本来自 towers.json）+ 仓库线已交付量
        Assert.Equal(2, sim.Towers.Count(t => sim.World.IsOccupied(t.Cell)));
        Assert.Equal(40.0 + sim.Carriers[2].DeliveredItems, sim.Stock.AmountOf(SimE1Fixture.Wood));
    }

    [Fact] // legal — 帧末快照：Latest 帧号/StateHash 与全量哈希一致，活单位入镜
    public void Frame_end_snapshot_carries_tick_hash_and_live_units()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        Assert.Null(sim.Snapshots.Latest); // 首帧之前无快照

        sim.Step();
        Assert.NotNull(sim.Snapshots.Latest);
        Assert.Equal(1, sim.Snapshots.Latest!.Tick);
        Assert.Equal(sim.ComputeStateHash(), sim.Snapshots.Latest.StateHash);
        Assert.Empty(sim.Snapshots.Latest.Units); // 1800 tick 内无波次（F-034 宽限）
        Assert.Single(sim.Snapshots.Latest.Buildings); // 仓库

        sim.Step();
        Assert.Equal(2, sim.Snapshots.Latest!.Tick);
        Assert.NotNull(sim.Snapshots.Previous);
        Assert.Equal(1, sim.Snapshots.Previous!.Tick);
    }

    [Fact] // illegal — 里程碑之外混入非法命令：全部被拒且带原因码
    public void Illegal_commands_are_rejected_with_reason_codes()
    {
        var config = SimE1Fixture.Config();
        var sim = new Simulation(config, new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.EnqueueRange(new List<SimCommand>
        {
            SimCommands.BuildLaborCamp(10, SimE1Fixture.HarvesterCell),   // 合法参照
            SimCommands.BuildHarvester(15, SimE1Fixture.ConverterCell, SimE1Fixture.Wood), // 合法参照（id=3 供货方）
            SimCommands.BuildLaborCamp(20, SimE1Fixture.HarvesterCell),   // cell-occupied
            SimCommands.BuildTower(30, "era1:tower-void", SimE1Fixture.TowerCell), // tower-unknown
            SimCommands.BuildTower(40, "era1:tower-spike-trap", SimE1Fixture.TowerCell), // 无武器循环
            SimCommands.Malformed(50),                                    // payload-malformed-json
            SimCommands.UnknownVerb(60),                                  // verb-unknown
            SimCommands.SystemAction(70),                                 // kind-unsupported
            SimCommands.AssignCarrier(80, from: 999, to: 3),              // from-not-a-supplier
            SimCommands.AssignCarrier(90, from: 3, to: 998),              // to-not-a-receiver
            SimCommands.BuildHarvester(100, SimE1Fixture.Harvester2Cell, SimE1Fixture.Wood, seats: 0), // seats-invalid
        });
        sim.Run(200);

        Assert.Equal(11, sim.CommandResults.Count);
        Assert.Equal(2, sim.AppliedCommandCount);

        Assert.True(sim.CommandResults[0].Accepted);
        Assert.True(sim.CommandResults[1].Accepted);
        string[] expected =
        {
            "cell-occupied",
            "tower-unknown:era1:tower-void",
            "tower-no-weapon-cycle:era1:tower-spike-trap",
            "payload-malformed-json",
            "verb-unknown:sell-everything",
            "kind-unsupported:SystemAction",
            "from-not-a-supplier:999",
            "to-not-a-receiver:998",
            "seats-invalid",
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.False(sim.CommandResults[i + 2].Accepted, $"command {i + 2} 应被拒");
            Assert.StartsWith(expected[i], sim.CommandResults[i + 2].RejectionReason);
        }
    }

    [Fact] // illegal — 水面格（未解锁格）拒绝建塔
    public void Water_cell_builds_are_rejected()
    {
        var config = SimE1Fixture.Config(waterRatio: 0.30);
        var sim = new Simulation(config, new SimRng(4321), 4321);

        EraCore.Contracts.Foundation.CellCoord? water = null;
        for (int y = 0; y < SimE1Fixture.Height && water is null; y++)
        {
            for (int x = 0; x < SimE1Fixture.Width; x++)
            {
                if (sim.World.TerrainAt(new(x, y)) == EraCore.World.TerrainKind.Water)
                {
                    water = new(x, y);
                    break;
                }
            }
        }

        Assert.NotNull(water); // seed 4321 + 30% 水面必然命中
        sim.Enqueue(SimCommands.BuildLaborCamp(10, water));
        sim.Run(20);
        Assert.False(sim.CommandResults[0].Accepted);
        Assert.Equal("cell-not-buildable", sim.CommandResults[0].RejectionReason);
    }

    [Fact] // illegal — 资源不足拒绝建塔（cost_total=30 > 库存 20）
    public void Insufficient_resources_reject_the_build()
    {
        var config = SimE1Fixture.Config(initialStock: new List<KeyValuePair<EraCore.Contracts.Foundation.ResourceKey, double>>
        {
            new(SimE1Fixture.Wood, 20.0),
        });
        var sim = new Simulation(config, new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.Enqueue(SimCommands.BuildTower(10, "era1:tower-wood-arrow", SimE1Fixture.TowerCell));
        sim.Run(20);

        Assert.False(sim.CommandResults[0].Accepted);
        Assert.Equal("insufficient-resources", sim.CommandResults[0].RejectionReason);
        Assert.Empty(sim.Towers);
        Assert.Equal(20.0, sim.Stock.AmountOf(SimE1Fixture.Wood)); // 未扣款
    }

    [Fact] // illegal — 白名单异常退费且零半注册（W2 批 3 F2）：注册段中途拒绝
           // 不残留"_towers 有塔、_pacersById 无 pacer"半对——后续 Step 不抛
           // KeyNotFoundException，占用格不泄漏可复用
    public void Refunded_tower_rejection_leaves_no_half_registration()
    {
        // 毒化目录项：reload_ticks 合法（通过 tower-no-weapon-cycle 预检与
        // WeaponFire 构造），ammo_per_shot=-1 令 RegisterTower 抛白名单
        // ArgumentException。修复前顺序（_towers/_buildings/_magazineById 先于
        // RegisterTower 入账）退费后留半注册：combat 段 _pacersById[tower.Id]
        // 查无此键 ⇒ 后续 Step 抛 KeyNotFoundException，且 registry 占用泄漏。
        var poisoned = SimEra1Content.WoodArrowTower() with
        {
            Id = "era1:tower-poisoned",
            AmmoPerShot = -1, // AmmoItem 保持非 null——弹匣路径一并行使
        };
        var config = SimE1Fixture.Config() with
        {
            TowerCatalog = SimEra1Content.LoadTowerSpecs().Append(poisoned).ToArray(),
        };
        var sim = new Simulation(config, new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);

        sim.Enqueue(SimCommands.BuildTower(10, "era1:tower-poisoned", SimE1Fixture.TowerCell));
        sim.Run(11);

        // 退费 + 拒绝码具名（BuildCostCompensation 补偿窗）
        Assert.False(sim.CommandResults[0].Accepted);
        Assert.Equal(
            BuildCostCompensation.RefundedRejectionPrefix + ":ArgumentException",
            sim.CommandResults[0].RejectionReason);
        Assert.Equal(100.0, sim.Stock.AmountOf(SimE1Fixture.Wood)); // 初始 100 全额复原

        // 零半注册残留：无塔无新增建筑，后续 Step 全程不抛（修复前在此抛
        // KeyNotFoundException）
        Assert.Empty(sim.Towers);
        Assert.Single(sim.Buildings); // 仅初始条目
        sim.Run(300);

        // 占用格未泄漏：同一格改用合法目录塔重下命令，正常落子
        sim.Enqueue(SimCommands.BuildTower(sim.CurrentTick + 10, "era1:tower-wood-arrow", SimE1Fixture.TowerCell));
        sim.Run(11);
        Assert.True(sim.CommandResults[1].Accepted);
        Assert.Single(sim.Towers);
    }

    [Fact] // legal — 被拒命令不污染状态哈希：同 seed 下（含非法命令流）与
           // （仅合法命令流）逐 tick 哈希全等（T-040 边界验收原文）
    public void Rejected_commands_leave_the_state_hash_untouched()
    {
        const long ticks = 1_200;
        var legal = SimE1Fixture.MilestoneCommands();
        var polluted = legal.Concat(new[]
        {
            SimCommands.BuildLaborCamp(100, SimE1Fixture.LaborCampCell), // tick 100 时该格已被 tick 60 的劳工营占用
            SimCommands.BuildTower(110, "era1:tower-void", SimE1Fixture.TowerCell),
            SimCommands.Malformed(120),
            SimCommands.SystemAction(130),
            SimCommands.AssignCarrier(140, from: 42, to: 43),
        }).ToArray();

        var clean = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        clean.EnqueueRange(legal);
        var dirty = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        dirty.EnqueueRange(polluted);

        for (long tick = 1; tick <= ticks; tick++)
        {
            clean.Step();
            dirty.Step();
            if (tick % 60 == 0)
            {
                Assert.Equal(clean.ComputeStateHash(), dirty.ComputeStateHash()); // 逐段全等
            }
        }

        Assert.Equal(10, clean.AppliedCommandCount);
        Assert.Equal(10, dirty.AppliedCommandCount); // 5 条非法命令全部未入账
        Assert.Equal(15, dirty.CommandResults.Count);
    }

    [Fact] // boundary — 空命令流：经济静默、波次自走、哈希照常产出
    public void Empty_command_stream_still_simulates_waves_and_energy()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        sim.Run(300);

        Assert.Equal(0, sim.AppliedCommandCount);
        Assert.Empty(sim.CommandResults);
        Assert.Equal(1.0, sim.Ledger.Satisfaction); // 需求≤0 恒 1（v3.2 §5 L75）
        Assert.Equal(300, sim.Ledger.SettledTicks);
        Assert.Equal(300, sim.CurrentTick);
        Assert.NotNull(sim.Snapshots.Latest);
        long first = sim.ComputeStateHash();
        sim.Step();
        Assert.NotEqual(first, sim.ComputeStateHash()); // 计数器随 tick 推进
    }

    [Fact] // illegal — Run 负数被拒
    public void Run_rejects_negative_ticks()
    {
        var sim = new Simulation(SimE1Fixture.Config(), new SimRng(1), 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Run(-1));
    }
}
