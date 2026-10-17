using System.Text.Json;
using EraCore.Narrative;
using Xunit;
using static EraCore.Tests.Narrative.NarrativeContent;

namespace EraCore.Tests.Narrative;

// [C-24] [F-098] [F-099] T-089 知识残卷目录（T-136 E5/终章增量后 22 条）：残卷装载
//（考古卷挂接+叙事拍挂接）、scroll_id 对 archaeology.json knowledge_scrolls 零幽灵
// 引用、E5 回声低语沿 echo_stage 渐显（1..4 单调）、schema 违规（规模漂移/渐显回退/
// 来源缺 id）。
public class RemnantCatalogTests
{
    // ---------------------------------------------------------------- legal

    [Fact] // legal — 22 条残卷装载：4 条挂考古卷 id+18 条挂叙事拍、era/stage 域合法（era 6 域 T-136 首用）
    public void TwentyTwo_remnants_load_with_archaeology_scroll_links()
    {
        RemnantCatalog catalog = Remnants();

        Assert.Equal(22, catalog.Items.Count); // T-089 首批 8+T-120 E3 增量 3+T-127 E4 增量 4+T-136 E5/终章增量 7
        Assert.Equal(4, catalog.Items.Count(item => item.SourceKind == RemnantSourceKind.KnowledgeScroll));
        Assert.Equal(18, catalog.Items.Count(item => item.SourceKind == RemnantSourceKind.NarrativeBeat));
        Assert.All(catalog.Items, item => Assert.InRange(item.EchoStage, 1, 4));
        Assert.All(catalog.Items, item => Assert.InRange(item.Era, 1, 6));
    }

