using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Checkpoint;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.IO;
using EraCore.Persistence;
using EraCore.Sim;
using EraCore.Telemetry;
using EraCore.Tests.CampaignLoop;
using EraCore.Tests.Checkpoint;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;

namespace EraCore.Tests.Levels;

// [F-001][F-004][F-006] T-160c 败北全链（战役宿主面断点一）：引擎调用约定=
// 宿主检测败北条件→DeclareDefeat 声明（无自动败北谓词）。本链经生产组装律
// （EraLevelAssembly——CampaignMenuUi 选关开局走的同一条路径）实证完整链：
// e1-02 真跑到检查点波（=max(10,⌊胜波×0.75⌋)——v3.2 §3/§7 失败分档）→
// DeclareDefeat（普通关分档：检查点恢复+残骸修复账+首败 ×0.8 折扣+遥测
// Defeat 行）→TryResumeCheckpoint（T-160c 引擎缝）重放到检查点波边界（与
// 原局该 tick 哈希逐字节全等——重放即恢复，T-040 不变量）→续局真通关
// （Victory 终态）。负向：未跨检查点波的败北=从头重开（恢复波 0/续局缝
// 返回假——失败面板「仅重开」档的引擎事实）。
[Collection(CheckpointHeavyCollection.Name)]
public class CampaignHostDefeatChainTests : SqliteSavePortTestBase
{
    private static HandcraftedLevel LevelOf(string file, string id) =>
        HandcraftedLevelData.Load(file).TryGet(id)
        ?? throw new InvalidOperationException($"缺关卡行 {id}");

    private static long IntervalTicks(HandcraftedLevel level) =>
        (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);

