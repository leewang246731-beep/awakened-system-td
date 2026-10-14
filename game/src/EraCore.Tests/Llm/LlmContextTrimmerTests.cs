using EraCore.Contracts.Agent;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 上下文裁剪（F-090, §11.7 system+最近 N 轮，工具调用对成对保留, T-091）：
/// system 恒留、N 轮窗口、预算超限最旧轮丢弃循环、tool/assistant 对不拆散、
/// 裁剪永不掏空 prompt、system+最近轮仍超预算时的下限行为。
/// </summary>
// [F-090][F-162] C-16 T-091：上下文裁剪器
public class LlmContextTrimmerTests
{
    private static LlmChatMessage Msg(string role, string content) => new(role, content);

    private static List<LlmChatMessage> History(int rounds, string markerPrefix = "第")
    {
        var messages = new List<LlmChatMessage> { Msg("system", "你是建造指挥官。") };
        for (var i = 1; i <= rounds; i++)
        {
            messages.Add(Msg("user", $"{markerPrefix}{i}轮军令"));
            messages.Add(Msg("assistant", $"第{i}轮已完成"));
        }

        return messages;
    }

    [Fact]
    public void System_messages_always_survive_any_cut()
    {
        // legal: system 恒留（承载闭合动作契约），即使轮全被预算裁掉
        var trimmed = LlmContextTrimmer.Trim(History(3), historyRounds: 1, contextBudgetTokens: 1);

        Assert.Contains(trimmed, m => m.Role == "system");
        Assert.Equal("你是建造指挥官。", trimmed[0].Content);
        Assert.Equal("system", trimmed[0].Role);
    }

    [Fact]
    public void Last_n_rounds_are_kept_and_older_rounds_dropped()
    {
        // legal: system+最近 2 轮——第 1 轮丢、2/3 轮留
        var trimmed = LlmContextTrimmer.Trim(History(3), historyRounds: 2, contextBudgetTokens: 0);

        Assert.Equal(
            ["system", "user", "assistant", "user", "assistant"],
            trimmed.Select(m => m.Role).ToArray());
        Assert.Contains(trimmed, m => m.Content == "第2轮军令");
        Assert.Contains(trimmed, m => m.Content == "第3轮军令");
        Assert.DoesNotContain(trimmed, m => m.Content == "第1轮军令");
    }

    [Fact]
    public void Zero_or_negative_history_keeps_only_the_newest_round()
    {
        // boundary: history_rounds=0——仅当前轮（system+最后 user/assistant）
        foreach (var rounds in new[] { 0, -3 })
        {
            var trimmed = LlmContextTrimmer.Trim(History(4), historyRounds: rounds, contextBudgetTokens: 0);

            Assert.Equal(["system", "user", "assistant"], trimmed.Select(m => m.Role).ToArray());
            Assert.Contains(trimmed, m => m.Content == "第4轮军令");
        }
    }

    [Fact]
    public void Budget_overflow_drops_oldest_rounds_in_a_loop_until_it_fits()
    {
        // boundary: 预算环——逐轮丢最旧直至装下；system+最后轮兜底不丢
        // 每条消息 ≈ content 估算 + 4 开销：第 N 轮军令≈6 字 → 6+4=10；完成≈7 字 → 11
        var history = History(4);
        var totalAll = LlmContextTrimmer.EstimateMessages(history);
        var trimmed = LlmContextTrimmer.Trim(
            history, historyRounds: 10, contextBudgetTokens: totalAll / 2);

        Assert.True(LlmContextTrimmer.EstimateMessages(trimmed) <= totalAll / 2
            || trimmed.Count(m => m.Role != "system") == 2,
            "预算装不下时至少保住最后一轮");
        Assert.Contains(trimmed, m => m.Content == "第4轮军令");
        Assert.DoesNotContain(trimmed, m => m.Content == "第1轮军令");
    }

    [Fact]
    public void Tiny_budget_still_keeps_system_and_the_newest_round()
    {
        // illegal: 预算 1 token 也掏不空——system+最后轮永在（拒绝空 prompt）
        var trimmed = LlmContextTrimmer.Trim(History(3), historyRounds: 10, contextBudgetTokens: 1);

        Assert.Equal(["system", "user", "assistant"], trimmed.Select(m => m.Role).ToArray());
        Assert.Equal("第3轮军令", trimmed[1].Content);
    }

