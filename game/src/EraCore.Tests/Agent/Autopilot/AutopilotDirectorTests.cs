using EraCore.Agent.Autopilot;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Intents;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Supply;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Autopilot;

// [F-093][C-13] T-081 全托管导演（AutopilotDirector）：波内循环（意图→计划→≤3 动作→汇报）、
// min(10,点数) 迭代上限执法、军师令预算门、断供事件响应、接管降级（当波罚则不回退）与波守卫。
// 复用 T-053 真实 E1 夹具 + T-068 五段管线（CS-3.4 禁 mock 被测系统：真模拟、真管线、真点数账本）。
public class AutopilotDirectorTests
{
    private static readonly CellCoord EastGate = PipelineHarness.EastGateAnchor;
    private static readonly CellCoord SouthGate = PipelineHarness.SouthGateAnchor;

    /// <summary>
    /// Fresh director over the standard harness. NOTE: PipelineHarness's
    /// constructor already begins ledger wave 1 (the host-side hook) — the
    /// director's wave numbers therefore start at 2 (the ledger's
    /// monotonicity guard rejects a re-begun wave 1; the wave label is not
    /// load-bearing for any assertion here).
    /// </summary>
    private static PipelineHarness NewHarness(
        out AutopilotDirector director,
        Func<AutopilotDirector, AutopilotIntentDraft?>? intentSource = null,
        int eraIndex = PipelineHarness.AgentEra)
    {
        var harness = new PipelineHarness(eraIndex: eraIndex);
        director = AutopilotDirector.OverSimulation(
            harness.Pipeline,
            harness.Simulation,
            PipelineHarness.Config(),
            new Dictionary<string, CellCoord>
            {
                ["east_gate"] = EastGate,
                ["south_gate"] = SouthGate,
            },
            intentSource: intentSource);
        return harness;
    }

    /// <summary>Pre-defends both anchors with one tower each (skips the HoldRegion branch).</summary>
    /// <param name="tick">Command tick — order matters for entity ids (see the chain tests).</param>
    private static void DefendAnchors(Simulation simulation, long tick = 1)
    {
        simulation.Enqueue(SimCommands.BuildTower(tick, "era1:tower-wood-arrow", EastGate));
        simulation.Enqueue(SimCommands.BuildTower(tick, "era1:tower-wood-arrow", SouthGate));
        simulation.Run(1);
    }

    private static SupplyBottleneckEvent Bottleneck(long towerId, long seq) => new(
        Tick: 60,
        Seq: seq,
        TowerId: new EntityId(towerId),
        TowerPosition: new CellCoord(6, 21),
        Efficiency: 0.2,
        Threshold: 0.5,
        DemandShotsPerSecond: 1.0,
        DeliveryShotsPerSecond: 0.1,
        SuggestionKind: SupplyBottleneckSuggestionKind.None,
        SuggestedCell: null,
        SuggestedRelayId: null,
        SuggestedRelayStock: null);

    // ------------------------------------------------ ① 默认意图源：HoldRegion 优先 -----------------------

    [Fact] // legal：全托管首轮自主建塔——HoldRegion 优先，侦察+建塔真落子，配额 2/3
    public void First_full_autopilot_round_builds_defense_autonomously()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        Assert.True(director.BeginWave(2, isBossWave: false));

        IReadOnlyList<AutopilotRoundResult> rounds = director.RunWave();

