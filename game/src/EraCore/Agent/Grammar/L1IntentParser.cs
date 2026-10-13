using System.Text;
using System.Text.RegularExpressions;
using EraCore.Contracts.Intents;

namespace EraCore.Agent.Grammar;

// C-15 L1 military-order grammar, T-052 (E1 high-frequency subset).
//
// Intent set: the union of the two T-052 definitions found in the docs —
// the task instruction (守住X/建造/敌情查询/储备查询) and SP-2's
// [NEW-原型校准] line「T-052 E1 高频 4 意图=敌情查询/储备查询/设闹/守住X」
// (11-spec-tables.md SP-2 配套口径). Five kinds ship here; the full
// 12-intent grammar is T-069, the ≥120-variant expansion T-061.
//
// Pipeline: normalize (TextNormalizer) → tower homophone repair
// (AliasDictionary.FuzzyRepair) → anchored template regex match in file
// order (zh book then en book) → slot canonicalization to language-neutral
// ids (tower registry id / region id / Arabic digits / seconds) → Intent.
// No match → ParseFailure with a clarifying key (default or subset-gap);
// the C-13 pipeline turns that into the clarification card plus the quick
// wheel built from SupportedIntents (SP-2 配套口径: 意图层失败=澄清卡+快捷轮盘).

/// <summary>
/// The offline L1 grammar parser (v3.2 §11.5 L1 军令语法): data-driven
/// pattern books + lexicon + content-registry tower aliases, compiled once
/// at construction. Parsing is pure — same text ⇒ same result, no hidden
/// state, no I/O (K-4 IIntentParser contract).
/// </summary>
public sealed class L1IntentParser : IIntentParser
{
    /// <summary>Clarifying key when nothing matched: 「这句我还没学会…」card.</summary>
    public const string DefaultClarifyingKey = "agent.clarify.unparsed";

    /// <summary>
    /// Clarifying key when the order names a known 12-intent kind outside
    /// the T-052 subset (升级/出售/集火/…): the same card, plus a hint that
    /// the full grammar lands with T-069.
    /// </summary>
    public const string SubsetGapClarifyingKey = "agent.clarify.subset_gap";

    /// <summary>
    /// The E1 high-frequency subset this parser resolves (quick-wheel source
    /// for the clarification card; SP-2 配套口径). T-069 extends to all 12.
    /// </summary>
    public static IReadOnlyList<IntentKind> SupportedIntents { get; } = new[]
    {
        IntentKind.HoldRegion,
        IntentKind.Build,
        IntentKind.QueryThreat,
        IntentKind.QueryStockpile,
        IntentKind.SetAlarm,
    };

    private readonly List<CompiledPattern> _patterns = new();
    private readonly AliasDictionary _towers;
    private readonly Dictionary<string, string> _regionById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _scopeById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemById = new(StringComparer.Ordinal);
    private readonly string _regionAlternation;
    private readonly string _scopeAlternation;
    private readonly string _itemAlternation;
    private readonly List<(IntentKind Kind, string Keyword)> _deferredKeywords = new();

