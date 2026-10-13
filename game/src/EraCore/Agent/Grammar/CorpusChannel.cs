using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Intents;

namespace EraCore.Agent.Grammar;

/// <summary>
/// One corpus row (T-052 corpus channel, F-170): a military order, its
/// expected parse (intent + canonical slots) or an expected failure. Seed
/// corpus lives at <c>game/content/grammar/corpus-seed.jsonl</c>; T-061
/// grows it to ≥200 bilingual rows for the shipping first-parse-rate gate
/// (v3.2 §11.5: ≥80% per intent per language).
/// </summary>
/// <param name="Text">The raw order text exactly as a commander would type it.</param>
/// <param name="Lang">Language tag ("zh" / "en") — per-language rates are the gate.</param>
/// <param name="Intent">Expected kind for success rows; null for failure rows.</param>
/// <param name="Slots">Expected canonical slot map (null = expect no slots).</param>
/// <param name="ExpectSuccess">True = must parse; false = must fail with a clarifying key.</param>
public sealed record CorpusEntry(
    string Text,
    string Lang,
    IntentKind? Intent,
    IReadOnlyDictionary<string, string>? Slots,
    bool ExpectSuccess);

/// <summary>One evaluated row: hit/miss plus a human-readable detail for misses.</summary>
/// <param name="Line">1-based line number in the source jsonl.</param>
/// <param name="Entry">The evaluated row.</param>
/// <param name="Hit">True when the parser matched the expectation exactly.</param>
/// <param name="Detail">Miss reason (or "ok").</param>
public sealed record CorpusRowResult(int Line, CorpusEntry Entry, bool Hit, string Detail);

/// <summary>
/// First-parse-rate report over a corpus (F-170): totals, hits and the
/// overall rate, with per-language helpers. The rate is computed from real
/// parses — never a stored constant (CS-4.4).
/// </summary>
/// <param name="Total">Rows evaluated.</param>
/// <param name="Hits">Rows whose parse matched the expectation.</param>
/// <param name="Rate">Hits / Total (0 when Total is 0).</param>
/// <param name="Rows">Per-row results, in corpus order.</param>
public sealed record CorpusReport(int Total, int Hits, double Rate, IReadOnlyList<CorpusRowResult> Rows)
{
    /// <summary>Rows of one language tag ("zh" / "en").</summary>
    public int TotalForLang(string lang)
        => Rows.Count(r => string.Equals(r.Entry.Lang, lang, StringComparison.Ordinal));

    /// <summary>Hits of one language tag.</summary>
    public int HitsForLang(string lang)
        => Rows.Count(r => r.Hit && string.Equals(r.Entry.Lang, lang, StringComparison.Ordinal));

    /// <summary>First-parse rate for one language tag (0 when that tag has no rows).</summary>
    public double RateForLang(string lang)
    {
        var total = TotalForLang(lang);
        return total == 0 ? 0.0 : HitsForLang(lang) / (double)total;
    }
}

/// <summary>
/// The corpus channel (T-052): jsonl loading with strict validation plus
/// the first-parse-rate evaluator that runs any <see cref="IIntentParser"/>
/// (L1 today, L2/L3 later — all tiers are measured by the same harness).
/// Pure functions over in-memory strings; file I/O belongs to callers.
/// </summary>
public static class CorpusChannel
{
    /// <summary>
    /// Parses corpus jsonl text. Blank lines are skipped; every other line
    /// must be a complete JSON object with text, lang and expected_parse
    /// ("success"/"failure"), and success rows must carry a frozen
    /// IntentKind name. Damaged rows throw <see cref="JsonException"/>
    /// carrying the 1-based line number.
    /// </summary>
    public static IReadOnlyList<CorpusEntry> ParseJsonl(string jsonl)
    {
        ArgumentNullException.ThrowIfNull(jsonl);

        var entries = new List<CorpusEntry>();
        var line = 0;
        foreach (var raw in jsonl.Split('\n'))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            CorpusEntryDocument? document;
            try
            {
                document = JsonSerializer.Deserialize(raw, CorpusChannelJsonContext.Default.CorpusEntryDocument);
            }
            catch (JsonException exception)
            {
                throw new JsonException($"Corpus line {line} is not valid JSON: {exception.Message}", exception);
            }

            if (document is null
                || string.IsNullOrWhiteSpace(document.Text)
                || string.IsNullOrWhiteSpace(document.Lang)
                || string.IsNullOrWhiteSpace(document.ExpectedParse))
            {
                throw new JsonException($"Corpus line {line} lacks text/lang/expected_parse.");
            }

            var expectSuccess = document.ExpectedParse switch
            {
                "success" => true,
                "failure" => false,
                _ => throw new JsonException($"Corpus line {line}: expected_parse must be 'success' or 'failure'."),
            };

            IntentKind? intent = null;
            if (expectSuccess)
            {
                if (string.IsNullOrWhiteSpace(document.Intent) || !Enum.TryParse<IntentKind>(document.Intent, out var kind))
                {
                    throw new JsonException($"Corpus line {line}: success rows need a frozen IntentKind in 'intent'.");
                }

                intent = kind;
            }

            entries.Add(new CorpusEntry(document.Text, document.Lang, intent, document.Slots, expectSuccess));
        }

