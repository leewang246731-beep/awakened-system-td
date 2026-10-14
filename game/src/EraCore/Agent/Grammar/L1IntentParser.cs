using System.Text;
using System.Text.RegularExpressions;
using EraCore.Contracts.Intents;

namespace EraCore.Agent.Grammar;

// C-15 L1 military-order grammar. T-052 shipped the E1 high-frequency subset
// (守住X/建造/敌情查询/储备查询/设闹 — the union of the task card and SP-2's
// calibration line); T-069 completes the frozen 12-intent table (SP-2) and
// adds the multimodal pointing slots (v3.2 §11.2/§11.5).
//
// Pipeline: strip a trailing @x,y coordinate suffix → note a trailing
// question mark (interrogative) → normalize (TextNormalizer) → tower
// homophone repair (AliasDictionary.FuzzyRepair, guarded against adjacent
// protected words) → anchored template regex match in file order (zh book
// then en book) → slot canonicalization to language-neutral ids (tower
// registry id / region id / enemy id / mode / lane / scroll / Arabic digits /
// seconds) → pointing-marker resolution (PointingContext or the @x,y suffix)
// → Intent. No match → ParseFailure with a clarifying key; the C-13 pipeline
// turns that into the clarification card plus the quick wheel built from
// SupportedIntents (SP-2 配套口径: 意图层失败=澄清卡+快捷轮盘).
//
// Intent-to-action wiring stays layered: parsing an intent does not unlock
// its action (SP-2 配套口径「意图可解析≠动作可执行」) — E1-locked kinds
// (巡逻…) parse fine here and get ERR_CAPABILITY_LOCKED at the action gate.

/// <summary>
/// The offline L1 grammar parser (v3.2 §11.5 L1 军令语法): data-driven
/// pattern books + lexicon + content-registry tower/enemy aliases, compiled
/// once at construction. Parsing is pure — same text (and pointing context)
/// ⇒ same result, no hidden state, no I/O (K-4 IIntentParser contract).
/// </summary>
public sealed class L1IntentParser : IIntentParser
{
    /// <summary>Clarifying key when nothing matched: 「这句我还没学会…」card.</summary>
    public const string DefaultClarifyingKey = "agent.clarify.unparsed";

    /// <summary>
    /// Slot marker values for multimodal pointing (v3.2 §11.2): the pattern
    /// matched a demonstrative (这个/这里/…), the slot carries this marker
    /// until <see cref="Parse(string, PointingContext?)"/> resolves it against
    /// the pointing context or an @x,y suffix. Unresolvable ⇒ clarification.
    /// </summary>
    public const string PointingSelectedMarker = "pointing:selected";
    public const string PointingHereMarker = "pointing:here";

    /// <summary>
    /// Every frozen intent kind (SP-2 12-intent table; quick-wheel source for
    /// the clarification card, SP-2 配套口径).
    /// </summary>
    public static IReadOnlyList<IntentKind> SupportedIntents { get; } =
        Enum.GetValues<IntentKind>();

    /// <summary>Generic tower words inside the tower-target slot (SP-2 #3/#4 名/分区/序号).</summary>
    private static readonly string[] ZhGenericTowerWords = { "旧塔", "新塔", "塔" };
    private static readonly string[] EnGenericTowerWords = { "oldtowers", "newtowers", "oldtower", "newtower", "towers", "tower" };
    private static readonly string[] EnLinkers = { "onthe", "atthe", "inthe" };
    private static readonly string[] AllTowerMarkers =
        { "全部的塔", "所有的塔", "全部塔", "所有塔", "alltowers", "alltower", "everything", "全部", "所有", "all" };
    private static readonly string[] ZhDemonstratives = { "这个", "那个" };
    private static readonly string[] EnDemonstratives = { "this", "that" };
    private static readonly string[] ZhGenericEnemyWords = { "敌人", "敌" };
    private static readonly string[] EnGenericEnemyWords = { "theenemy", "enemies", "enemy" };
    private static readonly string[] ZhSideWords = { "左", "右", "前", "后" };
    private static readonly string[] EnSideWords = { "left", "right", "front", "back" };
    private static readonly string[] AllTaskMarkers =
        { "所有卷轴", "全部卷轴", "allscrolls", "everyscroll", "scrolls", "卷轴", "所有", "全部", "all" };

