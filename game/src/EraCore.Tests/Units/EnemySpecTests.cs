using System.Text.Json;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [C-10] EnemySpec/enemies.json 反序列化（T-039）：真实内容文件加载锚点 +
// 严格解析的三行用例（合法/边界/非法）。数值锚点出处 SP-3a L99~L107，
// 与 Era1ContentTests 的 JSON 直读断言互证——本文件走生产解析器路径。
public class EnemySpecTests
{
    // 最小合法敌行（合成 JSON 用；字段形态与 enemies.json 同构）。
    private const string LegalItem = """
        {
          "id": "era1:enemy-test", "content_type": "Enemy", "sp_id": "e1_test",
          "name_zh": "测试", "hp": 100, "armor": 2, "speed_px_per_tick": 1.28,
          "traits": {}, "canvas_size_px": 32
        }
        """;

    private static string WrapItems(string itemsJson) => $$"""
        {
          "$schema": "urn:eratd:content:enemy:1",
          "content_type": "Enemy",
          "tick_rate_hz": 30,
          "cell_size_px": 32,
          "items": [{{itemsJson}}]
        }
        """;

    [Fact] // legal — 真实 era1 文件经生产解析器装载，九种与 SP-3a 锚点逐项一致
    public void Catalog_loads_real_era1_file_with_SP3a_anchors()
    {
        var catalog = UnitsContent.LoadEnemyCatalog();

        Assert.Equal(9, catalog.Count); // SP-3a 恰九种
        Assert.Equal(30, catalog.TickRateHz); // K-1 30 Hz 时域（文件头声明）
        Assert.Equal(32, catalog.CellSizePx); // 1 格 = 32 px

        // SP-3a L107：踏群者 2500/6/0.75，is_boss，践踏半径 2 格，画布 96 px
        EnemySpec boss = catalog.Get("era1:enemy-boss");
        Assert.Equal("e1_boss", boss.SpId);
        Assert.True(boss.IsBoss);
        Assert.Equal(2500, boss.Hp);       // v3.2 §4 逐字锚点
        Assert.Equal(6, boss.Armor);       // v3.2 §4 逐字锚点
        Assert.Equal(0.75, boss.SpeedPxPerTick, precision: 9);
        Assert.Equal(2.0, boss.TrampleRadiusCells, precision: 9);
        Assert.Equal(96, boss.CanvasSizePx);

        // SP-3a L99：兽崽 40/0/1.60 非甲非 Boss 无践踏
        EnemySpec beastling = catalog.Get("era1:enemy-beastling");
        Assert.Equal(40, beastling.Hp);
        Assert.Equal(0, beastling.Armor);
        Assert.Equal(1.60, beastling.SpeedPxPerTick, precision: 9);
        Assert.False(beastling.IsBoss);
        Assert.Equal(0.0, beastling.TrampleRadiusCells, precision: 9);

        // 唯一 Boss 行：九行中仅踏群者带 is_boss（SP-3a 表加粗行唯一）
        string[] allIds =
        {
            "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-boar",
            "era1:enemy-tribesman", "era1:enemy-warband", "era1:enemy-champion",
            "era1:enemy-ram", "era1:enemy-boomshrew", "era1:enemy-boss",
        };
        Assert.Equal(1, allIds.Count(id => catalog.Get(id).IsBoss));
    }

    [Fact] // legal — TryGet 命中与 Get 同一实例语义（record 值相等）
    public void TryGet_resolves_known_and_rejects_unknown_ids()
    {
        var catalog = UnitsContent.LoadEnemyCatalog();

        Assert.True(catalog.TryGet("era1:enemy-ram", out EnemySpec ram));
        Assert.Equal(800, ram.Hp); // SP-3a L105 撞木 800/8/0.64
        Assert.Equal(8, ram.Armor);
        Assert.Equal(0.64, ram.SpeedPxPerTick, precision: 9);

        Assert.False(catalog.TryGet("era1:enemy-unknown", out _));
    }

