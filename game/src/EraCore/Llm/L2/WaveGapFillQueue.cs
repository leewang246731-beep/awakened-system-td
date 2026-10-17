namespace EraCore.Llm.L2;

/// <summary>
/// Candidate action produced by one L2/L3 fill attempt (T-137): a canonical
/// action JSON ({"action":verb,…}, registry gates still apply downstream) or
/// a failure with the gateway/fill error key plus the degradation
/// bookkeeping the wave-gap queue needs.
/// </summary>
/// <param name="Ok">True iff a canonical candidate action JSON was produced.</param>
/// <param name="ActionJson">Canonical candidate ({"action":verb,…}); null on failure.</param>
/// <param name="ErrorKey">Error key on failure (gateway keys or fill-level keys).</param>
/// <param name="Degradation">Fill-level fallback reason; None on success.</param>
public sealed record L2IntentFillResult(
    bool Ok,
    string? ActionJson,
    string? ErrorKey,
    L2DegradationReason Degradation)
{
    /// <summary>Success factory.</summary>
    public static L2IntentFillResult Succeeded(string actionJson) =>
        new(true, actionJson, null, L2DegradationReason.None);

    /// <summary>Failure factory carrying the fallback reason.</summary>
    public static L2IntentFillResult Failed(string errorKey, L2DegradationReason reason) =>
        new(false, null, errorKey, reason);
}

/// <summary>
/// The intent-fill port (T-137 波间异步): one military order text → one
/// candidate action JSON. EraCore owns the queue/state machine; the port
/// implementation lives in the presentation host and wraps
/// <c>LlmActionRouter.ProposeAsync</c> against the synthesized L2 provider
/// row (or the L3 row). The port MUST be non-blocking on the caller's
/// thread — the queue's tests assert the enqueue/open paths return before
/// any fill completes (不阻塞模拟 tick).
/// </summary>
public interface IL2IntentFillPort
{
    /// <summary>Fills one order asynchronously. Deadline enforcement is the queue's job (the port honours only the token).</summary>
    Task<L2IntentFillResult> FillAsync(string orderText, CancellationToken cancellationToken);
}

/// <summary>One queued order inside the wave-gap fill queue.</summary>
/// <param name="Id">Monotonic queue id (1-based).</param>
/// <param name="OrderText">Raw military order text (untrusted input — wrapped at the router, F-096 ①).</param>
/// <param name="QueuedAtMs">Queue-clock timestamp (injected clock — wall ms, monotonic).</param>
public sealed record L2QueuedFill(long Id, string OrderText, long QueuedAtMs);

/// <summary>One landed fill (queue completion payload).</summary>
/// <param name="Order">The completed order.</param>
/// <param name="Result">The fill outcome (candidate JSON or failure).</param>
public sealed record L2FillCompletion(L2QueuedFill Order, L2IntentFillResult Result);

/// <summary>
/// The wave-gap async fill queue (T-137 验收面「波间异步」): orders that L1
/// could not parse wait here; on a wave-gap open the host starts at most one
/// fill at a time, each under the frozen budget
/// (<see cref="L2Facts.WaveGapFillBudget"/>); timeout = abandon THIS round
/// (放弃本轮不卡波 — the sim never waits on this queue; the host's frame loop
/// keeps stepping ticks while a fill runs). Pure state machine over an
/// injected clock — the async task itself is the host's; this queue only
/// tracks what may start, what expired and what landed.
/// </summary>
public sealed class WaveGapFillQueue
{
    private readonly Func<long> _clockMs;
    private readonly Dictionary<long, L2QueuedFill> _waiting = new();
    private long _nextId;
    private long? _inFlightId;
    private long _inFlightDeadlineMs;

    /// <param name="clockMs">Monotonic millisecond clock (host injects wall ms; tests inject a fake).</param>
    public WaveGapFillQueue(Func<long> clockMs)
    {
        _clockMs = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
    }