    [Fact] // legal — 残卷-考古联动：每条 scroll_id 都是 archaeology.json 已登记残卷（零幽灵引用）
    public void Every_scroll_linked_remanent_exists_in_the_archaeology_table()
    {
        RemnantCatalog catalog = Remnants();

        using JsonDocument archaeology = JsonDocument.Parse(ReadSibling("legacy", "archaeology.json"));
        IReadOnlyList<string> scrolls = archaeology.RootElement.GetProperty("knowledge_scrolls")
            .EnumerateArray()
            .Select(scroll => scroll.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(4, scrolls.Count); // 考古侧现役 4 卷

        foreach (KnowledgeRemnant remnant in catalog.Items.Where(item => item.ScrollId is not null))
        {
            Assert.Contains(remnant.ScrollId!, scrolls);
            Assert.Single(catalog.ByScroll(remnant.ScrollId!)); // 一卷一档
        }
    }

    [Fact] // legal — E5 回声渐显：echo_stage 沿文件序单调不降、覆盖 1..4 全段、低语文本齐
    public void The_echo_whisper_grows_monotonically_across_all_four_stages()
    {
        RemnantCatalog catalog = Remnants();
        int[] stages = catalog.Items.Select(item => item.EchoStage).ToArray();

        Assert.Equal(stages.OrderBy(stage => stage), stages); // 装载器已执法单调（此处双面断言）
        Assert.Equal(new[] { 1, 2, 3, 4 }, stages.Distinct().OrderBy(stage => stage).ToArray());
        Assert.All(catalog.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.EchoWhisperZh)));
        Assert.All(catalog.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.MemoryZh)));
    }

    [Fact] // legal — 渐显切片：AtEchoStage(4)=最接近显形的九条（含『选择你』终章伏笔+E3/E4 增量；
    //        E4 四条=『另一个声音』渐显最密章，低语推进『最清晰』并自称门那边的另一半）
    public void The_final_stage_slice_carries_the_finale_foreshadow()
    {
        IReadOnlyList<KnowledgeRemnant> stage4 = Remnants().AtEchoStage(4);

        Assert.Equal(16, stage4.Count); // E2 两条+E3 增量三条（T-120）+E4 增量四条（T-127）+T-136 E5 增量四条+终章增量三条
        Assert.Contains(stage4, remnant => remnant.EchoWhisperZh.Contains("选择你")); // §12.1 忠诚反转的种子
        Assert.Equal(3, stage4.Count(remnant => remnant.Era == 3)); // E3 增量全在渐显上沿
        Assert.Equal(4, stage4.Count(remnant => remnant.Era == 4)); // E4 增量=渐显最密章（密度峰）
        Assert.Contains(stage4, remnant => remnant.EchoWhisperZh.Contains("另一半")); // 孪生碎片首次自显
        Assert.Contains(stage4, remnant => remnant.MemoryZh.Contains("初代")); // 应答方自称『初代』——终章真相预告
        Assert.Equal(4, stage4.Count(remnant => remnant.Era == 5)); // T-136 E5 增量=回声得名与选边
        Assert.Equal(3, stage4.Count(remnant => remnant.Era == 6)); // T-136 终章增量=记忆闭环（era 6 域首用）
    }

    [Fact] // legal — Find 查询：id 命中/未命中
    public void Find_resolves_by_id()
    {
        RemnantCatalog catalog = Remnants();

        Assert.NotNull(catalog.Find("narrative:remnant-e1-mural"));
        Assert.Null(catalog.Find("narrative:remnant-e9-nonexistent"));
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 残卷规模漂移：21 条/23 条拒绝（T-089 首批 8+T-120 增量 3+T-127 增量 4+T-136 增量 7）
    public void A_remnant_count_drift_is_rejected()
    {
        Assert.Contains("恰好 22 条", Assert.Throws<NarrativeSchemaException>(
            () => RemnantCatalog.Parse(RemnantJson(21), "inline")).Message);
        Assert.Contains("恰好 22 条", Assert.Throws<NarrativeSchemaException>(
            () => RemnantCatalog.Parse(RemnantJson(23), "inline")).Message);
    }

    [Fact] // illegal — 渐显回退：后行 stage 低于前行拒绝（单调不降）
    public void An_echo_stage_regression_is_rejected()
    {
        string regress = RemnantJson(22, stageAt4: 1);

        Assert.Contains("单调不降", Assert.Throws<NarrativeSchemaException>(
            () => RemnantCatalog.Parse(regress, "inline")).Message);
    }

    [Fact] // illegal — 来源缺 id：knowledge_scroll 缺 scroll_id / narrative_beat 缺 beat_id
    public void A_source_row_missing_its_link_id_is_rejected()
    {
        string noScroll = RemnantJson(22, sourceAt0: "knowledge_scroll", stripScroll0: true);
        Assert.Contains("缺 scroll_id", Assert.Throws<NarrativeSchemaException>(
            () => RemnantCatalog.Parse(noScroll, "inline")).Message);

        string noBeat = RemnantJson(22, sourceAt0: "narrative_beat");
        Assert.Contains("缺 beat_id", Assert.Throws<NarrativeSchemaException>(
            () => RemnantCatalog.Parse(noBeat, "inline")).Message);
    }

    // ---- inline scaffold -----------------------------------------------------

    private static string RemnantJson(int count, int stageAt4 = 4, string? sourceAt0 = null, bool stripScroll0 = false)
    {
        var rows = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int stage = i == 3 ? stageAt4 : Math.Min(i / 2 + 1, 4);
            string source = i == 0 && sourceAt0 is not null ? sourceAt0 : "knowledge_scroll";
            string link = source == "knowledge_scroll"
                ? (i == 0 && stripScroll0
                    ? "\"beat_id\": \"campaign:beat-x\"" // 卷行却只带拍 id → 缺 scroll_id
                    : $"\"scroll_id\": \"legacy:scroll-x-{i}\"")
                : "\"scroll_id\": null"; // 拍行却只带 scroll null → 缺 beat_id
            rows.Add($$"""
                {"id": "legacy:r-{{i}}", "source_kind": "{{source}}", {{link}}, "era": 1,
                 "echo_stage": {{stage}}, "memory_zh": "m{{i}}", "echo_whisper_zh": "w{{i}}"}
                """);
        }

        return $$"""
            {"content_type": "RemnantTable", "items": [{{string.Join(",", rows)}}]}
            """;
    }
}