    /// <param name="zh">Chinese pattern book (matched first).</param>
    /// <param name="en">English pattern book (matched second).</param>
    /// <param name="lexicon">Region/resource/scope dictionary.</param>
    /// <param name="towers">Tower alias dictionary from the content registry.</param>
    public L1IntentParser(GrammarBook zh, GrammarBook en, GrammarLexicon lexicon, AliasDictionary towers)
    {
        ArgumentNullException.ThrowIfNull(zh);
        ArgumentNullException.ThrowIfNull(en);
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentNullException.ThrowIfNull(towers);

        foreach (var book in new[] { zh, en })
        {
            foreach (var pattern in book.Patterns)
            {
                if (!SupportedIntents.Contains(pattern.Intent))
                {
                    throw new ArgumentException(
                        $"Pattern '{pattern.Id}' targets {pattern.Intent}, outside the T-052 subset (T-069 extends the set).");
                }
            }
        }

        _towers = towers;

        if (towers.Surfaces.Count == 0)
        {
            throw new ArgumentException("Tower alias dictionary is empty — Build patterns cannot compile.", nameof(towers));
        }

        BuildSurfaceMap(lexicon.Regions, _regionById, nameof(lexicon.Regions));
        BuildSurfaceMap(lexicon.ThreatScopes, _scopeById, nameof(lexicon.ThreatScopes));
        foreach (var (surface, regionId) in _regionById)
        {
            // Region threat scope: SP-2 #9「分区威胁」reuses the region id.
            _scopeById.TryAdd(surface, regionId);
        }

        BuildSurfaceMap(lexicon.Resources, _itemById, nameof(lexicon.Resources));
        if (_regionById.Count == 0 || _scopeById.Count == 0 || _itemById.Count == 0)
        {
            throw new ArgumentException("Lexicon regions/scopes/resources must all be non-empty.", nameof(lexicon));
        }

        var allSurfaces = new List<string>();
        foreach (var surface in lexicon.AllItemsSurfaces)
        {
            var normalized = TextNormalizer.Normalize(surface);
            if (normalized.Length == 0 || _itemById.ContainsKey(normalized))
            {
                continue;
            }

            _itemById[normalized] = "all";
            allSurfaces.Add(normalized);
        }

        foreach (var deferred in lexicon.DeferredIntents)
        {
            foreach (var keyword in deferred.Keywords)
            {
                _deferredKeywords.Add((deferred.Intent, keyword));
            }
        }

        _regionAlternation = BuildAlternation(_regionById.Keys);
        _scopeAlternation = BuildAlternation(_scopeById.Keys);
        _itemAlternation = BuildAlternation(_itemById.Keys);

        foreach (var book in new[] { zh, en })
        {
            foreach (var pattern in book.Patterns)
            {
                _patterns.Add(Compile(pattern));
            }
        }
    }

    /// <summary>Convenience factory from the four content file payloads.</summary>
    public static L1IntentParser FromJson(
        string patternsZhJson, string patternsEnJson, string lexiconJson, string towersJson)
        => new(
            GrammarData.FromPatterns(patternsZhJson, "zh"),
            GrammarData.FromPatterns(patternsEnJson, "en"),
            GrammarData.FromLexicon(lexiconJson),
            AliasDictionary.FromTowerJson(towersJson));

