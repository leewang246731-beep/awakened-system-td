using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 协议注册表与共享归约基座的契约测试：两适配器都实现 IChatProtocol、
/// 归约产物字段语义一致。
/// </summary>
// [F-083] C-16 T-072：协议注册表
public class IChatProtocolContractTests
{
    [Fact]
    public void Registry_resolves_both_protocols_and_rejects_nothing_else()
    {
        // legal: 两协议；枚举封闭
        Assert.Equal("openai", ChatProtocols.Resolve(LlmProtocol.OpenAi).ProtocolId);
        Assert.Equal("anthropic", ChatProtocols.Resolve(LlmProtocol.Anthropic).ProtocolId);
    }

    [Fact]
    public void Both_reducers_report_empty_outcome_before_any_frame()
    {
        // boundary: 零帧归约（空流容错）
        foreach (var protocol in new[] { LlmProtocol.OpenAi, LlmProtocol.Anthropic })
        {
            var outcome = ChatProtocols.Resolve(protocol).CreateReducer().BuildOutcome();
            Assert.Equal(string.Empty, outcome.ContentText);
            Assert.Equal(string.Empty, outcome.ReasoningText);
            Assert.Null(outcome.ToolName);
            Assert.False(outcome.SawToolCall);
            Assert.False(outcome.SawReasoningChannel);
        }
    }

    [Fact]
    public void Outcome_flags_are_consistent_with_their_fields()
    {
        // legal: SawToolCall/SawReasoningChannel 语义自洽
        var withTool = new LlmStreamOutcome("", "think", "BuildPlan", "{}", "tool");
        var without = LlmStreamOutcome.Empty;

        Assert.True(withTool.SawToolCall);
        Assert.True(withTool.SawReasoningChannel);
        Assert.False(without.SawToolCall);
        Assert.False(without.SawReasoningChannel);
    }
}
