using System.Diagnostics;
using EraCore.Agent.Autopilot;
using EraCore.Agent.Governance;
using EraCore.Agent.Pipeline;
using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Legacy;
using EraCore.Narrative;
using EraCore.Persistence;
using EraCore.Presentation;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Supply;
using EraCore.Tests.Agent.Actions;
using EraCore.Tests.Agent.Grammar;
using EraCore.Tests.CampaignLoop;
using EraCore.Tests.Legacy;
using EraCore.Tests.Levels;
using EraCore.Tests.Narrative;
using EraCore.Tests.Progression;
using EraCore.Tests.Regression.OffSystemRegression;
using EraCore.Tests.Sim;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Levels.HandcraftedLevelContent;
using static EraCore.Tests.Legacy.LegacyFixture;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Regression.V10FullClear;

// [C-30][F-097][F-003][F-011] W7 出口① v1.0 硬验收——关闭系统全四章自动通关
// E2E 共享驱动（T-084 OffSystemRegression 四关先例扩展至 E1~E4 四时代整链）：
// 系统开关=off，一次链式通关 19 关（e1~e4.json 各 5/4/5/5 关、含四个终局
// Boss 关），每关贯穿生产 LevelSession（T-085 命令流纯玩家建造，零军令/零
// 卷轴），胜后经生产 CampaignProgression 结算（三重目标判分/开箱/首通成就/
// 占领入库），章末走 LegacyLedger 奇观位 + EraTransitionService 跃迁事务
// （E1→E2→E3→E4 三跳串成一条真实战役线）。全程三路审计：军令管线 K-8 事件
// 计数（关闭局=零管线零事件）、警报通道双开关位照发等价+中性载荷、逐 600 tick
// 采样哈希账本；章内叙事拍（StoryChapter 黄金三章）经 NarrativeDirector 照常
// 驱动——无任何军令/管线依赖。静态 Lazy 分章链接：每章测试只强制跑自己的
// 前缀链（E4 测试触发 E1→E4 全链），孪生链（同种子同命令流整链复跑）供确定性
// 用例对照；对照组=开启系统零军令局（E2 抽查一关，T-084 先例缩小口径）。

/// <summary>One level's terminal record inside the v1.0 four-era chain run.</summary>
/// <param name="LevelId">Content id of the handcrafted level row.</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="EraIndex">Owning era (1..4).</param>
/// <param name="Slot">Level slot inside its file (file order).</param>
/// <param name="WinWaves">Win waves achieved (formula + row double face).</param>
/// <param name="EndState">Machine state at run end (Victory expected).</param>
/// <param name="VictoryTick">Tick the victory predicate fired on.</param>
/// <param name="IssuedCommands">Commands the stream issued.</param>
/// <param name="AppliedCommands">Commands the engine applied (zero-refusal face).</param>
/// <param name="TowerCount">Weapon towers standing at victory.</param>
/// <param name="BossMaxHp">Terminal boss MaxHp (final levels only; else null).</param>
/// <param name="BossSpawnTick">Boss spawn tick (final levels only).</param>
/// <param name="LootLines">Per-resource banking lines of the settlement.</param>
/// <param name="StrategyObjectiveId">Judge-drawn strategy objective id.</param>
/// <param name="StrategyPassed">Strategy objective verdict.</param>
/// <param name="ChallengeObjectiveId">Judge-drawn challenge objective id.</param>
/// <param name="ChallengePassed">Challenge objective verdict.</param>
/// <param name="Survived">Survival objective verdict (Victory fact).</param>
/// <param name="CompletedCount">Completed-objective count (1..3).</param>
/// <param name="Tier">Chest tier the count mapped to.</param>
/// <param name="ChestTierId">Chest tier id.</param>
/// <param name="ChestItemIds">Granted decorative item ids (先定后演).</param>
/// <param name="FirstClearGrants">First-clear achievement ids the T-081 hook granted.</param>
/// <param name="HashLedger">Sampled state hashes (StateHashInterval cadence).</param>
/// <param name="FinalStateHash">Full state hash at the victory moment.</param>
internal sealed record V10LevelRecord(
    string LevelId,
    string NameZh,
    int EraIndex,
    int Slot,
    int WinWaves,
    GameFlowState EndState,
    long VictoryTick,
    int IssuedCommands,
    int AppliedCommands,
    int TowerCount,
    int? BossMaxHp,
    long? BossSpawnTick,
    IReadOnlyList<(string Resource, double Granted, double Accepted, double Refused)> LootLines,
    string StrategyObjectiveId,
    bool StrategyPassed,
    string ChallengeObjectiveId,
    bool ChallengePassed,
    bool Survived,
    int CompletedCount,
    ObjectiveTier Tier,
    string ChestTierId,
    IReadOnlyList<string> ChestItemIds,
    IReadOnlyList<string> FirstClearGrants,
    IReadOnlyList<long> HashLedger,
    long FinalStateHash);

/// <summary>One levy line of a chain transition (conservation-audit face).</summary>
/// <param name="Resource">Pooled resource id.</param>
/// <param name="OldAmount">Pool amount before the jump.</param>
/// <param name="Carried">floor(carried_share × old) — travels to the new era.</param>
/// <param name="Retained">OldAmount − Carried (恰 70% 口径留存侧).</param>
/// <param name="FundFolded">Fund units folded out of the carried part (use-less 3:1).</param>
/// <param name="UsefulInNewEra">Whether the target-era policy keeps the resource useful.</param>
internal sealed record V10LevyLine(
    string Resource,
    double OldAmount,
    long Carried,
    double Retained,
    long FundFolded,
    bool UsefulInNewEra);

