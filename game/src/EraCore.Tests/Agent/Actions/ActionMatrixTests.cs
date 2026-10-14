using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-065][F-068][F-062][F-063][F-064] T-068 19 动作矩阵（验收核心）：
// 每个动作 ×（合法路径 / 权限档位正确 / 计价正确 / 解锁门正确 / 自纠路径 /
// 危险位负向）——表驱动参数化。档位/计价断言锚 ActionCatalog（唯一行源，
// T-070 后）与 SystemActionTariffTable 派生视图；解锁门锚附录 G.2 列。
// [F-065][F-068][F-062][F-063] 19 动作矩阵验收核心（类级关联）
public class ActionMatrixTests
{
    private const string ArmedScroll = "era2:scroll-fire-oil";

    private static ActionRegistry Registry(
        CapturingLogSink sink,
        int era = EraOrdinals.E2,
        double initialWood = 200.0,
        bool runMilestone = false,
        long stepToTick = 0,
        IReadOnlySet<string>? armed = null,
        TowerEstateOps? estateOverride = null) =>
        new(
            sink,
            RegistryFixture.Create(
                initialStock: new List<KeyValuePair<ResourceKey, double>> { new(SimE1Fixture.Wood, initialWood) },
                runMilestone: runMilestone,
                stepToTick: stepToTick).View,
            eraOrdinalProvider: () => era,
            towerEstate: estateOverride ?? RegistryFixture.Create().Simulation.Estate,
            armedScrolls: armed);

    // ------------------------------------------------ ① 权限档位（19 行表驱动）

    [Theory] // legal/structure：19 动作 × 档位（§11.4 allow/ask 划分，锚目录行源）
    [InlineData("LookAround", "Allow")]
    [InlineData("InspectTile", "Allow")]
    [InlineData("QueryWorld", "Allow")]
    [InlineData("BuildPlan", "Ask")]
    [InlineData("UpgradeTower", "Ask")]
    [InlineData("SellTower", "Ask")]
    [InlineData("SetRally", "Ask")]
    [InlineData("RerouteLogistics", "Ask")]
    [InlineData("SetStockpile", "Ask")]
    [InlineData("FocusFire", "Ask")]
    [InlineData("SetTowerMode", "Ask")]
    [InlineData("TriggerScroll", "Ask")]
    [InlineData("HeroOrder", "Ask")]
    [InlineData("AskPlayer", "Allow")]
    [InlineData("ReportToPlayer", "Allow")]
    [InlineData("TodoBoard", "Allow")]
    [InlineData("SpawnScout", "Allow")]
    [InlineData("SetAlarm", "Allow")]
    [InlineData("StopTask", "Allow")]
    public void Every_verb_carries_its_frozen_permission_tier(string verb, string expectedTier)
    {
        // legal：目录=唯一行源；关税表=派生视图——两处同值
        ActionDescriptor descriptor = ActionCatalog.Descriptor(ActionCatalog.TryResolveName(verb, out var kind) ? kind : default);
        Assert.Equal(expectedTier, descriptor.Tier.ToString());
        Assert.Equal(expectedTier, SystemActionTariffTable.Get(kind).DefaultTier.ToString());
    }

    // ------------------------------------------------ ② 计价（§11.4 侦察 0/建造 2/物流 1/战斗 1/巡逻 1）