    private static readonly Regex CoordinateSuffix = new(
        @"[@＠]\s*(\d{1,3})\s*[,，、]\s*(\d{1,3})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly List<CompiledPattern> _patterns = new();
    private readonly AliasDictionary _towers;
    private readonly AliasDictionary _enemies;
    private readonly Dictionary<string, string> _regionById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _scopeById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _itemById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enemyById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modeById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _laneById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _scrollById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pointingById = new(StringComparer.Ordinal);
    private readonly string _regionAlternation;
    private readonly string _scopeAlternation;
    private readonly string _itemAlternation;
    private readonly string _modeAlternation;
    private readonly string _laneAlternation;
    private readonly string _scrollAlternation;
    private readonly string _pointingAlternation;
    private readonly string _regionWithPointingAlternation;
    private readonly string _towerTargetAlternation;
    private readonly string _enemyTargetAlternation;
    private readonly string _tierAlternation;
    private readonly string _taskAlternation;
    private readonly IReadOnlyList<string> _protectedSurfaces;

    /// <param name="zh">Chinese pattern book (matched first).</param>
    /// <param name="en">English pattern book (matched second).</param>
    /// <param name="lexicon">Region/resource/scope/mode/lane/scroll dictionary.</param>
    /// <param name="towers">Tower alias dictionary from the content registry.</param>
    /// <param name="enemies">
    /// Enemy alias dictionary from the content registry (era1 enemies.json);
    /// null ⇒ enemies come from the lexicon extras alone.
    /// </param>
    public L1IntentParser(
        GrammarBook zh,
        GrammarBook en,
        GrammarLexicon lexicon,
        AliasDictionary towers,
        AliasDictionary? enemies = null)
    {
        ArgumentNullException.ThrowIfNull(zh);
        ArgumentNullException.ThrowIfNull(en);
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentNullException.ThrowIfNull(towers);

        foreach (var book in new[] { zh, en })
        {
            foreach (var pattern in book.Patterns)
            {
                if (!Enum.IsDefined(pattern.Intent))
                {
                    throw new ArgumentException(
                        $"Pattern '{pattern.Id}' targets undefined intent {pattern.Intent}.");
                }
            }
        }

        _towers = towers;
        _enemies = enemies ?? AliasDictionary.FromRows(Array.Empty<(string, IEnumerable<string>)>());

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
        BuildSurfaceMap(lexicon.FocusModes, _modeById, nameof(lexicon.FocusModes));
        BuildSurfaceMap(lexicon.Lanes, _laneById, nameof(lexicon.Lanes));
        BuildSurfaceMap(lexicon.Scrolls, _scrollById, nameof(lexicon.Scrolls));
        BuildSurfaceMap(lexicon.Pointing, _pointingById, nameof(lexicon.Pointing));
        if (_regionById.Count == 0 || _scopeById.Count == 0 || _itemById.Count == 0
            || _modeById.Count == 0 || _laneById.Count == 0 || _scrollById.Count == 0
            || _pointingById.Count == 0)
        {
            throw new ArgumentException(
                "Lexicon regions/scopes/resources/modes/lanes/scrolls/pointing must all be non-empty.", nameof(lexicon));
        }

        foreach (var surface in lexicon.AllItemsSurfaces)
        {
            var normalized = TextNormalizer.Normalize(surface);
            if (normalized.Length == 0 || _itemById.ContainsKey(normalized))
            {
                continue;
            }

            _itemById[normalized] = "all";
        }

        // Enemy surface map: registry aliases win, lexicon extras fill gaps
        // (extras never override the registry, K-2 same-source discipline).
        foreach (var (surface, enemyId) in _enemies.Entries)
        {
            if (_enemyById.TryGetValue(surface, out var owner) && owner != enemyId)
            {
                throw new ArgumentException(
                    $"Enemy surface '{surface}' maps to both '{owner}' and '{enemyId}' (conflicts surface).", nameof(enemies));
            }

            _enemyById[surface] = enemyId;
        }

        var enemyExtras = new Dictionary<string, string>(StringComparer.Ordinal);
        BuildSurfaceMap(lexicon.Enemies, enemyExtras, nameof(lexicon.Enemies));
        foreach (var (surface, enemyId) in enemyExtras)
        {
            _enemyById.TryAdd(surface, enemyId);
        }

        if (_enemyById.Count == 0)
        {
            throw new ArgumentException("Enemy vocabulary is empty — FocusFire patterns cannot compile.", nameof(lexicon));
        }

        // Protected-word list for FuzzyRepair: every closed-vocabulary word a
        // tower-surface rewrite could destroy — lexicon surfaces plus enemy
        // surfaces, the generic tower/enemy designators, the all-markers and
        // the side words (T-061 §5 root cause: the guard must cover adjacent
        // dictionary words, not just tower surfaces).
        _protectedSurfaces = lexicon.AllProtectedSurfaces()
            .Concat(_enemyById.Keys)
            .Concat(ZhGenericEnemyWords)
            .Concat(EnGenericEnemyWords)
            .Concat(ZhGenericTowerWords)
            .Concat(EnGenericTowerWords)
            .Concat(AllTowerMarkers)
            .Concat(AllTaskMarkers)
            .Concat(ZhSideWords.Select(side => side + "边的敌人"))
            .Where(s => s.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _regionAlternation = BuildAlternation(_regionById.Keys);
        _scopeAlternation = BuildAlternation(_scopeById.Keys);
        _itemAlternation = BuildAlternation(_itemById.Keys);
        _modeAlternation = BuildAlternation(_modeById.Keys);
        _laneAlternation = BuildAlternation(_laneById.Keys);
        _scrollAlternation = BuildAlternation(_scrollById.Keys);
        _pointingAlternation = BuildAlternation(_pointingById.Keys);
        _regionWithPointingAlternation = Join(_regionAlternation, _pointingAlternation);
        _towerTargetAlternation = BuildTowerTargetAlternation();
        _enemyTargetAlternation = BuildEnemyTargetAlternation();
        _tierAlternation = "(?:第?[1-4一二三四]级|tier[1-4]|level[1-4])";
        _taskAlternation = Join(BuildAlternation(AllTaskMarkers), _scrollAlternation);

        foreach (var book in new[] { zh, en })
        {
            foreach (var pattern in book.Patterns)
            {
                _patterns.Add(Compile(pattern));
            }
        }
    }

    /// <summary>Convenience factory from the content file payloads.</summary>
    public static L1IntentParser FromJson(
        string patternsZhJson, string patternsEnJson, string lexiconJson, string towersJson, string? enemiesJson = null)
        => new(
            GrammarData.FromPatterns(patternsZhJson, "zh"),
            GrammarData.FromPatterns(patternsEnJson, "en"),
            GrammarData.FromLexicon(lexiconJson),
            AliasDictionary.FromTowerJson(towersJson),
            enemiesJson is null ? null : AliasDictionary.FromTowerJson(enemiesJson));

    /// <inheritdoc />
    /// <remarks>
    /// Never throws on unparseable orders — those return
    /// <see cref="IntentParseResult.Failure"/>. Null/empty input also routes
    /// to failure (documented defensive stance; the contract's「failure never
    /// throws」beats argument checking here).
    /// </remarks>
    public IntentParseResult Parse(string text) => Parse(text, null);

    /// <summary>
    /// Parses one order against a multimodal pointing context (v3.2 §11.2:
    /// 多模态指认补槽). Demonstrative slots (这个/这里/…) resolve through the
    /// context; a trailing <c>@x,y</c> coordinate suffix on the raw order
    /// resolves (and overrides) them without needing any context. Same text
    /// and same context ⇒ same result (K-4 purity).
    /// </summary>
    public IntentParseResult Parse(string text, PointingContext? pointing)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Fail(text ?? string.Empty, DefaultClarifyingKey);
        }

        var body = text;
        string? coordinate = null;
        var suffixMatch = CoordinateSuffix.Match(body);
        if (suffixMatch.Success)
        {
            coordinate = suffixMatch.Groups[1].Value + "," + suffixMatch.Groups[2].Value;
            body = body[..suffixMatch.Index];
        }

        // Trailing question mark = interrogative intonation; it enables the
        // bare-word query reading ("wood?" → 储备查询) but never changes what
        // an explicit template matches.
        var interrogative = false;
        body = body.TrimEnd();
        while (body.Length > 0 && (body[^1] == '?' || body[^1] == '？'))
        {
            interrogative = true;
            body = body[..^1].TrimEnd();
        }

        var normalized = TextNormalizer.Normalize(body);
        if (normalized.Length == 0)
        {
            return Fail(text, DefaultClarifyingKey);
        }

        // Two-pass matching (T-069): patterns run on the verbatim input
        // FIRST; the homophone repair only gets a turn when nothing matched
        // directly. A repair can therefore never corrupt an order the
        // grammar already understands (「集火敌人」is a valid FocusFire order
        // even though its 集火 verb sits one substitution away from the tower
        // surface 篝火 — the T-061 §5 guard family generalizes here).
        var matched = TryMatchPatterns(normalized, text, pointing, coordinate);
        if (matched is not null)
        {
            return matched;
        }

        var repaired = _towers.FuzzyRepair(normalized, _protectedSurfaces);
        if (repaired is not null && repaired != normalized)
        {
            matched = TryMatchPatterns(repaired, text, pointing, coordinate);
            if (matched is not null)
            {
                return matched;
            }
        }

        // Bare-word interrogative (T-061 miss #258): "wood?" — the whole order
        // is exactly one dictionary surface plus question intonation. Query
        // intents only (储备查询/敌情查询); a bare noun without the question
        // mark ("木材") stays a clarification, matching the zh corpus negative.
        if (interrogative)
        {
            if (_itemById.TryGetValue(normalized, out var itemId))
            {
                return new IntentParseResult(
                    new Intent(IntentKind.QueryStockpile, text, SingleSlot("item", itemId)), null);
            }

            if (_scopeById.TryGetValue(normalized, out var scopeId))
            {
                return new IntentParseResult(
                    new Intent(IntentKind.QueryThreat, text, SingleSlot("scope", scopeId)), null);
            }
        }

        return Fail(text, DefaultClarifyingKey);
    }

