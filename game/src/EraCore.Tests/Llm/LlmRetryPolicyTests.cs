using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 重试矩阵（F-088, 方案 §11.7：仅 429/1302/1305/5xx/网络错误重试，指数退避
/// ≤3 次；401/400 引导修配置；超时不重试）。纯决策面 + 退避表 + 上限钳制。
/// </summary>
// [F-088][F-162] C-16 T-091：重试矩阵决策表
public class LlmRetryPolicyTests
{
    [Theory]
    [InlineData(LlmErrorKeys.RateLimit)]
    [InlineData(LlmErrorKeys.ServerError)]
    [InlineData(LlmErrorKeys.Network)]
    public void Transient_classes_are_retryable(string key)
    {
        // legal: §11.7 钉死的可重试三键（429/5xx/网络）
        Assert.True(LlmRetryPolicy.ShouldRetryKey(key));
    }

    [Theory]
    [InlineData(LlmErrorKeys.Auth)]
    [InlineData(LlmErrorKeys.CodingPlan)]
    [InlineData(LlmErrorKeys.ContentBlocked)]
    [InlineData(LlmErrorKeys.InvalidModel)]
    [InlineData(LlmErrorKeys.BadRequest)]
    [InlineData(LlmErrorKeys.RedirectBlocked)]
    [InlineData(LlmErrorKeys.TimeoutFirstByte)]
    [InlineData(LlmErrorKeys.TimeoutIdle)]
    [InlineData(LlmErrorKeys.BadResponse)]
    [InlineData(LlmErrorKeys.BadTools)]
    [InlineData(LlmErrorKeys.NoToolCall)]
    [InlineData(LlmErrorKeys.Disabled)]
    [InlineData(LlmErrorKeys.UnknownProvider)]
    [InlineData(LlmErrorKeys.MissingKey)]
    [InlineData(LlmErrorKeys.DomainNotAllowed)]
    [InlineData(LlmErrorKeys.InvalidConfig)]
    public void Config_and_content_classes_never_retry(string key)
    {
        // illegal: 401/400 类/配置类/超时类一律不重试（引导修配置或立即上浮）
        Assert.False(LlmRetryPolicy.ShouldRetryKey(key));
    }

    [Theory]
    [InlineData("1302")]
    [InlineData("1305")]
    public void Pinned_transient_provider_codes_retry(string code)
    {
        // legal: §11.7 钉死的供应商瞬态码（即使被中转站包进 4xx 壳）
        Assert.True(LlmRetryPolicy.ShouldRetryProviderCode(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1315")]
    [InlineData("1301")]
    [InlineData("1300")]
    public void Non_transient_or_absent_provider_codes_do_not_retry(string? code)
    {
        // illegal: 1315（Coding Plan）/1301（内容拦截）/缺码均不重试
        Assert.False(LlmRetryPolicy.ShouldRetryProviderCode(code));
    }

    [Fact]
    public void ShouldRetry_combines_key_and_provider_code()
    {
        // legal: 键可重试或码可重试即重试；两者皆否则不重试
        Assert.True(LlmRetryPolicy.ShouldRetry(LlmErrorKeys.BadRequest, "1302"));
        Assert.True(LlmRetryPolicy.ShouldRetry(LlmErrorKeys.ServerError, null));
        Assert.False(LlmRetryPolicy.ShouldRetry(LlmErrorKeys.BadRequest, "1315"));
        Assert.False(LlmRetryPolicy.ShouldRetry(null, null));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 3)]
    public void Effective_retries_clamp_to_the_design_cap(int configured, int expected)
    {
        // boundary: 配置负数/0=单发；超过 ≤3 上限按 3 钳制
        Assert.Equal(expected, LlmRetryPolicy.EffectiveRetries(configured));
    }

    [Fact]
    public void Backoff_is_exponential_within_the_budget()
    {
        // legal: 0.5s→1s→2s（指数退避），之后不再增长（≤3 次封顶）
        Assert.Equal(TimeSpan.FromMilliseconds(500), LlmRetryPolicy.BackoffDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(1), LlmRetryPolicy.BackoffDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), LlmRetryPolicy.BackoffDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2), LlmRetryPolicy.BackoffDelay(99));
        Assert.Equal(TimeSpan.FromMilliseconds(500), LlmRetryPolicy.BackoffDelay(-1));
    }
}
