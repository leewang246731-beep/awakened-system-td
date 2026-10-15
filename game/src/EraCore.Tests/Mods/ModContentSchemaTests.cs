using System.Text.Json;
using EraCore.Contracts.Content;
using EraCore.Mods;
using Xunit;

namespace EraCore.Tests.Mods;

// [F-156][F-165] K-2 内容 schema 矩阵（模组与本体同管线，CS-2.5）——T-103。
// 三行覆盖：合法（各类型基线行）/边界（可空与可选字段）/非法（缺必填/闭枚举外/
// 闭块外键/DSL 落错类型，全部带指针+行号）。

public class ModContentSchemaTests
{
    private static readonly string BuildingItem = """
        {
          "id": "mod-a:tower",
          "content_type": "Building",
          "class": "pierce",
          "name_zh": "测试塔",
          "name_en": "Test Tower",
          "aliases": ["测试塔"],
          "tier": 1,
          "max_tier": 2,
          "cost_total": 45,
          "dps": 15.0,
          "damage_per_shot": 15,
          "reload_ticks": 30,
          "range_cells": 4.5,
          "hp": 130,
          "ammo_item": "mod-a:proj",
          "ammo_per_shot": 1,
          "tier_growth": {
            "dps_multiplier_per_tier": 2.0,
            "cost_multiplier_per_tier": 3.0,
            "range_multiplier_per_tier": 1.25
          },
          "traits": { "slow_percent": 10 }
        }
        """;

    private static IReadOnlyList<ModSchemaViolation> Validate(ContentType type, string itemJson)
    {
        string document = "{\n  \"items\": [\n" + Indent(itemJson) + "\n  ]\n}";
        JsonElement element = JsonDocument.Parse(document).RootElement.GetProperty("items")[0];
        return ModContentSchema.ValidateItem(type, element, "/items/0", ModJsonLineMap.Index(document));
    }

    private static string Indent(string json)
    {
        string[] lines = json.Split('\n');
        return string.Join("\n", lines.Select((l, i) => i == 0 ? l : "  " + l));
    }

    private static int ItemLine()
    {
        // Validate() 放 item 对象起始在第 3 行
        return 3;
    }

    // ---- legal ----------------------------------------------------------

    [Fact] // [F-156] Building 基线行（era1 形状）全过
    public void Building_BaselineItem_Passes()
    {
        Assert.Empty(Validate(ContentType.Building, BuildingItem));
    }

    [Fact] // [F-156] Projectile/Enemy/Era 深表基线行各过
    public void DeepTables_BaselineItems_Pass()
    {
        Assert.Empty(Validate(ContentType.Projectile, """
            { "id": "mod-a:proj", "content_type": "Projectile", "name_zh": "箭", "name_en": "Arrow",
              "damage_per_shot": 15, "speed_px_per_tick": 6.0, "pierce_count": 1 }
            """));
        Assert.Empty(Validate(ContentType.Enemy, """
            { "id": "mod-a:enemy", "content_type": "Enemy", "name_zh": "敌", "name_en": "Foe",
              "hp": 800, "armor": 2, "speed_px_per_tick": 1.5, "sp_id": "E-01" }
            """));
        Assert.Empty(Validate(ContentType.Era, """
            { "id": "mod-a:era", "content_type": "Era", "name_zh": "纪元", "name_en": "Epoch", "era_index": 3 }
            """));
    }

    [Fact] // [F-157] 未知普通键忽略（模组扩展缝，TowerSpec 同语义）+ aliases/patch 通用块
    public void UnknownPlainKeys_Ignored_AliasAndPatchBlock_Legal()
    {
        Assert.Empty(Validate(ContentType.Building, BuildingItem
            .Replace("\"traits\"", "\"future_field\": {\"x\":1},\n  \"traits\"")
            .Replace("\"tier\": 1,", "\"patch\": false,\n  \"tier\": 1,")));
    }

    // ---- boundary -------------------------------------------------------

    [Fact] // [F-156] 边界：控制/光环塔无武器循环（reload_ticks null/缺省）、tier 4 封顶
    public void Building_NullableAndBoundaryFields_Pass()
    {
        Assert.Empty(Validate(ContentType.Building, BuildingItem
            .Replace("\"reload_ticks\": 30,", "\"reload_ticks\": null,")
            .Replace("\"tier\": 1,", "\"tier\": 4,")
            .Replace("\"max_tier\": 2,", "\"max_tier\": 4,")));
    }

    // ---- illegal --------------------------------------------------------

