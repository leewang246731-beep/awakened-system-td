using System.Linq;
using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Combat;
using EraCore.Contracts.Content;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.ContentData;

// [C-02] K-2 E3 内容数据行回归（T-112）：game/content/era3/ 五文件
// （era/towers/enemies/recipes/levels）的锚点比对 + 三源一致性矩阵。
// 期望值全部硬编码自文档锚点（出处行写在各断言处注释），JSON 解析失败即红：
//   - E3 时代行       = v3.2 §4 L60（docs/我在游戏觉醒系统-游戏设计方案.md）+§4 能源阶梯/跨代资源行+§5 L78~L79
//   - 能源风险        = SP-4 L146『E3 过热爆炸』表体（[NEW-原型校准] 已定案值）
//   - 能源 L1=300/L2=1200 = T-112 任务卡锚点（倍率 1200/300=4∈[3,5]）
//   - E3 塔组         = §4 L60+SP-1b L46~L48（T-112 章节设计复核追认：加特林=穿刺/臼炮=溅射/铁甲墙=支援）；
//                       数值=F.5 类推锚带 [NEW]（推导式见 towers.json 各行 _source）
//   - E3 敌表         = 新设计 [NEW]（行形对位 SP-3b+Erekir 工业军风格）；Boss HP9000/甲16=§4 逐字锚点
//   - 配方集          = v3.2 §5 L74~L76（链深 ≤3；1 件弹药=10 发）+§4 L60 煤→钢炼链
//   - 前三关排期      = v3.2 §4 L67 + §11.8（对话级→建造级→巡逻/托管级）+§7 公式
//   - 三源互证        = ActionCatalog E3 开放集（空——19 动作封闭清单全于 ≤E2 解锁）↔ levels 排期侧
//                     ↔ campaign/techtree.json E3 节点（T-116 落位前为前向守卫）
public class Era3ContentTests
{
    // ---- 装载辅助（与 Era1/Era2ContentTests 同纪律） ----------------------

