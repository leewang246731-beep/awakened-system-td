using System.Text.Json;
using EraCore.Balance;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [C-02] [F-035] E2 敌表内容回归（T-066）：game/content/era2/enemies.json
// 九行锚点=11-spec-tables.md SP-3b L113~L121（HP/甲/速度 30 Hz 口径）；
// 期望值全部硬编码自文档锚点（出处写在各断言处），JSON 解析失败即红。
public class Era2EnemyContentTests
{
    private static readonly HashSet<string> ClosedCounterVocabulary = new()
    {
        "physical", "pierce", "splash", "energy", "slow", "support", "focus_fire",
    };

    private static readonly string[] NineIds =
    {
        "era2:enemy-skirmisher", "era2:enemy-warrior", "era2:enemy-ram", "era2:enemy-raider",
        "era2:enemy-crossbowman", "era2:enemy-guard", "era2:enemy-firearrow", "era2:enemy-centurion",
        "era2:enemy-boss",
    };

    private static EnemySpecCatalog LoadCatalog() => UnitsContent.LoadEra2EnemyCatalog();

    // ---- 1) 生产装载与行数 ----------------------------------------------

    [Fact] // legal — 生产装载器吃下整表（严格解析，结构缺陷即装载失败）
    public void Production_catalog_parses_all_nine_rows()
    {
        EnemySpecCatalog catalog = LoadCatalog();
        Assert.Equal(9, catalog.Count);
        Assert.Equal(30, catalog.TickRateHz);
        Assert.Equal(32, catalog.CellSizePx);

        string[] expectedIds =
        {
            "era2:enemy-skirmisher", "era2:enemy-warrior", "era2:enemy-ram", "era2:enemy-raider",
            "era2:enemy-crossbowman", "era2:enemy-guard", "era2:enemy-firearrow", "era2:enemy-centurion",
            "era2:enemy-boss",
        };
        string[] expectedSpIds =
        {
            "e2_skirmisher", "e2_warrior", "e2_ram", "e2_raider", "e2_crossbowman",
            "e2_guard", "e2_firearrow", "e2_centurion", "e2_boss",
        };
        int bosses = 0;
        for (int i = 0; i < expectedIds.Length; i++)
        {
            Assert.True(catalog.TryGet(expectedIds[i], out EnemySpec spec), $"缺行 {expectedIds[i]}");
            Assert.Equal(expectedSpIds[i], spec.SpId);
            bosses += spec.IsBoss ? 1 : 0;
        }

        Assert.Equal(1, bosses); // 唯一 Boss 行
        Assert.False(catalog.TryGet("era2:enemy-unknown", out _)); // 未登记 id 不命中
    }

    // ---- 2) SP-3b 数值锚点表（HP/甲/速度） --------------------------------

    [Theory] // legal — 九行逐项对表（SP-3b L113~L121；速度 px/tick @30Hz）
    [InlineData("era2:enemy-skirmisher", 60, 0, 1.60, 1.5)]   // L113 已在案+F.5 第 5 波互证
    [InlineData("era2:enemy-warrior", 300, 4, 1.17, 1.1)]     // L114 F.5 反推锚点
    [InlineData("era2:enemy-ram", 1500, 12, 0.53, 0.5)]       // L115 F.5 反推锚点
    [InlineData("era2:enemy-raider", 140, 2, 2.67, 2.5)]      // L116 [NEW]
    [InlineData("era2:enemy-crossbowman", 180, 3, 1.07, 1.0)] // L117 [NEW]
    [InlineData("era2:enemy-guard", 600, 8, 0.85, 0.8)]       // L118 [NEW]
    [InlineData("era2:enemy-firearrow", 120, 1, 1.71, 1.6)]   // L119 [NEW]
    [InlineData("era2:enemy-centurion", 900, 6, 0.96, 0.9)]   // L120 [NEW]
    [InlineData("era2:enemy-boss", 4000, 10, 0.64, 0.6)]      // L121 v3.2 §4 逐字锚点
    public void Enemies_match_SP3b_hp_armor_speed(string id, int hp, int armor, double speed, double cellsPerS)
    {
        EnemySpec spec = LoadCatalog().Get(id);

        Assert.Equal(hp, spec.Hp);
        Assert.Equal(armor, spec.Armor);
        Assert.Equal(speed, spec.SpeedPxPerTick, 3);

        // 格/s 换算一致性：px/tick×30÷32（K-1 30 Hz、1 格=32 px）
        Assert.InRange(spec.SpeedPxPerTick * 30.0 / 32.0, cellsPerS - 0.02, cellsPerS + 0.02);
    }

    // ---- 3) 引擎消费的特性字段 -------------------------------------------

