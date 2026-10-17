using System.Text.Json;

namespace EraCore.Narrative;

/// <summary>
/// One situation face of the persona line bank (T-126, F-109): the closed
/// categorisation every persona's 50-row bank must cover — the fifteen
/// F-105 system situations persona-rewritten, chapter-bound faces
/// (opening/three acts/victory), defeat, trust-tier shifts, settlement,
/// era arrivals and the switch/off moments of the persona experience.
/// </summary>
public enum PersonaLineFace
{
    /// <summary>F-105 情境面：15 条系统模板的人格改写（槽位语法与中性库逐条对齐）。</summary>
    Sys = 0,

    /// <summary>开场白（章绑定，e1~e3）。</summary>
    Opening = 1,

    /// <summary>第一幕反应：受辱。</summary>
    ActOne = 2,

    /// <summary>第二幕反应：觉醒。</summary>
    ActTwo = 3,

    /// <summary>第三幕反应：打脸/名场面。</summary>
    ActThree = 4,

    /// <summary>胜利交底反应（章绑定，e1~e3）。</summary>
    Victory = 5,

    /// <summary>低配隐藏评价达成（打脸/扮猪关）。</summary>
    VictoryHidden = 6,

    /// <summary>失败面（失守/核心危急）。</summary>
    Defeat = 7,

    /// <summary>信任档变面（三档×两向+两极值，对齐 TrustBadgeLines 六格）。</summary>
    TrustShift = 8,

    /// <summary>结算点评（按信任档三档；宝箱点评代码面=T-087 SettlementCommentary，本面=战报总结）。</summary>
    Settlement = 9,

    /// <summary>时代到达（e2/e3/e4 入场白）。</summary>
    EraArrival = 10,

    /// <summary>人格切入自报（切换瞬间三联动的台词臂）。</summary>
    Switch = 11,

    /// <summary>关闭系统告别（降级中性旁白前最后一句人格音）。</summary>
    OffFarewell = 12,

    /// <summary>Boss 波警报。</summary>
    BossWave = 13,

    /// <summary>零漏怪清波。</summary>
    PerfectClear = 14,

    /// <summary>全线资源告罄。</summary>
    ResourceStarved = 15, // ci-allow: R7 T-126 面枚举末位序数=15（封闭 16 面枚举序号，非内容平衡数值）
}

/// <summary>One persona bank row: persona + key + face + text + cue slot.</summary>
/// <param name="Persona">The owning persona (closed set of four).</param>
/// <param name="Key">Stable content key, bank-scoped (e.g. <c>snarky.sys.detect</c>).</param>
/// <param name="Face">The situation face (closed set).</param>
/// <param name="Text">The line text (sys face keeps the neutral template's 【槽位名】 slots verbatim).</param>
/// <param name="SoundCue">Optional ding cue slot (ding_info/ding_reward/ding_crisis).</param>
public sealed record PersonaLineRow(
    SystemPersona Persona,
    string Key,
    PersonaLineFace Face,
    string Text,
    string? SoundCue)
{
    /// <summary>The row as a runtime dialogue line (persona key runs through; speaker is the system itself).</summary>
    public DialogueLine ToDialogueLine() => new(Key, PersonaLineBank.SpeakerZh, Text, Persona, SoundCue);
}

/// <summary>
/// The four-persona situation bank of
/// <c>game/content/narrative/persona-lines.json</c> (T-126, F-109 四人格
/// 全量): 4 personas × 50 rows = 200 persona lines covering every face of
/// <see cref="PersonaLineFace"/>. Persona changes WORDING ONLY — never
/// capability (§10.7/§12.1: 人格只改表达不改能力; the capability-equivalence
/// assertion is the T-126 acceptance). The sys-face rows are the persona
/// rewrites of the fifteen T-089 neutral templates (F-105): same
/// 【槽位名】 slot names per situation, L1 档用模板填充（LLM 档只换表达层）.
/// Pure content — parsing enforces the frozen volumes (50/persona, all
/// faces per persona) so a silently-trimmed bank fails at load, and the
/// closed persona/face/cue sets reject typos loudly.
/// </summary>
public sealed class PersonaLineBank
{
    /// <summary>Content schema URN of the persona line bank.</summary>
    public const string SchemaUrn = "urn:eratd:content:narrative:persona-lines:1";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentType = "PersonaLineBank";

