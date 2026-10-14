using System.Diagnostics;
using EraCore.Agent.Autopilot;
using EraCore.Agent.Governance;
using EraCore.Agent.Pipeline;
using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Persistence;
using EraCore.Presentation;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Supply;
using EraCore.Tests.Agent.Actions;
using EraCore.Tests.Agent.Grammar;
using EraCore.Tests.Sim;
using EraCore.Tests.CampaignLoop;
using EraCore.Tests.Levels;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Regression.OffSystemRegression;

// T-084 (F-097, R2 硬标准首证) 关闭系统 E1 自动通关回归——共享驱动：
// headless 程序驱动一次完整 E1 战役（T-085 e1.json 四关，纯玩家命令流，
// 零军令/零系统点），贯穿 CampaignLoop（选图→进关→守波→Victory→结算→
// 占领）与 Progression（三重目标判分/开箱/首通成就/装饰入账），全程挂三路
// 审计：军令管线 K-8 事件计数、系统点账本余额观察、警报中性通道读数流。
// 同一驱动以 systemEnabled=true 复跑即为对照组（军师在场但玩家不下令）。

/// <summary>One level's terminal record inside an off-system campaign run.</summary>
/// <param name="LevelId">Content id of the handcrafted level row.</param>
/// <param name="Slot">Level slot (1..4, file order).</param>
/// <param name="Threat">Level row threat (drives WinWaves).</param>
/// <param name="WinWaves">Win waves of the run (formula + row double face).</param>
/// <param name="EndState">Machine state at run end (Victory expected).</param>
/// <param name="VictoryTick">Tick the victory predicate fired on.</param>
/// <param name="LootLines">Per-resource banking lines of the settlement.</param>
/// <param name="StrategyObjectiveId">The judge's drawn strategy objective id.</param>
/// <param name="StrategyPassed">Strategy objective verdict.</param>
/// <param name="ChallengeObjectiveId">The judge's drawn challenge objective id.</param>
/// <param name="ChallengePassed">Challenge objective verdict.</param>
/// <param name="Survived">Survival objective verdict (Victory fact).</param>
/// <param name="CompletedCount">Completed-objective count (0..3).</param>
/// <param name="Tier">Chest tier the count mapped to.</param>
/// <param name="ChestTierId">Chest tier id.</param>
/// <param name="ChestItemIds">Granted decorative item ids (先定后演).</param>
/// <param name="FirstClearGrants">First-clear achievement ids the T-081 hook granted.</param>
/// <param name="OccupiedCell">Campaign-grid node occupied after the victory.</param>
/// <param name="HashLedger">Sampled state hashes (StateHashInterval cadence).</param>
/// <param name="FinalStateHash">Full state hash at the victory moment.</param>
/// <param name="TowerCount">Weapon towers standing at victory.</param>
public sealed record OffSystemLevelRecord(
    string LevelId,
    int Slot,
    double Threat,
    int WinWaves,
    GameFlowState EndState,
    long VictoryTick,
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
    (int X, int Y) OccupiedCell,
    IReadOnlyList<long> HashLedger,
    long FinalStateHash,
    int TowerCount);

/// <summary>Terminal report of one full campaign run (off or control).</summary>
public sealed record OffSystemRunReport(
    bool SystemEnabled,
    PanelState PanelEndState,
    bool OrderInputVisible,
    bool TrustBadgeVisible,
    bool HudNeutral,
    AlarmChannel AlarmChannel,
    int JournalInputEvents,
    int JournalExecutionEvents,
    int JournalTotalEvents,
    bool LedgerPointsAlwaysZero,
    int LedgerObservations,
    int AlarmEvaluations,
    int AlarmParityViolations,
    int NaturalAlarmFired,
    double DefenseIntegrityMin,
    double WaveEndEfficiencyMin,
    double AmmoStarvedSecondsAudit,
    int FirstWaveLeaksTotal,
    int IssuedCommands,
    int AppliedCommands,
    IReadOnlyList<OffSystemLevelRecord> Levels,
    IReadOnlyList<(string Resource, double Amount)> PoolTotals,
    IReadOnlyList<string> CapturedOrder,
    int CosmeticsCount,
    int OccupiedCells,
    long RunMillis);

