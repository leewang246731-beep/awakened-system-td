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

// [C-02] K-2 E4 内容数据行回归（T-123）：game/content/era4/ 五文件
// （era/towers/enemies/recipes/levels）的锚点比对 + 三源一致性矩阵。
// 期望值全部硬编码自文档锚点（出处行写在各断言处注释），JSON 解析失败即红：
//   - E4 时代行       = v3.2 §4 L61（docs/我在游戏觉醒系统-游戏设计方案.md）+§4 能源阶梯/跨代资源行+§5 L78~L79
//   - 能源 L1=1000/L2=4000/L3=20000（核电） = T-123 任务卡锚点（倍率 ×4/×5 ∈[3,5]）
//   - 能源风险        = SP-4 L146『E3 过热爆炸』（沿用行）+L147『E4 熔毁』表体（[NEW-原型校准] 已定案值）
//                       +radiation_zone_hp_decay_per_s=5（[NEW-T-123]=300÷60 推导）
//   - 核电危险位      = §11.4/F.4 危险位先例（SellTower confirm_sell/BuildPlan confirm_overwrite 同族数据位）
//   - E4 塔组         = §4 L61+SP-1b L49~L51（T-123 章节设计复核追认：导弹塔=溅射/激光塔=能量/雷达=支援）
//                       +地雷=T-123 章节设计新增行（specialization 面封锁利基）；数值=F.5 类推锚带 [NEW]
//   - E4 敌表         = 新设计 [NEW]（行形对位 era3 敌表+跨代步 ×2）；Boss 夜枭 HP20000/甲22=§4 逐字锚点；
//                       隐形=雷达反制钩子（towers.reveal_stealth ↔ enemies.stealth.countered_by_radar 同源）
//   - 配方集          = v3.2 §5 L74~L76（链深 ≤3；1 件弹药=10 发）+§4 L61 石油→电子/混凝土炼链
//   - 前三关排期      = v3.2 §4 L67 + §11.8（对话级→建造级→巡逻/托管级）+§7 公式
//   - 三源互证        = ActionCatalog E4 开放集（空——19 动作封闭清单全于 ≤E2 解锁）↔ levels 排期侧
//                     ↔ campaign/techtree.json E4 节点（T-124 落位前为前向守卫）
public class Era4ContentTests
{
    // ---- 装载辅助（与 Era1/E2/Era3ContentTests 同纪律） ----------------------

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

