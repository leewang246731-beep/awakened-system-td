using EraCore.Agent.Governance;
using EraCore.Narrative;
using Xunit;

namespace EraCore.Tests.Narrative;

// [C-24] [F-098] [F-100] [F-101] [F-102] [F-103] [F-105] [F-106] T-089 章节
// schema：两章仓库内容装载即校验（三幕恰好/预算>0/闲聊池三档/打脸扮猪行齐/
// 卡点预告齐）、文本量纪律（两章 ≤8000 字、叙事总盘 ≤2 万字）、schema 违规
// 全矩阵拒绝（缺字段/空行/未登记谓词/未登记档位/未登记 kind/越界乘子/幕数
// 不符/重复 id）。
public class StoryChapterTests
{
    // ---------------------------------------------------------------- legal

    [Fact] // legal — E1 章装载：三幕（波 2 受辱/绑定演出/波 5 打脸）+预算+三档池+打脸行
    public void E1_chapter_loads_with_the_golden_three_acts()
    {
        StoryChapter chapter = NarrativeContent.StoryE1();

        Assert.Equal("narrative:story-e1", chapter.Id);
        Assert.Equal("era1:era-ancient", chapter.Era);
        Assert.Equal("campaign:level-e1-01", chapter.LevelId);
        Assert.Equal(3, chapter.GoldenActs.Acts.Count); // 受辱→觉醒→打脸
        Assert.Equal(900, chapter.GoldenActs.BudgetSeconds); // §12.1 15 分钟循环预算
        Assert.Equal(ActPredicateKind.WaveStart, chapter.GoldenActs.Acts[0].Predicate.Kind);
        Assert.Equal(2, chapter.GoldenActs.Acts[0].Predicate.Wave); // 波 2 受辱
        Assert.Equal(ActPredicateKind.CinematicFired, chapter.GoldenActs.Acts[1].Predicate.Kind);
        Assert.Equal("cinematic:e1-aha", chapter.GoldenActs.Acts[1].Predicate.ScriptId); // 绑定演出已有（T-086）
        Assert.Equal(ActPredicateKind.WaveStart, chapter.GoldenActs.Acts[2].Predicate.Kind);
        Assert.Equal(5, chapter.GoldenActs.Acts[2].Predicate.Wave); // 波 5 反推打脸
        Assert.Equal(3, chapter.IntermissionPools.Count); // 信任三档分池
        Assert.All(
            new[] { TrustBadge.Skeptical, TrustBadge.Trustworthy, TrustBadge.Devoted },
            badge => Assert.NotEmpty(chapter.IntermissionPools[badge]));
        Assert.Equal("campaign:hook-e1-awakening-handover", chapter.Ending.HookId);
        Assert.NotNull(chapter.Ending.Tease.EliteId); // E1 卡点预告带 E2 精英实体 id
        MockArcRow arc = Assert.IsType<MockArcRow>(chapter.MockArcFor("campaign:level-e1-01"));
        Assert.Equal(MockArcKind.FaceSlap, arc.Kind);
    }

    [Fact] // legal — E2 章装载：三幕（波 2 被嘲/波 5 水力/波 9 名场面）+扮猪行带压制参数
    public void E2_chapter_loads_with_the_pig_arc_row()
    {
        StoryChapter chapter = NarrativeContent.StoryE2();

        Assert.Equal("campaign:level-e2-01", chapter.LevelId);
        Assert.Equal(2, chapter.GoldenActs.Acts[0].Predicate.Wave);
        Assert.Equal(5, chapter.GoldenActs.Acts[1].Predicate.Wave);
        Assert.Equal(9, chapter.GoldenActs.Acts[2].Predicate.Wave);

        MockArcRow arc = Assert.IsType<MockArcRow>(chapter.MockArcFor("campaign:level-e2-02"));
        Assert.Equal(MockArcKind.MockArcPig, arc.Kind);
        Assert.NotNull(arc.Suppression);
        Assert.Equal("era2:res-copper", arc.Suppression.Resource);
        Assert.Equal(0.5d, arc.Suppression.Multiplier, 3); // 资源压制参数入数据
        Assert.Equal(3, arc.Taunts.Count); // NPC 嘲讽文案三拍
        Assert.Null(chapter.Ending.Tease.EliteId); // E3 未交付——纯文本预告
        Assert.False(string.IsNullOrWhiteSpace(chapter.Ending.Tease.ThreatLineZh));
    }

    // ------------------------------------------------------ F-106 文本量纪律

