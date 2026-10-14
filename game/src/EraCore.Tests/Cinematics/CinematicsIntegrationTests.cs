using EraCore.Agent.Pipeline;
using EraCore.Cinematics;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Supply;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Cinematics.CinematicStep;
using static EraCore.Cinematics.CinematicWorldCommand;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-113] [F-114] T-086 集成级验收：E1 啊哈在真模拟上放演零状态污染
// （孪生跑哈希逐段全等+世界命令恒为 0+演出期 AppliedCommandCount 不动）、
// 跳过=同一零效果终态；E2 觉醒三选一走真实五段管线（方案卡→Confirm→塔
// 落地）、确定性孪生（同 seed 同拍发令→哈希全等，演出不扰动确定性）、
// 跳过终态命令集与全放演等价。
public class CinematicsIntegrationTests
{
    /// <summary>E1 aha delivery watch cell: tower1 of the milestone stream (SimE1Fixture.TowerCell).</summary>
    private static readonly CellCoord TowerCell = SimE1Fixture.TowerCell;

    private static (Simulation Sim, long AppliedAtStart) BuildMilestoneSim()
    {
        var sim = new Simulation(
            SimE1Fixture.Config(initialStock: new List<KeyValuePair<ResourceKey, double>>
            {
                new(SimE1Fixture.Wood, 200.0),
            }),
            new SimRng(SimE1Fixture.Seed),
            SimE1Fixture.Seed);
        sim.EnqueueRange(SimE1Fixture.MilestoneCommands());
        for (int i = 0; i < 700; i++)
        {
            sim.Step(); // milestone stream (last command tick 600) fully applied
        }

        return (sim, sim.AppliedCommandCount);
    }