    private static string Era4Dir()
    {
        var dir = FindRepoContentDir("era4");
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

    private static JsonDocument LoadDoc(string fileName) => LoadDocFrom(Era4Dir(), fileName);

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

    private static bool BoolProperty(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var value), $"缺字段 {name}");
        Assert.Equal(JsonValueKind.True, value.ValueKind);
        return true;
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
        // 测试内文件小且条目固定，由 GC 回收（与 Era1~Era3ContentTests 同约定）。
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
        // 矩阵比较器（era2/era3 先例复刻）：任一侧多出/缺失即点名（负向用例喂突变集）。
        var drift = new List<string>();
        drift.AddRange(catalogSide.Except(scheduleSide, StringComparer.Ordinal)
            .Select(v => $"catalog_only:{v}"));
        drift.AddRange(scheduleSide.Except(catalogSide, StringComparer.Ordinal)
            .Select(v => $"schedule_only:{v}"));
        return drift;
    }

    // ---- 1) 解析与结构声明 [F-008] ------------------------------------------

    [Fact]
    public void All_five_era4_files_parse_and_declare_K2_content_types()
    {
        // [F-008] E4 行数据载体：五文件解析+K-2 content_type 声明。
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

            // $schema 预留字段（与 era1~era3 同格式约定）
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
    public void Era4_towers_and_enemies_load_through_the_real_loaders()
    {
        // [F-016][F-035] RL7 实装载器回归：TowerSpec.LoadCollection /
        // EnemySpecCatalog.Parse 全量吃下 era4 两表（坏形状直接抛即红）。
        var towers = TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era4Dir(), "towers.json")));
        Assert.Equal(4, towers.Count);
        Assert.All(towers, t => Assert.StartsWith("era4:tower-", t.Id, StringComparison.Ordinal));

        var enemies = EnemySpecCatalog.Parse(File.ReadAllText(Path.Combine(Era4Dir(), "enemies.json")));
        Assert.Equal(9, enemies.Count);
        var boss = enemies.Get("era4:enemy-boss");
        Assert.True(boss.IsBoss);
        Assert.Equal(20000, boss.Hp);       // §4 L61 逐字锚点
        Assert.Equal(22, boss.Armor);       // §4 L61 逐字锚点
        Assert.Equal(1200, boss.ShieldPoints); // 护盾 E3 起跨代步（era3 600×2 [NEW]）
        Assert.Equal(1.60, boss.SpeedPxPerTick, 9); // 1.5 格/s 隐形穿透奇袭档 [NEW]
    }

    // ---- 2) 时代行：v3.2 §4 E4 行逐项 [F-008] [F-009] [F-011] ---------------

    [Fact]
    public void Era_row_matches_v32_section4_E4_line()
    {
        // [F-008] E4 时代行逐项（资源/能源/物流/仓库/系统形态/Boss）
        var era = Era();

        Assert.Equal(4, IntProperty(era, "era_index")); // §4 L70 时代序号 E4=4
        Assert.Equal("era4:era-modern", StringProperty(era, "id"));
        Assert.Equal("现代", StringProperty(era, "name_zh"));
        Assert.Equal("Modern", StringProperty(era, "name_en"));

        // §4 L61 资源=石油/电子/混凝土 + 上代（E3）2 种基础资源传承刷新（煤/铁，丰度 ×0.5）
        var resources = ArrayProperty(era, "resources").EnumerateArray().ToList();
        var resourceIds = resources.Select(r => StringProperty(r, "id")).ToList();
        Assert.Equal(
            new[]
            {
                "era4:res-oil", "era3:res-coal", "era2:res-iron", "era4:res-electronics",
                "era4:res-concrete",
            },
            resourceIds);

        // 传承刷新行：丰度乘数恰 0.5、来自 E3（§4 跨代资源行——上代=紧邻前代 E3，
        // 其基础二资源=煤/铁；电子/混凝土=制品不入刷新对，era3『钢』分立先例）
        foreach (var legacyId in new[] { "era3:res-coal", "era2:res-iron" })
        {
            var refresh = ObjectProperty(
                resources.Single(r => StringProperty(r, "id") == legacyId), "legacy_refresh");
            Assert.Equal(3, IntProperty(refresh, "from_era"));
            Assert.Equal(0.5, NumberProperty(refresh, "abundance_multiplier"));
        }

        // §4 L61 能源=电网
        Assert.Equal("grid", StringProperty(ObjectProperty(era, "energy"), "type"));
        Assert.Equal("电网", StringProperty(ObjectProperty(era, "energy"), "name_zh"));

        // §4 L61 物流=传送带 15/s、卡车（§5 L78 阶梯第 3 档；20.0=E5 行档位预告）
        var logistics = ObjectProperty(era, "logistics");
        Assert.Equal(3, IntProperty(logistics, "tier"));
        Assert.Equal("conveyor_belt", StringProperty(logistics, "carrier"));
        Assert.Equal("传送带", StringProperty(logistics, "carrier_zh"));
        Assert.Equal(15.0, NumberProperty(logistics, "throughput_items_per_s"));
        var ladder = ArrayProperty(logistics, "ladder_items_per_s");
        Assert.Equal(6, ladder.GetArrayLength());
        Assert.Equal(0.3, ladder[0].GetDouble());
        Assert.Equal(0.6, ladder[1].GetDouble());
        Assert.Equal(10.0, ladder[2].GetDouble());
        Assert.Equal(15.0, ladder[3].GetDouble()); // E4 档位=阶梯第 3 档，与 throughput 同值
        Assert.Equal(20.0, ladder[4].GetDouble()); // E5 无人机档（E4 不提前启用）
        Assert.Equal(JsonValueKind.Null, ladder[5].ValueKind); // 末位 null=∞
        var truck = ObjectProperty(logistics, "truck");
        Assert.True(BoolProperty(truck, "available"));
        Assert.Equal("卡车", StringProperty(truck, "name_zh"));

        // §5 L79 仓库等级跃迁自动 +1：E3=3→E4=4
        Assert.Equal(4, IntProperty(era, "warehouse_level"));

        // §4 L61 系统形态=多核期
        Assert.Equal("multicore", StringProperty(era, "system_stage"));
        Assert.Equal("多核期", StringProperty(era, "system_stage_zh"));
        var features = ArrayProperty(era, "system_stage_features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("patrol_subagents", features);   // §4 L61『巡逻子代理』
        Assert.Contains("another_voice_hook", features); // §4 L61『"另一个声音"钩子』

        // §4 L61 Boss=夜枭 HP20000/甲22（逐字锚点）
        var boss = ObjectProperty(era, "boss");
        Assert.Equal("era4:enemy-boss", StringProperty(boss, "enemy_id"));
        Assert.Equal(20000, NumberProperty(boss, "hp"));
        Assert.Equal(22, NumberProperty(boss, "armor"));
        Assert.Equal("夜枭", StringProperty(boss, "name_zh"));
    }

    [Fact]
    public void Era_energy_ladder_is_1000_4000_20000_within_v32_growth_band()
    {
        // [F-009] 能源阶梯 1000→4000→20000、逐级倍率 ∈[3,5]
        // 能源阶梯锚点：L1=1000（柴油）/L2=4000（燃气）/L3=20000（核电）（T-123
        // 任务卡）；倍率 4000/1000=4、20000/4000=5 须逐级落在 v3.2 §4 能源阶梯行
        // 「×3~5/级」带内（F-009 验收）
        var energy = ObjectProperty(Era(), "energy");

        var levels = ArrayProperty(energy, "levels");
        Assert.Equal(3, levels.GetArrayLength());
        Assert.Equal(1, IntProperty(levels[0], "tier"));
        Assert.Equal(1000, NumberProperty(levels[0], "output"));
        Assert.Equal(2, IntProperty(levels[1], "tier"));
        Assert.Equal(4000, NumberProperty(levels[1], "output"));
        Assert.Equal(3, IntProperty(levels[2], "tier"));
        Assert.Equal(20000, NumberProperty(levels[2], "output"));

        Assert.InRange(NumberProperty(levels[1], "output") / NumberProperty(levels[0], "output"), 3.0, 5.0);
        Assert.InRange(NumberProperty(levels[2], "output") / NumberProperty(levels[1], "output"), 3.0, 5.0);

        // E3 起负载率类阶梯风险开闸沿用（SP-4 L142：E1/E2 risk_events=false 的反面）
        Assert.True(energy.GetProperty("risk_events").GetBoolean());
    }

    // boundary/legal：SP-4 L146 表体逐值（沿用行，[NEW-原型校准] 已定案——漂移即红）
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

    // boundary/legal：SP-4 L147 表体逐值（[NEW-原型校准] 已定案——漂移即红）
    [Theory]
    [MemberData(nameof(MeltdownParamRows))]
    public void Meltdown_risk_params_match_SP4_row_exactly(string jsonPath, double expected)
    {
        // [F-010] SP-4 L147『E4 熔毁』行全值入内容数据（RL7：阈值禁止硬编码于引擎）
        var meltdown = ObjectProperty(ObjectProperty(Era(), "energy"), "meltdown");
        Assert.Equal("meltdown", StringProperty(meltdown, "kind"));

        var segments = jsonPath.Split('.');
        var node = meltdown;
        foreach (var segment in segments[..^1])
        {
            node = ObjectProperty(node, segment);
        }

        Assert.Equal(expected, NumberProperty(node, segments[^1]), 9);
    }

    public static TheoryData<string, double> MeltdownParamRows => new()
    {
        // 出处：11-spec-tables.md SP-4 L147 E4 熔毁行（触发/冷却中断/半径/后果四列）
        { "trigger.overload_threshold_percent", 120 },            // 负载率 ≥120% 连续 20 s
        { "trigger.sustained_s", 20 },                            // 连续 20 s
        { "trigger.instant_threshold_percent", 150 },             // 或瞬时 ≥150%
        { "countdown_s", 45 },                                    // 熔毁倒计时 45 s
        { "interrupt.unload_percent", 30 },                       // 卸载 ≥30% 负载可中断
        { "explosion_radius_cells", 6 },                          // 半径 6 格
        { "consequences.building_damage_fraction_of_max_hp", 0.8 }, // 半径内建筑 最大 HP×80%
        { "consequences.radiation_zone_s", 60 },                  // 地面污染 60 s
        { "consequences.radiation_enemy_slow_percent", 25 },      // 过境敌 −25% 速度
        { "consequences.radiation_zone_hp_decay_per_s", 5 },      // [NEW-T-123]=300÷60 推导（见 era.json _source）
        { "consequences.wreck_recovery_cost_fraction", 0.3 },     // 残骸修复口径 原成本×30%
    };

    [Fact]
    public void Meltdown_bool_and_coolant_flags_match_the_spec_channels()
    {
        // [F-010] SP-4 L147 布尔位：肇事机永久损毁 + T-113 任务卡『断冷→熔毁』通道
        var meltdown = ObjectProperty(ObjectProperty(Era(), "energy"), "meltdown");
        Assert.True(BoolProperty(meltdown, "coolant_required")); // [NEW-T-113] RequireCoolant
        var consequences = ObjectProperty(meltdown, "consequences");
        Assert.True(BoolProperty(consequences, "culprit_permanently_destroyed")); // 永久损毁成残骸
    }

    [Fact]
    public void Nuclear_plant_carries_the_confirm_dangerous_position_per_F4()
    {
        // [F-016][F-033] §11.4/F.4 危险位先例对齐（T-123 数据位）：核电站（能源 L3
        // 终档）携带 confirm 类危险位——requires_confirm=true（建造须玩家确认）+
        // never_auto_allow=true（永不入关卡级自动允许清单）。先例=v3.2 L130
        // SellTower{confirm_sell:true,永不自动允许}/BuildPlan{confirm_overwrite
        // 强制 Ask}（ApprovalGate.DangerBits 规则驱动表同族；宿主管线接线=T-124）。
        var levels = ArrayProperty(ObjectProperty(Era(), "energy"), "levels");
        var nuclear = levels.EnumerateArray().Single(l => IntProperty(l, "tier") == 3);
        Assert.Equal("核电站", StringProperty(nuclear, "name_zh"));
        Assert.Equal(20000, NumberProperty(nuclear, "output"));

        var dangerous = ObjectProperty(nuclear, "dangerous_position");
        Assert.Equal("build_confirm", StringProperty(dangerous, "confirm_class"));
        Assert.True(BoolProperty(dangerous, "requires_confirm"));
        Assert.True(BoolProperty(dangerous, "never_auto_allow"));

        // 柴油/燃气两档不携带危险位（confirm 位=核电专属）
        foreach (var tier in new[] { 1, 2 })
        {
            var level = levels.EnumerateArray().Single(l => IntProperty(l, "tier") == tier);
            Assert.False(level.TryGetProperty("dangerous_position", out _),
                $"能源 L{tier} 不应携带危险位（confirm 类危险位=核电专属）");
        }
    }

    [Fact]
    public void Era_row_references_resolve_across_all_sibling_era4_files()
    {
        // [F-008] 时代行跨文件引用完整性：塔组/Boss/时代 id 四文件合流
        var era = Era();
        var eraId = StringProperty(era, "id");

        // era.towers 恰等于 towers.json 全体 id（§4 L61 塔防特色三塔+地雷章节设计行 ↔ 实存行）
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var eraTowers = ArrayProperty(era, "towers").EnumerateArray().Select(t => t.GetString()!).ToList();
        Assert.Equal(4, eraTowers.Count);
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

        // 多核期系统形态标记 ↔ 关 3 系统新能力排期（§4 L61『多核期（巡逻子代理）』
        // ↔ §4 L67 关 3 系统新能力 ↔ §11.8 巡逻/托管级末档）
        var slot3Features = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features")
            .EnumerateArray().Select(f => f.GetString()!).ToList();
        Assert.Contains("multicore_patrol", slot3Features);
    }

    // ---- 3) 塔组：§4 L61 + SP-1b 定类 + F.5 类推锚带 [F-016] -----------------

    private static IReadOnlyList<TowerSpec> Towers() =>
        TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era4Dir(), "towers.json")));

    public static TheoryData<string, double, double, double, string, int> TowerRows => new()
    {
        // §4 L61 塔防特色=导弹塔/激光塔/雷达（SP-1b L49~L51 预归类经 T-123 章节设计
        // 复核追认）+地雷（T-123 章节设计新增，specialization 面封锁利基）；
        // 数值=F.5 类推锚带 [NEW]（推导式见 towers.json 各行 _source）
        { "era4:tower-missile", 810, 480.0, 10.0, "splash", 1 },  // 成本 270×3 跨代基点步
        { "era4:tower-laser", 2430, 960.0, 12.5, "energy", 2 },   // 810×3 严格；480×2 严格
        { "era4:tower-radar", 405, 0.0, 12.0, "support", 1 },     // 支援档=T1 武器塔成本×0.5
        { "era4:tower-mine", 240, 100.0, 2.5, "splash", 1 },      // specialization 梯外利基行
    };

    [Theory]
    [MemberData(nameof(TowerRows))]
    public void Towers_match_SP1b_classes_and_F5_analog_anchor_band(
        string id, double cost, double dps, double range, string towerClass, int tier)
    {
        // [F-016] 四塔定类（SP-1b L49~L51 T-123 追认+章节设计行）+F.5 类推锚带值（[NEW] 入表随检）
        var spec = Towers().Single(t => t.Id == id);
        Assert.Equal(towerClass, spec.Class.ToString().ToLowerInvariant());
        Assert.Equal(tier, spec.Tier);
        Assert.Equal(cost, spec.CostTotal);
        Assert.Equal(dps, spec.Dps, 9);
        Assert.Equal(range, spec.RangeCells, 9);
    }

    [Fact]
    public void Radar_is_the_zero_damage_support_row_with_the_stealth_counter_hook()
    {
        // [F-016] SP-1b L51『雷达=支援：视野/集火增益』——T-123 章节设计复核落定
        //（round 2 处置 #9 挂接）；夜枭隐形=雷达反制钩子（双文件同源断言见 Boss 测试）。
        var radar = Towers().Single(t => t.Id == "era4:tower-radar");
        Assert.Equal(TowerClass.Support, radar.Class);
        Assert.Equal(0.0, radar.Dps, 9);
        Assert.Equal(0.0, radar.DamagePerShot, 9);
        Assert.Null(radar.ReloadTicks);   // 无武器循环（E1 篝火/陷阱先例）
        Assert.Null(radar.AmmoItem);      // 无弹药（不入弹药覆盖闭合）
        Assert.Equal(0, radar.AmmoPerShot);
        Assert.Equal(1, radar.MaxTier);   // 支援设施不升级（无后继行不发明）
        Assert.Null(radar.UpgradesTo);
        Assert.Equal(12.0, radar.RangeCells, 9); // 光环半径同值（篝火先例）
        Assert.Equal(12, radar.Traits!.AuraRadiusCells); // 警戒视野+集火增益光环
        Assert.Contains("alert_vision", radar.Traits.AuraEffects!);
        Assert.Contains("focus_fire_boost", radar.Traits.AuraEffects!);

        // 反隐钩子（装载器忽略的扩展 trait，数据位先行——T-124 战役宿主消费）
        var radarRow = FindById(LoadItems("towers.json"), "era4:tower-radar");
        Assert.True(BoolProperty(ObjectProperty(radarRow, "traits"), "reveal_stealth"));
    }

    [Fact]
    public void DPS_split_stays_self_consistent_on_the_tick_grid()
    {
        // [F-027] 内部一致性：dps = damage_per_shot × 30 / reload_ticks（§6 符号
        // 约定 d=单发伤害，30 Hz）——武器塔全过（雷达无武器周期豁免）
        foreach (var spec in Towers().Where(t => t.ReloadTicks is not null))
        {
            var expected = spec.DamagePerShot * 30.0 / spec.ReloadTicks!.Value;
            Assert.Equal(expected, spec.Dps, 3);
        }
    }

    [Fact]
    public void UpgradeLaw_validates_the_whole_era4_table()
    {
        // [F-027] 任务验收「塔过 UpgradeLaw」：声明块全过 + 标准线步全过
        var towers = Towers();
        foreach (var spec in towers)
        {
            Assert.Empty(UpgradeLaw.ValidateGrowthDeclaration(spec));
        }

        // 标准线唯一步：导弹塔 T1 → 激光塔 T2（成本 810→2430 严格 ×3；DPS 480→960
        // 严格 ×2；类变步溅射→能量射程带豁免，12.5/10.0=×1.25 仍恰带内+半格网格）
        var missile = towers.Single(t => t.Id == "era4:tower-missile");
        var laser = towers.Single(t => t.Id == "era4:tower-laser");
        Assert.Empty(UpgradeLaw.ValidateStep(missile, laser));

        // 严格 ×3 成本阶梯（F.5 90→270→810→2430 同带重演：E4 标准线与 E2 同构同价）
        Assert.Equal(missile.CostTotal * 3, laser.CostTotal);
        // DPS 律带 ±15%（实值恰 ×2）
        Assert.InRange(laser.Dps / missile.Dps, 2.0 * 0.85, 2.0 * 1.15);
        Assert.Equal(2.0, laser.Dps / missile.Dps, 9);
        // 接线：导弹塔→激光塔为线尾步（upgrades_to 指向实存行；激光/雷达/地雷无后继）
        Assert.Equal("era4:tower-laser", missile.UpgradesTo);
        Assert.Null(laser.UpgradesTo);
        Assert.Equal(missile.MaxTier, laser.MaxTier);

        // 地雷=specialization 梯外利基行（F.5 弩炮 700|80|11 先例）：max_tier 1、无后继
        var mine = towers.Single(t => t.Id == "era4:tower-mine");
        Assert.Equal(UpgradeLaw.SpecializationLine, mine.TierLine);
        Assert.Equal(1, mine.MaxTier);
        Assert.Null(mine.UpgradesTo);
    }

    [Fact]
    public void Class_bands_follow_the_SP1a_policy_with_the_sentinel_kept()
    {
        // [F-026] 按塔类验证带（SP-1a）：激光塔（T2 能量=标准单体行）对自身基准恰在
        // 1.0 带；雷达=支援类豁免；地雷=specialization 豁免
        var towers = Towers();
        var missile = towers.Single(t => t.Id == "era4:tower-missile");
        var laser = towers.Single(t => t.Id == "era4:tower-laser");
        var radar = towers.Single(t => t.Id == "era4:tower-radar");
        var mine = towers.Single(t => t.Id == "era4:tower-mine");

        Assert.Empty(UpgradeLaw.ValidateClassBand(laser, laser.Dps));
        Assert.Empty(UpgradeLaw.ValidateClassBand(radar, laser.Dps)); // support 豁免
        Assert.Empty(UpgradeLaw.ValidateClassBand(mine, laser.Dps));  // specialization 豁免

        // 导弹塔（T1 溅射）没有同级标准单体参照行（E4 行集冻结=四塔）：×0.8 带无从
        // 比起——断言参照缺失这一事实本身（E2 火油/E3 臼炮同款哨兵先例），E4 出同级
        // 单体行时本断言强制补上带校验。
        bool t1StandardSingleExists = towers.Any(t =>
            t.Tier == 1
            && t.Class is TowerClass.Physical or TowerClass.Pierce or TowerClass.Energy
            && !string.Equals(t.TierLine, UpgradeLaw.SpecializationLine, StringComparison.Ordinal));
        Assert.False(t1StandardSingleExists,
            "E4 无 T1 标准单体行（四塔行集冻结）——导弹塔 ×0.8 带校验随之 N/A；新增行时必须补 ValidateClassBand");

        // 哨兵兜底：导弹塔 DPS=前代标准线终点 480（跨代基点锚——E3 T1 240=E2 线终点
        // 同性质；类带不可判时仍钉住跨代基点，防溅射行越带抬价）
        Assert.Equal(480.0, missile.Dps, 9);
    }

    [Fact]
    public void Burst_price_law_accepts_the_era4_rows()
    {
        // [F-028] 爆发型代价：导弹塔对自身基准、激光塔对 T2 标准线投影（480×2=960）
        // 均不超基准 → 无需前摇代价（持续型）；雷达/地雷零/低持续 DPS 同过
        var towers = Towers();
        var missile = towers.Single(t => t.Id == "era4:tower-missile");
        var laser = towers.Single(t => t.Id == "era4:tower-laser");
        var radar = towers.Single(t => t.Id == "era4:tower-radar");
        var mine = towers.Single(t => t.Id == "era4:tower-mine");

        var baselines = new (TowerSpec Spec, double Baseline)[]
        {
            (missile, missile.Dps),
            (laser, missile.Dps * 2), // T2 标准线投影 960
            (radar, missile.Dps),
            (mine, missile.Dps),
        };
        foreach (var (spec, baseline) in baselines)
        {
            var verdict = Damage.ValidateBurstCost(spec, baseline);
            Assert.True(verdict.Allowed, $"{spec.Id}: {verdict.Violation}");
        }
    }

    // ---- 4) 敌表：新设计行 + §4 Boss 逐字锚点 + 隐形钩子 [F-035] [F-032] [F-033]

    public static TheoryData<string, double, double, double> EnemyStatRows => new()
    {
        // E4 敌表=新设计 [NEW]（行形对位 era3 敌表各位+跨代步 ×2；现代精确打击军团）；
        // 表体值入表随检——漂移即红。Boss 夜枭 HP20000/甲22=v3.2 §4 L61 逐字锚点。
        { "e4_militia", 240, 0, 1.60 },        // 散兵位（E3 征召兵 120×2）
        { "e4_assault_trooper", 1200, 12, 1.17 }, // 中坚位（线列兵 600×2、甲 6→12）
        { "e4_siege_mech", 6000, 20, 0.53 },   // 攻城位（蒸汽冲车 3000×2、甲 16→20）
        { "e4_raid_quad", 560, 2, 2.67 },      // 快速位（铁骑斥候 280×2）
        { "e4_missile_trooper", 720, 8, 1.07 }, // 远程位（排枪手 360×2、甲 4→8、射程 6）
        { "e4_riot_armor", 2400, 18, 0.85 },   // 重甲位（铁甲卫兵 1200×2、甲 12→18、盾 480）
        { "e4_kamikaze_drone", 480, 2, 1.71 }, // 自爆位（爆破工兵 240×2、自爆 240）
        { "e4_ew_officer", 3600, 16, 0.96 },   // 精英光环位（工头军士 1800×2、甲 10→16）
        { "e4_boss", 20000, 22, 1.60 },        // Boss（§4 逐字 20000/22；速度 1.5 格/s [NEW]）
    };

    [Theory]
    [MemberData(nameof(EnemyStatRows))]
    public void Enemies_match_the_E4_rows_hp_armor_speed(string spId, double hp, double armor, double speed)
    {
        // [F-035] E4 敌表九种数值锚点（表体 [NEW] 入表随检）
        var items = LoadItems("enemies.json");
        Assert.Equal(9, items.Count); // E4 恰九种（8 常规+Boss，行形对位 era3 九行制）

        var enemy = items.Single(i => StringProperty(i, "sp_id") == spId);
        Assert.Equal(hp, NumberProperty(enemy, "hp"));
        Assert.Equal(armor, NumberProperty(enemy, "armor"));
        Assert.Equal(speed, NumberProperty(enemy, "speed_px_per_tick"));

        // 换算列自洽：speed × 30 / 32 = 格/s（SP-3 头部 L92 换算式，1 格=32 px）
        Assert.Equal(speed * 30.0 / 32.0, NumberProperty(enemy, "speed_cells_per_s"), 2);
    }

    [Fact]
    public void Boss_carries_stealth_and_airstrike_trait_params_with_the_radar_counter_hook()
    {
        // [F-033][F-016] T-123 任务卡『夜枭隐形=雷达反制钩子』+空袭技能参数
        var boss = FindById(LoadItems("enemies.json"), "era4:enemy-boss");
        Assert.True(boss.GetProperty("is_boss").GetBoolean());

        // 隐形：塔目标选择不可见 + 雷达反制（与 towers.json 雷达 reveal_stealth 同源）
        var stealth = ObjectProperty(ObjectProperty(boss, "traits"), "stealth");
        Assert.True(BoolProperty(stealth, "invisible_to_targeting"));
        Assert.True(BoolProperty(stealth, "countered_by_radar"));
        var radarRow = FindById(LoadItems("towers.json"), "era4:tower-radar");
        Assert.True(BoolProperty(ObjectProperty(radarRow, "traits"), "reveal_stealth"),
            "夜枭 countered_by_radar 必须与雷达 reveal_stealth 同源成对（缺一即钩子断链）");

        // 空袭：30 s 周期（900 ticks@30 Hz）半径 2 格 800 点（践踏半径 2 格行锚对位、
        // 周期化 [NEW-原型校准]；T-123 数据位，引擎消费随 T-124 Boss 战宿主）
        var airstrike = ObjectProperty(ObjectProperty(boss, "traits"), "airstrike");
        Assert.Equal(900, NumberProperty(airstrike, "interval_ticks"));
        Assert.Equal(2, NumberProperty(airstrike, "radius_cells"));
        Assert.Equal(800, NumberProperty(airstrike, "damage"));

        // 常规 Boss 尺寸 64~96 px（v3.2 §13.1）→画布取上沿 96（era3 先例）
        Assert.Equal(96, NumberProperty(boss, "canvas_size_px"));
    }

    [Fact]
    public void Shields_are_present_from_E3_on_the_heavy_rows()
    {
        // [F-032] v3.2 §6「护盾 E3 起」沿用：重甲行+Boss 携带个体 shield 键（[NEW]
        // 跨代步 era3×2；穿甲打断反制位=激光塔 armor_pierce）；轻中甲行不携带
        var items = LoadItems("enemies.json");
        Assert.Equal(480, NumberProperty(
            FindById(items, "era4:enemy-riot-armor"), "shield"));
        Assert.Equal(1200, NumberProperty(
            FindById(items, "era4:enemy-boss"), "shield"));

        foreach (var id in new[]
                 { "era4:enemy-militia", "era4:enemy-assault-trooper", "era4:enemy-ew-officer" })
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

    // ---- 5) 配方集：§5 链深守卫 + 石油→电子/混凝土炼链 [F-022] [F-023] -------

    [Fact]
    public void Raw_resources_exclude_the_crafted_era4_resources()
    {
        // [F-023] 原料表口径：石油/煤/铁三件（煤/铁=传承刷新）；电子/混凝土=era.json
        // 资源行成员（§4 L61 点名）但为炼链制品——SupplyChain one key one meaning，
        // 制品不入原料表（era3『钢』分立先例：§4 点名资源含制品、两表分立）。
        var eraResourceIds = ArrayProperty(Era(), "resources").EnumerateArray()
            .Select(r => StringProperty(r, "id")).ToHashSet();
        var raws = ArrayProperty(LoadDoc("recipes.json").RootElement, "raw_resources").EnumerateArray()
            .Select(r => StringProperty(r, "resource")).ToList();

        Assert.Equal(new[] { "era4:res-oil", "era3:res-coal", "era2:res-iron" }, raws);
        Assert.All(raws, r => Assert.Contains(r, eraResourceIds)); // 原料 ⊆ 时代资源行
        Assert.Contains("era4:res-electronics", eraResourceIds);   // 电子在资源行（§4 点名）……
        Assert.Contains("era4:res-concrete", eraResourceIds);      // 混凝土在资源行（§4 点名）……

        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();
        Assert.Contains("era4:res-electronics", outputs);          // ……且均为配方输出（非原料）
        Assert.Contains("era4:res-concrete", outputs);
    }

    [Fact]
    public void Recipes_cover_every_era4_weapon_tower_ammo_item_exactly_once()
    {
        // [F-023] 弹药覆盖闭合：武器塔（ammo_item 非空）弹药件恰有一条配方；
        // 雷达无弹药不入闭合。配方集构成=中间件 2（电子/混凝土）+弹药 3（导弹/电容组/雷组件）。
        var recipeOutputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource"))
            .ToList();

        foreach (var tower in LoadItems("towers.json"))
        {
            if (!tower.TryGetProperty("ammo_item", out var ammoEl) || ammoEl.ValueKind is JsonValueKind.Null)
            {
                continue; // 雷达：支援设施无弹药
            }

            var ammo = ammoEl.GetString()!;
            Assert.True(recipeOutputs.Count(o => o == ammo) == 1, $"弹药 {ammo} 的配方数须恰为 1");
        }

        Assert.Equal(5, recipeOutputs.Count);
        var kinds = LoadItems("recipes.json").Select(r => StringProperty(r, "kind")).ToList();
        Assert.Equal(2, kinds.Count(k => k == "intermediate"));
        Assert.Equal(3, kinds.Count(k => k == "ammo"));
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

        // boundary：导弹/电容组/雷组件=链深天花板锚（石油/铁(1)→电子(2)→弹药(3)、
        // 煤/石油(1)→混凝土(2)→雷组件(3)，恰用满 ≤3）
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era4:ammo-missile")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era4:ammo-capacitor")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era4:ammo-mine")));
        Assert.Equal(SupplyChain.MaxDepth, chain.DepthOf(new ResourceKey("era4:ammo-missile")));
    }

    [Fact]
    public void Every_era4_resource_has_a_use_in_the_baskets()
    {
        // [F-011] 传承刷新资源+本代制品确有用途（§4 跨代资源行刷新语义+无死端资源）：
        // 煤→混凝土/导弹（煅烧/推进剂）；铁→电子/导弹（引线/壳体）；石油→电子/混凝土/电容；
        // 电子→导弹/电容/雷组件；混凝土→雷组件
        var usedRaws = LoadItems("recipes.json")
            .SelectMany(r => ArrayProperty(r, "inputs").EnumerateArray()
                .Select(i => StringProperty(i, "resource"))).ToHashSet();
        Assert.Contains("era3:res-coal", usedRaws);
        Assert.Contains("era2:res-iron", usedRaws);
        Assert.Contains("era4:res-oil", usedRaws);

        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();
        Assert.Contains("era4:res-electronics", outputs);
        Assert.Contains("era4:res-concrete", outputs);
        Assert.Contains("era4:res-electronics", usedRaws);  // 制品也被消费（导弹/电容/雷组件篮）
        Assert.Contains("era4:res-concrete", usedRaws);     // 制品也被消费（雷组件篮）
    }

    // ---- 6) 前三关排期：§4 L67 + §11.8 + §7 公式 [F-012] [F-092] ------------

    public static TheoryData<int, string, string, string, int, double, int> LevelScheduleRows => new()
    {
        // 出处：v3.2 §4 L67 排期（关 1 新能源=电网/关 2 新物流=传送带+卡车/关 3 考古+英雄+系统新能力）
        // +§11.8 三级（对话级→建造级→巡逻/托管级）+§7 公式（邻接敌营→威胁度→胜波）
        { 1, "campaign:level-e4-01", "new_energy", "dialogue", 0, 0.3, 13 },
        { 2, "campaign:level-e4-02", "new_logistics", "build", 1, 0.6, 16 },
        { 3, "campaign:level-e4-03", "archaeology_hero_system", "patrol", 2, 0.9, 19 },
    };

    [Theory]
    [MemberData(nameof(LevelScheduleRows))]
    public void Levels_match_schedule_tier_and_formula_anchors(
        int slot, string id, string focus, string tier, int camps, double threat, int winWaves)
    {
        // [F-012] 前三关排期结构与 §7 公式逐值比对
        var levels = Levels();
        Assert.Equal(3, levels.Count); // 恰前三关（T-123 范围；后续关随 T-124 战役制作）
        var level = Level(slot);

        Assert.Equal(id, StringProperty(level, "id"));
        Assert.Equal(focus, StringProperty(level, "focus"));
        Assert.Equal(tier, StringProperty(level, "schedule_tier"));
        Assert.Equal("era4:era-modern", StringProperty(level, "era"));

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
        // [F-012][F-010] 关 1 电网/关 2 传送带+卡车/关 3 考古+英雄+系统新能力：
        // 解锁特征逐关对位
        // L1：电网系统+过热（沿用）+熔毁风险（SP-4 自本关起生效；T-123 任务卡『关 1 电网教学』）
        var l1 = Level(1);
        var f1 = ArrayProperty(ObjectProperty(l1, "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("grid_energy", f1);
        Assert.Contains("overheat_risk", f1);
        Assert.Contains("meltdown_risk", f1);
        Assert.True(l1.GetProperty("teaching_level").GetBoolean());
        Assert.Equal(3.0, NumberProperty(l1, "first_wave_grace_multiplier")); // §6 教学关 ×3
        Assert.False(l1.GetProperty("awakening_cinematic").GetBoolean()); // 觉醒演出=E2 L1 专属锚（§10.4）

        // L2：传送带 15/s+卡车（§4 L61 物流列；20.0/E5 无人机行不提前启用）
        var f2 = ArrayProperty(ObjectProperty(Level(2), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("belt_logistics", f2);
        Assert.Contains("truck_logistics", f2);

        // L3：考古+英雄（G.2/SP-6 每代第 3 关）+系统新能力=多核巡逻（§4 L61 多核期）
        var f3 = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("archaeology", f3);
        Assert.Contains("hero_recruit", f3);
        Assert.Contains("multicore_patrol", f3);

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
    public void ActionCatalog_E4_open_set_equals_level_unlock_schedule_exactly()
    {
        // [F-092] 三源互证①：G.2 解锁时代列（代码行源）——19 动作封闭清单中
        // UnlockEra==E4 的动词恰为零（全部已于 ≤E2 解锁），排期侧三关
        // unlocks.actions 并集须同为空——双向漂移即红。
        var catalogE4 = ActionCatalog.All
            .Where(d => d.UnlockEra == EraOrdinals.E4)
            .Select(d => d.Verb)
            .ToList();

        var scheduled = ScheduledActions();
        Assert.Empty(catalogE4);   // 代码侧：无 E4 新动词
        Assert.Empty(scheduled);   // 排期侧：零动作排期
        Assert.Empty(MatrixDrift(catalogE4, scheduled));

        // 代码侧补证：19 动作全体 UnlockEra ≤E2（E4 无未解锁动作=『多核期』
        // 非动作扩员的代码侧事实，§4 L61 多核期注）
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
    public void Techtree_E4_nodes_cross_reference_only_real_era4_rows()
    {
        // [F-092] 三源互证②（techtree E4 侧前向守卫）：campaign/techtree.json
        // 尚无 E4 节点（T-124 落位）——本守卫保证此后任何 era_index==4 节点的
        // bound_levels/unlocks/前置都解析到实存内容行，悬空引用即红。
        var campaignDir = FindRepoContentDir("campaign");
        Assert.NotNull(campaignDir);
        var allTech = LoadDocFrom(campaignDir!.FullName, "techtree.json").RootElement
            .GetProperty("items").EnumerateArray().ToList();
        var techE4 = allTech.Where(t => t.GetProperty("era_index").GetInt32() == 4).ToList();

        var levelIds = Levels().Select(l => StringProperty(l, "id")).ToHashSet();
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var allTechIds = allTech.Select(t => t.GetProperty("id").GetString()!).ToHashSet();

        foreach (var node in techE4)
        {
            var nodeId = StringProperty(node, "id");
            foreach (var bound in ArrayProperty(node, "bound_levels").EnumerateArray())
            {
                Assert.Contains(bound.GetString()!, levelIds); // 关卡闸→era4 levels 实存行
            }

            foreach (var unlocked in ArrayProperty(node, "unlocks").EnumerateArray())
            {
                var target = unlocked.GetString()!;
                if (StringProperty(node, "kind") == "tower_unlock")
                {
                    Assert.Contains(target, towerIds); // 塔解锁→era4 towers 实存行
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
    public void Legacy_transition_E4_schedule_entries_align_when_T125_lands()
    {
        // [F-012] 三源互证③（era_transition 前向守卫）：legacy/era_transition.json
        // 目前冻结至 E2→E3（T-117）；E3→E4 first_three_schedule 扩展=T-125。本守卫
        // 保证此后任何 campaign:level-e4-* 排期行与 levels.json 逐值对齐。
        var legacyDir = FindRepoContentDir("legacy");
        Assert.NotNull(legacyDir);
        var schedule = ArrayProperty(
            LoadDocFrom(legacyDir!.FullName, "era_transition.json").RootElement,
            "first_three_schedule").EnumerateArray()
            .Where(s => s.TryGetProperty("level_id", out var id)
                        && id.GetString()!.StartsWith("campaign:level-e4-", StringComparison.Ordinal))
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
            .Where(d => d.UnlockEra == EraOrdinals.E4)
            .Select(d => d.Verb).ToList();

        // 真数据：零漂移（双侧皆空）
        Assert.Empty(MatrixDrift(catalog, ScheduledActions()));

        // 突变①：代码侧混入 E4 伪动词 → catalog_only:AutoBuild
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

        // 突变：抽走导弹配方 → 覆盖检查点名导弹塔弹药缺失
        var mutated = outputs.Where(o => o != "era4:ammo-missile").ToHashSet();
        var missingAfter = towers
            .Where(t => t.TryGetProperty("ammo_item", out var a) && a.ValueKind != JsonValueKind.Null)
            .Where(t => !mutated.Contains(t.GetProperty("ammo_item").GetString()!))
            .Select(t => t.GetProperty("ammo_item").GetString()!).ToList();
        Assert.Equal(new[] { "era4:ammo-missile" }, missingAfter);
    }

    // ---- 8) 注册表纪律：id 唯一性/命名空间/_new 溯源 [F-156] -----------------

    [Fact]
    public void Era4_item_ids_are_unique_and_follow_namespace_conventions()
    {
        // [F-156] 注册表 id 唯一性与命名空间（K-2 modid:contentid；关卡行归
        // campaign 命名空间=techtree/legacy 同源引用，其余归 era4 域）
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

        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count()); // era4 全域 id 唯一
        Assert.All(all.Where(x => x.File == "era.json"), x => Assert.StartsWith("era4:era-", x.Id));
        Assert.All(all.Where(x => x.File == "recipes.json"), x => Assert.StartsWith("era4:recipe-", x.Id));
        Assert.All(all.Where(x => x.File == "levels.json"), x => Assert.StartsWith("campaign:level-e4-", x.Id));
        Assert.All(all.Where(x => x.File == "towers.json"), x => Assert.StartsWith("era4:tower-", x.Id));
        Assert.All(all.Where(x => x.File == "enemies.json"), x => Assert.StartsWith("era4:enemy-", x.Id));
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

        // E4 时代行：能源逐级绝对值/危险位/风险双块/卡车为 _new（v3.2 仅钉 ×3~5 带
        // 与塔防特色列；SP-4 表体值 [NEW-原型校准] 待 Wave 1 校准）；Boss HP/甲为
        // §4 逐字锚点 → 不得列入 _new_fields（与 era1~era3 同纪律）
        var era = Era();
        Assert.True(era.GetProperty("_new").GetBoolean());
        var eraNewFields = era.GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("energy.levels[0].output", eraNewFields);
        Assert.Contains("energy.levels[1].output", eraNewFields);
        Assert.Contains("energy.levels[2].output", eraNewFields);
        Assert.Contains("energy.levels[2].dangerous_position", eraNewFields);
        Assert.Contains("energy.risk", eraNewFields);
        Assert.Contains("energy.meltdown", eraNewFields);
        Assert.Contains("logistics.truck", eraNewFields);

        var bossNewFields = ArrayProperty(FindById(LoadItems("enemies.json"), "era4:enemy-boss"), "_new_fields");
        Assert.DoesNotContain("hp", bossNewFields.EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("armor", bossNewFields.EnumerateArray().Select(f => f.GetString()));

        // F.5 基准表仅钉 E2 行——E4 塔四件（成本/DPS/射程）必须 _new（与 era1/era3 同纪律）
        var missileNewFields = FindById(LoadItems("towers.json"), "era4:tower-missile")
            .GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("cost_total", missileNewFields);
        Assert.Contains("dps", missileNewFields);
        Assert.Contains("range_cells", missileNewFields);
    }
}
