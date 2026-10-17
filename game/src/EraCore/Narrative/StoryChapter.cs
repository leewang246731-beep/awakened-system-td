using System.Text.Json;
using EraCore.Agent.Governance;

namespace EraCore.Narrative;

/// <summary>
/// Schema violation of one story chapter row (T-089): the parser throws
/// this with a field-pathed message — same discipline as
/// <c>CinematicSchemaException</c> / <c>HandcraftedLevelSchemaException</c>
/// (content defects surface as load failures, never as silently-zeroed rows).
/// </summary>
public sealed class NarrativeSchemaException : Exception
{
    public NarrativeSchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>Act trigger kinds (closed set): wave start / bound cinematic fired / victory.</summary>
public enum ActPredicateKind
{
    /// <summary>第 N 波开场（黄金三章受辱/打脸位）。</summary>
    WaveStart = 0,

    /// <summary>某场演出已触发（E1 绑定幕复用 cinematic:e1-aha）。</summary>
    CinematicFired = 1,

    /// <summary>关卡胜利（交底/终幕位）。</summary>
    Victory = 2,
}

/// <summary>
/// One act's trigger predicate: <see cref="ActPredicateKind.WaveStart"/>
/// carries the wave ordinal, <see cref="ActPredicateKind.CinematicFired"/>
/// the bound script id, <see cref="ActPredicateKind.Victory"/> neither.
/// </summary>
public sealed record ActPredicate(ActPredicateKind Kind, int? Wave, string? ScriptId)
{
    /// <summary>True when this predicate is satisfied by the observed beat sample.</summary>
    public bool Matches(ActPredicateKind beatKind, int wave, string? scriptId) => Kind switch
    {
        ActPredicateKind.WaveStart => beatKind == ActPredicateKind.WaveStart && Wave == wave,
        ActPredicateKind.CinematicFired => beatKind == ActPredicateKind.CinematicFired
            && string.Equals(ScriptId, scriptId, StringComparison.Ordinal),
        ActPredicateKind.Victory => beatKind == ActPredicateKind.Victory,
        _ => false,
    };
}

/// <summary>One 幕 of the golden three acts: predicate + its dialogue lines.</summary>
public sealed record StoryAct(
    string Id,
    string TitleZh,
    ActPredicate Predicate,
    IReadOnlyList<DialogueLine> Lines);

/// <summary>章末卡点预告 (F-102): next-era elite silhouette + one threat line.</summary>
/// <param name="NextEra">Next era content id (null when the era is not shipped yet).</param>
/// <param name="EliteId">Next-chapter elite enemy id (null when not shipped; text-only tease).</param>
/// <param name="SilhouetteZh">新精英剪影 one-liner.</param>
/// <param name="ThreatLineZh">一句威胁台词。</param>
public sealed record ChapterTease(
    string? NextEra,
    string? EliteId,
    string SilhouetteZh,
    string ThreatLineZh);

/// <summary>章末：胜利交底台词 + 卡点预告（挂 victory beat）。</summary>
public sealed record ChapterEnding(
    string HookId,
    IReadOnlyList<DialogueLine> Lines,
    ChapterTease Tease);

/// <summary>打脸/扮猪关结构 (F-100/F-101)：全部结构数值入数据（RL7），运行时零关卡数值。</summary>
public enum MockArcKind
{
    /// <summary>打脸关：NPC 嘲讽→低配通关→震惊演出+额外奖励。</summary>
    FaceSlap = 0,

