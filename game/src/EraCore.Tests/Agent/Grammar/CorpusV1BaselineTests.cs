using System.Text.Json;
using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Agent.Grammar;

// [F-170] T-061 语料 v1（game/content/grammar/corpus-v1.jsonl，324 条=中 162/英 162；
// 种子 corpus-seed.jsonl 60 条已并入去重）。语料文件本身钉死不改（CS-6 语料版
// 金样纪律）；本类的三层守卫随 T-069（12 意图全量）按下列口径重钉：
//   1) 分布守卫——≥200 条、双语均衡、5 个 T-052 意图族 ≥25 条/语言（反例 ≥3）、
//      7 个缺口意图族 ≥5 条/语言；
//   2) 行为守卫——缺口意图行（T-061 时 expected_parse:failure+subset_gap）
//      在 T-069 后必须解析为行内 gap_intent 注解的意图（历史失败期望与新解析
//      器的口径切换）；反例行（15×2 意图反例+垃圾 2×2）必须仍被拒；
//   3) 基线快照——命中数如实钉死（数字由真实解析跑出，非设计文档期望值，
//      CS-4.4）。T-061 基线 296/324（91.36%）→ T-069 重钉 324/324（28 条正例
//      miss 全修复 + 70 条缺口行转解析成功；zh 147→162、en 149→162）。
// 未来 parser 改进同样只允许升不允许降；钉死值变更必须同步 16-t061 报告并过审。
// [F-170] 类级关联：本类全部测试方法覆盖语料 v1 基线（上述三层守卫）。
public class CorpusV1BaselineTests
{
    private readonly ITestOutputHelper _output;

    public CorpusV1BaselineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string CorpusFile = "corpus-v1.jsonl";

    /// <summary>T-061 时代 7 缺口意图的子集澄清键——语料行 expect_key 注解的历史值（T-069 后解析器不再产生）。</summary>
    private const string HistoricalSubsetGapKey = "agent.clarify.subset_gap";

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

    /// <summary>T-052 五意图子集（T-069 前 SupportedIntents 的原值；语料分布守卫仍按它计量）。</summary>
    private static readonly IntentKind[] T052Subset =
    {
        IntentKind.HoldRegion,
        IntentKind.Build,
        IntentKind.QueryThreat,
        IntentKind.QueryStockpile,
        IntentKind.SetAlarm,
    };

    /// <summary>
    /// 缺口行解析用的指认上下文（语料行「这个塔卖掉」是指认句；评测给固定
    /// 上下文，presentation 接鼠标前的纯逻辑口径）。
    /// </summary>
    private static readonly PointingContext CorpusPointing =
        new(SelectedId: "era1:tower-campfire", SelectedCellX: 3, SelectedCellY: 4, CameraCellX: 12, CameraCellY: 34);

    private sealed record RawRow(
        string Text,
        string Lang,
        string? Intent,
        string? Family,
        string? GapIntent,
        string? ExpectKey,
        bool ExpectSuccess,
        IReadOnlyDictionary<string, string>? Slots);

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

