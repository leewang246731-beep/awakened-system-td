using EraCore.Agent.Governance;
using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Narrative;
using EraCore.Sim;
using EraCore.Tests.Cinematics;
using EraCore.Tests.Levels;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using NarrPersona = EraCore.Narrative.SystemPersona;

namespace EraCore.Tests.Narrative;

// [C-24] [F-100] [F-103] T-089 集成级验收：黄金三章在真 L1-1 命令流上放演——
// 真模拟（e1.json 装载+命令流）+ 真钩子路由（CinematicHookRouter 波 2 位触发
// 绑定演出）+ 叙事导演节拍流：三幕时序断言（幕序严格递增、波锚=2/2/5、
// 第三幕−第一幕 tick=公式值、循环跨度 ≤900s/§12.1 15 分钟预算——绝对窗口受
// T-085 节拍 RED-FLAG 约束归 T-095 校准，叙事侧执法循环跨度不变式）；
// 全程通关（真胜利拍）→三幕齐→交底+卡点预告合流→命令流塔数 3=低配
// →打脸隐藏评价命中（先定后演）。
public class NarrativeIntegrationTests : SqliteSavePortTestBase
{
    private const int BatchTicks = 60;

    private sealed record GoldenRun(
        NarrativeDirector Director,
        long Act1Tick,
        long Act2Tick,
        long Act3Tick,
        IReadOnlyList<string> Trace,
        IReadOnlyList<MockArcTaunt> TauntsSeen);

    /// <summary>
    /// Drives L1-1 headless (real config + command stream from e1.json) with
    /// the narrative director and the cinematic hook router wired on the same
    /// wave beats, until wave 6 (past the 打脸 anchor). Returns the act fire
    /// ticks and the emitted line trace.
    /// </summary>
    private GoldenRun PlayGoldenLoop(int untilWave)
    {
        HandcraftedLevel level = HandcraftedLevelData.Load("e1.json").TryGet("campaign:level-e1-01")!;
        var config = new LevelSessionConfig(
            BuildConfig(level, "era1"), $"t089.{level.Id}", level.Threat, level.Map.Seed);
        var session = new LevelSession(config, new SimRng(level.Map.Seed), CreatePort());

        // 绑定演出已有（T-086）：钩子路由按真关卡行绑定 aha 脚本。
        var hookRows = new List<CinematicHookRow>(CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e1"), "levels/e1.json"));
        var router = new CinematicHookRouter(hookRows, new[] { CinematicContent.E1Aha() });

        var director = new NarrativeDirector(
            NarrativeContent.StoryE1(),
            TrustBadge.Trustworthy,
            NarrPersona.Advisor,
            NarrativeContent.StoryE1().MockArcFor("campaign:level-e1-01"));

        session.Machine.StartLevel();
        foreach (SimCommand command in BuildCommands(level, "era1"))
        {
            session.Enqueue(command);
        }

        var trace = new List<string>();
        var tauntsSeen = new List<MockArcTaunt>();
        trace.AddRange(director.Begin().Select(line => line.BubbleText));

        long intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);

        int lastWave = session.Sim.CurrentWave;
        long steps = 0;
        long horizon = graceTicks + ((untilWave + 1) * intervalTicks);
        while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
        {
            session.RunFor(BatchTicks);
            steps += BatchTicks;

            if (session.Sim.CurrentWave != lastWave)
            {
                lastWave = session.Sim.CurrentWave;
                long tick = session.Sim.CurrentTick;

                // 同一拍两路：叙事导演收波拍（受辱/打脸+嘲讽），钩子路由收
                // 波拍（绑定演出 fired → 觉醒幕）——与宿主接线同序。
                IReadOnlyList<DialogueLine> waveLines = director.OnWaveStart(lastWave, tick);
                foreach (DialogueLine line in waveLines)
                {
                    if (line.Key.StartsWith("mock.", StringComparison.Ordinal))
                    {
                        tauntsSeen.Add(new MockArcTaunt(lastWave, line.Speaker, line.Text));
                    }

                    trace.Add(line.BubbleText);
                }

                foreach (string scriptId in router.OnWaveStart(lastWave))
                {
                    IReadOnlyList<DialogueLine> actLines = director.OnCinematicFired(scriptId, tick);
                    trace.AddRange(actLines.Select(line => line.BubbleText));
                }
            }
        }

        Assert.True(
            director.Golden.Complete,
            $"黄金循环未闭合（acts={director.Golden.FiredCount}, wave={session.Sim.CurrentWave}, steps={steps}/{horizon}）");

        return new GoldenRun(
            director,
            director.Golden.TickOf("narrative:act-e1-humiliation")!.Value,
            director.Golden.TickOf("narrative:act-e1-awakening")!.Value,
            director.Golden.TickOf("narrative:act-e1-face-slap")!.Value,
            trace,
            tauntsSeen);
    }