    /// <summary>扮猪吃虎关：强制劣势开局（资源压制）→隐藏翻盘。</summary>
    MockArcPig = 1,
}

/// <summary>One NPC taunt row, wave-anchored (打脸/扮猪关的嘲讽拍)。</summary>
public sealed record MockArcTaunt(int Wave, string Speaker, string Text);

/// <summary>强制劣势开局参数（扮猪吃虎）：resource stock multiplied at level assembly.</summary>
public sealed record MockArcSuppression(string Resource, double Multiplier);

/// <summary>低配判定阈值：victory with at most this many weapon towers standing.</summary>
public sealed record MockArcLowConfig(int MaxTowers);

/// <summary>隐藏评价奖励（复用 chests.json 档位与物品面——T-087 结算隐藏档的数据源）。</summary>
public sealed record HiddenReward(
    string ChestTier,
    IReadOnlyList<string> ItemIds,
    IReadOnlyList<DialogueLine> Lines);

/// <summary>One configured 打脸/扮猪 row of a level.</summary>
public sealed record MockArcRow(
    string LevelId,
    MockArcKind Kind,
    MockArcSuppression? Suppression,
    IReadOnlyList<MockArcTaunt> Taunts,
    MockArcLowConfig LowConfig,
    HiddenReward HiddenReward);

/// <summary>黄金三章配置：循环预算 + 三幕（受辱→觉醒→打脸）。</summary>
/// <param name="BudgetSeconds">§12.1 开局 15 分钟循环预算（第三幕−第一幕 ≤ 预算）。</param>
/// <param name="Acts">Exactly three acts, fired strictly in order.</param>
public sealed record GoldenActsConfig(int BudgetSeconds, IReadOnlyList<StoryAct> Acts);

/// <summary>
/// One story chapter of <c>game/content/narrative/story-{era}.json</c>
/// (T-089, F-098/F-100/F-101/F-102/F-103/F-105/F-106/F-109): opening
/// lines, trust-tiered intermission pools (persona-weighted), the golden
/// three acts, the victory ending with 卡点预告, and the configured
/// 打脸/扮猪 rows. Pure content — every wave anchor, budget and threshold
/// lives here (RL7); the runtime (<see cref="NarrativeDirector"/>) carries
/// no per-chapter values.
/// </summary>
/// <param name="Id">Chapter id (<c>narrative:story-e1</c>…).</param>
/// <param name="Era">Era content id.</param>
/// <param name="LevelId">The level the chapter binds to (golden-acts home level).</param>
/// <param name="TitleZh">Chapter title.</param>
/// <param name="GoldenActs">Three acts + loop budget.</param>
/// <param name="Opening">Opening lines (level start).</param>
/// <param name="IntermissionPools">波间军师闲聊池 per trust badge (three tiers).</param>
/// <param name="Ending">Victory handover + tease.</param>
/// <param name="MockArcs">打脸/扮猪 rows of this chapter's levels.</param>
public sealed record StoryChapter(
    string Id,
    string Era,
    string LevelId,
    string TitleZh,
    GoldenActsConfig GoldenActs,
    IReadOnlyList<DialogueLine> Opening,
    IReadOnlyDictionary<TrustBadge, IReadOnlyList<DialogueLine>> IntermissionPools,
    ChapterEnding Ending,
    IReadOnlyList<MockArcRow> MockArcs)
{
    /// <summary>Content schema URN of the story registry.</summary>
    public const string SchemaUrn = "urn:eratd:content:narrative:story:1";

    /// <summary>Content type marker the loader requires (<c>content_type</c> field).</summary>
    public const string ContentType = "StoryChapter";

    /// <summary>Closed sound-cue set (same table as the cinematic cues).</summary>
    public static readonly IReadOnlySet<string> SoundCues =
        new HashSet<string> { "ding_info", "ding_reward", "ding_crisis" };

    /// <summary>The golden acts are exactly three (受辱→觉醒→打脸, §12.1).</summary>
    public const int ActCount = 3; // ci-allow: R7 T-089 三幕=受辱→觉醒→打脸为 v3.2 §12.1 冻结结构常量（章节数纲，非内容平衡数值）

    /// <summary>The intermission pools cover all three trust badges (F.4 tiers).</summary>
    public static readonly IReadOnlyList<TrustBadge> TrustTiers =
        new[] { TrustBadge.Skeptical, TrustBadge.Trustworthy, TrustBadge.Devoted };

    /// <summary>The configured 打脸/扮猪 row of one level, when present.</summary>
    public MockArcRow? MockArcFor(string levelId) =>
        MockArcs.FirstOrDefault(row => string.Equals(row.LevelId, levelId, StringComparison.Ordinal));

    /// <summary>
    /// Strict parser (field-pathed errors, missing fields throw; keys
    /// starting with <c>_</c> are annotations and ignored — content
    /// convention).
    /// </summary>
    /// <param name="json">Raw chapter JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="NarrativeSchemaException">Any schema violation.</exception>
    public static StoryChapter Parse(string json, string sourceName = "story chapter")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new NarrativeSchemaException($"{sourceName}: 章节文件为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new NarrativeSchemaException($"{sourceName}: JSON 解析失败（{ex.Message}）");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, $"{sourceName}: 根节点须为对象");
            string contentType = String(root, "content_type", sourceName);
            Require(
                contentType == ContentType,
                $"{sourceName}: content_type 须为 {ContentType}（得到 {contentType}）");

            string id = String(root, "id", sourceName);
            Require(!id.Any(char.IsWhiteSpace), $"{sourceName}: id 含空白字符");
            string era = String(root, "era", sourceName);
            Require(!era.Any(char.IsWhiteSpace), $"{sourceName}: era 含空白字符");
            string levelId = String(root, "level_id", sourceName);
            Require(!levelId.Any(char.IsWhiteSpace), $"{sourceName}: level_id 含空白字符");
            string title = String(root, "title_zh", sourceName);
            Require(!string.IsNullOrWhiteSpace(title), $"{sourceName}: title_zh 为空白");

            // ---- golden acts（三幕+预算） ------------------------------------
            JsonElement goldenEl = Object(root, "golden_acts", sourceName);
            int budget = Int(goldenEl, "budget_seconds", sourceName);
            Require(budget > 0, $"{sourceName}: golden_acts.budget_seconds 须 >0（得到 {budget}）");
            JsonElement actsEl = Array(goldenEl, "acts", sourceName);
            Require(
                actsEl.GetArrayLength() == ActCount,
                $"{sourceName}: golden_acts.acts 须恰好 {ActCount} 幕（得到 {actsEl.GetArrayLength()}——受辱→觉醒→打脸）");
            var acts = new List<StoryAct>();
            var actIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement actEl in actsEl.EnumerateArray())
            {
                acts.Add(ParseAct(actEl, sourceName, actIds));
            }

            // ---- opening / pools / ending / mock arcs -------------------------
            IReadOnlyList<DialogueLine> opening = ParseLines(root, "opening", sourceName);
            Require(opening.Count >= 1, $"{sourceName}: opening 至少 1 行");

            JsonElement poolsEl = Object(root, "intermission_pools", sourceName);
            var pools = new Dictionary<TrustBadge, IReadOnlyList<DialogueLine>>();
            foreach (TrustBadge tier in TrustTiers)
            {
                IReadOnlyList<DialogueLine> pool = ParseLines(poolsEl, tier.PoolName(), sourceName);
                Require(pool.Count >= 1, $"{sourceName}: 闲聊池 {tier.PoolName()} 至少 1 行");
                pools[tier] = pool;
            }

            ChapterEnding ending = ParseEnding(Object(root, "ending", sourceName), sourceName);
            var mockArcs = new List<MockArcRow>();
            JsonElement arcsEl = Array(root, "mock_arcs", sourceName);
            foreach (JsonElement arcEl in arcsEl.EnumerateArray())
            {
                mockArcs.Add(ParseMockArc(arcEl, sourceName));
            }

            Require(mockArcs.Count >= 1, $"{sourceName}: mock_arcs 至少 1 行（打脸/扮猪配置化）");

            return new StoryChapter(
                id, era, levelId, title,
                new GoldenActsConfig(budget, acts),
                opening, pools, ending, mockArcs);
        }
    }

    private static StoryAct ParseAct(JsonElement el, string source, HashSet<string> actIds)
    {
        Require(el.ValueKind == JsonValueKind.Object, $"{source}: acts 行须为对象");
        string id = String(el, "id", source);
        Require(!string.IsNullOrWhiteSpace(id), $"{source}: acts.id 为空白");
        Require(actIds.Add(id), $"{source}: acts.id 重复（{id}）");
        string title = String(el, "title_zh", source);
        Require(!string.IsNullOrWhiteSpace(title), $"{source}: acts.title_zh 为空白");

        JsonElement predicateEl = Object(el, "predicate", source);
        string kindName = String(predicateEl, "kind", source);
        ActPredicate predicate = kindName switch
        {
            "wave_start" => new ActPredicate(
                ActPredicateKind.WaveStart, OptionalInt(predicateEl, "wave", 0), null),
            "cinematic_fired" => new ActPredicate(
                ActPredicateKind.CinematicFired, null, String(predicateEl, "script", source)),
            "victory" => new ActPredicate(ActPredicateKind.Victory, null, null),
            _ => throw new NarrativeSchemaException(
                $"{source}: 未登记谓词 kind（{kindName}，封闭表 wave_start/cinematic_fired/victory）"),
        };
        if (predicate.Kind == ActPredicateKind.WaveStart)
        {
            Require(
                predicate.Wave is >= 1,
                $"{source}: 幕 {id} 的 wave_start 必须带 wave ≥1");
        }

        if (predicate.Kind == ActPredicateKind.CinematicFired)
        {
            Require(!predicate.ScriptId!.Any(char.IsWhiteSpace), $"{source}: 幕 {id} 谓词 script 含空白");
        }

        IReadOnlyList<DialogueLine> lines = ParseLines(el, "lines", source);
        Require(lines.Count >= 1, $"{source}: 幕 {id} 至少 1 行台词");
        return new StoryAct(id, title, predicate, lines);
    }

    private static ChapterEnding ParseEnding(JsonElement el, string source)
    {
        string hook = String(el, "hook_id", source);
        Require(!string.IsNullOrWhiteSpace(hook), $"{source}: ending.hook_id 为空白");
        IReadOnlyList<DialogueLine> lines = ParseLines(el, "lines", source);
        Require(lines.Count >= 1, $"{source}: ending 至少 1 行");

        JsonElement teaseEl = Object(el, "tease", source);
        string? nextEra = OptionalString(teaseEl, "next_era");
        string? elite = OptionalString(teaseEl, "elite_id");
        string silhouette = String(teaseEl, "silhouette_zh", source);
        Require(!string.IsNullOrWhiteSpace(silhouette), $"{source}: tease.silhouette_zh 为空白");
        string threat = String(teaseEl, "threat_line_zh", source);
        Require(!string.IsNullOrWhiteSpace(threat), $"{source}: tease.threat_line_zh 为空白");
        return new ChapterEnding(hook, lines, new ChapterTease(nextEra, elite, silhouette, threat));
    }

    private static MockArcRow ParseMockArc(JsonElement el, string source)
    {
        Require(el.ValueKind == JsonValueKind.Object, $"{source}: mock_arcs 行须为对象");
        string levelId = String(el, "level_id", source);
        Require(!levelId.Any(char.IsWhiteSpace), $"{source}: mock_arcs.level_id 含空白");
        string kindName = String(el, "kind", source);
        MockArcKind kind = kindName switch
        {
            "face_slap" => MockArcKind.FaceSlap,
            "mock_arc_pig" => MockArcKind.MockArcPig,
            _ => throw new NarrativeSchemaException(
                $"{source}: 未登记 mock_arcs.kind（{kindName}，封闭表 face_slap/mock_arc_pig）"),
        };

        MockArcSuppression? suppression = null;
        if (el.TryGetProperty("suppression", out JsonElement suppressionEl)
            && suppressionEl.ValueKind == JsonValueKind.Object)
        {
            string resource = String(suppressionEl, "resource", source);
            Require(!resource.Any(char.IsWhiteSpace), $"{source}: suppression.resource 含空白");
            double multiplier = Double(suppressionEl, "multiplier", source);
            Require(
                multiplier is > 0 and <= 1,
                $"{source}: suppression.multiplier 须 ∈(0,1]（得到 {multiplier}——强制劣势=削弱而非增发）");
            suppression = new MockArcSuppression(resource, multiplier);
        }
        else if (kind == MockArcKind.MockArcPig)
        {
            throw new NarrativeSchemaException(
                $"{source}: mock_arc_pig 行 {levelId} 必须携带 suppression（强制劣势开局参数）");
        }

        var taunts = new List<MockArcTaunt>();
        JsonElement tauntsEl = Array(el, "taunts", source);
        foreach (JsonElement tauntEl in tauntsEl.EnumerateArray())
        {
            int wave = Int(tauntEl, "wave", source);
            Require(wave >= 1, $"{source}: taunts.wave 须 ≥1（得到 {wave}）");
            string speaker = String(tauntEl, "speaker", source);
            Require(!string.IsNullOrWhiteSpace(speaker), $"{source}: taunts.speaker 为空白");
            string text = String(tauntEl, "text", source);
            Require(!string.IsNullOrWhiteSpace(text), $"{source}: taunts.text 为空白");
            taunts.Add(new MockArcTaunt(wave, speaker, text));
        }

        Require(taunts.Count >= 1, $"{source}: mock_arcs 行 {levelId} 至少 1 条嘲讽（F-100 嘲讽拍）");

        JsonElement lowEl = Object(el, "low_config", source);
        int maxTowers = Int(lowEl, "max_towers", source);
        Require(maxTowers >= 1, $"{source}: low_config.max_towers 须 ≥1（得到 {maxTowers}）");

        JsonElement rewardEl = Object(el, "hidden_reward", source);
        string tier = String(rewardEl, "chest_tier", source);
        Require(!string.IsNullOrWhiteSpace(tier), $"{source}: hidden_reward.chest_tier 为空白");
        JsonElement itemsEl = Array(rewardEl, "item_ids", source);
        Require(itemsEl.GetArrayLength() >= 1, $"{source}: hidden_reward.item_ids 至少 1 项");
        var items = new List<string>();
        foreach (JsonElement itemEl in itemsEl.EnumerateArray())
        {
            Require(itemEl.ValueKind == JsonValueKind.String, $"{source}: item_ids 行须为字符串");
            string item = itemEl.GetString()!;
            Require(!string.IsNullOrWhiteSpace(item), $"{source}: item_ids 含空白项");
            items.Add(item);
        }

        IReadOnlyList<DialogueLine> rewardLines = ParseLines(rewardEl, "lines", source);
        Require(rewardLines.Count >= 1, $"{source}: hidden_reward 至少 1 行演出台词");

        return new MockArcRow(
            levelId, kind, suppression, taunts, new MockArcLowConfig(maxTowers),
            new HiddenReward(tier, items, rewardLines));
    }

    /// <summary>Parse one <c>lines</c> array of dialogue rows (key/speaker/text/persona/cue).</summary>
    internal static IReadOnlyList<DialogueLine> ParseLines(JsonElement parent, string name, string source)
    {
        JsonElement linesEl = Array(parent, name, source);
        var lines = new List<DialogueLine>();
        foreach (JsonElement lineEl in linesEl.EnumerateArray())
        {
            Require(lineEl.ValueKind == JsonValueKind.Object, $"{source}: {name} 行须为对象");
            string key = String(lineEl, "key", source);
            Require(!key.Any(char.IsWhiteSpace), $"{source}: {name}.key 含空白");
            string speaker = String(lineEl, "speaker", source);
            Require(!string.IsNullOrWhiteSpace(speaker), $"{source}: {name}.speaker 为空白");
            string text = String(lineEl, "text", source);
            Require(!string.IsNullOrWhiteSpace(text), $"{source}: {name}.text 为空白");

            SystemPersona? persona = null;
            if (lineEl.TryGetProperty("persona", out JsonElement personaEl)
                && personaEl.ValueKind == JsonValueKind.String)
            {
                persona = DialogueLine.ParsePersona(personaEl.GetString()!, source);
            }

            string? cue = OptionalString(lineEl, "cue");
            if (cue is not null)
            {
                Require(
                    SoundCues.Contains(cue),
                    $"{source}: {name}.cue 未登记（{cue}，封闭表 {string.Join("/", SoundCues)}）");
            }

            // T-152 配图位（31 号 §三 b 位）：可选 story-*/banner-*/hook-* 素材键——
            // 缺图自动降级纯文本（表现层职责），这里只做非空校验。
            string? image = OptionalString(lineEl, "image");
            if (image is not null)
            {
                Require(!image.Any(char.IsWhiteSpace), $"{source}: {name}.image 含空白（素材键，见 visual-assets.json）");
            }

            lines.Add(new DialogueLine(key, speaker, text, persona, cue, image));
        }

        return lines;
    }

    // ---- JSON helpers（与 CinematicScript 同约定，缺字段即异常） --------------

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new NarrativeSchemaException(message);
        }
    }

    private static string String(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new NarrativeSchemaException($"{source}: 缺字符串字段 {name}");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        Require(value.ValueKind == JsonValueKind.String, $"字段 {name} 须为字符串或 null");
        return value.GetString();
    }

    private static int Int(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int parsed))
        {
            throw new NarrativeSchemaException($"{source}: 缺整数字段 {name}");
        }

        return parsed;
    }

    private static int OptionalInt(JsonElement parent, string name, int fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed))
        {
            throw new NarrativeSchemaException($"字段 {name} 须为整数");
        }

        return parsed;
    }

    private static double Double(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double parsed))
        {
            throw new NarrativeSchemaException($"{source}: 缺数值字段 {name}");
        }

        return parsed;
    }

    private static JsonElement Object(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new NarrativeSchemaException($"{source}: 缺对象字段 {name}");
        }

        return value;
    }

    private static JsonElement Array(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new NarrativeSchemaException($"{source}: 缺数组字段 {name}");
        }

        return value;
    }
}

/// <summary>Trust-badge pool names (content-facing, closed set of three tiers).</summary>
internal static class TrustBadgeNarrativeExtensions
{
    public static string PoolName(this TrustBadge badge) =>
        badge is TrustBadge.Skeptical or TrustBadge.Trustworthy or TrustBadge.Devoted
            ? EraCore.Agent.Governance.TrustBadgeKeys.StrictKey(badge)
            : badge.ToString();
}