    [Fact] // legal — 文本量：两章合计 ≤8000 字（≤2 万总预算的 40%，T-089 任务卡口径）
    public void The_two_chapters_stay_within_the_8000_character_budget()
    {
        int twoChapters = NarrativeContent.CountNarrativeChars("story-e1.json", "story-e2.json");

        Assert.InRange(twoChapters, 1, 8000);
    }

    [Fact] // legal — 文本量：叙事总盘（三章+残卷+钩子+台词库）≤2 万字（F-106 主线预算）
    public void The_whole_narrative_corpus_stays_within_the_20000_character_budget()
    {
        int corpus = NarrativeContent.CountNarrativeChars(
            "story-e1.json", "story-e2.json", "story-e3.json", "remnants.json", "hooks.json", "system-lines.json");

        Assert.InRange(corpus, 1, 20000);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 关卡无配置行：MockArcFor 返回 null（合法缺省）
    public void A_level_without_a_configured_row_yields_null()
    {
        Assert.Null(NarrativeContent.StoryE1().MockArcFor("campaign:level-e1-04"));
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 幕数不符：恰好三幕（受辱→觉醒→打脸），两幕即拒绝
    public void A_two_act_chapter_is_rejected()
    {
        string json = ChapterJson(acts: """
            {"id":"a1","title_zh":"受辱","predicate":{"kind":"wave_start","wave":2},"lines":[{"key":"k","speaker":"旁白","text":"x"}]},
            {"id":"a2","title_zh":"打脸","predicate":{"kind":"wave_start","wave":5},"lines":[{"key":"k2","speaker":"旁白","text":"y"}]}
            """);

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse(json, "inline"));
        Assert.Contains("恰好 3 幕", ex.Message);
    }

    [Theory] // illegal — schema 违规矩阵：未登记谓词/wave_start 缺 wave/cinematic_fired 缺 script/未登记人格/未登记 cue/幕重复 id/缺字段
    [InlineData("""{"kind":"boss_die"}""", "未登记谓词")]
    [InlineData("""{"kind":"wave_start"}""", "wave ≥1")]
    [InlineData("""{"kind":"cinematic_fired"}""", "缺字符串字段 script")]
    [InlineData("""{"persona":"kuudere"}""", "未登记人格")]
    [InlineData("""{"cue":"ding_boom"}""", "cue 未登记")]
    [InlineData("""{"id":"a1","title_zh":"t","predicate":{"kind":"wave_start","wave":2},"lines":[{"key":"k","speaker":"旁白","text":"x"}]},{"id":"a1","title_zh":"t","predicate":{"kind":"wave_start","wave":3},"lines":[{"key":"k2","speaker":"旁白","text":"y"}]},{"id":"a3","title_zh":"t","predicate":{"kind":"wave_start","wave":5},"lines":[{"key":"k3","speaker":"旁白","text":"z"}]}""", "重复")]
    public void Act_schema_violations_are_rejected(string fragment, string hint)
    {
        // 单对象片段（谓词/行内键值对）剥外层大括号注入第一幕骨架，后补两幕
        // 合法行凑足三幕（让违规落在注入点而非幕数）；多行片段=完整 acts 体。
        string tail = """
            ,{"id":"a2","title_zh":"觉醒","predicate":{"kind":"victory"},"lines":[{"key":"k2","speaker":"系统","text":"y"}]},
            {"id":"a3","title_zh":"打脸","predicate":{"kind":"wave_start","wave":5},"lines":[{"key":"k3","speaker":"系统","text":"z"}]}
            """;
        string inner = fragment.StartsWith('{') && fragment.EndsWith('}') && !fragment.Contains("},{")
            ? fragment[1..^1]
            : fragment;
        string acts = inner.StartsWith("\"kind\"")
            ? $$"""{"id":"a1","title_zh":"受辱","predicate":{ {{inner}} },"lines":[{"key":"k","speaker":"旁白","text":"x"}]}{{tail}}"""
            : inner.StartsWith("\"persona\"") || inner.StartsWith("\"cue\"")
                ? $$"""{"id":"a1","title_zh":"受辱","predicate":{"kind":"wave_start","wave":2},"lines":[{"key":"k","speaker":"旁白","text":"x", {{inner}} }]}{{tail}}"""
                : fragment; // 三行重复 id 等：整段 acts 体

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse(ChapterJson(acts: acts), "inline"));
        Assert.Contains(hint, ex.Message);
    }

