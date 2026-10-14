using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Intents;

namespace EraCore.Agent.Grammar;

/// <summary>
/// Slot vocabulary per intent kind, frozen by SP-2 (T-008, 12-intent table).
/// Pattern templates may only reference the slots of their intent; constants
/// (<c>slots</c> map) must stay inside the same vocabulary and disjoint from
/// the template's slot markers.
/// </summary>
public static class GrammarSlots
{
    private static readonly IReadOnlyDictionary<IntentKind, string[]> Slots = new Dictionary<IntentKind, string[]>
    {
        [IntentKind.HoldRegion] = new[] { "target" },
        [IntentKind.Build] = new[] { "tower", "qty", "where" },
        [IntentKind.Upgrade] = new[] { "target", "to_tier" },
        [IntentKind.Sell] = new[] { "target" },
        [IntentKind.FocusFire] = new[] { "target" },
        [IntentKind.ChangeFocusMode] = new[] { "mode", "target_towers" },
        [IntentKind.RerouteLogistics] = new[] { "from", "to", "item", "rate" },
        [IntentKind.QueryStockpile] = new[] { "item" },
        [IntentKind.QueryThreat] = new[] { "scope" },
        [IntentKind.SetAlarm] = new[] { "after", "reason" },
        [IntentKind.Patrol] = new[] { "lane", "duration" },
        [IntentKind.ScrollControl] = new[] { "scroll", "task" },
    };

    /// <summary>All slot names of one intent kind (SP-2 slot column).</summary>
    public static IReadOnlyList<string> Allowed(IntentKind kind)
        => Slots.TryGetValue(kind, out var slots) ? slots : Array.Empty<string>();

    /// <summary>True when <paramref name="slot"/> is legal for <paramref name="kind"/>.</summary>
    public static bool IsAllowed(IntentKind kind, string slot)
        => Allowed(kind).Contains(slot);
}

/// <summary>One chunk of a parsed template: literal text or a slot marker.</summary>
/// <param name="IsSlot">True when this chunk is a <c>{slot}</c> slot marker.</param>
/// <param name="Text">Literal text (raw, not normalized) or the slot name.</param>
public sealed record TemplatePart(bool IsSlot, string Text);

/// <summary>
/// One sentence pattern of the L1 grammar, loaded from
/// <c>game/content/grammar/patterns-{lang}.json</c> (T-052). The template is
/// data (v3.2 §11.5「≥120 句式变体」 ships as content, not code); the parser
/// compiles it into an anchored regex over the normalized input.
/// </summary>
/// <param name="Id">Stable pattern id within its book (e.g. <c>build-zh-01</c>).</param>
/// <param name="Intent">Intent kind this pattern produces.</param>
/// <param name="Template">Raw template text, literals + <c>{slot}</c> slot markers.</param>
/// <param name="Parts">Template split into literal/slot chunks, in order.</param>
/// <param name="SlotNames">Slot names in template order (may repeat).</param>
/// <param name="ConstantSlots">
/// Slot values fixed by the pattern itself (e.g. the literal sentence「下一波怕什么」
/// implies <c>scope=next_wave</c>); validated disjoint from slot markers.
/// </param>
public sealed record GrammarPattern(
    string Id,
    IntentKind Intent,
    string Template,
    IReadOnlyList<TemplatePart> Parts,
    IReadOnlyList<string> SlotNames,
    IReadOnlyDictionary<string, string>? ConstantSlots);

/// <summary>One language's pattern book (zh or en); match order = file order.</summary>
/// <param name="Lang">Language tag ("zh" / "en").</param>
/// <param name="Patterns">Patterns in file order (priority order).</param>
public sealed record GrammarBook(string Lang, IReadOnlyList<GrammarPattern> Patterns);

/// <summary>One named lexicon entry: a language-neutral id plus its surfaces.</summary>
/// <param name="Id">Language-neutral id (region id, resource id, scope id).</param>
/// <param name="Surfaces">Surface strings (zh and en mixed); normalized at compile.</param>
public sealed record LexiconEntry(string Id, IReadOnlyList<string> Surfaces);