    [Fact] // illegal — 未知 id 的 Get 抛 KeyNotFoundException（确实拒绝）
    public void Get_unknown_id_throws_key_not_found()
    {
        var catalog = UnitsContent.LoadEnemyCatalog();
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("era1:enemy-unknown"));
    }

    [Fact] // legal — 合成最小敌行解析成功且缺省字段取默认（armor/canvas 可缺省）
    public void Parse_accepts_minimal_legal_item_with_defaults()
    {
        string json = WrapItems("""
            {
              "id": "era1:enemy-min", "content_type": "Enemy", "sp_id": "e1_min",
              "name_zh": "最小", "hp": 55, "speed_px_per_tick": 0.9
            }
            """);
        var catalog = EnemySpecCatalog.Parse(json);

        EnemySpec spec = catalog.Get("era1:enemy-min");
        Assert.Equal(55, spec.Hp);
        Assert.Equal(0, spec.Armor);          // 未写 armor → 0
        Assert.Equal(32, spec.CanvasSizePx);  // 未写 canvas → 32
        Assert.False(spec.IsBoss);            // 未写 is_boss → false
        Assert.Equal(0.0, spec.TrampleRadiusCells, precision: 9);
    }

    [Fact] // illegal — 非法 JSON 抛 JsonException 族（解析失败即红，不得吞）
    public void Parse_malformed_json_throws()
    {
        // JsonReaderException 是 JsonException 的子类——ThrowsAny 认全族
        Assert.ThrowsAny<JsonException>(() => EnemySpecCatalog.Parse("{ not json"));
    }

    [Theory] // illegal — 结构/取值违约各行均以 ArgumentException 拒绝
    [InlineData("wrong_root_content_type")]   // 根 content_type 非 Enemy
    [InlineData("wrong_item_content_type")]   // 条目 content_type 非 Enemy
    [InlineData("missing_hp")]                // 缺必填 hp
    [InlineData("zero_hp")]                   // hp=0 非法
    [InlineData("zero_speed")]                // speed=0 非法
    [InlineData("negative_armor")]            // armor<0 非法
    [InlineData("duplicate_id")]              // id 重复
    [InlineData("empty_items")]               // items 空数组
    [InlineData("missing_items")]             // 缺 items 节
    public void Parse_rejects_structural_and_value_violations(string defect)
    {
        string legalDoc = WrapItems(LegalItem);
        string json = defect switch
        {
            "wrong_root_content_type" => ReplaceFirst(
                legalDoc, "\"content_type\": \"Enemy\"", "\"content_type\": \"Era\""),
            "wrong_item_content_type" => ReplaceFirst(
                legalDoc, "\"content_type\": \"Enemy\"", "\"content_type\": \"Building\"", skip: 1),
            "missing_hp" => WrapItems(LegalItem.Replace("\"hp\": 100,", "")),
            "zero_hp" => WrapItems(LegalItem.Replace("\"hp\": 100", "\"hp\": 0")),
            "zero_speed" => WrapItems(LegalItem.Replace("\"speed_px_per_tick\": 1.28", "\"speed_px_per_tick\": 0")),
            "negative_armor" => WrapItems(LegalItem.Replace("\"armor\": 2", "\"armor\": -1")),
            "duplicate_id" => WrapItems(LegalItem + "," + LegalItem),
            "empty_items" => WrapItems(""),
            _ => legalDoc.Replace("\"items\": [" + LegalItem + "]", "\"nope\": 1"),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => EnemySpecCatalog.Parse(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    /// <summary>Replaces the n-th occurrence (skip = how many matches to pass over).</summary>
    private static string ReplaceFirst(string text, string search, string replacement, int skip = 0)
    {
        int at = -1;
        for (int i = 0; i <= skip; i++)
        {
            at = text.IndexOf(search, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, $"缺陷注入：第 {i + 1} 处 '{search}' 未找到");
        }

        return text[..at] + replacement + text[(at + search.Length)..];
    }

    [Fact] // boundary — 空白输入（null/空串/纯空白）直接拒绝
    public void Parse_blank_input_throws_argument()
    {
        Assert.Throws<ArgumentException>(() => EnemySpecCatalog.Parse(""));
        Assert.Throws<ArgumentException>(() => EnemySpecCatalog.Parse("   "));
    }

    // ---- 战吼 war_cry 解析（T-114；era3 Boss trait 四元组）-------------------

    [Fact] // legal — 真实 era3 文件：堡垒号 war_cry 四元组经生产解析器装载
    public void Era3_boss_war_cry_quad_loads_from_the_real_file()
    {
        EnemySpec boss = UnitsContent.LoadEra3EnemyCatalog().Get("era3:enemy-boss");

        Assert.True(boss.IsBoss);
        Assert.Equal(3.0, boss.WarCryRadiusCells, precision: 9); // war_cry.radius_cells
        Assert.Equal(15, boss.WarCrySpeedBonusPercent);
        Assert.Equal(180, boss.WarCryDurationTicks); // 6 s × 30 Hz
        Assert.Equal(600, boss.WarCryCooldownTicks); // 20 s × 30 Hz
        Assert.Equal(600, boss.ShieldPoints);        // 个体盾（叠加裁定对照位）
    }

    [Fact] // boundary — 无 war_cry 的行（era1/era2 全表）：四元组全零=trait 缺席
    public void Rows_without_war_cry_parse_to_an_all_zero_quad()
    {
        EnemySpec e1 = UnitsContent.LoadEnemyCatalog().Get("era1:enemy-boss"); // 践踏位亦无战吼
        Assert.Equal(0.0, e1.WarCryRadiusCells, precision: 9);
        Assert.Equal(0, e1.WarCrySpeedBonusPercent);
        Assert.Equal(0, e1.WarCryDurationTicks);
        Assert.Equal(0, e1.WarCryCooldownTicks);

        EnemySpec e2 = UnitsContent.LoadEra2EnemyCatalog().Get("era2:enemy-centurion"); // 战鼓位≠战吼位
        Assert.Equal(0.0, e2.WarCryRadiusCells, precision: 9);
        Assert.Equal(3.0, e2.WarDrumsRadiusCells, precision: 9); // 战鼓常驻光环不受影响
    }

    [Theory] // illegal — war_cry 违约各行确实被拒绝（负值/半径>0 而三元不全正）
    [InlineData("\"speed_bonus_percent\": -15")] // 负加成
    [InlineData("\"duration_ticks\": 0")]        // 半径>0 而时长非正
    [InlineData("\"cooldown_ticks\": -600")]     // 负冷却
    public void Parse_rejects_war_cry_violations(string cryMembers)
    {
        string item = """
            {
              "id": "era1:enemy-cry", "content_type": "Enemy", "sp_id": "e1_cry",
              "name_zh": "吼者", "hp": 100, "speed_px_per_tick": 1.0,
              "traits": { "war_cry": { "radius_cells": 3, %CRY% } }
            }
            """.Replace("%CRY%", cryMembers);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => EnemySpecCatalog.Parse(WrapItems(item)));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