    private static DirectoryInfo? FindRepoContentDir(string relativeUnderContent)
    {
        // 仓库相对寻址（RL9：禁绝对路径）：从测试程序集向上找 game/content/<dir>。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", relativeUnderContent);
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe);
            }

            dir = dir.Parent!;
        }

        return null;
    }

    private static string Era3Dir()
    {
        var dir = FindRepoContentDir("era3");
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static JsonDocument LoadDocFrom(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        Assert.True(File.Exists(path), $"内容文件缺失：{fileName}");
        // 严格模式解析：非法 JSON 直接抛异常 → 测试红（解析失败=测试红）
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonDocument LoadDoc(string fileName) => LoadDocFrom(Era3Dir(), fileName);

    private static JsonElement ArrayProperty(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var value), $"缺字段 {name}");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        return value;
    }

    private static JsonElement ObjectProperty(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var value), $"缺字段 {name}");
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        return value;
    }

    private static double NumberProperty(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var value), $"缺字段 {name}");
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        return value.GetDouble();
    }

    private static int IntProperty(JsonElement parent, string name)
    {
        var value = NumberProperty(parent, name);
        Assert.True(Math.Abs(value - Math.Floor(value)) < 1e-9, $"字段 {name} 须为整数（得 {value}）");
        return (int)value;
    }

    private static string StringProperty(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var value), $"缺字段 {name}");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString()!;
    }

    private static List<JsonElement> LoadItems(string fileName)
    {
        // 不 using-dispose：返回的 JsonElement 只在 JsonDocument 存活期间可访问，
        // 测试内文件小且条目固定，由 GC 回收（与 Era1/Era2ContentTests 同约定）。
        var doc = LoadDoc(fileName);
        return ArrayProperty(doc.RootElement, "items").EnumerateArray().ToList();
    }

    private static JsonElement FindById(IEnumerable<JsonElement> items, string id)
    {
        var found = items.FirstOrDefault(i =>
            i.TryGetProperty("id", out var v) && v.GetString() == id);
        Assert.True(found.ValueKind == JsonValueKind.Object, $"缺内容项 {id}");
        return found;
    }

    private static JsonElement Era() => LoadItems("era.json").Single();

    private static List<JsonElement> Levels() => LoadItems("levels.json");

    private static JsonElement Level(int slot) => Levels().Single(l => IntProperty(l, "slot") == slot);

    private static List<string> ScheduledActions()
    {
        // levels.json 三关 unlocks.actions 的并集（三源矩阵的"排期侧"全集）。
        var actions = new List<string>();
        foreach (var level in Levels())
        {
            actions.AddRange(ArrayProperty(ObjectProperty(level, "unlocks"), "actions")
                .EnumerateArray().Select(a => a.GetString()!));
        }

        return actions;
    }

    private static List<string> MatrixDrift(IEnumerable<string> catalogSide, IEnumerable<string> scheduleSide)
    {
        // 矩阵比较器（era2 先例复刻）：任一侧多出/缺失即点名（负向用例喂突变集）。
        var drift = new List<string>();
        drift.AddRange(catalogSide.Except(scheduleSide, StringComparer.Ordinal)
            .Select(v => $"catalog_only:{v}"));
        drift.AddRange(scheduleSide.Except(catalogSide, StringComparer.Ordinal)
            .Select(v => $"schedule_only:{v}"));
        return drift;
    }

    // ---- 1) 解析与结构声明 [F-008] ------------------------------------------

    [Fact]
    public void All_five_era3_files_parse_and_declare_K2_content_types()
    {
        // [F-008] E3 行数据载体：五文件解析+K-2 content_type 声明。
        // recipes 无 K-2 独立类 → 薄版声明 Resource 族（先例=era2/recipes.json，T-079）。
        foreach (var (file, expectedType) in new[]
                 {
                     ("era.json", ContentType.Era),
                     ("towers.json", ContentType.Building),
                     ("enemies.json", ContentType.Enemy),
                     ("recipes.json", ContentType.Resource),
                     ("levels.json", ContentType.Level),
                 })
        {
            using var doc = LoadDoc(file);
            var root = doc.RootElement;

            // $schema 预留字段（与 era1/era2 同格式约定）
            Assert.Matches("^urn:eratd:content:[a-z]+:1$", StringProperty(root, "$schema"));

            // content_type 声明须与 K-2 ContentType 枚举逐字对应（12-contracts-frozen K-2）
            Assert.Equal(expectedType.ToString(), StringProperty(root, "content_type"));

            foreach (var item in ArrayProperty(root, "items").EnumerateArray())
            {
                Assert.Equal(expectedType.ToString(), StringProperty(item, "content_type"));
            }
        }
    }

    [Fact]
    public void Era3_towers_and_enemies_load_through_the_real_loaders()
    {
        // [F-015][F-035] RL7 实装载器回归：TowerSpec.LoadCollection /
        // EnemySpecCatalog.Parse 全量吃下 era3 两表（坏形状直接抛即红）。
        var towers = TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era3Dir(), "towers.json")));
        Assert.Equal(3, towers.Count);
        Assert.All(towers, t => Assert.StartsWith("era3:tower-", t.Id, StringComparison.Ordinal));

        var enemies = EnemySpecCatalog.Parse(File.ReadAllText(Path.Combine(Era3Dir(), "enemies.json")));
        Assert.Equal(9, enemies.Count);
        var boss = enemies.Get("era3:enemy-boss");
        Assert.True(boss.IsBoss);
        Assert.Equal(9000, boss.Hp);      // §4 L60 逐字锚点
        Assert.Equal(16, boss.Armor);     // §4 L60 逐字锚点
        Assert.Equal(600, boss.ShieldPoints); // §6 护盾 E3 起（个体盾 [NEW]）
        Assert.Equal(2.0, boss.TrampleRadiusCells, 9); // 践踏半径 2 格（SP-3a/SP-3b Boss 同参）
    }

    // ---- 2) 时代行：v3.2 §4 E3 行逐项 [F-008] [F-009] [F-011] ---------------

    [Fact]
    public void Era_row_matches_v32_section4_E3_line()
    {
        // [F-008] E3 时代行逐项（资源/能源/物流/仓库/系统形态/Boss）
        var era = Era();

        Assert.Equal(3, IntProperty(era, "era_index")); // §4 L70 时代序号 E3=3
        Assert.Equal("era3:era-industrial", StringProperty(era, "id"));
        Assert.Equal("工业", StringProperty(era, "name_zh"));
        Assert.Equal("Industrial", StringProperty(era, "name_en"));

        // §4 L60 资源=煤/铁/钢 + 上代（E2）2 种基础资源传承刷新（铜/铁，丰度 ×0.5）
        var resources = ArrayProperty(era, "resources").EnumerateArray().ToList();
        var resourceIds = resources.Select(r => StringProperty(r, "id")).ToList();
        Assert.Equal(
            new[]
            {
                "era3:res-coal", "era2:res-iron", "era3:res-steel", "era2:res-copper",
            },
            resourceIds);

        // 传承刷新行：丰度乘数恰 0.5、来自 E2（§4 跨代资源行——上代=紧邻前代 E2，
        // 其基础二资源=铜/铁；铁既是刷新行又是 §4 E3 行点名资源=炼钢主原料）
        foreach (var legacyId in new[] { "era2:res-iron", "era2:res-copper" })
        {
            var refresh = ObjectProperty(
                resources.Single(r => StringProperty(r, "id") == legacyId), "legacy_refresh");
            Assert.Equal(2, IntProperty(refresh, "from_era"));
            Assert.Equal(0.5, NumberProperty(refresh, "abundance_multiplier"));
        }

        // §4 L60 能源=蒸汽
        Assert.Equal("steam", StringProperty(ObjectProperty(era, "energy"), "type"));
        Assert.Equal("蒸汽", StringProperty(ObjectProperty(era, "energy"), "name_zh"));

        // §4 L60 物流=矿轨 10/s（§5 L78 阶梯第 2 档；15.0=传送带=E4 行档位预告）
        var logistics = ObjectProperty(era, "logistics");
        Assert.Equal(2, IntProperty(logistics, "tier"));
        Assert.Equal("mine_rail", StringProperty(logistics, "carrier"));
        Assert.Equal("矿轨", StringProperty(logistics, "carrier_zh"));
        Assert.Equal(10.0, NumberProperty(logistics, "throughput_items_per_s"));
        var ladder = ArrayProperty(logistics, "ladder_items_per_s");
        Assert.Equal(6, ladder.GetArrayLength());
        Assert.Equal(0.3, ladder[0].GetDouble());
        Assert.Equal(0.6, ladder[1].GetDouble());
        Assert.Equal(10.0, ladder[2].GetDouble()); // E3 档位=阶梯第 2 档，与 throughput 同值
        Assert.Equal(15.0, ladder[3].GetDouble()); // E4 传送带档（§4 L61——E3 不提前启用）
        Assert.Equal(20.0, ladder[4].GetDouble());
        Assert.Equal(JsonValueKind.Null, ladder[5].ValueKind); // 末位 null=∞

        // §5 L79 仓库等级跃迁自动 +1：E2=2→E3=3
        Assert.Equal(3, IntProperty(era, "warehouse_level"));

        // §4 L60 系统形态=自主期
        Assert.Equal("autonomous", StringProperty(era, "system_stage"));
        Assert.Equal("自主期", StringProperty(era, "system_stage_zh"));
        var features = ArrayProperty(era, "system_stage_features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("auto_execute_approved_plans", features); // §4 L60『自动执行获批计划』

        // §4 L60 Boss=堡垒号 HP9000/甲16（逐字锚点）
        var boss = ObjectProperty(era, "boss");
        Assert.Equal("era3:enemy-boss", StringProperty(boss, "enemy_id"));
        Assert.Equal(9000, NumberProperty(boss, "hp"));
        Assert.Equal(16, NumberProperty(boss, "armor"));
        Assert.Equal("堡垒号", StringProperty(boss, "name_zh"));
    }

    [Fact]
    public void Era_energy_ladder_is_300_then_1200_within_v32_growth_band()
    {
        // [F-009] 能源阶梯 300→1200、倍率 ∈[3,5]
        // 能源阶梯锚点：L1=300（蒸汽机）、L2=1200（锅炉）（T-112 任务卡）；倍率
        // 1200/300=4 须落在 v3.2 §4 能源阶梯行「×3~5/级」带内（F-009 验收）
        var energy = ObjectProperty(Era(), "energy");

        var levels = ArrayProperty(energy, "levels");
        Assert.Equal(2, levels.GetArrayLength());
        Assert.Equal(1, IntProperty(levels[0], "tier"));
        Assert.Equal(300, NumberProperty(levels[0], "output"));
        Assert.Equal(2, IntProperty(levels[1], "tier"));
        Assert.Equal(1200, NumberProperty(levels[1], "output"));

        var ratio = NumberProperty(levels[1], "output") / NumberProperty(levels[0], "output");
        Assert.InRange(ratio, 3.0, 5.0);

        // E3 起负载率类阶梯风险开闸（SP-4 L142：E1/E2 risk_events=false 的反面）
        Assert.True(energy.GetProperty("risk_events").GetBoolean());
    }

    // boundary/legal：SP-4 L146 表体逐值（[NEW-原型校准] 已定案——漂移即红）
    [Theory]
    [MemberData(nameof(OverheatParamRows))]
    public void Overheat_risk_params_match_SP4_row_exactly(string jsonPath, double expected)
    {
        // [F-010] SP-4 L146『E3 过热爆炸』行全值入内容数据（RL7：阈值禁止硬编码于引擎）
        var risk = ObjectProperty(ObjectProperty(Era(), "energy"), "risk");
        Assert.Equal("overheat_explosion", StringProperty(risk, "kind"));

        // 走点路径 "trigger.overload_threshold_percent" 逐段下钻（末段为数值叶）
        var segments = jsonPath.Split('.');
        var node = risk;
        foreach (var segment in segments[..^1])
        {
            node = ObjectProperty(node, segment);
        }

        Assert.Equal(expected, NumberProperty(node, segments[^1]), 9);
    }

    public static TheoryData<string, double> OverheatParamRows => new()
    {
        // 出处：11-spec-tables.md SP-4 L146 E3 过热爆炸行（触发/冷却中断/半径/后果四列）
        { "trigger.overload_threshold_percent", 100 },            // 负载率 ≥100%
        { "trigger.sustained_s", 30 },                            // 连续 30 s
        { "trigger.heat_capacity", 100 },                         // 热量槽达 100
        { "heat_dynamics.gain_per_s_overloaded", 2 },             // 过热期 +2/s
        { "heat_dynamics.overload_step_percent", 10 },            // 阶梯步长=每 +10% 负载率
        { "heat_dynamics.bonus_gain_per_s_per_extra_10pct_overload", 1 }, // 每 +10% 追加 +1/s
        { "heat_dynamics.decay_per_s_unloaded", 5 },              // 卸载后 −5/s 回落
        { "heat_dynamics.min_seconds_to_full_heat_at_sustained_overload", 50 }, // 0→100 需 ≥50 s
        { "explosion_radius_cells", 4 },                          // 爆炸半径 4 格
        { "consequences.building_damage_fraction_of_max_hp", 0.5 }, // 对建筑其最大 HP×50%
        { "consequences.enemy_damage", 300 },                     // 对敌 300 伤害
        { "consequences.culprit_outage_s", 60 },                  // 停机 60 s
        { "consequences.culprit_degraded_output_fraction", 0.5 }, // 以 50% 功率运行
        { "consequences.culprit_degraded_duration_s", 30 },       // 运行 30 s
    };

    [Fact]
    public void Era_row_references_resolve_across_all_sibling_era3_files()
    {
        // [F-008] 时代行跨文件引用完整性：塔组/Boss/时代 id 三文件合流
        var era = Era();
        var eraId = StringProperty(era, "id");

        // era.towers 恰等于 towers.json 全体 id（§4 L60 塔防特色列=三塔 ↔ 实存行）
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var eraTowers = ArrayProperty(era, "towers").EnumerateArray().Select(t => t.GetString()!).ToList();
        Assert.Equal(3, eraTowers.Count);
        Assert.All(eraTowers, id => Assert.Contains(id, towerIds));
        Assert.Equal(towerIds.OrderBy(i => i, StringComparer.Ordinal),
            eraTowers.OrderBy(i => i, StringComparer.Ordinal));

        // era.boss 指向 enemies.json Boss 行且 HP/甲同值（v3.2 §4 逐字锚点不得漂移）
        var bossRef = StringProperty(ObjectProperty(era, "boss"), "enemy_id");
        var bossEnemy = FindById(LoadItems("enemies.json"), bossRef);
        Assert.True(bossEnemy.GetProperty("is_boss").GetBoolean());
        Assert.Equal(NumberProperty(bossEnemy, "hp"), NumberProperty(ObjectProperty(era, "boss"), "hp"));
        Assert.Equal(NumberProperty(bossEnemy, "armor"), NumberProperty(ObjectProperty(era, "boss"), "armor"));

        // 时代 id 合流：towers/enemies/recipes/levels 根 era 字段全部同指
        Assert.Equal(eraId, StringProperty(LoadDoc("towers.json").RootElement, "era"));
        Assert.Equal(eraId, StringProperty(LoadDoc("enemies.json").RootElement, "era"));
        Assert.Equal(eraId, StringProperty(LoadDoc("recipes.json").RootElement, "era"));
        Assert.Equal(eraId, StringProperty(LoadDoc("levels.json").RootElement, "era"));

        // 自主期系统形态标记 ↔ 关 3 托管级排期（§4 L60『自主期（自动执行获批计划）』
        // ↔ §4 L67 关 3 系统新能力 ↔ §11.8 巡逻/托管级末档）
        var slot3Features = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features")
            .EnumerateArray().Select(f => f.GetString()!).ToList();
        Assert.Contains("autonomous_execution", slot3Features);
    }

    // ---- 3) 塔组：§4 L60 三塔 + SP-1b 定类 + F.5 类推锚带 [F-015] ----------

    private static IReadOnlyList<TowerSpec> Towers() =>
        TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era3Dir(), "towers.json")));

    public static TheoryData<string, double, double, double, string, int> TowerRows => new()
    {
        // §4 L60 塔防特色=加特林/臼炮/铁甲墙；定类=SP-1b L46~L48 预归类经 T-112
        // 章节设计复核追认；数值=F.5 类推锚带 [NEW]（推导式见 towers.json _source）
        { "era3:tower-gatling", 270, 240.0, 8.0, "pierce", 1 },   // 成本 90×3 跨代基点步
        { "era3:tower-mortar", 810, 480.0, 11.0, "splash", 2 },   // 270×3 严格；240×2 严格
        { "era3:tower-ironclad-wall", 120, 0.0, 0.0, "support", 1 }, // 纯阻挡零伤害零射程
    };

    [Theory]
    [MemberData(nameof(TowerRows))]
    public void Towers_match_SP1b_classes_and_F5_analog_anchor_band(
        string id, double cost, double dps, double range, string towerClass, int tier)
    {
        // [F-015] 三塔定类（SP-1b L46~L48 T-112 追认）+F.5 类推锚带值（[NEW] 入表随检）
        var spec = Towers().Single(t => t.Id == id);
        Assert.Equal(towerClass, spec.Class.ToString().ToLowerInvariant());
        Assert.Equal(tier, spec.Tier);
        Assert.Equal(cost, spec.CostTotal);
        Assert.Equal(dps, spec.Dps, 9);
        Assert.Equal(range, spec.RangeCells, 9);
    }

    [Fact]
    public void Ironclad_wall_is_the_pure_blocker_zero_damage_zero_range()
    {
        // [F-015] SP-1b L48『铁甲墙=支援：纯阻挡、零伤害（不计 DPS 带；HP 计入
        // 流场代价 ×5 规则）』——T-112 章节设计复核落定（round 2 处置 #9 挂接）。
        var wall = Towers().Single(t => t.Id == "era3:tower-ironclad-wall");
        Assert.Equal(TowerClass.Support, wall.Class);
        Assert.Equal(0.0, wall.Dps, 9);
        Assert.Equal(0.0, wall.DamagePerShot, 9);
        Assert.Null(wall.ReloadTicks);   // 无武器循环（E1 陷阱/篝火先例）
        Assert.Equal(0.0, wall.RangeCells, 9);
        Assert.Null(wall.AmmoItem);      // 无弹药（不入弹药覆盖闭合）
        Assert.Equal(0, wall.AmmoPerShot);
        Assert.Equal(1, wall.MaxTier);   // 墙体不升级（无后继行不发明）
        Assert.Null(wall.UpgradesTo);
        Assert.Equal(1600, wall.Hp);     // 阻挡利基：HP 显著高于同级武器塔（[NEW]，流场 ×5 代价标的）
    }

    [Fact]
    public void DPS_split_stays_self_consistent_on_the_tick_grid()
    {
        // [F-027] 内部一致性：dps = damage_per_shot × 30 / reload_ticks（§6 符号
        // 约定 d=单发伤害，30 Hz）——武器塔全过（铁甲墙无武器周期豁免）
        foreach (var spec in Towers().Where(t => t.ReloadTicks is not null))
        {
            var expected = spec.DamagePerShot * 30.0 / spec.ReloadTicks!.Value;
            Assert.Equal(expected, spec.Dps, 3);
        }
    }

    [Fact]
    public void UpgradeLaw_validates_the_whole_era3_table()
    {
        // [F-027] 任务验收「塔过 UpgradeLaw」：声明块全过 + 标准线步全过
        var towers = Towers();
        foreach (var spec in towers)
        {
            Assert.Empty(UpgradeLaw.ValidateGrowthDeclaration(spec));
        }

        // 标准线唯一步：加特林 T1 → 臼炮 T2（成本 270→810 严格 ×3；DPS 240→480
        // 严格 ×2；类变步穿刺→溅射射程带豁免，11.0 仍落 1.25±15% 带内+半格网格）
        var gatling = towers.Single(t => t.Id == "era3:tower-gatling");
        var mortar = towers.Single(t => t.Id == "era3:tower-mortar");
        Assert.Empty(UpgradeLaw.ValidateStep(gatling, mortar));

        // 严格 ×3 成本阶梯（F.5 90→270→810→2430 同带重演）
        Assert.Equal(gatling.CostTotal * 3, mortar.CostTotal);
        // DPS 律带 ±15%（实值恰 ×2）
        Assert.InRange(mortar.Dps / gatling.Dps, 2.0 * 0.85, 2.0 * 1.15);
        Assert.Equal(2.0, mortar.Dps / gatling.Dps, 9);
        // 接线：加特林→臼炮为线尾步（upgrades_to 指向实存行；臼炮/铁甲墙无后继）
        Assert.Equal("era3:tower-mortar", gatling.UpgradesTo);
        Assert.Null(mortar.UpgradesTo);
        Assert.Equal(gatling.MaxTier, mortar.MaxTier);
    }

    [Fact]
    public void Class_bands_follow_the_SP1a_policy_with_the_sentinel_kept()
    {
        // [F-026] 按塔类验证带（SP-1a）：加特林（T1 穿刺标准单体行）对自身基准恰在带；
        // 铁甲墙=支援类豁免（零伤不入带）
        var towers = Towers();
        var gatling = towers.Single(t => t.Id == "era3:tower-gatling");
        var mortar = towers.Single(t => t.Id == "era3:tower-mortar");
        var wall = towers.Single(t => t.Id == "era3:tower-ironclad-wall");

        Assert.Empty(UpgradeLaw.ValidateClassBand(gatling, gatling.Dps));
        Assert.Empty(UpgradeLaw.ValidateClassBand(wall, gatling.Dps)); // support 豁免

        // 臼炮（T2 溅射）没有同级标准单体参照行（E3 行集冻结=三塔）：×0.8 带无从
        // 比起——断言参照缺失这一事实本身（E2 火油同款哨兵先例），E3 出同级单体
        // 行时本断言强制补上带校验。
        bool t2StandardSingleExists = towers.Any(t =>
            t.Tier == 2
            && t.Class is TowerClass.Physical or TowerClass.Pierce or TowerClass.Energy
            && !string.Equals(t.TierLine, UpgradeLaw.SpecializationLine, StringComparison.Ordinal));
        Assert.False(t2StandardSingleExists,
            "E3 无 T2 标准单体行（三塔行集冻结）——臼炮 ×0.8 带校验随之 N/A；新增行时必须补 ValidateClassBand");
    }

    [Fact]
    public void Burst_price_law_accepts_the_era3_rows()
    {
        // [F-028] 爆发型代价：加特林/铁甲墙对自身基准、臼炮对 T2 标准线投影
        // （240×2=480）均不超基准 → 无需前摇代价（持续型/阻挡型）
        var towers = Towers();
        var gatling = towers.Single(t => t.Id == "era3:tower-gatling");
        var mortar = towers.Single(t => t.Id == "era3:tower-mortar");
        var wall = towers.Single(t => t.Id == "era3:tower-ironclad-wall");

        var baselines = new (TowerSpec Spec, double Baseline)[]
        {
            (gatling, gatling.Dps),
            (mortar, gatling.Dps * 2), // T2 标准线投影 480
            (wall, gatling.Dps),
        };
        foreach (var (spec, baseline) in baselines)
        {
            var verdict = Damage.ValidateBurstCost(spec, baseline);
            Assert.True(verdict.Allowed, $"{spec.Id}: {verdict.Violation}");
        }
    }

    // ---- 4) 敌表：新设计行 + §4 Boss 逐字锚点 [F-035] [F-032] ---------------

    public static TheoryData<string, double, double, double> EnemyStatRows => new()
    {
        // E3 敌表=新设计 [NEW]（行形对位 SP-3b 各位+跨代步 ×2；Erekir 工业军风格）；
        // 表体值入表随检——漂移即红。Boss HP/甲=v3.2 §4 L60 逐字锚点。
        { "e3_conscript", 120, 0, 1.60 },       // 散兵位（E2 散兵 60×2）
        { "e3_line_infantry", 600, 6, 1.17 },   // 中坚位（勇士 300×2、甲 4→6）
        { "e3_ram", 3000, 16, 0.53 },           // 攻城位（攻城锤 1500×2、甲 12→16）
        { "e3_scout_rider", 280, 2, 2.67 },     // 快速位（轻骑 140×2）
        { "e3_gunner", 360, 4, 1.07 },          // 远程位（弩手 180×2、甲 3→4）
        { "e3_ironclad_guard", 1200, 12, 0.85 },// 重甲位（卫士 600×2、甲 8→12）
        { "e3_sapper", 240, 1, 1.71 },          // 自爆位（火矢手 120×2）
        { "e3_overseer", 1800, 10, 0.96 },      // 精英光环位（百夫长 900×2、甲 6→10）
        { "e3_boss", 9000, 16, 0.53 },          // Boss（§4 逐字 9000/16；速度 0.5 格/s [NEW]）
    };

    [Theory]
    [MemberData(nameof(EnemyStatRows))]
    public void Enemies_match_the_E3_rows_hp_armor_speed(string spId, double hp, double armor, double speed)
    {
        // [F-035] E3 敌表九种数值锚点（表体 [NEW] 入表随检）
        var items = LoadItems("enemies.json");
        Assert.Equal(9, items.Count); // E3 恰九种（8 常规+Boss，行形对位 SP-3b 九行制）

        var enemy = items.Single(i => StringProperty(i, "sp_id") == spId);
        Assert.Equal(hp, NumberProperty(enemy, "hp"));
        Assert.Equal(armor, NumberProperty(enemy, "armor"));
        Assert.Equal(speed, NumberProperty(enemy, "speed_px_per_tick"));

        // 换算列自洽：speed × 30 / 32 = 格/s（SP-3 头部 L92 换算式，1 格=32 px）
        Assert.Equal(speed * 30.0 / 32.0, NumberProperty(enemy, "speed_cells_per_s"), 2);
    }

    [Fact]
    public void Boss_carries_war_cry_and_trample_trait_params()
    {
        // [F-033] T-112 任务卡『堡垒号 Boss 含战吼/践踏技能参数』
        var boss = FindById(LoadItems("enemies.json"), "era3:enemy-boss");
        Assert.True(boss.GetProperty("is_boss").GetBoolean());

        // 践踏：半径 2 格（SP-3a L107 踏群者/SP-3b L121 铁蹄同参锚）
        var trample = ObjectProperty(ObjectProperty(boss, "traits"), "trample");
        Assert.Equal(2, NumberProperty(trample, "radius_cells"));

        // 战吼：半径 3 格友军 +15% 速度×180 ticks（6 s）、冷却 600 ticks（20 s）
        // ——加成带对齐 SP-3b L120 战鼓行，主动化周期 [NEW-原型校准]
        var warCry = ObjectProperty(ObjectProperty(boss, "traits"), "war_cry");
        Assert.Equal(3, NumberProperty(warCry, "radius_cells"));
        Assert.Equal(15, NumberProperty(warCry, "speed_bonus_percent"));
        Assert.Equal(180, NumberProperty(warCry, "duration_ticks"));
        Assert.Equal(600, NumberProperty(warCry, "cooldown_ticks"));

        // 常规 Boss 尺寸 64~96 px（v3.2 §13.1）→画布取上沿 96
        Assert.Equal(96, NumberProperty(boss, "canvas_size_px"));
    }

    [Fact]
    public void Shields_are_present_from_E3_on_the_heavy_rows()
    {
        // [F-032] v3.2 §6「护盾 E3 起」：重甲行+Boss 携带个体 shield 键（[NEW] 数据位，
        // 再生/穿甲打断实装=T-114）；轻中甲行不携带
        var items = LoadItems("enemies.json");
        Assert.Equal(240, NumberProperty(
            FindById(items, "era3:enemy-ironclad-guard"), "shield"));
        Assert.Equal(600, NumberProperty(
            FindById(items, "era3:enemy-boss"), "shield"));

        foreach (var id in new[]
                 { "era3:enemy-conscript", "era3:enemy-line-infantry", "era3:enemy-overseer" })
        {
            Assert.False(FindById(items, id).TryGetProperty("shield", out _),
                $"{id} 非重甲位不应携带个体盾");
        }
    }

    [Fact]
    public void Enemy_counters_use_closed_tower_class_or_focus_fire()
    {
        // [F-035] 克制指向封闭枚举（六类 SP-1 L19+集火意图保留值）
        var allowed = new HashSet<string>
        {
            "physical", "pierce", "splash", "energy", "slow", "support", "focus_fire",
        };
        foreach (var enemy in LoadItems("enemies.json"))
        {
            foreach (var counter in ArrayProperty(enemy, "counters").EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, counter.ValueKind);
                Assert.Contains(counter.GetString()!, allowed);
            }
        }
    }

    // ---- 5) 配方集：§5 链深守卫 + 煤→钢炼链 [F-022] [F-023] -----------------

    [Fact]
    public void Raw_resources_exclude_steel_the_crafted_era_resource()
    {
        // [F-023] 原料表口径：煤/铁/铜三件（铁/铜=传承刷新）；钢=era.json 资源行
        // 成员（§4 L60 点名）但为炼链制品——SupplyChain one key one meaning，制品
        // 不入原料表（era2 两表相等先例自 E3 起分立：§4 点名资源含制品）。
        var eraResourceIds = ArrayProperty(Era(), "resources").EnumerateArray()
            .Select(r => StringProperty(r, "id")).ToHashSet();
        var raws = ArrayProperty(LoadDoc("recipes.json").RootElement, "raw_resources").EnumerateArray()
            .Select(r => StringProperty(r, "resource")).ToList();

        Assert.Equal(new[] { "era3:res-coal", "era2:res-iron", "era2:res-copper" }, raws);
        Assert.All(raws, r => Assert.Contains(r, eraResourceIds)); // 原料 ⊆ 时代资源行
        Assert.Contains("era3:res-steel", eraResourceIds);         // 钢在资源行（§4 点名）……

        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();
        Assert.Contains("era3:res-steel", outputs);                // ……且为配方输出（非原料）
    }

    [Fact]
    public void Recipes_cover_every_era3_weapon_tower_ammo_item_exactly_once()
    {
        // [F-023] 弹药覆盖闭合：武器塔（ammo_item 非空）弹药件恰有一条配方；
        // 铁甲墙无弹药不入闭合。配方集构成=中间件 1（钢）+弹药 2（弹链/臼炮弹）。
        var recipeOutputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource"))
            .ToList();

        foreach (var tower in LoadItems("towers.json"))
        {
            if (!tower.TryGetProperty("ammo_item", out var ammoEl) || ammoEl.ValueKind is JsonValueKind.Null)
            {
                continue; // 铁甲墙：纯阻挡无弹药
            }

            var ammo = ammoEl.GetString()!;
            Assert.True(recipeOutputs.Count(o => o == ammo) == 1, $"弹药 {ammo} 的配方数须恰为 1");
        }

        Assert.Equal(3, recipeOutputs.Count);
        var kinds = LoadItems("recipes.json").Select(r => StringProperty(r, "kind")).ToList();
        Assert.Equal(1, kinds.Count(k => k == "intermediate"));
        Assert.Equal(2, kinds.Count(k => k == "ammo"));
    }

    [Fact]
    public void Recipe_baskets_follow_the_AmmoRecipe_shape_rules()
    {
        // [F-023] 篮子形状守卫：对齐 AmmoRecipe 构造约束（输入非空/整件 ≥1/无重复
        // 资源/时长 ≥1 tick/输出唯一）
        var outputs = new HashSet<string>();
        foreach (var recipe in LoadItems("recipes.json"))
        {
            var id = StringProperty(recipe, "id");
            var inputs = ArrayProperty(recipe, "inputs").EnumerateArray().ToList();
            Assert.NotEmpty(inputs); // AmmoRecipe：至少一条输入
            var inputIds = new List<string>();
            foreach (var input in inputs)
            {
                var amount = IntProperty(input, "amount");
                Assert.True(amount >= 1, $"{id}: 输入数量须为整件 ≥1");
                inputIds.Add(StringProperty(input, "resource"));
            }

            Assert.Equal(inputIds.Count, inputIds.Distinct().Count()); // 无重复资源行
            Assert.Equal(1, IntProperty(ObjectProperty(recipe, "output"), "amount")); // 一 craft 产 1 件
            Assert.True(IntProperty(recipe, "craft_ticks") >= 1, $"{id}: craft_ticks ≥1");

            var output = StringProperty(ObjectProperty(recipe, "output"), "resource");
            Assert.True(outputs.Add(output), $"{output} 重复出现在多条配方（一键一义）");
        }
    }

    [Fact]
    public void Recipe_chain_passes_T034_SupplyChain_guard_within_depth_ceiling()
    {
        // [F-022][F-023] 链深 ≤3 守卫（接 T-034 SupplyChain.Validate）：装载即校验
        var doc = LoadDoc("recipes.json");

        var chain = new SupplyChain();
        foreach (var raw in ArrayProperty(doc.RootElement, "raw_resources").EnumerateArray())
        {
            chain.RegisterRaw(new ResourceKey(StringProperty(raw, "resource")));
        }

        foreach (var recipe in LoadItems("recipes.json"))
        {
            var inputs = ArrayProperty(recipe, "inputs").EnumerateArray()
                .Select(i => new ResourceKey(StringProperty(i, "resource"))).ToArray();
            chain.RegisterRecipe(new SupplyChain.ChainRecipe(
                inputs, new ResourceKey(StringProperty(ObjectProperty(recipe, "output"), "resource"))));
        }

        Assert.Empty(chain.Validate()); // 无 unknown_input/无环/无链深违例

        // 声明链深与 T-034 计深一致（原料=1，制品=1+最深输入）
        foreach (var recipe in LoadItems("recipes.json"))
        {
            var output = StringProperty(ObjectProperty(recipe, "output"), "resource");
            Assert.Equal(IntProperty(recipe, "chain_depth"), chain.DepthOf(new ResourceKey(output)));
        }

        // boundary：弹链/臼炮弹=链深天花板锚（煤/铁/铜(1)→钢(2)→弹药(3)，恰用满 ≤3）
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era3:ammo-bullet")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era3:ammo-shell")));
        Assert.Equal(SupplyChain.MaxDepth, chain.DepthOf(new ResourceKey("era3:ammo-bullet")));
    }

    [Fact]
    public void Refreshed_resources_have_E3_uses_in_the_baskets()
    {
        // [F-011] 传承刷新资源确有本代用途（§4 跨代资源行刷新语义落位）：
        // 铁→钢炼链主料；铜→弹链弹壳位
        var usedRaws = LoadItems("recipes.json")
            .SelectMany(r => ArrayProperty(r, "inputs").EnumerateArray()
                .Select(i => StringProperty(i, "resource"))).ToHashSet();
        Assert.Contains("era2:res-iron", usedRaws);
        Assert.Contains("era2:res-copper", usedRaws);
        Assert.Contains("era3:res-coal", usedRaws);
    }

    // ---- 6) 前三关排期：§4 L67 + §11.8 + §7 公式 [F-012] [F-092] ------------

    public static TheoryData<int, string, string, string, int, double, int> LevelScheduleRows => new()
    {
        // 出处：v3.2 §4 L67 排期（关 1 新能源=蒸汽/关 2 新物流=矿轨/关 3 考古+英雄+系统新能力）
        // +§11.8 三级（对话级→建造级→巡逻/托管级）+§7 公式（邻接敌营→威胁度→胜波）
        { 1, "campaign:level-e3-01", "new_energy", "dialogue", 0, 0.3, 13 },
        { 2, "campaign:level-e3-02", "new_logistics", "build", 1, 0.6, 16 },
        { 3, "campaign:level-e3-03", "archaeology_hero_system", "patrol", 2, 0.9, 19 },
    };

    [Theory]
    [MemberData(nameof(LevelScheduleRows))]
    public void Levels_match_schedule_tier_and_formula_anchors(
        int slot, string id, string focus, string tier, int camps, double threat, int winWaves)
    {
        // [F-012] 前三关排期结构与 §7 公式逐值比对
        var levels = Levels();
        Assert.Equal(3, levels.Count); // 恰前三关（T-112 范围；后续关随 T-116 战役制作）
        var level = Level(slot);

        Assert.Equal(id, StringProperty(level, "id"));
        Assert.Equal(focus, StringProperty(level, "focus"));
        Assert.Equal(tier, StringProperty(level, "schedule_tier"));
        Assert.Equal("era3:era-industrial", StringProperty(level, "era"));

        // §7：威胁度 = clamp(0.3+0.3×邻接敌营, 0.3, 1.2)；胜波 = 10+⌊威胁度×10⌋
        Assert.Equal(camps, IntProperty(level, "adjacent_enemy_camps"));
        Assert.Equal(threat, NumberProperty(level, "threat"), 3);
        Assert.Equal(Math.Clamp(0.3 + 0.3 * camps, 0.3, 1.2), NumberProperty(level, "threat"), 3);
        Assert.Equal(winWaves, IntProperty(level, "win_waves"));
        Assert.Equal(10 + (int)Math.Floor(threat * 10), IntProperty(level, "win_waves"));
    }

    [Fact]
    public void Level_unlock_features_match_the_schedule_focus_of_each_slot()
    {
        // [F-012][F-010] 关 1 蒸汽教学/关 2 矿轨/关 3 考古+英雄+系统新能力：
        // 解锁特征逐关对位
        // L1：蒸汽系统+过热爆炸风险（T-112 任务卡『关 1 蒸汽教学』；SP-4 自本关生效）
        var l1 = Level(1);
        var f1 = ArrayProperty(ObjectProperty(l1, "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("steam_energy", f1);
        Assert.Contains("overheat_risk", f1);
        Assert.True(l1.GetProperty("teaching_level").GetBoolean());
        Assert.Equal(3.0, NumberProperty(l1, "first_wave_grace_multiplier")); // §6 教学关 ×3
        Assert.False(l1.GetProperty("awakening_cinematic").GetBoolean()); // 觉醒演出=E2 L1 专属锚（§10.4）

        // L2：矿轨 10/s（§4 L60 物流列；传送带 15/E4 行不提前启用）
        var f2 = ArrayProperty(ObjectProperty(Level(2), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("mine_rail_logistics", f2);

        // L3：考古+英雄（G.2/SP-6 每代第 3 关）+系统新能力=自主执行（§4 L60 自主期）
        var f3 = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("archaeology", f3);
        Assert.Contains("hero_recruit", f3);
        Assert.Contains("autonomous_execution", f3);

        // 非教学关首波宽限在 §6 带 ×1.5~2 内
        Assert.InRange(NumberProperty(Level(2), "first_wave_grace_multiplier"), 1.5, 2.0);
        Assert.InRange(NumberProperty(Level(3), "first_wave_grace_multiplier"), 1.5, 2.0);

        // 前三关难度递进（威胁 0.3<0.6<0.9 → 胜波 13<16<19）
        Assert.True(NumberProperty(Level(1), "threat") < NumberProperty(Level(2), "threat"));
        Assert.True(NumberProperty(Level(2), "threat") < NumberProperty(Level(3), "threat"));
        Assert.True(IntProperty(Level(1), "win_waves") < IntProperty(Level(2), "win_waves"));
        Assert.True(IntProperty(Level(2), "win_waves") < IntProperty(Level(3), "win_waves"));
    }

    // ---- 7) 三源一致性矩阵：ActionCatalog ↔ levels ↔ techtree [F-092] -------

    [Fact]
    public void ActionCatalog_E3_open_set_equals_level_unlock_schedule_exactly()
    {
        // [F-092] 三源互证①：G.2 解锁时代列（代码行源）——19 动作封闭清单中
        // UnlockEra==E3 的动词恰为零（全部已于 ≤E2 解锁），排期侧三关
        // unlocks.actions 并集须同为空——双向漂移即红。
        var catalogE3 = ActionCatalog.All
            .Where(d => d.UnlockEra == EraOrdinals.E3)
            .Select(d => d.Verb)
            .ToList();

        var scheduled = ScheduledActions();
        Assert.Empty(catalogE3);   // 代码侧：无 E3 新动词
        Assert.Empty(scheduled);   // 排期侧：零动作排期
        Assert.Empty(MatrixDrift(catalogE3, scheduled));

        // 代码侧补证：19 动作全体 UnlockEra ≤E2（E3 无未解锁动作=『自主期』
        // 非动作扩员的代码侧事实，§4 L60 自主期注）
        Assert.Equal(19, ActionCatalog.All.Count);
        Assert.All(ActionCatalog.All, d => Assert.True(d.UnlockEra <= EraOrdinals.E2));

        // 排期侧形状：每关 unlocks.actions 数组存在且为空（schema 正则性——
        // 空数组显式携带，防缺键歧义）
        foreach (var level in Levels())
        {
            Assert.Equal(0, ArrayProperty(ObjectProperty(level, "unlocks"), "actions").GetArrayLength());
        }
    }

    [Fact]
    public void Techtree_E3_nodes_cross_reference_only_real_era3_rows()
    {
        // [F-092] 三源互证②（techtree E3 侧前向守卫）：campaign/techtree.json
        // 尚无 E3 节点（T-116 落位）——本守卫保证此后任何 era_index==3 节点的
        // bound_levels/unlocks/前置都解析到实存内容行，悬空引用即红。
        var campaignDir = FindRepoContentDir("campaign");
        Assert.NotNull(campaignDir);
        var allTech = LoadDocFrom(campaignDir!.FullName, "techtree.json").RootElement
            .GetProperty("items").EnumerateArray().ToList();
        var techE3 = allTech.Where(t => t.GetProperty("era_index").GetInt32() == 3).ToList();

        var levelIds = Levels().Select(l => StringProperty(l, "id")).ToHashSet();
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var allTechIds = allTech.Select(t => t.GetProperty("id").GetString()!).ToHashSet();

        foreach (var node in techE3)
        {
            var nodeId = StringProperty(node, "id");
            foreach (var bound in ArrayProperty(node, "bound_levels").EnumerateArray())
            {
                Assert.Contains(bound.GetString()!, levelIds); // 关卡闸→era3 levels 实存行
            }

            foreach (var unlocked in ArrayProperty(node, "unlocks").EnumerateArray())
            {
                var target = unlocked.GetString()!;
                if (StringProperty(node, "kind") == "tower_unlock")
                {
                    Assert.Contains(target, towerIds); // 塔解锁→era3 towers 实存行
                }
                else
                {
                    Assert.StartsWith("campaign:", target); // 能力 id 归战役命名空间
                }
            }

            foreach (var prereq in ArrayProperty(node, "prerequisites").EnumerateArray())
            {
                Assert.Contains(prereq.GetString()!, allTechIds); // 三重闸门前置可解析
            }
        }
    }

    [Fact]
    public void Legacy_transition_E3_schedule_entries_align_when_T117_lands()
    {
        // [F-012] 三源互证③（era_transition 前向守卫）：legacy/era_transition.json
        // 目前仅冻结 E1→E2（T-074）；E2→E3 first_three_schedule 扩展=T-117。本守卫
        // 保证此后任何 campaign:level-e3-* 排期行与 levels.json 逐值对齐。
        var legacyDir = FindRepoContentDir("legacy");
        Assert.NotNull(legacyDir);
        var schedule = ArrayProperty(
            LoadDocFrom(legacyDir!.FullName, "era_transition.json").RootElement,
            "first_three_schedule").EnumerateArray()
            .Where(s => s.TryGetProperty("level_id", out var id)
                        && id.GetString()!.StartsWith("campaign:level-e3-", StringComparison.Ordinal))
            .ToList();

        foreach (var frozen in schedule)
        {
            var slot = IntProperty(frozen, "slot");
            var level = Level(slot);
            Assert.Equal(StringProperty(frozen, "level_id"), StringProperty(level, "id"));
            Assert.Equal(StringProperty(frozen, "focus"), StringProperty(level, "focus"));
            Assert.Equal(StringProperty(frozen, "focus_zh"), StringProperty(level, "focus_zh"));
        }
    }

    [Fact]
    public void Consistency_comparator_flags_drift_on_a_mutated_matrix()
    {
        // illegal — [F-092] 负向用例：矩阵比较器对突变集必须点名漂移
        //（防"比较器恒空"假绿——喂入伪造的 catalog_only/schedule_only 两侧漂移）。
        var catalog = ActionCatalog.All
            .Where(d => d.UnlockEra == EraOrdinals.E3)
            .Select(d => d.Verb).ToList();

        // 真数据：零漂移（双侧皆空）
        Assert.Empty(MatrixDrift(catalog, ScheduledActions()));

        // 突变①：代码侧混入 E3 伪动词 → catalog_only:AutoBuild
        var drift1 = MatrixDrift(catalog.Append("AutoBuild"), ScheduledActions());
        Assert.Contains("catalog_only:AutoBuild", drift1);

        // 突变②：排期侧混入未登记动作 → schedule_only:Bogus
        var drift2 = MatrixDrift(catalog, new List<string> { "Bogus" });
        Assert.Contains("schedule_only:Bogus", drift2);
    }

    [Fact]
    public void Ammo_coverage_helper_flags_a_missing_recipe_on_a_mutated_output_set()
    {
        // illegal — [F-023] 负向用例：弹药覆盖闭合对缺失配方必须点名（防恒绿）。
        var towers = LoadItems("towers.json");
        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();

        // 真数据：每座武器塔弹药恰有配方
        var missing = towers
            .Where(t => t.TryGetProperty("ammo_item", out var a) && a.ValueKind != JsonValueKind.Null)
            .Where(t => !outputs.Contains(t.GetProperty("ammo_item").GetString()!))
            .Select(t => t.GetProperty("ammo_item").GetString()!).ToList();
        Assert.Empty(missing);

        // 突变：抽走弹链配方 → 覆盖检查点名加特林弹药缺失
        var mutated = outputs.Where(o => o != "era3:ammo-bullet").ToHashSet();
        var missingAfter = towers
            .Where(t => t.TryGetProperty("ammo_item", out var a) && a.ValueKind != JsonValueKind.Null)
            .Where(t => !mutated.Contains(t.GetProperty("ammo_item").GetString()!))
            .Select(t => t.GetProperty("ammo_item").GetString()!).ToList();
        Assert.Equal(new[] { "era3:ammo-bullet" }, missingAfter);
    }

    [Fact]
    public void SupplyChain_guard_rejects_a_depth_four_assembly()
    {
        // illegal — [F-022] 负向用例：链深=4 的建造组合被拒绝并给出 chain_depth 提示
        //（T-034 验收式：构造 原料(1)→中间件(2)→中间件(3)→弹药(4) 的合成链）。
        var chain = new SupplyChain();
        chain.RegisterRaw(new ResourceKey("era3:res-coal"));
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(
            new[] { new ResourceKey("era3:res-coal") }, new ResourceKey("t112test:ingot")));
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(
            new[] { new ResourceKey("t112test:ingot") }, new ResourceKey("t112test:plate")));
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(
            new[] { new ResourceKey("t112test:plate") }, new ResourceKey("t112test:shell")));

        var violations = chain.Validate();
        Assert.Contains(violations, v => v.StartsWith("chain_depth:t112test:shell=", StringComparison.Ordinal)
                                         && v.Contains("4", StringComparison.Ordinal));
    }

    // ---- 8) 注册表纪律：id 唯一性/命名空间/_new 溯源 [F-156] -----------------

    [Fact]
    public void Era3_item_ids_are_unique_and_follow_namespace_conventions()
    {
        // [F-156] 注册表 id 唯一性与命名空间（K-2 modid:contentid；关卡行归
        // campaign 命名空间=techtree/legacy 同源引用，其余归 era3 域）
        var all = new List<(string File, string Id)>();
        foreach (var file in new[]
                 { "era.json", "recipes.json", "levels.json", "towers.json", "enemies.json" })
        {
            foreach (var item in LoadItems(file))
            {
                var id = StringProperty(item, "id");
                Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*:[a-z][a-z0-9]*(-[a-z0-9]+)*$", id);
                all.Add((file, id));
            }
        }

        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count()); // era3 全域 id 唯一
        Assert.All(all.Where(x => x.File == "era.json"), x => Assert.StartsWith("era3:era-", x.Id));
        Assert.All(all.Where(x => x.File == "recipes.json"), x => Assert.StartsWith("era3:recipe-", x.Id));
        Assert.All(all.Where(x => x.File == "levels.json"), x => Assert.StartsWith("campaign:level-e3-", x.Id));
        Assert.All(all.Where(x => x.File == "towers.json"), x => Assert.StartsWith("era3:tower-", x.Id));
        Assert.All(all.Where(x => x.File == "enemies.json"), x => Assert.StartsWith("era3:enemy-", x.Id));
    }

    [Fact]
    public void New_flag_policy_marks_invented_values_with_sources()
    {
        // [F-156] _new/_source 溯源纪律（11-spec-tables L7：新发明数值一律标注）
        foreach (var file in new[]
                 { "era.json", "recipes.json", "levels.json", "towers.json", "enemies.json" })
        {
            foreach (var item in LoadItems(file))
            {
                var id = StringProperty(item, "id");
                Assert.True(item.TryGetProperty("_new", out var flag), $"{file}:{id} 缺 _new 标记");
                Assert.Contains(flag.ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
                Assert.False(string.IsNullOrWhiteSpace(StringProperty(item, "_source")));
                Assert.True(ObjectProperty(item, "_units").EnumerateObject().Any(),
                    $"{file}:{id} 缺单位注解键 _units");
            }
        }

        // E3 时代行：能源逐级绝对值与 SP-4 风险块为 _new（v3.2 仅钉 ×3~5 带；
        // SP-4 表体值 [NEW-原型校准] 待 Wave 1 校准）；Boss HP/甲为 §4 逐字
        // 锚点 → 不得列入 _new_fields（与 era1/era2 同纪律）
        var era = Era();
        Assert.True(era.GetProperty("_new").GetBoolean());
        var eraNewFields = era.GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("energy.levels[0].output", eraNewFields);
        Assert.Contains("energy.levels[1].output", eraNewFields);
        Assert.Contains("energy.risk", eraNewFields);

        var bossNewFields = ArrayProperty(FindById(LoadItems("enemies.json"), "era3:enemy-boss"), "_new_fields");
        Assert.DoesNotContain("hp", bossNewFields.EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("armor", bossNewFields.EnumerateArray().Select(f => f.GetString()));

        // F.5 基准表仅钉 E2 行——E3 塔三件（成本/DPS/射程）必须 _new（与 era1 同纪律）
        var gatlingNewFields = FindById(LoadItems("towers.json"), "era3:tower-gatling")
            .GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("cost_total", gatlingNewFields);
        Assert.Contains("dps", gatlingNewFields);
        Assert.Contains("range_cells", gatlingNewFields);
    }
}
