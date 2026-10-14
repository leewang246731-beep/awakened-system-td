using System.Text.Json;
using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Agent.Grammar;

// [F-170] 军令语料库与首解率评测（T-052 语料通道；T-061 扩容 ≥200 条出基线）。
// 三层：jsonl 装载 → 评估器正确性 → 种子语料回归（60 条，首解率 ≥80% 落断言）。
public class CorpusChannelTests
{
    private readonly ITestOutputHelper _output;

    public CorpusChannelTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ---- jsonl 装载 ---------------------------------------------------------

    [Fact] // legal：两行完整语料
    public void ParseJsonl_loads_success_and_failure_rows()
    {
        const string jsonl = """
            {"text": "守住东门", "lang": "zh", "intent": "HoldRegion", "slots": {"target": "east_gate"}, "expected_parse": "success"}
            {"text": "呃呃", "lang": "zh", "expected_parse": "failure"}
            """;

        var entries = CorpusChannel.ParseJsonl(jsonl);

        Assert.Equal(2, entries.Count);
        Assert.Equal(IntentKind.HoldRegion, entries[0].Intent);
        Assert.Equal("east_gate", entries[0].Slots!["target"]);
        Assert.True(entries[0].ExpectSuccess);
        Assert.False(entries[1].ExpectSuccess);
        Assert.Null(entries[1].Intent);
    }

    [Fact] // boundary：空行/尾随换行跳过，不产生条目
    public void ParseJsonl_skips_blank_lines()
    {
        const string jsonl = """
            {"text": "ok", "lang": "en", "expected_parse": "failure"}

            """;

        var entries = CorpusChannel.ParseJsonl(jsonl + "\n");

        Assert.Single(entries);
    }

    [Theory] // illegal：坏行必须红且带行号
    [InlineData("{oops}", "line 1")]
    [InlineData("{\"text\": \"a\", \"lang\": \"zh\"}", "line 1")]                                    // 缺 expected_parse
    [InlineData("{\"text\": \"a\", \"lang\": \"zh\", \"expected_parse\": \"maybe\"}", "line 1")]      // 未知期望
    [InlineData("{\"text\": \"a\", \"lang\": \"zh\", \"intent\": \"Teleport\", \"expected_parse\": \"success\"}", "line 1")] // 清单外意图
    [InlineData("{\"lang\": \"zh\", \"expected_parse\": \"failure\"}", "line 1")]                    // 缺 text
    public void ParseJsonl_rejects_damaged_rows(string line, string expectedMessagePart)
    {
        var exception = Assert.Throws<JsonException>(() => CorpusChannel.ParseJsonl(line));

        Assert.Contains(expectedMessagePart, exception.Message);
    }

    [Fact] // illegal：第二行损坏 → 行号指向第 2 行
    public void ParseJsonl_reports_damaged_line_number()
    {
        const string jsonl = """
            {"text": "ok", "lang": "zh", "expected_parse": "failure"}
            {bad}
            """;

        var exception = Assert.Throws<JsonException>(() => CorpusChannel.ParseJsonl(jsonl));

        Assert.Contains("line 2", exception.Message);
    }

    // ---- 评估器正确性（手造微语料 + 白名单 Fake 解析器，CS-3.4） ------------

    private sealed record ScriptedParser(IntentParseResult Result) : IIntentParser
    {
        public IntentParseResult Parse(string text) => Result;
    }

    [Fact] // legal：命中=意图相同+槽位逐一相同
    public void Evaluate_counts_slot_exact_matches_as_hits()
    {
        var intent = new Intent(IntentKind.Build, "建塔", new Dictionary<string, string>
        {
            ["tower"] = "era1:tower-wood-arrow",
            ["qty"] = "2",
        });
        var entries = new List<CorpusEntry>
        {
            new("建塔", "zh", IntentKind.Build, new Dictionary<string, string>
            {
                ["tower"] = "era1:tower-wood-arrow",
                ["qty"] = "2",
            }, ExpectSuccess: true),
        };

        var report = CorpusChannel.Evaluate(new ScriptedParser(new IntentParseResult(intent, null)), entries);

        Assert.Equal(1, report.Hits);
        Assert.Equal(1.0, report.Rate, 5);
    }

    [Fact] // illegal：槽位值不同/键数不同/意图不同 → miss 且 detail 说明原因
    public void Evaluate_counts_slot_mismatches_as_misses()
    {
        var intent = new Intent(IntentKind.Build, "x", new Dictionary<string, string> { ["qty"] = "3" });
        var parser = new ScriptedParser(new IntentParseResult(intent, null));
        var wrongValue = new CorpusEntry("x", "zh", IntentKind.Build, new Dictionary<string, string> { ["qty"] = "2" }, true);
        var wrongCount = new CorpusEntry("x", "zh", IntentKind.Build, new Dictionary<string, string>
        {
            ["qty"] = "3",
            ["tower"] = "era1:tower-wood-arrow",
        }, true);
        var wrongKind = new CorpusEntry("x", "zh", IntentKind.Sell, null, true);

        var report = CorpusChannel.Evaluate(parser, new[] { wrongValue, wrongCount, wrongKind });

        Assert.Equal(0, report.Hits);
        Assert.Equal(3, report.Rows.Count(r => !r.Hit));
        Assert.Contains("expected '2', got '3'", report.Rows[0].Detail);
        Assert.Contains("slot count", report.Rows[1].Detail);
        Assert.Contains("expected Sell, got Build", report.Rows[2].Detail);
    }

