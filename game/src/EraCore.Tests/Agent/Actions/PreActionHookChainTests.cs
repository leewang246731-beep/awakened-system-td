using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Agent;
using EraCore.Tests.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-075] T-070 PreAction 钩子链（注册制）：改写串行前馈（后钩见前钩改写）、
// 首个 Block 短路胜出、空链零漂移（同引用返回）。注册表集成面：拦截面=
// Err+ErrHookBlocked:<钩子id>:<原因>；改写面=闭 schema 复验（钩子绕不过 RL6）。

/// <summary>[F-075] PreAction hook chain mechanics: ordered rewrites, first-block-wins.</summary>
public class PreActionHookChainTests
{
    private sealed record StubHook(string Id, Func<PreActionContext, PreActionHookVerdict> Behavior) : IPreActionHook
    {
        public PreActionHookVerdict Apply(PreActionContext context) => Behavior(context);
    }

    [Fact] // legal — 空链零漂移：原样放行（同引用、零注记）
    public void Empty_chain_passes_everything_through()
    {
        PreActionChainResult result = PreActionHookChain.Empty.Apply(
            SystemActionKind.LookAround, ActionJsonSamples.LookAround(), 42, null);
        Assert.False(result.Blocked);
        Assert.Null(result.BlockedBy);
        Assert.Empty(result.Notes);
        Assert.Same(ActionJsonSamples.LookAround(), result.ActionJson);
    }

    [Fact] // legal — 改写串行前馈：第二只钩子看到第一只的改写，注记按序累积
    public void Rewrites_feed_forward_to_later_hooks()
    {
        string first = ActionJsonSamples.SetRally().Replace("hold", "fall");
        string second = first.Replace("lane-north", "lane-south");
        var chain = new PreActionHookChain(new IPreActionHook[]
        {
            new StubHook("h1", _ => PreActionHookVerdict.Rewrite(first, "stance-adjust")),
            new StubHook("h2", context =>
            {
                Assert.Same(first, context.ActionJson); // 前钩改写已入上下文
                return PreActionHookVerdict.Rewrite(second, "lane-adjust");
            }),
        });

        PreActionChainResult result = chain.Apply(SystemActionKind.SetRally, ActionJsonSamples.SetRally(), 42, null);
        Assert.False(result.Blocked);
        Assert.Same(second, result.ActionJson);
        Assert.Equal(new[] { "h1:stance-adjust", "h2:lane-adjust" }, result.Notes);
    }

    [Fact] // illegal — 首个 Block 短路：后续钩子不再运行，Blocker 与原因透出
    public void First_block_wins_and_short_circuits()
    {
        bool thirdRan = false;
        var chain = new PreActionHookChain(new IPreActionHook[]
        {
            new StubHook("pass", _ => PreActionHookVerdict.Pass()),
            new StubHook("blocker", _ => PreActionHookVerdict.Block("ruin-cell(3,4)")),
            new StubHook("after", _ => { thirdRan = true; return PreActionHookVerdict.Pass(); }),
        });

        PreActionChainResult result = chain.Apply(SystemActionKind.BuildPlan, ActionJsonSamples.BuildPlan(), 42, null);
        Assert.True(result.Blocked);
        Assert.Equal("blocker", result.BlockedBy);
        Assert.Equal("ruin-cell(3,4)", result.BlockReason);
        Assert.False(thirdRan);
    }

    [Fact] // boundary — Pass/Block 工厂：半填充形态不可构造（工厂外无路径）
    public void Verdict_factories_fill_exactly_their_own_slots()
    {
        PreActionHookVerdict pass = PreActionHookVerdict.Pass();
        Assert.Equal(PreActionVerdictKind.Pass, pass.Kind);
        Assert.Null(pass.RewrittenActionJson);
        Assert.Null(pass.BlockReason);

        PreActionHookVerdict rewrite = PreActionHookVerdict.Rewrite("{}", "note");
        Assert.Equal(PreActionVerdictKind.Rewrite, rewrite.Kind);
        Assert.Equal("note", rewrite.Note);

        PreActionHookVerdict block = PreActionHookVerdict.Block("why");
        Assert.Equal(PreActionVerdictKind.Block, block.Kind);
        Assert.Equal("why", block.BlockReason);
    }

    [Fact] // illegal — 注册表集成：拦截面=Err + ErrHookBlocked:<id>:<原因>，且 Warn 落日志
    public void Registry_surfaces_hook_blocks_as_err_hook_blocked()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(
            sink,
            worldView: null,
            eraOrdinalProvider: () => EraOrdinals.E2,
            preActionHooks: new IPreActionHook[]
            {
                new StubHook("test-blocker", _ => PreActionHookVerdict.Block("always-blocked")),
            });

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.LookAround());
        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.StartsWith("ErrHookBlocked:test-blocker:always-blocked", outcome.Result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("ErrHookBlocked"));
    }

    [Fact] // illegal — 注册表集成：钩子改写若破坏闭 schema → 拒绝（钩子绕不过 RL6）
    public void Registry_denies_hook_rewrites_that_break_the_closed_schema()
    {
        var sink = new CapturingLogSink();
        string broken = ActionJsonSamples.LookAround() + "extra-not-json";
        var registry = new ActionRegistry(
            sink,
            worldView: null,
            eraOrdinalProvider: () => EraOrdinals.E2,
            preActionHooks: new IPreActionHook[]
            {
                new StubHook("bad-rewriter", _ => PreActionHookVerdict.Rewrite(broken, "broke-it")),
            });

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.LookAround());
        Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.Contains("hook-rewrite-schema", outcome.Result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact] // legal — 注册表集成：合法改写生效（改写后的载荷才是被执行载荷）+ 注记入日志
    public void Registry_executes_the_rewritten_payload_and_logs_the_notice()
    {
        var sink = new CapturingLogSink();
        string rewritten = ActionJsonSamples.ReportToPlayer(summary: "改写后的汇报");
        var registry = new ActionRegistry(
            sink,
            worldView: null,
            eraOrdinalProvider: () => EraOrdinals.E2,
            preActionHooks: new IPreActionHook[]
            {
                new StubHook("rewriter", _ => PreActionHookVerdict.Rewrite(rewritten, "summary-tightened")),
            });

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.ReportToPlayer(summary: "改写前的汇报"));
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var report = Assert.IsType<AgentReport>(outcome.Payload);
        Assert.Equal("改写后的汇报", report.Summary);
        Assert.Contains(sink.Entries, e => e.Message.Contains("hook-rewrite") && e.Message.Contains("rewriter"));
    }

    [Fact] // boundary — 缺省构造（无钩子参数）=空链：既有 19 动作行为零漂移
    public void Registry_default_installs_no_hooks()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E2);
        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.ReportToPlayer());
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.DoesNotContain(sink.Entries, e => e.Message.Contains("hook-rewrite"));
    }
}
