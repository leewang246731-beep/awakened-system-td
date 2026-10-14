using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-113] [F-114] T-086 演出时间轴纯逻辑：步骤推进（计时/瞬发）、
// 事件等待（缓冲/误键不满足）、三选一停泊与解析、任意时刻跳过=同一终态
// 命令集（跳过等价的结构保证）、确定性双跑（同脚本+同输入调度→效果迹全等）、
// 非法调用拒绝（未 Start 即 Tick/越界选择/空事件键）。
public class CinematicTimelineTests
{
    private static CinematicScript Script(
        IReadOnlyList<CinematicStep> steps,
        IReadOnlyList<CinematicWorldCommand>? terminal = null) =>
        new("cinematic:test", null, "测试脚本", steps, terminal ?? Array.Empty<CinematicWorldCommand>());

    private static CinematicScript AwakenLikeScript() => Script(new CinematicStep[]
    {
        new CinematicStep.Screen("black_fade_in", 10),
        new CinematicStep.Choice(
            "选一道军令：",
            new CinematicChoiceOption[]
            {
                new("守住东门", new CinematicWorldCommand[] { new CinematicWorldCommand.IssueOrder("守住东门") }),
                new("敌情怎么样", new CinematicWorldCommand[] { new CinematicWorldCommand.IssueOrder("敌情怎么样") }),
                new("在东门建两座木箭塔", new CinematicWorldCommand[] { new CinematicWorldCommand.IssueOrder("在东门建两座木箭塔") }),
            },
            DefaultIndex: 0),
        new CinematicStep.WaitEvent("order_settled"),
        new CinematicStep.SkipPoint("done"),
    });

    private static string TraceOf(CinematicEffect effect) => effect switch
    {
        CinematicEffect.CameraMove camera => $"camera:{camera.Target.Kind}:{camera.DurationTicks}",
        CinematicEffect.Highlight highlight => $"highlight:{highlight.Flavor}",
        CinematicEffect.Speak speak => $"speak:{speak.Speaker}:{speak.Text}:{speak.DurationTicks}",
        CinematicEffect.SoundCue cue => $"sound:{cue.Cue}",
        CinematicEffect.Screen screen => $"screen:{screen.Flavor}:{screen.DurationTicks}",
        CinematicEffect.UiSurface ui => $"ui:{ui.Surface}:{ui.Ledger.Count}",
        CinematicEffect.ChoicePrompted choice => $"choicePrompt:{choice.OptionLabels.Count}",
        CinematicEffect.ChoiceResolved resolved => $"choiceResolved:{resolved.Index}",
        CinematicEffect.SkipPointReached skipPoint => $"checkpoint:{skipPoint.Id}",
        CinematicEffect.Skipped skipped => $"skipped:{skipped.AtStep}",
        CinematicEffect.Completed => "completed",
        _ => "unknown",
    };

    // ---------------------------------------------------------------- legal

    [Fact] // legal — Start 激活第 0 步并回放其入步效果；瞬发布骤（音效）当 tick 完成
    public void Start_activates_step_zero_and_returns_its_enter_effects()
    {
        var timeline = new CinematicTimeline(Script(new CinematicStep[]
        {
            new CinematicStep.Sound("ding_crisis"),
            new CinematicStep.Speak("系统", "叮！", 5),
        }));

        IReadOnlyList<CinematicEffect> effects = timeline.Start();

        Assert.Equal(CinematicPhase.Playing, timeline.Phase);
        CinematicEffect.SoundCue cue = Assert.IsType<CinematicEffect.SoundCue>(effects[0]);
        Assert.Equal("ding_crisis", cue.Cue);
        Assert.Equal(0, timeline.CurrentStepIndex);
    }

    [Fact] // legal — 计时步骤按拍推进：激活 tick 出效果，其后 duration 拍静默，下一拍后继入步
    public void Timed_steps_advance_one_per_tick()
    {
        var timeline = new CinematicTimeline(Script(new CinematicStep[]
        {
            new CinematicStep.CameraMove(
                new CinematicTarget(CinematicTargetKind.Cell, new CellCoord(6, 21)), 30),
            new CinematicStep.Sound("ding_reward"),
        }));
        timeline.Start();

        for (int tick = 1; tick <= 30; tick++)
        {
            Assert.Empty(timeline.Tick()); // camera countdown emits nothing new
            Assert.Equal(0, timeline.CurrentStepIndex); // camera stays the last-activated step
        }

        Assert.Equal(30, timeline.ElapsedTicks);
        Assert.Equal(0, timeline.CurrentStepIndex); // tick 30 expired the camera; successor lands next tick
        IReadOnlyList<CinematicEffect> next = timeline.Tick();
        CinematicEffect.SoundCue cue = Assert.IsType<CinematicEffect.SoundCue>(Assert.Single(next));
        Assert.Equal("ding_reward", cue.Cue);
        Assert.Equal(1, timeline.CurrentStepIndex);
    }

