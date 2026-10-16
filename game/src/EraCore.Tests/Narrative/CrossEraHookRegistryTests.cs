using System.Text.Json;
using EraCore.Narrative;
using Xunit;
using static EraCore.Tests.Narrative.NarrativeContent;

namespace EraCore.Tests.Narrative;

// [C-24] [F-098] T-089 跨时代钩子登记表：五条钩子装载（E1~E5 每时代一条）、
// 埋点对实锚（planted_level 存在于 levels/*.json、L1-3=石语河谷任务卡钉锚）、
// 考古回收通道对 archaeology.json channels/L2-3 dig_sites 实锚、hook↔remnant
// 双向链一致、schema 违规矩阵。
public class CrossEraHookRegistryTests
{
    private static IReadOnlyList<(string Id, string? NameZh)> LevelRows(string era)
    {
        using JsonDocument document = JsonDocument.Parse(ReadSibling("levels", era + ".json"));
        var rows = new List<(string, string?)>();
        foreach (JsonElement item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            rows.Add((
                item.GetProperty("id").GetString()!,
                item.TryGetProperty("name_zh", out JsonElement name) ? name.GetString() : null));
        }

        return rows;
    }

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 五条钩子装载：E1~E5 每时代恰一条、序号 1..5、层表齐
    public void Five_hooks_load_one_per_era_in_ordinal_order()
    {
        CrossEraHookRegistry registry = Hooks();

        Assert.Equal(5, registry.Items.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, registry.Items.Select(hook => hook.Ordinal));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, registry.Items.Select(hook => hook.PlantedEra));
        Assert.Single(registry.PlantedIn(1));
        Assert.Equal("narrative:hook-e1-mural-airship", registry.PlantedIn(1)[0].Id);
        Assert.Equal(HookLayer.AdvisorPrivate, registry.Items[3].Layer); // E4 军师私人台词层
        Assert.Equal(HookLayer.SystemVoice, registry.Items[4].Layer); // E5 回声低语
    }

    [Fact] // legal — 埋点对实锚：E1 壁画=L1-3 石语河谷（08 任务卡钉锚）；E2 残页=L2-2；
    //        E3 图纸=L3-3 考古关（T-120 物化——dig_site 挖出位）；E4 卫星信号=L4-3 多核关
    //        （T-127 物化——军师私人频道开通位）；E5 未交付只登记回收位
    public void Planted_levels_exist_in_the_level_files_at_the_pinned_anchors()
    {
        CrossEraHookRegistry registry = Hooks();
        IReadOnlyList<(string Id, string? NameZh)> e1 = LevelRows("e1");
        IReadOnlyList<(string Id, string? NameZh)> e2 = LevelRows("e2");
        IReadOnlyList<(string Id, string? NameZh)> e3 = LevelRows("e3");
        IReadOnlyList<(string Id, string? NameZh)> e4 = LevelRows("e4");

        CrossEraHook mural = registry.Items[0];
        Assert.Equal("campaign:level-e1-03", mural.PlantedLevelId);
        Assert.Equal("石语河谷", e1.Single(row => row.Id == mural.PlantedLevelId).NameZh); // 任务卡钉锚
        Assert.Equal("era2:enemy-ram", StoryE1().Ending.Tease.EliteId); // E1 预告指向 E2 精英（era2 实锚）

        CrossEraHook prophecy = registry.Items[1];
        Assert.Equal("campaign:level-e2-02", prophecy.PlantedLevelId);
        Assert.Contains(e2, row => row.Id == prophecy.PlantedLevelId);

        CrossEraHook blueprint = registry.Items[2]; // T-120 物化：E3 已交付
        Assert.Equal("campaign:level-e3-03", blueprint.PlantedLevelId);
        Assert.Contains(e3, row => row.Id == blueprint.PlantedLevelId);

        CrossEraHook satellite = registry.Items[3]; // T-127 物化：E4 已交付（多核期=军师私人频道开通位）
        Assert.Equal("campaign:level-e4-03", satellite.PlantedLevelId);
        Assert.Contains(e4, row => row.Id == satellite.PlantedLevelId);
        Assert.Null(registry.Items[4].PlantedLevelId); // E5 未交付章只登记回收位
    }

    [Fact] // legal — E3 图纸-考古链一致性（T-120 物化）：埋点关=考古关（dig_sites 物理挖出位+
    //        unlocks.features 含 archaeology）+双向残卷链+E2 钩子的 E3 回收在章节文本兑现
    public void The_blueprint_hook_digs_out_of_a_real_archaeology_level()
    {
        CrossEraHook blueprint = Hooks().Items[2];

        using JsonDocument e3 = JsonDocument.Parse(ReadSibling("levels", "e3.json"));
        JsonElement level3 = e3.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == blueprint.PlantedLevelId);
        Assert.True(level3.GetProperty("map").TryGetProperty("dig_sites", out JsonElement digSites));
        Assert.True(digSites.GetArrayLength() >= 1, "埋点关须有 dig_site（图纸物理挖出位）");
        Assert.Contains(
            "archaeology",
            level3.GetProperty("unlocks").GetProperty("features").EnumerateArray()
                .Select(f => f.GetString()));

        // 双向残卷链：钩子 linked_remanent ↔ 残卷 beat_id（图纸记忆文本回扣壁画笔迹线）
        KnowledgeRemnant remnant = Assert.IsType<KnowledgeRemnant>(Remnants().Find(blueprint.LinkedRemnant!));
        Assert.Equal(blueprint.Id, remnant.BeatId);
        Assert.Contains("笔迹", remnant.MemoryZh);

        // E2 钩子的 E3 回收（recover.era=3 chapter_progress）：payoff 预言句在 E3 章节文本兑现
        //（story-e3 开场白第三行=残页译文『守望者会换一千张脸，门会再开。』）
        CrossEraHook prophecy = Hooks().Items[1];
        Assert.Equal(3, prophecy.Recover.Era);
        Assert.Contains("门会再开", prophecy.PayoffZh); // 回收方向句在 payoff 声明里
        Assert.Contains(
            StoryE3().Opening.Select(line => line.Text),
            text => text.Contains("门会再开")); // 兑现于 E3 开场白
    }

    [Fact] // legal — 考古回收实锚：E1 钩子回收通道=knowledge_scroll（archaeology.json 三通道之一）
    public void The_mural_recovers_through_a_real_archaeology_channel()
    {
        CrossEraHook mural = Hooks().Items[0];

        Assert.Equal(HookRecoverKind.ArchaeologyChannel, mural.Recover.Kind);
        Assert.Equal(2, mural.Recover.Era); // E2 考古可挖出

        using JsonDocument archaeology = JsonDocument.Parse(ReadSibling("legacy", "archaeology.json"));
        JsonElement channels = archaeology.RootElement.GetProperty("channels");
        Assert.True(
            channels.TryGetProperty(mural.Recover.Channel!, out JsonElement weight),
            $"archaeology.json 通道缺 {mural.Recover.Channel}");
        Assert.Equal(50, weight.GetInt32()); // knowledge_scroll 权重实锚（T-074 落表值）

        // 考古自 L2-3 开放（unlocks.features 含 archaeology）——回收时代与解锁关一致
        using JsonDocument e2 = JsonDocument.Parse(ReadSibling("levels", "e2.json"));
        JsonElement level3 = e2.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "campaign:level-e2-03");
        JsonElement unlocks = level3.GetProperty("unlocks");
        Assert.Contains(
            "archaeology",
            unlocks.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        // L2-3 有 dig_sites 两处（E1 壁画的物理挖出位）
        Assert.Equal(2, level3.GetProperty("map").GetProperty("dig_sites").GetArrayLength());
    }

    [Fact] // legal — hook↔remnant 双向链：钩子 linked_remanent 都在残卷表；壁画钩子链到壁画残卷
    public void Hook_remanent_links_resolve_both_ways()
    {
        CrossEraHookRegistry registry = Hooks();
        RemnantCatalog catalog = Remnants();

        CrossEraHook mural = registry.Items[0];
        KnowledgeRemnant muralRemnant = Assert.IsType<KnowledgeRemnant>(catalog.Find(mural.LinkedRemnant!));
        Assert.Equal(mural.Id, muralRemnant.BeatId); // 双向：残卷的 beat_id 指回钩子
        Assert.Contains("壁画", muralRemnant.MemoryZh);

        foreach (CrossEraHook hook in registry.Items.Where(hook => hook.LinkedRemnant is not null))
        {
            Assert.NotNull(catalog.Find(hook.LinkedRemnant!));
        }
    }

    [Fact] // legal — 谱系锚：E5 回声低语钩子的回收=忠诚反转（写死方向，防后续漂移）
    public void The_echo_hook_payoff_is_pinned_to_the_loyalty_reversal()
    {
        CrossEraHook echo = Hooks().Items[4];

        Assert.Contains("忠诚", echo.PayoffZh);
        Assert.Contains("不搞背叛骗局", echo.PayoffZh); // §12.1 谱系写死
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 钩子规模漂移：4 条/6 条拒绝（E1~E5 每时代一条）
    public void A_hook_count_drift_is_rejected()
    {
        Assert.Contains("恰好 5 条", Assert.Throws<NarrativeSchemaException>(
            () => CrossEraHookRegistry.Parse(HookJson(4), "inline")).Message);
        Assert.Contains("恰好 5 条", Assert.Throws<NarrativeSchemaException>(
            () => CrossEraHookRegistry.Parse(HookJson(6), "inline")).Message);
    }

    [Fact] // illegal — archaeology 回收缺 channel / 未登记层 / 未登记回收 kind / era 重复：拒绝
    public void Hook_schema_violations_are_rejected()
    {
        string noChannel = HookJson(5, hook0Recover: """{"era": 2, "kind": "archaeology_channel"}""");
        Assert.Contains("必须带 channel", Assert.Throws<NarrativeSchemaException>(
            () => CrossEraHookRegistry.Parse(noChannel, "inline")).Message);

        string badLayer = HookJson(5, hook0Layer: "npc_face");
        Assert.Contains("未登记 layer", Assert.Throws<NarrativeSchemaException>(
            () => CrossEraHookRegistry.Parse(badLayer, "inline")).Message);

        string badRecover = HookJson(5, hook0Recover: """{"era": 2, "kind": "dig_luck"}""");
        Assert.Contains("未登记 recover.kind", Assert.Throws<NarrativeSchemaException>(
            () => CrossEraHookRegistry.Parse(badRecover, "inline")).Message);

        string dupEra = HookJson(5, hook1Era: 1);
        Assert.Contains("planted_era 1 重复", Assert.Throws<NarrativeSchemaException>(
            () => CrossEraHookRegistry.Parse(dupEra, "inline")).Message);
    }

    // ---- inline scaffold -----------------------------------------------------

    private static string HookJson(
        int count,
        string? hook0Recover = null,
        string? hook0Layer = null,
        int? hook1Era = null)
    {
        var rows = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int era = i == 1 && hook1Era is { } pinned ? pinned : i + 1;
            string recover = i == 0 && hook0Recover is not null
                ? hook0Recover
                : $$"""{"era": {{Math.Min(era + 1, 5)}}, "kind": "chapter_progress"}""";
            string layer = i == 0 && hook0Layer is not null ? hook0Layer : "system_memory";
            rows.Add($$"""
                {"id": "narrative:hook-x-{{i}}", "ordinal": {{i + 1}}, "title_zh": "钩子{{i}}",
                 "planted_era": {{era}}, "planted_level_id": null, "planted_anchor_zh": "锚{{i}}",
                 "teaser_zh": "t{{i}}", "recover": {{recover}}, "payoff_zh": "p{{i}}",
                 "layer": "{{layer}}", "linked_remanent": null}
                """);
        }

        return $$"""
            {"content_type": "CrossEraHookTable", "items": [{{string.Join(",", rows)}}]}
            """;
    }
}
