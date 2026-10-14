using System.Text.Json;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 玩家可见调用日志（F-091, §11.7 时间/域名/模型/状态/耗时/token 数/错误摘要,
/// 永不记录完整 Key；T-091 补环形缓冲 N=50+无 key 无完整 prompt）：字段面、
/// 环形逐出、快照序、开关语义、时间戳来自注入墙钟（CS-1.4）、序列化无敏感面。
/// </summary>
// [F-091][F-162] C-16 T-091：调用日志环形缓冲
public class LlmCallLogTests
{
    private static LlmCallLogEntry Entry(
        long timestamp = 1_700_000_000_000,
        string providerId = "test-provider",
        bool ok = true,
        string? errorKey = null,
        int? status = 200,
        int? promptTokens = 12,
        int? completionTokens = 34) => new(
        timestamp, providerId, "api.test.example.com", "test-model",
        ok, errorKey, status, 567, promptTokens, completionTokens,
        errorKey is null ? string.Empty : ErrorCopy(errorKey));

    private static string ErrorCopy(string key) =>
        LlmErrorCatalog.TryGetCopy(key, out var zh, out _) ? zh : string.Empty;

    [Fact]
    public void Ring_from_default_config_keeps_fifty_rows()
    {
        // legal: §11.6 log_retention=50 缺省——第 51 条逐出最旧
        var ring = LlmCallLogRing.FromConfig(new LlmLoggingConfig(), new FixedWallClock(0));

        Assert.Equal(50, ring.Capacity);
        for (var i = 0; i < 60; i++)
        {
            ring.Record(Entry(timestamp: 1_000 + i));
        }

        Assert.Equal(50, ring.Count);
        var newest = ring.Snapshot();
        Assert.Equal(1_059, newest[0].TimestampMs);
        Assert.Equal(1_010, newest[^1].TimestampMs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(200)]
    public void Capacity_is_clamped_to_at_least_one(int configured)
    {
        // boundary: 容量下限 1（0/负数不炸，退化为单行）
        var ring = new LlmCallLogRing(configured, new FixedWallClock(0));

        Assert.Equal(Math.Max(1, configured), ring.Capacity);
        ring.Record(Entry(timestamp: 1));
        ring.Record(Entry(timestamp: 2));
        Assert.Equal(Math.Min(2, ring.Capacity), ring.Count);
        Assert.Equal(2, ring.Snapshot()[0].TimestampMs); // 最新行恒在第 0 位
    }

    [Fact]
    public void Snapshot_is_newest_first_and_a_defensive_copy()
    {
        // legal: 面板渲染序（新→旧）；快照后再 Record 不影响已取快照
        var ring = new LlmCallLogRing(10, new FixedWallClock(0));
        ring.Record(Entry(timestamp: 1));
        ring.Record(Entry(timestamp: 2));
        ring.Record(Entry(timestamp: 3));

        var snapshot = ring.Snapshot();
        Assert.Equal([3, 2, 1], snapshot.Select(e => e.TimestampMs).ToArray());

        ring.Record(Entry(timestamp: 4));
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(4, ring.Snapshot()[0].TimestampMs);
    }

    [Fact]
    public void Disabled_ring_records_nothing()
    {
        // illegal: 设置页开关关闭（show_call_log=false）——一行不存（不缓冲“备用”）
        var ring = LlmCallLogRing.FromConfig(
            new LlmLoggingConfig { ShowCallLog = false }, new FixedWallClock(0));

        Assert.False(ring.Enabled);
        ring.Record(Entry());

        Assert.Equal(0, ring.Count);
        Assert.Empty(ring.Snapshot());
    }

    [Fact]
    public void Clear_empties_the_ring()
    {
        // legal: 设置页一键清除
        var ring = new LlmCallLogRing(10, new FixedWallClock(0));
        ring.Record(Entry());
        Assert.Equal(1, ring.Count);

        ring.Clear();

        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Null_entry_is_rejected()
    {
        // illegal: null 行——显式拒绝
        var ring = new LlmCallLogRing(10, new FixedWallClock(0));
        Assert.Throws<ArgumentNullException>(() => ring.Record(null!));
    }

    [Fact]
    public void Timestamps_come_from_the_injected_clock()
    {
        // legal: CS-1.4——时间戳唯一来源是注入墙钟（推进即前进）
        var clock = new FixedWallClock(100);
        var ring = new LlmCallLogRing(10, clock);

        ring.Record(Entry(timestamp: clock.NowMs));
        clock.Advance(250);
        ring.Record(Entry(timestamp: clock.NowMs));

        Assert.Equal([350, 100], ring.Snapshot().Select(e => e.TimestampMs).ToArray());
    }

    [Fact]
    public void Serialized_entries_carry_no_key_shaped_or_prompt_material()
    {
        // illegal: RL5——结构化输出（UI 渲染/导出面）永不携带密钥形态或完整 prompt
        var entry = Entry(
            errorKey: LlmErrorKeys.Auth,
            status: 401,
            promptTokens: null,
            completionTokens: null);

        var json = JsonSerializer.Serialize(entry, LlmCallLogJsonContext.Default.LlmCallLogEntry);

        Assert.DoesNotMatch("sk-[A-Za-z0-9]{8,}", json);
        Assert.DoesNotContain("Bearer ", json);
        Assert.DoesNotContain("Authorization", json);
        Assert.DoesNotContain("api_key", json);
        // 错误摘要=文案表文本（含 401 引导语义），非原始响应体
        Assert.Contains("密钥无效", entry.ErrorSummaryZh);
    }

    /// <summary>OpenAI 非流式样例 + 根级 usage 块（token 数捕获面，F-091）。</summary>
    private static readonly string OpenAiNonStreamWithUsage = LlmFixtures.OpenAiNonStream
        .Replace(
            "\"finish_reason\":\"tool_calls\"}]}",
            "\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":8,\"total_tokens\":17}}");

    [Fact]
    public async Task Gateway_appends_one_row_per_completed_route_with_final_outcome()
    {
        // legal: 组合——成功+401 两条路由各落一行（域名/模型/状态/耗时/token 数）
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var ring = LlmCallLogRing.FromConfig(new LlmLoggingConfig(), new FixedWallClock(1_000));
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(
                active: LlmFixtures.TestProvider(useStream: false, retries: 0)),
            port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            callLog: ring);
        Assert.Equal(0, ring.Count); // 前置：路由发生前日志为空
        port.EnqueueReply(_ => new HttpPortResponse(200, OpenAiNonStreamWithUsage, null));
        port.EnqueueError(401, "{\"error\":{\"message\":\"bad key\"}}");

        var ok = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);
        var failed = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "在东门建两座弩塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.True(ok.Ok, ok.ErrorKey);
        Assert.Equal(LlmErrorKeys.Auth, failed.ErrorKey);
        var rows = ring.Snapshot();
        Assert.Equal(2, rows.Count);

        var failure = rows[0]; // newest first
        Assert.False(failure.Ok);
        Assert.Equal(LlmErrorKeys.Auth, failure.ErrorKey);
        Assert.Equal(401, failure.StatusCode);
        Assert.Contains("密钥无效", failure.ErrorSummaryZh);

        var success = rows[1];
        Assert.True(success.Ok);
        Assert.Equal("api.test.example.com", success.Host);
        Assert.Equal("test-model", success.Model);
        Assert.Equal("test-provider", success.ProviderId);
        Assert.Equal(200, success.StatusCode);
        Assert.Equal(9, success.PromptTokens);
        Assert.Equal(8, success.CompletionTokens);
        Assert.True(success.ElapsedMs >= 0);
        // 用户 prompt 文本与请求体永不入日志
        var all = JsonSerializer.Serialize(
            ring.Snapshot().ToArray(), LlmCallLogJsonContext.Default.LlmCallLogEntryArray);
        Assert.DoesNotContain("在东门建两座弩塔", all);
        Assert.DoesNotContain("synthetic-key-value", all);
    }

    [Fact]
    public async Task Gateway_without_a_ring_still_routes_normally()
    {
        // boundary: 未挂日志环——路由不受影响（F-091 可选挂载）
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store("test-provider", "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(
                active: LlmFixtures.TestProvider(useStream: false, retries: 0)),
            port, secrets);
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "建塔")],
            LlmFixtures.BuildPlanToolsJson);

        Assert.True(result.Ok, result.ErrorKey);
    }
}
