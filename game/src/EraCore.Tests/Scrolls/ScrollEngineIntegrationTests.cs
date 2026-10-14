using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Scrolls;
using EraCore.Sim;
using EraCore.Units;
using Xunit;
using EraCore.Tests.Sim;

namespace EraCore.Tests.Scrolls;

// [C-18][F-037][F-038] T-071 集成：SP-5 三张卷轴（SC-001/SC-002/SC-003）在
// 60 s（1800 ticks）真实 Simulation 中触发，触发时刻全部手算对照：
//   波 1=BaseCount 12、ThreatLevel 1.2（数量乘子 2.0/HP 乘子 1.5/间隔乘子
//   0.6→2160 ticks）→ 12 散兵（HP ⌈60×1.5⌉=90，速 1.6 px/tick）于 grace
//   540 ticks 自 (10,0) 单入口同批入场；敌人沿 x=10 直线每 20 ticks 走
//   一格、入场后第 T tick 位于 y=16+1.6×(T−539) → 进 row r 于 T≥529+20r。
//   - SC-001 北场分区（rows 2..4）：row 2 于 T=569 → 首个整秒边界 570 触发
//     （12≥5），FocusFire 最强敌=HP 并列取最小 id=1；冷却 1350>1800 仅一发。
//   - SC-002 瓮城分区（rows 16..18）：row 16 于 T=849 → 边界 870 触发，
//     +50% 效果 [870, 1170)；效果存续期重复条件不重触（AlreadyActive）。
//   - SC-003 动作卡：t=600 手动触发（TriggerScroll 通道），穿刺塔攻速
//     +25% [600,1050)；t=900 复触被 60 s 冷却拒绝（ready 2400）。
//   塔置于 (2,3)（北场分区内、距敌路 x=10 有 8 格>箭楼射程 6.5 格）→
//   全程零开火（ShotsLanded=0），保证手算不被战斗扰动。
// [C-18][F-037][F-038]
public class ScrollEngineIntegrationTests
{
    private const long Horizon = 1800;

    private static (Simulation Sim, ScrollEngine Engine, SimulationScrollWorldView View, RecordingExecutor Executor) Build()
    {
        var rng = new SimRng(20261018);
        var config = Config();
        var sim = new Simulation(config, rng, seed: 20261018);

        // 分区（F-117 命名层）：北场 rows 2..4 全宽；瓮城 rows 16..18 全宽。
        var northCells = new List<Contracts.Foundation.CellCoord>();
        var barbicanCells = new List<Contracts.Foundation.CellCoord>();
        for (int x = 0; x < 20; x++)
        {
            for (int y = 2; y <= 4; y++)
            {
                northCells.Add(new Contracts.Foundation.CellCoord(x, y));
            }

            for (int y = 16; y <= 18; y++)
            {
                barbicanCells.Add(new Contracts.Foundation.CellCoord(x, y));
            }
        }

        sim.World.DefineZone("north", northCells);
        sim.World.DefineZone("barbican", barbicanCells);

        _ = sim.Enqueue(new SimCommand(
            10, SimCommandKind.PlayerBuild,
            "{\"verb\":\"build-tower\",\"tower\":\"era2:tower-arrow\",\"cell\":{\"x\":2,\"y\":3}}"));

        var executor = new RecordingExecutor();
        var engine = new ScrollEngine(2, executor, effectSink: null);
        string json = File.ReadAllText(FindScrollsPath());
        foreach (var definition in ScrollDataLoader.Parse(json).Where(d => d.SpId is "SC-001" or "SC-002" or "SC-003"))
        {
            Assert.Equal(ScrollEquipResult.Ok, engine.Equip(definition));
        }

        var view = new SimulationScrollWorldView(sim, new[] { "north", "barbican" });
        return (sim, engine, view, executor);
    }

