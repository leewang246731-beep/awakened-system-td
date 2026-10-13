using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Sim;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Sim;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Interaction;

// [C-23][F-036][F-119] T-056 玩家建造泳道（PlayerBuildLane）：
// 1) 同一咽喉证据——玩家拖放与军师第 5 段（PipelineExecutor.InvokeStep→
//    ActionRegistry.Invoke→SimCommand）在孪生模拟上产出逐位相同的世界态
//    （ComputeStateHash 相等）与注册表 verdict 行；
// 2) 口径裁定证据——JournalEvent 载荷 Actor="player"、PointsSpent=0（系统点
//    限军师，v3.2 §11.4 计价/§6 L82 同款语义——裁定三段依据见泳道文件头）；
// 3) spike2-pit#6 三场景走完整咽喉——站敌格拒绝/装卸格拒绝/在途民夫推移；
// 4) SellTower 危险位——confirm_sell=false 本地拒（不出载荷）、true 走注册表
//    （E1/E2 薄版=诚实 not-implemented 拒绝，v3.2 §11.4）。
public class PlayerBuildLaneTests
{
    private const string WoodArrow = "era1:tower-wood-arrow";

    private static (PipelineHarness Harness, PlayerBuildLane Lane) MakeLane(int era = PipelineHarness.AgentEra)
    {
        var harness = new PipelineHarness();
        PlayerBuildLane lane = PlayerBuildLane.OverSimulation(
            harness.Simulation, PipelineHarness.Config(), () => era,
            // F5：按 PlayerBuildLane 的 journal 契约（ctor 参数注释：K-8 sink
            // 须 sequence-ordered per run）以 SequencingJournalWriter 包装——
            // 与生产侧 BuildInteractionUi 的接线同形，裸写入器会落 Seq=0。
            new SequencingJournalWriter(harness.Journal),
            harness.LogSink);
        return (harness, lane);
    }

    private static List<PlayerBuildJournalPayload> PlayerEvents(string journalPath) =>
        JsonlJournalWriter.ReadAll(journalPath)
            .Select(evt => TryParsePlayerPayload(evt.PayloadJson))
            .OfType<PlayerBuildJournalPayload>()
            .Where(payload => payload.Actor == PlayerBuildLane.Actor)
            .ToList();

    private static PlayerBuildJournalPayload? TryParsePlayerPayload(string payloadJson)
    {
        try
        {
            // 军师/账本载荷缺 Actor/Verb 等必选构造参数——按负载形状筛掉
            return JsonSerializer.Deserialize(payloadJson, PlayerBuildJsonContext.Default.PlayerBuildJournalPayload);
        }
        catch (JsonException)
        {
            return null; // 非 player-lane 载荷（五段管线/账本），非本断言对象
        }
    }

    [Fact] // legal：同一咽喉——玩家门与军师第 5 段在孪生模拟上落子后世界哈希逐位一致
    public void Player_build_and_advisor_stage5_produce_identical_worlds() // [F-036]
    {
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane();
        var cells = new[] { new CellCoord(11, 11), new CellCoord(12, 12) };

        PlayerBuildOutcome outcome = lane.Build(WoodArrow, cells);

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.Equal(cells.Length, outcome.CellsPlaced);
        Assert.Equal(cells.Length, harness.Simulation.Towers.Count);
        double stockAfterPlayer = harness.Simulation.Stock.AmountOf(PipelineHarness.Wood);
        double unitCost = SimEra1Content.WoodArrowTower().CostTotal;
        Assert.Equal(200.0 - (unitCost * cells.Length), stockAfterPlayer);
        Assert.Contains("verdict=ok action=BuildPlan", harness.LogSink.JoinedMessages);

        // 军师第 5 段的同一调用：同一 JSON → PipelineExecutor.InvokeStep（孪生
        // 模拟+等价注册表——与 AgentPipeline.OverSimulation 内部装配同构）。
        using var twin = new PipelineHarness();
        var twinView = new SimulationActionView(twin.Simulation, PipelineHarness.Config());
        var twinExecutor = new PipelineExecutor(
            new ActionRegistry(twin.LogSink, twinView, eraOrdinalProvider: () => PipelineHarness.AgentEra),
            twin.Simulation,
            twinView);
        string sameJson = JsonSerializer.Serialize(
            new BuildPlanActionJson(
                nameof(SystemActionKind.BuildPlan),
                WoodArrow,
                cells.Select(cell => new CellJson(cell.X, cell.Y)).ToArray()),
            PipelinePlannerJsonContext.Default.BuildPlanActionJson);
        ActionOutcome twinOutcome = twinExecutor.InvokeStep(SystemActionKind.BuildPlan, sameJson);

        Assert.Equal(ActionResultStatus.Ok, twinOutcome.Result.Status);
        Assert.Equal(
            harness.Simulation.ComputeStateHash(),
            twin.Simulation.ComputeStateHash()); // 同一执行路径 ⇒ 同一世界态（逐位）
    }