/// <summary>
/// The L1 grammar dictionary (<c>game/content/grammar/lexicon.json</c>).
/// T-069 grows the vocabulary from the T-052 five-intent subset to the full
/// 12-intent table (SP-2): regions (守住X/建造 where), resources (储备查询,
/// 物流改道 item), threat scopes (敌情查询), enemy designations (集火 —
/// registry ids come from <c>game/content/era1/enemies.json</c>, this section
/// carries the extras it does not cover yet), focus modes (改集火模式), lanes
/// (巡逻), scrolls (卷轴触发+停止) and the multimodal pointing placeholders
/// (这里/这个 → resolved against <see cref="PointingContext"/>, v3.2 §11.2).
/// </summary>
/// <param name="Regions">Region/gate/wall surfaces → region id (SP-2 #1/#2).</param>
/// <param name="Resources">Resource surfaces → era resource id (SP-2 #8/#7).</param>
/// <param name="ThreatScopes">Threat scope surfaces → scope id (SP-2 #9).</param>
/// <param name="AllItemsSurfaces">Surfaces meaning "every resource" → the <c>all</c> item id.</param>
/// <param name="Enemies">Extra enemy surfaces → enemy id; merged over the era registry (SP-2 #5).</param>
/// <param name="FocusModes">Focus-mode surfaces → first/last/strong/closest (SP-2 #6).</param>
/// <param name="Lanes">Lane surfaces → lane id (SP-2 #11).</param>
/// <param name="Scrolls">Scroll surfaces → scroll id, plus the generic「卷轴/any」entry (SP-2 #12).</param>
/// <param name="Pointing">Multimodal pointing placeholders → pointing:selected / pointing:here.</param>
public sealed record GrammarLexicon(
    IReadOnlyList<LexiconEntry> Regions,
    IReadOnlyList<LexiconEntry> Resources,
    IReadOnlyList<LexiconEntry> ThreatScopes,
    IReadOnlyList<string> AllItemsSurfaces,
    IReadOnlyList<LexiconEntry> Enemies,
    IReadOnlyList<LexiconEntry> FocusModes,
    IReadOnlyList<LexiconEntry> Lanes,
    IReadOnlyList<LexiconEntry> Scrolls,
    IReadOnlyList<LexiconEntry> Pointing)
{
    /// <summary>Every lexicon surface (all sections), normalized — the FuzzyRepair protected-word list.</summary>
    public IEnumerable<string> AllProtectedSurfaces()
    {
        IEnumerable<string> Of(IEnumerable<LexiconEntry> entries)
        {
            foreach (var entry in entries)
            {
                foreach (var surface in entry.Surfaces)
                {
                    yield return TextNormalizer.Normalize(surface);
                }
            }
        }

        foreach (var surface in Of(Regions).Concat(Of(Resources)).Concat(Of(ThreatScopes))
                     .Concat(Of(Enemies)).Concat(Of(FocusModes)).Concat(Of(Lanes))
                     .Concat(Of(Scrolls)).Concat(Of(Pointing)))
        {
            yield return surface;
        }

        foreach (var surface in AllItemsSurfaces)
        {
            yield return TextNormalizer.Normalize(surface);
        }
    }
}

/// <summary>
/// Loader and validator for the grammar content files (T-052). Damaged data
/// throws <see cref="JsonException"/> at load time — content validation is
/// strict, mirroring the TowerSpec content stance.
/// </summary>
public static class GrammarData
{
    /// <summary>
    /// Loads one pattern book. <paramref name="expectedLang"/> guards wiring
    /// mistakes (the zh slot must receive the zh file). Validates: non-empty
    /// pattern list, unique ids, intents inside the frozen 12-kind enum,
    /// slot names legal for their intent (SP-2), no slot marker adjacent to an
    /// open-ended slot, at least one literal char per template, and constant
    /// slots disjoint from slot markers.
    /// </summary>
    public static GrammarBook FromPatterns(string json, string expectedLang)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(expectedLang);

        var document = JsonSerializer.Deserialize(json, GrammarDataJsonContext.Default.GrammarBookDocument)
            ?? throw new JsonException("Grammar book deserialized to null.");
        if (string.IsNullOrEmpty(document.Lang) || document.Lang != expectedLang)
        {
            throw new JsonException($"Grammar book lang '{document.Lang}' does not match expected '{expectedLang}'.");
        }