/// <summary>One executed era jump of the chain (era N full clear → wonder → transaction).</summary>
/// <param name="FromEra">Era being left.</param>
/// <param name="ToEra">Era entered.</param>
/// <param name="WonderId">The era's wonder id marked built before the jump.</param>
/// <param name="WonderNameZh">Wonder Chinese name.</param>
/// <param name="WonderRefusalMessage">Negative-probe message: the jump WITHOUT the wonder built (refusal is structural, not decorative).</param>
/// <param name="Levy">Levy lines with policy flags (conservation faces).</param>
/// <param name="FundGained">Civilization-fund units folded this jump.</param>
/// <param name="OldWarehouseLevel">Old pool warehouse level.</param>
/// <param name="NewWarehouseLevel">New pool warehouse level (+1 per §5).</param>
/// <param name="ScheduleLevelIds">Target-era first-three schedule ids.</param>
/// <param name="CampaignWrites">Campaign-document writes of the transaction (exactly one).</param>
/// <param name="WrittenMetaEraIndex">Era index of the written campaign document.</param>
/// <param name="RelicCityBuildings">Relic-city building facts snapshotted.</param>
/// <param name="DigSiteCount">Degraded-ruin dig sites anchored on the new continent.</param>
internal sealed record V10TransitionRecord(
    int FromEra,
    int ToEra,
    string WonderId,
    string WonderNameZh,
    string WonderRefusalMessage,
    IReadOnlyList<V10LevyLine> Levy,
    long FundGained,
    int OldWarehouseLevel,
    int NewWarehouseLevel,
    IReadOnlyList<string> ScheduleLevelIds,
    int CampaignWrites,
    int WrittenMetaEraIndex,
    int RelicCityBuildings,
    int DigSiteCount);

/// <summary>Narrative face of one era segment: the chapter's golden acts driven by real beats.</summary>
/// <param name="ChapterId">Story chapter id.</param>
/// <param name="BoundLevelId">The chapter's home level id.</param>
/// <param name="ActTicks">Fired tick per act (act order; null=not fired).</param>
/// <param name="ActsFired">Fired-act count (three expected).</param>
/// <param name="LoopWithinBudget">Third-act − first-act span ≤ budget (§12.1).</param>
/// <param name="PhaseEnded">Director reached the Ended phase (handover + tease emitted).</param>
/// <param name="EndingLineCount">Lines the victory beat emitted (acts tail + handover + tease).</param>
/// <param name="WaveStartBeatsFed">Wave-start beats fed from the real spawner.</param>
internal sealed record V10NarrativeRecord(
    string ChapterId,
    string BoundLevelId,
    long?[] ActTicks,
    int ActsFired,
    bool LoopWithinBudget,
    bool PhaseEnded,
    int EndingLineCount,
    int WaveStartBeatsFed);

/// <summary>Live chain state handed from one era segment to the next.</summary>
internal sealed record V10ChainState(
    ResourceStock Pool,
    TechTree Tech,
    LegacyLedger Ledger,
    ICampaignMeta Meta,
    CampaignProgression Progression,
    DevFileStore Files,
    ISavePort LevelPort,
    int EraIndex);

/// <summary>Terminal report of the control spot-check (system ON, zero orders, one E2 level).</summary>
internal sealed record V10ControlSpotRun(
    string LevelId,
    PanelState PanelEndState,
    bool OrderInputVisible,
    int JournalInputEvents,
    int JournalExecutionEvents,
    int JournalTotalEvents,
    bool LedgerNeverSpent,
    int LedgerPointsMax,
    int AlarmParityViolations,
    V10LevelRecord Level);

/// <summary>One era segment of the chain: its levels, its jump (eras 1..3), audits and narrative.</summary>
internal sealed record V10EraSegment(
    int EraIndex,
    string EraDir,
    IReadOnlyList<V10LevelRecord> Levels,
    V10TransitionRecord? Transition,
    V10NarrativeRecord Narrative,
    int JournalTotalEvents,
    int AlarmEvaluations,
    int AlarmParityViolations,
    int NaturalAlarmFired,
    int NeutralTextViolations,
    double DefenseIntegrityMin,
    int OccupiedCells,
    IReadOnlyList<(string Resource, double Amount)> PoolTotalsAfter,
    long LedgerStateHash,
    long FundBalanceAfter,
    IReadOnlyList<string> WondersBuiltAfter,
    int ProgressionCapturedCount,
    int ProgressionCosmeticsCount,
    double WallMillis,
    V10ChainState? NextState);

/// <summary>
/// The v1.0 off-system full-clear chain driver (W7 exit ①). One shared static
/// chain per process: <see cref="Era1"/>..<see cref="Era4"/> are Lazy links
/// (each forces its predecessor), the twin chain <see cref="TwinEra1"/>..
/// <see cref="TwinEra4"/> replays the whole line from scratch for the
/// determinism face, and <see cref="ControlSpot"/> is the on-system zero-order
/// E2 spot-check. All seeds come from the content rows and fixture constants —
/// the two chain runs share every input, so any terminal difference is a
/// determinism break, and the control run shares the E2 level's inputs, so any
/// terminal difference is attributable to the switch alone.
/// </summary>
internal static class V10FullClearChain
{
    /// <summary>Campaign world seed (CampaignLoop fixture family; drives grids + transition continents).</summary>
    public const long WorldSeed = CampaignLoopFixture.WorldSeed;