    [Fact] // legal：战报——JournalEvent(Validation) 载荷 Type=execution、Actor=player、PointsSpent=0
    public void Player_builds_journal_actor_player_events_with_zero_points() // [F-036]
    {
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane();
        lane.Build(WoodArrow, new[] { new CellCoord(11, 11) });
        lane.Sell(targetId: 1, confirmSell: false);
        lane.Sell(targetId: 1, confirmSell: true);

        List<PlayerBuildJournalPayload> events = PlayerEvents(harness.JournalPath);
        Assert.Equal(3, events.Count);
        Assert.All(events, payload => Assert.Equal(PlayerBuildLane.Actor, payload.Actor));
        Assert.All(events, payload => Assert.Equal(PipelineJournalTypes.Execution, payload.Type));

        PlayerBuildJournalPayload build = events[0];
        Assert.Equal(nameof(SystemActionKind.BuildPlan), build.Verb);
        Assert.Equal(nameof(ActionResultStatus.Ok), build.Status);
        Assert.Equal(1, build.CellsPlaced);
        Assert.Equal(0, build.PointsSpent); // 口径裁定：玩家手动建造不消耗系统点
        Assert.Contains("\"tower_id\":\"" + WoodArrow + "\"", build.ActionJson);

        Assert.Equal(nameof(SystemActionKind.SellTower), events[1].Verb);
        Assert.Equal(nameof(ActionResultStatus.ErrSchemaDenied), events[1].Status);
        Assert.Equal(nameof(SystemActionKind.SellTower), events[2].Verb);
        Assert.Equal(nameof(ActionResultStatus.Err), events[2].Status);
    }

    [Fact] // legal：K-8 单流有序——玩家事件 Seq 经 SequencingJournalWriter 重编号
           // 后严格递增（W2 批 3 F5：泳道契约=sequence-ordered per run，裸
           // 写入器会恒落 Seq=0） // [F-036]
    public void Player_events_carry_strictly_increasing_seq()
    {
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane();
        lane.Build(WoodArrow, new[] { new CellCoord(11, 11) });
        lane.Sell(targetId: 1, confirmSell: false); // 本地拒也入报（事件 2）
        lane.Sell(targetId: 1, confirmSell: true);  // 注册表诚实拒（事件 3）

        var playerSeqs = JsonlJournalWriter.ReadAll(harness.JournalPath)
            .Select(evt => (Evt: evt, Payload: TryParsePlayerPayload(evt.PayloadJson)))
            .Where(pair => pair.Payload is { Actor: PlayerBuildLane.Actor }) // 军师/账本载荷缺 Actor——按载荷形状筛掉
            .Select(pair => pair.Evt.Seq)
            .ToArray();
        Assert.Equal(3, playerSeqs.Length);
        Assert.Equal(new[] { 1L, 2, 3 }, playerSeqs); // 1 起步逐 +1（严格递增）
        for (int i = 1; i < playerSeqs.Length; i++)
        {
            Assert.True(playerSeqs[i] > playerSeqs[i - 1], $"Seq 必须严格递增：{playerSeqs[i - 1]} → {playerSeqs[i]}");
        }
    }

    [Fact] // illegal：pit#6 场景一——放置格站活敌：预览层与执行道同码拒绝，零落子零扣费
    public void Enemy_standing_on_the_cell_rejects_on_both_shared_lanes() // [F-036]
    {
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane();
        StepUntil(harness.Simulation, sim => sim.Units.Count > 0, maxTicks: 6_000);
        Assert.True(harness.Simulation.Units.Count > 0, "宽限后首波必须已生成（夹具确定性）");
        Unit enemy = harness.Simulation.Units.First(unit => unit.Alive);
        var cell = new CellCoord(
            (int)Math.Floor(enemy.X / Simulation.CellSizePx),
            (int)Math.Floor(enemy.Y / Simulation.CellSizePx));

        // 预览层（SimulationActionView→BuildOccupancyRule）：红格+共享拒绝码
        Assert.False(lane.View.IsBuildable(cell, out string? previewRejection));
        Assert.Equal(BuildOccupancyRule.EnemyStandingRejection, previewRejection);

        // 执行道（同一咽喉）：草案全红 → no-buildable-cells，世界零变化
        double stockBefore = harness.Simulation.Stock.AmountOf(PipelineHarness.Wood);
        PlayerBuildOutcome outcome = lane.Build(WoodArrow, new[] { cell });
        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.StartsWith(PipelineExecutor.NoBuildableCells, outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, outcome.CellsPlaced);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(stockBefore, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));