    /// <summary>Speaker label of every bank row — the system voice 『零』 (persona = its expression layer).</summary>
    public const string SpeakerZh = "零";

    /// <summary>
    /// Frozen rows per persona: F-109 上限 ≤50 与 05-schedule 台词 200 条
    /// （4×50）目标合并取满额（G10 条数核对口径）。
    /// </summary>
    public const int RowsPerPersona = 50; // ci-allow: R7 T-126 冻结口径=F-109 ≤50 上限取满 4×50=200（§12.1；扩容走改表）

    /// <summary>The closed face list (matrix completeness parses against this).</summary>
    public static readonly IReadOnlyList<PersonaLineFace> Faces =
        (IReadOnlyList<PersonaLineFace>)Enum.GetValues<PersonaLineFace>();

    private readonly Dictionary<string, PersonaLineRow> _byKey;
    private readonly Dictionary<SystemPersona, IReadOnlyList<PersonaLineRow>> _byPersona;

    private PersonaLineBank(IReadOnlyList<PersonaLineRow> rows)
    {
        Rows = rows;
        _byKey = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        _byPersona = Enum.GetValues<SystemPersona>()
            .ToDictionary(
                persona => persona,
                persona => (IReadOnlyList<PersonaLineRow>)rows
                    .Where(row => row.Persona == persona)
                    .ToArray());
    }

    /// <summary>All 200 rows in file order.</summary>
    public IReadOnlyList<PersonaLineRow> Rows { get; }

    /// <summary>All rows of one persona (50 when the bank is the shipped one).</summary>
    public IReadOnlyList<PersonaLineRow> RowsOf(SystemPersona persona) => _byPersona[persona];

    /// <summary>All rows of one persona in one face.</summary>
    public IReadOnlyList<PersonaLineRow> RowsOf(SystemPersona persona, PersonaLineFace face) =>
        RowsOf(persona).Where(row => row.Face == face).ToArray();

    /// <summary>Row by full key; null when absent.</summary>
    public PersonaLineRow? Find(string key) =>
        _byKey.TryGetValue(key, out PersonaLineRow? row) ? row : null;

    /// <summary>
    /// Deterministic first row of one persona in one face (CS-1.4 no
    /// sampling: the L1 filler face of the bank — same persona+face ⇒ same
    /// line, on every machine).
    /// </summary>
    /// <exception cref="InvalidOperationException">The face carries no row for this persona (parser forbids this).</exception>
    public PersonaLineRow First(SystemPersona persona, PersonaLineFace face) =>
        RowsOf(persona, face).FirstOrDefault()
            ?? throw new InvalidOperationException($"人格 {persona} 面 {face} 无台词行（解析器应已拦截）");

    /// <summary>
    /// Strict parse: closed marker, closed persona/face/cue sets, unique
    /// keys, exactly <see cref="RowsPerPersona"/> rows per persona and ≥1
    /// row of every face per persona (matrix completeness — a hole fails
    /// the load, never a silent gap).
    /// </summary>
    /// <param name="json">Raw bank JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="NarrativeSchemaException">Any schema violation.</exception>
    public static PersonaLineBank Parse(string json, string sourceName = "persona lines")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new NarrativeSchemaException($"{sourceName}: 台词库文件为空");
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

