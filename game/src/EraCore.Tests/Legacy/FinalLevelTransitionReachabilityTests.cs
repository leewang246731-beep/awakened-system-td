using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.Legacy;
using EraCore.Sim;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Levels;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-003][F-011][F-055] 终局关跃迁可达性集成（终局关补全的验收面）：
// era_transition.json final_levels{1,2} 引用的 campaign:level-e1-final /
// campaign:level-e2-final 此前只是字符串——实关行不存在，E1→E2/E2→E3 的
// Precheck 在真实游戏永不可达（T-117 的『有行未占领仍硬拒』守卫恰好用
// 别的 id 注册占领，掩盖了引用悬空）。本套件把整条链跑真：
// ①E1 全五关（含 e1-final Boss 终局关）headless 真通关→逐关占领入树→
//   E1→E2 事务 Precheck 放行（真前置链通）；
// ②E2 全四关（含 e2-final）同理→E2→E3 放行（target_era=3 排期=e3-01..03）；
// ③E3 全五关（含 e3-final 堡垒号——T-124 补实关行，final_levels[3]=T-125
//   引用的实锚）同理→E3→E4 放行（target_era=4 排期=e4-01..03，T-124 链收口）；
// ④负向：占领本时代全部非终局关（id 全部来自真实关卡行）仍硬拒——缺的
//   恰是终局关一行（Precheck 谓词口径，RegisterCapture=循环占领入库写侧）。
// 入 CheckpointHeavyCollection 串行（全关卡循环耗时同 CampaignLoopE2ETests 量级）。
[Collection(CheckpointHeavyCollection.Name)]
public class FinalLevelTransitionReachabilityTests : SqliteSavePortTestBase
{
    /// <summary>Headless 真通关一关（offsystem 纯玩家命令流，T-084/T-116/T-124 先例）。</summary>
    private (int WinWaves, long VictoryTick, int Towers) PlayThrough(
        HandcraftedLevel level, string eraDir, ResourceStock? pool)
    {
        SimulationConfig config = FinalBossLevelTests.BuildBossConfig(
            level, eraDir, eraDir == "era1" ? 60 : eraDir == "era4" ? 0 : 100); // 践踏调参（BossBattle/Ironhoof/Fortress 先例；夜枭无践踏；非 Boss 关不消费）
        IReadOnlyList<SimCommand> commands = BuildCommands(level, eraDir);
        var session = new LevelSession(
            new LevelSessionConfig(config, $"reach.{level.Id}", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort(),
            pool);
        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        long steps = 0;
        // 胜波+12 波窗 horizon（FinalBossLevelTests +8 的加宽版）：T-085 首批
        // 关的弹链是刀锋经济（e2-01 两箭塔清 13 波的尾清场 ~+4 波窗），终局
        // 关 +2~3 波窗即胜——通关即停，horizon 只是失败护栏。
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long horizon = graceTicks + ((level.WinWaves + 12) * intervalTicks);
        while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
        {
            session.RunFor(600);
            steps += 600;
        }

        Assert.True(session.Machine.State == GameFlowState.Victory,
            $"未通关（state={session.Machine.State}）: id={level.Id} wave={session.Sim.CurrentWave} " +
            $"tick={session.Sim.CurrentTick} issued={session.IssuedCommands.Count} " +
            $"applied={session.Sim.AppliedCommandCount} towers={session.Sim.Towers.Count} " +
            $"alive={session.Sim.Units.Count(u => u.Alive)} aliveHp={session.Sim.Units.Where(u => u.Alive).Sum(u => u.Hp)}");
        return (session.WinWaves, session.Sim.CurrentTick, session.Sim.Towers.Count);
    }

    private static IReadOnlyList<HandcraftedLevel> LevelsOf(string file) =>
        HandcraftedLevelData.Load(file).Items.OrderBy(level => level.Slot).ToList();

    [Fact] // [F-003] legal — E1 全五关（含 e1-final）真通关→占领→E1→E2 Precheck 放行
    public void E1_full_clear_including_the_final_boss_level_unlocks_the_transition()
    {
        var transition = Transition();
        string finalId = transition.FinalLevels[1];
        Assert.Equal("campaign:level-e1-final", finalId);

        IReadOnlyList<HandcraftedLevel> levels = LevelsOf("e1.json");
        Assert.Equal(5, levels.Count);
        Assert.Contains(levels, level => level.Id == finalId); // 引用不悬空（补全前此行即红）

        // 全五关 headless 真通关（终局关=第 19 波踏群者），逐关占领入树。
        ResourceStock pool = Pool(wood: 500, stone: 400, food: 300);
        var tech = Tree(1);
        foreach (HandcraftedLevel level in levels)
        {
            (int winWaves, long victoryTick, int towers) = PlayThrough(level, "era1", pool);
            Assert.Equal(level.WinWaves, winWaves);
            Assert.True(victoryTick > 0);
            Assert.True(towers > 0);
            tech.RegisterCapture(level.Id);
        }

        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(E1Wonder().Id); // 附录 F.3 跃迁硬条件之奇观侧

        EraTransitionResult result = new EraTransitionService().Execute(
            Request(pool, tech, ledger, CityFacts(7, 40, 13, Mapping().Orientations),
                new CountingSavePort(CreatePort()), EmptyMeta(Seed, 1)));

        Assert.Equal(1, result.FromEraIndex);
        Assert.Equal(2, result.ToEraIndex); // Precheck 放行：终局关+奇观双前置皆真
        Assert.Equal(
            new[] { "campaign:level-e2-01", "campaign:level-e2-02", "campaign:level-e2-03" },
            result.Schedule.Select(slot => slot.LevelId).ToArray()); // E1→E2 排期=e2 行
        Assert.Equal(
            levels.Select(level => level.Id).ToHashSet(),
            tech.CapturedLevelIds.ToHashSet()); // 全五关占领入树（含终局关）
        Assert.Contains(finalId, tech.CapturedLevelIds);
    }

    [Fact] // [F-003] legal — E2 全四关（含 e2-final）真通关→占领→E2→E3 Precheck 放行
    public void E2_full_clear_including_the_final_boss_level_unlocks_the_transition()
    {
        var transition = Transition();
        string finalId = transition.FinalLevels[2];
        Assert.Equal("campaign:level-e2-final", finalId);

        IReadOnlyList<HandcraftedLevel> levels = LevelsOf("e2.json");
        Assert.Equal(4, levels.Count);
        Assert.Contains(levels, level => level.Id == finalId);

        // 全四关 headless 真通关（终局关=第 19 波铁蹄），逐关占领入树。
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 2);
        _ = pool.Deposit(new ResourceKey("era1:res-wood"), 600);
        _ = pool.Deposit(new ResourceKey("era1:res-stone"), 300);
        _ = pool.Deposit(new ResourceKey("era2:res-copper"), 500);
        var tech = Tree(2);
        foreach (HandcraftedLevel level in levels)
        {
            (int winWaves, _, _) = PlayThrough(level, "era2", pool);
            Assert.Equal(level.WinWaves, winWaves);
            tech.RegisterCapture(level.Id);
        }

        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(wonder => wonder.EraIndex == 2).Id);

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era3"), "era.json")));
        var request = new EraTransitionRequest(
            Seed,
            FromEraIndex: 2,
            pool,
            tech,
            ledger,
            CityFacts(11, 30, 9, Mapping().Orientations),
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(Seed + 3),
            new CountingSavePort(CreatePort()),
            EmptyMeta(Seed, 2),
            Mapping(),
            transition,
            Wonders());

