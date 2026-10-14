using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-111] T-075 HUD 第三指标全口径（DEBT v1.5 F8 清账）：波次构成
// 聚合器（类型×数量×护甲概览，快照 Units 聚合）+ 下一波预告投影（均匀拆分+
// 精英/整魁行）+ 等宽格式化。合法聚合/边界（空构成、单护甲、整除与余数拆分
// 两步跨档）/非法（空 id、负护甲、空名册、负数量、null 各参）。
// [F-111] [C-22]
public class WaveCompositionTests
{
    private static string NameOf(string id) => id switch
    {
        "e-beast" => "兽崽",
        "e-hunter" => "猎手",
        "e-elite" => "精锐",
        "e-boss" => "酋魁",
        _ => id,
    };

    // ---- legal — aggregation (类型×数量×护甲概览) --------------------------

    [Fact] // legal — 三种敌两段护甲：按种分组计数，护甲取该种最小~最大
    public void Aggregate_groups_by_species_with_armor_range()
    {
        var units = new (string EnemyId, int Armor)[]
        {
            ("e-hunter", 3), ("e-beast", 0), ("e-hunter", 5), ("e-beast", 0), ("e-beast", 2),
        };

        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Aggregate(units);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new WaveCompositionRow("e-beast", 3, 0, 2), rows[0]);
        Assert.Equal(new WaveCompositionRow("e-hunter", 2, 3, 5), rows[1]);
    }

    [Fact] // legal — 行序按 id 升序确定（HUD 文本与冒烟输出不可跨运行漂移）
    public void Aggregate_orders_rows_by_species_id()
    {
        var units = new (string EnemyId, int Armor)[] { ("z-last", 1), ("a-first", 1), ("m-mid", 1) };

        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Aggregate(units);

        Assert.Equal(new[] { "a-first", "m-mid", "z-last" }, rows.Select(row => row.EnemyId));
    }

    [Theory] // legal — 表驱动：单种数量递增仍聚为一行，护甲概览随之取极值
    [InlineData(1, "e-beast", 5, 5)]
    [InlineData(4, "e-beast", 0, 0)]
    [InlineData(9, "e-hunter", 0, 8)]
    public void Aggregate_table_singlespecies_count_and_armor_range(
        int count, string id, int armorLow, int armorHigh)
    {
        Assert.True(armorLow <= armorHigh);
        var units = Enumerable.Range(0, count)
            .Select(i => (id, i % 2 == 0 ? armorLow : armorHigh))
            .ToArray();

        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Aggregate(units);

        WaveCompositionRow row = Assert.Single(rows);
        Assert.Equal(new WaveCompositionRow(id, count, armorLow, armorHigh), row);
    }

    // ---- legal — preview projection (WaveSpawner 计划只读投影) -------------

    [Fact] // legal — 预告=常规数均匀拆名册（整除：12/3=4/4/4），护甲取名录原值
    public void Preview_splits_regular_count_evenly_across_roster()
    {
        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Preview(
            new[] { "e-beast", "e-hunter", "e-tribe" },
            regularCount: 12, eliteEnemyId: null, eliteCount: 0, bossEnemyId: null,
            armorOf: id => id == "e-beast" ? 0 : 3);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(4, row.Count));
        Assert.Equal(new WaveCompositionRow("e-beast", 4, 0, 0), rows[0]);
        Assert.Equal(new WaveCompositionRow("e-hunter", 4, 3, 3), rows[1]);
    }

    [Theory] // boundary — 余数拆分：前 regular%K 种各多 1（7/3=3/2/2）
    [InlineData(7, new[] { 3, 2, 2 })]
    [InlineData(8, new[] { 3, 3, 2 })]
    [InlineData(9, new[] { 3, 3, 3 })]
    public void Preview_remainder_goes_to_the_earliest_roster_entries(
        int regularCount, int[] expectedCounts)
    {
        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Preview(
            new[] { "e-beast", "e-hunter", "e-tribe" },
            regularCount, eliteEnemyId: null, eliteCount: 0, bossEnemyId: null,
            armorOf: _ => 0);

        Assert.Equal(expectedCounts, rows.Select(row => row.Count));
    }

    [Fact] // legal — 精英行与整魁行各自成行追加（数量=精英总数/1，护甲取名录）
    public void Preview_appends_elite_and_boss_rows()
    {
        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Preview(
            new[] { "e-beast" }, regularCount: 5,
            eliteEnemyId: "e-elite", eliteCount: 2, bossEnemyId: "e-boss",
            armorOf: id => id switch { "e-elite" => 6, "e-boss" => 9, _ => 0 });

        Assert.Equal(3, rows.Count);
        Assert.Equal(new WaveCompositionRow("e-beast", 5, 0, 0), rows[0]);
        Assert.Equal(new WaveCompositionRow("e-elite", 2, 6, 6), rows[1]);
        Assert.Equal(new WaveCompositionRow("e-boss", 1, 9, 9), rows[2]);
    }

    [Fact] // boundary — 常规数 0：名册行全空，仅剩精英/整魁（若有）
    public void Preview_with_zero_regular_keeps_only_elite_and_boss()
    {
        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Preview(
            new[] { "e-beast", "e-hunter" }, regularCount: 0,
            eliteEnemyId: "e-elite", eliteCount: 0, bossEnemyId: "e-boss", armorOf: _ => 9);

        WaveCompositionRow boss = Assert.Single(rows);
        Assert.Equal(new WaveCompositionRow("e-boss", 1, 9, 9), boss);
    }

    [Fact] // boundary — 关闭的精英线（null id）与无整魁（null）不产生行
    public void Preview_skips_disabled_elite_line_and_absent_boss()
    {
        IReadOnlyList<WaveCompositionRow> rows = WaveComposition.Preview(
            new[] { "e-beast" }, regularCount: 3,
            eliteEnemyId: null, eliteCount: 4, bossEnemyId: null, armorOf: _ => 0);

        WaveCompositionRow row = Assert.Single(rows);
        Assert.Equal(3, row.Count);
    }

    // ---- boundary — formatting ---------------------------------------------

    [Theory] // boundary — 行文本：护甲区间折叠（min==max 单值，否则 min~max）
    [InlineData("e-beast", 3, 0, 0, "兽崽×3(甲0)")]
    [InlineData("e-hunter", 12, 2, 5, "猎手×12(甲2~5)")]
    public void FormatRow_renders_name_count_and_armor_overview(
        string id, int count, int min, int max, string expected)
    {
        Assert.Equal(expected, WaveComposition.FormatRow(
            new WaveCompositionRow(id, count, min, max), NameOf));
    }

    [Fact] // boundary — 空构成（未入场/已清场）渲染占位符
    public void FormatSummary_renders_dash_for_empty_composition()
    {
        Assert.Equal("—", WaveComposition.FormatSummary(Array.Empty<WaveCompositionRow>(), NameOf));
    }

    [Fact] // legal — 汇总行空格相连，行序即聚合序
    public void FormatSummary_joins_rows_in_order()
    {
        var rows = new[]
        {
            new WaveCompositionRow("e-beast", 2, 0, 0),
            new WaveCompositionRow("e-hunter", 1, 4, 4),
        };

        Assert.Equal("兽崽×2(甲0) 猎手×1(甲4)", WaveComposition.FormatSummary(rows, NameOf));
    }

    [Theory] // boundary — 摘要：总数×种数×护甲带；空 → "—"
    [InlineData(0, 0, 0, "—")]
    [InlineData(12, 0, 3, "12敌·2种·甲0~3")]
    [InlineData(6, 2, 2, "6敌·2种·甲2")]
    public void FormatDigest_totals_units_species_and_armor_band(
        int total, int min, int max, string expected)
    {
        IReadOnlyList<WaveCompositionRow> rows = total == 0
            ? Array.Empty<WaveCompositionRow>()
            : new[]
            {
                new WaveCompositionRow("e-beast", total / 2 + total % 2, min, min),
                new WaveCompositionRow("e-hunter", total / 2, max, max),
            };

        // 种数=行数：两行同护甲（min==max 的 6 案）折叠护甲带但仍是 2 种；
        // 单行场景由 FormatDigest_single_row_has_no_armor_range 覆盖
        Assert.Equal(expected, WaveComposition.FormatDigest(rows));
    }

    [Fact] // legal — 摘要单行（只此一种）不显示区间
    public void FormatDigest_single_row_has_no_armor_range()
    {
        var rows = new[] { new WaveCompositionRow("e-boss", 1, 9, 9) };

        Assert.Equal("1敌·1种·甲9", WaveComposition.FormatDigest(rows));
    }

    // ---- illegal — rejected inputs ------------------------------------------

    [Fact] // illegal — null 单位序列拒绝
    public void Aggregate_rejects_null_units()
    {
        Assert.Throws<ArgumentNullException>(
            () => WaveComposition.Aggregate(null!));
    }

    [Theory] // illegal — 空/空白内容 id 拒绝（具体异常）
    [InlineData("")]
    [InlineData("   ")]
    public void Aggregate_rejects_blank_content_ids(string id)
    {
        Assert.Throws<ArgumentException>(
            () => WaveComposition.Aggregate(new[] { (id, 0) }));
    }

    [Theory] // illegal — 负护甲拒绝
    [InlineData(-1)]
    [InlineData(-9)]
    public void Aggregate_rejects_negative_armor(int armor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WaveComposition.Aggregate(new[] { ("e-beast", armor) }));
    }

    [Fact] // illegal — null 名册拒绝
    public void Preview_rejects_null_roster()
    {
        Assert.Throws<ArgumentNullException>(
            () => WaveComposition.Preview(null!, 5, null, 0, null, _ => 0));
    }

    [Fact] // illegal — 空名册拒绝
    public void Preview_rejects_empty_roster()
    {
        Assert.Throws<ArgumentException>(
            () => WaveComposition.Preview(Array.Empty<string>(), 5, null, 0, null, _ => 0));
    }

    [Theory] // illegal — 名册空白 id / 负数量 / null 护甲查表 / null 格式化委托拒绝
    [InlineData("bad-roster-id")]
    [InlineData("bad-count")]
    [InlineData("bad-armor-lookup")]
    [InlineData("bad-format-delegate")]
    public void Preview_and_formatting_reject_malformed_inputs(string mode)
    {
        switch (mode)
        {
            case "bad-roster-id":
                Assert.Throws<ArgumentException>(() => WaveComposition.Preview(
                    new[] { "e-beast", " " }, 5, null, 0, null, _ => 0));
                break;
            case "bad-count":
                Assert.Throws<ArgumentException>(() => WaveComposition.Preview(
                    new[] { "e-beast" }, -1, null, -3, null, _ => 0));
                break;
            case "bad-armor-lookup":
                Assert.Throws<ArgumentNullException>(() => WaveComposition.Preview(
                    new[] { "e-beast" }, 5, null, 0, null, null!));
                break;
            case "bad-format-delegate":
                Assert.Throws<ArgumentNullException>(() => WaveComposition.FormatRow(
                    new WaveCompositionRow("e-beast", 1, 0, 0), null!));
                break;
        }
    }

    [Fact] // illegal — null 行列表与 null 行拒绝
    public void Formatting_rejects_null_rows()
    {
        Assert.Throws<ArgumentNullException>(() => WaveComposition.FormatSummary(null!, NameOf));
        Assert.Throws<ArgumentNullException>(() => WaveComposition.FormatDigest(null!));
        Assert.Throws<ArgumentNullException>(() => WaveComposition.FormatRow(null!, NameOf));
    }
}
