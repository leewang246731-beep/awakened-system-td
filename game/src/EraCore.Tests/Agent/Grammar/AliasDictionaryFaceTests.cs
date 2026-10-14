using System.Text.Json;
using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [F-077][F-170] T-090 DEBT 清偿的执法面（08-task-plan T-090 行尾 DEBT：
// 「别名词典封闭面扩容——AliasDictionary 现仅自 towers/enemies 内容装载，
// 卷轴/分区/资源等玩家可指名面未入封闭词典…盲测调优时一并扩容并补同源
// 校验断言」）。四条同源校验：
//   ① 资源面——lexicon resources id 集合与 era1/era.json 注册表精确相等；
//   ② 卷轴面——scrolls/era2.json 全部注册表面（含英文多词面）可经封闭词典解析；
//   ③ lexicon 卷轴残留面=钉死的 T-069 预登记三张（fire-oil/frost/strength，
//      corpus-v1 依赖其继续可解析），新漂移即红；
//   ④ 解析器合并——注册面优先 + 纯 CJK 面「卷轴/卷」口语变体生成。
// 分区面无内容注册表（分区 id 由 grammar 层定义、表现层锚点接线），不适用
// 同源断言——登记于 25-t090 报告 §5。
// [F-077] 类级关联：本类全部测试方法覆盖封闭别名词典面扩容+同源校验。
public class AliasDictionaryFaceTests
{
    /// <summary>T-069 时代 lexicon 预登记、注册表尚无对应实体的卷轴 id（钉死：增删即红）。</summary>
    private static readonly IReadOnlySet<string> PinnedLegacyScrollIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "era2:scroll-fire-oil",
        "era2:scroll-frost",
        "era2:scroll-strength",
    };

    [Fact] // legal：资源面同源——lexicon resources id 集合 == era1/era.json 注册表
    public void Lexicon_resource_ids_match_the_era1_registry_exactly()
    {
        var lexiconIds = GrammarData.FromLexicon(GrammarTestPaths.ReadGrammarFile("lexicon.json"))
            .Resources.Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(GrammarTestPaths.Era1EraJson());
        var registryIds = document.RootElement.GetProperty("items")[0]
            .GetProperty("resources")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(lexiconIds);
        Assert.Equal(registryIds, lexiconIds); // K-2：集合级精确同源（多一面少一面都红）
    }

    [Fact] // legal：卷轴注册面全量入典——每张卷轴的每个别面（含英文多词面）都可解析
    public void Scrolls_registry_faces_load_into_the_closed_dictionary()
    {
        var scrolls = AliasDictionary.FromContentRegistryJson(GrammarTestPaths.Era2ScrollsJson());

        using var document = JsonDocument.Parse(GrammarTestPaths.Era2ScrollsJson());
        var expectedCount = 0;
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            foreach (var rawSurface in EnumerateSurfaces(item))
            {
                expectedCount++;
                var surface = TextNormalizer.Normalize(rawSurface); // 词典键=归一形（英文多词面去空格）
                Assert.True(
                    scrolls.TryResolve(surface, out var resolved),
                    $"scroll surface '{rawSurface}' of {id} 不在封闭词典");
                Assert.Equal(id, resolved);
            }
        }

        Assert.True(expectedCount >= 14); // 7 卷轴 × (name_zh+name_en/alias) 下界
    }

    [Fact] // legal：lexicon 卷轴残留面=注册表 ∪ 钉死三张（corpus-v1 依赖）；漂移即红
    public void Lexicon_scroll_ids_stay_within_registry_plus_pinned_legacy()
    {
        var lexiconIds = GrammarData.FromLexicon(GrammarTestPaths.ReadGrammarFile("lexicon.json"))
            .Scrolls.Select(entry => entry.Id)
            .Where(id => id != "any") // 通用卷轴面是语法层概念，非注册表实体
            .ToHashSet(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(GrammarTestPaths.Era2ScrollsJson());
        var registryIds = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var residue = lexiconIds.Except(registryIds).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(PinnedLegacyScrollIds, residue); // 残留面恰好=钉死三张：新预登记/漏清理都红
        Assert.All(lexiconIds.Intersect(registryIds), id => Assert.NotEqual(id, string.Empty));
    }

    [Fact] // legal：解析器合并卷轴面——注册面可下令 + CJK 面口语后缀变体生成
    public void Parser_merges_scroll_registry_faces_with_cjk_variants()
    {
        var parser = L1IntentParserTests.CreateParser();

        // 注册面（裸名 + 后缀变体 + 英文多词面）全部可下令。
        Assert.Equal("era2:scroll-alarm-string", ScrollOf(parser, "开警弦卷轴")); // 警弦+卷轴 变体
        Assert.Equal("era2:scroll-beacon", ScrollOf(parser, "把烽火开了"));       // 裸注册面
        Assert.Equal("era2:scroll-press-gang", ScrollOf(parser, "启动民夫征调"));
        Assert.Equal("era2:scroll-spare-blades", ScrollOf(parser, "用备刃"));
        Assert.Equal("era2:scroll-silence", ScrollOf(parser, "开静默卷轴"));
        Assert.Equal("era2:scroll-alarm-string", ScrollOf(parser, "trigger the alarm string"));
        Assert.Equal("era2:scroll-rearguard", ScrollOf(parser, "the rearguard, go"));

        // lexicon 既有面（T-069 预登记）继续可解析——corpus-v1 依赖不破。
        Assert.Equal("era2:scroll-fire-oil", ScrollOf(parser, "开火油卷轴"));
        Assert.Equal("any", ScrollOf(parser, "用卷轴"));
    }

    [Fact] // illegal：跨源卷轴面冲突（lexicon 与注册表同面不同主）→ 构造期拒绝
    public void Conflicting_scroll_surface_between_lexicon_and_registry_throws()
    {
        // 把注册表「警弦」面嫁接到另一张卷轴上 → 与 lexicon 侧无冲突的表面制造跨源冲突。
        string scrolls = GrammarTestPaths.Era2ScrollsJson();
        string crafted = scrolls.Replace("\"警弦\"", "\"火油卷轴\"", StringComparison.Ordinal);
        Assert.NotEqual(scrolls, crafted); // 替换确实命中（否则测试自身失效）

        Assert.Throws<ArgumentException>(() => L1IntentParser.FromJson(
            GrammarTestPaths.ReadGrammarFile("patterns-zh.json"),
            GrammarTestPaths.ReadGrammarFile("patterns-en.json"),
            GrammarTestPaths.ReadGrammarFile("lexicon.json"),
            GrammarTestPaths.Era1TowersJson(),
            GrammarTestPaths.Era1EnemiesJson(),
            crafted));
    }

    private static string ScrollOf(L1IntentParser parser, string order)
    {
        var intent = parser.Parse(order).Intent;

        Assert.NotNull(intent);
        Assert.Equal(IntentKind.ScrollControl, intent!.Kind);
        Assert.True(
            intent.Slots.TryGetValue("scroll", out var scroll) || intent.Slots.TryGetValue("task", out scroll),
            $"{order}: 无 scroll/task 槽");
        return scroll!;
    }

    private static IEnumerable<string> EnumerateSurfaces(JsonElement item)
    {
        foreach (var property in new[] { "name_zh", "name_en" })
        {
            if (item.TryGetProperty(property, out var name) && name.ValueKind == JsonValueKind.String)
            {
                yield return name.GetString()!;
            }
        }

        if (item.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
        {
            foreach (var alias in aliases.EnumerateArray())
            {
                if (alias.ValueKind == JsonValueKind.String)
                {
                    yield return alias.GetString()!;
                }
            }
        }
    }
}
