using System.Diagnostics;
using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Endless;
using EraCore.IO;
using EraCore.Procedural;
using EraCore.Sim;
using EraCore.Telemetry;
using EraCore.Tests.Endless;
using EraCore.Tests.Levels;
using EraCore.Tests.Persistence;
using EraCore.Tests.Procedural;
using EraCore.Tests.Sim;
using EraCore.Tests.World;
using EraCore.Units;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Levels.HandcraftedLevelContent;
using static EraCore.Tests.Procedural.ProceduralEntryContent;

namespace EraCore.Tests.Telemetry;

// [C-19][F-006] T-155 硬验收：六类全覆盖链路断言——战役关 E1~E6 全部时代
// （E1/E2/E3/E4/E5/终章各一关真内容真命令流 headless 通关）+程序关（T-140
// 生成器产物通关）+无尽局（EndlessEntry.Begin 真内容局死亡终局）全部经
// LevelSession/EndlessTelemetry 挂点自动落本地 JSONL（玩家零操作——本链即
// headless 自动局，Source=script 入账，供 T-158 比对机器 vs 真人比率）。
// 附：战役弃局行（中途退出=Abandoned 诚实行）。双口径裁定与挂点说明见
// REV-20261018-T155（主口径=纯游玩 tick；墙钟=参考列）。
public class LevelDurationTelemetryChainTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public LevelDurationTelemetryChainTests(ITestOutputHelper output) => _output = output;

    // CS-3.8：临时数据入 .tmp-tests 沙盒（pinned dev 根下，gitignored），Dispose 由基类/自身处理。
    private static string NewRoot() => Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", "telemetry-chain", Guid.NewGuid().ToString("N"));

    /// <summary>可拨墙钟（CS-1.4 端口 Fake）：由测试按真实经过拨针（Stopwatch）。</summary>
    private sealed class StopwatchWallClock : IClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly long _baseMs;

        public StopwatchWallClock(long baseMs) => _baseMs = baseMs;

        public long Tick => 0;

        public long NowMs => _baseMs + _watch.ElapsedMilliseconds;
    }

    private static HandcraftedLevel EraLevel(int era) => HandcraftedLevelData.Load($"e{era}.json")
        .Items.Single(level => level.Slot == 1);

    /// <summary>各时代装配（T-085 BuildConfig 基座+时代扩展面——先例=EraNLevelPlaythroughTests 同口径）。</summary>
    private static SimulationConfig EraConfig(HandcraftedLevel level, int era) => era switch
    {
        3 => Era3LevelPlaythroughTests.BuildE3Config(level),
        4 => Era4LevelPlaythroughTests.BuildE4Config(level),
        5 => Era5LevelPlaythroughTests.BuildE5Config(level),
        6 => Era6LevelPlaythroughTests.BuildE6Config(level),
        _ => BuildConfig(level, $"era{era}"), // E1/E2：基座装配即全量
    };

    private static long HorizonTicks(HandcraftedLevel level)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return graceTicks + ((level.WinWaves + 12) * intervalTicks); // 胜波+12 波窗（E5/6 加宽版，向下兼容）
    }

    /// <summary>跑通一关（真内容+T-085 命令流）并把遥测接到 LevelSession 挂点。</summary>
    private long RunCampaignLevel(int era, LevelDurationTelemetry telemetry)
    {
        HandcraftedLevel level = EraLevel(era);
        SimulationConfig config = EraConfig(level, era);
        IReadOnlyList<SimCommand> commands = BuildCommands(level, $"era{era}");
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t155.{level.Id}", level.Threat, level.Map.Seed)
            {
                Telemetry = new LevelTelemetryScope(
                    TelemetryLevelKind.Campaign, level.Id, era, level.Map.Seed, TelemetrySources.Script),
            },
            new SimRng(level.Map.Seed),
            CreatePort(),
            durationTelemetry: telemetry);

        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        long steps = 0;
        while (session.Machine.State == GameFlowState.InLevel && steps < HorizonTicks(level))
        {
            session.RunFor(600);
            steps += 600;
        }

        Assert.True(session.Machine.State == GameFlowState.Victory,
            $"未通关（state={session.Machine.State}）: era={era} id={level.Id} wave={session.Sim.CurrentWave} " +
            $"tick={session.Sim.CurrentTick} issued={session.IssuedCommands.Count} " +
            $"applied={session.Sim.AppliedCommandCount} towers={session.Sim.Towers.Count}");
        return session.Sim.CurrentTick;
    }

    /// <summary>程序关一局（T-140 生成器产物）通关并把遥测接到同一 LevelSession 挂点。</summary>
    private void RunProceduralLevel(LevelDurationTelemetry telemetry)
    {
        ProceduralTier tier = Load().TryGetTier("regular-1")
            ?? throw new InvalidOperationException("档位 regular-1 缺席注册表");
        const long seed = 20260255; // T-155 专用 seed（不与 T-140 表重叠）
        ProceduralLevel generated = ProceduralLevelGenerator.Generate(
            new ProceduralRequest(seed, tier, Era1Pack(), Load().Constraints),
            derived => new SimRng(derived));
        Levels.HandcraftedLevel level = HandcraftedLevelData.Parse(generated.ToLevelJson()).Items.Single();

        var session = new LevelSession(
            new LevelSessionConfig(BuildConfig(level, "era1"), $"t155.{level.Id}", level.Threat, level.Map.Seed)
            {
                Telemetry = new LevelTelemetryScope(
                    TelemetryLevelKind.Procedural, level.Id, 1, level.Map.Seed, TelemetrySources.Script),
            },
            new SimRng(level.Map.Seed),
            CreatePort(),
            durationTelemetry: telemetry);

        session.Machine.StartLevel();
        foreach (SimCommand command in BuildCommands(level, "era1"))
        {
            session.Enqueue(command);
        }

        long horizon = ((long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero) * (level.WinWaves + 12))
            + (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond
            * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long steps = 0;
        while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
        {
            session.RunFor(600);
            steps += 600;
        }

        Assert.True(session.Machine.State == GameFlowState.Victory,
            $"程序关未通关（state={session.Machine.State}）: id={level.Id} tick={session.Sim.CurrentTick}");
    }

    /// <summary>无尽一局（真内容两时代混编池）死亡终局+遥测经 EndlessTelemetry 挂点。</summary>
    private static void RunEndless(LevelDurationTelemetry telemetry)
    {
        const long seed = 20260256;
        const int waves = 30;
        IReadOnlyList<EndlessEraContent> content = new[]
        {
            new EndlessEraContent(1, Rows(1), 14, 3, 3, 1.6),
            new EndlessEraContent(3, Rows(3), 16, 3, 4, 1.6),
        };
        var request = new EndlessEntryRequest(
            seed,
            StartEraIndex: 1,
            EndlessParticipationMode.Manual,
            ThreatLevel: 0.9,
            EraContent: content,
            WonderIds: Array.Empty<string>());

        LevelDurationTracker tracker = EndlessTelemetry.BeginRun(telemetry, request.StartEraIndex, seed, TelemetrySources.Script);
        EndlessSession session = EndlessEntry.Begin(request, new LcgRng(seed));

        long ledger = 0;
        for (int i = 0; i < waves; i++)
        {
            EndlessWavePlan plan = session.Run.BeginWave();
            ledger += EndlessTelemetry.WaveLedgerTicks(plan);
            session.Run.CompleteWave(plan.EliteCount, plan.BossCount);
        }

        EndlessSettlement settlement = session.Die();
        tracker.End(TelemetryOutcome.Defeat, ledger);

        Assert.True(settlement.WavesSurvived == waves, $"无尽局波数不符：{settlement.WavesSurvived}");
        Assert.True(ledger > 0, "无尽波表 tick 台账必须为正");
    }

    /// <summary>各代真敌表（生产解析器）。</summary>
    private static IReadOnlyList<EnemySpec> Rows(int era) =>
        HandcraftedLevelContent.Enemies($"era{era}").Values.OrderBy(spec => spec.Id, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------ 六类覆盖

    // [F-006] legal：六类全覆盖——E1~E6 六时代战役关+程序关+无尽局，全部
    // 自动落 JSONL（一行一坐；Source=script=headless 自动局入账）
    [Fact]
    public void Six_categories_all_land_local_jsonl_automatically()
    {
        string root = NewRoot();
        var wall = new StopwatchWallClock(baseMs: 1_700_000_000_000); // 任意纪元基线（Unix ms 域）
        var files = new DevFileStore(root);
        var telemetry = new LevelDurationTelemetry(files, wall);

        // ---- 战役关：E1~E6 全部时代（含终章）——LevelSession 挂点
        var victoryTicks = new Dictionary<int, long>();
        foreach (int era in new[] { 1, 2, 3, 4, 5, 6 })
        {
            victoryTicks[era] = RunCampaignLevel(era, telemetry);
        }

        // ---- 程序关：T-140 生成器产物——同一 LevelSession 挂点
        RunProceduralLevel(telemetry);

        // ---- 无尽局：EndlessTelemetry 挂点（死亡终局）
        RunEndless(telemetry);

        // ---- 战役弃局行：E1 开局数 tick 后中途退出（Pause→SaveAndExit）=Abandoned
        HandcraftedLevel exitLevel = EraLevel(1);
        var exitSession = new LevelSession(
            new LevelSessionConfig(BuildConfig(exitLevel, "era1"), $"t155.exit.{exitLevel.Id}", exitLevel.Threat, exitLevel.Map.Seed)
            {
                Telemetry = new LevelTelemetryScope(
                    TelemetryLevelKind.Campaign, exitLevel.Id, 1, exitLevel.Map.Seed, TelemetrySources.Script),
            },
            new SimRng(exitLevel.Map.Seed),
            CreatePort(),
            durationTelemetry: telemetry);
        exitSession.Machine.StartLevel();
        exitSession.RunFor(300);
        exitSession.Pause();
        exitSession.SaveAndExitToMenu();

        // ---- 断言面：读回账本逐行核对
        IReadOnlyList<LevelDurationRecord> ledger = LevelDurationTelemetry.ReadAll(files);
        _output.WriteLine($"T-155 六类覆盖账本（{ledger.Count} 行，root={root}）:");
        foreach (LevelDurationRecord line in ledger)
        {
            _output.WriteLine(
                $"  kind={line.Kind} level={line.LevelId} era={line.EraIndex} seed={line.Seed} " +
                $"outcome={line.Outcome} source={line.Source} playTicks={line.PlayTicks} " +
                $"({line.PlayTicks / (double)(Simulation.TicksPerSecond * 60):F2}min) " +
                $"wallMs={line.WallMs} startTick={line.StartTick} endTick={line.EndTick}");
        }

        Assert.Equal(9, ledger.Count); // 6 战役+1 程序+1 无尽+1 弃局

        // 战役六行：kind/era/levelId/结果/seed 逐行对上
        var campaign = ledger.Where(l => l.Kind == TelemetryLevelKind.Campaign && l.Outcome == TelemetryOutcome.Victory).ToList();
        Assert.Equal(6, campaign.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, campaign.Select(l => l.EraIndex).OrderBy(e => e).ToArray());
        Assert.All(campaign, l =>
        {
            Assert.Equal(TelemetryOutcome.Victory, l.Outcome);
            Assert.StartsWith("campaign:level-e", l.LevelId, StringComparison.Ordinal);
            Assert.True(l.PlayTicks > 0, $"战役关纯游玩 tick 必须为正：{l.LevelId}");
            Assert.Equal(0, l.StartTick);
            Assert.Equal(l.EndTick, l.StartTick + l.PlayTicks);
        });

        // 每时代通关刻与遥测行一致（遥测=会话终局刻的诚实读数）
        foreach (LevelDurationRecord line in campaign)
        {
            int era = line.EraIndex;
            Assert.Equal(victoryTicks[era], line.EndTick);
        }

        // 程序关一行：kind=Procedural+真 seed+Victory
        LevelDurationRecord proc = ledger.Single(l => l.Kind == TelemetryLevelKind.Procedural);
        Assert.Equal(TelemetryOutcome.Victory, proc.Outcome);
        Assert.True(proc.PlayTicks > 0);
        Assert.NotEqual(0, proc.Seed);

        // 无尽一行：kind=Endless+开局时代+seed+死亡终局（无尽无胜利终态）
        LevelDurationRecord endless = ledger.Single(l => l.Kind == TelemetryLevelKind.Endless);
        Assert.Equal(LevelTelemetryScope.EndlessId, endless.LevelId);
        Assert.Equal(1, endless.EraIndex);
        Assert.Equal(TelemetryOutcome.Defeat, endless.Outcome);
        Assert.True(endless.PlayTicks > 0);

        // 弃局一行：Abandoned+Resumed=false+退出刻读数
        LevelDurationRecord abandoned = ledger.Single(l => l.Outcome == TelemetryOutcome.Abandoned);
        Assert.Equal(TelemetryLevelKind.Campaign, abandoned.Kind);
        Assert.Equal(exitLevel.Id, abandoned.LevelId);
        Assert.Equal(300, abandoned.PlayTicks);

        // 全表：来源列=script（headless 自动局入账——T-158 机器 vs 真人比对位）
        Assert.All(ledger, l => Assert.Equal(TelemetrySources.Script, l.Source));
        // 全表：墙钟参考列非负、schema 版本一致
        Assert.All(ledger, l => Assert.True(l.WallMs >= 0));
        Assert.All(ledger, l => Assert.Equal(LevelDurationTelemetry.CurrentSchema, l.Schema));

        // 账本物理形态：LF 结尾、一行一 JSON 对象（发布件任意注入盘皆同形）
        string text = File.ReadAllText(Path.Combine(root, LevelDurationTelemetry.DefaultRelativePath));
        Assert.EndsWith("\n", text);
        Assert.Equal(ledger.Count, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