    [Fact] // legal — 事件等待：未通知不动，通知后下一拍放行；提前缓冲同样满足
    public void WaitEvent_blocks_until_the_matching_key_is_notified()
    {
        var timeline = new CinematicTimeline(Script(new CinematicStep[]
        {
            new CinematicStep.Sound("ding_info"),
            new CinematicStep.WaitEvent("ammo_restored"),
            new CinematicStep.SkipPoint("after"),
        }));
        timeline.Start(); // step 0 sound（瞬发）

        timeline.Tick(); // step 1 wait_event 激活
        timeline.NotifyEvent("order_settled"); // 误键：不满足
        Assert.Empty(timeline.Tick()); // 仍在等待

        timeline.NotifyEvent("ammo_restored");
        Assert.Empty(timeline.Tick()); // 满足拍：本拍出清，后继下一拍激活
        IReadOnlyList<CinematicEffect> next = timeline.Tick();
        CinematicEffect.SkipPointReached checkpoint =
            Assert.IsType<CinematicEffect.SkipPointReached>(next[0]);
        Assert.Equal("after", checkpoint.Id);
    }

    [Fact] // legal — 三选一停泊：停泊拍出 ChoicePrompted，选择后解析拍+预置命令就绪
    public void Choice_parks_until_resolved_then_queues_the_options_commands()
    {
        CinematicScript awaken = AwakenLikeScript();
        var timeline = new CinematicTimeline(awaken);
        timeline.Start(); // screen step

        for (int tick = 0; tick < 12 && timeline.Phase != CinematicPhase.AwaitingChoice; tick++)
        {
            timeline.Tick();
        }

        Assert.Equal(CinematicPhase.AwaitingChoice, timeline.Phase);
        CinematicStep.Choice choice = Assert.IsType<CinematicStep.Choice>(timeline.CurrentStep);
        Assert.Equal(3, choice.Options.Count);
        Assert.Empty(timeline.Tick()); // 停泊中：tick 空转
        Assert.Null(timeline.SelectedChoice);

        IReadOnlyList<CinematicEffect> resolved = timeline.SelectChoice(1);

        CinematicEffect.ChoiceResolved effect = Assert.IsType<CinematicEffect.ChoiceResolved>(resolved[0]);
        Assert.Equal(1, effect.Index);
        Assert.Equal(CinematicPhase.Playing, timeline.Phase);
        Assert.Equal(1, timeline.SelectedChoice);

        CinematicWorldCommand.IssueOrder order =
            Assert.IsType<CinematicWorldCommand.IssueOrder>(Assert.Single(timeline.TakePendingCommands()));
        Assert.Equal("敌情怎么样", order.OrderText);
    }

    [Theory] // legal — 跳过等价（核心验收）：任意时刻跳过=同一终态命令集（默认或已选）
    [InlineData(0)] // 选择前跳过 → 默认选项 0
    [InlineData(1)]
    [InlineData(12)] // 停泊在选择步上跳过 → 仍默认
    public void Skip_from_any_moment_yields_the_declared_terminal_command_set(int skipAfterTicks)
    {
        CinematicScript awaken = AwakenLikeScript();
        var fullRun = new CinematicTimeline(awaken);
        fullRun.Start();
        for (int tick = 0; tick < 20 && fullRun.Phase != CinematicPhase.AwaitingChoice; tick++)
        {
            fullRun.Tick();
        }

        fullRun.SelectChoice(0);
        while (fullRun.Phase != CinematicPhase.Completed)
        {
            fullRun.NotifyEvent("order_settled");
            fullRun.Tick();
        }

        IReadOnlyList<CinematicWorldCommand> fullSet = fullRun.TakePendingCommands();

        var skipRun = new CinematicTimeline(awaken);
        skipRun.Start();
        for (int tick = 0; tick < skipAfterTicks; tick++)
        {
            skipRun.Tick();
        }

        bool skippedEffect = false;
        foreach (CinematicEffect effect in skipRun.Skip())
        {
            skippedEffect |= effect is CinematicEffect.Skipped;
        }

        Assert.True(skippedEffect);
        Assert.Equal(CinematicPhase.Completed, skipRun.Phase);
        Assert.True(skipRun.Skipped);
        Assert.Equal(fullSet, skipRun.TakePendingCommands()); // 命令集等价（同选择）
        Assert.Equal(fullRun.TerminalCommands, skipRun.TerminalCommands);
    }

