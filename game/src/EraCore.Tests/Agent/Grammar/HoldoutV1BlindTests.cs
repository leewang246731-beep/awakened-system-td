using System.Security.Cryptography;
using System.Text.Json;
using EraCore.Agent.Grammar;
using EraCore.Contracts.Intents;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Agent.Grammar;

// [F-077][F-170] T-090 留出集盲测（RL4：≥200 条语料不得进入文法/别名词典——
// 留出集与训练语料物理同目录但内容零交集，去重断言执法；文法/词典装载路径
// 不含 holdout 文件名，反向引用为零）。holdout-v1.jsonl（120 条=中 60/英 60，
// 12 意图 × 5/语言，全部成功期望行）在盲测首跑前一次性写定并 hash 钉死；
// 之后只许 parser/词典侧改进，改留出集内容=作弊，本测试转红（08 T-090
// 盲测纪律）。首解率数字由真实解析跑出（CS-4.4），门禁=中英各自 ≥80%
// （08 T-090 验收标准，v3.2 §11.5 出货门槛在留出集上的落地）+ 分意图
// ≥80%（W4 出口①口径）。终态钉死 zh 59/en 60（首跑 zh 36/en 30，调优
// 轮 1 后重钉——只升不降政策同 corpus-v1）；残余 miss 恰 1 条（满级面，
// 见 25-t090 报告 §4.3）。
// [F-170] 类级关联：本类全部测试方法覆盖留出集盲测三层守卫（钉死/分布+去重/门禁）。
public class HoldoutV1BlindTests
{
    private readonly ITestOutputHelper _output;

    public HoldoutV1BlindTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string HoldoutFile = "holdout-v1.jsonl";

    /// <summary>
    /// 盲测纪律执法锚：holdout-v1.jsonl 内容 SHA-256（首跑前登记；调优轮改内容即红）。
    /// 登记时点=盲测首跑前，见 docs/engineering/25-t090-holdout.md §2。
    /// </summary>
    private const string HoldoutSha256 = "cbf8794a4167e7399232aab73e1fcb7c57beee0c0bb0260fff3a1aa9662e41a5";

    private static readonly IntentKind[] AllTwelve =
        Enum.GetValues<IntentKind>();

    // ---- 钉死与分布守卫 --------------------------------------------------------

    [Fact] // legal：盲测纪律——文件 hash 钉死，留出集内容在首跑后不可变
    public void Holdout_v1_file_hash_is_pinned()
    {
        byte[] content = File.ReadAllBytes(Path.Combine(GrammarTestPaths.GrammarDir(), HoldoutFile));
        string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        Assert.Equal(HoldoutSha256, actual);
    }

    [Fact] // legal：120 条、中英 60/60、12 意图 × 5/语言、全部成功期望且携带槽位
    public void Holdout_v1_meets_distribution_requirements()
    {
        var entries = LoadEntries();

        Assert.Equal(120, entries.Count);
        Assert.Equal(60, entries.Count(e => e.Lang == "zh"));
        Assert.Equal(60, entries.Count(e => e.Lang == "en"));
        Assert.All(entries, e => Assert.True(e.ExpectSuccess, $"{e.Text}: 留出集只收成功期望行（反例/缺口由 corpus-v1 与 adversarial 承载）"));
        Assert.All(entries, e => Assert.NotNull(e.Intent));
        Assert.All(entries, e => Assert.NotNull(e.Slots));

        foreach (IntentKind kind in AllTwelve)
        {
            foreach (var lang in new[] { "zh", "en" })
            {
                int rows = entries.Count(e => e.Lang == lang && e.Intent == kind);
                Assert.Equal(5, rows); // 12 意图均衡：每意图每语言恰好 5 条
            }
        }
    }

