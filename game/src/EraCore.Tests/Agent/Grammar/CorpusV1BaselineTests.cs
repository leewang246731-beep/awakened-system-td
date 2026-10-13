using System.Text.Json;
using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Agent.Grammar;

// [F-170] T-061 语料 v1（game/content/grammar/corpus-v1.jsonl，324 条=中 162/英 162；
// 种子 corpus-seed.jsonl 60 条已并入去重）。三层守卫：
//   1) 分布守卫——≥200 条、双语均衡、5 已实现意图族 ≥25 条/语言（反例 ≥3）、
//      7 未实现意图 ≥5 条/语言全部 expected_parse:failure；
//   2) 行为守卫——缺口意图行必须落 subset_gap 澄清键（解析成意图=违约），反例行必须被拒；
//   3) 基线快照——命中数如实钉死（数字由真实 Evaluator 跑出，非设计文档期望值，CS-4.4）。
// 钉死纪律：未来 parser 改进只允许升不允许降；任何钉死值变更必须同步更新语料与
// docs/engineering/16-t061-corpus-baseline.md 并过审（CS-6 金样纪律的语料版）。
// [F-170] 类级关联：本类全部测试方法覆盖 T-061 语料 v1 基线（下述三层守卫）。
public class CorpusV1BaselineTests
{
    private readonly ITestOutputHelper _output;

    public CorpusV1BaselineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string CorpusFile = "corpus-v1.jsonl";

    private static readonly IntentKind[] GapIntents =
    {
        IntentKind.Upgrade,
        IntentKind.Sell,
        IntentKind.FocusFire,
        IntentKind.ChangeFocusMode,
        IntentKind.RerouteLogistics,
        IntentKind.Patrol,
        IntentKind.ScrollControl,
    };

    private sealed record RawRow(
        string Text, string Lang, string? Intent, string? Family, string? GapIntent, string? ExpectKey, bool ExpectSuccess);

    private static IReadOnlyList<CorpusEntry> LoadCorpus()
        => CorpusChannel.ParseJsonl(GrammarTestPaths.ReadGrammarFile(CorpusFile));

    private static IReadOnlyList<RawRow> LoadRawRows()
    {
        var rows = new List<RawRow>();
        foreach (var line in GrammarTestPaths.ReadGrammarFile(CorpusFile).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string? Optional(string name)
                => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;

            rows.Add(new RawRow(
                root.GetProperty("text").GetString()!,
                root.GetProperty("lang").GetString()!,
                Optional("intent"),
                Optional("family"),
                Optional("gap_intent"),
                Optional("expect_key"),
                string.Equals(root.GetProperty("expected_parse").GetString(), "success", StringComparison.Ordinal)));
        }

        return rows;
    }

    /// <summary>行 → 意图族（成功行=intent；反例行=family 注解；缺口行=gap_intent 注解）。</summary>
    private static string FamilyOf(RawRow row)
        => row.Intent ?? row.GapIntent ?? row.Family ?? "none";

    // ---- 分布守卫 ------------------------------------------------------------

    [Fact] // legal：≥200 条、双语均衡 |zh−en|≤10、5 意图族 ≥25 条/语言、反例 ≥3/族/语言、7 缺口意图 ≥5/语言
    public void Corpus_v1_meets_distribution_requirements()
    {
        var rows = LoadRawRows();

        Assert.True(rows.Count >= 200, $"corpus has {rows.Count} rows, need >= 200");
        var zh = rows.Count(r => r.Lang == "zh");
        var en = rows.Count(r => r.Lang == "en");
        Assert.True(Math.Abs(zh - en) <= 10, $"bilingual balance broken: zh={zh} en={en}");
        Assert.Equal(rows.Count, zh + en);

        foreach (var kind in L1IntentParser.SupportedIntents)
        {
            var name = kind.ToString();
            foreach (var lang in new[] { "zh", "en" })
            {
                var familyRows = rows.Count(r => r.Lang == lang && FamilyOf(r) == name);
                Assert.True(familyRows >= 25, $"{lang} {name} family has {familyRows} rows, need >= 25");

                var negatives = rows.Count(r => r.Lang == lang && FamilyOf(r) == name && !r.ExpectSuccess);
                Assert.True(negatives >= 3, $"{lang} {name} has {negatives} negative rows, need >= 3");
            }
        }

        foreach (var gap in GapIntents)
        {
            var name = gap.ToString();
            Assert.DoesNotContain(gap, L1IntentParser.SupportedIntents);
            foreach (var lang in new[] { "zh", "en" })
            {
                var gapRows = rows.Where(r => r.Lang == lang && r.GapIntent == name).ToList();
                Assert.True(gapRows.Count >= 5, $"{lang} gap {name} has {gapRows.Count} rows, need >= 5");
                Assert.All(gapRows, r => Assert.False(r.ExpectSuccess, $"gap row must expect failure: {r.Text}"));
                Assert.All(gapRows, r => Assert.Equal(L1IntentParser.SubsetGapClarifyingKey, r.ExpectKey));
            }
        }
    }

    [Fact] // legal：去重纪律——种子并入后归一化文本无重复（同一 TextNormalizer 管道）
    public void Corpus_v1_rows_are_unique_after_normalization()
    {
        var seen = new HashSet<string>();
        foreach (var row in LoadRawRows())
        {
            var key = $"{row.Lang}|{TextNormalizer.Normalize(row.Text)}";
            Assert.True(seen.Add(key), $"duplicate corpus row after normalization: '{row.Text}' [{row.Lang}]");
        }
    }

    // ---- 注解与行为守卫 -------------------------------------------------------

