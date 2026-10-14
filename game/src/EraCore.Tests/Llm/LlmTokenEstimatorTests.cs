using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// token 估算误差带（F-090, 中文≈1/字, T-091）：CJK 每字 1 token、非 CJK 每
/// 4 字符 1 token（向上取整）、混合相加；空串=0。确定性公式钉死 + 单调性。
/// </summary>
// [F-090] C-16 T-091：上下文预算 token 估算
public class LlmTokenEstimatorTests
{
    [Fact]
    public void Pure_chinese_text_estimates_one_token_per_character()
    {
        // legal: 中文≈1/字（§11.7 误差带锚点）——100 汉字恰 100 token
        var text = string.Concat(System.Linq.Enumerable.Repeat("防", 100));
        Assert.Equal(100, LlmTokenEstimator.EstimateTokens(text));
    }

    [Fact]
    public void Pure_ascii_text_estimates_one_token_per_four_characters()
    {
        // legal: ASCII ≈1/4 字（100→25；不足 4 向上取整）
        var text = string.Concat(System.Linq.Enumerable.Repeat("a", 100));
        Assert.Equal(25, LlmTokenEstimator.EstimateTokens(text));
        Assert.Equal(1, LlmTokenEstimator.EstimateTokens("ab"));
        Assert.Equal(1, LlmTokenEstimator.EstimateTokens("abcd"));
        Assert.Equal(2, LlmTokenEstimator.EstimateTokens("abcde"));
    }

    [Fact]
    public void Mixed_text_sums_both_script_bands()
    {
        // legal: 混合=两带相加（中文带精确、拉丁带四取整）
        // 建+塔=2（CJK 带），"now!"=4 字符→1（拉丁带）⇒ 3
        Assert.Equal(3, LlmTokenEstimator.EstimateTokens("建塔now!"));
    }

    [Fact]
    public void Fullwidth_and_cjk_punctuation_belong_to_the_cjk_band()
    {
        // legal: 全角形式/CJK 标点按 1/字计（，！：）
        Assert.Equal(3, LlmTokenEstimator.EstimateTokens("，！："));
        Assert.True(LlmTokenEstimator.IsCjk('防'));
        Assert.True(LlmTokenEstimator.IsCjk('，'));
        Assert.False(LlmTokenEstimator.IsCjk('a'));
        Assert.False(LlmTokenEstimator.IsCjk(' '));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_input_estimates_zero(string? text)
    {
        // boundary: 空输入
        Assert.Equal(0, LlmTokenEstimator.EstimateTokens(text));
    }

    [Fact]
    public void Estimate_is_monotonic_under_appending()
    {
        // legal: 追加文本不减（预算环依赖的单调性）
        var text = "在东门建两座弩塔 crossbow towers, x=3,y=7";
        var before = LlmTokenEstimator.EstimateTokens(text);
        Assert.True(LlmTokenEstimator.EstimateTokens(text + "更多的字") >= before);
    }

    [Fact]
    public void Estimate_band_is_conservative_for_realistic_order_text()
    {
        // boundary: 真实军令样本的误差带——CJK 主导时估算不高于字符数、不低于其半
        var order = "在东门建两座弩塔，并把旧箭塔升到二级，随后集火攻城锤。";
        var estimate = LlmTokenEstimator.EstimateTokens(order);
        Assert.True(estimate <= order.Length,
            $"估算应不超过字符数（中文≈1/字）：{estimate} > {order.Length}");
        Assert.True(estimate >= order.Length / 2,
            $"估算应不小于字符数一半（下带）：{estimate} < {order.Length / 2}");
    }
}