    // ---------------------------------------------------------------- [F-103]

    [Fact] // legal — 三幕时序断言：幕序严格不降、波锚 2/2/5、tick=公式值、循环 ≤15 分钟
    public void L1_1_command_flow_fires_the_golden_loop_within_the_fifteen_minute_budget()
    {
        GoldenRun run = PlayGoldenLoop(untilWave: 5);

        // 幕序：受辱 ≤ 觉醒 ≤ 打脸，严格不降（同一拍窗口允许并拍）。
        Assert.True(run.Act1Tick <= run.Act2Tick && run.Act2Tick < run.Act3Tick, $"幕序错位 {run.Act1Tick}/{run.Act2Tick}/{run.Act3Tick}");

        // 波锚：受辱/打脸幕=真波计数器 2/5（生产 WaveSpawner 实锚，非叙事自说自话）。
        // tick 公式：grace+(wave−1)×interval（WaveSpawner 冻结节奏——CS-4.4 公式驱动）。
        HandcraftedLevel level = HandcraftedLevelData.Load("e1.json").TryGet("campaign:level-e1-01")!;
        long intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        Assert.Equal(graceTicks + intervalTicks, run.Act1Tick); // 波 2 起点
        Assert.Equal(run.Act1Tick, run.Act2Tick); // 绑定幕与受辱幕同拍窗口（钩子路由同拍触发）
        Assert.Equal(graceTicks + (4 * intervalTicks), run.Act3Tick); // 波 5 起点

        // 15 分钟循环预算（§12.1）：第三幕−第一幕 ≤900s（实锚=3×240s=720s）。
        double loopSeconds = (run.Act3Tick - run.Act1Tick) / (double)Simulation.TicksPerSecond;
        Assert.Equal(3 * Campaign.WaveIntervalSeconds(level.Threat), loopSeconds, 6);
        Assert.True(run.Director.Golden.WithinBudget, $"循环跨度 {loopSeconds:F0}s 超预算");
        Assert.True(run.Director.Golden.Complete);

        // 台词链：三幕+开场+波 2/4 嘲讽全部下发（气泡面）。
        Assert.Contains(run.Trace, text => text.Contains("兽群踏碎了营地东篱"));
        Assert.Contains(run.Trace, text => text.Contains("绑定完成"));
        Assert.Contains(run.Trace, text => text.Contains("反推开始"));
        Assert.Equal(2, run.TauntsSeen.Count); // 打脸拍：波 2+波 4 嘲讽
    }

    [Fact] // legal — 确定性：同一命令流两次跑，幕 tick/台词迹全等（叙事零随机）
    public void The_golden_loop_is_deterministic_across_replays()
    {
        GoldenRun first = PlayGoldenLoop(untilWave: 5);
        GoldenRun second = PlayGoldenLoop(untilWave: 5);

        Assert.Equal((first.Act1Tick, first.Act2Tick, first.Act3Tick), (second.Act1Tick, second.Act2Tick, second.Act3Tick));
        Assert.Equal(first.Trace, second.Trace);
        Assert.Equal(first.TauntsSeen.Count, second.TauntsSeen.Count);
    }

    // ---------------------------------------------------------------- [F-100]