        AutopilotRoundResult round = Assert.Single(rounds);
        Assert.Equal(1, round.Round);
        Assert.Equal(IntentKind.HoldRegion, round.Intent);
        Assert.Equal("wave-2", round.Trigger);
        Assert.Equal(2, round.Actions.Count); // LookAround + BuildPlan（轮末汇报不计入 Actions）
        Assert.Equal(2, round.QuotaUsed);     // ≤3 配额内
        Assert.False(round.QuotaRefused);
        Assert.All(round.Actions, action => Assert.Equal("Ok", action.Status));
        Assert.Equal(2, harness.Simulation.Towers.Count); // 真建了 2 座（qty 2 与 SP-2 模板同对）
        Assert.Equal(2, director.PointsSpentByAutopilot); // 建造 2 点（侦察 0）
        Assert.Equal(3, harness.Pipeline.Ledger.Points);  // 5-2
        Assert.Equal(1, director.RoundsUsedThisWave);
    }

    [Fact] // legal：军师令 40% 波上限钳制——第二轮建案预算不允许，导演如实等待不硬闯
    public void Second_round_is_skipped_honestly_when_the_wave_budget_cannot_cover_it()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);

        // E2：ceiling 5、波上限 ⌊5×40%⌋=2 —— 第一轮 BuildPlan(2 点) 已打满
        IReadOnlyList<AutopilotRoundResult> first = director.RunWave();
        Assert.Single(first);

        // 再来一轮：预算不允许（波上限 0 余量）→ 无可行动意图 → 空转等待
        IReadOnlyList<AutopilotRoundResult> second = director.RunWave();
        Assert.Empty(second);
        Assert.Equal(2, harness.Simulation.Towers.Count); // 没有第二次建造
        Assert.Equal(1, director.RoundsUsedThisWave);
    }

    [Fact] // legal：两波连续——第二波补防第二个分区（波重置后预算恢复）
    public void Next_wave_reopens_the_budget_and_defends_the_second_region()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);
        _ = director.RunWave();
        Assert.Equal(2, harness.Simulation.Towers.Count);

        Assert.True(director.BeginWave(3, isBossWave: false));
        IReadOnlyList<AutopilotRoundResult> rounds = director.RunWave();

        Assert.Single(rounds);
        Assert.Equal(1, rounds[0].Round);                 // 波内轮计数已重置
        Assert.Equal(IntentKind.HoldRegion, rounds[0].Intent);
        Assert.Equal(4, harness.Simulation.Towers.Count); // south_gate 补防 2 座
        Assert.Equal(3, director.Wave);
    }

    // ------------------------------------------------ ② ≤3 动作/轮 硬限 -----------------------------------

    [Fact] // legal：恰 3 动作 + 轮中汇报——汇报不占配额，3 动作全进
    public void Exactly_three_actions_run_and_mid_round_report_stays_free()
    {
        var draft = new AutopilotIntentDraft(IntentKind.HoldRegion, new[]
        {
            new AutopilotPlannedAction(SystemActionKind.LookAround, "{\"action\":\"LookAround\"}"),
            new AutopilotPlannedAction(SystemActionKind.ReportToPlayer,
                "{\"action\":\"ReportToPlayer\",\"summary\":\"中途简报\",\"verdict\":\"CONFIRMED\"}"),
            new AutopilotPlannedAction(SystemActionKind.SetAlarm,
                "{\"action\":\"SetAlarm\",\"after_seconds\":90.0,\"reason\":\"轮内闹钟\"}"),
            new AutopilotPlannedAction(SystemActionKind.TodoBoard,
                "{\"action\":\"TodoBoard\",\"todos\":[\"北门补一座箭塔\",\"给西侧塔供弹\"]}"),
        });
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);

        AutopilotRoundResult round = director.ExecuteRound(draft, "injected");

        Assert.Equal(4, round.Actions.Count);  // 3 动作 + 1 轮中汇报（汇报免费段）
        Assert.Equal(3, round.QuotaUsed);      // 只有 3 个非汇报动词占格
        Assert.False(round.QuotaRefused);
        Assert.All(round.Actions, action => Assert.Equal("Ok", action.Status));
        Assert.Equal(0, round.Actions.Sum(action => action.PointsSpent)); // 全免费动词
        Assert.Single(harness.Pipeline.State.Tasks);                      // SetAlarm 真注册
    }

    [Fact] // boundary：4 动作草案在入轮前被拒——§11.8 硬限，整轮拒绝而非截断
    public void Four_action_draft_is_rejected_whole_by_the_round_guard()
    {
        AutopilotPlannedAction LookAround() =>
            new(SystemActionKind.LookAround, "{\"action\":\"LookAround\"}");
        var draft = new AutopilotIntentDraft(IntentKind.HoldRegion, new[]
        {
            LookAround(),
            LookAround(),
            LookAround(),
            new AutopilotPlannedAction(SystemActionKind.SetAlarm,
                "{\"action\":\"SetAlarm\",\"after_seconds\":90.0,\"reason\":\"第四动作\"}"),
        });
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => director.ExecuteRound(draft, "injected"));
        Assert.Contains("3", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, director.RoundsUsedThisWave); // 拒绝不消耗轮次
        Assert.Empty(harness.Simulation.Towers);      // 也没有任何落子
    }

    // ------------------------------------------------ ③ 迭代上限执法 --------------------------------------

    [Fact] // legal：min(10,点数) 迭代上限——免费意图源下轮数恰达上限，第 cap+1 轮 null
    public void RunWave_stops_at_the_iteration_cap_computed_from_wave_points()
    {
        // E2 波初余额 5 → 上限 min(10,5)=5；LookAround 免费——预算永远允许，
        // 轮数只被迭代上限截停。
        static AutopilotIntentDraft? FreeRound(AutopilotDirector _) => new AutopilotIntentDraft(
            IntentKind.QueryThreat,
            new[] { new AutopilotPlannedAction(SystemActionKind.LookAround, "{\"action\":\"LookAround\"}") });
        using PipelineHarness harness = NewHarness(out AutopilotDirector director, intentSource: FreeRound);
        director.EngageFullAutopilot();
        Assert.True(director.BeginWave(2, isBossWave: false));
        Assert.Equal(5, director.IterationCapThisWave);

        IReadOnlyList<AutopilotRoundResult> rounds = director.RunWave();

        Assert.Equal(5, rounds.Count);                    // 恰 5 轮（= min(10,5)）
        Assert.Equal(5, director.RoundsUsedThisWave);
        Assert.All(rounds, round => Assert.Equal(1, round.QuotaUsed));
        Assert.Null(director.RunRound("extra"));          // 上限后不再开轮（返回 null 不抛）
        Assert.Equal(10, director.ActionsIssued);         // 5 动作 + 5 轮末汇报
        Assert.Equal(5, harness.Pipeline.Ledger.Points);  // 全免费——零扣点
    }

    [Fact] // boundary：无预算波（E1 点数关闭）——上限 0，RunWave 一轮不出
    public void Zero_point_wave_runs_no_rounds_at_all()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director, eraIndex: 1);
        director.EngageFullAutopilot();
        Assert.True(director.BeginWave(2, isBossWave: false));
        Assert.Equal(0, director.IterationCapThisWave);

        Assert.Empty(director.RunWave());
        Assert.Empty(harness.Simulation.Towers);
    }

    // ------------------------------------------------ ④ 接管降级（当波不回退） ----------------------------

    [Fact] // legal：手动介入 → 当波降级辅助——托管停摆，参与位不回退（罚则已生效）
    public void Manual_intervention_downgrades_to_assisted_and_keeps_penalties_armed()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);
        _ = director.RunWave();
        Assert.True(director.Participation.LevelParticipated);
        Assert.True(director.Participation.WaveParticipated);

        director.NotifyManualIntervention("manual-build");

        Assert.Equal(AutopilotMode.Assisted, director.Mode);
        Assert.Equal("manual-build", director.LastTakeoverReason);
        Assert.Empty(director.RunWave());               // 辅助档不再自主开轮
        Assert.Throws<InvalidOperationException>(() => director.ExecuteRound(
            new AutopilotIntentDraft(IntentKind.QueryThreat,
                new[] { new AutopilotPlannedAction(SystemActionKind.LookAround, "{\"action\":\"LookAround\"}") }),
            "rejected"));
        // 罚则当波已生效不回退：参与位仍为真 → 首通无成就 + 标准榜拒收仍执法
        AutopilotParticipation snapshot = director.Participation;
        Assert.False(AutopilotPenalties.FirstClearAchievementEligible(snapshot));
        Assert.False(AutopilotPenalties.EligibleForStandardBoards(snapshot));
        Assert.Equal(2, harness.Simulation.Towers.Count); // 已建的不拆（接管不清场）
    }

    [Fact] // legal：接管后重新授权——下一波恢复全托管口径
    public void Re_engage_after_a_takeover_restores_full_autopilot_next_wave()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);
        _ = director.RunWave();
        director.NotifyManualIntervention("manual-order");

        director.EngageFullAutopilot();                 // 玩家重新托管
        Assert.True(director.BeginWave(3, isBossWave: false));

        IReadOnlyList<AutopilotRoundResult> rounds = director.RunWave();
        Assert.Single(rounds);                          // 预算恢复（波重置）→ 又能建
        Assert.Equal(4, harness.Simulation.Towers.Count);
    }

    [Fact] // boundary：非全托管态的手动通报不计数（Off/Assisted 下无降级可降）
    public void Intervention_outside_full_autopilot_is_a_no_op()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        Assert.Equal(AutopilotMode.Off, director.Mode);

        director.NotifyManualIntervention("idle-ping");

        Assert.Equal(AutopilotMode.Off, director.Mode);
        Assert.Null(director.LastTakeoverReason);
        Assert.Empty(director.RunWave());
        Assert.Empty(harness.Simulation.Towers);
    }

    // ------------------------------------------------ ⑤ 断供事件响应 --------------------------------------

    /// <summary>
    /// Minimal supply chain (labor camp + harvester + converter + one tower,
    /// ids 2..5) WITHOUT any assign-carrier — the autopilot's reroute then
    /// allocates a genuinely new carrier (converter 4 → tower 5, the E1
    /// ammo lane). Anchor towers must enqueue AFTER the chain (tick 250) or
    /// they steal the ids 2/3 and shift every later id.
    /// </summary>
    private static void EnqueueBareSupplyChain(Simulation simulation)
    {
        simulation.Enqueue(SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell));
        simulation.Enqueue(SimCommands.BuildHarvester(120, SimE1Fixture.HarvesterCell, SimE1Fixture.Wood, seats: 1));
        simulation.Enqueue(SimCommands.BuildConverter(180, SimE1Fixture.ConverterCell));
        simulation.Enqueue(SimCommands.BuildTower(240, "era1:tower-wood-arrow", SimE1Fixture.TowerCell));
    }

    [Fact] // legal：断供瓶颈事件驱动——RerouteLogistics 真落子（供给链上真实改道）
    public void Supply_bottleneck_wakes_a_reroute_round_with_a_real_carrier()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        EnqueueBareSupplyChain(harness.Simulation);
        DefendAnchors(harness.Simulation, tick: 250); // 补防放在链后（保住链 id 2..5）
        harness.Simulation.Run(700);
        int carriersBefore = harness.Simulation.Carriers.Count;
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);

        AutopilotRoundResult? round = director.NotifySupplyBottleneck(Bottleneck(towerId: 5, seq: 1));

        Assert.NotNull(round);
        Assert.Equal(IntentKind.RerouteLogistics, round!.Intent);
        Assert.Equal("supply-bottleneck:1", round.Trigger);
        Assert.Equal(2, round.Actions.Count); // 侦察 + 改道
        Assert.All(round.Actions, action => Assert.Equal("Ok", action.Status));
        Assert.Equal(carriersBefore + 1, harness.Simulation.Carriers.Count); // assign-carrier 真落子
        Assert.Equal(1, director.PointsSpentByAutopilot);                    // 物流 1 点
    }

    [Fact] // boundary：降级后断供事件不丢——入队但不开轮（等待重新授权后消费）
    public void Bottleneck_while_downgraded_stays_queued_without_a_round()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        EnqueueBareSupplyChain(harness.Simulation); // 改道轮需要供给链在场（converter 4→tower 5）
        DefendAnchors(harness.Simulation, tick: 250);
        harness.Simulation.Run(700);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);
        director.NotifyManualIntervention("manual-build"); // 未开轮即接管——直接降级

        AutopilotRoundResult? round = director.NotifySupplyBottleneck(Bottleneck(towerId: 5, seq: 1));

        Assert.Null(round); // 降级态不开轮（事件已入队不丢）
        director.EngageFullAutopilot(); // 当波内重新授权（接管不取消当前波）
        AutopilotRoundResult? resumed = director.RunRound("post-rearm");
        Assert.NotNull(resumed); // 队列中的断供事件被消费 → 改道轮
        Assert.Equal("post-rearm", resumed!.Trigger);
        Assert.Equal(IntentKind.RerouteLogistics, resumed.Intent);
        Assert.Equal(2, director.Wave); // 仍是当波（BeginWave 才清空断供队列——陈旧事件按波丢弃）
    }

    // ------------------------------------------------ ⑥ 战斗轮 --------------------------------------------

    [Fact] // legal：活敌在场且无集火目标 → 侦察+集火轮（真注册集火目标）
    public void Living_enemies_without_a_focus_target_wake_a_focus_fire_round()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        DefendAnchors(harness.Simulation);
        harness.Simulation.Run(3300); // E1 夹具 grace 3240——首波敌在场
        Assert.Contains(harness.Simulation.Units, unit => unit.Alive);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);

        IReadOnlyList<AutopilotRoundResult> rounds = director.RunWave();

        AutopilotRoundResult round = Assert.Single(rounds);
        Assert.Equal(IntentKind.FocusFire, round.Intent);
        Assert.Equal(2, round.Actions.Count);
        Assert.Contains(round.Actions, action => action.Verb == SystemActionKind.FocusFire
            && action.Status == "Ok");
        Assert.NotNull(harness.Pipeline.State.FocusTarget); // 集火真注册
        // 集火目标仍在场——第二轮不重复下达（轮间等待）
        Assert.Empty(director.RunWave());
    }

    // ------------------------------------------------ ⑦ 波守卫与模式守卫 ----------------------------------

    [Fact] // boundary：伪造/重复波号被账本单调性拒绝——导演不清零任何计数
    public void Forged_or_duplicate_wave_numbers_are_rejected()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        Assert.True(director.BeginWave(2, isBossWave: false));
        _ = director.RunWave();
        int roundsUsed = director.RoundsUsedThisWave;
        long wave = director.Wave;

        Assert.False(director.BeginWave(2, isBossWave: false)); // 重复
        Assert.False(director.BeginWave(1, isBossWave: false)); // 回退
        Assert.Equal(roundsUsed, director.RoundsUsedThisWave);  // 拒绝波不清零计数
        Assert.Equal(wave, director.Wave);
    }

    [Fact] // boundary：未授权（Off）不开轮——Engage 是唯一入口
    public void Rounds_require_full_autopilot_engagement()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        _ = director.BeginWave(2, isBossWave: false);

        Assert.Empty(director.RunWave());
        Assert.Null(director.RunRound("wave-2"));
        Assert.Empty(harness.Simulation.Towers);
    }

    [Fact] // legal：Disengage 全停（宿主侧关断——与接管降级不同的"彻底停"）
    public void Disengage_stops_the_lane_entirely()
    {
        using PipelineHarness harness = NewHarness(out AutopilotDirector director);
        director.EngageFullAutopilot();
        _ = director.BeginWave(2, isBossWave: false);
        director.Disengage();

        Assert.Equal(AutopilotMode.Off, director.Mode);
        Assert.Empty(director.RunWave());
    }
}
