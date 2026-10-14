using System.Linq;
using System.Text.Json;
using EraCore.Combat;
using Xunit;
using static EraCore.Tests.Balance.E2TowerBench;

namespace EraCore.Tests.Combat;

// [F-014] [C-09] T-065 E2 塔组内容回归：game/content/era2/towers.json 五塔
// 逐塔对齐 F.5 基准行（v3.2 附录 F.5 L359 冻结值）+ SP-1b L40~L44 定类 +
// §6 成长律（DPS ×2/级 ±15%、成本 ×3/级 严格、射程 +25%/级 ±15% 设计带）
// + 07 D-15 AOE −20% 与特化线口径。期望值全部硬编码自文档锚点（出处写在
// 各断言处注释），JSON 解析失败即红；实值一律从内容 JSON 装载（RL7——
// 引擎侧无数值）。
public class Era2TowerContentTests
{
    // ---- 装载辅助（与 Era1ContentTests 同纪律） --------------------------

    private static string FindEra2Dir()
    {
        // 仓库相对寻址（RL9：禁绝对路径）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era2");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到 game/content/era2（测试须在仓库内运行）");
    }

    private static List<JsonElement> LoadItems()
    {
        var path = Path.Combine(FindEra2Dir(), "towers.json");
        Assert.True(File.Exists(path), "内容文件缺失：era2/towers.json");
        // 注意不 using-dispose（Era1ContentTests 同例）：返回的 JsonElement 只在
        // JsonDocument 存活期间可访问，文件小且条目固定由 GC 回收。
        var doc = JsonDocument.Parse(File.ReadAllText(path)); // 严格模式：非法 JSON 即红
        return doc.RootElement.GetProperty("items").EnumerateArray().ToList();
    }

    private static JsonElement FindById(List<JsonElement> items, string id)
    {
        var found = items.FirstOrDefault(i => i.GetProperty("id").GetString() == id);
        Assert.True(found.ValueKind == JsonValueKind.Object, $"缺内容项 {id}");
        return found;
    }

    // ---- 1) F.5 基准行表驱动回归：成本|DPS|射程 逐塔冻结值 ------------------

    public static TheoryData<string, double, double, double, string, int> F5Rows => new()
    {
        // v3.2 附录 F.5 L359：「箭楼 T1 90|30|6.5；弩塔 T2 270|60|8；连弩塔 T3
        // 810|120|9.5；弩炮 T3 特化 700|80|11；火油塔 T4 2430|240AOE|7.5」
        { "era2:tower-arrow", 90, 30.0, 6.5, "pierce", 1 },       // F.5 行 1
        { "era2:tower-crossbow", 270, 60.0, 8.0, "pierce", 2 },   // F.5 行 2（箭楼同线升级，SP-1b L41）
        { "era2:tower-repeating-crossbow", 810, 120.0, 9.5, "pierce", 3 }, // F.5 行 3（9.5=07 修正 #2 合法非整值）
        { "era2:tower-onager", 700, 80.0, 11.0, "physical", 3 },  // F.5 行 4（T3 特化，SP-1b L43 物理）
        { "era2:tower-fire-oil", 2430, 240.0, 7.5, "splash", 4 }, // F.5 行 5（07 D-15 恢复 810×3=2430）
    };

    [Theory]
    [MemberData(nameof(F5Rows))]
    public void Towers_match_F5_baseline_rows_exactly(
        string id, double cost, double dps, double range, string towerClass, int tier)
    {
        // [F-014] E2 五塔 F.5 基准回归（成本|DPS|射程逐字段冻结值）
        var spec = Era2Content.Tower(id);
        Assert.Equal(towerClass, spec.Class.ToString().ToLowerInvariant()); // SP-1b L40~L44 定类
        Assert.Equal(tier, spec.Tier);
        Assert.Equal(cost, spec.CostTotal);   // F.5 冻结成本（铁/木合计资源点）
        Assert.Equal(dps, spec.Dps, 9);       // F.5 冻结 DPS（火油 240=AOE 合计）
        Assert.Equal(range, spec.RangeCells, 9); // F.5 冻结射程（格）
    }