    /// <summary>
    /// Read-only ammo observation for the watched tower cell: (accepted
    /// magazine items, shots remaining in the weapon).
    /// </summary>
    private static (long Accepted, int Shots) AmmoState(Simulation sim)
    {
        foreach (SimTower tower in sim.Towers)
        {
            if (tower.Cell == TowerCell && sim.TryGetTowerMagazine(tower.Id, out TowerMagazine? magazine))
            {
                return (magazine.ItemsAccepted, tower.Weapon.AmmoShotsRemaining);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// 恢复判定 (§10.4 ①): the ammo line counts as restored when the magazine
    /// accepted a delivery since the baseline (under fire) OR already holds a
    /// fed magazine — ≥1 accepted item and ≥ one item's worth of shots (the
    /// idle no-enemies case: the porter-filled magazine stays full).
    /// </summary>
    private static bool AmmoRestored(Simulation sim, long baselineAccepted) =>
        AmmoState(sim) is var (accepted, shots)
        && (accepted > baselineAccepted
            || (accepted >= 1 && shots >= EraCore.Logistics.Carrier.ShotsPerItem));

    // ---------------------------------------------------------------- [F-113]

    [Fact] // legal — E1 啊哈全放演：孪生跑零状态污染（哈希逐段全等+命令恒 0）
    public void E1_aha_performs_on_a_real_simulation_with_zero_world_pollution()
    {
        (Simulation played, long appliedAtStart) = BuildMilestoneSim();
        (Simulation twin, _) = BuildMilestoneSim();
        Assert.Equal(played.ComputeStateHash(), twin.ComputeStateHash()); // 孪生起点全等

        var timeline = new CinematicTimeline(CinematicContent.E1Aha());
        long acceptedBaseline = AmmoState(played).Accepted;
        _ = timeline.Start();

        int ticks = 0;
        while (timeline.Phase != CinematicPhase.Completed && ticks < 2400)
        {
            played.Step();
            twin.Step();

            if (AmmoRestored(played, acceptedBaseline))
            {
                timeline.NotifyEvent("ammo_restored"); // 真实世界信号：弹药线恢复
            }

            _ = timeline.Tick();
            Assert.Empty(timeline.TakePendingCommands()); // 演出期间世界命令恒为 0

            if (ticks % 100 == 0)
            {
                Assert.Equal(twin.ComputeStateHash(), played.ComputeStateHash()); // 逐段全等
            }

            ticks++;
        }

        Assert.Equal(CinematicPhase.Completed, timeline.Phase);
        Assert.False(timeline.Skipped);
        Assert.Equal(appliedAtStart, played.AppliedCommandCount); // 零污染：一个命令都没多发
        Assert.Equal(twin.ComputeStateHash(), played.ComputeStateHash()); // 终点全等

        while (ticks < 1200)
        {
            played.Step();
            twin.Step();
            ticks++;
        }

        Assert.Equal(twin.ComputeStateHash(), played.ComputeStateHash()); // 放演后长跑仍确定
    }

    [Fact] // legal — E1 啊哈任意时刻跳过：终态=零命令集，世界与孪生跑全等
    public void E1_aha_skipped_at_any_moment_leaves_the_identical_world_state()
    {
        (Simulation skipped, _) = BuildMilestoneSim();
        (Simulation twin, _) = BuildMilestoneSim();

        var timeline = new CinematicTimeline(CinematicContent.E1Aha());
        _ = timeline.Start();
        for (int i = 0; i < 3; i++)
        {
            skipped.Step();
            twin.Step();
            _ = timeline.Tick();
        }

        Assert.Contains(timeline.Skip(), effect => effect is CinematicEffect.Skipped);
        Assert.Empty(timeline.TakePendingCommands()); // 跳过终态命令集=空（与全放演等价）

        for (int i = 3; i < 1200; i++)
        {
            skipped.Step();
            twin.Step();
        }

        Assert.Equal(twin.ComputeStateHash(), skipped.ComputeStateHash());
    }

    // ---------------------------------------------------------------- [F-114]

    [Fact] // legal — E2 觉醒全放演：三选一→方案卡→Confirm→真实管线→塔列队落地
    public void E2_awakening_runs_the_first_order_through_the_real_pipeline()
    {
        using var harness = new PipelineHarness();
        var timeline = new CinematicTimeline(CinematicContent.E2Awaken());
        var drained = new List<CinematicWorldCommand>();
        bool sawPlanCard = false;
        PipelineOutcomeKind? settledKind = null;
        int resolvedAtTick = -1;

        _ = timeline.Start();
        int ticks = 0;
        while (timeline.Phase != CinematicPhase.Completed && ticks < 700)
        {
            harness.Simulation.Step();
            _ = timeline.Tick();
            if (timeline.Phase == CinematicPhase.AwaitingChoice)
            {
                _ = timeline.SelectChoice(0); // 玩家确认：第一道预填军令
                resolvedAtTick = ticks + 1;
            }

            foreach (CinematicWorldCommand command in timeline.TakePendingCommands())
            {
                drained.Add(command);
                if (command is IssueOrder order)
                {
                    OrderOutcome outcome = harness.Pipeline.Handle(order.OrderText);
                    sawPlanCard |= outcome.Kind == PipelineOutcomeKind.AwaitingApproval;
                    if (outcome.Kind == PipelineOutcomeKind.AwaitingApproval)
                    {
                        outcome = harness.Pipeline.Confirm(outcome.PendingId); // 方案卡确认→执行
                    }

                    settledKind = outcome.Kind;
                    timeline.NotifyEvent("order_settled"); // 管线终态回填等待步
                }
            }

            ticks++;
        }

        while (ticks < 800)
        {
            harness.Simulation.Step();
            ticks++;
        }

        CinematicStep.Choice choice = CinematicContent.E2Awaken().Steps.OfType<Choice>().Single();
        Assert.Equal(choice.Options[0].OnConfirm, drained); // 放演命令集=选项 0 预填
        Assert.True(sawPlanCard, "预填军令必须先出方案卡（§11.2 计划审批）");
        Assert.Equal(PipelineOutcomeKind.Completed, settledKind);
        Assert.Equal(2, harness.Simulation.Towers.Count); // 塔列队开建（HoldRegion qty 2）
        Assert.True(resolvedAtTick > 0);
    }

    [Fact] // legal — 确定性孪生：同 seed 同拍发令，放演跑与直发跑哈希全等（演出不扰动确定性）
    public void E2_awakening_cinematic_run_matches_a_direct_order_twin()
    {
        string firstOrder = DefaultFirstOrder();
        long cinematicHash = PlayAwakenCinematic(out int resolvedAtTick);
        long directHash = PlayDirectOrder(resolvedAtTick, firstOrder);

        Assert.Equal(directHash, cinematicHash);
    }

    [Fact] // legal — 跳过等价：选前跳过 → 终态命令集=默认选项（与全放演同集）
    public void E2_awakening_skip_before_the_choice_drains_the_default_terminal_set()
    {
        using var harness = new PipelineHarness();
        var timeline = new CinematicTimeline(CinematicContent.E2Awaken());
        var drained = new List<CinematicWorldCommand>();

        _ = timeline.Start();
        for (int i = 0; i < 3; i++)
        {
            harness.Simulation.Step();
            _ = timeline.Tick();
        }

        Assert.Contains(timeline.Skip(), effect => effect is CinematicEffect.Skipped);
        drained.AddRange(timeline.TakePendingCommands());

        CinematicStep.Choice choice = CinematicContent.E2Awaken().Steps.OfType<Choice>().Single();
        Assert.Equal(choice.Options[choice.DefaultIndex].OnConfirm, drained); // 默认选项命令集
        foreach (CinematicWorldCommand command in drained)
        {
            if (command is IssueOrder order)
            {
                OrderOutcome outcome = harness.Pipeline.Handle(order.OrderText);
                if (outcome.Kind == PipelineOutcomeKind.AwaitingApproval)
                {
                    outcome = harness.Pipeline.Confirm(outcome.PendingId);
                }

                Assert.Equal(PipelineOutcomeKind.Completed, outcome.Kind); // 跳过路径同样走通真管线
            }
        }
    }

    /// <summary>
    /// Full cinematic play on a fresh harness (choice 0, drain-and-apply at the
    /// resolution tick); returns the state hash after 800 world steps.
    /// </summary>
    private static long PlayAwakenCinematic(out int resolvedAtTick)
    {
        using var harness = new PipelineHarness();
        var timeline = new CinematicTimeline(CinematicContent.E2Awaken());
        resolvedAtTick = -1;
        _ = timeline.Start();
        int ticks = 0;
        while (ticks < 800)
        {
            harness.Simulation.Step();
            if (timeline.Phase != CinematicPhase.Completed)
            {
                _ = timeline.Tick();
                if (timeline.Phase == CinematicPhase.AwaitingChoice)
                {
                    _ = timeline.SelectChoice(0);
                    resolvedAtTick = ticks + 1;
                }

                foreach (CinematicWorldCommand command in timeline.TakePendingCommands())
                {
                    if (command is IssueOrder order)
                    {
                        OrderOutcome outcome = harness.Pipeline.Handle(order.OrderText);
                        if (outcome.Kind == PipelineOutcomeKind.AwaitingApproval)
                        {
                            outcome = harness.Pipeline.Confirm(outcome.PendingId);
                        }

                        timeline.NotifyEvent("order_settled");
                    }
                }
            }

            ticks++;
        }

        Assert.True(resolvedAtTick > 0, "觉醒三选一必须在 800 tick 内解析");
        return harness.Simulation.ComputeStateHash();
    }

    /// <summary>The default first choice's order text straight out of the content script.</summary>
    private static string DefaultFirstOrder()
    {
        CinematicStep.Choice choice = CinematicContent.E2Awaken().Steps.OfType<Choice>().Single();
        return Assert.IsType<IssueOrder>(
            Assert.Single(choice.Options[choice.DefaultIndex].OnConfirm)).OrderText;
    }

    /// <summary>Direct twin: the same order issued through the pipeline at the same world tick, no timeline.</summary>
    private static long PlayDirectOrder(int issueAfterSteps, string orderText)
    {
        using var harness = new PipelineHarness();
        for (int ticks = 0; ticks < 800; ticks++)
        {
            harness.Simulation.Step();
            if (ticks + 1 == issueAfterSteps)
            {
                OrderOutcome outcome = harness.Pipeline.Handle(orderText);
                if (outcome.Kind == PipelineOutcomeKind.AwaitingApproval)
                {
                    outcome = harness.Pipeline.Confirm(outcome.PendingId);
                }

                Assert.Equal(PipelineOutcomeKind.Completed, outcome.Kind);
            }
        }

        return harness.Simulation.ComputeStateHash();
    }
}
