using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Narrative;
using Xunit;

namespace EraCore.Tests.Narrative;

// [C-13] [C-24] [F-105] [F-106] [F-109] T-126 四人格情境台词库：装载即校验
// （4×50=200 行/16 面×4 人格覆盖矩阵/G10 条数核对）、sys 槽位与中性库逐条
// 对齐（L1 填充共用槽位解析）、章绑定面与三章 story 交叉核对、人格键贯穿
// （DialogueLine.Persona）、字数表人格维度（≤8000）、切换人格能力等价
// （同节拍流四人格→幕推进/拍刻/行数/判定全等，只有人格行不同——F-109 验收）
// 与 schema 违规全矩阵拒绝（未知人格/未知面/重复 key/行数不符/缺面/未知 cue）。
public class PersonaLineBankTests
{
    private static PersonaLineBank Bank() => PersonaLineBank.Parse(
        NarrativeContent.Read("persona-lines.json"), "narrative/persona-lines.json");

    // ---------------------------------------------------------------- legal

    [Fact] // legal — [F-109] 装载即全量：四人格各恰好 50 行（G10 4×50 条核对）
    public void The_bank_carries_four_personas_at_fifty_rows_each()
    {
        PersonaLineBank bank = Bank();

        Assert.Equal(200, bank.Rows.Count);
        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            Assert.Equal(PersonaLineBank.RowsPerPersona, bank.RowsOf(persona).Count);
        }

        // 200 行文本互不相同（人格改写=四套不同表达，非四份拷贝）
        Assert.Equal(200, bank.Rows.Select(row => row.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact] // legal — [F-109] 覆盖矩阵：16 面 × 4 人格全格非空（解析器执法+测试复核）
    public void Every_persona_covers_every_face()
    {
        PersonaLineBank bank = Bank();

        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            foreach (PersonaLineFace face in PersonaLineBank.Faces)
            {
                Assert.NotEmpty(bank.RowsOf(persona, face)); // 缺格在装载即抛，这里复核
            }
        }

        Assert.Equal(16, PersonaLineBank.Faces.Count);
    }

    [Fact] // legal — [F-105] sys 面槽位对齐：15 情境×4 人格的【槽位名】序列与中性模板逐条相等
    public void Sys_rows_keep_the_neutral_templates_slot_sequences()
    {
        PersonaLineBank bank = Bank();
        SystemLineLibrary neutral = NarrativeContent.SystemLines();

        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            foreach (PersonaLineRow row in bank.RowsOf(persona, PersonaLineFace.Sys))
            {
                // 键约定：{persona}.{中性键（'.'分隔）}——"advisor.sys.alarm-integrity"
                // ↔ 中性模板 "sys-alarm-integrity"（逐条锚定，槽位序列逐字对齐）
                string situation = row.Key[(row.Key.IndexOf('.') + 1)..];
                SystemLineTemplate? template = neutral.Find(situation.Replace('.', '-'));
                Assert.NotNull(template); // 人格行锚定一条中性情境
                Assert.Equal(template!.Slots, SlotsOf(row.Text)); // 槽位名序列逐字对齐
            }
        }

