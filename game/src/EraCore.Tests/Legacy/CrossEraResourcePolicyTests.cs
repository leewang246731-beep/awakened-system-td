using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Legacy;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-011] 跨代资源策略派生（T-115）：era.json 的 resources 行
// （含 legacy_refresh）→ 唯一派生两件事——新时代有用资源集（T-074 跃迁
// NewEraResources 的内容源：其余全量 3:1 折文明基金）与传承刷新行
// （上代 2 种基础资源，丰度 ×0.5，v3.2 §4 跨代资源行）。期望值全部
// 硬编码自 era2/era3 内容行（T-079/T-112 已冻结），解析失败即红。
public class CrossEraResourcePolicyTests
{
    private static CrossEraResourcePolicy ParseEra(string era) =>
        CrossEraResourcePolicy.Parse(File.ReadAllText(Path.Combine(EraDir(era), "era.json")));

    private static ResourceKey Res(string id) => new(id);

    // ---- legal --------------------------------------------------------------

    [Fact] // [F-011] E2→E3 折算表：era3 行派生出的有用集恰=四资源（铜/铁传承刷新+煤/钢本代）
    public void Era3_policy_derives_the_E2_to_E3_useful_set_and_refresh_rows()
    {
        CrossEraResourcePolicy policy = ParseEra("era3");

        Assert.Equal(3, policy.TargetEraIndex);
        Assert.Equal(
            new HashSet<ResourceKey> { Res("era3:res-coal"), Res("era2:res-iron"), Res("era3:res-steel"), Res("era2:res-copper") },
            policy.NewEraResources);

        // §4 跨代资源行：上代（E2）基础二资源=铜/铁，丰度乘数恰 0.5（era3/era.json legacy_refresh 行）
        Assert.Equal(2, policy.LegacyRefresh.Count);
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(2, row.FromEraIndex));
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(0.5, row.AbundanceMultiplier, 9));
        Assert.Equal(
            new HashSet<ResourceKey> { Res("era2:res-iron"), Res("era2:res-copper") },
            policy.LegacyRefresh.Select(row => row.Resource).ToHashSet());

        // 本代原生资源丰度乘数=1（无折减）
        Assert.Equal(1.0, policy.AbundanceOf(Res("era3:res-coal")), 9);
        Assert.Equal(1.0, policy.AbundanceOf(Res("era3:res-steel")), 9);
        Assert.Equal(0.5, policy.AbundanceOf(Res("era2:res-iron")), 9);
        Assert.Equal(0.5, policy.AbundanceOf(Res("era2:res-copper")), 9);
    }

    [Fact] // [F-011] E1→E2 同法验证（T-074 测试手写集合的内容侧同源化）
    public void Era2_policy_useful_set_matches_the_T074_hand_written_set()
    {
        CrossEraResourcePolicy policy = ParseEra("era2");

        Assert.Equal(6, policy.NewEraResources.Count);
        Assert.Equal(
            new HashSet<ResourceKey> { Res("era1:res-wood"), Res("era1:res-stone") },
            policy.LegacyRefresh.Select(row => row.Resource).ToHashSet());
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(1, row.FromEraIndex));

        // E1→E2 跃迁的有用谓词：policy 集 ∩ E1 资源恰= {wood, stone}——food 折算。
        // 与 EraTransitionTests 手写 NewEraResources（wood/stone）逐值同源。
        var e1Resources = new[] { Res("era1:res-wood"), Res("era1:res-stone"), Res("era1:res-food") };
        Assert.Equal(
            new HashSet<ResourceKey> { Res("era1:res-wood"), Res("era1:res-stone") },
            e1Resources.Where(policy.NewEraResources.Contains).ToHashSet());
    }

    // ---- boundary ------------------------------------------------------------

    [Fact] // [F-011] 最小行：单 item 单资源无 legacy_refresh → 有用集恰一元、丰度 1
    public void Minimal_single_resource_document_parses_without_refresh()
    {
        const string json = """
            { "items": [ { "era_index": 4, "resources": [ { "id": "era4:res-oil" } ] } ] }
            """;

        CrossEraResourcePolicy policy = CrossEraResourcePolicy.Parse(json);

        Assert.Equal(4, policy.TargetEraIndex);
        Assert.Equal(new HashSet<ResourceKey> { Res("era4:res-oil") }, policy.NewEraResources);
        Assert.Empty(policy.LegacyRefresh);
        Assert.Equal(1.0, policy.AbundanceOf(Res("era4:res-oil")), 9);
    }

    // ---- illegal -------------------------------------------------------------

    public static TheoryData<string, string> MalformedDocuments() => new()
    {
        // 重复资源 id
        { """{ "items": [ { "era_index": 3, "resources": [ { "id": "x:a" }, { "id": "x:a" } ] } ] }""", "Duplicate resource id 'x:a'" },
        // from_era 越下界（0）
        { """{ "items": [ { "era_index": 3, "resources": [ { "id": "x:a", "legacy_refresh": { "from_era": 0, "abundance_multiplier": 0.5 } } ] } ] }""", "from_era" },
        // from_era 非前代（=本代 3）
        { """{ "items": [ { "era_index": 3, "resources": [ { "id": "x:a", "legacy_refresh": { "from_era": 3, "abundance_multiplier": 0.5 } } ] } ] }""", "predecessor" },
        // 丰度乘数=0（死行）
        { """{ "items": [ { "era_index": 3, "resources": [ { "id": "x:a", "legacy_refresh": { "from_era": 2, "abundance_multiplier": 0 } } ] } ] }""", "abundance_multiplier" },
        // 丰度乘数 ≥1（刷新不得增强）
        { """{ "items": [ { "era_index": 3, "resources": [ { "id": "x:a", "legacy_refresh": { "from_era": 2, "abundance_multiplier": 1 } } ] } ] }""", "abundance_multiplier" },
        // 资源行缺 id
        { """{ "items": [ { "era_index": 3, "resources": [ { "name_zh": "煤" } ] } ] }""", "'id'" },
        // items 缺失
        { """{ "era_index": 3 }""", "items" },
    };

    [Theory] // [F-011] 非法行：结构违约必须点名字段拒绝（红测试，不静默半载）
    [MemberData(nameof(MalformedDocuments))]
    public void Malformed_documents_are_rejected_by_name(string json, string messagePart)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CrossEraResourcePolicy.Parse(json));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }

    [Fact] // [F-011] 非法：era_index 越界（终章=6 封顶）
    public void Era_index_out_of_six_is_rejected()
    {
        const string json = """{ "items": [ { "era_index": 7, "resources": [ { "id": "x:a" } ] } ] }""";

        var ex = Assert.Throws<InvalidOperationException>(() => CrossEraResourcePolicy.Parse(json));
        Assert.Contains("era_index", ex.Message, StringComparison.Ordinal);
    }

    [Fact] // [F-011] 真内容烟测：已落地时代行（era1/era2/era3）全部可解析；
            // era4/era5 由 T-123/T-134 落地后在数组扩展
    public void Every_landed_era_document_parses_with_non_empty_useful_set()
    {
        foreach (string era in new[] { "era1", "era2", "era3" })
        {
            string path = Path.Combine(EraDir(era), "era.json");
            Assert.True(File.Exists(path), $"era document missing: {era}");

            CrossEraResourcePolicy policy = CrossEraResourcePolicy.Parse(File.ReadAllText(path));
            Assert.False(policy.NewEraResources.Count == 0, $"{era}: useful set empty");
            Assert.True(era == "era1" ^ policy.LegacyRefresh.Count > 0,
                $"{era}: E1 无上代（无刷新行），其余代恰有刷新行");
        }
    }
}