    [Fact] // [F-156] 缺必填字段：报在 item 行（字段不存在→引用 item 起始行）
    public void Building_MissingRequiredField_ReportedAtItemLine()
    {
        string broken = BuildingItem.Replace("  \"cost_total\": 45,\n", "");

        ModSchemaViolation violation = Assert.Single(Validate(ContentType.Building, broken));

        Assert.Equal("/items/0", violation.Pointer);
        Assert.Equal(ItemLine(), violation.Line);
        Assert.Contains("cost_total", violation.Message);
    }

    [Fact] // [F-156] 闭枚举外 class：报在字段行（行级报错；class 在 item 第 4 行→文档第 6 行）
    public void Building_UnknownTowerClass_ReportedAtFieldLine()
    {
        string broken = BuildingItem.Replace("\"class\": \"pierce\"", "\"class\": \"laser\"");

        ModSchemaViolation violation = Assert.Single(Validate(ContentType.Building, broken));

        Assert.Equal("/items/0/class", violation.Pointer);
        Assert.Equal(6, violation.Line);
        Assert.Contains("'class'", violation.Message);
    }

    [Fact] // [F-156] tier 越上界：4 为 K-3 to_tier 上限
    public void Building_TierAboveCeiling_Rejected()
    {
        string broken = BuildingItem.Replace("\"tier\": 1,", "\"tier\": 5,").Replace("\"max_tier\": 2,", "\"max_tier\": 5,");

        Assert.Contains(
            Validate(ContentType.Building, broken),
            v => v.Pointer == "/items/0/tier" && v.Message.Contains("tier"));
    }

    [Fact] // [F-156] 闭块：tier_growth 未知子键拒绝（封闭块不容未知成员）
    public void Building_TierGrowthUnknownChild_Rejected()
    {
        string broken = BuildingItem.Replace("\"range_multiplier_per_tier\": 1.25", "\"range_multiplier_per_tier\": 1.25, \"speed_multiplier\": 9.0");

        Assert.Contains(
            Validate(ContentType.Building, broken),
            v => v.Pointer == "/items/0/tier_growth/speed_multiplier" && v.Message.Contains("unknown key"));
    }

    [Fact] // [F-156] 闭块：traits 未知特性键拒绝（SP-1b 封闭特性集）
    public void Building_UnknownTraitKey_Rejected()
    {
        string broken = BuildingItem.Replace("{ \"slow_percent\": 10 }", "{ \"vampirism_percent\": 10 }");

        Assert.Contains(
            Validate(ContentType.Building, broken),
            v => v.Pointer == "/items/0/traits/vampirism_percent" && v.Message.Contains("unknown trait"));
    }

    [Fact] // [F-094] DSL 落错类型：Building 带 world_ops → Dsl 类错误
    public void Building_WrongDslFamily_RejectedAsDslError()
    {
        string broken = BuildingItem.Replace("\"traits\": { \"slow_percent\": 10 }", "\"world_ops\": [{\"op\":\"reveal_zone\",\"zone\":\"z1\"}]");

        Assert.Contains(
            Validate(ContentType.Building, broken),
            v => v.Kind == ModErrorKind.Dsl && v.Pointer == "/items/0/world_ops");
    }

    [Fact] // [F-156] item content_type 与文档类型不一致 → 拒绝
    public void Item_ContentTypeMismatch_Rejected()
    {
        string broken = BuildingItem.Replace("\"content_type\": \"Building\"", "\"content_type\": \"Enemy\"");

        Assert.Contains(
            Validate(ContentType.Building, broken),
            v => v.Pointer == "/items/0/content_type" && v.Message.Contains("Building"));
    }

    [Fact] // [F-156] DSL 落位矩阵：on_hit→Building/Projectile/StatusEffect；world_ops→Era/WaveGroup/Level；system_commands→Scroll/Hero
    public void DslPlacement_KindMatrix_IsClosed()
    {
        Assert.Equal(ModDslFamily.EffectPrimitive, ModContentSchema.DslFamilyOf(ContentType.Projectile));
        Assert.Equal(ModDslFamily.WorldOperation, ModContentSchema.DslFamilyOf(ContentType.Level));
        Assert.Equal(ModDslFamily.SystemCommand, ModContentSchema.DslFamilyOf(ContentType.Scroll));
        Assert.Null(ModContentSchema.DslFamilyOf(ContentType.Resource));
        Assert.Equal("on_hit", ModContentSchema.DslFieldOf(ContentType.Building));
        Assert.Null(ModContentSchema.DslFieldOf(ContentType.Wonder));
    }
}
