using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Tests.CampaignLoop;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Legacy;
using EraCore.Tests.Persistence;
using EraCore.Tests.Progression;
using EraCore.Tests.Sim;
using EraCore.Units;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;

namespace EraCore.Tests.Levels;

// [C-29][F-005][F-003][F-001] T-160 战役宿主面验收②——E1→E2 连续推进链：
// 经生产组装律（EraLevelAssembly——表现层 CampaignMenuUi 选关开局走的正是
// 这同一条路径）从 e1-01 连续通关到 e1-final（含终局 Boss 关），章末走真实
// 时代奇观位+EraTransitionService 原子事务（CityFacts=e1-final 终局实况存续
// 建筑——非 V10 链的合成校准事实，跃迁快照第一次吃到真城市），CampaignState.
// AdvanceEra 落位时代 2 后，e2-01 经同一路径开局并真通关（下一时代可玩性
// 的直接实证；E2→E6 为同构代码路径+装载矩阵全量面）。解锁推进输入面（菜单
// 进度态的三源）随链断言：占领集逐关增长、终局关占领后事务放行、时代位
// 1→2、跃迁排期=e2 前三关。V10FullClearChain 已证 E1→E4（测试侧组装律），
// 本链证的是「生产组装律+真城市事实」——T-160 的宿主面口径。
[Collection(CheckpointHeavyCollection.Name)]
public class CampaignHostProgressionChainTests : SqliteSavePortTestBase
{
    private static HandcraftedLevel LevelOf(string file, string id) =>
        HandcraftedLevelData.Load(file).TryGet(id)
        ?? throw new InvalidOperationException($"缺关卡行 {id}");