    [Theory] // legal：19 动作 × 点数（含 SpawnScout 巡逻 1 点、管理组其余 0 点）
    [InlineData("LookAround", 0)]
    [InlineData("InspectTile", 0)]
    [InlineData("QueryWorld", 0)]
    [InlineData("BuildPlan", 2)]
    [InlineData("UpgradeTower", 2)]
    [InlineData("SellTower", 2)]
    [InlineData("SetRally", 2)]
    [InlineData("RerouteLogistics", 1)]
    [InlineData("SetStockpile", 1)]
    [InlineData("FocusFire", 1)]
    [InlineData("SetTowerMode", 1)]
    [InlineData("TriggerScroll", 1)]
    [InlineData("HeroOrder", 1)]
    [InlineData("AskPlayer", 0)]
    [InlineData("ReportToPlayer", 0)]
    [InlineData("TodoBoard", 0)]
    [InlineData("SpawnScout", 1)]
    [InlineData("SetAlarm", 0)]
    [InlineData("StopTask", 0)]
    public void Every_verb_carries_its_frozen_point_price(string verb, int expectedPoints)
    {
        var kind = ActionCatalog.TryResolveName(verb, out var parsed) ? parsed : default;
        Assert.Equal(expectedPoints, ActionCatalog.Descriptor(kind).PointCost);
        Assert.Equal(expectedPoints, SystemActionTariffTable.Get(kind).PointCost);
    }

    // ------------------------------------------------ ③ 解锁时代门（G.2 列：E1 组 vs E2 组）

    public static TheoryData<string, int> UnlockRows => new()
    {
        // legal：E1 解锁（真 handler 执行）
        { "LookAround", 1 },
        { "InspectTile", 1 },
        { "QueryWorld", 1 },
        { "AskPlayer", 1 },
        { "ReportToPlayer", 1 },
        { "SetAlarm", 1 },
        // boundary：E2 解锁组在 E1 全锁（ErrCapabilityLocked + 澄清话术）
        { "BuildPlan", 2 },
        { "UpgradeTower", 2 },
        { "SellTower", 2 },
        { "SetRally", 2 },
        { "RerouteLogistics", 2 },
        { "SetStockpile", 2 },
        { "FocusFire", 2 },
        { "SetTowerMode", 2 },
        { "TriggerScroll", 2 },
        { "HeroOrder", 2 },
        { "TodoBoard", 2 },
        { "SpawnScout", 2 },
        { "StopTask", 2 },
    };

    [Theory] // boundary+legal：解锁前一时代 → ErrCapabilityLocked + 澄清话术（N9/A2）；解锁当时代 → 过时代门
    [MemberData(nameof(UnlockRows))]
    public void Unlock_gate_answers_per_the_g2_column(string verb, int unlockEra)
    {
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create();
        ActionRegistry lockedRegistry = new(
            sink,
            view,
            eraOrdinalProvider: () => unlockEra - 1,
            towerEstate: simulation.Estate,
            armedScrolls: new HashSet<string>(StringComparer.Ordinal) { ArmedScroll });

        ActionOutcome lockedOutcome = lockedRegistry.Invoke(ActionJsonSamples.All()[verb]());
        Assert.Equal(ActionResultStatus.ErrCapabilityLocked, lockedOutcome.Result.Status);
        Assert.Contains($"capability-locked:{verb}", lockedOutcome.Result.ErrorMessage);
        Assert.Contains("clarifying", lockedOutcome.Result.ErrorMessage);

        ActionRegistry unlockedRegistry = new(
            new CapturingLogSink(),
            view,
            eraOrdinalProvider: () => unlockEra,
            towerEstate: simulation.Estate,
            armedScrolls: new HashSet<string>(StringComparer.Ordinal) { ArmedScroll });
        ActionOutcome unlockedOutcome = unlockedRegistry.Invoke(ActionJsonSamples.All()[verb]());
        Assert.NotEqual(ActionResultStatus.ErrCapabilityLocked, unlockedOutcome.Result.Status);
    }

    // ------------------------------------------------ ④ 合法路径：真执行+真载荷