        Assert.Equal(15, neutral.Items.Count); // 15 情境全覆盖（每人各 15 行 sys 面）
    }

    [Fact] // legal — 章绑定面交叉核对：三章 story 的 open/act1~3/victory 面逐章在库
    public void Chapter_bound_faces_track_the_shipped_story_chapters()
    {
        PersonaLineBank bank = Bank();
        var chapters = new[] { NarrativeContent.StoryE1(), NarrativeContent.StoryE2(), NarrativeContent.StoryE3() };

        foreach (StoryChapter chapter in chapters)
        {
            string era = chapter.Id[^2..]; // story-e1 → e1
            foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
            {
                foreach ((PersonaLineFace face, string tag) in new[]
                {
                    (PersonaLineFace.Opening, "opening"),
                    (PersonaLineFace.ActOne, "act1"),
                    (PersonaLineFace.ActTwo, "act2"),
                    (PersonaLineFace.ActThree, "act3"),
                    (PersonaLineFace.Victory, "victory"),
                })
                {
                    string key = $"{PersonaKeyId(persona)}.{tag}.{era}";
                    Assert.NotNull(bank.Find(key)); // 章绑定行按命名约定在库
                    Assert.Equal(face, bank.Find(key)!.Face);
                }
            }
        }
    }

    [Fact] // legal — [F-137] 音效位全量：200 行 cue 全落封闭三级（警报/确认/点评三组）
    public void Every_row_carries_a_closed_cue_slot()
    {
        PersonaLineBank bank = Bank();

        Assert.All(bank.Rows, row => Assert.NotNull(row.SoundCue));
        Assert.All(bank.Rows, row => Assert.Contains(row.SoundCue!, StoryChapter.SoundCues));

        // 三级全用：ding_crisis（警报组）/ding_info（确认组）/ding_reward（点评奖励组）
        Assert.Equal(new[] { "ding_crisis", "ding_info", "ding_reward" },
            bank.Rows.Select(row => row.SoundCue).Distinct().Order().ToArray());
    }

    [Fact] // legal — [F-109] 人格键贯穿：行→DialogueLine 适配保留人格与音效位
    public void Rows_adapt_to_dialogue_lines_with_the_persona_key_intact()
    {
        PersonaLineBank bank = Bank();

        foreach (PersonaLineRow row in bank.Rows)
        {
            DialogueLine line = row.ToDialogueLine();
            Assert.Equal(row.Key, line.Key);
            Assert.Equal(PersonaLineBank.SpeakerZh, line.Speaker);
            Assert.Equal(row.Persona, line.Persona); // 人格键贯穿（T-126 交付面）
            Assert.Equal(row.SoundCue, line.SoundCue);
            Assert.Equal($"零：{row.Text}", line.BubbleText);
        }
    }

    // ------------------------------------------------------ F-106 字数表（人格维度）

    [Fact] // legal — [F-106] 字数表人格维度行：人格库独立预算 ≤8000 字（不入主线 2 万口径）
    public void The_persona_bank_stays_within_its_own_8000_character_budget()
    {
        int chars = NarrativeContent.CountNarrativeChars("persona-lines.json");

        Assert.InRange(chars, 1, 8000); // 主线 corpus 测试（StoryChapterTests）不计本库——
        // 表达层变体与主线正文本是两个维度（人格维度入字数表=本行执法）
    }

    // ------------------------------------------------------ F-109 能力等价（验收）

    [Fact] // legal — [F-109] 能力等价：四人格同节拍流→幕推进/拍刻/行数/闲聊拍数全等
    public void Switching_personas_changes_wording_only_never_the_beat_trace()
    {
        StoryChapter chapter = NarrativeContent.StoryE1();
        MockArcRow arc = chapter.MockArcFor(chapter.LevelId)!;

        var traces = new Dictionary<SystemPersona, string>();
        var idleCounts = new Dictionary<SystemPersona, int>();
        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            var director = new NarrativeDirector(chapter, TrustBadge.Trustworthy, persona, arc);
            var beats = new List<string>
            {
                $"begin={director.Begin().Count}",
                $"w2={director.OnWaveStart(2, 28_800).Count}@{director.ActsCompleted}",
                $"aha={director.OnCinematicFired("cinematic:e1-aha", 28_800).Count}@{director.ActsCompleted}",
                $"w4={director.OnWaveStart(4, 36_000).Count}@{director.ActsCompleted}",
                $"w5={director.OnWaveStart(5, 50_400).Count}@{director.ActsCompleted}",
            };

            MockArcVerdict verdict = MockArcRulebook.Judge(director.MockArc!, victory: true, towersStanding: 3);
            beats.Add($"verdict={verdict.HiddenHit}/{verdict.HiddenChestTier}");
            beats.Add($"loop={director.Golden.LoopSeconds}");
            int idles = 0;
            foreach (TrustBadge badge in StoryChapter.TrustTiers)
            {
                director.SetTrust(badge);
                idles += director.OnWaveEnd() is null ? 0 : 1; // 波间闲聊拍数（不挨饿）
            }

            beats.Add($"end={director.OnVictory(50_430).Count}@{director.Phase}");
            traces[persona] = string.Join(";", beats);
            idleCounts[persona] = idles;
        }

        Assert.Equal(8, traces[SystemPersona.Advisor].Count(ch => ch == '=')); // 轨迹非空壳（8 拍段）
        Assert.Single(traces.Values.Distinct()); // 四人格轨迹全等（能力面零变更）
        Assert.All(idleCounts.Values, count => Assert.Equal(3, count)); // 闲聊三档都不挨饿
        Assert.Contains("end=4@Ended", traces[SystemPersona.Advisor]); // 三幕齐→交底 2 行+预告 2 行收章
        // 表达面确实按人格走：闲聊行的人格=当前人格（只换措辞）
        var personaDirector = new NarrativeDirector(chapter, TrustBadge.Devoted, SystemPersona.Tsundere, arc);
        _ = personaDirector.Begin();
        Assert.Equal(SystemPersona.Tsundere, personaDirector.OnWaveEnd()!.Persona);
    }

    [Fact] // legal — [F-109] 人格不触达判定面：bank 行不含数值谓词，mock-arc 判定与人格无关
    public void Persona_rows_never_touch_the_judgment_surfaces()
    {
        PersonaLineBank bank = Bank();

        // 台词是纯表达：四人格同面行的 cue 全一致（同情境同一音效位，人格只换措辞）
        foreach (PersonaLineFace face in PersonaLineBank.Faces)
        {
            var cues = new HashSet<string>(
                Enum.GetValues<SystemPersona>().Select(p => bank.First(p, face).SoundCue!),
                StringComparer.Ordinal);
            Assert.Single(cues); // 同面同 cue——音效位归情境，人格无权改判警报级别
        }
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — First() 确定性：同人格同面两次取行恒等（CS-1.4 无采样）
    public void Face_picks_are_deterministic()
    {
        PersonaLineBank bank = Bank();

        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            PersonaLineRow first = bank.First(persona, PersonaLineFace.Switch);
            Assert.Equal(first.Key, bank.First(persona, PersonaLineFace.Switch).Key);
            Assert.Equal(first.Text, bank.First(persona, PersonaLineFace.Switch).Text);
        }

        Assert.Null(bank.Find("advisor.nope")); // 缺键=null（消费面降级，不抛）
        Assert.NotNull(bank.Find("tsundere.switch.in")); // 三联动台词臂的切入行在库
    }

    // -------------------------------------------------------------- illegal

    [Theory] // illegal — schema 违规矩阵：未知人格/未知面/重复 key/未知 cue/缺字段
    [InlineData("\"persona\":\"kuudere\"", "未登记人格")]
    [InlineData("\"face\":\"side_quest\"", "未登记面")]
    [InlineData("\"cue\":\"ding_boom\"", "cue 未登记")]
    [InlineData("\"key\":\"advisor.sys.detect\"", "重复")]
    public void Row_violations_are_rejected(string mutation, string hint)
    {
        string json = MutateFirstRow(mutation);

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => PersonaLineBank.Parse(json, "inline"));
        Assert.Contains(hint, ex.Message);
    }

    [Fact] // illegal — 行数纪律：51 行人格（200→201）即拒（F-109 ≤50 上限取满）
    public void A_fifty_first_row_of_one_persona_is_rejected()
    {
        string json = BankJson(extraRow: true);

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => PersonaLineBank.Parse(json, "inline"));
        Assert.Contains("201", ex.Message); // 总行数先拒（201≠200）
    }

    [Fact] // illegal — 覆盖矩阵缺格：剥 advisor 的 switch 面行（补回凑数行保总数）→面缺失拒绝
    public void A_missing_face_for_one_persona_is_rejected()
    {
        string json = BankJson(stripFaceOf: ("advisor", "switch"));

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => PersonaLineBank.Parse(json, "inline"));
        Assert.Contains("Switch", ex.Message); // 缺面点名（覆盖矩阵执法）
    }

    [Fact] // illegal — 根结构：错 content_type / 空文件 / 非 JSON
    public void Root_violations_are_rejected()
    {
        Assert.Contains("content_type", Assert.Throws<NarrativeSchemaException>(
            () => PersonaLineBank.Parse("""{"rows":[]}""", "inline")).Message);
        Assert.Contains("为空", Assert.Throws<NarrativeSchemaException>(
            () => PersonaLineBank.Parse(" ", "inline")).Message);
        Assert.Contains("JSON 解析失败", Assert.Throws<NarrativeSchemaException>(
            () => PersonaLineBank.Parse("{nope", "inline")).Message);
    }

    // ---- inline scaffolds ---------------------------------------------------

    private static string PersonaKeyId(SystemPersona persona) => persona switch
    {
        SystemPersona.Advisor => "advisor",
        SystemPersona.Snarky => "snarky",
        SystemPersona.Goofy => "goofy",
        _ => "tsundere",
    };

    private static List<string> SlotsOf(string text)
    {
        var names = new List<string>();
        int i = text.IndexOf('【');
        while (i >= 0)
        {
            int j = text.IndexOf('】', i);
            if (j < 0)
            {
                break;
            }

            names.Add(text[(i + 1)..j]);
            i = text.IndexOf('【', j);
        }

        return names;
    }

    /// <summary>Applies one mutation to the shipped bank's first row (rows are one per line).</summary>
    private static string MutateFirstRow(string mutation)
    {
        string raw = NarrativeContent.Read("persona-lines.json");
        string[] lines = raw.Split('\n');
        int index = Array.FindIndex(lines, line => line.Contains("\"advisor.sys.detect\"", StringComparison.Ordinal));
        Assert.True(index >= 0, "generator convention: one row per line");

        lines[index] = mutation switch
        {
            "\"persona\":\"kuudere\"" => lines[index].Replace("\"persona\": \"advisor\"", "\"persona\": \"kuudere\""),
            "\"face\":\"side_quest\"" => lines[index].Replace("\"face\": \"sys\"", "\"face\": \"side_quest\""),
            "\"cue\":\"ding_boom\"" => lines[index].Replace("\"cue\": \"ding_info\"", "\"cue\": \"ding_boom\""),
            "\"key\":\"advisor.sys.detect\"" => lines[index].Replace(
                "\"key\": \"advisor.sys.detect\"", "\"key\": \"advisor.sys.wave-report\""), // 与 wave-report 行撞键
            _ => throw new InvalidOperationException($"unhandled mutation {mutation}"),
        };

        return string.Join('\n', lines);
    }

    /// <summary>Rebuilds a bank JSON from the shipped rows with one structural mutation.</summary>
    private static string BankJson(bool extraRow = false, (string Persona, string Face)? stripFaceOf = null)
    {
        string raw = NarrativeContent.Read("persona-lines.json");
        using JsonDocument document = JsonDocument.Parse(raw);
        var rows = new List<string>();
        foreach (JsonElement row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            bool strip = stripFaceOf is { } spec
                && row.GetProperty("persona").GetString() == spec.Persona
                && row.GetProperty("face").GetString() == spec.Face;
            if (!strip)
            {
                rows.Add(JsonSerializer.Serialize(row));
            }
        }

        if (extraRow)
        {
            // 复制一条 advisor 行改键——凑第 51 行 advisor（总数 201）
            string clone = rows.First(row => row.Contains("\"persona\":\"advisor\"", StringComparison.Ordinal));
            rows.Add(clone.Replace("\"key\":\"advisor.sys.detect\"", "\"key\":\"advisor.sys.extra\""));
        }

        if (stripFaceOf is { } stripped)
        {
            // 面缺格探针须先补回行数（总数 200、advisor 仍 50）：补一条 sys 面凑数行
            string filler = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["persona"] = stripped.Persona,
                ["key"] = $"{stripped.Persona}.sys.filler",
                ["face"] = "sys",
                ["text"] = "凑数行。",
                ["cue"] = "ding_info",
            });
            rows.Add(filler);
        }

        return $$"""
            {
                "content_type": "PersonaLineBank",
                "rows": [{{string.Join(",", rows)}}]
            }
            """;
    }
}