    private static long HorizonTicks(HandcraftedLevel level)
    {
        long interval = IntervalTicks(level);
        long grace = (long)Math.Round(interval * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        return grace + ((level.WinWaves + 12) * interval);
    }

    /// <summary>生产组装律开局（命令流=关卡脚本）+遥测注入（T-155 战役半边）.</summary>
    private static LevelSession Start(ISavePort port, HandcraftedLevel level, LevelDurationTelemetry telemetry) =>
        Start(port, level, telemetry, out _);

    private static LevelSession Start(
        ISavePort port, HandcraftedLevel level, LevelDurationTelemetry telemetry, out LevelSessionConfig config)
    {
        config = new LevelSessionConfig(
            EraLevelAssembly.BuildConfig(level, ContentRoot()),
            $"t160c.{level.Id}",
            level.Threat,
            level.Map.Seed,
            Telemetry: new LevelTelemetryScope(
                TelemetryLevelKind.Campaign, level.Id, 1, level.Map.Seed, TelemetrySources.Script));
        var session = new LevelSession(config, new SimRng(level.Map.Seed), port, durationTelemetry: telemetry);
        session.Machine.StartLevel();
        foreach (SimCommand command in EraLevelAssembly.BuildCommands(level, ContentRoot()))
        {
            session.Enqueue(command);
        }

        return session;
    }

    [Fact]
    public void Defeat_after_checkpoint_resumes_to_boundary_and_wins_the_level()
    {
        HandcraftedLevel level = LevelOf("e1.json", "campaign:level-e1-02");
        var telemetry = new LevelDurationTelemetry(Files, new SimClock());
        LevelSession session = Start(CreatePort(), level, telemetry, out LevelSessionConfig config);

        // —— ① 跑过检查点波（#ckpt 落盘；检查点=max(10,⌊13×0.75⌋)=10）——
        int checkpointWave = session.Checkpoints.CheckpointWave;
        Assert.Equal(Math.Max(10, (int)Math.Floor(level.WinWaves * 0.75)), checkpointWave);
        long steps = 0;
        while (session.Machine.State == GameFlowState.InLevel
            && session.Sim.CurrentWave <= checkpointWave
            && steps < HorizonTicks(level))
        {
            session.RunFor(600);
            steps += 600;
        }

        Assert.True(session.Sim.CurrentWave > checkpointWave,
            $"未跨检查点波：wave={session.Sim.CurrentWave} checkpoint={checkpointWave}");

        // —— ② DeclareDefeat（宿主声明——引擎调用约定）：普通关分档落地 ——
        NormalLevelFailureOutcome outcome = session.DeclareDefeat(firstDefeat: true);
        Assert.Equal(FailureScenarioKind.NormalLevel, session.FailureTier);
        Assert.Equal(GameFlowState.Defeat, session.Machine.State);
        Assert.Equal(checkpointWave, outcome.RestoredFromWave);
        Assert.Equal(level.Map.Seed, outcome.RunSeed);
        Assert.True(outcome.RestoredAtTick > 0);
        Assert.True(outcome.FirstDefeat);
        Assert.NotEmpty(outcome.Wreckage); // 波界重放回来的非仓库建筑=残骸账
        // 残骸修复账=v3.2 §3 设计字面：原造价×30%×首败 0.8（乘法叠加，甲方签认）。
        foreach (WreckEntry wreck in outcome.Wreckage)
        {
            Assert.Equal(wreck.OriginalCostPoints * 0.30 * 0.80, wreck.RepairCostPoints, precision: 6);
        }

        Assert.True(outcome.TotalRepairCostPoints > 0);
        session.AcknowledgeDefeat();
        Assert.Equal(GameFlowState.Menu, session.Machine.State);

        // 遥测 Defeat 行（T-155 终态三态的败北半边——source=script 沙盒）。
        Assert.Contains(LevelDurationTelemetry.ReadAll(Files), row =>
            row.Kind == TelemetryLevelKind.Campaign && row.LevelId == level.Id
            && row.Outcome == TelemetryOutcome.Defeat && !row.Resumed
            && row.Source == TelemetrySources.Script && row.PlayTicks > 0);

        // —— ③ TryResumeCheckpoint（T-160c 引擎缝）：重放到检查点波边界 ——
        var port2 = CreatePort(); // 同库（level 域共享）；#ckpt 由①的边界观察落盘
        Assert.True(LevelSession.TryResumeCheckpoint(
            config, new SimRng(level.Map.Seed), port2, null, out LevelSession resumed, telemetry));
        Assert.Equal(GameFlowState.Paused, resumed.Machine.State);
        Assert.Equal(outcome.RestoredAtTick, resumed.Sim.CurrentTick);
        Assert.Equal(checkpointWave + 1, resumed.Sim.CurrentWave); // 边界=波 N 结束/波 N+1 入场
        Assert.Equal(session.IssuedCommands.Count(command => command.IssuedAtTick <= outcome.RestoredAtTick),
            resumed.IssuedCommands.Count); // 命令流种子化（重放键完整）

        // 重放即恢复（T-040）：续局会话在该 tick 的哈希=原局账本同位采样。
        long? hashAtBoundary = session.HashLedger
            .FirstOrDefault(sample => sample.Tick == outcome.RestoredAtTick).Hash;
        Assert.NotNull(hashAtBoundary);
        Assert.Equal(hashAtBoundary!.Value, resumed.Sim.ComputeStateHash());

        // —— ④ 续局真通关（败北→检查点继续→Victory 全链）——
        resumed.ResumePlaying();
        steps = 0;
        while (resumed.Machine.State == GameFlowState.InLevel && steps < HorizonTicks(level))
        {
            resumed.RunFor(600);
            steps += 600;
        }

        Assert.Equal(GameFlowState.Victory, resumed.Machine.State);
        Assert.Equal(level.WinWaves, resumed.Victory!.WinWaves);

        // 续局=新遥测行（Resumed=true——可拼接还原整关，T-155 口径）。
        Assert.Contains(LevelDurationTelemetry.ReadAll(Files), row =>
            row.LevelId == level.Id && row.Resumed);
    }

    [Fact]
    public void Defeat_before_checkpoint_restarts_from_tick_zero_with_no_wreckage()
    {
        HandcraftedLevel level = LevelOf("e1.json", "campaign:level-e1-03");
        ISavePort port = CreatePort();
        var telemetry = new LevelDurationTelemetry(Files, new SimClock());

        // 打过波 1 命令段（塔已落地）但远未跨检查点波——败北仍=从头重开。
        long earlyTick = IntervalTicks(level) + ((long)Math.Round(
            IntervalTicks(level) * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero));
        LevelSession session = Start(port, level, telemetry, out LevelSessionConfig config);
        while (session.Machine.State == GameFlowState.InLevel && session.Sim.CurrentTick < earlyTick)
        {
            session.RunFor(600);
        }

        Assert.True(session.Sim.Towers.Count > 0, "波 1 命令段应已建塔（活局有防线）");
        NormalLevelFailureOutcome outcome = session.DeclareDefeat(firstDefeat: true);
        session.AcknowledgeDefeat();

        // 无检查点：恢复点=零（失败面板「仅重开」档）；恢复态=tick 0 的新局
        // （残骸账随恢复态走——零点态无可残骸，账面为空是引擎语义非缺漏）。
        Assert.Equal(0, outcome.RestoredFromWave);
        Assert.Equal(0, outcome.RestoredAtTick);
        Assert.Equal(level.Map.Seed, outcome.RunSeed);
        Assert.Empty(outcome.Wreckage);
        Assert.Equal(0.0, outcome.TotalRepairCostPoints);
        Assert.Equal(0, outcome.RestoredSimulation.CurrentTick);
        Assert.True(outcome.FirstDefeat);

        // 续局缝同事实：未跨检查点波=不可续（返回假+全新 Menu 会话）。
        Assert.False(LevelSession.TryResumeCheckpoint(
            config, new SimRng(level.Map.Seed), CreatePort(), null, out LevelSession fresh, telemetry));
        Assert.Equal(GameFlowState.Menu, fresh.Machine.State);
        Assert.Equal(0, fresh.Sim.CurrentTick);

        // 遥测 Defeat 行（未跨检查点波的败北同样入账——T-155 终态三态）。
        Assert.Contains(LevelDurationTelemetry.ReadAll(Files), row =>
            row.Kind == TelemetryLevelKind.Campaign && row.LevelId == level.Id
            && row.Outcome == TelemetryOutcome.Defeat && !row.Resumed
            && row.Source == TelemetrySources.Script);
    }
}
