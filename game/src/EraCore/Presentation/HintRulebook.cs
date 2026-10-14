using System.Text.Json;

namespace EraCore.Presentation;

/// <summary>
/// The observable fact kinds that drive predicate hints (T-093, F-110
/// 谓词式提示: 行为触发). One enum value = one thing the game can observe
/// about what the player just did or what just happened; the hint library
/// maps each kind to at most a few lines (hints.json). Adding a kind is a
/// content+code pair change by design (§10.1 触发规则在附录 G.3).
/// </summary>
public enum HintPredicateKind
{
    /// <summary>The session mounted (first frames) — opening guidance.</summary>
    GameStart = 0,

    /// <summary>The first production building appeared (harvester/converter).</summary>
    FirstProduction = 1,

    /// <summary>The first weapon tower appeared.</summary>
    FirstTower = 2,

    /// <summary>The wave counter left 0 (first assault inbound).</summary>
    WaveIncoming = 3,

    /// <summary>An advisor clarification card fired (order not parsed).</summary>
    ClarifyCard = 4,

    /// <summary>A supply bottleneck event fired (因果型提示 trigger).</summary>
    FirstBottleneck = 5,

    /// <summary>Defense integrity fell under the §10.3 warning line (因果型提示 trigger).</summary>
    LowIntegrity = 6,

    /// <summary>The camera came near a named zone (分区标注联动).</summary>
    CameraNearZone = 7,
}

/// <summary>
/// One hint row of hints.json (T-093, F-110): predicate + priority +
/// cooldown + once + bilingual copy + optional glossary term link
/// (提示与帮助词条互链).
/// </summary>
/// <param name="Id">Stable hint id (<c>hint-first-tower</c>).</param>
/// <param name="Predicate">The observable fact that arms this hint.</param>
/// <param name="Priority">Display order inside the queue — smaller fires first (≥ 1).</param>
/// <param name="CooldownTicks">冷静期 in 30 Hz ticks: the earliest this hint may re-arm after showing (0 = no cooldown).</param>
/// <param name="Once">True = a read hint never fires again (persistence through <see cref="HintProgressStore"/>).</param>
/// <param name="TextZh">Chinese hint copy.</param>
/// <param name="TextEn">English hint copy.</param>
/// <param name="Term">Glossary entry id linked from the hint (null = no link).</param>
public sealed record HintRule(
    string Id,
    HintPredicateKind Predicate,
    int Priority,
    long CooldownTicks,
    bool Once,
    string TextZh,
    string TextEn,
    string? Term);

/// <summary>
/// The parsed hint library (hints.json, RL7). Strict and total: wrong
/// content_type, unknown predicate names, duplicate ids, non-positive
/// priorities, negative cooldowns, blank copy or a term link that does
/// not resolve against the loaded glossary all throw — the 互链 contract
/// (every hint term opens a real entry) is enforced at load, not at click.
/// </summary>
public sealed class HintRulebook
{
    private const string ExpectedContentType = "HintTable";

    /// <summary>30 Hz tick domain (K-1 IClock) — seconds-to-ticks conversion base.</summary>
    public const int TicksPerSecond = 30;

    private readonly List<HintRule> _rules;

    private HintRulebook(List<HintRule> rules) => _rules = rules;

    /// <summary>All rules in file order.</summary>
    public IReadOnlyList<HintRule> Rules => _rules;

    /// <summary>Rule count (smoke probe).</summary>
    public int Count => _rules.Count;

    /// <summary>
    /// Parses one hints.json document; <paramref name="glossary"/> validates
    /// the term links.
    /// </summary>
    /// <param name="json">Document text.</param>
    /// <param name="sourceName">Label for error messages.</param>
    /// <param name="glossary">The loaded glossary every <c>term</c> must resolve against.</param>
    /// <exception cref="ArgumentException">Structure or value violations.</exception>
    public static HintRulebook Parse(string json, string sourceName, Glossary glossary)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(glossary);
        HintDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, UiContentJsonContext.Default.HintDocument);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{sourceName}: invalid JSON ({ex.Message}).", nameof(json));
        }

        if (document is null)
        {
            throw new ArgumentException($"{sourceName}: deserialized to null.", nameof(json));
        }

        if (document.ContentType != ExpectedContentType)
        {
            throw new ArgumentException(
                $"{sourceName}: content_type must be '{ExpectedContentType}', got '{document.ContentType}'.");
        }

        HintEntryJson[]? items = document.Items;
        if (items is not { Length: > 0 })
        {
            throw new ArgumentException($"{sourceName}: items must be a non-empty array.");
        }

        var rules = new List<HintRule>(items.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (HintEntryJson item in items)
        {
            string id = item.Id ?? string.Empty;
            if (id.Trim().Length == 0)
            {
                throw new ArgumentException($"{sourceName}: items[].id must be a non-blank string.");
            }

            if (!seen.Add(id))
            {
                throw new ArgumentException($"{sourceName}: duplicate hint id '{id}'.");
            }

            if (!Enum.TryParse(item.Predicate, ignoreCase: false, out HintPredicateKind predicate))
            {
                throw new ArgumentException(
                    $"{sourceName}: items[{id}].predicate '{item.Predicate}' is not a HintPredicateKind name.");
            }

            if (item.Priority is not int priority || priority < 1)
            {
                throw new ArgumentException($"{sourceName}: items[{id}].priority must be >= 1.");
            }

            double cooldownSeconds = item.CooldownS ?? 0d;
            if (cooldownSeconds < 0d)
            {
                throw new ArgumentException($"{sourceName}: items[{id}].cooldown_s must be >= 0.");
            }

            string textZh = item.TextZh ?? string.Empty;
            string textEn = item.TextEn ?? string.Empty;
            if (textZh.Trim().Length == 0 || textEn.Trim().Length == 0)
            {
                throw new ArgumentException($"{sourceName}: items[{id}] text_zh/text_en must be non-blank (双语交付).");
            }

            if (item.Term is { } term && !glossary.TryGet(term, out _))
            {
                throw new ArgumentException(
                    $"{sourceName}: items[{id}].term '{term}' does not resolve in the glossary.");
            }

            rules.Add(new HintRule(
                id, predicate, priority,
                CooldownTicks: (long)Math.Round(cooldownSeconds * TicksPerSecond),
                Once: item.Once,
                textZh, textEn, item.Term));
        }

        return new HintRulebook(rules);
    }
}

/// <summary>Root shape of hints.json: content_type + items.</summary>
internal sealed record HintDocument(string? ContentType, HintEntryJson[]? Items);

/// <summary>Raw item shape of hints.json.</summary>
internal sealed record HintEntryJson(
    string? Id,
    string? Predicate,
    int? Priority,
    double? CooldownS,
    bool Once,
    string? TextZh,
    string? TextEn,
    string? Term);