    /// <summary>Audit sampling cadence (ticks; 600 = 20 s sim time, T-084 rail cadence).</summary>
    public const int SampleTicks = 600;

    /// <summary>State-hash sampling cadence of the session ledger (replay fingerprint).</summary>
    public const int StateHashInterval = 600;

    /// <summary>
    /// 践踏调参 per era (FinalBossLevelTests.Finals 同锚：踏群者 60/铁蹄 100/
    /// 堡垒号 100/夜枭 0——测试侧 [NEW-原型校准] 配置，先例 BossBattle/
    /// IronhoofBattle/FortressBattle；非 Boss 关不消费)。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> TramplePerEra =
        new Dictionary<int, int> { [1] = 60, [2] = 100, [3] = 100, [4] = 0 };

    /// <summary>
    /// Relic-city fact knobs per era (FinalLevelTransitionReachabilityTests 同锚：
    /// 城市事实生成器 count/radius，era1 (7,40)、era2 (11,30)、era3 (13,26)；
    /// era4 无跃迁行——v1.0 链止于 E4 全清+互联网奇观位)。
    /// </summary>
    private static readonly IReadOnlyDictionary<int, (int Count, int Radius)> CityFactsPerEra =
        new Dictionary<int, (int, int)> { [1] = (7, 40), [2] = (11, 30), [3] = (13, 26) };

    /// <summary>Story chapter of one era, straight out of content (narrative face).</summary>
    public static StoryChapter ChapterOf(int eraIndex) => StoryChapter.Parse(
        NarrativeContent.Read($"story-e{eraIndex}.json"), $"narrative/story-e{eraIndex}.json");