        return entries;
    }

    /// <summary>
    /// Runs the parser over the corpus and produces the first-parse-rate
    /// report. A row hits when: success rows parse to the exact intent kind
    /// with an identical slot map (same keys, same values, ordinal); failure
    /// rows return a non-null failure carrying a non-empty clarifying key.
    /// </summary>
    public static CorpusReport Evaluate(IIntentParser parser, IReadOnlyList<CorpusEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(entries);

        var rows = new List<CorpusRowResult>(entries.Count);
        var line = 0;
        var hits = 0;
        foreach (var entry in entries)
        {
            line++;
            var result = parser.Parse(entry.Text);
            var (hit, detail) = entry.ExpectSuccess
                ? EvaluateSuccess(entry, result)
                : EvaluateFailure(entry, result);
            if (hit)
            {
                hits++;
            }

            rows.Add(new CorpusRowResult(line, entry, hit, detail));
        }

        var rate = entries.Count == 0 ? 0.0 : hits / (double)entries.Count;
        return new CorpusReport(entries.Count, hits, rate, rows);
    }

    private static (bool Hit, string Detail) EvaluateSuccess(CorpusEntry entry, IntentParseResult result)
    {
        if (result.Intent is null)
        {
            return (false, $"expected {entry.Intent}, got failure key '{result.Failure?.ClarifyingKey}'");
        }

        if (result.Intent.Kind != entry.Intent)
        {
            return (false, $"expected {entry.Intent}, got {result.Intent.Kind}");
        }

        var expected = entry.Slots ?? new Dictionary<string, string>();
        var actual = result.Intent.Slots;
        if (expected.Count != actual.Count)
        {
            return (false, $"slot count expected {expected.Count}, got {actual.Count}");
        }

        foreach (var (slot, value) in expected)
        {
            if (!actual.TryGetValue(slot, out var actualValue))
            {
                return (false, $"missing slot '{slot}'");
            }

            if (!string.Equals(actualValue, value, StringComparison.Ordinal))
            {
                return (false, $"slot '{slot}' expected '{value}', got '{actualValue}'");
            }
        }

        return (true, "ok");
    }

    private static (bool Hit, string Detail) EvaluateFailure(CorpusEntry entry, IntentParseResult result)
    {
        if (result.Intent is not null)
        {
            return (false, $"expected failure, got {result.Intent.Kind}");
        }

        if (result.Failure is null || string.IsNullOrWhiteSpace(result.Failure.ClarifyingKey))
        {
            return (false, "failure without a clarifying key");
        }

        return (true, "ok");
    }
}

// ---- JSON DTO + source-generation context (AOT red line D-05) -------------

/// <summary>Raw corpus row shape (snake_case jsonl fields).</summary>
public sealed record CorpusEntryDocument(
    string? Text,
    string? Lang,
    string? Intent,
    IReadOnlyDictionary<string, string>? Slots,
    string? ExpectedParse);

/// <summary>
/// System.Text.Json source-generation context for the corpus channel (AOT
/// red line, 07 D-05). Kept in this file as a secondary type so its coverage
/// rides on CorpusChannelTests (T2 file mapping).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CorpusEntryDocument))]
public sealed partial class CorpusChannelJsonContext : JsonSerializerContext;
