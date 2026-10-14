using EraCore.Combat;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-120] T-093 塔详情卡（三层信息①静态面板：DPS/射程/弹种/克制+
// 升级预览）与 TowerCardCatalog 解析：三行——合法（行渲染/×3 成本差/
// upgrades_to 优先）、边界（tier 顶格=无预览/名字缺落回 id）、非法
// （悬空 upgrades_to/错 content_type/maxCounterNames<1）。
public class TowerInfoCardTests
{
    private const string TowersDoc =
        """
        {
          "content_type": "Building",
          "ammo_convention": { "shots_per_ammo_item": 10 },
          "items": [
            {
              "id": "era1:tower-wood-arrow", "class": "pierce", "tier": 1, "max_tier": 2,
              "cost_total": 30, "dps": 10.0, "damage_per_shot": 10, "reload_ticks": 30,
              "range_cells": 5.0, "hp": 120, "ammo_item": "era1:ammo-arrow", "ammo_per_shot": 1,
              "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
              "traits": {}, "name_zh": "木箭塔", "name_en": "Wood Arrow Tower"
            },
            {
              "id": "era1:tower-campfire", "class": "support", "tier": 1, "max_tier": 2,
              "cost_total": 20, "dps": 0.0, "damage_per_shot": 0, "reload_ticks": null,
              "range_cells": 3.0, "hp": 80, "ammo_item": null, "ammo_per_shot": 0,
              "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
              "traits": {}, "name_zh": "篝火", "name_en": "Campfire"
            }
          ]
        }
        """;

    private const string EnemiesDoc =
        """
        {
          "content_type": "Enemy",
          "items": [
            { "id": "e-beast", "name_zh": "兽崽", "armor": 0, "counters": ["pierce"] },
            { "id": "e-hunter", "name_zh": "猎狼", "armor": 0, "counters": ["pierce"] },
            { "id": "e-boar", "name_zh": "铠猪", "armor": 2, "counters": ["physical"] },
            { "id": "e-warrior", "name_zh": "蛮勇士", "armor": 3, "counters": ["physical", "pierce"] },
            { "id": "e-ram", "name_zh": "撞木", "armor": 8, "counters": ["physical"] },
            { "id": "e-boss", "name_zh": "踏群者", "armor": 6, "counters": ["physical"] }
          ]
        }
        """;

    private static TowerCardCatalog Catalog() => TowerCardCatalog.FromTowersJson(TowersDoc, "test-towers");

    private static CounterIndex Counters() => CounterIndex.FromEnemiesJson(EnemiesDoc, "test-enemies");

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 详情卡全行渲染：标题（名+级+类）/五统计行/克制行
    public void Build_renders_title_stats_and_counter_line()
    {
        TowerCardCatalog catalog = Catalog();
        TowerSpec spec = catalog.SpecsById["era1:tower-wood-arrow"];
        TowerCardView view = TowerInfoCard.Build(
            spec, catalog.Names, Counters(), catalog.SpecsById);

        Assert.Equal("木箭塔 T1·穿刺", view.Title);
        Assert.Contains("DPS 10", view.Lines[0]);
        Assert.Contains("射程 5.0 格", view.Lines[2]);
        Assert.Equal("弹种 era1:ammo-arrow", view.Lines[3]);
        Assert.Equal("克制 兽崽(甲0)、猎狼(甲0)、蛮勇士(甲3)", view.CounterLine); // 穿刺恰 3 种=全列无折叠

        // 物理线 4 种敌 → 上限 3 名折叠「等4种」（落石台=physical 类）
        TowerSpec rockfall = catalog.SpecsById["era1:tower-wood-arrow"] with
        {
            Id = "era1:tower-rockfall",
            Class = TowerClass.Physical,
        };
        string physicalLine = TowerInfoCard.Build(
            rockfall, catalog.Names, Counters(), catalog.SpecsById).CounterLine;
        Assert.EndsWith("等4种", physicalLine, StringComparison.Ordinal);
        Assert.StartsWith("克制 铠猪(甲2)、蛮勇士(甲3)、撞木(甲8)", physicalLine, StringComparison.Ordinal);
    }

    [Fact] // legal — 升级预览（乘子路径）：×2 DPS、×3 成本合计（v3.2 §6 严格律）
    public void PreviewUpgrade_multiplies_by_declared_growth()
    {
        TowerCardCatalog catalog = Catalog();
        TowerSpec spec = catalog.SpecsById["era1:tower-wood-arrow"];

        TowerUpgradePreview? preview = TowerInfoCard.PreviewUpgrade(spec, catalog.SpecsById);

        Assert.NotNull(preview);
        Assert.Equal(20.0, preview!.NextDps, precision: 6);
        Assert.Equal(10.0, preview.DpsDelta, precision: 6);
        Assert.Equal(90.0, preview.NextCostTotal, precision: 6);
        Assert.Equal(60.0, preview.CostDelta, precision: 6);
        Assert.Equal(6.25, preview.NextRangeCells, precision: 6);
    }