        var defs = document.Patterns ?? Array.Empty<GrammarPatternDef>();
        if (defs.Count == 0)
        {
            throw new JsonException($"Grammar book '{expectedLang}' carries no patterns.");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var patterns = new List<GrammarPattern>(defs.Count);
        foreach (var def in defs)
        {
            if (string.IsNullOrEmpty(def.Id) || !seenIds.Add(def.Id))
            {
                throw new JsonException($"Pattern id missing or duplicated: '{def.Id}' (book '{expectedLang}').");
            }

            if (string.IsNullOrEmpty(def.Template) || !Enum.TryParse<IntentKind>(def.Intent, out var kind))
            {
                throw new JsonException($"Pattern '{def.Id}' has no template or unknown intent '{def.Intent}'.");
            }

            var parts = ParseTemplate(def.Id, def.Template, kind, out var slotNames);
            var literalChars = parts.Where(p => !p.IsSlot).Sum(p => TextNormalizer.Normalize(p.Text).Length);
            if (literalChars == 0)
            {
                throw new JsonException($"Pattern '{def.Id}' has no literal text — a bare placeholder would over-match.");
            }

            var constants = ValidateConstants(def.Id, kind, def.Slots, slotNames);
            patterns.Add(new GrammarPattern(def.Id, kind, def.Template!, parts, slotNames, constants));
        }

        return new GrammarBook(expectedLang, patterns);
    }

    /// <summary>
    /// Loads the grammar lexicon. Validates: at least one region (HoldRegion/
    /// Build need it), resources, threat scopes, enemy extras, focus modes,
    /// lanes, scrolls and pointing placeholders all non-empty (every section
    /// feeds at least one 12-intent alternation, T-069), every entry carrying
    /// an id and at least one non-empty surface.
    /// </summary>
    public static GrammarLexicon FromLexicon(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var document = JsonSerializer.Deserialize(json, GrammarDataJsonContext.Default.GrammarLexiconDocument)
            ?? throw new JsonException("Grammar lexicon deserialized to null.");
        var regions = ValidateEntries(document.Regions, "regions", requireNonEmpty: true);
        var resources = ValidateEntries(document.Resources, "resources", requireNonEmpty: true);
        var scopes = ValidateEntries(document.ThreatScopes, "threat_scopes", requireNonEmpty: true);
        var enemies = ValidateEntries(document.Enemies, "enemies", requireNonEmpty: true);
        var modes = ValidateEntries(document.FocusModes, "focus_modes", requireNonEmpty: true);
        var lanes = ValidateEntries(document.Lanes, "lanes", requireNonEmpty: true);
        var scrolls = ValidateEntries(document.Scrolls, "scrolls", requireNonEmpty: true);
        var pointing = ValidateEntries(document.Pointing, "pointing", requireNonEmpty: true);

        return new GrammarLexicon(
            regions,
            resources,
            scopes,
            document.AllItemsSurfaces ?? Array.Empty<string>(),
            enemies,
            modes,
            lanes,
            scrolls,
            pointing);
    }