    [Fact] // legal：RL4——与训练侧三语料（corpus-v1/种子/对抗载荷）归一化零重复
    public void Holdout_v1_shares_no_row_with_training_corpora()
    {
        var holdoutKeys = new HashSet<string>(LoadEntries()
            .Select(e => $"{e.Lang}|{TextNormalizer.Normalize(e.Text)}"));
        Assert.Equal(120, holdoutKeys.Count); // 内部亦零重复

        var trainingKeys = new HashSet<string>();
        foreach (var file in new[] { "corpus-v1.jsonl", "corpus-seed.jsonl" })
        {
            foreach (var entry in CorpusChannel.ParseJsonl(GrammarTestPaths.ReadGrammarFile(file)))
            {
                trainingKeys.Add($"{entry.Lang}|{TextNormalizer.Normalize(entry.Text)}");
            }
        }

        // 对抗语料按 payload 全量比对（lang 含 mixed，跨语言一并禁重）。
        foreach (var rawLine in GrammarTestPaths.ReadGrammarFile("adversarial.jsonl").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            using var document = JsonDocument.Parse(rawLine);
            string payload = document.RootElement.GetProperty("payload").GetString()!;
            trainingKeys.Add(TextNormalizer.Normalize(payload));
        }

        foreach (var entry in LoadEntries())
        {
            var key = $"{entry.Lang}|{TextNormalizer.Normalize(entry.Text)}";
            Assert.False(trainingKeys.Contains(key), $"holdout 行与训练语料重复: '{entry.Text}' [{entry.Lang}]");
            Assert.False(trainingKeys.Contains(TextNormalizer.Normalize(entry.Text)),
                $"holdout 行与对抗载荷重复: '{entry.Text}'");
        }
    }

    // ---- 盲测门禁（验收断言） ---------------------------------------------------

    /// <summary>终态钉死：中英命中数（T-090 调优终跑实测，CS-4.4；只升不降政策同 corpus-v1）。</summary>
    private const int PinnedZhHits = 59;
    private const int PinnedEnHits = 60;

    [Fact] // [F-170] 门禁：留出集中英各自首解率 ≥80%（08 T-090 验收）+ 分意图 ≥80%（W4 出口①口径）——数字为真实解析输出
    public void Holdout_v1_first_parse_rate_meets_gate()
    {
        var report = CorpusChannel.Evaluate(L1IntentParserTests.CreateParser(), LoadEntries());

        double zhRate = report.RateForLang("zh");
        double enRate = report.RateForLang("en");
        _output.WriteLine($"holdout v1: total={report.Total} hits={report.Hits} rate={report.Rate:0.####}");
        _output.WriteLine($"  zh: {report.HitsForLang("zh")}/{report.TotalForLang("zh")} = {zhRate:0.####}");
        _output.WriteLine($"  en: {report.HitsForLang("en")}/{report.TotalForLang("en")} = {enRate:0.####}");

        Assert.True(zhRate >= 0.80, $"zh 首解率 {zhRate:0.##} < 80%（08 T-090 验收线）");
        Assert.True(enRate >= 0.80, $"en 首解率 {enRate:0.##} < 80%（08 T-090 验收线）");

        // W4 出口①「单意图首解率 ≥80% 中英各自」：每意图每语言 5 行，≥4 行命中。
        foreach (IntentKind kind in AllTwelve)
        {
            foreach (var lang in new[] { "zh", "en" })
            {
                var rows = report.Rows.Where(r => r.Entry.Lang == lang && r.Entry.Intent == kind).ToList();
                Assert.Equal(5, rows.Count);
                Assert.True(
                    rows.Count(r => r.Hit) >= 4,
                    $"{lang}/{kind}: {rows.Count(r => r.Hit)}/5 < 80% 单意图线");
            }
        }
    }

    [Fact] // 诊断：miss 清单钉死——终态残余恰 1 条（满级面：to_tier 为显式档位词表，塔感知 max 需结构改造，RL4 不猜；见 25-t090 §4）
    public void Holdout_v1_miss_inventory_is_pinned()
    {
        var report = CorpusChannel.Evaluate(L1IntentParserTests.CreateParser(), LoadEntries());
        var misses = report.Rows.Where(r => !r.Hit).ToList();

        foreach (var miss in misses)
        {
            _output.WriteLine(
                $"MISS [{miss.Entry.Lang}/{miss.Entry.Intent}] {miss.Entry.Text}: {miss.Detail}");
        }

        Assert.Equal(PinnedZhHits, report.HitsForLang("zh")); // 首跑 36 → 终态 59（只升不降政策的落地见证）
        Assert.Equal(PinnedEnHits, report.HitsForLang("en")); // 首跑 30 → 终态 60
        var residual = Assert.Single(misses); // 残余 miss 恰 1 条（120−59−60）
        Assert.Equal("把东门的箭塔升到满级", residual.Entry.Text);
    }

    // ---- 装载 ---------------------------------------------------------------

    private static IReadOnlyList<CorpusEntry> LoadEntries()
        => CorpusChannel.ParseJsonl(GrammarTestPaths.ReadGrammarFile(HoldoutFile));
}
