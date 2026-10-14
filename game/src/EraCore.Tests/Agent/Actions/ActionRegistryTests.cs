using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-062][F-070][F-065] C-03 动作注册表（T-068 后 19 动作全量）：
// T-068 13 动作真 handler（治理态+状态投影）、T-065 estate 对未接线显式拒绝、
// 清单外拒绝并记日志、时代门（SpawnScout E1 锁/E2 活）
public class ActionRegistryTests
{
    private static ActionRegistry CreateRegistry(
        CapturingLogSink sink,
        IActionWorldView? view = null,
        int era = EraOrdinals.E1) =>
        new(sink, view, eraOrdinalProvider: () => era);

    // ------------------------------------------------ 3 个真实动作可调

    [Fact]
    public void LookAround_is_callable_and_returns_the_summary_payload()
    {
        // legal：E1 可调，载荷=战局摘要（快照数据来自真 Simulation 视图）
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create();
        ActionRegistry registry = CreateRegistry(sink, view);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.LookAround());
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.Equal(0, outcome.Result.PointsSpent);
        var summary = Assert.IsType<LookAroundSummary>(outcome.Payload);
        Assert.Equal(simulation.CurrentTick, summary.Tick);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("verdict=ok")
            && e.Message.Contains("LookAround"));
    }

    [Fact]
    public void BuildPlan_is_callable_at_era2_and_returns_a_draft_not_an_execution()
    {
        // legal：E2 可调（解锁时代门放行）→ 草案载荷；同时仿真未被改动
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create();
        ActionRegistry registry = CreateRegistry(sink, view, era: EraOrdinals.E2);
        long hashBefore = simulation.ComputeStateHash();

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.BuildPlan());
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var draft = Assert.IsType<BuildPlanDraft>(outcome.Payload);
        Assert.Equal("era1:tower-wood-arrow", draft.TowerId);
        Assert.Equal(2, draft.Steps.Count);
        Assert.Equal(simulation.ComputeStateHash(), hashBefore); // 草案不落子（执行属 T-053 管线）
    }

    [Fact]
    public void BuildPlan_refuses_unknown_towers_with_err_target_invalid()
    {
        // illegal：目录外塔 → ErrTargetInvalid + Warn 日志
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, RegistryFixture.Create().View, era: EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.BuildPlan(towerId: "era1:tower-missing"));
        Assert.Equal(ActionResultStatus.ErrTargetInvalid, outcome.Result.Status);
        Assert.Null(outcome.Payload);
        Assert.Contains(sink.Entries, e => e.Message.Contains("target-invalid"));
    }

    [Fact]
    public void ReportToPlayer_is_callable_and_returns_the_formatted_report()
    {
        // legal：E1 可调，载荷=AgentReport（verdict 映射+findings）
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.ReportToPlayer());
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var report = Assert.IsType<AgentReport>(outcome.Payload);
        Assert.Equal(2, report.Findings.Count);
        Assert.All(report.Findings, f => Assert.Equal(FindingVerdict.Confirmed, f.Verdict));
    }

    [Fact]
    public void BuildPlan_without_a_world_view_refuses_instead_of_guessing()
    {
        // illegal：未接视图的注册表显式拒绝（不编造战局）
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, view: null, era: EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.BuildPlan());
        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("world-view-missing", outcome.Result.ErrorMessage);
    }

    // ------------------------------------------------ T-068：13 个新 handler 全部可调

    [Theory] // legal：视图+治理态在位 → 13 个 T-068 动作全部真实执行（真 handler，禁止伪报成功）
    [InlineData("InspectTile")]
    [InlineData("QueryWorld")]
    [InlineData("SetRally")]
    [InlineData("SetStockpile")]
    [InlineData("FocusFire")]
    [InlineData("SetTowerMode")]
    [InlineData("TriggerScroll")]
    [InlineData("RerouteLogistics")]
    [InlineData("AskPlayer")]
    [InlineData("TodoBoard")]
    [InlineData("SpawnScout")]
    [InlineData("SetAlarm")]
    [InlineData("StopTask")]
    public void T068_verbs_execute_against_real_state_or_refuse_honestly(string verb)
    {
        // legal：带视图的注册表——动作要么真执行（目标合法时 Ok+载荷），
        // 要么按状态显式拒绝（目标不在场等）——两者都绝不是 not-implemented。
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create();
        var registry = new ActionRegistry(
            sink, view, eraOrdinalProvider: () => EraOrdinals.E2,
            towerEstate: simulation.Estate,
            armedScrolls: new HashSet<string>(StringComparer.Ordinal) { "era1:scroll-arrow-rain" });
        Assert.True(registry.HasLiveHandler(ActionCatalog.TryResolveName(verb, out var kind) ? kind : default));

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.All()[verb]());
        // 非 Ok 的只能是状态性拒绝（ErrTargetInvalid 等），未接线档与伪报成功都被排除
        if (outcome.Result.Status != ActionResultStatus.Ok)
        {
            Assert.Contains("target-invalid", outcome.Result.ErrorMessage);
            Assert.DoesNotContain("not-implemented", outcome.Result.ErrorMessage);
            Assert.NotEqual(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        }
    }

    [Theory] // illegal：无视图注册表 → 视图态依赖动词显式拒绝（state-view-missing），仍不是伪报成功
    [InlineData("InspectTile")]
    [InlineData("QueryWorld")]
    [InlineData("SetStockpile")]
    [InlineData("FocusFire")]
    [InlineData("SetTowerMode")]
    [InlineData("RerouteLogistics")]
    public void State_view_verbs_refuse_without_a_state_projection(string verb)
    {
        var sink = new CapturingLogSink();
        var bare = new BareViewFake();
        ActionRegistry registry = new(sink, bare, eraOrdinalProvider: () => EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.All()[verb]());
        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("state-view-missing", outcome.Result.ErrorMessage);
        Assert.Null(outcome.Payload);
    }

    [Theory] // illegal：estate 未接线 → 升级/出售保持诚实拒绝档（T-065 口径不变）
    [InlineData("UpgradeTower")]
    [InlineData("SellTower")]
    public void Estate_verbs_still_refuse_without_a_wired_estate(string verb)
    {
        var sink = new CapturingLogSink();
        var (simulation, view) = RegistryFixture.Create();
        ActionRegistry registry = new(sink, view, eraOrdinalProvider: () => EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.All()[verb]());
        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("not-implemented", outcome.Result.ErrorMessage);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("verdict=refused"));
    }

    [Fact]
    public void Registered_kinds_list_the_whole_closed_set()
    {
        // legal：19 动作全注册（枚举全集一致）
        Assert.Equal(19, ActionRegistry.RegisteredKinds.Count);
        Assert.Equal(
            Enum.GetValues<SystemActionKind>().OrderBy(k => (int)k),
            ActionRegistry.RegisteredKinds.OrderBy(k => (int)k));
    }

    // ------------------------------------------------ 解锁时代门（数据化 G.2 列）

    [Fact]
    public void SpawnScout_in_era1_returns_capability_locked_with_clarifying_copy()
    {
        // boundary：E1 调用巡逻级 → ErrCapabilityLocked + 澄清话术（N9/A2），不静默丢弃
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, view: null, era: EraOrdinals.E1);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.SpawnScout());
        Assert.Equal(ActionResultStatus.ErrCapabilityLocked, outcome.Result.Status);
        Assert.Contains("capability-locked:SpawnScout", outcome.Result.ErrorMessage);
        Assert.Contains("clarifying", outcome.Result.ErrorMessage);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Info && e.Message.Contains("verdict=era-locked"));
    }

    [Fact]
    public void SpawnScout_in_era2_runs_the_live_handler()
    {
        // legal：时代已解锁 → T-068 真 handler 执行（巡逻任务落治理态），档位可区分
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, view: null, era: EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.SpawnScout());
        Assert.NotEqual(ActionResultStatus.ErrCapabilityLocked, outcome.Result.Status);
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var ack = Assert.IsType<SpawnScoutAck>(outcome.Payload);
        Assert.Equal("lane-north", ack.Lane);
        Assert.Single(registry.State.Tasks);
    }

    [Fact]
    public void BuildPlan_in_era1_is_also_behind_the_era_gate()
    {
        // boundary：解锁表数据驱动——建造级 E1 同样落锁（表不止 SpawnScout 一行）
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, RegistryFixture.Create().View, era: EraOrdinals.E1);

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.BuildPlan());
        Assert.Equal(ActionResultStatus.ErrCapabilityLocked, outcome.Result.Status);
        Assert.Contains("BuildPlan", outcome.Result.ErrorMessage);
    }

    // ------------------------------------------------ 清单外拒绝 + 记日志（验收口径）

    [Theory] // illegal：清单外动作名（含大小写漂移与伪造动词）→ ErrSchemaDenied
    [InlineData("{\"action\":\"SellEverything\",\"cell\":{\"x\":1,\"y\":1}}")]
    [InlineData("{\"action\":\"lookaround\"}")]
    [InlineData("{\"action\":\"HackThePlanet\"}")]
    [InlineData("{}")]
    [InlineData("{\"tower_id\":\"era1:tower-wood-arrow\"}")]
    public void Out_of_list_actions_are_denied_and_logged(string payload)
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, RegistryFixture.Create().View);

        ActionOutcome outcome = registry.Invoke(payload);
        Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.Null(outcome.Payload);
        Assert.Equal(0, outcome.Result.PointsSpent);

        CapturingLogSink.Entry entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("verdict=denied", entry.Message);
        Assert.Contains("reason=", entry.Message);
        Assert.Contains("payload=", entry.Message); // 原始载荷摘要随行入日志
    }

    [Theory] // illegal：坏 JSON / 空载荷 → ErrSchemaDenied + 日志
    [InlineData("{\"action\":")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2]")]
    public void Malformed_payloads_are_denied_and_logged(string payload)
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink);

        Assert.Equal(ActionResultStatus.ErrSchemaDenied, registry.Invoke(payload).Result.Status);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public void Schema_violations_inside_known_verbs_are_denied_and_logged()
    {
        // illegal：清单内动作但字段越界（SpawnScout duration 越界 + E1）→ ErrSchemaDenied（schema 门先于时代门）
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, view: null, era: EraOrdinals.E1);

        ActionOutcome outcome = registry.Invoke("{\"action\":\"SpawnScout\",\"lane\":\"lane-north\",\"duration\":999.0}");
        Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.Contains("/duration", outcome.Result.ErrorMessage);
        Assert.Contains("duration", sink.Entries.Single().Message);
    }

    [Fact]
    public void Deny_logs_never_carry_credential_patterns_from_the_payload()
    {
        // illegal：载荷夹带密钥形态（api_key/sk- 令牌）→ 日志只见 <redacted>，密文不落盘
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink);
        string payload = "{\"action\":\"StealKey\",\"api_key\":\"demo-secret-987654321\",\"note\":\"sk-demo1234567\"}";

        Assert.Equal(ActionResultStatus.ErrSchemaDenied, registry.Invoke(payload).Result.Status);
        string logged = sink.JoinedMessages;
        Assert.DoesNotContain("demo-secret-987654321", logged);
        Assert.DoesNotContain("sk-demo1234567", logged);
        Assert.Contains(ActionLog.RedactedToken, logged);
    }

    [Fact]
    public void Deny_log_tick_is_minus_one_without_a_world_view()
    {
        // boundary：未接视图时日志 tick=-1（ILogSink 契约：仿真外域）
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink);

        registry.Invoke("{\"action\":\"Nope\"}");
        Assert.Equal(-1, sink.Entries.Single().Tick);
    }

    // ------------------------------------------------ K-5 执行端口形态

    [Fact]
    public void Execute_routes_the_typed_entry_through_the_same_door()
    {
        // legal：IActionExecutor 形态（K-5 stage 5 端口）
        var sink = new CapturingLogSink();
        ActionRegistry registry = CreateRegistry(sink, RegistryFixture.Create().View);

        Assert.Equal(ActionResultStatus.Ok, registry.Execute(SystemActionKind.LookAround, ActionJsonSamples.LookAround()).Status);

        // illegal：kind 与载荷动词不符 → schema 拒绝
        ActionResult mismatch = registry.Execute(SystemActionKind.LookAround, ActionJsonSamples.ReportToPlayer());
        Assert.Equal(ActionResultStatus.ErrSchemaDenied, mismatch.Status);
        Assert.Contains("verb-kind-mismatch", mismatch.ErrorMessage);
    }
}