    [Fact] // legal：InspectTile 三 aspect 全跑通（真视图读数）
    public void InspectTile_runs_all_three_aspects_against_the_live_view()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink, era: EraOrdinals.E1);

        ActionOutcome stats = registry.Invoke(ActionJsonSamples.InspectTileAspect("stats"));
        ActionOutcome threat = registry.Invoke(ActionJsonSamples.InspectTileAspect("threat"));
        ActionOutcome supply = registry.Invoke(ActionJsonSamples.InspectTileAspect("supply"));

        Assert.Equal(ActionResultStatus.Ok, stats.Result.Status);
        Assert.Equal(ActionResultStatus.Ok, threat.Result.Status);
        Assert.Equal(ActionResultStatus.Ok, supply.Result.Status);
        var threatReport = Assert.IsType<InspectTileReport>(threat.Payload);
        Assert.Equal("threat", threatReport.Aspect);
        Assert.Contains(threatReport.Lines, line => line.StartsWith("enemies-total=", StringComparison.Ordinal));
    }

    [Fact] // illegal：界外格 → ErrTargetInvalid（不猜格子）
    public void InspectTile_refuses_out_of_grid_cells()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);
        string payload = "{\"action\":\"InspectTile\",\"cell\":{\"x\":999,\"y\":999},\"aspect\":\"threat\"}";

        ActionOutcome outcome = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, outcome.Result.Status);
        Assert.Contains("cell-out-of-bounds", outcome.Result.ErrorMessage);
    }

    [Fact] // legal：QueryWorld 四 key 全跑通 + head_limit 截断
    public void QueryWorld_answers_every_key_and_honors_head_limit()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink, era: EraOrdinals.E1);

        foreach (string key in new[] { "wave", "resource", "threat", "paths" })
        {
            ActionOutcome outcome = registry.Invoke(ActionJsonSamples.QueryWorld(key));
            Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
            var report = Assert.IsType<QueryWorldReport>(outcome.Payload);
            Assert.Equal(key, report.Key);
        }

        var limited = Assert.IsType<QueryWorldReport>(registry.Invoke(ActionJsonSamples.QueryHeadLimit(1)).Payload);
        Assert.Single(limited.Lines);
    }

    [Fact] // legal：SetRally/SetStockpile/TodoBoard/SetAlarm/AskPlayer 治理态真落账
    public void Management_and_directive_verbs_write_the_governed_state()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink, initialWood: 100.0);

        Assert.Equal(ActionResultStatus.Ok, registry.Invoke(ActionJsonSamples.SetRally()).Result.Status);
        Assert.Equal("hold", registry.State.RallyStances["lane-north"]);

        Assert.Equal(ActionResultStatus.Ok, registry.Invoke(ActionJsonSamples.SetStockpile()).Result.Status);
        Assert.Equal(50.0, registry.State.StockpileFloors["era1:res-wood"]);

        Assert.Equal(ActionResultStatus.Ok, registry.Invoke(ActionJsonSamples.TodoBoard()).Result.Status);
        Assert.Equal(2, registry.State.Todos.Count);

        ActionOutcome alarm = registry.Invoke(ActionJsonSamples.SetAlarm());
        Assert.Equal(ActionResultStatus.Ok, alarm.Result.Status);
        var alarmAck = Assert.IsType<SetAlarmAck>(alarm.Payload);
        Assert.StartsWith("alarm-", alarmAck.TaskId);
        Assert.Single(registry.State.Tasks);

        ActionOutcome ask = registry.Invoke(ActionJsonSamples.AskPlayer());
        Assert.Equal(ActionResultStatus.Ok, ask.Result.Status);
        var card = Assert.IsType<AskPlayerCard>(ask.Payload);
        Assert.Equal(3, card.Options.Count); // 2 选项 + 系统附「其他」
        Assert.Equal("其他", card.Options[^1]);
    }

    [Fact] // illegal：池外物资的储备下限 → ErrTargetInvalid（不替玩家编物资）
    public void SetStockpile_refuses_items_the_pool_never_carries()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);
        string payload = "{\"action\":\"SetStockpile\",\"item_id\":\"era9:res-unobtainium\",\"min_amount\":5.0}";

        ActionOutcome outcome = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, outcome.Result.Status);
        Assert.Contains("item-unknown", outcome.Result.ErrorMessage);
        Assert.Empty(registry.State.StockpileFloors);
    }

    [Fact] // legal：StopTask 统一句柄停止闹钟+巡逻；illegal：未知句柄拒绝
    public void StopTask_stops_registered_handles_and_refuses_unknown_ones()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);
        registry.Invoke(ActionJsonSamples.SetAlarm());
        registry.Invoke(ActionJsonSamples.SpawnScout());

        ActionOutcome stopped = registry.Invoke(ActionJsonSamples.StopTask().Replace("task-17", "all"));
        Assert.Equal(ActionResultStatus.Ok, stopped.Result.Status);
        Assert.Empty(registry.State.Tasks);

        ActionOutcome unknown = registry.Invoke(ActionJsonSamples.StopTask());
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, unknown.Result.Status);
        Assert.Contains("task-unknown", unknown.Result.ErrorMessage);
    }

    // ------------------------------------------------ ⑤ FocusFire / SetTowerMode：目标校验负向

    [Fact] // illegal：无现役敌 → 集火拒绝；无塔 → 塔模式拒绝（诚实拒绝而非静默改道）
    public void Combat_directives_refuse_absent_targets()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);

        ActionOutcome fire = registry.Invoke(ActionJsonSamples.FocusFire());
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, fire.Result.Status);
        Assert.Contains("not a living enemy", fire.Result.ErrorMessage);
        Assert.Null(registry.State.FocusTarget);

        ActionOutcome mode = registry.Invoke(ActionJsonSamples.SetTowerMode());
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, mode.Result.Status);
        Assert.Contains("not-a-tower", mode.Result.ErrorMessage);
        Assert.Empty(registry.State.TowerModes);
    }

    [Fact] // legal：有塔时塔模式落账（里程碑夹具=真塔 id 5/7）
    public void SetTowerMode_records_modes_for_placed_towers()
    {
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create(runMilestone: true);
        ActionRegistry registry = new(
            sink, view, eraOrdinalProvider: () => EraOrdinals.E2, towerEstate: simulation.Estate);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.SetTowerMode());

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var ack = Assert.IsType<SetTowerModeAck>(outcome.Payload);
        Assert.Equal(new long[] { 5, 7 }, ack.TargetIds);
        Assert.Equal(2, registry.State.TowerModes.Count);
        Assert.Equal("first", registry.State.TowerModes[5]);
    }

    // ------------------------------------------------ ⑥ TriggerScroll：装备集+冷却（注册表层）

    [Fact] // legal→boundary→legal：装备卷轴首触 Ok，90 tick 窗口内再触被冷却拒绝
    public void TriggerScroll_fires_once_then_cools_down()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(
            sink, armed: new HashSet<string>(StringComparer.Ordinal) { ArmedScroll });
        string payload = "{\"action\":\"TriggerScroll\",\"scroll_id\":\"" + ArmedScroll + "\"}";

        ActionOutcome first = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.Ok, first.Result.Status);
        var ack = Assert.IsType<TriggerScrollAck>(first.Payload);
        Assert.Equal(AgentActionState.ScrollCooldownTicks, ack.ReadyAgainTick); // 3s @30Hz
        Assert.Single(registry.State.ScrollActivations);

        ActionOutcome second = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.Err, second.Result.Status);
        Assert.Contains("scroll-cooldown", second.Result.ErrorMessage);
        Assert.Single(registry.State.ScrollActivations); // 冷却拒绝不产激活动记录
    }

    [Fact] // illegal：未装备卷轴 → 拒绝（G.2 限已装备）
    public void TriggerScroll_refuses_unequipped_scrolls()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.TriggerScroll());

        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("scroll-not-equipped", outcome.Result.ErrorMessage);
    }

    // ------------------------------------------------ ⑦ 危险位负向（F.4：SellTower/confirm_sell；BuildPlan/confirm_overwrite）

    [Fact] // illegal：confirm_sell 缺失/false → schema 拒（危险位必填且必真）
    public void SellTower_danger_bit_is_required_and_must_be_true()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);

        Assert.Equal(ActionResultStatus.ErrSchemaDenied, registry.Invoke(ActionJsonSamples.SellTowerDangerBit(null)).Result.Status);
        Assert.Equal(ActionResultStatus.ErrSchemaDenied, registry.Invoke(ActionJsonSamples.SellTowerDangerBit("false")).Result.Status);
    }

    [Fact] // boundary：审批门上 SellTower 永不自动允许（危险位语义）
    public void SellTower_never_enters_an_auto_approve_whitelist()
    {
        var gate = new ApprovalGate(EraOrdinals.E2);
        Assert.Throws<ArgumentException>(() => gate.SetLevelAutoApprove(SystemActionKind.SellTower, true));
    }

    [Fact] // boundary：confirm_overwrite=true 强制弹卡（ForceAskWhenTrue，即使关卡已自动允许）
    public void Confirm_overwrite_forces_an_ask_even_under_auto_approve()
    {
        var gate = new ApprovalGate(EraOrdinals.E2);
        gate.SetLevelAutoApprove(SystemActionKind.BuildPlan, true);

        var plain = gate.DecideStep(SystemActionKind.BuildPlan, ActionJsonSamples.BuildPlan());
        var overwrite = gate.DecideStep(SystemActionKind.BuildPlan, ActionJsonSamples.BuildPlan(confirmOverwrite: true));

        Assert.Equal(ApprovalDecisionKind.Allow, plain.Kind);
        Assert.Equal(ApprovalDecisionKind.Ask, overwrite.Kind);
    }

    // ------------------------------------------------ ⑧ 危险位负向：越界值一律 schema 拒（除 G.2 声明钳制字段）

    [Theory] // illegal：越界载荷 → ErrSchemaDenied（拒绝即红，不放过界值）
    [InlineData("SpawnScoutDuration", "10.0")]
    [InlineData("SpawnScoutDuration", "121.0")]
    [InlineData("QueryHeadLimit", "21")]
    [InlineData("UpgradeTier", "5")]
    [InlineData("SetTowerModeIds", "7")]
    [InlineData("AskPlayerOptions", "5")]
    [InlineData("ReportFindings", "9")]
    [InlineData("RerouteRate", "1.5")]
    public void Out_of_range_payloads_are_schema_denied(string sampler, string argument)
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);
        string payload = sampler switch
        {
            "SpawnScoutDuration" => ActionJsonSamples.SpawnScoutDuration(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            "SetAlarmAfter" => ActionJsonSamples.SetAlarmAfter(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            "QueryHeadLimit" => ActionJsonSamples.QueryHeadLimit(int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            "UpgradeTier" => ActionJsonSamples.UpgradeTier(int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            "SetTowerModeIds" => ActionJsonSamples.SetTowerModeIds(int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            "AskPlayerOptions" => ActionJsonSamples.AskPlayerOptions(int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            "ReportFindings" => ActionJsonSamples.ReportFindings(int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            "RerouteRate" => ActionJsonSamples.RerouteRate(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException(sampler),
        };

        Assert.Equal(ActionResultStatus.ErrSchemaDenied, registry.Invoke(payload).Result.Status);
    }

    // ------------------------------------------------ ⑨ 自纠路径（矩阵层：调整器判定的表驱动核对）

    public static TheoryData<string, string, bool> AdjusterRows => new()
    {
        // legal：三个可自纠动词对目标性失败有调整器（载荷含可剔除目标）
        { "BuildPlan", "cells-rejected:(1,1):cell-occupied", true },
        { "SetTowerMode", "target-invalid:not-a-tower:9", true },
        { "FocusFire", "target-invalid:42 is not a living enemy", true },
        // illegal：其余动词无可行调整（null=第一次失败即如实汇报）
        { "TriggerScroll", "scroll-not-equipped:x", false },
        { "StopTask", "target-invalid:task-unknown:t", false },
        { "SetStockpile", "target-invalid:item-unknown:x", false },
    };

    [Theory] // boundary：统一自纠入口的每动词判定（F-068 全动作统一）
    [MemberData(nameof(AdjusterRows))]
    public void TryAdjust_covers_exactly_the_correctable_verbs(string verb, string error, bool expectAdjustable)
    {
        // 场景差异：BuildPlan 用界内+界外双格（剔除界外）；SetTowerMode 用里程碑
        // 真塔 5 + 伪塔 999（剔除伪塔）；FocusFire 跑到首波敌入场后改打首个现役。
        string payload = verb switch
        {
            "BuildPlan" => ActionJsonSamples.BuildPlanCells("[{\"x\":6,\"y\":21},{\"x\":999,\"y\":999}]"),
            "SetTowerMode" => "{\"action\":\"SetTowerMode\",\"target_id\":[5,999],\"mode\":\"first\"}",
            "FocusFire" => "{\"action\":\"FocusFire\",\"target_id\":999999}",
            _ => ActionJsonSamples.All()[verb](),
        };
        long stepToTick = verb == "FocusFire" ? 3300 : 0;
        bool runMilestone = verb == "SetTowerMode";
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create(runMilestone: runMilestone, stepToTick: stepToTick);
        if (verb == "FocusFire")
        {
            Assert.True(view.LivingEnemies.Count > 0, "首波敌应在 tick≈2592 入场（E1 夹具口径）");
        }

        var registry = new ActionRegistry(sink, view, eraOrdinalProvider: () => EraOrdinals.E2, towerEstate: simulation.Estate);
        var executor = new EraCore.Agent.Pipeline.PipelineExecutor(registry, simulation, view);

        string? adjusted = executor.TryAdjust(
            ActionCatalog.TryResolveName(verb, out var kind) ? kind : default,
            payload,
            error);

        Assert.Equal(expectAdjustable, adjusted is not null);
    }

    // ------------------------------------------------ ⑩ 注入防护③：AskPlayer 文本过滤

    [Fact] // illegal：提问与选项文本里的可疑链接被过滤为固定标记（F.4 ③ 汇报/提问过滤）
    public void AskPlayer_filters_suspicious_links_from_question_and_options()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);
        string payload = "{\"action\":\"AskPlayer\",\"question\":\"先看 https://evil.example.com/steal 吗？\","
            + "\"options\":[{\"label\":\"看链接\"},{\"label\":\"不看\"}],\"multi_select\":false}";

        ActionOutcome outcome = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var card = Assert.IsType<AskPlayerCard>(outcome.Payload);
        Assert.DoesNotContain("evil.example.com", card.Question);
        Assert.Contains(ReportToPlayerAction.FilteredLinkToken, card.Question);
    }

    [Fact] // illegal：SetAlarm 的 reason 同过链接过滤（玩家可见文本统一出口）
    public void SetAlarm_filters_the_reason_text()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = Registry(sink);
        string payload = "{\"action\":\"SetAlarm\",\"after_seconds\":90.0,\"reason\":\"看 www.evil.com/x 再来\"}";

        ActionOutcome outcome = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var ack = Assert.IsType<SetAlarmAck>(outcome.Payload);
        Assert.DoesNotContain("evil.com", ack.Reason);
        Assert.Contains(ReportToPlayerAction.FilteredLinkToken, ack.Reason);
    }

    // ------------------------------------------------ ⑪ RerouteLogistics：校验层

    [Fact] // illegal：端点不存在/同端点 → 拒绝（执行层的 assign-carrier 之前先挡）
    public void RerouteLogistics_validates_endpoints_before_any_carrier_moves()
    {
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create(runMilestone: true);
        ActionRegistry registry = new(sink, view, eraOrdinalProvider: () => EraOrdinals.E2, towerEstate: simulation.Estate);

        ActionOutcome unknown = registry.Invoke(ActionJsonSamples.RerouteRate(0.5).Replace("\"from\":3", "\"from\":999"));
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, unknown.Result.Status);
        Assert.Contains("from-unknown", unknown.Result.ErrorMessage);

        ActionOutcome degenerate = registry.Invoke("{\"action\":\"RerouteLogistics\",\"from\":3,\"to\":3,\"item\":\"era1:res-wood\",\"rate\":0.5}");
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, degenerate.Result.Status);
        Assert.Contains("lane-degenerate", degenerate.Result.ErrorMessage);
    }
}
