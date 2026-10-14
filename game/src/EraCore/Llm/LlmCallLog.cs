using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;

namespace EraCore.Llm;

/// <summary>
/// One player-visible call-log row (F-091, 方案 §11.7 玩家可见调用日志:
/// 时间/域名/模型/状态/耗时/token 数/错误摘要). BY CONSTRUCTION the record
/// carries neither key material nor prompt text — there is no field that
/// could hold either, so the RL5 guarantee holds even for relays that echo
/// auth material inside error bodies (the summary is the catalog copy, not
/// the raw body). Serialized shape is frozen through
/// <see cref="LlmCallLogJsonContext"/> (CS-1.2: source-generated JSON only).
/// </summary>
/// <param name="TimestampMs">Wall-clock Unix milliseconds (from <see cref="IClock.NowMs"/>).</param>
/// <param name="ProviderId">Provider row id.</param>
/// <param name="Host">Endpoint authority (域名), e.g. api.xiaomimimo.com.</param>
/// <param name="Model">Model id of the call.</param>
/// <param name="Ok">True iff the call produced a merged candidate action.</param>
/// <param name="ErrorKey">Classified key (<see cref="LlmErrorKeys"/>); null on success.</param>
/// <param name="StatusCode">HTTP status when a reply arrived; null otherwise.</param>
/// <param name="ElapsedMs">Whole-call wall time in milliseconds (retries included).</param>
/// <param name="PromptTokens">Provider-reported prompt tokens; null when unreported.</param>
/// <param name="CompletionTokens">Provider-reported completion tokens; null when unreported.</param>
/// <param name="ErrorSummaryZh">Player-facing error copy (catalog text); empty on success.</param>
public sealed record LlmCallLogEntry(
    long TimestampMs,
    string ProviderId,
    string Host,
    string Model,
    bool Ok,
    string? ErrorKey,
    int? StatusCode,
    long ElapsedMs,
    int? PromptTokens,
    int? CompletionTokens,
    string ErrorSummaryZh);

/// <summary>
/// Ring buffer of the newest call-log rows (F-091; §11.6
/// <c>logging.log_retention</c>, default 50 — the T-077 settings panel's
/// 调用日志开关 maps to <see cref="Enabled"/> via
/// <see cref="FromConfig"/>). Timestamps come exclusively from the
/// injected <see cref="IClock"/> (CS-1.4).
/// </summary>
public sealed class LlmCallLogRing
{
    private readonly Queue<LlmCallLogEntry> _entries = new();

    /// <param name="capacity">Maximum rows kept (clamped to ≥1).</param>
    /// <param name="clock">Wall-clock source for row timestamps.</param>
    /// <param name="enabled">False ⇒ the ring stays empty (设置页开关 off).</param>
    public LlmCallLogRing(int capacity, IClock clock, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Capacity = Math.Max(1, capacity);
        Clock = clock;
        Enabled = enabled;
    }

    /// <summary>Maximum number of rows retained.</summary>
    public int Capacity { get; }

    /// <summary>Wall-clock port (CS-1.4).</summary>
    public IClock Clock { get; }

    /// <summary>False when the player switched the call log off (§11.6 show_call_log).</summary>
    public bool Enabled { get; }

    /// <summary>Rows currently retained.</summary>
    public int Count => _entries.Count;

    /// <summary>Builds a ring from the §11.6 logging block of llm.config.json.</summary>
    public static LlmCallLogRing FromConfig(LlmLoggingConfig config, IClock clock) =>
        new(config.LogRetention, clock, config.ShowCallLog);

    /// <summary>
    /// Appends one already-built row, evicting the oldest when over
    /// capacity. A disabled ring drops the row (recording itself is the
    /// privacy switch — nothing is buffered "just in case").
    /// </summary>
    public void Record(LlmCallLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Enabled)
        {
            return;
        }

        _entries.Enqueue(entry);
        while (_entries.Count > Capacity)
        {
            _entries.Dequeue();
        }
    }

    /// <summary>Rows newest-first (panel rendering order); a defensive copy.</summary>
    public IReadOnlyList<LlmCallLogEntry> Snapshot()
    {
        var rows = _entries.ToArray();
        Array.Reverse(rows);
        return rows;
    }

    /// <summary>Clears every retained row (设置页一键清除).</summary>
    public void Clear() => _entries.Clear();
}

/// <summary>
/// Source-generated JSON context for the call log (CS-1.2/D-05:
/// reflection-free serialization in EraCore). The settings panel persists
/// nothing through this context — it is the structured-output interface
/// the UI consumes for rendering and export.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LlmCallLogEntry))]
[JsonSerializable(typeof(LlmCallLogEntry[]))]
public sealed partial class LlmCallLogJsonContext : JsonSerializerContext
{
}
