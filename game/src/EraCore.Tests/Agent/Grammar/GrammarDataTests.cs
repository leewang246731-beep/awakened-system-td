using System.Text.Json;
using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [C-15] 句式/词典数据装载与校验（T-052）：patterns-zh/en.json + lexicon.json
// 是内容（RL7 精神：句式进数据不进代码），装载期严格校验——坏数据抛 JsonException。
public class GrammarDataTests
{
    // ---- 仓库数据装载（回归锚点） ------------------------------------------

    [Theory] // legal
    [InlineData("patterns-zh.json", "zh", "HoldRegion", "hold-zh-01")]
    [InlineData("patterns-en.json", "en", "HoldRegion", "hold-en-01")]
    public void FromPatterns_loads_repo_books(string file, string lang, string intent, string firstId)
    {
        var book = GrammarData.FromPatterns(GrammarTestPaths.ReadGrammarFile(file), lang);

        Assert.Equal(lang, book.Lang);
        Assert.Equal(firstId, book.Patterns[0].Id);
        Assert.Equal(Enum.Parse<IntentKind>(intent), book.Patterns[0].Intent);
        Assert.All(book.Patterns, p => Assert.False(string.IsNullOrEmpty(p.Id)));
    }

    [Fact] // legal：两本书合计 120 条、每个 T-052 子集意图每语言 ≥10 变体（任务卡起步线 4×10）
    public void Repo_books_meet_subset_variant_floor()
    {
        var zh = GrammarData.FromPatterns(GrammarTestPaths.ReadGrammarFile("patterns-zh.json"), "zh");
        var en = GrammarData.FromPatterns(GrammarTestPaths.ReadGrammarFile("patterns-en.json"), "en");

        Assert.Equal(120, zh.Patterns.Count + en.Patterns.Count);
        foreach (var kind in L1IntentParser.SupportedIntents)
        {
            Assert.True(zh.Patterns.Count(p => p.Intent == kind) >= 10, $"{kind} zh variants < 10");
            Assert.True(en.Patterns.Count(p => p.Intent == kind) >= 10, $"{kind} en variants < 10");
        }
    }

    [Fact] // legal：词典装出分区/物资/敌情范围/未收录关键词（资源 id 与 era.json 同源）
    public void FromLexicon_loads_repo_lexicon()
    {
        var lexicon = GrammarData.FromLexicon(GrammarTestPaths.ReadGrammarFile("lexicon.json"));

        Assert.Contains(lexicon.Regions, r => r.Id == "east_gate");
        Assert.Contains(lexicon.Regions, r => r.Id == "center");
        Assert.Equal(
            new[] { "era1:res-food", "era1:res-stone", "era1:res-wood" },
            lexicon.Resources.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Contains(lexicon.ThreatScopes, s => s.Id == "next_wave");
        Assert.Contains(lexicon.ThreatScopes, s => s.Id == "current");
        Assert.Contains(lexicon.DeferredIntents, d => d.Intent == IntentKind.Upgrade);
        Assert.NotEmpty(lexicon.AllItemsSurfaces);
    }

    [Fact] // boundary：字面量句式的常量槽位解析（下一波怕什么 → scope=next_wave）
    public void FromPatterns_parses_constant_slots()
    {
        var book = GrammarData.FromPatterns(GrammarTestPaths.ReadGrammarFile("patterns-zh.json"), "zh");
        var pattern = book.Patterns.Single(p => p.Id == "threat-zh-01");

        Assert.Null(pattern.SlotNames.FirstOrDefault());
        Assert.Equal("next_wave", pattern.ConstantSlots!["scope"]);
    }

    // ---- 装载校验（非法数据必须红） ----------------------------------------

    [Theory] // illegal
    [InlineData("{\"lang\":\"zh\",\"patterns\":[]}", "zh")]                       // 空句式表
    [InlineData("{\"lang\":\"en\",\"patterns\":[{\"id\":\"a\",\"intent\":\"HoldRegion\",\"template\":\"守住{target}\"}]}", "zh")] // lang 不符
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"Teleport\",\"template\":\"守住{target}\"}]}", "zh")]  // 清单外意图
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"HoldRegion\",\"template\":\"守住{bogus}\"}]}", "zh")] // SP-2 外槽位名
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"SetAlarm\",\"template\":\"{after}{reason}\"}]}", "zh")]  // 开放槽位相邻（边界歧义）
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"SetAlarm\",\"template\":\"{reason}{after}\"}]}", "zh")]  // 开放槽位相邻（反向）
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"HoldRegion\",\"template\":\"{target}\"}]}", "zh")]     // 纯槽位无字面量
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"HoldRegion\",\"template\":\"守住{target\",\"slots\":{}}]}", "zh")] // 未闭合
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"HoldRegion\",\"template\":\"守住{target}\",\"slots\":{\"scope\":\"x\"}]}", "zh")] // 常量槽位非法
    [InlineData("{\"lang\":\"zh\",\"patterns\":[{\"id\":\"a\",\"intent\":\"QueryThreat\",\"template\":\"下一波怕什么\",\"slots\":{\"scope\":\"next_wave\"}},{\"id\":\"a\",\"intent\":\"QueryThreat\",\"template\":\"敌情\"}]}", "zh")] // id 重复
    public void FromPatterns_rejects_damaged_books(string json, string lang)
    {
        Assert.Throws<JsonException>(() => GrammarData.FromPatterns(json, lang));
    }

    [Theory] // illegal：词典缺必需节
    [InlineData("{\"regions\":[],\"resources\":[],\"threat_scopes\":[]}")]
    [InlineData("{\"resources\":[{\"id\":\"r\",\"surfaces\":[\"wood\"]}],\"threat_scopes\":[{\"id\":\"s\",\"surfaces\":[\"now\"]}]}")]
    public void FromLexicon_rejects_missing_sections(string json)
    {
        Assert.Throws<JsonException>(() => GrammarData.FromLexicon(json));
    }

    [Fact] // illegal：不是 JSON → JsonException
    public void FromPatterns_rejects_non_json()
    {
        Assert.Throws<JsonException>(() => GrammarData.FromPatterns("not json at all", "zh"));
    }

    [Fact] // legal：GrammarSlots 覆盖全部 12 意图（SP-2 冻结表）
    public void GrammarSlots_cover_all_twelve_intents()
    {
        Assert.Equal(12, Enum.GetValues<IntentKind>().Length);
        Assert.Equal(new[] { "tower", "qty", "where" }, GrammarSlots.Allowed(IntentKind.Build));
        Assert.Equal(new[] { "after", "reason" }, GrammarSlots.Allowed(IntentKind.SetAlarm));
        Assert.True(GrammarSlots.IsAllowed(IntentKind.ScrollControl, "scroll"));
        Assert.False(GrammarSlots.IsAllowed(IntentKind.Build, "scroll"));
    }
}