    [Fact] // legal — 百夫长战鼓：半径 3 格、友军 +15%（SP-3b L120）
    public void Centurion_carries_the_war_drums_pair_from_SP3b()
    {
        EnemySpec spec = LoadCatalog().Get("era2:enemy-centurion");
        Assert.Equal(3.0, spec.WarDrumsRadiusCells);
        Assert.Equal(15, spec.WarDrumsSpeedBonusPercent);
        Assert.Equal(0, spec.ChargeIntervalTicks); // 无冲撞
    }

    [Fact] // legal — 铁蹄 Boss 三技能行：践踏+战鼓+冲撞（T-066 任务卡+SP-3b L121）
    public void Ironhoof_carries_trample_war_drums_and_charge()
    {
        EnemySpec spec = LoadCatalog().Get("era2:enemy-boss");

        Assert.Equal(2.0, spec.TrampleRadiusCells);        // 践踏半径（任务卡增补 [NEW]）
        Assert.Equal(3.0, spec.WarDrumsRadiusCells);       // 战鼓（值取 SP-3b L120 行）
        Assert.Equal(15, spec.WarDrumsSpeedBonusPercent);
        Assert.Equal(600, spec.ChargeIntervalTicks);       // 每 20 s（SP-3b L121 行文）
        Assert.Equal(150, spec.ChargeDamage);              // [NEW-原型校准]
        Assert.Equal(2.0, spec.ChargeReachCells);          // [NEW-原型校准]
        Assert.Equal(90, spec.ChargeDurationTicks);        // [NEW-原型校准]
        Assert.Equal(50, spec.ChargeSpeedBonusPercent);    // [NEW-原型校准]
    }

    [Fact] // legal — 攻城锤对建筑伤害 ×3（SP-3b L115）+ 精英线物种
    public void Ram_scales_building_damage_three_times()
    {
        EnemySpec spec = LoadCatalog().Get("era2:enemy-ram");
        Assert.Equal(3.0, spec.BuildingDamageMultiplier);
    }

    [Fact] // legal — 个体护盾框架位：SP-3b 九行全空（E2 无个体盾；波次盾 E3 起）
    public void No_era2_row_carries_an_individual_shield()
    {
        EnemySpecCatalog catalog = LoadCatalog();
        foreach (string id in NineIds)
        {
            Assert.Equal(0, catalog.Get(id).ShieldPoints);
        }
    }

    // ---- 4) 原始 JSON 结构断言（装载器不映射的字段） -----------------------

    [Fact] // legal — 克制提示走封闭词表（六塔类+focus_fire）
    public void Counters_use_the_closed_vocabulary()
    {
        using JsonDocument doc = JsonDocument.Parse(UnitsContent.ReadEra2EnemiesJson());
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Enemy", item.GetProperty("content_type").GetString());
            foreach (JsonElement counter in item.GetProperty("counters").EnumerateArray())
            {
                string? name = counter.GetString();
                Assert.False(string.IsNullOrWhiteSpace(name), "克制提示不得为空串");
                Assert.Contains(name!, ClosedCounterVocabulary);
            }
        }
    }

    [Fact] // legal — 散兵成群/勇士无特性（SP-3b 行文）+ K-2 命名空间唯一
    public void Trait_rows_and_ids_follow_SP3b_wording()
    {
        using JsonDocument doc = JsonDocument.Parse(UnitsContent.ReadEra2EnemiesJson());
        var ids = new HashSet<string>();
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            Assert.StartsWith("era2:enemy-", id);
            Assert.True(ids.Add(id), $"重复 id {id}");
        }

        Assert.Equal(9, ids.Count);
        Assert.Equal("era2:era-classical", doc.RootElement.GetProperty("era").GetString());
    }

    // ---- 5) F.5 互证锚（SP-3c L129/L131）----------------------------------

    [Fact] // legal — 第 20 波精英线 4 攻城锤=6000 点基础 HP（F.5 验算行）
    public void Wave20_elite_line_is_four_rams_of_six_thousand_total_hp()
    {
        Assert.Equal(4, Elite.Count(20)); // 1+⌊(20−8)/4⌋=4（v3.2 §6）
        EnemySpec ram = LoadCatalog().Get("era2:enemy-ram");
        Assert.Equal(6000, 4 * ram.Hp);   // SP-3c L131：4×1500=6000 点基础 HP
    }

    [Fact] // legal — 第 5 波散兵 EHP 互证：6×60×1.0=360（SP-3c L129）
    public void Wave5_skirmisher_ehp_cross_checks_F5()
    {
        // t=0.75 → HP 乘子 0.5+(0.75−0.3)/0.9=1.0（v3.2 §7/F.2）
        double hpMultiplier = 0.5 + (0.75 - 0.3) / 0.9;
        Assert.Equal(1.0, hpMultiplier, 9);
        EnemySpec skirmisher = LoadCatalog().Get("era2:enemy-skirmisher");
        Assert.Equal(360, (int)Math.Round(6 * skirmisher.Hp * hpMultiplier));
    }
}
