using EraCore.Sim;

namespace EraCore.Narrative;

/// <summary>
/// The golden three acts executor (T-089, F-103, §12.1 黄金三章): drives the
/// 受辱→觉醒→打脸 loop of a chapter's home level against tick-stamped beat
/// observations and enforces the two frozen invariants:
/// <list type="bullet">
/// <item><b>Ordering</b>: acts fire strictly in declaration order — act N's
/// predicate is only evaluated once acts 1..N−1 have fired (序贯推进).</item>
/// <item><b>15-minute loop</b>: the third act fires within
/// <c>BudgetSeconds</c> of the FIRST act (完整体验『受辱→觉醒→打脸』循环).
/// The tick is always the driving host's tick count (CS-1.4 — no clock
/// reads); the same beat schedule yields the same fire ticks.</item>
/// </list>
/// Absolute-from-level-start placement is data-owned (level pacing) and
/// carries the registered T-085 pacing RED-FLAG — the narrative side
/// enforces the loop-span invariant, which is pacing-independent.
/// </summary>
public sealed class GoldenActs
{
    private readonly GoldenActsConfig _config;
    private readonly long?[] _firedTicks;

    /// <exception cref="ArgumentNullException">config is null.</exception>
    public GoldenActs(GoldenActsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _firedTicks = new long?[config.Acts.Count];
    }

    /// <summary>The chapter's act configuration (read-only projection).</summary>
    public GoldenActsConfig Config => _config;

    /// <summary>Number of acts fired so far (0..3).</summary>
    public int FiredCount => _firedTicks.Count(tick => tick is not null);

    /// <summary>True once every act has fired.</summary>
    public bool Complete => FiredCount == _config.Acts.Count;

    /// <summary>Fire tick of one act id, when it has fired (observability).</summary>
    public long? TickOf(string actId)
    {
        for (int i = 0; i < _config.Acts.Count; i++)
        {
            if (string.Equals(_config.Acts[i].Id, actId, StringComparison.Ordinal))
            {
                return _firedTicks[i];
            }
        }

        throw new ArgumentException($"未知幕 id（{actId}）", nameof(actId));
    }

    /// <summary>
    /// Loop span in seconds: third-act fire tick minus first-act fire tick
    /// over the 30 Hz tick rate. Null until BOTH ends of the loop (act 1 and
    /// act 3) have fired.
    /// </summary>
    public double? LoopSeconds
    {
        get
        {
            if (_firedTicks.Length == 0
                || _firedTicks[0] is not { } from
                || _firedTicks[^1] is not { } to)
            {
                return null;
            }

            return (to - from) / (double)Simulation.TicksPerSecond;
        }
    }

    /// <summary>
    /// True when the loop completed within the budget (§12.1 开局 15 分钟):
    /// <c>LoopSeconds ≤ BudgetSeconds</c>. False until the loop closes.
    /// </summary>
    public bool WithinBudget => LoopSeconds is { } span && span <= _config.BudgetSeconds;

    /// <summary>
    /// Offers one observed beat to the NEXT unfired act: the act fires only
    /// when its predicate matches AND every earlier act already fired.
    /// Returns the fired act, or null when the beat did not advance the
    /// sequence. A fired act never re-fires (幕单发).
    /// </summary>
    /// <param name="beatKind">The observed beat kind.</param>
    /// <param name="wave">Wave ordinal of a wave_start beat.</param>
    /// <param name="scriptId">Script id of a cinematic_fired beat.</param>
    /// <param name="tick">Host tick of the observation.</param>
    public StoryAct? Offer(ActPredicateKind beatKind, int wave, string? scriptId, long tick)
    {
        int next = NextIndex();
        if (next >= _config.Acts.Count)
        {
            return null; // already complete
        }

        StoryAct act = _config.Acts[next];
        if (!act.Predicate.Matches(beatKind, wave, scriptId))
        {
            return null;
        }

        _firedTicks[next] = tick;
        return act;
    }

    private int NextIndex()
    {
        for (int i = 0; i < _firedTicks.Length; i++)
        {
            if (_firedTicks[i] is null)
            {
                return i;
            }
        }

        return _firedTicks.Length;
    }
}