        EraTransitionResult result = new EraTransitionService().Execute(request);

        Assert.Equal(2, result.FromEraIndex);
        Assert.Equal(3, result.ToEraIndex); // Precheck 放行（真前置链通）
        Assert.Equal(
            new[] { "campaign:level-e3-01", "campaign:level-e3-02", "campaign:level-e3-03" },
            result.Schedule.Select(slot => slot.LevelId).ToArray()); // E2→E3 排期=e3 行
    }

    [Fact] // [F-003] legal — E3 全五关（含 e3-final 堡垒号）真通关→占领→E3→E4 Precheck 放行
    public void E3_full_clear_including_the_final_boss_level_unlocks_the_transition_to_e4()
    {
        // T-124 链收口：final_levels[3]=campaign:level-e3-final（T-125 落引用）
        // 的实关行由本任务补齐——E3 全五关 headless 真通关（终局关=第 19 波
        // 堡垒号）后，E3→E4 事务在真实游戏可达；排期=target_era=4 行（T-125
        // 落位、逐值对齐 era4/levels.json T-123 冻结侧）；final_levels[4]=
        // campaign:level-e4-final（T-124 落位——E4→E5 引用先行+实关同落，引用
        // 不悬空：levels/e4.json slot 5 夜枭 Boss 关行真实存在）。
        var transition = Transition();
        Assert.Equal("campaign:level-e3-final", transition.FinalLevels[3]);
        Assert.Equal("campaign:level-e4-final", transition.FinalLevels[4]);

        IReadOnlyList<HandcraftedLevel> levels = LevelsOf("e3.json");
        Assert.Equal(5, levels.Count);
        string finalId = transition.FinalLevels[3];
        Assert.Contains(levels, level => level.Id == finalId); // 引用不悬空（补全前此行即红）
        Assert.Contains(LevelsOf("e4.json"), level => level.Id == transition.FinalLevels[4]); // E4 行同锚

        // 全五关 headless 真通关（终局关=第 19 波堡垒号），逐关占领入树。
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 3);
        _ = pool.Deposit(new ResourceKey("era3:res-steel"), 1200);
        _ = pool.Deposit(new ResourceKey("era3:res-coal"), 400);
        _ = pool.Deposit(new ResourceKey("era2:res-iron"), 300);
        var tech = Tree(3);
        foreach (HandcraftedLevel level in levels)
        {
            (int winWaves, long victoryTick, int towers) = PlayThrough(level, "era3", pool);
            Assert.Equal(level.WinWaves, winWaves);
            Assert.True(victoryTick > 0);
            Assert.True(towers > 0);
            tech.RegisterCapture(level.Id);
        }

        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(wonder => wonder.EraIndex == 3).Id); // 世界博览会=E3→E4 硬前置

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era4"), "era.json")));
        var request = new EraTransitionRequest(
            Seed,
            FromEraIndex: 3,
            pool,
            tech,
            ledger,
            CityFacts(13, 26, 9, Mapping().Orientations),
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(Seed + 3),
            new CountingSavePort(CreatePort()),
            EmptyMeta(Seed, 3),
            Mapping(),
            transition,
            Wonders());

        EraTransitionResult result = new EraTransitionService().Execute(request);

        Assert.Equal(3, result.FromEraIndex);
        Assert.Equal(4, result.ToEraIndex); // Precheck 放行：终局关+奇观双前置皆真（真前置链通）
        Assert.Equal(4, result.NewPool.WarehouseLevel); // 仓库 3+1=4（§5 自动 +1）
        Assert.Equal(
            new[] { "campaign:level-e4-01", "campaign:level-e4-02", "campaign:level-e4-03" },
            result.Schedule.Select(slot => slot.LevelId).ToArray()); // E3→E4 排期=e4 行
        Assert.Contains(finalId, tech.CapturedLevelIds); // 终局关占领入树（链收口物证）
    }

    [Fact] // [F-003] legal — E4 全五关（含 e4-final 夜枭）真通关→占领→E4→E5 Precheck 放行
    public void E4_full_clear_including_the_final_boss_level_unlocks_the_transition_to_e5()
    {
        // T-134 清账（REV-20261017-v10-fullclear §八5 勘正注收口）：
        // final_levels[4]=campaign:level-e4-final 的『引用先行』状态在本任务
        // 清偿——era5 目标域实体化（era5 五件套+levels/e5.json+排期三行），
        // E4→E5 事务在真实游戏可达：E4 全五关 headless 真通关（终局关=第 19 波
        // 夜枭）后 Precheck 放行（互联网奇观=E4 硬前置侧）；排期=target_era=5
        // 行（T-134 落位、逐值对齐 era5/levels.json 冻结侧）。
        var transition = Transition();
        Assert.Equal("campaign:level-e4-final", transition.FinalLevels[4]);

        IReadOnlyList<HandcraftedLevel> levels = LevelsOf("e4.json");
        Assert.Equal(5, levels.Count);
        string finalId = transition.FinalLevels[4];
        Assert.Contains(levels, level => level.Id == finalId); // 引用不悬空（T-124 落位，本任务域引用侧不变）
        Assert.Contains(LevelsOf("e5.json"), level => level.Id == "campaign:level-e5-final"); // E5 目标域实锚（终章=e5 压轴关）

        // 全五关 headless 真通关（终局关=第 19 波夜枭），逐关占领入树。
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 4);
        _ = pool.Deposit(new ResourceKey("era4:res-concrete"), 4000);
        _ = pool.Deposit(new ResourceKey("era4:res-oil"), 600);
        _ = pool.Deposit(new ResourceKey("era2:res-iron"), 300);
        var tech = Tree(4);
        foreach (HandcraftedLevel level in levels)
        {
            (int winWaves, long victoryTick, int towers) = PlayThrough(level, "era4", pool);
            Assert.Equal(level.WinWaves, winWaves);
            Assert.True(victoryTick > 0);
            Assert.True(towers > 0);
            tech.RegisterCapture(level.Id);
        }

        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(wonder => wonder.EraIndex == 4).Id); // 互联网=E4→E5 硬前置

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era5"), "era.json")));
        var request = new EraTransitionRequest(
            Seed,
            FromEraIndex: 4,
            pool,
            tech,
            ledger,
            CityFacts(13, 26, 9, Mapping().Orientations),
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(Seed + 4),
            new CountingSavePort(CreatePort()),
            EmptyMeta(Seed, 4),
            Mapping(),
            transition,
            Wonders());

        EraTransitionResult result = new EraTransitionService().Execute(request);

        Assert.Equal(4, result.FromEraIndex);
        Assert.Equal(5, result.ToEraIndex); // Precheck 放行：终局关+奇观双前置皆真（真前置链通）
        Assert.Equal(5, result.NewPool.WarehouseLevel); // 仓库 4+1=5（§5 自动 +1）
        Assert.Equal(
            new[] { "campaign:level-e5-01", "campaign:level-e5-02", "campaign:level-e5-03" },
            result.Schedule.Select(slot => slot.LevelId).ToArray()); // E4→E5 排期=e5 行
        Assert.Contains(finalId, tech.CapturedLevelIds); // 终局关占领入树（链收口物证）
    }

    [Fact] // [F-003] legal — E5 全五关（含 e5-final 主宰）真通关→占领→E5→终章 Precheck 放行
    public void E5_full_clear_including_the_final_boss_level_unlocks_the_transition_to_the_finale()
    {
        // T-135 落位（第五跳）：final_levels[5]=campaign:level-e5-final（本任务
        // 落引用）+ 实关行已随 T-134 交付（levels/e5.json slot 5 主宰 Boss 关）
        // ——引用与实关两侧齐备不悬空；era6 目标域实体化（era6 五件套+
        // levels/e6.json+排期三行）后，E5 全五关 headless 真通关即可 Precheck
        // 放行（戴森云奇观=E5 硬前置侧）；排期=target_era=6 行（T-135 落位、
        // 逐值对齐 era6/levels.json 冻结侧）；终章终局关（e6-final 堕落守望者）
        // 不走 final_levels——纪元之门通关之门语义（requires_wonder 数据位+
        // WonderFinaleGateBoard），键域 1..5 封闭。
        var transition = Transition();
        Assert.Equal("campaign:level-e5-final", transition.FinalLevels[5]);
        Assert.Equal(5, transition.FinalLevels.Count); // 无第 6 键（无第七代）

        IReadOnlyList<HandcraftedLevel> levels = LevelsOf("e5.json");
        Assert.Equal(5, levels.Count);
        string finalId = transition.FinalLevels[5];
        Assert.Contains(levels, level => level.Id == finalId); // 引用不悬空（T-134 实关，本任务域引用侧落位）
        Assert.Contains(LevelsOf("e6.json"), level => level.Id == "campaign:level-e6-final"); // 终章目标域实锚（通关之门关）

        // 全五关 headless 真通关（终局关=第 19 波主宰），逐关占领入树。
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 5);
        _ = pool.Deposit(new ResourceKey("era5:res-nano"), 10000);
        _ = pool.Deposit(new ResourceKey("era5:res-rare-earth"), 600);
        _ = pool.Deposit(new ResourceKey("era2:res-iron"), 300);
        var tech = Tree(5);
        foreach (HandcraftedLevel level in levels)
        {
            (int winWaves, long victoryTick, int towers) = PlayThrough(level, "era5", pool);
            Assert.Equal(level.WinWaves, winWaves);
            Assert.True(victoryTick > 0);
            Assert.True(towers > 0);
            tech.RegisterCapture(level.Id);
        }

        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(wonder => wonder.EraIndex == 5).Id); // 戴森云=E5→终章硬前置

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era6"), "era.json")));
        var request = new EraTransitionRequest(
            Seed,
            FromEraIndex: 5,
            pool,
            tech,
            ledger,
            CityFacts(13, 26, 9, Mapping().Orientations),
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(Seed + 5),
            new CountingSavePort(CreatePort()),
            EmptyMeta(Seed, 5),
            Mapping(),
            transition,
            Wonders());

        EraTransitionResult result = new EraTransitionService().Execute(request);

        Assert.Equal(5, result.FromEraIndex);
        Assert.Equal(6, result.ToEraIndex); // Precheck 放行：终局关+奇观双前置皆真（第五跳通）
        Assert.Equal(6, result.NewPool.WarehouseLevel); // 仓库 5+1=6（§5 自动 +1=终章）
        Assert.Equal(
            new[] { "campaign:level-e6-01", "campaign:level-e6-02", "campaign:level-e6-03" },
            result.Schedule.Select(slot => slot.LevelId).ToArray()); // E5→终章排期=e6 行
        Assert.Contains(finalId, tech.CapturedLevelIds); // 终局关占领入树（链收口物证）
    }

    [Fact] // [F-003] illegal — 占领全部非终局关仍硬拒：缺的恰是终局关一行
    public void Capturing_every_non_final_level_still_refuses_every_jump()
    {
        // 占领面用 Precheck 谓词口径（RegisterCapture=循环占领入库的写侧
        // API，EraTransitionIntegrationTests 先例）：各时代的全部非终局关
        // id 逐一注册（id 全部来自真实关卡行）——终局关未占，Precheck 必拒
        // 且点名 final_levels 引用值（T-117 守卫的可达性版：别关都打完也
        // 不能替代终局关；E3/E4/E5 行=T-124/T-134/T-135 扩展——补齐后负向
        // 面同步覆盖）。
        var transition = Transition();
        foreach (var (eraIndex, file, finalId, nextEraDir) in new[]
                 {
                     (1, "e1.json", "campaign:level-e1-final", "era2"),
                     (2, "e2.json", "campaign:level-e2-final", "era3"),
                     (3, "e3.json", "campaign:level-e3-final", "era4"),
                     (4, "e4.json", "campaign:level-e4-final", "era5"),
                     (5, "e5.json", "campaign:level-e5-final", "era6"),
                 })
        {
            var tech = Tree(eraIndex);
            foreach (HandcraftedLevel level in LevelsOf(file).Where(level => level.Id != finalId))
            {
                tech.RegisterCapture(level.Id);
            }

            var ledger = new LegacyLedger(Archaeology(), Shop());
            ledger.MarkWonderBuilt(Wonders().Single(wonder => wonder.EraIndex == eraIndex).Id);

            EraTransitionRequest request = eraIndex == 1
                ? Request(Pool(500, 400, 300), tech, ledger,
                    CityFacts(7, 40, 13, Mapping().Orientations),
                    new CountingSavePort(CreatePort()), EmptyMeta(Seed, 1))
                : new EraTransitionRequest(
                    Seed,
                    FromEraIndex: eraIndex,
                    new ResourceStock(new SimClock(), warehouseLevel: eraIndex),
                    tech,
                    ledger,
                    CityFacts(11, 30, 9, Mapping().Orientations),
                    Anchor,
                    CrossEraResourcePolicy.Parse(
                        File.ReadAllText(Path.Combine(EraDir(nextEraDir), "era.json"))).NewEraResources,
                    new SimClock(),
                    new LcgRng(Seed + 3),
                    new CountingSavePort(CreatePort()),
                    EmptyMeta(Seed, eraIndex),
                    Mapping(),
                    transition,
                    Wonders());

            EraTransitionRejectedException ex = Assert.Throws<EraTransitionRejectedException>(
                () => new EraTransitionService().Execute(request));
            Assert.Contains(finalId, ex.Message, StringComparison.Ordinal); // 点名终局关 id
        }
    }
}