    [Fact]
    public void Five_towers_load_with_exactly_the_SP1b_set()
    {
        // [F-014] SP-1b L40~L44：E2 塔组恰五塔，id 命名空间 era2:tower-*
        var items = LoadItems();
        Assert.Equal(5, items.Count);
        Assert.All(items, item =>
            Assert.Matches("^era2:tower-[a-z][a-z0-9]*(-[a-z0-9]+)*$", item.GetProperty("id").GetString()!));
        Assert.All(items, item =>
            Assert.Equal("Building", item.GetProperty("content_type").GetString()!));

        // 弹药约定 v3.2 §5 L78「1 件弹药 = 10 发」随文件声明
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FindEra2Dir(), "towers.json")));
        Assert.Equal(10, doc.RootElement.GetProperty("ammo_convention")
            .GetProperty("shots_per_ammo_item").GetInt32());
    }

    [Fact]
    public void DPS_split_stays_self_consistent_on_the_tick_grid()
    {
        // [F-027] 内部一致性：dps = damage_per_shot × 30 / reload_ticks（§6 符号
        // 约定 d=单发伤害，30 Hz）——五塔全过（含整 tick 网格就近取值的拆分）
        foreach (var spec in Era2Content.Towers)
        {
            Assert.NotNull(spec.ReloadTicks);
            var expected = spec.DamagePerShot * 30.0 / spec.ReloadTicks!.Value;
            Assert.Equal(expected, spec.Dps, 3);
        }
    }

    [Fact]
    public void Content_dps_matches_the_F5_golden_bench_constants()
    {
        // [F-014] 内容与金样台架单源互证：era2 装载值 == GoldenVerifications
        // 四组验算用的冻结常量（Balance/E2TowerBench——同程序集 internal 可见）
        Assert.Equal(ArrowT1Dps, Era2Content.Arrow.Dps, 9);
        Assert.Equal(CrossbowT2Dps, Era2Content.Crossbow.Dps, 9);
        Assert.Equal(OnagerT3Dps, Era2Content.Onager.Dps, 9);
        Assert.Equal(FireOilT4Dps, Era2Content.FireOil.Dps, 9);
        Assert.Equal(OnagerT3Damage, Era2Content.Onager.DamagePerShot, 9); // SP-3c L132 单发 160
    }

    // ---- 2) 成长律：×2/×3 严格与 ±15% 带（F.5「成本合计 ×3/级严格成立」）----

    [Fact]
    public void Cost_ladder_is_strict_x3_across_the_standard_line()
    {
        // [F-026] F.5「成本合计 ×3/级严格成立：90→270→810→2430」（07 D-15 恢复）
        Assert.Equal(270, Era2Content.Arrow.CostTotal * 3);
        Assert.Equal(810, Era2Content.Crossbow.CostTotal * 3);
        Assert.Equal(2430, Era2Content.Repeater.CostTotal * 3);

        // 特化线不入梯（F.5 原文 tier_line:"specialization" 豁免）：弩炮 700 ≠ 810
        Assert.Equal("specialization", Era2Content.Onager.TierLine);
        Assert.NotEqual(Era2Content.Crossbow.CostTotal * 3, Era2Content.Onager.CostTotal);
    }

    [Fact]
    public void Dps_law_is_x2_per_tier_inside_the_15_percent_band()
    {
        // [F-026] v3.2 §6 L82「DPS ×2/级(±15%)」——标准线三步全在带内（实值恰 ×2）
        var steps = new[]
        {
            (Era2Content.Arrow, Era2Content.Crossbow),
            (Era2Content.Crossbow, Era2Content.Repeater),
            (Era2Content.Repeater, Era2Content.FireOil), // 类变终点档（穿刺→溅射）DPS 律不变
        };
        foreach (var (lower, upper) in steps)
        {
            double ratio = upper.Dps / lower.Dps;
            Assert.InRange(ratio, 2.0 * 0.85, 2.0 * 1.15); // 律带 ±15%
            Assert.Equal(2.0, ratio, 9); // E2 行实值严格 ×2（30→60→120→240）
        }

        // 弩炮对同级基准低 DPS=特化定位（对重甲：F.5 第 20 波 3 弩炮验算线）
        Assert.True(Era2Content.Onager.Dps < Era2Content.Repeater.Dps, "特化线 DPS 低于同级标准线（换甲交互利基）");
    }

    [Fact]
    public void Range_law_is_plus_25_percent_inside_the_design_band()
    {
        // [F-026] v3.2 §6「射程 +25%/级（±15% 设计带内取值，9.5 等非整值合法）」
        double arrowToCrossbow = Era2Content.Crossbow.RangeCells / Era2Content.Arrow.RangeCells;
        Assert.InRange(arrowToCrossbow, 1.25 * 0.85, 1.25 * 1.15); // 6.5→8 = ×1.231

        double crossbowToRepeater = Era2Content.Repeater.RangeCells / Era2Content.Crossbow.RangeCells;
        Assert.InRange(crossbowToRepeater, 1.25 * 0.85, 1.25 * 1.15); // 8→9.5 = ×1.1875（07 修正 #2）

        // 连弩→火油为类变步（穿刺→溅射，F.5 冻结 7.5）：+25% 带不适用——由
        // UpgradeLaw 的类变步规则承接（下方 step 校验），此处锚定 7.5 本值。
        Assert.Equal(7.5, Era2Content.FireOil.RangeCells, 9);
        Assert.Equal(11.0, Era2Content.Onager.RangeCells, 9); // 弩炮 11=F.5 冻结（同级最长，物理特化射程换 DPS）
    }

    [Fact]
    public void UpgradeLaw_validates_the_whole_era2_table()
    {
        // [F-027] 任务验收「UpgradeLaw 校验成长律（对 era2 数据全过）」：
        // 声明块全过 + 线上三步全过（含类变终点步 9.5→7.5 的射程豁免）
        foreach (var spec in Era2Content.Towers)
        {
            Assert.Empty(UpgradeLaw.ValidateGrowthDeclaration(spec));
        }

        var steps = new[]
        {
            (Era2Content.Arrow, Era2Content.Crossbow),
            (Era2Content.Crossbow, Era2Content.Repeater),
            (Era2Content.Repeater, Era2Content.FireOil),
        };
        foreach (var (lower, upper) in steps)
        {
            Assert.Empty(UpgradeLaw.ValidateStep(lower, upper));
        }
    }

    [Fact]
    public void Upgrade_line_wiring_follows_the_F5_ladder()
    {
        // [F-014] 同线升级链（数据接线，SP-1b L41「箭楼同线升级」）：
        // 箭楼→弩塔→连弩→火油；弩炮/火油为线尾（无后继行，不发明 T5）
        Assert.Equal("era2:tower-crossbow", Era2Content.Arrow.UpgradesTo);
        Assert.Equal("era2:tower-repeating-crossbow", Era2Content.Crossbow.UpgradesTo);
        Assert.Equal("era2:tower-fire-oil", Era2Content.Repeater.UpgradesTo);
        Assert.Null(Era2Content.FireOil.UpgradesTo);
        Assert.Null(Era2Content.Onager.UpgradesTo); // 特化线 F.5 无 T4 行

        // tier/max_tier 形状：标准线统一 max 4；特化线止于 3
        Assert.All(new[] { Era2Content.Arrow, Era2Content.Crossbow, Era2Content.Repeater, Era2Content.FireOil },
            spec => Assert.Equal(4, spec.MaxTier));
        Assert.Equal(3, Era2Content.Onager.MaxTier);
    }

    [Fact]
    public void Class_bands_follow_the_SP1a_policy()
    {
        // [F-026] 07 D-15「AOE −20% 仅作用于 DPS（相对同级单体塔）」按塔类
        // 验证带；SP-1a 特化线豁免；支援类不入带（E2 无支援行）
        // T1/T2/T3 标准单体行对自身基准（同级标准单体行=本行）恰在 1.0 带：
        Assert.Empty(UpgradeLaw.ValidateClassBand(Era2Content.Arrow, Era2Content.Arrow.Dps));
        Assert.Empty(UpgradeLaw.ValidateClassBand(Era2Content.Crossbow, Era2Content.Crossbow.Dps));
        Assert.Empty(UpgradeLaw.ValidateClassBand(Era2Content.Repeater, Era2Content.Repeater.Dps));

        // 弩炮=特化线豁免（F.5 tier_line:"specialization"；利基=甲交互非梯一致）
        Assert.Empty(UpgradeLaw.ValidateClassBand(Era2Content.Onager, Era2Content.Repeater.Dps));

        // 火油（T4 溅射）没有同级标准单体参照行（E2 标准线终于溅射终点档）：
        // ×0.8 带无从比起——断言参照缺失这一事实本身，E3 出同级单体行时本断言
        // 强制补上带校验（把带哨兵留在表驱动回归里，而非静默跳过）。
        bool t4StandardSingleExists = Era2Content.Towers.Any(t =>
            t.Tier == 4
            && t.Class is TowerClass.Physical or TowerClass.Pierce or TowerClass.Energy
            && !string.Equals(t.TierLine, UpgradeLaw.SpecializationLine, StringComparison.Ordinal));
        Assert.False(t4StandardSingleExists,
            "E2 无 T4 标准单体行（F.5 行集冻结）——火油 ×0.8 带校验随之 N/A；新增行时必须补 ValidateClassBand");
    }

    [Fact]
    public void Burst_price_law_accepts_the_era2_rows()
    {
        // [F-028] 爆发型代价：同级基准=标准线同级 DPS（T1 30/T2 60/T3 120/T4
        // 240）；五塔 DPS 均不超基准（火油 240=同级投影持平）→ 无需前摇代价
        var baselines = new (TowerSpec Spec, double Baseline)[]
        {
            (Era2Content.Arrow, Era2Content.Arrow.Dps),
            (Era2Content.Crossbow, Era2Content.Crossbow.Dps),
            (Era2Content.Repeater, Era2Content.Repeater.Dps),
            (Era2Content.Onager, Era2Content.Repeater.Dps),
            (Era2Content.FireOil, Era2Content.Repeater.Dps * 2), // T4 标准线投影 240
        };
        foreach (var (spec, baseline) in baselines)
        {
            var verdict = Damage.ValidateBurstCost(spec, baseline);
            Assert.True(verdict.Allowed, $"{spec.Id}: {verdict.Violation}");
        }
    }

    // ---- 3) 换弹系与特性锚点 ---------------------------------------------

    [Fact]
    public void Ammo_line_swaps_per_tier_as_the_upgrade_narrative()
    {
        // [F-014] 换弹系（任务卡 T-065）：弩塔 石矢 → 连弩 铁矢（DPS×2 载体）
        // → 火油 火油矢（燃烧载体）；全部 [NEW]（无文档锚点）
        Assert.Equal("era2:ammo-arrow", Era2Content.Arrow.AmmoItem);
        Assert.Equal("era2:ammo-stone-bolt", Era2Content.Crossbow.AmmoItem);
        Assert.Equal("era2:ammo-iron-bolt", Era2Content.Repeater.AmmoItem);
        Assert.Equal("era2:ammo-siege-bolt", Era2Content.Onager.AmmoItem);
        Assert.Equal("era2:ammo-fire-oil", Era2Content.FireOil.AmmoItem);

        // 铁矢换弹的数值载体：d ×2（18→36）同射速 → DPS ×2（60→120）
        Assert.Equal(Era2Content.Crossbow.DamagePerShot * 2, Era2Content.Repeater.DamagePerShot, 9);
        Assert.Equal(Era2Content.Crossbow.ReloadTicks, Era2Content.Repeater.ReloadTicks);
    }

    public static TheoryData<string, string> FireOilTraitRows => new()
    {
        // 火油特性全 [NEW]（T-065 原型校准）：溅射半径 1.5 格、燃烧 12 DPS×3 s
        { "splash_radius_cells", "1.5" },
        { "burn_dps", "12" },
        { "burn_ticks", "90" },
    };

    [Theory]
    [MemberData(nameof(FireOilTraitRows))]
    public void Fire_oil_traits_carry_the_new_calibration_values(string key, string expected)
    {
        // [F-014] 火油塔特性行回归（[NEW] 值入表随检——漂移即红）
        var traits = FindById(LoadItems(), "era2:tower-fire-oil").GetProperty("traits");
        Assert.True(traits.TryGetProperty(key, out var value), $"缺特性 {key}");
        Assert.Equal(double.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value.GetDouble(), 9);
    }

    [Fact]
    public void New_flag_policy_marks_invented_values_with_sources()
    {
        // [F-156] _new/_source 溯源纪律（11-spec-tables L7：新发明数值一律标注）
        foreach (var item in LoadItems())
        {
            var id = item.GetProperty("id").GetString()!;
            Assert.True(item.TryGetProperty("_new", out var flag), $"{id} 缺 _new 标记");
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("_source").GetString()!), $"{id} 缺 _source");
            Assert.True(item.GetProperty("_units").EnumerateObject().Any(), $"{id} 缺单位注解 _units");

            // F.5 冻结三件（cost_total/dps/range_cells）不得列入 _new_fields
            var newFields = item.GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
            Assert.DoesNotContain("cost_total", newFields);
            Assert.DoesNotContain("dps", newFields);
            Assert.DoesNotContain("range_cells", newFields);
        }

        // 弩炮单发 160=SP-3c [NEW-原型校准] → 须标 _new
        Assert.Contains("damage_per_shot", FindById(LoadItems(), "era2:tower-onager")
            .GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()));
    }
}