    [Fact] // boundary：failure 行必须携带合法 expect_key；family 只允许冻结意图名或 "none"
    public void Failure_row_annotations_are_wellformed()
    {
        var rows = LoadRawRows();
        var knownKeys = new[] { L1IntentParser.DefaultClarifyingKey, L1IntentParser.SubsetGapClarifyingKey };
        var knownFamilies = GapIntents.Concat(L1IntentParser.SupportedIntents).Select(k => k.ToString()).Append("none").ToHashSet();

        var failures = rows.Where(r => !r.ExpectSuccess).ToList();
        Assert.NotEmpty(failures);
        foreach (var row in failures)
        {
            Assert.Contains(row.ExpectKey, knownKeys);
            Assert.Contains(FamilyOf(row), knownFamilies);
        }

        // 成功行不允许带 expect_key（成功期望与失败期望互斥，注解纪律）
        foreach (var row in rows.Where(r => r.ExpectSuccess))
        {
            Assert.Null(row.ExpectKey);
        }
    }

    [Fact] // illegal：7 缺口意图行——解析器必须拒绝且失败键=subset_gap（解析成意图或落 unparsed 均违约）
    public void Gap_intent_rows_resolve_to_subset_gap()
    {
        var parser = L1IntentParserTests.CreateParser();
        var gapRows = LoadRawRows().Where(r => r.GapIntent is not null).ToList();

        Assert.NotEmpty(gapRows);
        foreach (var row in gapRows)
        {
            var result = parser.Parse(row.Text);

            Assert.Null(result.Intent);
            Assert.NotNull(result.Failure);
            Assert.Equal(L1IntentParser.SubsetGapClarifyingKey, result.Failure!.ClarifyingKey);
        }
    }

    [Fact] // illegal：已实现意图的反例行必须被拒，且失败键与行内 expect_key 注解一致
    public void Negative_rows_fail_with_annotated_key()
    {
        var parser = L1IntentParserTests.CreateParser();
        var negatives = LoadRawRows().Where(r => !r.ExpectSuccess && r.GapIntent is null).ToList();

        Assert.NotEmpty(negatives);
        foreach (var row in negatives)
        {
            var result = parser.Parse(row.Text);

            Assert.Null(result.Intent);
            Assert.NotNull(result.Failure);
            Assert.Equal(row.ExpectKey, result.Failure!.ClarifyingKey);
        }
    }

    // ---- 基线快照（钉死） -----------------------------------------------------

    [Fact] // [F-170] 基线快照：总数/命中数/命中率（总体+分语言）钉死——数字如实，升不降
    public void Baseline_hit_counts_are_pinned()
    {
        var report = CorpusChannel.Evaluate(L1IntentParserTests.CreateParser(), LoadCorpus());
        var rows = LoadRawRows();

        _output.WriteLine($"corpus v1: total={report.Total} hits={report.Hits} rate={report.Rate:F4} " +
                          $"zh={report.RateForLang("zh"):F4} en={report.RateForLang("en"):F4}");

        Assert.Equal(324, report.Total);
        Assert.Equal(162, report.TotalForLang("zh"));
        Assert.Equal(162, report.TotalForLang("en"));
        Assert.Equal(296, report.Hits); // 基线快照（16-t061 报告 §3）——只允许升不允许降
        Assert.Equal(147, report.HitsForLang("zh"));
        Assert.Equal(149, report.HitsForLang("en"));
        Assert.Equal(0.9136, report.Rate, 4);
        Assert.Equal(0.9074, report.RateForLang("zh"), 4);
        Assert.Equal(0.9198, report.RateForLang("en"), 4);

        // 分意图族钉死（rows/hits，族=成功行 intent+反例行 family+缺口行 gap_intent）
        var evaluated = report.Rows.ToList();
        var pinned = new (string Lang, string Family, int Rows, int Hits)[]
        {
            ("zh", "HoldRegion", 25, 21),
            ("zh", "Build", 25, 22),
            ("zh", "QueryThreat", 25, 21),
            ("zh", "QueryStockpile", 25, 22),
            ("zh", "SetAlarm", 25, 24),
            ("en", "HoldRegion", 25, 20),
            ("en", "Build", 25, 23),
            ("en", "QueryThreat", 25, 22),
            ("en", "QueryStockpile", 25, 23),
            ("en", "SetAlarm", 25, 24),
        };
        for (var i = 0; i < evaluated.Count; i++)
        {
            Assert.Equal(rows[i].Text, evaluated[i].Entry.Text); // 行序对齐（同文件同序）
        }

        foreach (var (lang, family, expectedRows, expectedHits) in pinned)
        {
            var indices = Enumerable.Range(0, rows.Count).Where(i => rows[i].Lang == lang && FamilyOf(rows[i]) == family).ToList();
            Assert.Equal(expectedRows, indices.Count);
            Assert.Equal(expectedHits, indices.Count(i => evaluated[i].Hit));
        }
    }

    [Fact] // 诊断：miss 逐条清单钉死（28 条=15 zh+13 en；分类见 16-t061 报告 §4）
    public void Miss_inventory_is_pinned()
    {
        var report = CorpusChannel.Evaluate(L1IntentParserTests.CreateParser(), LoadCorpus());
        var rows = LoadRawRows();
        var misses = report.Rows.Where(r => !r.Hit).ToList();

        foreach (var miss in misses)
        {
            var row = rows[miss.Line - 1];
            _output.WriteLine($"MISS #{miss.Line} [{row.Lang}/{FamilyOf(row)}] {row.Text}: {miss.Detail}");
        }

        Assert.Equal(28, misses.Count);
        Assert.Equal(15, misses.Count(m => m.Entry.Lang == "zh"));
        Assert.Equal(13, misses.Count(m => m.Entry.Lang == "en"));
        Assert.All(misses, m => Assert.False(m.Entry.ExpectSuccess && m.Detail == "ok"));
    }
}