            IReadOnlyDictionary<string, string>? slots = null;
            if (root.TryGetProperty("slots", out var slotsElement) && slotsElement.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in slotsElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        map[property.Name] = property.Value.GetString()!;
                    }
                }

                slots = map;
            }

            rows.Add(new RawRow(
                root.GetProperty("text").GetString()!,
                root.GetProperty("lang").GetString()!,
                Optional("intent"),
                Optional("family"),
                Optional("gap_intent"),
                Optional("expect_key"),
                string.Equals(root.GetProperty("expected_parse").GetString(), "success", StringComparison.Ordinal),
                slots));
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

        foreach (var kind in T052Subset)
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
            Assert.Contains(gap, L1IntentParser.SupportedIntents); // T-069: 12 意图全量
            foreach (var lang in new[] { "zh", "en" })
            {
                var gapRows = rows.Where(r => r.Lang == lang && r.GapIntent == name).ToList();
                Assert.True(gapRows.Count >= 5, $"{lang} gap {name} has {gapRows.Count} rows, need >= 5");
                // 历史注解钉死：这些行在语料文件里仍是 failure+subset_gap 期望（T-061 时代口径）
                Assert.All(gapRows, r => Assert.False(r.ExpectSuccess, $"historical gap row must stay failure-annotated: {r.Text}"));
                Assert.All(gapRows, r => Assert.Equal(HistoricalSubsetGapKey, r.ExpectKey));
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
        var knownKeys = new[] { L1IntentParser.DefaultClarifyingKey, HistoricalSubsetGapKey };
        var knownFamilies = GapIntents.Concat(T052Subset).Select(k => k.ToString()).Append("none").ToHashSet();

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

    [Fact] // legal：7 缺口意图行（T-061 失败期望）在 T-069 全量文法下解析为注解的冻结意图
    public void Gap_intent_rows_parse_to_their_annotated_intent()
    {
        var parser = L1IntentParserTests.CreateParser();
        var gapRows = LoadRawRows().Where(r => r.GapIntent is not null).ToList();

        Assert.Equal(70, gapRows.Count);
        foreach (var row in gapRows)
        {
            var result = parser.Parse(row.Text, CorpusPointing);

            Assert.NotNull(result.Intent);
            Assert.Equal(Enum.Parse<IntentKind>(row.GapIntent!), result.Intent!.Kind);
            // 槽位键必须在 SP-2 该意图的槽位表内（槽位值语义由覆盖矩阵测试逐条钉死）
            Assert.All(
                result.Intent.Slots.Keys,
                slot => Assert.True(
                    GrammarSlots.IsAllowed(result.Intent.Kind, slot),
                    $"{row.Text}: slot '{slot}' outside SP-2 vocabulary of {result.Intent.Kind}"));
        }
    }

    [Fact] // illegal：已实现意图的反例行必须被拒，且失败键与行内 expect_key 注解一致
    public void Negative_rows_fail_with_annotated_key()
    {
        var parser = L1IntentParserTests.CreateParser();
        var negatives = LoadRawRows().Where(r => !r.ExpectSuccess && r.GapIntent is null).ToList();

        Assert.Equal(34, negatives.Count);
        foreach (var row in negatives)
        {
            var result = parser.Parse(row.Text);

            Assert.Null(result.Intent);
            Assert.NotNull(result.Failure);
            Assert.Equal(row.ExpectKey, result.Failure!.ClarifyingKey);
        }
    }

    // ---- 基线快照（钉死） -----------------------------------------------------

    /// <summary>
    /// T-069 口径的行级命中判定（与 CorpusChannel.Evaluate 同严格度，多一层缺口
    /// 行的口径切换）：成功行=意图 kind 相同且槽位图逐键逐值相同；缺口行=解析
    /// 成功且 kind=gap_intent 注解；反例行=解析失败且失败键与注解一致。
    /// </summary>
    private static List<(RawRow Row, bool Hit, string Detail)> EvaluateRows(L1IntentParser parser)
    {
        var results = new List<(RawRow, bool, string)>();
        foreach (var row in LoadRawRows())
        {
            if (row.GapIntent is not null)
            {
                var parsed = parser.Parse(row.Text, CorpusPointing);
                var hit = parsed.Intent is not null
                    && parsed.Intent.Kind == Enum.Parse<IntentKind>(row.GapIntent);
                results.Add((row, hit, hit ? "ok" : $"gap row failed to parse as {row.GapIntent}"));
                continue;
            }

            if (!row.ExpectSuccess)
            {
                var parsed = parser.Parse(row.Text);
                var hit = parsed.Intent is null && parsed.Failure?.ClarifyingKey == row.ExpectKey;
                results.Add((row, hit, hit ? "ok" : $"negative row not rejected with {row.ExpectKey}"));
                continue;
            }

            var success = parser.Parse(row.Text);
            if (success.Intent is null)
            {
                results.Add((row, false, $"expected {row.Intent}, got failure '{success.Failure?.ClarifyingKey}'"));
                continue;
            }

            if (success.Intent.Kind != Enum.Parse<IntentKind>(row.Intent!))
            {
                results.Add((row, false, $"expected {row.Intent}, got {success.Intent.Kind}"));
                continue;
            }

            var expected = row.Slots ?? new Dictionary<string, string>();
            var actual = success.Intent.Slots;
            if (expected.Count != actual.Count)
            {
                results.Add((row, false, $"slot count expected {expected.Count}, got {actual.Count}"));
                continue;
            }

            var slotMiss = expected.FirstOrDefault(kv =>
                !actual.TryGetValue(kv.Key, out var value) || value != kv.Value);
            if (slotMiss.Key is not null)
            {
                results.Add((row, false, $"slot '{slotMiss.Key}' expected '{slotMiss.Value}', got '{actual.GetValueOrDefault(slotMiss.Key)}'"));
                continue;
            }

            results.Add((row, true, "ok"));
        }

        return results;
    }

    [Fact] // [F-170] 基线快照重钉（T-069）：324/324 全命中——T-061 基线 296/324（91.36%）只升不降政策的落地
    public void Baseline_hit_counts_are_pinned()
    {
        var evaluated = EvaluateRows(L1IntentParserTests.CreateParser());
        var totalHits = evaluated.Count(e => e.Hit);
        var zhHits = evaluated.Count(e => e.Hit && e.Row.Lang == "zh");
        var enHits = evaluated.Count(e => e.Hit && e.Row.Lang == "en");

        _output.WriteLine(
            $"corpus v1 (T-069): total={evaluated.Count} hits={totalHits} zh={zhHits} en={enHits}");

        Assert.Equal(324, evaluated.Count);
        Assert.Equal(162, evaluated.Count(e => e.Row.Lang == "zh"));
        Assert.Equal(162, evaluated.Count(e => e.Row.Lang == "en"));
        Assert.Equal(324, totalHits); // 基线重钉：296 → 324（28 miss 修复 + 70 缺口行转解析）
        Assert.Equal(162, zhHits);    // zh 147 → 162
        Assert.Equal(162, enHits);    // en 149 → 162

        // 分意图族钉死（族=成功行 intent+反例行 family+缺口行 gap_intent）：
        // T-052 五族=22 正例+3 反例=25 行；7 缺口族=5 行/语言（T-061 §1.2 分布）
        foreach (var family in T052Subset.Select(k => k.ToString()))
        {
            foreach (var lang in new[] { "zh", "en" })
            {
                var rowsOfFamily = evaluated.Where(e => e.Row.Lang == lang && FamilyOf(e.Row) == family).ToList();
                Assert.Equal(25, rowsOfFamily.Count);
                Assert.Equal(25, rowsOfFamily.Count(e => e.Hit));
            }
        }

        foreach (var family in GapIntents.Select(k => k.ToString()))
        {
            foreach (var lang in new[] { "zh", "en" })
            {
                var rowsOfFamily = evaluated.Where(e => e.Row.Lang == lang && FamilyOf(e.Row) == family).ToList();
                Assert.Equal(5, rowsOfFamily.Count);
                Assert.Equal(5, rowsOfFamily.Count(e => e.Hit));
            }
        }
    }

    [Fact] // 诊断：miss 清单钉死——T-061 的 28 条 miss 在 T-069 后归零（只升不降政策的见证断言）
    public void Miss_inventory_is_pinned()
    {
        var evaluated = EvaluateRows(L1IntentParserTests.CreateParser());
        var misses = evaluated.Where(e => !e.Hit).ToList();

        foreach (var miss in misses)
        {
            _output.WriteLine($"MISS [{miss.Row.Lang}/{FamilyOf(miss.Row)}] {miss.Row.Text}: {miss.Detail}");
        }

        Assert.Empty(misses); // T-061 基线 28 条（zh 15 + en 13）→ T-069 全修复
    }
}
