using System.Text;
using EraCore.Agent.Autopilot;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;
using EraCore.Contracts.Journal;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Autopilot;

// [F-093][C-13] T-081 全托管集成：60 秒（1800 tick × 30Hz，确定性口径——不用墙钟，CS-1.4）托管对局：
// 自主建塔有活性、每轮 ≤3 动作、轮末汇报入 K-8 战报；同 seed 同唤醒序列 ⇒ 导演哈希序列与模拟哈希序列逐点一致。
public class AutopilotIntegrationTests
{
    /// <summary>60 托管秒 = 60 × 30Hz = 1800 tick（确定性时间轴，非墙钟）。</summary>
    private const long SixtySecondsTicks = 1800;

    private sealed record EpisodeTrace(
        IReadOnlyList<(long DirectorHash, long SimHash)> Checkpoints,
        IReadOnlyList<string> RoundDigests,
        int TowersBuilt,
        long AutopilotActions,
        long AutopilotPoints);

    /// <summary>
    /// One full 60s autopilot episode over the real E1 fixture and the real
    /// T-068 pipeline: engage → two host waves (ledger pre-begun wave 1 by
    /// the harness; the director's waves are labeled 2/3) with 60s of sim
    /// advance between them. Returns the checkpoint hash sequence plus the
    /// per-round digests for the liveness/determinism assertions.
    /// </summary>
    private static EpisodeTrace RunEpisode()
    {
        using var harness = new PipelineHarness();
        AutopilotDirector director = AutopilotDirector.OverSimulation(
            harness.Pipeline,
            harness.Simulation,
            PipelineHarness.Config(),
            new Dictionary<string, CellCoord>
            {
                ["east_gate"] = PipelineHarness.EastGateAnchor,
                ["south_gate"] = PipelineHarness.SouthGateAnchor,
            });
        director.EngageFullAutopilot();

        var checkpoints = new List<(long DirectorHash, long SimHash)>();
        var digests = new List<string>();

        void CaptureWave(long waveNumber)
        {
            Assert.True(director.BeginWave(waveNumber, isBossWave: false));
            foreach (AutopilotRoundResult round in director.RunWave())
            {
                digests.Add(Digest(round));
            }

            checkpoints.Add((director.ComputeStateHash(), harness.Simulation.ComputeStateHash()));
        }

        CaptureWave(2);                                  // 托管波 1：east_gate 自主设防
        harness.Simulation.Run(SixtySecondsTicks);       // 60s 对局推进
        CaptureWave(3);                                  // 托管波 2：south_gate 补防

        return new EpisodeTrace(
            checkpoints,
            digests,
            harness.Simulation.Towers.Count,
            director.ActionsIssued,
            director.PointsSpentByAutopilot);
    }

    private static string Digest(AutopilotRoundResult round) => new StringBuilder()
        .Append("round=").Append(round.Round)
        .Append(";intent=").Append(round.Intent)
        .Append(";quota=").Append(round.QuotaUsed)
        .Append(";verbs=").Append(string.Join("+", round.Actions.Select(action => action.Verb)))
        .Append(";status=").Append(string.Join("+", round.Actions.Select(action => action.Status)))
        .ToString();

    [Fact] // legal：60s 全托管集成——自主建塔有活性，两波各设防一个分区
    public void Sixty_second_autopilot_episode_builds_towers_autonomously()
    {
        EpisodeTrace trace = RunEpisode();

        Assert.True(trace.TowersBuilt >= 4,
            $"托管 60s 应自主建成 ≥4 座塔（实际 {trace.TowersBuilt}）——HoldRegion 优先 ×2 波");
        Assert.Equal(2, trace.RoundDigests.Count);           // 每波恰一轮建案（40% 波上限钳制）
        Assert.Contains(trace.RoundDigests, digest => digest.Contains("HoldRegion", StringComparison.Ordinal));
        Assert.True(trace.AutopilotActions >= 6, "侦察+建塔×2 波 + 轮末汇报——动作有活性");
        Assert.Equal(4, trace.AutopilotPoints);              // 建造 2 点 × 2 波
        Assert.All(trace.RoundDigests, digest =>
            Assert.DoesNotContain("Err", digest, StringComparison.Ordinal)); // 无失败步骤
    }

