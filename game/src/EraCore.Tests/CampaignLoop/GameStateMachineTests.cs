using EraCore.CampaignLoop;
using Xunit;

namespace EraCore.Tests.CampaignLoop;

// [C-12][F-001] 游戏循环状态机（T-059）：合法迁移全表 + 迁移史审计 + 非法
// 迁移逐条拦截（CS-3.1 三行：legal/boundary/illegal）。
public class GameStateMachineTests
{
    // ---------------------------------------------------------- legal

    [Fact] // legal — 完整胜利环：Menu→InLevel→Victory→Menu（v3.2 §3 单局循环主链）
    public void Victory_path_walks_menu_level_victory_menu()
    {
        var machine = new GameStateMachine();

        machine.StartLevel();
        machine.Win();
        machine.CompleteLevel();

        Assert.Equal(GameFlowState.Menu, machine.State);
        Assert.Equal(3, machine.TransitionCount);
        Assert.Equal(
            new[]
            {
                new GameFlowTransition(GameFlowTransitionKind.StartLevel, GameFlowState.Menu, GameFlowState.InLevel),
                new GameFlowTransition(GameFlowTransitionKind.Win, GameFlowState.InLevel, GameFlowState.Victory),
                new GameFlowTransition(GameFlowTransitionKind.CompleteLevel, GameFlowState.Victory, GameFlowState.Menu),
            },
            machine.History);
    }

    [Fact] // legal — 暂停环与中途退出环：InLevel↔Paused + Paused→Menu（存档退出唯一合法起点）
    public void Pause_and_exit_paths_are_legal()
    {
        var machine = new GameStateMachine();

        machine.StartLevel();
        machine.Pause();
        machine.Resume();
        machine.Pause();
        machine.SaveAndExit();

        Assert.Equal(GameFlowState.Menu, machine.State);
        Assert.Equal(
            new[]
            {
                GameFlowTransitionKind.StartLevel,
                GameFlowTransitionKind.Pause,
                GameFlowTransitionKind.Resume,
                GameFlowTransitionKind.Pause,
                GameFlowTransitionKind.SaveAndExit,
            },
            machine.History.Select(t => t.Kind));
    }

    [Fact] // legal — 失败环：InLevel→Defeat→Menu（分档后果由 T-057 FailureTiers 承接）
    public void Defeat_path_is_legal()
    {
        var machine = new GameStateMachine();

        machine.StartLevel();
        machine.Lose();
        machine.AcknowledgeDefeat();

        Assert.Equal(GameFlowState.Menu, machine.State);
        Assert.Equal(
            new[]
            {
                new GameFlowTransition(GameFlowTransitionKind.Lose, GameFlowState.InLevel, GameFlowState.Defeat),
                new GameFlowTransition(GameFlowTransitionKind.AcknowledgeDefeat, GameFlowState.Defeat, GameFlowState.Menu),
            },
            machine.History.Skip(1));
    }

    // ---------------------------------------------------------- boundary

    [Fact] // boundary — 全新机器：Menu 态、零迁移、历史空
    public void Fresh_machine_sits_at_menu_with_empty_history()
    {
        var machine = new GameStateMachine();

        Assert.Equal(GameFlowState.Menu, machine.State);
        Assert.Equal(0, machine.TransitionCount);
        Assert.Empty(machine.History);
    }

    [Fact] // boundary — 闭环后可再开新关（Menu→InLevel 二次成立：选下一张图）
    public void Machine_reopens_a_new_level_after_a_completed_one()
    {
        var machine = new GameStateMachine();
        machine.StartLevel();
        machine.Win();
        machine.CompleteLevel();

        machine.StartLevel();

        Assert.Equal(GameFlowState.InLevel, machine.State);
        Assert.Equal(4, machine.TransitionCount);
    }

    // ---------------------------------------------------------- illegal

    [Theory] // illegal — 非法迁移逐条确实被拒（具体异常断言，CS-3.5）
    [InlineData("start-from-inlevel")]      // Menu 之外的任何态不能 StartLevel
    [InlineData("pause-from-menu")]
    [InlineData("pause-from-paused")]
    [InlineData("resume-from-inlevel")]
    [InlineData("exit-from-inlevel")]       // 运行中直接退出被拒（必须先 Pause）
    [InlineData("exit-from-menu")]
    [InlineData("win-from-menu")]
    [InlineData("win-from-paused")]
    [InlineData("win-from-victory")]
    [InlineData("lose-from-paused")]
    [InlineData("complete-from-inlevel")]   // 结算返回只能从 Victory
    [InlineData("complete-from-menu")]
    [InlineData("acknowledge-from-inlevel")]
    [InlineData("acknowledge-from-menu")]
    public void Illegal_transition_is_rejected(string scenario)
    {
        var machine = new GameStateMachine();
        PrepareIllegalScenario(machine, scenario);
        (Action illegal, GameFlowTransitionKind kind) = IllegalMove(machine, scenario);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(illegal);
        Assert.Contains(kind.ToString(), ex.Message); // 拒绝信息指明被拒迁移
    }

    private static void PrepareIllegalScenario(GameStateMachine machine, string scenario)
    {
        switch (scenario)
        {
            case "pause-from-paused":
            case "win-from-paused":
            case "lose-from-paused":
                machine.StartLevel();
                machine.Pause();
                break;
            case "win-from-victory":
                machine.StartLevel();
                machine.Win();
                break;
            case "start-from-inlevel":
            case "resume-from-inlevel":
            case "exit-from-inlevel":
            case "complete-from-inlevel":
            case "acknowledge-from-inlevel":
                machine.StartLevel();
                break;
            default:
                // Menu-based scenarios need no setup.
                break;
        }
    }

    private static (Action Move, GameFlowTransitionKind Kind) IllegalMove(GameStateMachine machine, string scenario) =>
        scenario switch
        {
            "start-from-inlevel" => (machine.StartLevel, GameFlowTransitionKind.StartLevel),
            "pause-from-menu" => (machine.Pause, GameFlowTransitionKind.Pause),
            "pause-from-paused" => (machine.Pause, GameFlowTransitionKind.Pause),
            "resume-from-inlevel" => (machine.Resume, GameFlowTransitionKind.Resume),
            "exit-from-inlevel" => (machine.SaveAndExit, GameFlowTransitionKind.SaveAndExit),
            "exit-from-menu" => (machine.SaveAndExit, GameFlowTransitionKind.SaveAndExit),
            "win-from-menu" => (machine.Win, GameFlowTransitionKind.Win),
            "win-from-paused" => (machine.Win, GameFlowTransitionKind.Win),
            "win-from-victory" => (machine.Win, GameFlowTransitionKind.Win),
            "lose-from-paused" => (machine.Lose, GameFlowTransitionKind.Lose),
            "complete-from-inlevel" => (machine.CompleteLevel, GameFlowTransitionKind.CompleteLevel),
            "complete-from-menu" => (machine.CompleteLevel, GameFlowTransitionKind.CompleteLevel),
            "acknowledge-from-inlevel" => (machine.AcknowledgeDefeat, GameFlowTransitionKind.AcknowledgeDefeat),
            "acknowledge-from-menu" => (machine.AcknowledgeDefeat, GameFlowTransitionKind.AcknowledgeDefeat),
            _ => throw new ArgumentException($"Unknown scenario {scenario}.", nameof(scenario)),
        };
}