    [Fact] // legal — 无选择脚本：自然放演与跳过都恰好落到脚本级终态命令
    public void Choiceless_script_completes_and_skips_into_the_same_terminal_commands()
    {
        var terminal = new CinematicWorldCommand[]
        {
            new CinematicWorldCommand.EnqueueSim(0, SimCommandKind.TimeControl, "{\"speed\":1}"),
        };
        var script = Script(new CinematicStep[] { new CinematicStep.Sound("ding_info") }, terminal);

        var played = new CinematicTimeline(script);
        played.Start();
        while (played.Phase != CinematicPhase.Completed)
        {
            played.Tick();
        }

        Assert.Equal(terminal, played.TakePendingCommands());

        var skipped = new CinematicTimeline(script);
        skipped.Start();
        skipped.Skip();

        Assert.Equal(terminal, skipped.TakePendingCommands());
        Assert.True(skipped.Skipped);
    }

    [Fact] // legal — 确定性双跑：同脚本+同通知/选择调度 → 效果迹全等（逐拍字符串拼接）
    public void Two_runs_with_the_same_inputs_produce_identical_effect_traces()
    {
        string RunOnce()
        {
            var timeline = new CinematicTimeline(AwakenLikeScript());
            var trace = new List<string> { string.Join("|", timeline.Start().Select(TraceOf)) };
            int tick = 0;
            while (timeline.Phase != CinematicPhase.Completed && tick < 64)
            {
                if (timeline.Phase == CinematicPhase.AwaitingChoice)
                {
                    trace.Add(string.Join("|", timeline.SelectChoice(2).Select(TraceOf)));
                }

                if (tick == 3)
                {
                    timeline.NotifyEvent("order_settled"); // 提前缓冲
                }

                trace.Add(string.Join("|", timeline.Tick().Select(TraceOf)));
                tick++;
            }

            return string.Join(";", trace);
        }

        Assert.Equal(RunOnce(), RunOnce());
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 已完成后：Tick/继续等待均空转，Skip 幂等空返
    public void Completed_timeline_is_inert()
    {
        var timeline = new CinematicTimeline(AwakenLikeScript());
        timeline.Start();
        Assert.Contains(timeline.Skip(), effect => effect is CinematicEffect.Skipped);
        Assert.NotEmpty(timeline.TakePendingCommands()); // 选前跳过→默认选项命令已就绪（一次性）

        Assert.Empty(timeline.Tick());
        Assert.Empty(timeline.Skip());
        Assert.Empty(timeline.TakePendingCommands());
    }

    [Fact] // boundary — 事件先于等待步到达（缓冲区持有），激活后第一拍即放行
    public void Event_notified_before_the_wait_step_activates_still_satisfies_it()
    {
        var timeline = new CinematicTimeline(Script(new CinematicStep[]
        {
            new CinematicStep.WaitEvent("ammo_restored"),
        }));
        timeline.Start();
        timeline.NotifyEvent("ammo_restored"); // 激活拍内先通知
        Assert.Empty(timeline.Tick()); // 第一拍：消费+出清（无后继，完结在下一拍）

        Assert.IsType<CinematicEffect.Completed>(Assert.Single(timeline.Tick()));
        Assert.Equal(CinematicPhase.Completed, timeline.Phase);
        Assert.Empty(timeline.TakePendingCommands()); // 终态命令空集
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 未 Start 即 Tick：拒绝
    public void Tick_before_Start_is_rejected()
    {
        var timeline = new CinematicTimeline(AwakenLikeScript());

        Assert.Throws<InvalidOperationException>(() => timeline.Tick());
    }

    [Fact] // illegal — 未 Start 即 Skip：拒绝
    public void Skip_before_Start_is_rejected()
    {
        var timeline = new CinematicTimeline(AwakenLikeScript());

        Assert.Throws<InvalidOperationException>(() => timeline.Skip());
    }

    [Fact] // illegal — 非停泊态 SelectChoice：拒绝
    public void SelectChoice_outside_a_parked_choice_is_rejected()
    {
        var timeline = new CinematicTimeline(AwakenLikeScript());
        timeline.Start();

        Assert.Throws<InvalidOperationException>(() => timeline.SelectChoice(0));
    }

    [Theory] // illegal — 选择越界（负/超上限）：拒绝
    [InlineData(-1)]
    [InlineData(3)]
    public void SelectChoice_out_of_range_is_rejected(int index)
    {
        var timeline = new CinematicTimeline(AwakenLikeScript());
        timeline.Start();
        for (int tick = 0; tick < 12 && timeline.Phase != CinematicPhase.AwaitingChoice; tick++)
        {
            timeline.Tick();
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.SelectChoice(index));
    }

    [Theory] // illegal — 空白事件键：拒绝
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NotifyEvent_with_a_blank_key_is_rejected(string? key)
    {
        var timeline = new CinematicTimeline(AwakenLikeScript());

        Assert.Throws<ArgumentException>(() => timeline.NotifyEvent(key!));
    }

    [Fact] // illegal — 双重 Start：拒绝
    public void Start_twice_is_rejected()
    {
        var timeline = new CinematicTimeline(AwakenLikeScript());
        timeline.Start();

        Assert.Throws<InvalidOperationException>(() => timeline.Start());
    }
}