        // SimCommand 直投同一格：同一拒绝码（ValidateBuildableCell 同源）
        harness.Simulation.Enqueue(SimCommands.BuildTower(harness.Simulation.CurrentTick + 1, WoodArrow, cell));
        harness.Simulation.Step();
        SimCommandResult result = harness.Simulation.CommandResults[^1];
        Assert.False(result.Accepted);
        Assert.Equal(BuildOccupancyRule.EnemyStandingRejection, result.RejectionReason);
    }

    [Fact] // illegal：pit#6 场景二——民夫正在该格装卸（Loading 于 home）：两道同码拒绝
    public void Servicing_porter_cell_rejects_on_both_shared_lanes() // [F-036]
    {
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane();
        harness.Simulation.EnqueueRange(new[]
        {
            SimCommands.BuildHarvester(60, SimE1Fixture.HarvesterCell, PipelineHarness.Wood, seats: 1),
            SimCommands.BuildConverter(120, SimE1Fixture.ConverterCell),
            SimCommands.AssignCarrier(180, from: 2, to: 3),
        });
        harness.Simulation.Run(181); // 载具于 tick 180 生成，Loading 窗口（20t）内
        Carrier porter = Assert.Single(harness.Simulation.Carriers);
        Assert.Equal(CarrierPhase.Loading, porter.Phase);

        Assert.False(lane.View.IsBuildable(porter.Home, out string? previewRejection));
        Assert.Equal(BuildOccupancyRule.CarrierServicingRejection, previewRejection);

        double stockBefore = harness.Simulation.Stock.AmountOf(PipelineHarness.Wood);
        PlayerBuildOutcome outcome = lane.Build(WoodArrow, new[] { porter.Home });
        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.StartsWith(PipelineExecutor.NoBuildableCells, outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Equal(stockBefore, harness.Simulation.Stock.AmountOf(PipelineHarness.Wood));

        harness.Simulation.Enqueue(SimCommands.BuildTower(harness.Simulation.CurrentTick + 1, WoodArrow, porter.Home));
        harness.Simulation.Step();
        SimCommandResult result = harness.Simulation.CommandResults[^1];
        Assert.False(result.Accepted);
        Assert.Equal(BuildOccupancyRule.CarrierServicingRejection, result.RejectionReason);
    }

    [Fact] // legal：pit#6 场景三——在途民夫推移：放置成功（咽喉 Ok）+民夫改道绕开+照常交付
    public void Placing_on_an_inbound_route_pushes_the_porter_around_the_new_tower() // [F-036]
    {
        // 探针跑：同种子孪生模拟上先观测完整出站折线（确定性：两跑逐 tick 等价）
        (int outboundTick, List<CellCoord> routeCells) = ProbeOutboundRoute();
        Assert.True(routeCells.Count >= 4, $"出站折线过短（{routeCells.Count} 格），夹具漂移须复查");
        CellCoord blocked = routeCells[2]; // 锚格之后 ≥2 格裕量的中间格（非终点）

        // 正式跑：同一命令流跑到出站 tick，随后经玩家泳道在折线格上落塔
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane();
        EnsupplyChain(harness.Simulation);
        harness.Simulation.Run(outboundTick);
        Carrier porter = Assert.Single(harness.Simulation.Carriers);
        Assert.Equal(CarrierPhase.Outbound, porter.Phase);
        Assert.Equal(routeCells[0], porter.CurrentCell);

        PlayerBuildOutcome outcome = lane.Build(WoodArrow, new[] { blocked });
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.Equal(1, outcome.CellsPlaced);
        Assert.Single(harness.Simulation.Towers);

        // 推移后：民夫绕开被占格（当前格序列不再含该格），且照常完成首趟交付
        var visited = new HashSet<CellCoord> { porter.CurrentCell };
        bool delivered = false;
        for (int i = 0; i < 900 && !delivered; i++)
        {
            harness.Simulation.Step();
            visited.Add(porter.CurrentCell);
            delivered = porter.DeliveredItems > 0;
        }

        Assert.DoesNotContain(blocked, visited);
        Assert.True(delivered, "改道后民夫必须完成首趟交付（900t 界内）");
    }

    [Fact] // illegal：SellTower 危险位——confirm_sell=false 本地拒；true 走注册表诚实拒绝（E2 动词薄版 not-implemented）
    public void SellTower_requires_the_explicit_danger_bit_and_refuses_honestly() // [F-036]
    {
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane();

        PlayerBuildOutcome naked = lane.Sell(targetId: 1, confirmSell: false);
        Assert.Equal(ActionResultStatus.ErrSchemaDenied, naked.Result.Status);
        Assert.Equal("confirm_sell-must-be-true", naked.Result.ErrorMessage);

        PlayerBuildOutcome confirmed = lane.Sell(targetId: 1, confirmSell: true);
        Assert.Equal(ActionResultStatus.Err, confirmed.Result.Status);
        Assert.Contains("not-implemented:SellTower", confirmed.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(harness.Simulation.Towers); // 世界零变化
    }

    [Fact] // illegal：时代门同一咽喉生效——era1 泳道 Build → ErrCapabilityLocked（与军师同门同码）
    public void Era1_lane_hits_the_same_capability_gate_as_the_advisor() // [F-036]
    {
        (PipelineHarness harness, PlayerBuildLane lane) = MakeLane(era: 1);
        PlayerBuildOutcome outcome = lane.Build(WoodArrow, new[] { new CellCoord(11, 11) });
        Assert.Equal(ActionResultStatus.ErrCapabilityLocked, outcome.Result.Status);
        Assert.Contains("capability-locked:BuildPlan", outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(harness.Simulation.Towers);
        Assert.Contains("verdict=era-locked action=BuildPlan", harness.LogSink.JoinedMessages);
    }

    /// <summary>
    /// 补链命令流（里程碑前缀，实体号：仓库 1、劳役营 2、采集场 3、转化器 4）
    /// ：无劳役营则采集场无能量→货堆永不积货→民夫装载窗口空转（生产链前提）。
    /// </summary>
    private static void EnsupplyChain(Simulation simulation) => simulation.EnqueueRange(new[]
    {
        SimCommands.BuildLaborCamp(60, SimE1Fixture.LaborCampCell),
        SimCommands.BuildHarvester(120, SimE1Fixture.HarvesterCell, PipelineHarness.Wood, seats: 1),
        SimCommands.BuildConverter(180, SimE1Fixture.ConverterCell),
        SimCommands.AssignCarrier(240, from: 3, to: 4),
    });

    /// <summary>
    /// 探针跑（与正式跑同构的孪生）：步进至民夫恰好 Outbound 记步数，再步进
    /// 至 Unloading 途中依序记录出站折线（同种子 ⇒ 正式跑同一折线同一 tick）。
    /// </summary>
    private static (int OutboundTick, List<CellCoord> RouteCells) ProbeOutboundRoute()
    {
        using var probe = new PipelineHarness();
        EnsupplyChain(probe.Simulation);
        int outboundTick = 0;
        while (probe.Simulation.Carriers.Count == 0
            || probe.Simulation.Carriers[0].Phase != CarrierPhase.Outbound)
        {
            probe.Simulation.Step();
            outboundTick++;
            Assert.True(outboundTick <= 1_800, "民夫 1800t 内未出站（装载窗+货堆积货），夹具漂移须复查");
        }

        Carrier porter = probe.Simulation.Carriers[0];
        var route = new List<CellCoord> { porter.CurrentCell };
        for (int i = 0; i < 400; i++)
        {
            probe.Simulation.Step();
            Carrier walking = probe.Simulation.Carriers[0];
            if (!route[^1].Equals(walking.CurrentCell))
            {
                route.Add(walking.CurrentCell);
            }

            if (walking.Phase == CarrierPhase.Unloading)
            {
                break;
            }
        }

        Assert.Equal(CarrierPhase.Unloading, probe.Simulation.Carriers[0].Phase);
        Assert.Equal(porter.Destination, route[^1]);
        return (outboundTick, route);
    }

    private static void StepUntil(Simulation simulation, Func<Simulation, bool> done, int maxTicks)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            if (done(simulation))
            {
                return;
            }

            simulation.Step();
        }

        Assert.True(done(simulation), $"StepUntil 界内未达条件（{maxTicks}t）");
    }
}