    [Fact] // legal：全剧每轮 ≤3 动作（非汇报动词），配额执法贯穿 60s
    public void Every_round_of_the_episode_respects_the_three_action_quota()
    {
        EpisodeTrace trace = RunEpisode();

        Assert.All(trace.RoundDigests, digest => Assert.InRange(QuotaOf(digest), 0, 3));

        static int QuotaOf(string digest)
        {
            int at = digest.IndexOf("quota=", StringComparison.Ordinal) + "quota=".Length;
            int end = digest.IndexOf(';', at);
            return int.Parse(digest[at..end]);
        }
    }

    [Fact] // legal：同 seed 同唤醒序列 → 导演哈希与模拟哈希序列逐点一致（确定性）
    public void Same_seed_and_wake_sequence_produce_identical_hash_sequences()
    {
        EpisodeTrace first = RunEpisode();
        EpisodeTrace second = RunEpisode();

        Assert.Equal(first.RoundDigests, second.RoundDigests);        // 轮摘要一致（同意图同动作同状态）
        Assert.Equal(first.TowersBuilt, second.TowersBuilt);
        Assert.Equal(first.AutopilotPoints, second.AutopilotPoints);
        Assert.Equal(first.Checkpoints.Count, second.Checkpoints.Count);
        for (int i = 0; i < first.Checkpoints.Count; i++)
        {
            Assert.Equal(first.Checkpoints[i].DirectorHash, second.Checkpoints[i].DirectorHash);
            Assert.Equal(first.Checkpoints[i].SimHash, second.Checkpoints[i].SimHash);
        }
    }

    [Fact] // legal：轮末汇报真入 K-8 JSONL 战报——全托管对局全程留痕可审计
    public void Autopilot_rounds_leave_their_reports_in_the_war_journal()
    {
        using var harness = new PipelineHarness();
        AutopilotDirector director = AutopilotDirector.OverSimulation(
            harness.Pipeline,
            harness.Simulation,
            PipelineHarness.Config(),
            new Dictionary<string, CellCoord> { ["east_gate"] = PipelineHarness.EastGateAnchor });
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);
        AutopilotRoundResult round = Assert.Single(director.RunWave());

        IReadOnlyList<JournalEvent> events = JsonlJournalWriter.ReadAll(harness.JournalPath);
        Assert.True(events.Count > 0, "K-8 战报应有事件流");
        Assert.Contains(events, journalEvent =>
            journalEvent.PayloadJson.Contains("ReportToPlayer", StringComparison.Ordinal));
        Assert.Contains("全托管第 1 轮", round.ReportSummary, StringComparison.Ordinal); // 轮末汇报文本
    }

    [Fact] // legal：托管局三罚则联合执法——60s 参与局：首通无成就 + 收益 ×0.8 + 仅无尽通道
    public void Sixty_second_episode_participation_arms_all_three_penalties()
    {
        using var harness = new PipelineHarness();
        AutopilotDirector director = AutopilotDirector.OverSimulation(
            harness.Pipeline,
            harness.Simulation,
            PipelineHarness.Config(),
            new Dictionary<string, CellCoord> { ["east_gate"] = PipelineHarness.EastGateAnchor });
        director.EngageFullAutopilot();
        Assert.False(director.Participation.LevelParticipated); // 未开轮不武装
        _ = director.BeginWave(2, isBossWave: false);
        _ = director.RunWave();

        AutopilotParticipation participation = director.Participation;
        Assert.True(participation.LevelParticipated);
        Assert.True(participation.WaveParticipated);
        // 三罚则矩阵在托管局全挂（矩阵细则见 AutopilotPenaltiesTests）
        Assert.Empty(AutopilotPenalties.FilterFirstClearAchievements(participation, new[] { "ach:first-clear" }));
        Assert.Equal(80.0, AutopilotPenalties.ScaleYield(100.0));
        Assert.Equal(
            AutopilotPenalties.EndlessAutopilotChannelId,
            AutopilotPenalties.RouteScoreChannel(participation, isEndlessRun: true));
        Assert.Null(AutopilotPenalties.RouteScoreChannel(participation, isEndlessRun: false));
    }
}
