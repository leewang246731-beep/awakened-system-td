using EraCore.Agent.Governance;

namespace EraCore.Presentation;

/// <summary>
/// Three-tier trust badge mapping (T-075, F-111/§11.4): one frozen source
/// for the panel-side tier of a raw trust score, reusing the
/// <see cref="TrustModel"/> boundary constants so presenter and model can
/// never drift apart. Tier of a score: &lt; <see cref="TrustModel.SkepticalBelow"/>
/// = <see cref="TrustBadge.Skeptical"/>, &lt; <see cref="TrustModel.DevotedAtLeast"/>
/// = <see cref="TrustBadge.Trustworthy"/>, else <see cref="TrustBadge.Devoted"/>.
/// </summary>
public static class TrustTiers
{
    /// <summary>Tier of a raw 0..100 trust score (same thresholds as <see cref="TrustModel.Badge"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Score outside [0, 100].</exception>
    public static TrustBadge BadgeOf(int score)
    {
        if (score < TrustModel.MinScore || score > TrustModel.MaxScore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(score), score, $"trust score must be within [{TrustModel.MinScore},{TrustModel.MaxScore}].");
        }

        return score < TrustModel.SkepticalBelow
            ? TrustBadge.Skeptical
            : score < TrustModel.DevotedAtLeast ? TrustBadge.Trustworthy : TrustBadge.Devoted;
    }
}

/// <summary>
/// Display labels of the three badge tiers (§11.4 信任三档徽记呈现:
/// 不露裸数字 — the score itself is never shown, only these tier marks).
/// </summary>
public static class TrustBadgeLabels
{
    /// <summary>Full tier label rendered next to the advisor avatar (将信将疑/可托付/言听计从).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown badge value.</exception>
    public static string Label(TrustBadge badge) => badge switch
    {
        TrustBadge.Skeptical => "将信将疑",
        TrustBadge.Trustworthy => "可托付",
        TrustBadge.Devoted => "言听计从",
        _ => throw new ArgumentOutOfRangeException(nameof(badge), badge, "unknown trust badge tier."),
    };

    /// <summary>One-glyph corner mark for the avatar badge (角标: 疑/托/从).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown badge value.</exception>
    public static string Mark(TrustBadge badge) => badge switch
    {
        TrustBadge.Skeptical => "疑",
        TrustBadge.Trustworthy => "托",
        TrustBadge.Devoted => "从",
        _ => throw new ArgumentOutOfRangeException(nameof(badge), badge, "unknown trust badge tier."),
    };
}

/// <summary>
/// One observed tier transition of the trust badge (§11.4 档位变化军师自嘲台词).
/// </summary>
/// <param name="From">Tier before the observation.</param>
/// <param name="To">Tier after (different by construction).</param>
/// <param name="Upward">true when <paramref name="To"/> ranks above <paramref name="From"/>.</param>
/// <param name="Line">The advisor self-deprecating line (L1 template fill, no LLM).</param>
public sealed record TrustBadgeChange(TrustBadge From, TrustBadge To, bool Upward, string Line);

/// <summary>
/// The six tier-change advisor lines (T-075 task card: 三档×升降 6 条台词模板,
/// L1 档模板填充 — deterministic templates, never a model call): keyed by the
/// tier LEFT and the direction. The two structural extremes (leaving Devoted
/// upward / leaving Skeptical downward) have lines too, so the table is the
/// full 3×2 the card freezes even where the trust domain makes them
/// unreachable (no tier above 言听计从, none below 将信将疑).
/// </summary>
public static class TrustBadgeLines
{
    /// <summary>
    /// The self-deprecating line for one tier transition, chosen by the tier
    /// the badge LEFT and the direction it moved (跨档一步或两步同向同线).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="from"/> equals <paramref name="to"/>.</exception>
    public static string Line(TrustBadge from, TrustBadge to)
    {
        if (from == to)
        {
            throw new ArgumentException($"no transition line within one tier ({from}).");
        }

        bool upward = to > from;
        return (from, upward) switch
        {
            (TrustBadge.Skeptical, true) => "宿主近来用兵如神，此前多疑，是老夫眼拙了。",
            (TrustBadge.Trustworthy, true) => "宿主算无遗策，这军师的名分，老夫坐得越发心虚。",
            (TrustBadge.Devoted, true) => "言听计从之上再无档位，老夫唯有焚香告庙、静候军令。",
            (TrustBadge.Devoted, false) => "方才误报，老夫惭愧。",
            (TrustBadge.Trustworthy, false) => "这几道判断接连走眼，宿主且慢信我。",
            (TrustBadge.Skeptical, false) => "猜疑至此仍是老夫之过，惭愧惭愧。",
            _ => throw new ArgumentOutOfRangeException(nameof(from), from, "unknown trust badge tier."),
        };
    }
}

/// <summary>
/// Stateful presenter-side tracker of the badge tier (T-075): feed it trust
/// scores (from the live <see cref="TrustModel"/>, which stays the single
/// mutator of the score); it remembers the last tier and emits a
/// <see cref="TrustBadgeChange"/> only when the tier actually changed. Pure
/// decision logic, no engine types (D-01).
/// </summary>
public sealed class TrustBadgeTracker
{
    private TrustBadge _current;

    /// <param name="initial">Tier the run starts at (era reset = 可托付 at score 50).</param>
    public TrustBadgeTracker(TrustBadge initial) => _current = initial;

    /// <summary>Tier of the last observed score.</summary>
    public TrustBadge Current => _current;

    /// <summary>
    /// Observes a new score; returns the transition when the tier changed
    /// (with the 6-line template filled), null when it did not.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Score outside [0, 100].</exception>
    public TrustBadgeChange? Observe(int score)
    {
        TrustBadge tier = TrustTiers.BadgeOf(score);
        if (tier == _current)
        {
            return null;
        }

        TrustBadge from = _current;
        _current = tier;
        return new TrustBadgeChange(from, tier, tier > from, TrustBadgeLines.Line(from, tier));
    }
}