    /// <summary>Fill budget in ms (mirrors <see cref="L2Facts.WaveGapFillBudget"/> — frozen with it).</summary>
    public static long BudgetMs => (long)L2Facts.WaveGapFillBudget.TotalMilliseconds;

    /// <summary>The order currently allowed to run (exactly one at a time), null when idle.</summary>
    public L2QueuedFill? InFlight => _inFlightId is { } id && _waiting.TryGetValue(id, out L2QueuedFill? fill)
        ? fill
        : null;

    /// <summary>In-flight deadline (ms clock value); meaningful only while <see cref="InFlight"/> is live.</summary>
    public long InFlightDeadlineMs => _inFlightDeadlineMs;

    /// <summary>Orders waiting (excluding the in-flight one), insertion order.</summary>
    public IReadOnlyList<L2QueuedFill> Waiting =>
        _waiting.Values.Where(f => f.Id != _inFlightId).OrderBy(f => f.Id).ToArray();

    /// <summary>Total orders tracked (waiting + in-flight).</summary>
    public int Count => _waiting.Count;

    /// <summary>
    /// Enqueues one L1-clarified order. Returns the queue entry (or null when
    /// the text is blank — a clarification with no text cannot be re-filled).
    /// </summary>
    public L2QueuedFill? Enqueue(string orderText)
    {
        if (string.IsNullOrWhiteSpace(orderText))
        {
            return null;
        }

        var entry = new L2QueuedFill(++_nextId, orderText, _clockMs());
        _waiting[entry.Id] = entry;
        return entry;
    }

    /// <summary>
    /// Wave-gap open: promotes the head waiting order to in-flight under the
    /// budget. Returns it (null when idle or one is already in flight). The
    /// PREVIOUS in-flight order is first expired-checked — a fill that
    /// outlived its deadline is abandoned here (its late result is then
    /// refused by <see cref="Complete"/>).
    /// </summary>
    public L2QueuedFill? OpenWindow()
    {
        ExpireInFlight();
        if (_inFlightId is not null || _waiting.Count == 0)
        {
            return null;
        }

        long head = _waiting.Keys.Min();
        _inFlightId = head;
        _inFlightDeadlineMs = _clockMs() + BudgetMs;
        return _waiting[head];
    }

    /// <summary>True when the in-flight order has exceeded its budget (host polls per frame to catch timeouts mid-window).</summary>
    public bool InFlightExpired => _inFlightId is not null && _clockMs() >= _inFlightDeadlineMs;

    /// <summary>
    /// Abandons the in-flight order (timeout / tier switch): removes it and
    /// returns the expired entry for the L1-fallback notice (放弃本轮). The
    /// next head starts on the host's NEXT window probe — never auto-chained.
    /// </summary>
    public L2QueuedFill? AbandonInFlight()
    {
        if (_inFlightId is not { } id || !_waiting.TryGetValue(id, out L2QueuedFill? fill))
        {
            return null;
        }

        _waiting.Remove(id);
        _inFlightId = null;
        return fill;
    }

    private void ExpireInFlight()
    {
        if (InFlightExpired)
        {
            _waiting.Remove(_inFlightId!.Value);
            _inFlightId = null;
        }
    }

    /// <summary>
    /// Completes the in-flight order — ONLY when <paramref name="orderId"/>
    /// is still the in-flight one (a late answer for an already-abandoned
    /// order must never complete the NEXT order — id-checked by design).
    /// Returns the landing; null when stale (abandoned earlier) or mismatched.
    /// </summary>
    public L2FillCompletion? Complete(long orderId, L2IntentFillResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_inFlightId is not { } id || id != orderId || !_waiting.TryGetValue(id, out L2QueuedFill? fill))
        {
            return null; // stale completion (already abandoned / different order)
        }

        _waiting.Remove(id);
        _inFlightId = null;
        return new L2FillCompletion(fill, result);
    }

    /// <summary>Drops every queued order (tier switched off / force-close). Returns how many were dropped.</summary>
    public int Clear()
    {
        int dropped = _waiting.Count;
        _waiting.Clear();
        _inFlightId = null;
        return dropped;
    }
}