    private static string FindScrollsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "scrolls", "era2.json");
            if (File.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/scrolls/era2.json（测试须在仓库内运行）");
    }

    private static SimulationConfig Config()
    {
        return new SimulationConfig(
            Width: 20,
            Height: 26,
            ForestRatio: 0.0, // 夹具校准：地形全平——敌路=x=10 直线，手算前提
            RockRatio: 0.0,
            WaterRatio: 0.0,
            BaseCell: new Contracts.Foundation.CellCoord(10, 25),
            EntryCells: new[] { new Contracts.Foundation.CellCoord(10, 0) },
            TowerCatalog: SimEra2Content.LoadTowerSpecs(),
            Enemies: SimEra2Content.LoadEnemyCatalog(),
            Waves: new WaveSpawnConfig(
                BaseCount: 12, // (12+0)÷2=6 ×2.0=12（WaveFormula 手算）
                BeginWave: 99, // wave 1 无增量：⌊(1−99)/5⌋≤0→0
                Spacing: 5,
                Scaling: 2.0,
                ThreatLevel: 1.2, // 乘子表：数量 2.0/HP 1.5/间隔 0.6
                RegularRoster: new[] { "era2:enemy-skirmisher" },
                EliteLineEnabled: false,
                BossWave: 999, // 60 s 内无 Boss 波
                BossEnemyId: null,
                FirstWaveGraceMultiplier: 0.25, // 2160×0.25=540 → 波 1 于 t=540
                ValleySeconds: 15.0,
                BatchesPerWave: 1, // 单批：12 敌同刻同点入场（锁步行进）
                TickRateHz: 30,
                CellSizePx: 32,
                EntryCells: new[] { new Contracts.Foundation.CellCoord(10, 0) }),
            InitialStock: new List<KeyValuePair<ResourceKey, double>>
            {
                new(new ResourceKey("era2:res-timber"), 300.0), // 覆盖箭楼 90 标价
            },
            WarehouseCell: null,
            Costs: new BuildCostPolicy(new ResourceKey("era2:res-timber")));
    }

    [Fact]
    public void Three_SP5_scrolls_fire_at_hand_computed_ticks_inside_60s_sim()
    {
        var (sim, engine, view, executor) = Build();
        long hashAt600 = 0;
        IReadOnlyList<ActiveScrollEffect> effectsAt700 = Array.Empty<ActiveScrollEffect>();
        IReadOnlyList<ActiveScrollEffect> effectsAt1000 = Array.Empty<ActiveScrollEffect>();
        IReadOnlyList<ActiveScrollEffect> effectsAt1200 = Array.Empty<ActiveScrollEffect>();
        string? towerControllerAt700 = null;
        long claimedTowerId = -1;

        for (long tick = 1; tick <= Horizon; tick++)
        {
            sim.Step();
            engine.Step(tick, view);
            switch (tick)
            {
                case 600:
                    hashAt600 = engine.ComputeStateHash();
                    Assert.Equal(ScrollInvokeStatus.Ok, engine.TryInvokeActionCard("era2:scroll-spare-blades", tick, view));
                    break;
                case 700:
                    effectsAt700 = engine.ActiveEffects;
                    ActiveScrollEffect? bladesNow = effectsAt700.FirstOrDefault(e => e.ScrollId == "era2:scroll-spare-blades");
                    Assert.NotNull(bladesNow);
                    claimedTowerId = bladesNow!.ClaimedTowerIds.Single();
                    _ = engine.TryGetTowerController(claimedTowerId, out towerControllerAt700);
                    break;
                case 900:
                    // SC-003 冷却拒绝：ready=600+1800=2400>900（TriggerScroll 通道的手算锚点）
                    Assert.Equal(ScrollInvokeStatus.CooldownNotReady,
                        engine.TryInvokeActionCard("era2:scroll-spare-blades", tick, view));
                    break;
                case 1000:
                    effectsAt1000 = engine.ActiveEffects;
                    break;
                case 1200:
                    effectsAt1200 = engine.ActiveEffects;
                    break;
            }
        }

        // ---- SC-001 警弦：t=570 恰一发，FocusFire 走共享注册表门（K-3 同门）----
        Assert.Single(engine.FireLog, r => r.ScrollId == "era2:scroll-alarm-string"
            && r.Outcome == ScrollFireOutcome.Executed && r.Tick == 570 && !r.Manual);
        Assert.Equal(570, engine.GetBudget("era2:scroll-alarm-string", Horizon).CooldownReadyTick - 1350);
        Assert.Single(executor.Calls, c => c.Kind == Contracts.Actions.SystemActionKind.FocusFire);
        Assert.Equal("{\"action\":\"FocusFire\",\"target_id\":1}", executor.Calls[0].Json);

        // ---- SC-002 瓮城反击：t=870 触发；45 s 冷却（ready=870+1350=2220>1800）
        //      → 60 s 窗内恰一发；效果 [870,1170)（t=1000 在册、t=1200 已过期）。
        Assert.Single(engine.FireLog, r => r.ScrollId == "era2:scroll-barbican-retort"
            && r.Outcome == ScrollFireOutcome.Executed && r.Tick == 870);
        Assert.Equal(2220, engine.GetBudget("era2:scroll-barbican-retort", Horizon).CooldownReadyTick);
        ActiveScrollEffect barbican = Assert.Single(effectsAt1000, e => e.ScrollId == "era2:scroll-barbican-retort");
        Assert.Equal(1170, barbican.UntilTick);
        Assert.Equal("barbican", barbican.ZoneId);
        Assert.Equal(0.5, barbican.Magnitude, 3);
        Assert.DoesNotContain(effectsAt1200, e => e.ScrollId == "era2:scroll-barbican-retort"); // t=1200 已过期

        // ---- SC-003 备刃：t=600 手动触发生效 [600,1050)，t=700 在册并持有唯一穿刺塔 ----
        Assert.Single(engine.FireLog, r => r.ScrollId == "era2:scroll-spare-blades"
            && r.Outcome == ScrollFireOutcome.Executed && r.Tick == 600 && r.Manual);
        ActiveScrollEffect blades = Assert.Single(effectsAt700, e => e.ScrollId == "era2:scroll-spare-blades");
        Assert.Equal(TowerClass.Pierce, blades.TowerClass);
        Assert.Equal(0.25, blades.Magnitude, 3);
        Assert.Equal(1050, blades.UntilTick);
        Assert.Equal("era2:scroll-spare-blades", towerControllerAt700);

        // ---- 塔零开火（几何前提未被战斗扰动）+ 哈希演进（非零且随状态变化）----
        Assert.Equal(0, sim.ShotsLanded);
        Assert.NotEqual(0, hashAt600);
        Assert.NotEqual(hashAt600, engine.ComputeStateHash());

        // ---- 12 敌全程在场（HP 90=⌈60×1.5⌉ 威肋 HP 乘子手算锚点；零开火→零死亡）----
        Assert.Equal(12, sim.Units.Count);
        Assert.All(sim.Units, u => Assert.True(u.Alive));
    }

    [Fact]
    public void Identical_60s_run_reproduces_the_same_scroll_hash_sequence()
    {
        // 确定性（集成级）：两个独立实例同 seed 同命令流 → 逐边界卷轴哈希全等。
        long[] RunOnce()
        {
            var (sim, engine, view, _) = Build();
            var hashes = new List<long>();
            for (long tick = 1; tick <= Horizon; tick++)
            {
                sim.Step();
                engine.Step(tick, view);
                if (tick == 600)
                {
                    _ = engine.TryInvokeActionCard("era2:scroll-spare-blades", tick, view);
                }

                if (tick % 300 == 0)
                {
                    hashes.Add(engine.ComputeStateHash());
                }
            }

            return hashes.ToArray();
        }

        long[] first = RunOnce();
        long[] second = RunOnce();
        Assert.Equal(6, first.Length);
        Assert.Equal(first, second);
    }
}