    [Fact] // illegal — 扮猪行缺 suppression（强制劣势开局参数是结构必填）
    public void A_pig_arc_row_without_suppression_is_rejected()
    {
        string json = ChapterJson(mockArcs: """
            {"level_id":"campaign:level-x","kind":"mock_arc_pig","taunts":[{"wave":1,"speaker":"工头","text":"嘲讽"}],"low_config":{"max_towers":3},"hidden_reward":{"chest_tier":"gold_extra","item_ids":["campaign:title-wallkeeper"],"lines":[{"key":"k","speaker":"零","text":"x"}]}}
            """);

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse(json, "inline"));
        Assert.Contains("必须携带 suppression", ex.Message);
    }

    [Theory] // illegal — 压制乘子越界：(0,1] 外拒绝（劣势=削弱而非增发）
    [InlineData(0.0)]
    [InlineData(1.5)]
    public void An_out_of_range_suppression_multiplier_is_rejected(double multiplier)
    {
        string json = ChapterJson(mockArcs: """
            {"level_id":"campaign:level-x","kind":"mock_arc_pig","suppression":{"resource":"era2:res-copper","multiplier":MULT},"taunts":[{"wave":1,"speaker":"工头","text":"嘲讽"}],"low_config":{"max_towers":3},"hidden_reward":{"chest_tier":"gold_extra","item_ids":["campaign:title-wallkeeper"],"lines":[{"key":"k","speaker":"零","text":"x"}]}}
            """.Replace("MULT", multiplier.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse(json, "inline"));
        Assert.Contains("multiplier", ex.Message);
    }

    [Fact] // illegal — 闲聊池缺一档：三档分池是结构必填（F.4 三档徽记）
    public void A_missing_trust_tier_pool_is_rejected()
    {
        string json = ChapterJson(pools: """
            "intermission_pools": {"skeptical": [{"key":"k","speaker":"系统","text":"x"}], "trustworthy": [{"key":"k2","speaker":"系统","text":"y"}]},
            """);

        NarrativeSchemaException ex = Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse(json, "inline"));
        Assert.Contains("devoted", ex.Message);
    }

    [Fact] // illegal — [F-098] 根结构：缺 content_type / 空文件 / 非 JSON
    public void Root_violations_are_rejected()
    {
        Assert.Contains("content_type", Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse("""{"id":"x"}""", "inline")).Message);
        Assert.Contains("为空", Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse(" ", "inline")).Message);
        Assert.Contains("JSON 解析失败", Assert.Throws<NarrativeSchemaException>(
            () => StoryChapter.Parse("{nope", "inline")).Message);
    }

    // ---- inline chapter scaffold（合法骨架，违规点注入） ------------------

    private static string ChapterJson(string? acts = null, string? pools = null, string? mockArcs = null)
    {
        acts ??= """
            {"id":"a1","title_zh":"受辱","predicate":{"kind":"wave_start","wave":2},"lines":[{"key":"k","speaker":"旁白","text":"x"}]},
            {"id":"a2","title_zh":"觉醒","predicate":{"kind":"victory"},"lines":[{"key":"k2","speaker":"系统","text":"y"}]},
            {"id":"a3","title_zh":"打脸","predicate":{"kind":"wave_start","wave":5},"lines":[{"key":"k3","speaker":"系统","text":"z"}]}
            """;
        pools ??= """
            "intermission_pools": {
                "skeptical": [{"key":"k","speaker":"系统","text":"x","persona":"advisor"}],
                "trustworthy": [{"key":"k2","speaker":"系统","text":"y","persona":"snarky"}],
                "devoted": [{"key":"k3","speaker":"系统","text":"z","persona":"goofy"}]
            },
            """;
        mockArcs ??= """
            {"level_id":"campaign:level-x","kind":"face_slap","taunts":[{"wave":2,"speaker":"斥候","text":"嘲讽"}],"low_config":{"max_towers":3},"hidden_reward":{"chest_tier":"gold_extra","item_ids":["campaign:title-wallkeeper"],"lines":[{"key":"k","speaker":"系统","text":"r"}]}}
            """;

        return $$"""
            {
                "content_type": "StoryChapter",
                "id": "narrative:story-x",
                "era": "era1:era-ancient",
                "level_id": "campaign:level-x",
                "title_zh": "测试章",
                "golden_acts": {
                    "budget_seconds": 900,
                    "acts": [{{acts}}]
                },
                "opening": [{"key":"o1","speaker":"旁白","text":"开场"}],
                {{pools}}
                "ending": {
                    "hook_id": "campaign:hook-x",
                    "lines": [{"key":"e1","speaker":"系统","text":"交底"}],
                    "tease": {"next_era": null, "elite_id": null, "silhouette_zh": "剪影", "threat_line_zh": "威胁"}
                },
                "mock_arcs": [{{mockArcs}}]
            }
            """;
    }
}