    /// <inheritdoc />
    /// <remarks>
    /// Never throws on unparseable orders — those return
    /// <see cref="IntentParseResult.Failure"/>. Null/empty input also routes
    /// to failure (documented defensive stance; the contract's「failure never
    /// throws」beats argument checking here).
    /// </remarks>
    public IntentParseResult Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Fail(text ?? string.Empty, DefaultClarifyingKey);
        }

        var normalized = TextNormalizer.Normalize(text);
        if (normalized.Length == 0)
        {
            return Fail(text, DefaultClarifyingKey);
        }

        var probe = _towers.FuzzyRepair(normalized) ?? normalized;
        foreach (var compiled in _patterns)
        {
            var match = compiled.Regex.Match(probe);
            if (!match.Success)
            {
                continue;
            }

            var slots = new Dictionary<string, string>(StringComparer.Ordinal);
            var canonical = true;
            for (var i = 0; i < compiled.SlotNames.Length; i++)
            {
                var raw = match.Groups[Group(i)].Value;
                if (!TryCanonicalize(compiled.Intent, compiled.SlotNames[i], raw, out var value))
                {
                    canonical = false;
                    break;
                }

                slots[compiled.SlotNames[i]] = value;
            }

            if (!canonical)
            {
                continue;
            }

            if (compiled.ConstantSlots is { } constants)
            {
                foreach (var (slot, value) in constants)
                {
                    slots[slot] = value;
                }
            }

            return new IntentParseResult(new Intent(compiled.Intent, text, slots), null);
        }

        foreach (var (kind, keyword) in _deferredKeywords)
        {
            if (probe.Contains(keyword, StringComparison.Ordinal))
            {
                return Fail(text, SubsetGapClarifyingKey);
            }
        }

        return Fail(text, DefaultClarifyingKey);
    }

    /// <summary>
    /// Deterministic fingerprint of one parse result (FNV-1a 64 over the
    /// canonical form: kind + raw text + slots in ordinal key order, or
    /// failure + key). Same input ⇒ same fingerprint across instances and
    /// runs — the determinism probe for regression tests.
    /// </summary>
    public static string Fingerprint(IntentParseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var canonical = new StringBuilder();
        if (result.Intent is { } intent)
        {
            canonical.Append("i:").Append(intent.Kind.ToString()).Append(':').Append(intent.RawText);
            foreach (var slot in intent.Slots.Keys.Order(StringComparer.Ordinal))
            {
                canonical.Append('|').Append(slot).Append('=').Append(intent.Slots[slot]);
            }
        }
        else if (result.Failure is { } failure)
        {
            canonical.Append("f:").Append(failure.RawText).Append(':').Append(failure.ClarifyingKey);
        }
        else
        {
            throw new ArgumentException("Result carries neither intent nor failure (K-4: exactly one is set).", nameof(result));
        }

        return Fnv1a64(canonical.ToString());
    }

    private static IntentParseResult Fail(string rawText, string key)
        => new(null, new IntentParseFailure(rawText, key));

    private static string Group(int index) => "g" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Fnv1a64(string text)
    {
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes(text))
            {
                hash = (hash ^ b) * 1099511628211UL;
            }

            return hash.ToString("x16");
        }
    }

    private static void BuildSurfaceMap(
        IReadOnlyList<LexiconEntry> entries, Dictionary<string, string> target, string section)
    {
        foreach (var entry in entries)
        {
            foreach (var raw in entry.Surfaces)
            {
                var surface = TextNormalizer.Normalize(raw);
                if (surface.Length == 0)
                {
                    continue;
                }

                if (target.TryGetValue(surface, out var owner) && owner != entry.Id)
                {
                    throw new ArgumentException(
                        $"Lexicon surface '{surface}' in {section} maps to both '{owner}' and '{entry.Id}' (conflicts surface).");
                }

                target[surface] = entry.Id;
            }
        }
    }

    private static string BuildAlternation(IEnumerable<string> surfaces)
        => string.Join("|", surfaces
            .OrderByDescending(s => s.Length)
            .ThenBy(s => s, StringComparer.Ordinal)
            .Select(Regex.Escape));

    private CompiledPattern Compile(GrammarPattern pattern)
    {
        var body = new StringBuilder("^");
        var slotNames = new List<string>();
        foreach (var part in pattern.Parts)
        {
            if (part.IsSlot)
            {
                body.Append("(?<").Append(Group(slotNames.Count)).Append(">(?:")
                    .Append(AlternationFor(pattern.Intent, part.Text)).Append("))");
                slotNames.Add(part.Text);
            }
            else
            {
                body.Append(Regex.Escape(TextNormalizer.Normalize(part.Text)));
            }
        }

        body.Append('$');
        var regex = new Regex(body.ToString(), RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
        return new CompiledPattern(pattern.Intent, pattern.ConstantSlots, regex, slotNames.ToArray());
    }

    private string AlternationFor(IntentKind intent, string slot) => slot switch
    {
        "tower" => _towers.TowerAlternation(),
        "qty" => ChineseNumber.QuantityAlternation(),
        "after" => ChineseNumber.DurationAlternation(),
        "reason" => ".+?",
        "target" or "where" => _regionAlternation,
        "scope" => _scopeAlternation,
        "item" => _itemAlternation,
        _ => throw new ArgumentException($"No alternation for slot '{slot}' of {intent}."),
    };

    private bool TryCanonicalize(IntentKind intent, string slot, string raw, out string value)
    {
        switch (slot)
        {
            case "tower":
                if (_towers.TryResolve(raw, out var towerId))
                {
                    value = towerId;
                    return true;
                }

                break;
            case "qty":
                if (ChineseNumber.TryParseQuantity(raw) is { } quantity)
                {
                    value = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }

                break;
            case "after":
                if (ChineseNumber.TryParseDuration(raw) is { } seconds)
                {
                    value = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }

                break;
            case "target" or "where":
                if (_regionById.TryGetValue(raw, out var regionId))
                {
                    value = regionId;
                    return true;
                }

                break;
            case "scope":
                if (_scopeById.TryGetValue(raw, out var scopeId))
                {
                    value = scopeId;
                    return true;
                }

                break;
            case "item":
                if (_itemById.TryGetValue(raw, out var itemId))
                {
                    value = itemId;
                    return true;
                }

                break;
            case "reason":
                if (raw.Length > 0)
                {
                    value = raw;
                    return true;
                }

                break;
            default:
                throw new InvalidOperationException($"No canonicalization for slot '{slot}' of {intent}.");
        }

        value = string.Empty;
        return false;
    }

    private sealed record CompiledPattern(
        IntentKind Intent,
        IReadOnlyDictionary<string, string>? ConstantSlots,
        Regex Regex,
        string[] SlotNames);
}