    private static long HorizonTicks(HandcraftedLevel level)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((level.WinWaves + 12) * intervalTicks);
    }

    [Fact]
    public void E1_full_clear_then_real_transition_unlocks_and_plays_E2_01()
    {
        string root = Path.Combine(DevFileStore.DefaultDevRoot, ".tmp-tests", $"t160-chain-{Guid.NewGuid():N}");
        var files = new DevFileStore(root);
        try
        {
            var port = new SqliteSavePort(files, "saves/campaign.db");
            var campaign = new CampaignState(
                CampaignLoopFixture.WorldSeed,
                new SimRng(CampaignLoopFixture.WorldSeed),
                CampaignLoopFixture.GridWidth,
                CampaignLoopFixture.GridHeight);
            var tech = new TechTree(
                TechTreeData.Parse(File.ReadAllText(Path.Combine(ContentRoot(), "campaign", "techtree.json"))),
                campaign.Pool,
                CampaignState.E1EraIndex);
            var ledger = new LegacyLedger(Archaeology(), Shop());
            var progression = new CampaignProgression(
                campaign.Pool,
                tech,
                new TripleObjectiveJudge(
                    ObjectivesData.Parse(File.ReadAllText(Path.Combine(ContentRoot(), "campaign", "objectives.json"))),
                    new SimRng(ProgressionFixture.Seed)),
                new ChestRewards(
                    ChestRewardsData.Parse(File.ReadAllText(Path.Combine(ContentRoot(), "campaign", "chests.json"))),
                    new SimRng(ProgressionFixture.Seed)));
            ICampaignMeta meta = EmptyMeta(CampaignLoopFixture.WorldSeed, 1);

            var captured = new List<string>();
            LevelSession? finalSession = null;
            foreach (HandcraftedLevel level in HandcraftedLevelData.Load("e1.json").Items.OrderBy(row => row.Slot))
            {
                LevelSession session = PlayThrough(port, campaign.Pool, level);
                Settle(progression, session, level);
                captured.Add(level.Id);
                if (level.Id.EndsWith("-final", StringComparison.Ordinal))
                {
                    finalSession = session; // 终局城市事实载体（跃迁快照输入）
                }

                session.CompleteLevel();
                session.ClearExitSnapshot();

                // 解锁推进输入面：占领集含全部已通关（菜单「已通关★」与章内
                // 顺序解锁的共同事实源）。
                Assert.All(captured, id => Assert.Contains(id, progression.CapturedLevelIds));
            }

            Assert.Equal(5, progression.CapturedLevelIds.Count);
            Assert.Contains("campaign:level-e1-final", progression.CapturedLevelIds);

            // —— 章末：奇观位 + 真城市跃迁事务（CityFacts=本链 e1-final 终局实况
            //    存续建筑——非 V10 链的合成校准事实）——
            WonderSpec wonder = Wonders().Single(row => row.EraIndex == 1);
            IReadOnlyList<RelicCityBuildingFact> cityFacts = finalSession!.Sim.Buildings
                .Where(building => building.Kind != SimBuildingKind.Warehouse)
                .Select(building => new RelicCityBuildingFact(
                    building.Id.Value, building.ContentId, building.Tier, Orientation: 0, building.Cell))
                .ToList();
            Assert.True(cityFacts.Count >= 5, $"终局关存续建筑应 ≥5（实际 {cityFacts.Count}）——快照事实非空集");

            EraTransitionRejectedException refused = Assert.Throws<EraTransitionRejectedException>(
                () => new EraTransitionService().Execute(TransitionRequest(1, campaign, tech, ledger, meta, files, cityFacts)));
            Assert.Contains("奇观", refused.Message, StringComparison.Ordinal);

            ledger.MarkWonderBuilt(wonder.Id);
            var countingPort = new CountingSavePort(port);
            EraTransitionResult result = new EraTransitionService().Execute(
                TransitionRequest(1, campaign, tech, ledger, meta, files, cityFacts, countingPort));
            Assert.Equal(1, countingPort.CampaignWrites); // campaign 域恰一次写
            Assert.Equal(2, result.ToEraIndex);
            Assert.Equal(2, result.NewPool.WarehouseLevel);

            campaign.AdvanceEra(result);
            Assert.Equal(2, campaign.EraIndex); // 菜单「下一时代章节解锁」的时代位事实

            // 跃迁排期=e2 前三关（新章首三行的宿主面元数据）。
            Assert.Equal(
                new[] { "campaign:level-e2-01", "campaign:level-e2-02", "campaign:level-e2-03" },
                result.Schedule.Select(slot => slot.LevelId).ToArray());

            // —— E2-01 经同一条生产路径开局并真通关（下一时代可玩性实证）——
            HandcraftedLevel e201 = LevelOf("e2.json", "campaign:level-e2-01");
            LevelSession e2Session = PlayThrough(port, campaign.Pool, e201);
            Assert.Equal(GameFlowState.Victory, e2Session.Machine.State);
            Settle(progression, e2Session, e201);
            e2Session.CompleteLevel();
            e2Session.ClearExitSnapshot();
            Assert.Contains("campaign:level-e2-01", progression.CapturedLevelIds);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>生产组装律开局一关并真通关（命令流=关卡脚本，V10 采样律推进）.</summary>
    private static LevelSession PlayThrough(ISavePort port, ResourceStock pool, HandcraftedLevel level)
    {
        SimulationConfig config = EraLevelAssembly.BuildConfig(level, ContentRoot());
        IReadOnlyList<SimCommand> commands = EraLevelAssembly.BuildCommands(level, ContentRoot());
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t160.{level.Id}", level.Threat, level.Map.Seed, 600),
            new SimRng(level.Map.Seed),
            port,
            pool);
        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        long horizon = HorizonTicks(level);
        long steps = 0;
        while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
        {
            session.RunFor(600);
            steps += 600;
        }

        Assert.True(session.Machine.State == GameFlowState.Victory,
            $"未通关: {level.Id} state={session.Machine.State} wave={session.Sim.CurrentWave} " +
            $"tick={session.Sim.CurrentTick} issued={session.IssuedCommands.Count} " +
            $"applied={session.Sim.AppliedCommandCount} towers={session.Sim.Towers.Count} " +
            $"alive={session.Sim.Units.Count(unit => unit.Alive)}");
        return session;
    }

    /// <summary>胜利结算（判分/开箱/占领——V10 SettlementOf 同律）.</summary>
    private static void Settle(CampaignProgression progression, LevelSession session, HandcraftedLevel level)
    {
        var counters = new HostRunCounters(
            StationsLost: session.Sim.Buildings.Count(building => building.Hp <= 0),
            DefenseIntegrityMin: 1.0,
            TowersSoldCount: 0,
            AmmoStarvedSeconds: 0.0,
            WaveEndEfficiencyMin: 1.0,
            KeyBuildingsDamagedCount: session.Sim.Buildings.Count(building => building.Hp < building.MaxHpResolved),
            LogisticsDeadlockCount: 0,
            SystemBuildPlanCount: 0,
            ScrollTriggerCount: 0,
            FirstWaveLeaks: 0);
        LevelRunFacts facts = CampaignProgression.ObserveFacts(session, level.BudgetSeconds, 0, counters);
        _ = progression.SettleVictory(session, level.Id, level.Threat, facts);
    }

    private static EraTransitionRequest TransitionRequest(
        int fromEra,
        CampaignState campaign,
        TechTree tech,
        LegacyLedger ledger,
        ICampaignMeta meta,
        DevFileStore files,
        IReadOnlyList<RelicCityBuildingFact> cityFacts,
        ISavePort? port = null)
    {
        IReadOnlySet<ResourceKey> newEraResources = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(ContentRoot(), $"era{fromEra + 1}", "era.json"))).NewEraResources;
        return new EraTransitionRequest(
            CampaignLoopFixture.WorldSeed,
            FromEraIndex: fromEra,
            campaign.Pool,
            tech,
            ledger,
            cityFacts,
            new CellCoord(24, 24), // ci-allow: R7 跃迁快照锚（遗迹城核心带中心，FinalLevelTransitionReachability 同族结构位）
            newEraResources,
            new SimClock(),
            new SimRng(CampaignLoopFixture.WorldSeed + fromEra),
            port ?? new SqliteSavePort(files, $"saves/transition-e{fromEra}.db"),
            meta,
            Mapping(),
            Transition(),
            Wonders());
    }
}