            JsonElement rowsEl = Array(root, "rows", sourceName);
            Require(
                rowsEl.GetArrayLength() == RowsPerPersona * 4,
                $"{sourceName}: rows 须 {RowsPerPersona * 4} 行（四人格×{RowsPerPersona}，得到 {rowsEl.GetArrayLength()}）");

            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var perPersona = Enum.GetValues<SystemPersona>()
                .ToDictionary(persona => persona, persona => new List<PersonaLineRow>());
            foreach (JsonElement rowEl in rowsEl.EnumerateArray())
            {
                Require(rowEl.ValueKind == JsonValueKind.Object, $"{sourceName}: rows 行须为对象");
                string personaName = String(rowEl, "persona", sourceName);
                SystemPersona persona = DialogueLine.ParsePersona(personaName, sourceName);
                string key = String(rowEl, "key", sourceName);
                Require(!key.Any(char.IsWhiteSpace), $"{sourceName}: rows.key 含空白（{key}）");
                Require(seenKeys.Add(key), $"{sourceName}: rows.key 重复（{key}）");
                string faceName = String(rowEl, "face", sourceName);
                if (!FaceNames.TryGetValue(faceName, out PersonaLineFace face))
                {
                    throw new NarrativeSchemaException(
                        $"{sourceName}: 未登记面 {faceName}（封闭表 {string.Join("/", FaceNames.Keys)}）");
                }

                string text = String(rowEl, "text", sourceName);
                Require(!string.IsNullOrWhiteSpace(text), $"{sourceName}: rows.text 为空白（{key}）");

                string? cue = null;
                if (rowEl.TryGetProperty("cue", out JsonElement cueEl) && cueEl.ValueKind != JsonValueKind.Null)
                {
                    Require(cueEl.ValueKind == JsonValueKind.String, $"{sourceName}: rows.cue 须为字符串（{key}）");
                    cue = cueEl.GetString();
                    Require(
                        StoryChapter.SoundCues.Contains(cue!),
                        $"{sourceName}: rows.cue 未登记（{cue}，封闭表 {string.Join("/", StoryChapter.SoundCues)}）");
                }

                perPersona[persona].Add(new PersonaLineRow(persona, key, face, text, cue));
            }

            foreach (KeyValuePair<SystemPersona, List<PersonaLineRow>> pair in perPersona)
            {
                Require(
                    pair.Value.Count == RowsPerPersona,
                    $"{sourceName}: 人格 {pair.Key} 须恰好 {RowsPerPersona} 行（得到 {pair.Value.Count}——F-109 ≤50 取满）");
                foreach (PersonaLineFace face in Faces)
                {
                    Require(
                        pair.Value.Any(row => row.Face == face),
                        $"{sourceName}: 人格 {pair.Key} 缺面 {face} 行（覆盖矩阵不完备）");
                }
            }

            var rows = new List<PersonaLineRow>(rowsEl.GetArrayLength());
            foreach (List<PersonaLineRow> personaRows in perPersona.Values)
            {
                rows.AddRange(personaRows);
            }

            return new PersonaLineBank(rows);
        }
    }

    /// <summary>Content-facing face names (closed mapping, snake_case).</summary>
    public static readonly IReadOnlyDictionary<string, PersonaLineFace> FaceNames =
        new Dictionary<string, PersonaLineFace>(StringComparer.Ordinal)
        {
            ["sys"] = PersonaLineFace.Sys,
            ["opening"] = PersonaLineFace.Opening,
            ["act1"] = PersonaLineFace.ActOne,
            ["act2"] = PersonaLineFace.ActTwo,
            ["act3"] = PersonaLineFace.ActThree,
            ["victory"] = PersonaLineFace.Victory,
            ["victory_hidden"] = PersonaLineFace.VictoryHidden,
            ["defeat"] = PersonaLineFace.Defeat,
            ["trust_shift"] = PersonaLineFace.TrustShift,
            ["settlement"] = PersonaLineFace.Settlement,
            ["era_arrival"] = PersonaLineFace.EraArrival,
            ["switch"] = PersonaLineFace.Switch,
            ["off_farewell"] = PersonaLineFace.OffFarewell,
            ["boss_wave"] = PersonaLineFace.BossWave,
            ["perfect_clear"] = PersonaLineFace.PerfectClear,
            ["resource_starved"] = PersonaLineFace.ResourceStarved,
        };

    // ---- JSON helpers（与 StoryChapter 同约定，缺字段即异常） ----------------

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