/// <summary>
/// In-memory K-8 audit sink: counts every journal event by kind and flags the
/// ledger-refill noise separately, so "zero commander orders / zero executed
/// actions" is a measured fact, not an assumption (RL10/CS-4.7 audit surface).
/// </summary>
internal sealed class CountingJournalWriter : IJournalWriter
{
    private int _input;
    private int _validation;
    private int _other;
    private int _refillNoise;
    private int _total;

    /// <inheritdoc />
    public void Append(JournalEvent evt)
    {
        _total++;
        switch (evt.Kind)
        {
            case JournalEventKind.Input:
                _input++;
                break;
            case JournalEventKind.Validation:
                _validation++;
                if (evt.PayloadJson.Contains("\"wave_refill\"", StringComparison.Ordinal))
                {
                    _refillNoise++;
                }

                break;
            default:
                _other++;
                break;
        }
    }

    /// <summary>Commander orders that entered the pipeline (军令条数).</summary>
    public int InputEvents => _input;

    /// <summary>Executed-action events (Validation minus point-ledger refill noise).</summary>
    public int ExecutionEvents => _validation - _refillNoise;

    /// <summary>Every event that reached the sink.</summary>
    public int TotalEvents => _total;
}

/// <summary>
/// Headless E1 campaign driver (T-084): replays the four handcrafted levels of
/// <c>game/content/levels/e1.json</c> through the production CampaignLoop +
/// Progression chain with the T-085 scripted command streams (player builds
/// only — 零军令/零系统点/零卷轴), sampling three audit rails while it runs:
/// the agent-bus journal count, the system-point balance and the defense-alarm
/// channel (real integrity readings through <see cref="OffModeDowngrade"/>).
/// <paramref name="systemEnabled"/> = false is the R2 regression run; true is
/// the control run (advisor constructed over the live sim, never invoked).
/// <para>
/// 主机审计口径（坏事件计数）：当前 E1 引擎没有敌方→建筑伤害路径（敌方
/// 只被塔杀伤，建筑 HP 自建成后不可变），故 StationsLost/
/// KeyBuildingsDamagedCount 的 0 有"终局全员存活"实况背书；弹药断供秒数
/// 用塔效率缺口的采样累积（每 20 s 采样 Σ(1−efficiency)×Δt）作上界口径
/// 传入 S4 判分——非事件级精算，首证口径如实在 REV 登记。
/// </para>
/// </summary>
internal sealed class OffSystemCampaignDriver
{
    /// <summary>Sampling cadence of the audit rails (ticks; 600 = 20 s sim time).</summary>
    public const int SampleTicks = 600;

    /// <summary>State-hash sampling cadence of the session ledger (replay fingerprint).</summary>
    public const int StateHashInterval = 600;

    /// <summary>E1 agent-era ordinal (construction verbs locked, §11.4 points off).</summary>
    public const int E1AgentEra = 1;

    /// <summary>Region anchor the control-run pipeline gets (build-ring seed cell; never used).</summary>
    public static readonly CellCoord EastGateAnchor = new(12, 12);