    [Fact] // boundary：期望失败行——真失败带话术键=hit；解析成功=miss；空键=miss
    public void Evaluate_checks_failure_rows_for_clarifying_key()
    {
        var failure = new IntentParseResult(null, new IntentParseFailure("x", "agent.clarify.unparsed"));
        var emptyKey = new IntentParseResult(null, new IntentParseFailure("x", ""));
        var success = new IntentParseResult(
            new Intent(IntentKind.Build, "x", new Dictionary<string, string>()), null);
        var entries = new[]
        {
            new CorpusEntry("a", "zh", null, null, ExpectSuccess: false),
            new CorpusEntry("b", "zh", null, null, ExpectSuccess: false),
            new CorpusEntry("c", "zh", null, null, ExpectSuccess: false),
        };

        var hitFailure = CorpusChannel.Evaluate(new ScriptedParser(failure), new[] { entries[0] });
        var missEmptyKey = CorpusChannel.Evaluate(new ScriptedParser(emptyKey), new[] { entries[1] });
        var missSuccess = CorpusChannel.Evaluate(new ScriptedParser(success), new[] { entries[2] });

        Assert.Equal(1, hitFailure.Hits);
        Assert.Equal(0, missEmptyKey.Hits);
        Assert.Equal(0, missSuccess.Hits);
    }

    // ---- 种子语料回归（60 条，首解率数字落断言） -----------------------------
    // 注：corpus-seed.jsonl 是 T-052 时代的历史样本（语料 v1 已并入去重，v1 为
    // 基线权威）。其中 6 条 expected_parse:failure 行是当时 7 缺口意图的自然
    // 军令——T-069 全量 12 意图后它们解析成功，按 Evaluate 口径计 miss 是历史
    // 注解与新解析器的预期错位，此处如实钉死（不改种子文件——历史数据钉死纪律）。

    private static readonly string[] SeedGapRowsNowParsing =
    {
        "把箭楼升到三级",
        "卖掉北墙的旧塔",
        "集火那个攻城锤",
        "upgrade the arrow tower to tier 3",
        "sell the old tower on the north wall",
        "focus the ram",
    };

    [Fact] // legal：种子语料 60 条入库：中英各 30，T-052 5 意图 × 每语言 5 正例 + 5 反例
    public void Seed_corpus_has_expected_shape()
    {
        var entries = LoadSeed();

        Assert.Equal(60, entries.Count);
        Assert.Equal(30, entries.Count(e => e.Lang == "zh"));
        Assert.Equal(30, entries.Count(e => e.Lang == "en"));
        var t052Subset = new[]
        {
            IntentKind.HoldRegion,
            IntentKind.Build,
            IntentKind.QueryThreat,
            IntentKind.QueryStockpile,
            IntentKind.SetAlarm,
        };
        foreach (var kind in t052Subset)
        {
            Assert.Equal(5, entries.Count(e => e.Lang == "zh" && e.Intent == kind));
            Assert.Equal(5, entries.Count(e => e.Lang == "en" && e.Intent == kind));
        }

        Assert.Equal(10, entries.Count(e => !e.ExpectSuccess));
    }

    [Fact] // [F-170] 首解率报告：≥80%/语言兜底不变；6 条缺口时代失败行如今解析成功（T-069）
    public void Seed_corpus_first_parse_rate_meets_floor()
    {
        var entries = LoadSeed();
        var report = CorpusChannel.Evaluate(L1IntentParserTests.CreateParser(), entries);

        _output.WriteLine($"seed corpus: total={report.Total} hits={report.Hits} rate={report.Rate:F3} " +
                          $"zh={report.RateForLang("zh"):F3} en={report.RateForLang("en"):F3}");
        foreach (var miss in report.Rows.Where(r => !r.Hit))
        {
            _output.WriteLine($"MISS line {miss.Line} [{miss.Entry.Lang}] {miss.Entry.Text}: {miss.Detail}");
        }

        Assert.True(report.Rate >= 0.80, $"overall first-parse rate {report.Rate:F3} < 0.80");
        Assert.True(report.RateForLang("zh") >= 0.80, $"zh rate {report.RateForLang("zh"):F3} < 0.80");
        Assert.True(report.RateForLang("en") >= 0.80, $"en rate {report.RateForLang("en"):F3} < 0.80");

        // T-069 过渡期如实钉死：仅有的 miss = 6 条历史缺口行解析成功（54/60）
        var misses = report.Rows.Where(r => !r.Hit).Select(r => r.Entry.Text).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(SeedGapRowsNowParsing.OrderBy(t => t, StringComparer.Ordinal), misses);
        Assert.Equal(54, report.Hits);
    }

    private static IReadOnlyList<CorpusEntry> LoadSeed()
        => CorpusChannel.ParseJsonl(GrammarTestPaths.ReadGrammarFile("corpus-seed.jsonl"));
}
