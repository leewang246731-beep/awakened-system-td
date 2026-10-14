namespace EraCore.Presentation;

/// <summary>
/// A hint the queue decided is due right now (T-093, F-110 谓词式提示的
/// 出队视图): the rule plus the glossary entry its term link resolves to
/// (null when the hint carries no link) — the presenter renders the bubble
/// and an optional「术语」jump straight from this view.
/// </summary>
/// <param name="Rule">The due rule (copy, id, priority).</param>
/// <param name="TermEntry">The linked glossary entry, or null.</param>
public sealed record HintDue(HintRule Rule, GlossaryEntry? TermEntry);

/// <summary>
/// The predicate-driven hint queue (T-093, F-110 + 新玩家首 10 分钟引导,
/// §10.1/附录 G.3): the host observes facts
/// (<see cref="Observe(HintPredicateKind, long)"/>), the queue decides
/// which hint becomes due — the highest-priority (smallest number) armed
/// rule of that predicate, respecting the per-rule 冷静期 (cooldown) and
/// the persisted read set (once-hints never refire). Pure decision logic,
/// no Godot, no IO (persistence lives in <see cref="HintProgressStore"/>).
/// </summary>
public sealed class HintQueue
{
    private readonly HintRulebook _rulebook;
    private readonly IReadOnlySet<string> _read;
    private readonly Dictionary<string, long> _lastShownTick = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the queue over one rulebook with a pre-seeded read set
    /// (loaded from the progress store; an empty set = fresh player).
    /// </summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    public HintQueue(HintRulebook rulebook, IReadOnlySet<string> read)
    {
        ArgumentNullException.ThrowIfNull(rulebook);
        ArgumentNullException.ThrowIfNull(read);
        _rulebook = rulebook;
        _read = new HashSet<string>(read, StringComparer.Ordinal);
    }

    /// <summary>
    /// One observed fact. Returns the due hint for that predicate (highest
    /// priority first), or null when every matching rule is either already
    /// read, inside its cooldown window, or none exist. A returned hint
    /// records its show tick (arms the 冷静期); <c>once</c> rules
    /// additionally self-mark read on first show (附录 G.3 持久化), other
    /// rules repeat per cooldown until the player dismisses them
    /// (<see cref="MarkRead"/>, 可跳过).
    /// </summary>
    /// <param name="kind">The observed fact.</param>
    /// <param name="tick">Current 30 Hz tick (K-1 domain).</param>
    /// <exception cref="ArgumentOutOfRangeException">tick is negative.</exception>
    public HintDue? Observe(HintPredicateKind kind, long tick)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "tick must be >= 0.");
        }

        HintRule? best = null;
        foreach (HintRule rule in _rulebook.Rules)
        {
            if (rule.Predicate != kind || _read.Contains(rule.Id))
            {
                continue; // 已读（含 once 行出队即记读）永不再弹（持久化）
            }

            if (_lastShownTick.TryGetValue(rule.Id, out long last)
                && rule.CooldownTicks > 0
                && tick < last + rule.CooldownTicks)
            {
                continue; // 冷静期内——同提示不连发
            }

            if (best is null || rule.Priority < best.Priority
                || (rule.Priority == best.Priority && string.CompareOrdinal(rule.Id, best.Id) < 0))
            {
                best = rule;
            }
        }

        if (best is null)
        {
            return null;
        }

        _lastShownTick[best.Id] = tick;
        if (best.Once)
        {
            ((HashSet<string>)_read).Add(best.Id); // once 行出队即视为已读（附录 G.3 持久化）
        }

        GlossaryEntry? term = null;
        if (best.Term is { } termId && _glossary is not null && _glossary.TryGet(termId, out GlossaryEntry entry))
        {
            term = entry;
        }

        return new HintDue(best, term);
    }

    private Glossary? _glossary;

    /// <summary>
    /// Binds the glossary so <see cref="Observe"/> can resolve term links
    /// without the presenter re-reading the catalog (optional — without a
    /// binding the due view carries a null TermEntry and the presenter
    /// resolves lazily).
    /// </summary>
    /// <exception cref="ArgumentNullException">glossary is null.</exception>
    public void BindGlossary(Glossary glossary)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        _glossary = glossary;
    }

    /// <summary>
    /// Marks one hint read (玩家点「知道了」or the bubble's close): the rule
    /// stops firing permanently (any rule, once or repeatable); the caller
    /// persists the new set through <see cref="ReadIds"/> +
    /// <see cref="HintProgressStore"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a rule of this book.</exception>
    public void MarkRead(string hintId)
    {
        if (string.IsNullOrWhiteSpace(hintId))
        {
            throw new ArgumentException("hint id must be non-blank.", nameof(hintId));
        }

        if (_rulebook.Rules.All(rule => rule.Id != hintId))
        {
            throw new ArgumentException($"'{hintId}' is not a rule of this rulebook.", nameof(hintId));
        }

        ((HashSet<string>)_read).Add(hintId);
    }

    /// <summary>The current read set (persistence snapshot).</summary>
    public IReadOnlySet<string> ReadIds => _read;

    /// <summary>True when the given hint has been read.</summary>
    public bool IsRead(string hintId) => _read.Contains(hintId);
}