    /// <summary>
    /// Runs one full E1 campaign headless and returns the terminal report.
    /// Every seed is the content row's own (map.seed / level id hash) — the
    /// two switch positions therefore share command streams and seeds, and any
    /// terminal difference is attributable to the switch alone.
    /// </summary>
    public static OffSystemRunReport Run(bool systemEnabled)
    {
        string root = Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", "t084-" + Guid.NewGuid().ToString("N"));
        var files = new DevFileStore(root);
        try
        {
            return Run(systemEnabled, files);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static OffSystemRunReport Run(bool systemEnabled, DevFileStore files)
    {
        var watch = Stopwatch.StartNew();

        // —— 开关位（F-097 支柱 4 的宿主面）：面板机 + 降级矩阵同刻落位。
        var panel = new PanelStateMachine(systemEnabled);
        OffModeSurfaces surfaces = OffModeDowngrade.Surfaces(systemEnabled);

        // —— 战役环（CampaignLoop 夹具旋钮先例：7×7 网格 + 每胜波 10 木）。
        CampaignState campaign = CampaignLoopFixture.NewCampaign();
        ResourceStock pool = campaign.Pool;
        var progression = new CampaignProgression(
            pool,
            new TechTree(TechTreeData.Parse(ReadCampaignContent("techtree.json")), pool, CampaignState.E1EraIndex),
            new TripleObjectiveJudge(ObjectivesData.Parse(ReadCampaignContent("objectives.json")), new SimRng(Seed)),
            new ChestRewards(ChestRewardsData.Parse(ReadCampaignContent("chests.json")), new SimRng(Seed)));

        ISavePort port = new SqliteSavePort(files, "saves/profile.db");
        var journal = new CountingJournalWriter();
        var auditLedger = new SystemPointLedger(CampaignState.E1EraIndex); // off-run point audit (no journaling)
        bool ledgerPointsAlwaysZero = true;
        int ledgerObservations = 0;

        int alarmEvaluations = 0;
        int alarmParityViolations = 0;
        int naturalAlarmFired = 0;
        double integrityMin = 1.0;
        double waveEndEfficiencyMin = 1.0;
        double starvedSeconds = 0.0;
        int firstWaveLeaksTotal = 0;
        int issuedCommands = 0;
        int appliedCommands = 0;

        var levels = new List<OffSystemLevelRecord>();
        foreach (HandcraftedLevel level in HandcraftedLevelData.Load("e1.json").Items)
        {
            LevelNodeInfo node = PickNextNode(campaign.Grid);
            SimulationConfig simConfig = BuildConfig(level, "era1");
            IReadOnlyList<SimCommand> commands = BuildCommands(level, "era1");
            var levelConfig = new LevelSessionConfig(
                simConfig, $"t084.{level.Id}", level.Threat, level.Map.Seed, StateHashInterval);
            var session = new LevelSession(levelConfig, new SimRng(level.Map.Seed), port, pool);

            // —— 对照组：军师在场（管线挂上同一活体模拟），但玩家整局不下令。
            AgentPipeline? pipeline = systemEnabled
                ? AgentPipeline.OverSimulation(
                    session.Sim,
                    simConfig,
                    L1IntentParserTests.CreateParser(),
                    E1AgentEra,
                    journal,
                    new CapturingLogSink(),
                    new Dictionary<string, CellCoord> { ["east_gate"] = EastGateAnchor })
                : null;

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
            long horizon = graceTicks + ((level.WinWaves + 2) * intervalTicks);

            long lastSampleTick = 0;
            int lastWave = session.Sim.CurrentWave;
            long steps = 0;
            while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
            {
                session.RunFor(SampleTicks);
                steps += SampleTicks;
                long tick = session.Sim.CurrentTick;
                double seconds = (tick - lastSampleTick) / (double)Simulation.TicksPerSecond;

                if (session.Sim.CurrentWave != lastWave)
                {
                    // 波界：波末效率采样 + 系统点回充（E1 关闭-era 回充目标恒 0）。
                    waveEndEfficiencyMin = Math.Min(waveEndEfficiencyMin, AverageEfficiency(session.Sim));
                    pipeline?.Ledger.BeginWave(session.Sim.CurrentWave, isBossWave: false); // E1 无 Boss 线
                    _ = auditLedger.BeginWave(session.Sim.CurrentWave, isBossWave: false);
                    lastWave = session.Sim.CurrentWave;
                }

                // —— 警报通道（真实读数）：中性横幅决策 + 双开关位照发等价。
                HudSnapshot hud = HudMetrics.Compute(
                    session.Sim.CurrentWave,
                    0.0,
                    session.Sim.Buildings
                        .Where(building => building.Kind == SimBuildingKind.Tower)
                        .Select(building => (building.Hp, building.Hp))
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
                }

                // —— 弹药审计（效率缺口累积——断供秒数的采样口径）。
                starvedSeconds += SupplyGapSeconds(session.Sim, seconds);

                // —— 系统点审计：全程余额观察（E1 era=1 关闭 ⇒ 恒 0）。
                ledgerObservations++;
                if ((pipeline?.Ledger.Points ?? 0) != 0 || auditLedger.Points != 0)
                {
                    ledgerPointsAlwaysZero = false;
                }

                lastSampleTick = tick;
            }

            // —— 首波漏敌审计：波 1 入场窗内出现且抵达核心的敌人数（ArrivedAtTarget 粘滞位）。
            int firstWaveLeaks = session.Sim.Units.Count(
                unit => unit.ArrivedAtTarget && unit.SpawnTick < graceTicks + intervalTicks);
            firstWaveLeaksTotal += firstWaveLeaks;

            // —— 主机审计计数：坏事件计数有实况背书（建筑无伤路径 ⇒ 全员存活；
            //    命令流无出售/卷轴动词 ⇒ 0；系统动作审计=K-8 计数）。
            var hostCounters = new HostRunCounters(
                StationsLost: 0, // 采集站存活实况（本引擎无敌方→建筑伤害路径，见类注）
                DefenseIntegrityMin: integrityMin,
                TowersSoldCount: 0,
                AmmoStarvedSeconds: starvedSeconds,
                WaveEndEfficiencyMin: waveEndEfficiencyMin,
                KeyBuildingsDamagedCount: 0, // 主城/仓库存活实况（同上）
                LogisticsDeadlockCount: 0, // E1 薄物流无死锁源（ThinHostCounters 先例口径）
                SystemBuildPlanCount: journal.ExecutionEvents, // C4 审计面=动作事件计数
                ScrollTriggerCount: 0,
                FirstWaveLeaks: firstWaveLeaks);

            GameFlowState endState = session.Machine.State;
            long victoryTick = session.Sim.CurrentTick;
            long finalHash = session.Sim.ComputeStateHash();
            IReadOnlyList<long> hashLedger = session.HashLedger.Select(sample => sample.Hash).ToList();
            issuedCommands += session.IssuedCommands.Count;
            appliedCommands += (int)session.Sim.AppliedCommandCount;

            CampaignSettlement settlement = settlementOf(session, progression, level, hostCounters);
            IReadOnlyList<string> firstClear = AutopilotPenalties.FilterFirstClearAchievements(
                new AutopilotParticipation(LevelParticipated: false, WaveParticipated: false, AutopilotMode.Off),
                new[] { "first-clear:" + level.Id });

            session.CompleteLevel();
            session.ClearExitSnapshot();

            OccupyOutcome occupy = campaign.Grid.Occupy(node.Cell.X, node.Cell.Y);

            levels.Add(new OffSystemLevelRecord(
                level.Id,
                level.Slot,
                level.Threat,
                session.WinWaves,
                endState,
                victoryTick,
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
                (occupy.Node.Cell.X, occupy.Node.Cell.Y),
                hashLedger,
                finalHash,
                session.Sim.Towers.Count));
        }

        watch.Stop();
        return new OffSystemRunReport(
            systemEnabled,
            panel.State,
            surfaces.OrderInputVisible,
            surfaces.TrustBadgeVisible,
            surfaces.HudNeutral,
            surfaces.Alarm,
            journal.InputEvents,
            journal.ExecutionEvents,
            journal.TotalEvents,
            ledgerPointsAlwaysZero,
            ledgerObservations,
            alarmEvaluations,
            alarmParityViolations,
            naturalAlarmFired,
            integrityMin,
            waveEndEfficiencyMin,
            starvedSeconds,
            firstWaveLeaksTotal,
            issuedCommands,
            appliedCommands,
            levels,
            pool.Resources.Select(resource => (resource.Value, Math.Round(pool.AmountOf(resource), 6))).ToList(),
            progression.CapturedLevelIds.ToList(),
            progression.Cosmetics.Count,
            campaign.Grid.OccupiedCells.Count,
            watch.ElapsedMilliseconds);
    }

    private static CampaignSettlement settlementOf(
        LevelSession session, CampaignProgression progression, HandcraftedLevel level, HostRunCounters host)
    {
        LevelRunFacts facts = CampaignProgression.ObserveFacts(
            session, level.BudgetSeconds, researchCompletedCount: 0, host);
        return progression.SettleVictory(session, level.Id, level.Threat, facts);
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

    /// <summary>Deterministic next-node pick: fewest adjacent camps, then row-major.</summary>
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
}