    /// <summary>
    /// The pattern-matching pass: first anchored template whose slots all
    /// canonicalize wins. Returns null when nothing matched; a matched
    /// pattern with an unresolvable pointing slot returns the clarification
    /// failure (never falls through to guessing).
    /// </summary>
    private IntentParseResult? TryMatchPatterns(
        string probe, string rawText, PointingContext? pointing, string? coordinate)
    {
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

            var resolved = ResolvePointingAndCoordinate(slots, pointing, coordinate);
            if (resolved is null)
            {
                // The intent matched but a pointing slot has nothing to point
                // at — clarification card, never a guess (SP-2 配套口径).
                return Fail(rawText, DefaultClarifyingKey);
            }

            return new IntentParseResult(new Intent(compiled.Intent, rawText, resolved), null);
        }

        return null;
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

    private static Dictionary<string, string> SingleSlot(string slot, string value)
        => new(StringComparer.Ordinal) { [slot] = value };

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

    private static string Join(params string[] alternations)
        => string.Join("|", alternations.Where(a => a.Length > 0));

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
        "after" or "duration" => ChineseNumber.DurationAlternation(),
        "reason" => ".+?",
        "target" => intent switch
        {
            IntentKind.HoldRegion => _regionWithPointingAlternation,
            IntentKind.FocusFire => _enemyTargetAlternation,
            IntentKind.Upgrade or IntentKind.Sell => _towerTargetAlternation,
            _ => throw InvalidSlot(intent, slot),
        },
        "target_towers" => _towerTargetAlternation,
        "where" => _regionWithPointingAlternation,
        "from" or "to" => _regionAlternation,
        "scope" => _scopeAlternation,
        "item" => _itemAlternation,
        "to_tier" => _tierAlternation,
        "mode" => _modeAlternation,
        "lane" => _laneAlternation,
        "scroll" => _scrollAlternation,
        "task" => _taskAlternation,
        _ => throw InvalidSlot(intent, slot),
    };

    private static ArgumentException InvalidSlot(IntentKind intent, string slot)
        => new($"No alternation for slot '{slot}' of {intent}.");

    // ---- tower-target vocabulary (SP-2 #3/#4: 名/分区/序号) -------------------
    //
    // Canonical value scheme (language-neutral, one string per slot):
    //   {tower_id}                      — bare named tower ("篝火"/"campfires")
    //   {tower_id}x{qty}                — named + quantity ("两座箭塔"/"both rockfalls")
    //   {region_id}+any                 — the (unspecified) towers of a region ("北墙的塔"/"all towers on the north wall")
    //   {region_id}+{tower_id}[x{qty}]  — region-qualified tower ("东门的两座箭塔"/"two campfires at the east gate")
    //   all                             — every tower ("全部的塔"/"everything")
    //   ord:{n}                         — ordinal designation ("3号塔"/"tower 3")
    //   pointing marker                 — demonstrative, resolved against PointingContext/@x,y
    // Adjectives (旧/新/old/new) narrow selection at the action layer and are
    // dropped at parse level.

    private string BuildTowerTargetAlternation()
    {
        var tower = _towers.TowerAlternation();
        var qty = ChineseNumber.QuantityAlternation();
        var region = _regionAlternation;
        var all = BuildAlternation(AllTowerMarkers);
        var genericZh = BuildAlternation(ZhGenericTowerWords);
        var linker = BuildAlternation(EnLinkers);
        var det = "(?:the|old|new|this|that|旧|新|这个|那个)";
        var ordinal = "(?:\\d{1,2}号塔|thetowernumber\\d{1,2}|towernumber\\d{1,2}|tower\\d{1,2})";

        return Join(
            // 东门的两座箭塔 / 东门的两箭塔 / 东门的箭塔
            $"(?:{region})的(?:{qty})?(?:{tower})",
            // 北墙的旧塔 / 北墙的塔 / 东门塔
            $"(?:{region})(?:的)?(?:{genericZh})",
            // 东门箭塔 (no particle)
            $"(?:{region})(?:的)?(?:{tower})",
            // all towers on the north wall / everything on the west side / all
            $"(?:{all})(?:{linker})?(?:{region})?",
            // (the) (old/new) towers on the north wall
            $"(?:the)?(?:old|new)?towers?(?:{linker})(?:{region})",
            // two campfires at the east gate / that campfire / 篝火 / 两座箭塔
            $"(?:{det})?(?:{qty})?(?:{tower})(?:{linker})?(?:{region})?",
            // ordinal designation
            ordinal,
            // demonstratives (multimodal)
            _pointingAlternation);
    }

    private bool TryCanonicalizeTowerTarget(string raw, out string value)
    {
        value = string.Empty;

        if (_pointingById.TryGetValue(raw, out var pointing))
        {
            value = pointing;
            return true;
        }

        if (TryOrdinal(raw, "号塔", "tower", out var ordinal))
        {
            value = ordinal;
            return true;
        }

        // English linker split: "<left> on/at/in the <region>"
        foreach (var linker in EnLinkers)
        {
            var at = raw.IndexOf(linker, StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            var left = raw[..at];
            var regionSurface = raw[(at + linker.Length)..];
            if (!_regionById.TryGetValue(regionSurface, out var regionId))
            {
                return false;
            }

            left = left == "the" ? string.Empty : left;
            if (left.Length == 0
                || AllTowerMarkers.Contains(left, StringComparer.Ordinal)
                || EnGenericTowerWords.Contains(left, StringComparer.Ordinal))
            {
                value = regionId + "+any";
                return true;
            }

            if (TryCanonicalizeTowerTarget(left, out var inner)
                && IsBareTowerValue(inner))
            {
                value = regionId + "+" + inner;
                return true;
            }

            return false;
        }

        // Chinese particle split: "<region>的<right>" (unknown left side falls
        // through to the later checks —「全部的塔」carries no region)
        var de = raw.IndexOf('的');
        if (de > 0 && _regionById.TryGetValue(raw[..de], out var particleRegionId))
        {
            var right = raw[(de + 1)..];
            if (right.Length == 0 || ZhGenericTowerWords.Contains(right, StringComparer.Ordinal))
            {
                value = particleRegionId + "+any";
                return true;
            }

            if (TryCanonicalizeTowerTarget(right, out var inner) && IsBareTowerValue(inner))
            {
                value = particleRegionId + "+" + inner;
                return true;
            }

            return false;
        }

        // Region + tower concatenated without particle: 东门箭塔
        foreach (var (surface, concatRegionId) in _regionById.OrderByDescending(k => k.Key.Length))
        {
            if (raw.Length > surface.Length
                && raw.StartsWith(surface, StringComparison.Ordinal)
                && TryCanonicalizeTowerTarget(raw[surface.Length..], out var inner)
                && IsBareTowerValue(inner))
            {
                value = concatRegionId + "+" + inner;
                return true;
            }
        }

        if (AllTowerMarkers.Contains(raw, StringComparer.Ordinal))
        {
            value = "all";
            return true;
        }

        if (ZhGenericTowerWords.Contains(raw, StringComparer.Ordinal) || EnGenericTowerWords.Contains(raw, StringComparer.Ordinal))
        {
            // A generic tower word with no region and no demonstrative
            // designates nothing — clarification, never a guess.
            return false;
        }

        var stripped = StripDeterminers(raw);
        if (_towers.TryResolve(stripped, out var towerId))
        {
            value = towerId;
            return true;
        }

        // Quantity prefix + tower suffix in one capture: 两座箭塔 / bothrockfalls
        for (var cut = 1; cut < stripped.Length; cut++)
        {
            if (ChineseNumber.TryParseQuantity(stripped[..cut]) is { } quantity
                && _towers.TryResolve(stripped[cut..], out towerId))
            {
                value = towerId + "x"
                    + quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
        }

        return false;
    }

    /// <summary>True for a canonical value usable inside a region-qualified pair (tower id or tower id × qty).</summary>
    private static bool IsBareTowerValue(string value)
        => value.Length > 0 && !value.Contains('+') && value != "all"
           && !value.StartsWith("ord:", StringComparison.Ordinal)
           && !value.StartsWith("pointing:", StringComparison.Ordinal);

    /// <summary>Strips leading determiners/adjectives (这个/那个/the/old/new/旧/新) from a bare tower mention.</summary>
    private static string StripDeterminers(string raw)
    {
        var words = EnDemonstratives.Concat(ZhDemonstratives).Append("the").Append("old").Append("new")
            .OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).ToList();
        var current = raw;
        bool changed = true;
        while (changed)
        {
            changed = false;
            if (current.StartsWith('旧') || current.StartsWith('新'))
            {
                current = current[1..];
                changed = true;
                continue;
            }

            foreach (var word in words)
            {
                if (current.Length > word.Length && current.StartsWith(word, StringComparison.Ordinal))
                {
                    current = current[word.Length..];
                    changed = true;
                    break;
                }
            }
        }

        return current;
    }

    // ---- enemy-target vocabulary (SP-2 #5: 头顶序号/类型名/方位) -------------

    private string BuildEnemyTargetAlternation()
    {
        var enemy = string.Join("|", _enemyById.Keys
            .OrderByDescending(s => s.Length)
            .ThenBy(s => s, StringComparer.Ordinal)
            .Select(s => Regex.Escape(s) + "(?:es|s)?"));
        var region = _regionAlternation;
        var genericZh = BuildAlternation(ZhGenericEnemyWords);
        var genericEn = BuildAlternation(EnGenericEnemyWords);
        var sideZh = string.Join("|", ZhSideWords.Select(side => "(?:" + Regex.Escape(side) + "边?的?敌人)"));
        var sideEn = string.Join("|", EnSideWords.Select(side => "(?:the)?" + Regex.Escape(side) + "(?:side)?enemies"));
        var ordinal = "(?:\\d{1,2}号敌人|theenemynumber\\d{1,2}|enemynumber\\d{1,2}|enemy\\d{1,2})";

        return Join(
            // 东门的敌人 (enemies of a region → the region id, SP-2 #5 方位)
            $"(?:{region})的(?:{genericZh})",
            // bare region as enemy selector: focus the enemies at the {east gate}
            region,
            // 敌人 / enemies — generic
            genericZh,
            genericEn,
            // 左边的敌人 / enemies on the left
            sideZh,
            sideEn,
            // 3号敌人 / enemy 3
            ordinal,
            // named enemy types (registry + lexicon extras), plurals included
            enemy,
            _pointingAlternation);
    }

    private bool TryCanonicalizeEnemyTarget(string raw, out string value)
    {
        value = string.Empty;

        if (_pointingById.TryGetValue(raw, out var pointing))
        {
            value = pointing;
            return true;
        }

        if (TryOrdinal(raw, "号敌人", "enemy", out var ordinal))
        {
            value = ordinal;
            return true;
        }

        if (ZhGenericEnemyWords.Contains(raw, StringComparer.Ordinal) || EnGenericEnemyWords.Contains(raw, StringComparer.Ordinal))
        {
            value = "any";
            return true;
        }

        foreach (var side in ZhSideWords)
        {
            if (IsSideSurface(raw, side, "边的敌人") || IsSideSurface(raw, side, "边敌人")
                || IsSideSurface(raw, side, "的敌人") || IsSideSurface(raw, side, "敌人"))
            {
                value = "side:" + SideId(side);
                return true;
            }
        }

        foreach (var side in EnSideWords)
        {
            if (IsSideSurface(raw, side, "sideenemies") || IsSideSurface(raw, side, "enemies")
                || IsSideSurface("the" + raw, side, "sideenemies") || IsSideSurface("the" + raw, side, "enemies"))
            {
                value = "side:" + side;
                return true;
            }
        }

        var de = raw.IndexOf('的');
        if (de > 0
            && _regionById.TryGetValue(raw[..de], out var regionId)
            && (raw.Length == de + 1 || ZhGenericEnemyWords.Contains(raw[(de + 1)..], StringComparer.Ordinal)))
        {
            value = regionId;
            return true;
        }

        if (_regionById.TryGetValue(raw, out var bareRegion))
        {
            value = bareRegion;
            return true;
        }

        var stripped = StripDeterminers(raw);
        if (_enemyById.TryGetValue(stripped, out var enemyId))
        {
            value = enemyId;
            return true;
        }

        if (stripped.EndsWith("es", StringComparison.Ordinal) && _enemyById.TryGetValue(stripped[..^2], out enemyId))
        {
            value = enemyId;
            return true;
        }

        if (stripped.EndsWith('s') && _enemyById.TryGetValue(stripped[..^1], out enemyId))
        {
            value = enemyId;
            return true;
        }

        return false;
    }

    private static bool IsSideSurface(string raw, string side, string tail)
        => raw == side + tail;

    /// <summary>ZH side char → language-neutral id (左→left, 右→right, 前→front, 后→back).</summary>
    private static string SideId(string zhSide)
        => zhSide switch
        {
            "左" => "left",
            "右" => "right",
            "前" => "front",
            "后" => "back",
            _ => zhSide,
        };

    private static bool TryOrdinal(string raw, string zhSuffix, string stem, out string value)
    {
        value = string.Empty;
        if (raw.EndsWith(zhSuffix, StringComparison.Ordinal)
            && raw[..^zhSuffix.Length] is { Length: > 0 } zhNumber
            && zhNumber.All(char.IsAsciiDigit))
        {
            value = "ord:" + zhNumber;
            return true;
        }

        foreach (var prefix in new[] { "the" + stem + "number", stem + "number", stem })
        {
            if (raw.StartsWith(prefix, StringComparison.Ordinal)
                && raw[prefix.Length..] is { Length: > 0 } digits
                && digits.All(char.IsAsciiDigit))
            {
                value = "ord:" + digits;
                return true;
            }
        }

        return false;
    }

    // ---- pointing & coordinate resolution -----------------------------------

    /// <summary>
    /// Resolves pointing markers through the context (or the @x,y suffix) and
    /// applies the coordinate override. Null return = a marker had nothing to
    /// resolve against ⇒ the caller answers with the clarification card.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ResolvePointingAndCoordinate(
        Dictionary<string, string> slots,
        PointingContext? pointing,
        string? coordinate)
    {
        var result = new Dictionary<string, string>(slots, StringComparer.Ordinal);

        foreach (var (slot, val) in slots)
        {
            if (val != PointingSelectedMarker && val != PointingHereMarker)
            {
                continue;
            }

            string? cell;
            if (coordinate is not null)
            {
                cell = "cell:" + coordinate;
            }
            else if (val == PointingSelectedMarker)
            {
                cell = pointing?.ResolveSelected();
            }
            else
            {
                cell = pointing?.ResolveCell();
            }

            if (cell is null)
            {
                return null;
            }

            result[slot] = cell;
        }

        // Explicit @x,y suffix overrides a matched spatial slot even without
        // a demonstrative: the coordinate is the most specific pointer the
        // commander can give.
        if (coordinate is not null)
        {
            if (result.ContainsKey("where"))
            {
                result["where"] = "cell:" + coordinate;
            }
            else if (result.ContainsKey("target"))
            {
                result["target"] = "cell:" + coordinate;
            }
        }

        return result;
    }

    // ---- slot canonicalization ------------------------------------------------

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
            case "after" or "duration":
                if (ChineseNumber.TryParseDuration(raw) is { } seconds)
                {
                    value = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }

                break;
            case "to_tier":
                value = CanonicalTier(raw);
                return value.Length > 0;
            case "target":
                return intent switch
                {
                    IntentKind.HoldRegion => TryCanonicalizeRegionWithPointing(raw, out value),
                    IntentKind.FocusFire => TryCanonicalizeEnemyTarget(raw, out value),
                    IntentKind.Upgrade or IntentKind.Sell => TryCanonicalizeTowerTarget(raw, out value),
                    _ => throw InvalidSlot(intent, slot),
                };
            case "target_towers":
                return TryCanonicalizeTowerTarget(raw, out value);
            case "where":
                return TryCanonicalizeRegionWithPointing(raw, out value);
            case "from" or "to":
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
            case "mode":
                if (_modeById.TryGetValue(raw, out var modeId))
                {
                    value = modeId;
                    return true;
                }

                break;
            case "lane":
                if (_laneById.TryGetValue(raw, out var laneId))
                {
                    value = laneId;
                    return true;
                }

                break;
            case "scroll":
                if (_scrollById.TryGetValue(raw, out var scrollId))
                {
                    value = scrollId;
                    return true;
                }

                break;
            case "task":
                if (AllTaskMarkers.Contains(raw, StringComparer.Ordinal))
                {
                    value = "all";
                    return true;
                }

                if (_scrollById.TryGetValue(raw, out var taskScrollId))
                {
                    value = taskScrollId;
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
                throw InvalidSlot(intent, slot);
        }

        value = string.Empty;
        return false;
    }

    private bool TryCanonicalizeRegionWithPointing(string raw, out string value)
    {
        if (_regionById.TryGetValue(raw, out var regionId))
        {
            value = regionId;
            return true;
        }

        if (_pointingById.TryGetValue(raw, out var pointing))
        {
            value = pointing;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Tier surface (三级/2级/tier3/第4级/level2) → 1..4 digit.</summary>
    private static string CanonicalTier(string raw)
    {
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 1)
        {
            return digits;
        }

        return raw.FirstOrDefault(c => c is '一' or '二' or '三' or '四') switch
        {
            '一' => "1",
            '二' => "2",
            '三' => "3",
            '四' => "4",
            _ => string.Empty,
        };
    }

    private sealed record CompiledPattern(
        IntentKind Intent,
        IReadOnlyDictionary<string, string>? ConstantSlots,
        Regex Regex,
        string[] SlotNames);
}