    [Fact] // legal — 全程通关（真胜利拍）：三幕齐→交底+卡点预告；命令流塔数 3=低配→打脸命中
    public void L1_1_full_clear_lands_the_face_slap_hidden_evaluation()
    {
        HandcraftedLevel level = HandcraftedLevelData.Load("e1.json").TryGet("campaign:level-e1-01")!;
        var config = new LevelSessionConfig(
            BuildConfig(level, "era1"), $"t089.full.{level.Id}", level.Threat, level.Map.Seed);
        var session = new LevelSession(config, new SimRng(level.Map.Seed), CreatePort());

        var director = new NarrativeDirector(
            NarrativeContent.StoryE1(),
            TrustBadge.Trustworthy,
            NarrPersona.Advisor,
            NarrativeContent.StoryE1().MockArcFor("campaign:level-e1-01"));

        session.Machine.StartLevel();
        foreach (SimCommand command in BuildCommands(level, "era1"))
        {
            session.Enqueue(command);
        }

        _ = director.Begin(); // 开场白下发后进入章节态（此后拍喂入合法）

        long intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * (double)level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long horizon = graceTicks + ((level.WinWaves + 2) * intervalTicks);

        var endingTrace = new List<DialogueLine>();
        int lastWave = session.Sim.CurrentWave;
        long steps = 0;
        while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
        {
            session.RunFor(BatchTicks);
            steps += BatchTicks;

            if (session.Machine.State == GameFlowState.Victory && endingTrace.Count == 0)
            {
                endingTrace.AddRange(director.OnVictory(session.Sim.CurrentTick));
            }
            else if (session.Sim.CurrentWave != lastWave)
            {
                lastWave = session.Sim.CurrentWave;
                _ = director.OnWaveStart(lastWave, session.Sim.CurrentTick);
                if (lastWave == 2)
                {
                    _ = director.OnCinematicFired("cinematic:e1-aha", session.Sim.CurrentTick);
                }
            }
        }

        Assert.Equal(GameFlowState.Victory, session.Machine.State); // 真通关（命令流面）
        Assert.Equal(3, director.ActsCompleted); // 三幕齐
        Assert.Equal(NarrativePhase.Ended, director.Phase);
        Assert.Contains(endingTrace, line => line.Key == "e1.end.handover"); // 交底
        Assert.Contains(endingTrace, line => line.Text.Contains("撞木")); // 卡点预告（E2 ram 威胁面）

        // 先定后演：胜利面武器塔=命令流三塔（两木箭+落石）→低配判定命中。
        int towersStanding = session.Sim.Towers.Count;
        MockArcRow arc = NarrativeContent.StoryE1().MockArcFor("campaign:level-e1-01")!;
        MockArcVerdict verdict = MockArcRulebook.Judge(arc, victory: true, towersStanding);
        Assert.InRange(towersStanding, 1, arc.LowConfig.MaxTowers);
        Assert.True(verdict.HiddenHit);
        Assert.Equal("gold_extra", verdict.HiddenChestTier);
    }

    // ---------------------------------------------------------------- [F-101]

    [Fact] // legal — 扮猪压制装配：L2-2 初始 1600 铜×0.5=800（压制参数应用于真 SimulationConfig）
    public void The_pig_arc_suppression_applies_to_the_real_level_config()
    {
        HandcraftedLevel level = HandcraftedLevelData.Load("e2.json").TryGet("campaign:level-e2-02")!;
        SimulationConfig config = BuildConfig(level, "era2");
        MockArcRow arc = NarrativeContent.StoryE2().MockArcFor("campaign:level-e2-02")!;

        double baseStock = config.InitialStock.Single(
            pair => pair.Key.Value == arc.Suppression!.Resource).Value;
        Assert.Equal(1600d, baseStock, 6); // 关卡行实锚

        var suppressed = new List<KeyValuePair<ResourceKey, double>>(
            config.InitialStock.Select(pair =>
                pair.Key.Value == arc.Suppression!.Resource
                    ? new KeyValuePair<ResourceKey, double>(pair.Key, MockArcRulebook.SuppressedStock(pair.Value, arc))
                    : pair));
        Assert.Equal(800d, suppressed.Single(pair => pair.Key.Value == arc.Suppression!.Resource).Value, 6);
    }
}