    private static IReadOnlyList<LexiconEntry> ValidateEntries(
        IReadOnlyList<LexiconEntryDef>? entries, string section, bool requireNonEmpty)
    {
        if (entries is null || entries.Count == 0)
        {
            if (requireNonEmpty)
            {
                throw new JsonException($"Grammar lexicon section '{section}' is empty — the subset grammar cannot compile.");
            }

            return Array.Empty<LexiconEntry>();
        }

        var result = new List<LexiconEntry>(entries.Count);
        foreach (var def in entries)
        {
            if (string.IsNullOrEmpty(def.Id))
            {
                throw new JsonException($"Lexicon entry without id in section '{section}'.");
            }

            var surfaces = (def.Surfaces ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (surfaces.Count == 0)
            {
                throw new JsonException($"Lexicon entry '{def.Id}' has no surfaces.");
            }

            result.Add(new LexiconEntry(def.Id, surfaces));
        }

        return result;
    }

    private static IReadOnlyList<TemplatePart> ParseTemplate(
        string id, string template, IntentKind kind, out List<string> slotNames)
    {
        var parts = new List<TemplatePart>();
        slotNames = new List<string>();
        var literal = new System.Text.StringBuilder();
        var previousWasSlot = false;
        var previousSlotName = string.Empty;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new JsonException($"Pattern '{id}' has an unclosed '{{'.");
                }

                var name = template[(i + 1)..close];
                if (string.IsNullOrWhiteSpace(name) || !GrammarSlots.IsAllowed(kind, name))
                {
                    throw new JsonException($"Pattern '{id}' references slot '{name}' which is not legal for {kind} (SP-2).");
                }

                if (previousWasSlot && (IsOpenEndedSlot(previousSlotName) || IsOpenEndedSlot(name)))
                {
                    throw new JsonException(
                        $"Pattern '{id}' places a placeholder next to an open-ended slot — the boundary would be ambiguous.");
                }

                FlushLiteral();
                parts.Add(new TemplatePart(true, name));
                slotNames.Add(name);
                previousWasSlot = true;
                previousSlotName = name;
                i = close;
            }
            else if (c == '}')
            {
                throw new JsonException($"Pattern '{id}' has a stray '}}'.");
            }
            else
            {
                literal.Append(c);
                if (!char.IsWhiteSpace(c))
                {
                    // Punctuation-only literals normalize away, so they cannot
                    // separate two slot markers — the adjacency guard stays
                    // armed only while the char itself will be dropped.
                    previousWasSlot = previousWasSlot && IsPunctuationOnly(c);
                }
            }
        }

        FlushLiteral();
        return parts;

        void FlushLiteral()
        {
            if (literal.Length == 0)
            {
                return;
            }

            parts.Add(new TemplatePart(false, literal.ToString()));
            literal.Clear();
            previousWasSlot = false;
        }
    }

    /// <summary>
    /// True for characters normalization will drop (non-alnum, non-CJK).
    /// </summary>
    private static bool IsPunctuationOnly(char c)
        => !char.IsAsciiLetterOrDigit(c)
           && !('\u4E00' <= c && c <= '\u9FFF')
           && !('\u3400' <= c && c <= '\u4DBF');

    /// <summary>
    /// Slots with open-ended captures (free text). Adjacent slot markers are
    /// only ambiguous when an open-ended slot is involved — closed-vocabulary
    /// slots (tower/qty/where/…) resolve their own boundary.
    /// </summary>
    private static bool IsOpenEndedSlot(string slot)
        => slot == "reason";

    private static IReadOnlyDictionary<string, string>? ValidateConstants(
        string id, IntentKind kind, IReadOnlyDictionary<string, string>? constants, List<string> slotNames)
    {
        if (constants is null || constants.Count == 0)
        {
            return null;
        }

        foreach (var (slot, value) in constants)
        {
            if (!GrammarSlots.IsAllowed(kind, slot))
            {
                throw new JsonException($"Pattern '{id}' constant slot '{slot}' is not legal for {kind} (SP-2).");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException($"Pattern '{id}' constant slot '{slot}' has an empty value.");
            }

            if (slotNames.Contains(slot))
            {
                throw new JsonException($"Pattern '{id}' constant slot '{slot}' collides with its own placeholder.");
            }
        }

        return constants;
    }
}

// ---- JSON DTOs + source-generation context (AOT red line D-05) ------------

/// <summary>Root shape of a patterns file: lang + patterns[] (snake_case).</summary>
public sealed record GrammarBookDocument(string? Lang, IReadOnlyList<GrammarPatternDef>? Patterns);

/// <summary>Raw pattern row before validation.</summary>
public sealed record GrammarPatternDef(
    string? Id,
    string? Intent,
    string? Template,
    IReadOnlyDictionary<string, string>? Slots);

/// <summary>Root shape of lexicon.json.</summary>
public sealed record GrammarLexiconDocument(
    IReadOnlyList<LexiconEntryDef>? Regions,
    IReadOnlyList<LexiconEntryDef>? Resources,
    IReadOnlyList<LexiconEntryDef>? ThreatScopes,
    IReadOnlyList<string>? AllItemsSurfaces,
    IReadOnlyList<LexiconEntryDef>? Enemies,
    IReadOnlyList<LexiconEntryDef>? FocusModes,
    IReadOnlyList<LexiconEntryDef>? Lanes,
    IReadOnlyList<LexiconEntryDef>? Scrolls,
    IReadOnlyList<LexiconEntryDef>? Pointing);

/// <summary>Raw lexicon entry before validation.</summary>
public sealed record LexiconEntryDef(string? Id, IReadOnlyList<string>? Surfaces);

/// <summary>
/// System.Text.Json source-generation context for the grammar content (AOT
/// red line, 07 D-05). Kept in this file as a secondary type so its coverage
/// rides on GrammarDataTests (T2 file mapping).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GrammarBookDocument))]
[JsonSerializable(typeof(GrammarLexiconDocument))]
public sealed partial class GrammarDataJsonContext : JsonSerializerContext;