    [Fact]
    public void Tool_call_pairs_are_never_split_by_any_cut()
    {
        // legal: 工具调用历史一致性——assistant(带工具调用)+tool(结果)对成对保留：
        // 轮以 user 开界，切点永不落在对中间
        var history = new List<LlmChatMessage>
        {
            Msg("system", "指挥官。"),
            Msg("user", "第1轮军令"),
            Msg("assistant", "{\"action\":\"BuildPlan\",…}"),
            Msg("tool", "{\"ok\":true,\"built\":2}"),
            Msg("user", "第2轮军令"),
            Msg("assistant", "{\"action\":\"SellTower\",…}"),
            Msg("tool", "{\"ok\":true,\"sold\":1}"),
            Msg("user", "第3轮军令"),
            Msg("assistant", "{\"action\":\"BuildPlan\",…}"),
            Msg("tool", "{\"ok\":true,\"built\":1}"),
        };

        // 三档裁剪（轮窗口/小预算/极小预算）下：每个 tool 消息前必有 assistant
        foreach (var (rounds, budget) in new[] { (1, 0), (2, 0), (10, 60), (10, 1) })
        {
            var trimmed = LlmContextTrimmer.Trim(history, rounds, budget);
            Assert.Contains(trimmed, m => m.Role != "system");
            for (var i = 0; i < trimmed.Count; i++)
            {
                if (trimmed[i].Role == "tool")
                {
                    Assert.True(i > 0 && trimmed[i - 1].Role == "assistant",
                        $"第 {i} 位 tool 消息成为孤儿（rounds={rounds},budget={budget}）");
                }
            }
        }
    }

    [Fact]
    public void Orphan_traffic_before_the_first_user_message_is_dropped_first()
    {
        // boundary: 首个 user 之前的孤儿 assistant/tool（无开圈者）自成第 0 轮——
        // 任何窗口裁剪先丢它
        var history = new List<LlmChatMessage>
        {
            Msg("system", "指挥官。"),
            Msg("assistant", "上局遗留的半句话"),
            Msg("tool", "{\"stale\":true}"),
            Msg("user", "本轮军令"),
            Msg("assistant", "已执行"),
        };

        var trimmed = LlmContextTrimmer.Trim(history, historyRounds: 1, contextBudgetTokens: 0);

        Assert.Equal(["system", "user", "assistant"], trimmed.Select(m => m.Role).ToArray());
        Assert.DoesNotContain(trimmed, m => m.Content.Contains("遗留"));
    }

    [Fact]
    public void Multiple_system_messages_keep_their_relative_order()
    {
        // legal: 多条 system（人格+规则）全保留且保序，在前
        var history = new List<LlmChatMessage>
        {
            Msg("system", "人格A。"),
            Msg("user", "第1轮军令"),
            Msg("assistant", "完成"),
            Msg("system", "补充规则B。"),
            Msg("user", "第2轮军令"),
            Msg("assistant", "完成"),
        };

        var trimmed = LlmContextTrimmer.Trim(history, historyRounds: 1, contextBudgetTokens: 0);

        Assert.Equal(
            ["system", "system", "user", "assistant"],
            trimmed.Select(m => m.Role).ToArray());
        Assert.Equal("人格A。", trimmed[0].Content);
        Assert.Equal("补充规则B。", trimmed[1].Content);
    }

    [Fact]
    public void Empty_history_trims_to_empty()
    {
        // boundary: 空进空出
        Assert.Empty(LlmContextTrimmer.Trim([], historyRounds: 5, contextBudgetTokens: 100));
    }

    [Fact]
    public void Null_history_is_rejected()
    {
        // illegal: null 入参——ArgumentNullException（显式拒绝）
        Assert.Throws<ArgumentNullException>(
            () => LlmContextTrimmer.Trim(null!, historyRounds: 1, contextBudgetTokens: 1));
    }

    [Fact]
    public void Estimate_adds_per_message_overhead_to_content()
    {
        // legal: 消息估算=内容估算+每条 4 token 开销（保守预算）
        var single = new List<LlmChatMessage> { Msg("user", "防") };
        Assert.Equal(
            1 + LlmContextTrimmer.PerMessageOverheadTokens,
            LlmContextTrimmer.EstimateMessages(single));
    }

    [Fact]
    public async Task Gateway_applies_the_provider_context_window_to_sent_messages()
    {
        // legal: 网关组合——history_rounds=1 时wire 请求只带 system+最后轮
        var port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        var row = LlmFixtures.TestProvider(useStream: false, historyRounds: 1);
        secrets.Store(row.Id, "synthetic-key-value-000");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(active: row), port, secrets,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        port.EnqueueJson(LlmFixtures.OpenAiNonStream);

        var result = await gateway.RouteAsync(
            gateway.FindProvider(gateway.ActiveProviderId),
            History(3), LlmFixtures.BuildPlanToolsJson);

        Assert.True(result.Ok, result.ErrorKey);
        var call = Assert.Single(port.Calls);
        using var body = System.Text.Json.JsonDocument.Parse(call.Body);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }
}