    [Fact] // legal — 升级预览（接线路径）：upgrades_to 行存在时读真实继任值
    public void PreviewUpgrade_prefers_the_wired_successor_row()
    {
        const string doc =
            """
            {
              "content_type": "Building",
              "ammo_convention": { "shots_per_ammo_item": 10 },
              "items": [
                { "id": "t1", "class": "pierce", "tier": 1, "max_tier": 2, "cost_total": 90,
                  "dps": 30.0, "damage_per_shot": 30, "reload_ticks": 30, "range_cells": 6.5,
                  "hp": 200, "ammo_item": "a", "ammo_per_shot": 1,
                  "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
                  "upgrades_to": "t2", "name_zh": "箭楼" },
                { "id": "t2", "class": "pierce", "tier": 2, "max_tier": 2, "cost_total": 270,
                  "dps": 60.0, "damage_per_shot": 60, "reload_ticks": 30, "range_cells": 8,
                  "hp": 300, "ammo_item": "a", "ammo_per_shot": 1, "name_zh": "连弩塔" }
              ]
            }
            """;
        TowerCardCatalog catalog = TowerCardCatalog.FromTowersJson(doc, "test-towers");

        TowerUpgradePreview? preview = TowerInfoCard.PreviewUpgrade(
            catalog.SpecsById["t1"], catalog.SpecsById);

        Assert.NotNull(preview);
        Assert.Equal(60.0, preview!.NextDps, precision: 6);   // 继任行真值（非 30×2 巧合值可分辨）
        Assert.Equal(270.0, preview.NextCostTotal, precision: 6);
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — tier 顶格（T=max_tier）：无升级预览（null），不是异常
    public void PreviewUpgrade_returns_null_at_the_tier_ceiling()
    {
        TowerCardCatalog catalog = Catalog();
        TowerSpec campfire = catalog.SpecsById["era1:tower-campfire"];
        TowerSpec topped = campfire with { Tier = 2 }; // tier==max_tier=2 → 顶格

        Assert.Null(TowerInfoCard.PreviewUpgrade(topped, catalog.SpecsById));

        // 对照：未顶格（T1<max 2）+乘子块在 → 预览非空（同法可分辨两态）
        Assert.NotNull(TowerInfoCard.PreviewUpgrade(campfire, catalog.SpecsById));
    }

    [Fact] // boundary — 名字缺落回 id；克制空行渲染为「克制 —」
    public void Missing_name_falls_back_to_id_and_empty_counters_render_dash()
    {
        TowerCardCatalog catalog = Catalog();
        TowerSpec campfire = catalog.SpecsById["era1:tower-campfire"]; // support 类无敌点名
        var emptyNames = new Dictionary<string, string>();

        TowerCardView view = TowerInfoCard.Build(campfire, emptyNames, Counters(), catalog.SpecsById);

        Assert.StartsWith("era1:tower-campfire T1", view.Title, StringComparison.Ordinal);
        Assert.Equal("克制 —", view.CounterLine);
        Assert.Equal("弹种 无", view.Lines[3]);
    }

    // ---- illegal ---------------------------------------------------------

    [Theory] // illegal — 悬空 upgrades_to / 错 content_type / 坏 JSON / 空 items
    [InlineData("dangling-upgrade", "does not resolve")]
    [InlineData("wrong-type", "content_type")]
    [InlineData("broken-json", "invalid JSON")]
    [InlineData("empty-items", "non-empty")]
    public void Catalog_parse_rejects_broken_documents(string kind, string messagePart)
    {
        string json = kind switch
        {
            "dangling-upgrade" => """
                {
                  "content_type": "Building",
                  "items": [
                    { "id": "t1", "class": "pierce", "tier": 1, "max_tier": 2, "cost_total": 90,
                      "dps": 30.0, "damage_per_shot": 30, "reload_ticks": 30, "range_cells": 6.5,
                      "hp": 200, "ammo_item": "a", "ammo_per_shot": 1, "upgrades_to": "ghost" }
                  ]
                }
                """,
            "wrong-type" => """{"content_type": "Enemy", "items": []}""",
            "broken-json" => """{"content_type": "Building", "items": [""",
            "empty-items" => """{"content_type": "Building", "items": []}""",
            _ => throw new InvalidOperationException(),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => TowerCardCatalog.FromTowersJson(json, "test-towers"));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }

    [Fact] // illegal — Build 的 maxCounterNames 下界与 null 入参
    public void Build_rejects_bad_arguments()
    {
        TowerCardCatalog catalog = Catalog();
        TowerSpec spec = catalog.SpecsById["era1:tower-wood-arrow"];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TowerInfoCard.Build(spec, catalog.Names, Counters(), catalog.SpecsById, maxCounterNames: 0));
        Assert.Throws<ArgumentNullException>(
            () => TowerInfoCard.Build(null!, catalog.Names, Counters(), catalog.SpecsById));
        Assert.Throws<ArgumentNullException>(
            () => TowerInfoCard.PreviewUpgrade(spec, null!));
        Assert.Throws<ArgumentNullException>(
            () => TowerCardCatalog.FromTowersJson(null!, "x"));
    }
}
