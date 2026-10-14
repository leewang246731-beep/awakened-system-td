namespace EraCore.Llm;

/// <summary>
/// Retry matrix of the C-16 gateway (F-088, 方案 §11.7: 仅
/// 429/1302/1305/5xx/网络错误重试，指数退避 ≤3 次；401/400 引导修配置).
/// Pure decision logic — the delay itself is injected into
/// <see cref="LlmGateway"/> so tests observe the backoff schedule without
/// waiting. Timeouts are deliberately NOT retryable: §11.7 pins the
/// retryable set to the four transient classes, and a timed-out call is
/// surfaced to the player immediately (双超时 already bounds the wait).
/// </summary>
public static class LlmRetryPolicy
{
    /// <summary>Hard cap of retries after the first attempt (§11.7 ≤3 次).</summary>
    public const int MaxRetries = 3; // ci-allow: R7 T-091 §11.7 重试上限 ≤3 次（调用工程域常量，非平衡数值）

    /// <summary>Base of the exponential backoff: 0.5s, 1s, 2s for attempts 1..3.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500); // ci-allow: R7 T-091 §11.7 指数退避基值 500ms（调用工程域常量，非平衡数值）

    /// <summary>GLM/zhipu transient provider codes that retry regardless of the HTTP shell status.</summary>
    public static readonly IReadOnlySet<string> RetryableProviderCodes = new HashSet<string>(
        StringComparer.Ordinal) { "1302", "1305" };

    /// <summary>
    /// True when one <see cref="LlmErrorKeys"/> verdict belongs to a
    /// retryable class: 429 rate limit, 5xx server error, or transport
    /// failure. Auth/config/content verdicts never retry — the player must
    /// fix configuration (§11.7 401/400 引导修配置).
    /// </summary>
    public static bool ShouldRetryKey(string? errorKey) => errorKey switch
    {
        LlmErrorKeys.RateLimit or LlmErrorKeys.ServerError or LlmErrorKeys.Network => true,
        _ => false,
    };

    /// <summary>
    /// True when the provider error code extracted from a failure body is
    /// one of the §11.7-pinned transient codes (1302/1305) — some relays
    /// wrap them in a 4xx shell that would otherwise look non-retryable.
    /// </summary>
    public static bool ShouldRetryProviderCode(string? providerCode) =>
        providerCode is not null && RetryableProviderCodes.Contains(providerCode);

    /// <summary>
    /// Combined verdict for one failed attempt: the classified key is
    /// retryable OR the body carried a pinned transient provider code.
    /// </summary>
    public static bool ShouldRetry(string? errorKey, string? providerCode) =>
        ShouldRetryKey(errorKey) || ShouldRetryProviderCode(providerCode);

    /// <summary>
    /// Retries actually available for one provider row: configured budget
    /// clamped to <see cref="MaxRetries"/> (0 = single-shot).
    /// </summary>
    public static int EffectiveRetries(int configured) => Math.Clamp(configured, 0, MaxRetries);

    /// <summary>
    /// Backoff before retry number <paramref name="retryIndex"/> (0-based):
    /// BaseDelay × 2^retryIndex — 0.5s, 1s, 2s within the ≤3 budget, capped
    /// at the last in-budget step for out-of-range indices.
    /// </summary>
    public static TimeSpan BackoffDelay(int retryIndex)
    {
        var shift = Math.Clamp(retryIndex, 0, MaxRetries - 1);
        return BaseDelay * (1 << shift);
    }
}