    /// <summary>Main-chain segment of one era (1..4; forces its predecessors in order).</summary>
    public static V10EraSegment Era(int eraIndex) => eraIndex switch
    {
        1 => Era1.Value,
        2 => Era2.Value,
        3 => Era3.Value,
        4 => Era4.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(eraIndex), eraIndex, "v1.0 链覆盖 E1..E4"),
    };

    // ---- 静态链：主链（关闭系统）＋孪生链（同输入整链复跑）＋对照组抽查 ------

    /// <summary>Era 1 segment of the main (system-off) chain.</summary>
    public static readonly Lazy<V10EraSegment> Era1 = new(() => RunEra(eraIndex: 1, previous: null, sandboxTag: "main"));

    /// <summary>Era 2 segment of the main chain (forces Era 1 first).</summary>
    public static readonly Lazy<V10EraSegment> Era2 = new(() => RunEra(2, Era1.Value.NextState, "main"));

    /// <summary>Era 3 segment of the main chain (forces Era 2 first).</summary>
    public static readonly Lazy<V10EraSegment> Era3 = new(() => RunEra(3, Era2.Value.NextState, "main"));

    /// <summary>Era 4 segment of the main chain (forces Era 3 first — the full four-era line).</summary>
    public static readonly Lazy<V10EraSegment> Era4 = new(() => RunEra(4, Era3.Value.NextState, "main"));

    /// <summary>Era 1 segment of the twin chain (determinism replay).</summary>
    public static readonly Lazy<V10EraSegment> TwinEra1 = new(() => RunEra(1, null, "twin"));

    /// <summary>Era 2 segment of the twin chain.</summary>
    public static readonly Lazy<V10EraSegment> TwinEra2 = new(() => RunEra(2, TwinEra1.Value.NextState, "twin"));

    /// <summary>Era 3 segment of the twin chain.</summary>
    public static readonly Lazy<V10EraSegment> TwinEra3 = new(() => RunEra(3, TwinEra2.Value.NextState, "twin"));

    /// <summary>Era 4 segment of the twin chain.</summary>
    public static readonly Lazy<V10EraSegment> TwinEra4 = new(() => RunEra(4, TwinEra3.Value.NextState, "twin"));

    /// <summary>Control spot-check: system ON over the E2 home level, zero orders issued.</summary>
    public static readonly Lazy<V10ControlSpotRun> ControlSpot = new(RunControlSpot);

    // ---------------------------------------------------------------- driver

    private static V10EraSegment RunEra(int eraIndex, V10ChainState? previous, string sandboxTag)
    {
        var watch = Stopwatch.StartNew();
        string eraDir = $"era{eraIndex}";
        string levelFile = $"e{eraIndex}.json";

        DevFileStore files;
        ResourceStock pool;
        TechTree tech;
        LegacyLedger ledger;
        ICampaignMeta meta;
        CampaignProgression progression;
        ISavePort levelPort;
        CampaignState campaign;
        if (previous is null)
        {
            string root = Path.Combine(
                DevFileStore.DefaultDevRoot, ".tmp-tests",
                $"v10-{sandboxTag}-{Guid.NewGuid():N}");
            files = new DevFileStore(root);
            campaign = CampaignLoopFixture.NewCampaign(); // T-084 同锚：E1 战役网格+池
            pool = campaign.Pool;
            tech = new TechTree(
                TechTreeData.Parse(ReadCampaignContent("techtree.json")), pool, CampaignState.E1EraIndex);
            ledger = new LegacyLedger(Archaeology(), Shop());
            meta = EmptyMeta(WorldSeed, 1);
            progression = NewProgression(pool, tech);
            levelPort = new SqliteSavePort(files, "saves/profile.db");
        }
        else
        {
            files = previous.Files;
            // 后续时代网格：同旋钮新大陆（薄壳池不承接入库——跃迁 NewPool 才是战役池，
            // 薄壳 E1-only 恢复面=既有生产边界，见 REV 缝注记）。
            campaign = new CampaignState(
                WorldSeed + (eraIndex * 1000),
                new SimRng(WorldSeed + (eraIndex * 1000)),
                CampaignLoopFixture.GridWidth,
                CampaignLoopFixture.GridHeight);
            pool = previous.Pool; // 上一跳 NewPool（仓库 +1、金字塔项随行）
            tech = previous.Tech; // 同一棵树：占领跨章累积、时代根随跃迁 +1
            ledger = previous.Ledger; // 同一本账：奇观/基金/跃迁记录跨章累积
            meta = previous.Meta; // 上一跳写出的 campaign 文档=本跳 PreviousMeta（真实链式）
            progression = previous.Progression; // 同一条结算链：19 关首通/判分/开箱一账到底
            levelPort = previous.LevelPort;
        }

        // —— 开关位（关闭局）：面板机 + 降级矩阵同刻落位（T-084 同锚）。
        var panel = new PanelStateMachine(systemEnabled: false);
        OffModeSurfaces surfaces = OffModeDowngrade.Surfaces(false);
        Assert.Equal(PanelState.Hidden, panel.State);
        Assert.False(surfaces.OrderInputVisible);
        Assert.Equal(AlarmChannel.NeutralBanner, surfaces.Alarm);

        var journal = new CountingJournalWriter();
        var auditLedgerProbe = new SystemPointLedger(eraIndex); // 点账本审计探针：关闭局无管线喂入——恒 0 是"无系统"的账面事实
        int alarmEvaluations = 0;
        int alarmParityViolations = 0;
        int naturalAlarmFired = 0;
        int neutralTextViolations = 0;
        double integrityMin = 1.0;
        double waveEndEfficiencyMin = 1.0;
        double starvedSeconds = 0.0;
        int ledgerObservations = 0;
        bool ledgerPointsAlwaysZero = true;

        StoryChapter chapter = ChapterOf(eraIndex);
        string? awakeningScript = chapter.GoldenActs.Acts
            .FirstOrDefault(act => act.Predicate.Kind == ActPredicateKind.CinematicFired)
            ?.Predicate.ScriptId;
        V10NarrativeRecord? narrative = null;

        var levels = new List<V10LevelRecord>();
        foreach (HandcraftedLevel level in HandcraftedLevelData.Load(levelFile).Items
                     .OrderBy(row => row.Slot))
        {
            NarrativeDirector? director = null;
            if (level.Id == chapter.LevelId)
            {
                // 章节绑定关：叙事拍照常驱动（纯拍事实输入——无军令/无管线依赖）。
                director = new NarrativeDirector(
                    chapter, TrustBadge.Trustworthy, SystemPersona.Advisor, chapter.MockArcFor(level.Id));
                _ = director.Begin();
            }

            SimulationConfig simConfig = BuildEraConfig(level, eraIndex);
            IReadOnlyList<SimCommand> commands = BuildCommands(level, eraDir);
            var levelConfig = new LevelSessionConfig(
                simConfig, $"v10.{level.Id}", level.Threat, level.Map.Seed, StateHashInterval);
            var session = new LevelSession(levelConfig, new SimRng(level.Map.Seed), levelPort, pool);

            session.Machine.StartLevel();
            foreach (SimCommand command in commands)
            {
                session.Enqueue(command);
            }

            long intervalTicks = (long)Math.Round(
                EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
                MidpointRounding.AwayFromZero);
            long graceTicks = (long)Math.Round(
                intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
            long horizon = graceTicks + ((level.WinWaves + 12) * intervalTicks); // 胜波+12 波窗（失败护栏，通关即停）

            long lastSampleTick = 0;
            int lastWave = session.Sim.CurrentWave;
            int waveStartBeatsFed = 0;
            bool narrativeVictoryFed = false;
            long steps = 0;
            while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
            {
                session.RunFor(SampleTicks);
                steps += SampleTicks;
                long tick = session.Sim.CurrentTick;
                double seconds = (tick - lastSampleTick) / (double)Simulation.TicksPerSecond;

                if (session.Sim.CurrentWave != lastWave)
                {
                    lastWave = session.Sim.CurrentWave;
                    waveStartBeatsFed++;
                    waveEndEfficiencyMin = Math.Min(waveEndEfficiencyMin, AverageEfficiency(session.Sim));
                    if (director is not null)
                    {
                        _ = director.OnWaveStart(lastWave, tick);
                        if (lastWave == 2 && awakeningScript is not null)
                        {
                            // 绑定幕：觉醒演出随波 2 钩子拍触发（NarrativeIntegrationTests 同锚）。
                            _ = director.OnCinematicFired(awakeningScript, tick);
                        }
                    }
                }

                if (session.Machine.State == GameFlowState.Victory && director is not null && !narrativeVictoryFed)
                {
                    narrativeVictoryFed = true;
                    int endingLines = director.OnVictory(tick).Count;
                    narrative = new V10NarrativeRecord(
                        chapter.Id,
                        chapter.LevelId,
                        chapter.GoldenActs.Acts.Select(act => director.Golden.TickOf(act.Id)).ToArray(),
                        director.ActsCompleted,
                        director.Golden.WithinBudget,
                        director.Phase == NarrativePhase.Ended,
                        endingLines,
                        waveStartBeatsFed);
                }

                // —— 警报通道（真实塔线读数）：双开关位同刻评估+关闭态中性载荷。
                HudSnapshot hud = HudMetrics.Compute(
                    session.Sim.CurrentWave,
                    0.0,
                    session.Sim.Buildings
                        .Where(building => building.Kind == SimBuildingKind.Tower)
                        .Select(building => (building.Hp, building.MaxHpResolved))
                        .ToList());
                integrityMin = Math.Min(integrityMin, hud.DefenseIntegrity);
                DefenseAlarmView offView = OffModeDowngrade.DefenseAlarmAt(false, hud.DefenseIntegrity);
                DefenseAlarmView onView = OffModeDowngrade.DefenseAlarmAt(true, hud.DefenseIntegrity);
                alarmEvaluations++;
                if (offView.Fires != onView.Fires)
                {
                    alarmParityViolations++;
                }

                if (offView.Fires)
                {
                    naturalAlarmFired++;
                    if (!System.Text.RegularExpressions.Regex.IsMatch(offView.Text, "^防线完整度 [0-9]+%$")
                        || offView.Text.Contains("建议", StringComparison.Ordinal)
                        || offView.Text.Contains('！'))
                    {
                        neutralTextViolations++;
                    }
                }

                // —— 弹药审计（效率缺口累积，T-084 采样上界口径）。
                starvedSeconds += SupplyGapSeconds(session.Sim, seconds);

                // —— 系统点审计：关闭局无管线回充/无动作消耗——恒 0。
                ledgerObservations++;
                if (auditLedgerProbe.Points != 0)
                {
                    ledgerPointsAlwaysZero = false;
                }

                lastSampleTick = tick;
            }

            Assert.True(session.Machine.State == GameFlowState.Victory && ledgerPointsAlwaysZero,
                $"未通关或系统点账本非零（state={session.Machine.State}）: {level.Id} wave={session.Sim.CurrentWave} " +
                $"tick={session.Sim.CurrentTick} issued={session.IssuedCommands.Count} " +
                $"applied={session.Sim.AppliedCommandCount} towers={session.Sim.Towers.Count} " +
                $"alive={session.Sim.Units.Count(unit => unit.Alive)} " +
                $"aliveHp={session.Sim.Units.Where(unit => unit.Alive).Sum(unit => unit.Hp)}");

            // —— 首波漏敌审计（波 1 入场窗内 ArrivedAtTarget 粘滞位）。
            int firstWaveLeaks = session.Sim.Units.Count(
                unit => unit.ArrivedAtTarget && unit.SpawnTick < graceTicks + intervalTicks);

            // —— 主机审计计数：真实终局实况（E3/E4 过热/践踏可致损——按终局 HP 实测）。
            int deadBuildings = session.Sim.Buildings.Count(building => building.Hp <= 0);
            int damagedBuildings = session.Sim.Buildings.Count(building => building.Hp < building.MaxHpResolved);
            var hostCounters = new HostRunCounters(
                StationsLost: deadBuildings, // 采集面=终局实况（全存活=0 有读数背书）
                DefenseIntegrityMin: integrityMin,
                TowersSoldCount: 0, // 命令流无出售动词
                AmmoStarvedSeconds: starvedSeconds,
                WaveEndEfficiencyMin: waveEndEfficiencyMin,
                KeyBuildingsDamagedCount: damagedBuildings, // 主城/仓库面=终局实扣读数
                LogisticsDeadlockCount: 0,
                SystemBuildPlanCount: journal.ExecutionEvents, // C4 审计面=K-8 动作事件计数（关闭局=0）
                ScrollTriggerCount: 0, // 命令流无卷轴动词
                FirstWaveLeaks: firstWaveLeaks);

            GameFlowState endState = session.Machine.State;
            long finalHash = session.Sim.ComputeStateHash();
            IReadOnlyList<long> hashLedger = session.HashLedger.Select(sample => sample.Hash).ToList();

            int? bossMaxHp = null;
            long? bossSpawnTick = null;
            if (level.Waves.BossEnemyId is not null)
            {
                EraCore.Units.Unit boss = Assert.Single(session.Sim.Units, unit => unit.IsBoss);
                Assert.False(boss.Alive); // Boss 必死（Victory=全灭场清）
                bossMaxHp = boss.MaxHp;
                bossSpawnTick = boss.SpawnTick;
            }

            CampaignSettlement settlement = SettlementOf(session, progression, level, hostCounters);
            IReadOnlyList<string> firstClear = AutopilotPenalties.FilterFirstClearAchievements(
                new AutopilotParticipation(LevelParticipated: false, WaveParticipated: false, AutopilotMode.Off),
                new[] { "first-clear:" + level.Id });

            session.CompleteLevel();
            session.ClearExitSnapshot();

            LevelNodeInfo node = PickNextNode(campaign.Grid);
            _ = campaign.Grid.Occupy(node.Cell.X, node.Cell.Y);

            levels.Add(new V10LevelRecord(
                level.Id,
                level.NameZh,
                eraIndex,
                level.Slot,
                session.WinWaves,
                endState,
                session.Sim.CurrentTick,
                session.IssuedCommands.Count,
                (int)session.Sim.AppliedCommandCount,
                session.Sim.Towers.Count,
                bossMaxHp,
                bossSpawnTick,
                settlement.Loot.Select(line =>
                    (line.Resource.Value, Math.Round(line.Granted, 6), Math.Round(line.Accepted, 6), Math.Round(line.Refused, 6))).ToList(),
                settlement.Objectives.Draw.Strategy.Id,
                settlement.Objectives.StrategyPassed,
                settlement.Objectives.Draw.Challenge.Id,
                settlement.Objectives.ChallengePassed,
                settlement.Objectives.Survived,
                settlement.Objectives.CompletedCount,
                settlement.Objectives.Tier,
                settlement.ChestTierId,
                settlement.ChestItems.Select(item => item.ItemId).ToList(),
                firstClear,
                hashLedger,
                finalHash));
        }

        // —— 章末：奇观位 + 跃迁事务（E1~E3；E4 收于全清+互联网奇观位）。
        V10TransitionRecord? transition = null;
        V10ChainState next;
        if (eraIndex <= 3)
        {
            WonderSpec wonder = Wonders().Single(row => row.EraIndex == eraIndex);

            // illegal probe：奇观未建 → 硬拒（拒绝零改动——奇观是链的真前置，非装饰）。
            EraTransitionRequest bareRequest = BuildTransitionRequest(eraIndex, pool, tech, ledger, meta, files);
            EraTransitionRejectedException refused = Assert.Throws<EraTransitionRejectedException>(
                () => new EraTransitionService().Execute(bareRequest));

            ledger.MarkWonderBuilt(wonder.Id);
            EraTransitionRequest request = BuildTransitionRequest(eraIndex, pool, tech, ledger, meta, files);
            var countingPort = (CountingSavePort)request.Port;
            EraTransitionResult result = new EraTransitionService().Execute(request);

            IReadOnlySet<string> newEraResources = CrossEraResourcePolicy.Parse(
                File.ReadAllText(Path.Combine(EraDir($"era{eraIndex + 1}"), "era.json"))).NewEraResources
                .Select(resource => resource.Value)
                .ToHashSet();
            transition = new V10TransitionRecord(
                eraIndex,
                result.ToEraIndex,
                wonder.Id,
                wonder.NameZh,
                refused.Message,
                result.Levy
                    .Select(line => new V10LevyLine(
                        line.Resource.Value,
                        line.OldAmount,
                        line.Carried,
                        line.Retained,
                        line.FundFolded,
                        newEraResources.Contains(line.Resource.Value)))
                    .ToList(),
                result.FundGained,
                pool.WarehouseLevel,
                result.NewPool.WarehouseLevel,
                result.Schedule.Select(slot => slot.LevelId).ToList(),
                countingPort.CampaignWrites,
                ((CampaignMetaDto)result.WrittenMeta).EraIndex,
                result.Snapshot.Buildings.Count,
                result.DigSites.Sites.Count);
            next = new V10ChainState(
                result.NewPool, tech, ledger, result.WrittenMeta, progression, files, levelPort, result.ToEraIndex);
        }
        else
        {
            // E4 收口：互联网奇观位落账（四奇观全建=附录 F.3 奇观链闭环）。
            ledger.MarkWonderBuilt(Wonders().Single(row => row.EraIndex == 4).Id);
            next = new V10ChainState(pool, tech, ledger, meta, progression, files, levelPort, 4);
        }

        watch.Stop();
        return new V10EraSegment(
            eraIndex,
            eraDir,
            levels,
            transition,
            narrative ?? new V10NarrativeRecord(
                chapter.Id, chapter.LevelId, Array.Empty<long?>(), 0, false, false, 0, 0),
            journal.TotalEvents,
            alarmEvaluations,
            alarmParityViolations,
            naturalAlarmFired,
            neutralTextViolations,
            integrityMin,
            campaign.Grid.OccupiedCells.Count,
            pool.Resources.Select(resource => (resource.Value, Math.Round(pool.AmountOf(resource), 6))).ToList(),
            ledger.ComputeStateHash(),
            ledger.Fund.Balance,
            ledger.BuiltWonderIds.ToList(),
            progression.CapturedLevelIds.Count,
            progression.Cosmetics.Count,
            watch.Elapsed.TotalMilliseconds,
            next);
    }

    /// <summary>Progression over the shared pool/tech (judge/chests seeded once per chain — replay-stable).</summary>
    private static CampaignProgression NewProgression(ResourceStock pool, TechTree tech) => new(
        pool,
        tech,
        new TripleObjectiveJudge(
            ObjectivesData.Parse(ReadCampaignContent("objectives.json")), new SimRng(ProgressionFixture.Seed)),
        new ChestRewards(
            ChestRewardsData.Parse(ReadCampaignContent("chests.json")), new SimRng(ProgressionFixture.Seed)));

    private static CampaignSettlement SettlementOf(
        LevelSession session, CampaignProgression progression, HandcraftedLevel level, HostRunCounters host)
    {
        LevelRunFacts facts = CampaignProgression.ObserveFacts(
            session, level.BudgetSeconds, researchCompletedCount: 0, host);
        return progression.SettleVictory(session, level.Id, level.Threat, facts);
    }

    /// <summary>Era assembly: T-085 BuildConfig + 终局关践踏调参（FinalBossLevelTests.Finals 同锚）+ E3/E4 能源风险表（Era3/Era4LevelPlaythroughTests 同口径）。</summary>
    private static SimulationConfig BuildEraConfig(HandcraftedLevel level, int eraIndex)
    {
        SimulationConfig config = FinalBossLevelTests.BuildBossConfig(level, $"era{eraIndex}", TramplePerEra[eraIndex]);
        return eraIndex switch
        {
            3 => config with { EnergyRisk = Era3EnergyRisk() },
            4 => config with
            {
                EnergyRisk = Era4EnergyRisk(),
                RadiationZoneHpDecayPerSecond = Era4RadiationZoneHpDecayPerSecond(),
            },
            _ => config,
        };
    }

    private static EraTransitionRequest BuildTransitionRequest(
        int eraIndex,
        ResourceStock pool,
        TechTree tech,
        LegacyLedger ledger,
        ICampaignMeta meta,
        DevFileStore files)
    {
        var port = new CountingSavePort(new SqliteSavePort(files, $"saves/transition-e{eraIndex}.db"));
        (int count, int radius) = CityFactsPerEra[eraIndex];
        IReadOnlySet<ResourceKey> newEraResources = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir($"era{eraIndex + 1}"), "era.json"))).NewEraResources;
        return new EraTransitionRequest(
            WorldSeed,
            FromEraIndex: eraIndex,
            pool,
            tech,
            ledger,
            CityFacts(WorldSeed + eraIndex, count, radius, Mapping().Orientations),
            Anchor,
            newEraResources,
            new SimClock(),
            new LcgRng(WorldSeed + eraIndex),
            port,
            meta,
            Mapping(),
            Transition(),
            Wonders());
    }

    /// <summary>Deterministic next-node pick (T-084 同锚：fewest adjacent camps, then row-major).</summary>
    private static LevelNodeInfo PickNextNode(CampaignGrid grid)
    {
        IReadOnlyList<CampaignCell> selectable = grid.SelectableCells();
        Assert.NotEmpty(selectable);
        CampaignCell best = selectable
            .OrderBy(cell => grid.NodeInfo(cell.X, cell.Y).AdjacentEnemyCamps)
            .ThenBy(cell => cell.Y)
            .ThenBy(cell => cell.X)
            .First();
        return grid.NodeInfo(best.X, best.Y);
    }

    private static double AverageEfficiency(Simulation sim)
    {
        var efficiencies = new List<double>();
        foreach (SimTower tower in sim.Towers)
        {
            if (sim.TryGetFirePacer(tower.Id, out SuppliedWeaponFire pacer))
            {
                efficiencies.Add(pacer.Efficiency);
            }
        }

        return efficiencies.Count == 0 ? 1.0 : efficiencies.Average();
    }

    private static double SupplyGapSeconds(Simulation sim, double seconds)
    {
        double gap = 0.0;
        foreach (SimTower tower in sim.Towers)
        {
            if (sim.TryGetFirePacer(tower.Id, out SuppliedWeaponFire pacer))
            {
                gap += Math.Max(0.0, 1.0 - pacer.Efficiency);
            }
        }

        return gap * seconds;
    }

    // ---------------------------------------------------------------- control spot

    /// <summary>Control run: E2 home level with the advisor constructed over the live sim, zero orders issued (T-084 control 先例缩小到一关).</summary>
    private static V10ControlSpotRun RunControlSpot()
    {
        // 主链 E2-01 行 = 对照基准（先跑主链 E1/E2 前缀——Lazy 链保证同进程一次）。
        V10EraSegment era2 = Era2.Value;
        V10LevelRecord baseline = era2.Levels.Single(row => row.Slot == 1);
        V10TransitionRecord jump = era2.Transition!;
        HandcraftedLevel level = HandcraftedLevelData.Load("e2.json").TryGet(baseline.LevelId)!;

        string root = Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", $"v10-control-{Guid.NewGuid():N}");
        var files = new DevFileStore(root);
        try
        {
            // 入池镜像=跃迁 NewPool 事实（仓库 2+金字塔项随行、带入资源=带走侧有用行）。
            var pool = new ResourceStock(new SimClock(), jump.NewWarehouseLevel, pyramidBuilt: true);
            foreach (V10LevyLine line in jump.Levy.Where(line => line.UsefulInNewEra && line.FundFolded == 0))
            {
                _ = pool.Deposit(new ResourceKey(line.Resource), line.Carried);
            }

            SimulationConfig simConfig = BuildEraConfig(level, 2);
            IReadOnlyList<SimCommand> commands = BuildCommands(level, "era2");
            var journal = new CountingJournalWriter();
            var session = new LevelSession(
                new LevelSessionConfig(simConfig, $"v10.control.{level.Id}", level.Threat, level.Map.Seed, StateHashInterval),
                new SimRng(level.Map.Seed),
                new SqliteSavePort(files, "saves/profile.db"),
                pool);

            var panel = new PanelStateMachine(systemEnabled: true);
            OffModeSurfaces surfaces = OffModeDowngrade.Surfaces(true);

            // 军师在场（管线挂上同一活体模拟，era=2 点经济开启），玩家整局不下令。
            AgentPipeline pipeline = AgentPipeline.OverSimulation(
                session.Sim,
                simConfig,
                L1IntentParserTests.CreateParser(),
                eraIndex: 2,
                journal,
                new CapturingLogSink(),
                new Dictionary<string, CellCoord> { ["east_gate"] = new(12, 12) });

            session.Machine.StartLevel();
            foreach (SimCommand command in commands)
            {
                session.Enqueue(command);
            }

            long intervalTicks = (long)Math.Round(
                EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
                MidpointRounding.AwayFromZero);
            long graceTicks = (long)Math.Round(
                intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
            long horizon = graceTicks + ((level.WinWaves + 12) * intervalTicks);

            int alarmParityViolations = 0;
            int pointsMax = 0;
            double integrityMin = 1.0;
            double waveEndEfficiencyMin = 1.0;
            double starvedSeconds = 0.0;
            long lastSampleTick = 0;
            int lastWave = session.Sim.CurrentWave;
            long steps = 0;
            while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
            {
                session.RunFor(SampleTicks);
                steps += SampleTicks;
                long tick = session.Sim.CurrentTick;
                double seconds = (tick - lastSampleTick) / (double)Simulation.TicksPerSecond;
                lastSampleTick = tick;

                if (session.Sim.CurrentWave != lastWave)
                {
                    lastWave = session.Sim.CurrentWave;
                    pipeline.Ledger.BeginWave(lastWave, isBossWave: false);
                    waveEndEfficiencyMin = Math.Min(waveEndEfficiencyMin, AverageEfficiency(session.Sim));
                }

                HudSnapshot hud = HudMetrics.Compute(
                    session.Sim.CurrentWave,
                    0.0,
                    session.Sim.Buildings
                        .Where(building => building.Kind == SimBuildingKind.Tower)
                        .Select(building => (building.Hp, building.MaxHpResolved))
                        .ToList());
                integrityMin = Math.Min(integrityMin, hud.DefenseIntegrity);
                if (OffModeDowngrade.DefenseAlarmAt(false, hud.DefenseIntegrity).Fires
                    != OffModeDowngrade.DefenseAlarmAt(true, hud.DefenseIntegrity).Fires)
                {
                    alarmParityViolations++;
                }

                starvedSeconds += SupplyGapSeconds(session.Sim, seconds);
                pointsMax = Math.Max(pointsMax, pipeline.Ledger.Points);
            }

            Assert.True(session.Machine.State == GameFlowState.Victory,
                $"对照局未通关: state={session.Machine.State} wave={session.Sim.CurrentWave} tick={session.Sim.CurrentTick}");

            int firstWaveLeaks = session.Sim.Units.Count(
                unit => unit.ArrivedAtTarget && unit.SpawnTick < graceTicks + intervalTicks);
            var hostCounters = new HostRunCounters(
                StationsLost: session.Sim.Buildings.Count(building => building.Hp <= 0),
                DefenseIntegrityMin: integrityMin,
                TowersSoldCount: 0,
                AmmoStarvedSeconds: starvedSeconds,
                WaveEndEfficiencyMin: waveEndEfficiencyMin,
                KeyBuildingsDamagedCount: session.Sim.Buildings.Count(building => building.Hp < building.MaxHpResolved),
                LogisticsDeadlockCount: 0,
                SystemBuildPlanCount: journal.ExecutionEvents,
                ScrollTriggerCount: 0,
                FirstWaveLeaks: firstWaveLeaks);

            // 结算走同种子判分/开箱（ProgressionFixture 种子家族——判分同抽对照锚）。
            GameFlowState controlEndState = session.Machine.State;
            var tech = new TechTree(TechTreeData.Parse(ReadCampaignContent("techtree.json")), pool, eraIndex: 2);
            CampaignProgression progression = NewProgression(pool, tech);
            CampaignSettlement settlement = SettlementOf(session, progression, level, hostCounters);
            IReadOnlyList<string> firstClear = AutopilotPenalties.FilterFirstClearAchievements(
                new AutopilotParticipation(LevelParticipated: false, WaveParticipated: false, AutopilotMode.Off),
                new[] { "first-clear:" + level.Id });
            session.CompleteLevel();
            session.ClearExitSnapshot();

            var controlLevel = new V10LevelRecord(
                level.Id,
                level.NameZh,
                2,
                level.Slot,
                session.WinWaves,
                controlEndState,
                session.Sim.CurrentTick,
                session.IssuedCommands.Count,
                (int)session.Sim.AppliedCommandCount,
                session.Sim.Towers.Count,
                null,
                null,
                settlement.Loot.Select(line =>
                    (line.Resource.Value, Math.Round(line.Granted, 6), Math.Round(line.Accepted, 6), Math.Round(line.Refused, 6))).ToList(),
                settlement.Objectives.Draw.Strategy.Id,
                settlement.Objectives.StrategyPassed,
                settlement.Objectives.Draw.Challenge.Id,
                settlement.Objectives.ChallengePassed,
                settlement.Objectives.Survived,
                settlement.Objectives.CompletedCount,
                settlement.Objectives.Tier,
                settlement.ChestTierId,
                settlement.ChestItems.Select(item => item.ItemId).ToList(),
                firstClear,
                session.HashLedger.Select(sample => sample.Hash).ToList(),
                session.Sim.ComputeStateHash());

            return new V10ControlSpotRun(
                level.Id,
                panel.State,
                surfaces.OrderInputVisible,
                journal.InputEvents,
                journal.ExecutionEvents,
                journal.TotalEvents,
                pipeline.Ledger.WaveSpent == 0,
                pointsMax,
                alarmParityViolations,
                controlLevel);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
