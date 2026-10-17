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

// [C-02] K-2 E5 内容数据行回归（T-134）：game/content/era5/ 五文件
// （era/towers/enemies/recipes/levels）的锚点比对 + 三源一致性矩阵。
// 期望值全部硬编码自文档锚点（出处行写在各断言处注释），JSON 解析失败即红：
//   - E5 时代行       = v3.2 §4 L62（docs/我在游戏觉醒系统-游戏设计方案.md）+§4 能源阶梯/跨代资源行+§5 L78~L79
//   - 能源 L1=100000/L2=400000/L3=2000000（湮灭芯堆） = T-134 任务卡锚点（倍率 ×4/×5 ∈[3,5]）
//   - 能源风险        = SP-4 L146『E3 过热爆炸』（沿用行）+L148『E5 湮灭』表体（[NEW-原型校准] 已定案值）
//   - 湮灭芯危险位    = §11.4/F.4 危险位先例（E4 核电站同族数据位）
//   - E5 塔组         = §4 L62+SP-1b L52~L54（T-134 章节设计复核追认：轨道炮=能量/无人机巢=穿刺/力场塔=减速）；
//                       数值=F.5 类推锚带 [NEW]
//   - E5 敌表         = 新设计 [NEW]（行形对位 era4 敌表+跨代步 ×2）；Boss 主宰 HP45000/甲30=§4 逐字锚点；
//                       力场塔↔湮灭半径减半= towers/enemies/era 三文件同源钩子
//   - 配方集          = v3.2 §5 L74~L76（链深 ≤3；1 件弹药=10 发）+§4 L62 稀土→纳米/聚变燃料炼链
//   - 前三关排期      = v3.2 §4 L67 + §11.8（对话级→建造级→巡逻/托管级）+§7 公式
//   - 三源互证        = ActionCatalog E5 开放集（空——19 动作封闭清单全于 ≤E2 解锁）↔ levels 排期侧
//                     ↔ campaign/techtree.json E5 节点（T-135 前为前向守卫）
public class Era5ContentTests
{
    // ---- 装载辅助（与 Era1~Era4ContentTests 同纪律） ------------------------

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

    private static string Era5Dir()
    {
        var dir = FindRepoContentDir("era5");
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

    private static JsonDocument LoadDoc(string fileName) => LoadDocFrom(Era5Dir(), fileName);

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
        // 测试内文件小且条目固定，由 GC 回收（与 Era1~Era4ContentTests 同约定）。
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
        // 矩阵比较器（era2~era4 先例复刻）：任一侧多出/缺失即点名（负向用例喂突变集）。
        var drift = new List<string>();
        drift.AddRange(catalogSide.Except(scheduleSide, StringComparer.Ordinal)
            .Select(v => $"catalog_only:{v}"));
        drift.AddRange(scheduleSide.Except(catalogSide, StringComparer.Ordinal)
            .Select(v => $"schedule_only:{v}"));
        return drift;
    }

    // ---- 1) 解析与结构声明 [F-008] ------------------------------------------

    [Fact]
    public void All_five_era5_files_parse_and_declare_K2_content_types()
    {
        // [F-008] E5 行数据载体：五文件解析+K-2 content_type 声明。
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

            // $schema 预留字段（与 era1~era4 同格式约定）
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
    public void Era5_towers_and_enemies_load_through_the_real_loaders()
    {
        // [F-016][F-035] RL7 实装载器回归：TowerSpec.LoadCollection /
        // EnemySpecCatalog.Parse 全量吃下 era5 两表（坏形状直接抛即红）。
        var towers = TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era5Dir(), "towers.json")));
        Assert.Equal(3, towers.Count);
        Assert.All(towers, t => Assert.StartsWith("era5:tower-", t.Id, StringComparison.Ordinal));

        var enemies = EnemySpecCatalog.Parse(File.ReadAllText(Path.Combine(Era5Dir(), "enemies.json")));
        Assert.Equal(9, enemies.Count);
        var boss = enemies.Get("era5:enemy-boss");
        Assert.True(boss.IsBoss);
        Assert.Equal(45000, boss.Hp);       // §4 L62 逐字锚点
        Assert.Equal(30, boss.Armor);       // §4 L62 逐字锚点
        Assert.Equal(2400, boss.ShieldPoints); // 护盾跨代步（era4 1200×2 [NEW]）
        Assert.Equal(0.32, boss.SpeedPxPerTick, 9); // 0.3 格/s 历代最重移动要塞档 [NEW-T-134 校准]
    }

    // ---- 2) 时代行：v3.2 §4 E5 行逐项 [F-008] [F-009] [F-011] ---------------

    [Fact]
    public void Era_row_matches_v32_section4_E5_line()
    {
        // [F-008] E5 时代行逐项（资源/能源/物流/仓库/系统形态/Boss）
        var era = Era();

        Assert.Equal(5, IntProperty(era, "era_index")); // §4 L68 时代序号 E5=5
        Assert.Equal("era5:era-future", StringProperty(era, "id"));
        Assert.Equal("未来", StringProperty(era, "name_zh"));
        Assert.Equal("Future", StringProperty(era, "name_en"));

        // §4 L62 资源=稀土/纳米/聚变燃料 + 上代（E4）2 种基础资源传承刷新（石油/铁，丰度 ×0.5）
        var resources = ArrayProperty(era, "resources").EnumerateArray().ToList();
        var resourceIds = resources.Select(r => StringProperty(r, "id")).ToList();
        Assert.Equal(
            new[]
            {
                "era5:res-rare-earth", "era4:res-oil", "era2:res-iron", "era5:res-nano",
                "era5:res-fusion-fuel",
            },
            resourceIds);

        // 传承刷新行：丰度乘数恰 0.5、来自 E4（§4 跨代资源行——上代=紧邻前代 E4，
        // 其原料集=石油/煤/铁，取石油（本代新矿）+铁（连续第三代刷新）；纳米/聚变燃料=
        // 制品不入刷新对，era3『钢』/era4『电子·混凝土』分立先例）
        foreach (var legacyId in new[] { "era4:res-oil", "era2:res-iron" })
        {
            var refresh = ObjectProperty(
                resources.Single(r => StringProperty(r, "id") == legacyId), "legacy_refresh");
            Assert.Equal(4, IntProperty(refresh, "from_era"));
            Assert.Equal(0.5, NumberProperty(refresh, "abundance_multiplier"));
        }

        // §4 L62 能源=聚变
        Assert.Equal("fusion", StringProperty(ObjectProperty(era, "energy"), "type"));
        Assert.Equal("聚变", StringProperty(ObjectProperty(era, "energy"), "name_zh"));

        // §4 L62 物流=无人机 20/s（§5 L78 阶梯第 4 档；末位 null=∞）
        var logistics = ObjectProperty(era, "logistics");
        Assert.Equal(4, IntProperty(logistics, "tier"));
        Assert.Equal("drone", StringProperty(logistics, "carrier"));
        Assert.Equal("无人机", StringProperty(logistics, "carrier_zh"));
        Assert.Equal(20.0, NumberProperty(logistics, "throughput_items_per_s"));
        var ladder = ArrayProperty(logistics, "ladder_items_per_s");
        Assert.Equal(6, ladder.GetArrayLength());
        Assert.Equal(0.3, ladder[0].GetDouble());
        Assert.Equal(0.6, ladder[1].GetDouble());
        Assert.Equal(10.0, ladder[2].GetDouble());
        Assert.Equal(15.0, ladder[3].GetDouble()); // E4 传送带档
        Assert.Equal(20.0, ladder[4].GetDouble()); // E5 档位=阶梯第 4 档，与 throughput 同值
        Assert.Equal(JsonValueKind.Null, ladder[5].ValueKind); // 末位 null=∞（终章传送阵）
        var drone = ObjectProperty(logistics, "drone");
        Assert.True(BoolProperty(drone, "available"));
        Assert.Equal("无人机", StringProperty(drone, "name_zh"));

        // §5 L79 仓库等级跃迁自动 +1：E4=4→E5=5（仓库等级=时代序号，E5=5）
        Assert.Equal(5, IntProperty(era, "warehouse_level"));

        // §4 L62 系统形态=全托管可选
        Assert.Equal("full_hosting_optional", StringProperty(era, "system_stage"));
        Assert.Equal("全托管可选", StringProperty(era, "system_stage_zh"));
        var features = ArrayProperty(era, "system_stage_features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("full_autopilot_optional", features); // §4 L62『全托管可选』

        // §4 L62 Boss=主宰 HP45000/甲30（逐字锚点）
        var boss = ObjectProperty(era, "boss");
        Assert.Equal("era5:enemy-boss", StringProperty(boss, "enemy_id"));
        Assert.Equal(45000, NumberProperty(boss, "hp"));
        Assert.Equal(30, NumberProperty(boss, "armor"));
        Assert.Equal("主宰", StringProperty(boss, "name_zh"));
    }

    [Fact]
    public void Era_energy_ladder_is_100000_400000_2000000_within_v32_growth_band()
    {
        // [F-009] 能源阶梯 100000→400000→2000000、逐级倍率 ∈[3,5]
        // 能源阶梯锚点：L1=100000（聚变电池组）/L2=400000（聚变堆）/L3=2000000（湮灭芯堆）
        // （T-134 任务卡）；倍率 400000/100000=4、2000000/400000=5 须逐级落在 v3.2 §4
        // 能源阶梯行「×3~5/级」带内（F-009 验收；E4 核电 20000→100000=×5 跨代步）
        var energy = ObjectProperty(Era(), "energy");

        var levels = ArrayProperty(energy, "levels");
        Assert.Equal(3, levels.GetArrayLength());
        Assert.Equal(1, IntProperty(levels[0], "tier"));
        Assert.Equal(100000, NumberProperty(levels[0], "output"));
        Assert.Equal(2, IntProperty(levels[1], "tier"));
        Assert.Equal(400000, NumberProperty(levels[1], "output"));
        Assert.Equal(3, IntProperty(levels[2], "tier"));
        Assert.Equal(2000000, NumberProperty(levels[2], "output"));

        Assert.InRange(NumberProperty(levels[1], "output") / NumberProperty(levels[0], "output"), 3.0, 5.0);
        Assert.InRange(NumberProperty(levels[2], "output") / NumberProperty(levels[1], "output"), 3.0, 5.0);

        // E3 起负载率类阶梯风险开闸沿用（SP-4 L142：E1/E2 risk_events=false 的反面）
        Assert.True(energy.GetProperty("risk_events").GetBoolean());
    }

    // boundary/legal：SP-4 L148 表体逐值（[NEW-原型校准] 已定案——漂移即红）
    [Theory]
    [MemberData(nameof(AnnihilationParamRows))]
    public void Annihilation_risk_params_match_SP4_row_exactly(string jsonPath, double expected)
    {
        // [F-010] SP-4 L148『E5 湮灭』行全值入内容数据（RL7：阈值禁止硬编码于引擎）
        var annihilation = ObjectProperty(ObjectProperty(Era(), "energy"), "annihilation");
        Assert.Equal("annihilation", StringProperty(annihilation, "kind"));

        // 走点路径 "trigger.overload_threshold_percent" 逐段下钻（末段为数值叶）
        var segments = jsonPath.Split('.');
        var node = annihilation;
        foreach (var segment in segments[..^1])
        {
            node = ObjectProperty(node, segment);
        }

        Assert.Equal(expected, NumberProperty(node, segments[^1]), 9);
    }

    public static TheoryData<string, double> AnnihilationParamRows => new()
    {
        // 出处：11-spec-tables.md SP-4 L148 E5 湮灭行（触发/冷却中断/半径/后果四列）
        { "trigger.overload_threshold_percent", 150 },        // 负载率 ≥150%
        { "trigger.sustained_s", 10 },                        // 连续 10 s
        { "countdown_s", 0 },                                 // 无倒计时（即时）
        { "explosion_radius_cells", 8 },                      // 半径 8 格
        { "consequences.unit_damage", 2000 },                 // 半径内一切单位受 2000 伤害
        { "consequences.chain_reaction.damage_decay_per_chain", 0.6 }, // 每连锁一栋伤害 ×0.6 衰减
        { "consequences.force_field_radius_multiplier", 0.5 }, // 力场塔在场则半径减半（SP-1b L54）
        { "consequences.wreck_recovery_cost_fraction", 0.3 }, // 残骸修复口径 §十①/F-004 同族 [NEW-T-134]（引擎载荷行）
    };

    [Fact]
    public void Annihilation_bool_flags_match_the_spec_channels()
    {
        // [F-010] SP-4 L148 布尔位：聚变建筑 HP 归零触发通道+连锁引爆开关
        var annihilation = ObjectProperty(ObjectProperty(Era(), "energy"), "annihilation");
        Assert.True(BoolProperty(annihilation.GetProperty("trigger"), "fusion_hp_zero")); // HP 归零即时触发
        Assert.True(BoolProperty(
            ObjectProperty(ObjectProperty(annihilation, "consequences"), "chain_reaction"), "enabled"));
    }

    [Fact]
    public void Annihilator_core_carries_the_confirm_dangerous_position_per_F4()
    {
        // [F-016][F-033] §11.4/F.4 危险位先例对齐（E4 核电站同族，T-134）：湮灭芯堆
        // （能源 L3 终档，携带 SP-4 L148 湮灭后果）携带 confirm 类危险位——
        // requires_confirm=true（建造须玩家确认）+never_auto_allow=true（永不入
        // 关卡级自动允许清单）。先例=v3.2 L130 SellTower{confirm_sell:true,
        // 永不自动允许}/BuildPlan{confirm_overwrite 强制 Ask}（ApprovalGate.
        // DangerBits 规则驱动表同族）。
        var levels = ArrayProperty(ObjectProperty(Era(), "energy"), "levels");
        var core = levels.EnumerateArray().Single(l => IntProperty(l, "tier") == 3);
        Assert.Equal("湮灭芯堆", StringProperty(core, "name_zh"));
        Assert.Equal(2000000, NumberProperty(core, "output"));

        var dangerous = ObjectProperty(core, "dangerous_position");
        Assert.Equal("build_confirm", StringProperty(dangerous, "confirm_class"));
        Assert.True(BoolProperty(dangerous, "requires_confirm"));
        Assert.True(BoolProperty(dangerous, "never_auto_allow"));

        // 聚变电池组/聚变堆两档不携带危险位（confirm 位=湮灭芯堆专属）
        foreach (var tier in new[] { 1, 2 })
        {
            var level = levels.EnumerateArray().Single(l => IntProperty(l, "tier") == tier);
            Assert.False(level.TryGetProperty("dangerous_position", out _),
                $"能源 L{tier} 不应携带危险位（confirm 类危险位=湮灭芯堆专属）");
        }
    }

    [Fact]
    public void Era_row_references_resolve_across_all_sibling_era5_files()
    {
        // [F-008] 时代行跨文件引用完整性：塔组/Boss/时代 id 四文件合流
        var era = Era();
        var eraId = StringProperty(era, "id");

        // era.towers 恰等于 towers.json 全体 id（§4 L62 塔防特色三塔 ↔ 实存行）
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

        // 全托管可选系统形态标记 ↔ 关 3 系统新能力排期（§4 L62『全托管可选』
        // ↔ §4 L67 关 3 系统新能力 ↔ §11.8 巡逻/托管级末档）
        var slot3Features = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features")
            .EnumerateArray().Select(f => f.GetString()!).ToList();
        Assert.Contains("full_autopilot", slot3Features);
    }

    // ---- 3) 塔组：§4 L62 + SP-1b 定类 + F.5 类推锚带 [F-016] -----------------

    private static IReadOnlyList<TowerSpec> Towers() =>
        TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era5Dir(), "towers.json")));

    public static TheoryData<string, double, double, double, string, int> TowerRows => new()
    {
        // §4 L62 塔防特色=轨道炮/无人机巢/力场塔（SP-1b L52~L54 预归类经 T-134
        // 章节设计复核追认）；数值=F.5 类推锚带 [NEW]（推导式见 towers.json 各行 _source）
        { "era5:tower-railgun", 2430, 960.0, 12.5, "energy", 1 },  // 成本 810×3 跨代基点步；320@3 发/s（reload 10=E4 导弹同档）
        { "era5:tower-drone-nest", 7290, 1920.0, 15.5, "pierce", 2 }, // 2430×3 严格；960×2 严格；256@7.5 发/s
        { "era5:tower-force-field", 1215, 192.0, 5.5, "slow", 1 },   // 控制档=T1 武器塔成本×0.5；DPS 0.2×带
    };

    [Theory]
    [MemberData(nameof(TowerRows))]
    public void Towers_match_SP1b_classes_and_F5_analog_anchor_band(
        string id, double cost, double dps, double range, string towerClass, int tier)
    {
        // [F-016] 三塔定类（SP-1b L52~L54 T-134 追认）+F.5 类推锚带值（[NEW] 入表随检）
        var spec = Towers().Single(t => t.Id == id);
        Assert.Equal(towerClass, spec.Class.ToString().ToLowerInvariant());
        Assert.Equal(tier, spec.Tier);
        Assert.Equal(cost, spec.CostTotal);
        Assert.Equal(dps, spec.Dps, 9);
        Assert.Equal(range, spec.RangeCells, 9);
    }

    [Fact]
    public void Force_field_carries_the_slow_class_band_and_the_annihilation_counter_hook()
    {
        // [F-016] SP-1b L54『力场塔=减速——可吸收湮灭 50% 半径（联动 SP-4）』：
        // 减速类 DPS 带=0.2×同级单体（SP-1a L29 [NEW-原型校准]——192=0.2×960 恰带内）
        // +湮灭半径减半钩子三文件同源（towers/era.energy.annihilation 同值断言）。
        var towers = Towers();
        var railgun = towers.Single(t => t.Id == "era5:tower-railgun");
        var field = towers.Single(t => t.Id == "era5:tower-force-field");

        Assert.Empty(UpgradeLaw.ValidateClassBand(field, railgun.Dps)); // slow 0.2×带内
        Assert.Equal(0.2 * railgun.Dps, field.Dps, 9); // 恰 0.2×同级单体（960 基准）
        Assert.Equal(1, field.MaxTier);   // 控制场设施不升级（无后继行不发明）
        Assert.Null(field.UpgradesTo);

        // 减速参数行锚（E1 陷阱 slow_percent 30/slow_ticks 120 跨代沿用）+反湮灭钩子
        var fieldRow = FindById(LoadItems("towers.json"), "era5:tower-force-field");
        var traits = ObjectProperty(fieldRow, "traits");
        Assert.Equal(30, NumberProperty(traits, "slow_percent"));
        Assert.Equal(120, NumberProperty(traits, "slow_ticks"));
        Assert.Equal(0.5, NumberProperty(traits, "force_field_annihilation_radius_multiplier"));

        // 三文件同源闭环：era.energy.annihilation.consequences.force_field_radius_multiplier 同值
        var eraField = ObjectProperty(
            ObjectProperty(ObjectProperty(Era(), "energy"), "annihilation"), "consequences");
        Assert.Equal(
            NumberProperty(traits, "force_field_annihilation_radius_multiplier"),
            NumberProperty(eraField, "force_field_radius_multiplier"),
            9); // 力场塔反湮灭钩子与 era.energy.annihilation 后果列同源成对（缺一即钩子断链）
    }

    [Fact]
    public void Railgun_carries_the_energy_class_pierce_marker()
    {
        // [F-016] SP-1a L28 能量类『穿甲』标记（可打断护盾再生 v3.2 §6——E4 激光塔
        // 同款类标记；主宰盾 2400 的反制位）
        var railgun = FindById(LoadItems("towers.json"), "era5:tower-railgun");
        Assert.True(BoolProperty(ObjectProperty(railgun, "traits"), "armor_pierce"));
    }

    [Fact]
    public void DPS_split_stays_self_consistent_on_the_tick_grid()
    {
        // [F-027] 内部一致性：dps = damage_per_shot × 30 / reload_ticks（§6 符号
        // 约定 d=单发伤害，30 Hz）——三塔全过（全部携带武器周期，E5 无零伤行）
        foreach (var spec in Towers().Where(t => t.ReloadTicks is not null))
        {
            var expected = spec.DamagePerShot * 30.0 / spec.ReloadTicks!.Value;
            Assert.Equal(expected, spec.Dps, 3);
        }
    }

    [Fact]
    public void UpgradeLaw_validates_the_whole_era5_table()
    {
        // [F-027] 任务验收「塔过 UpgradeLaw」：声明块全过 + 标准线步全过
        var towers = Towers();
        foreach (var spec in towers)
        {
            Assert.Empty(UpgradeLaw.ValidateGrowthDeclaration(spec));
        }

        // 标准线唯一步：轨道炮 T1 → 无人机巢 T2（成本 2430→7290 严格 ×3；DPS
        // 960→1920 严格 ×2；类变步能量→穿刺射程带豁免，15.5/12.5=×1.24 带内
        // （±15% 设计带）且半格网格合法值——恰值 15.625 的带内取值）
        var railgun = towers.Single(t => t.Id == "era5:tower-railgun");
        var nest = towers.Single(t => t.Id == "era5:tower-drone-nest");
        Assert.Empty(UpgradeLaw.ValidateStep(railgun, nest));

        // 严格 ×3 成本阶梯（F.5 90→270→810→2430→7290 同带重演）
        Assert.Equal(railgun.CostTotal * 3, nest.CostTotal);
        // DPS 律带 ±15%（实值恰 ×2）
        Assert.InRange(nest.Dps / railgun.Dps, 2.0 * 0.85, 2.0 * 1.15);
        Assert.Equal(2.0, nest.Dps / railgun.Dps, 9);
        // 射程律带 ±15% 围绕 +25%（实值 15.5/12.5=×1.24）
        Assert.InRange(nest.RangeCells / railgun.RangeCells, 1.25 * 0.85, 1.25 * 1.15);
        // 接线：轨道炮→无人机巢为线尾步（upgrades_to 指向实存行；无人机巢/力场塔无后继）
        Assert.Equal("era5:tower-drone-nest", railgun.UpgradesTo);
        Assert.Null(nest.UpgradesTo);
        Assert.Equal(railgun.MaxTier, nest.MaxTier);

        // 力场塔=max_tier 1（控制场设施不升级，无后继）
        var field = towers.Single(t => t.Id == "era5:tower-force-field");
        Assert.Equal(1, field.MaxTier);
        Assert.Null(field.UpgradesTo);
    }

    [Fact]
    public void Class_bands_follow_the_SP1a_policy_with_the_sentinel_kept()
    {
        // [F-026] 按塔类验证带（SP-1a）：无人机巢（T2 穿刺=标准单体行）对自身基准
        // 恰在 1.0 带；力场塔=减速 0.2× 带（T1 轨道炮基准）
        var towers = Towers();
        var railgun = towers.Single(t => t.Id == "era5:tower-railgun");
        var nest = towers.Single(t => t.Id == "era5:tower-drone-nest");
        var field = towers.Single(t => t.Id == "era5:tower-force-field");

        Assert.Empty(UpgradeLaw.ValidateClassBand(nest, nest.Dps));        // T2 自身基准
        Assert.Empty(UpgradeLaw.ValidateClassBand(railgun, railgun.Dps));  // T1 能量=标准单体行
        Assert.Empty(UpgradeLaw.ValidateClassBand(field, railgun.Dps));    // slow 0.2×带（T1 基准）

        // 跨代基点哨兵：轨道炮 DPS=前代标准线终点 960（E4 激光塔同值——跨代基点
        // 锚；防越带抬价）+无人机巢=960×2 严格
        Assert.Equal(960.0, railgun.Dps, 9);
        Assert.Equal(1920.0, nest.Dps, 9);
    }

    [Fact]
    public void Burst_price_law_accepts_the_era5_rows()
    {
        // [F-028] 爆发型代价：轨道炮对自身基准、无人机巢对 T2 标准线投影（960×2
        // =1920）、力场塔对低持续 DPS 档——均不超基准 → 无需前摇代价（持续型）
        var towers = Towers();
        var railgun = towers.Single(t => t.Id == "era5:tower-railgun");
        var nest = towers.Single(t => t.Id == "era5:tower-drone-nest");
        var field = towers.Single(t => t.Id == "era5:tower-force-field");

        var baselines = new (TowerSpec Spec, double Baseline)[]
        {
            (railgun, railgun.Dps),
            (nest, railgun.Dps * 2), // T2 标准线投影 1920
            (field, railgun.Dps),
        };
        foreach (var (spec, baseline) in baselines)
        {
            var verdict = Damage.ValidateBurstCost(spec, baseline);
            Assert.True(verdict.Allowed, $"{spec.Id}: {verdict.Violation}");
        }
    }

    // ---- 4) 敌表：新设计行 + §4 Boss 逐字锚点 + 重力脉冲 [F-035] [F-032] [F-033]

    public static TheoryData<string, double, double, double> EnemyStatRows => new()
    {
        // E5 敌表=新设计 [NEW]（行形对位 era4 敌表各位+跨代步 ×2；纳米灾变·自律
        // 机械军团）；表体值入表随检——漂移即红。Boss 主宰 HP45000/甲30=v3.2
        // §4 L62 逐字锚点。
        { "e5_nano_swarm", 480, 0, 1.6 },        // 散兵位（E4 武装民兵 240×2）
        { "e5_combat_frame", 2400, 24, 1.17 },   // 中坚位（突击兵 1200×2、甲 12→24）
        { "e5_siege_titan", 12000, 26, 0.53 },   // 攻城位（攻城机甲 6000×2、甲 20→26）
        { "e5_hover_striker", 1120, 4, 2.67 },   // 快速位（掠袭四驱 560×2、甲 2→4）
        { "e5_lancer_drone", 1440, 16, 1.07 },   // 远程位（导弹兵 720×2、甲 8→16、射程 7）
        { "e5_barrier_mech", 4800, 24, 0.85 },   // 重甲位（防爆装甲兵 2400×2、甲 18→24、盾 960）
        { "e5_melt_drone", 960, 4, 1.71 },       // 自爆位（自爆无人机 480×2、自爆 480）
        { "e5_nexus_core", 7200, 22, 0.96 },     // 精英光环位（电子战军官 3600×2、甲 16→22）
        { "e5_boss", 45000, 30, 0.32 },          // Boss（§4 逐字 45000/30；速度 0.3 格/s 历代最重移动要塞 [NEW-T-134 校准]）
    };

    [Theory]
    [MemberData(nameof(EnemyStatRows))]
    public void Enemies_match_the_E5_rows_hp_armor_speed(string spId, double hp, double armor, double speed)
    {
        // [F-035] E5 敌表九种数值锚点（表体 [NEW] 入表随检）
        var items = LoadItems("enemies.json");
        Assert.Equal(9, items.Count); // E5 恰九种（8 常规+Boss，行形对位 era3/era4 九行制）

        var enemy = items.Single(i => StringProperty(i, "sp_id") == spId);
        Assert.Equal(hp, NumberProperty(enemy, "hp"));
        Assert.Equal(armor, NumberProperty(enemy, "armor"));
        Assert.Equal(speed, NumberProperty(enemy, "speed_px_per_tick"));

        // 换算列自洽：speed × 30 / 32 = 格/s（SP-3 头部 L92 换算式，1 格=32 px）
        Assert.Equal(speed * 30.0 / 32.0, NumberProperty(enemy, "speed_cells_per_s"), 2);
    }

    [Fact]
    public void Boss_carries_gravity_pulse_trait_params_with_the_counter_hooks()
    {
        // [F-033][F-016] T-134 任务卡『主宰重力脉冲=数据位』+反制钩子双向锚
        var boss = FindById(LoadItems("enemies.json"), "era5:enemy-boss");
        Assert.True(boss.GetProperty("is_boss").GetBoolean());

        // 重力脉冲：30 s 周期（900 ticks@30 Hz）半径 3 格 600 点（E4 夜枭空袭
        // 30s/2 格/800 行锚对位 [NEW-T-134 原型校准：塔线 1520 hp 可受两发，
        // Boss 33s 亡于第二发前——enemies.json _source 推导]；引擎消费随
        // Boss 战宿主扩展）
        var pulse = ObjectProperty(ObjectProperty(boss, "traits"), "gravity_pulse");
        Assert.Equal(900, NumberProperty(pulse, "interval_ticks"));
        Assert.Equal(3.0, NumberProperty(pulse, "radius_cells"));
        Assert.Equal(600, NumberProperty(pulse, "damage"));

        // 常规 Boss 尺寸 64~96 px（v3.2 §13.1）→画布取上沿 96（era3/era4 先例）
        Assert.Equal(96, NumberProperty(boss, "canvas_size_px"));

        // 克制指向=能量+减速（轨道炮=穿甲伤害位、力场塔=减速/湮灭对策位——
        // Boss 与本代塔组的双向克制锚；两行都在 towers.json 实存）
        var counters = ArrayProperty(boss, "counters").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Contains("energy", counters);
        Assert.Contains("slow", counters);
        Assert.Contains("era5:tower-railgun", LoadItems("towers.json").Select(t => StringProperty(t, "id")));
        Assert.Contains("era5:tower-force-field", LoadItems("towers.json").Select(t => StringProperty(t, "id")));
    }

    [Fact]
    public void Shields_are_present_from_E3_on_the_heavy_rows()
    {
        // [F-032] v3.2 §6「护盾 E3 起」沿用：重甲行+Boss 携带个体 shield 键（[NEW]
        // 跨代步 era4×2；穿甲打断反制位=轨道炮 armor_pierce）；轻中甲行不携带
        var items = LoadItems("enemies.json");
        Assert.Equal(960, NumberProperty(
            FindById(items, "era5:enemy-barrier-mech"), "shield"));
        Assert.Equal(2400, NumberProperty(
            FindById(items, "era5:enemy-boss"), "shield"));

        foreach (var id in new[]
                 { "era5:enemy-nano-swarm", "era5:enemy-combat-frame", "era5:enemy-nexus-core" })
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

    // ---- 5) 配方集：§5 链深守卫 + 稀土→纳米/聚变燃料炼链 [F-022] [F-023] -----

    [Fact]
    public void Raw_resources_exclude_the_crafted_era5_resources()
    {
        // [F-023] 原料表口径：稀土/石油/铁三件（石油/铁=传承刷新）；纳米/聚变燃料=
        // era.json 资源行成员（§4 L62 点名）但为炼链制品——SupplyChain one key one
        // meaning，制品不入原料表（era3『钢』/era4『电子·混凝土』分立先例）。
        var eraResourceIds = ArrayProperty(Era(), "resources").EnumerateArray()
            .Select(r => StringProperty(r, "id")).ToHashSet();
        var raws = ArrayProperty(LoadDoc("recipes.json").RootElement, "raw_resources").EnumerateArray()
            .Select(r => StringProperty(r, "resource")).ToList();

        Assert.Equal(new[] { "era5:res-rare-earth", "era4:res-oil", "era2:res-iron" }, raws);
        Assert.All(raws, r => Assert.Contains(r, eraResourceIds)); // 原料 ⊆ 时代资源行
        Assert.Contains("era5:res-nano", eraResourceIds);          // 纳米在资源行（§4 点名）……
        Assert.Contains("era5:res-fusion-fuel", eraResourceIds);   // 聚变燃料在资源行（§4 点名）……

        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();
        Assert.Contains("era5:res-nano", outputs);                 // ……且均为配方输出（非原料）
        Assert.Contains("era5:res-fusion-fuel", outputs);
    }

    [Fact]
    public void Recipes_cover_every_era5_weapon_tower_ammo_item_exactly_once()
    {
        // [F-023] 弹药覆盖闭合：武器塔（ammo_item 非空）弹药件恰有一条配方；
        // E5 三塔全部携带弹药（E5 无零伤支援行）。配方集构成=中间件 2（纳米/
        // 聚变燃料）+弹药 3（轨道弹丸/无人机组/力场单元）。
        var recipeOutputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource"))
            .ToList();

        foreach (var tower in LoadItems("towers.json"))
        {
            if (!tower.TryGetProperty("ammo_item", out var ammoEl) || ammoEl.ValueKind is JsonValueKind.Null)
            {
                continue;
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

        // boundary：轨道弹丸/无人机组/力场单元=链深天花板锚（稀土/石油(1)→纳米(2)
        // →轨道弹丸/无人机组(3)、稀土/铁(1)→聚变燃料(2)→力场单元(3)，恰用满 ≤3）
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era5:ammo-rail-slug")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era5:ammo-drone-pack")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era5:ammo-field-cell")));
        Assert.Equal(SupplyChain.MaxDepth, chain.DepthOf(new ResourceKey("era5:ammo-rail-slug")));
    }

    [Fact]
    public void Every_era5_resource_has_a_use_in_the_baskets()
    {
        // [F-011] 传承刷新资源+本代制品确有用途（§4 跨代资源行刷新语义+无死端资源）：
        // 石油→纳米/轨道弹丸（分散介质/衬管）；铁→聚变燃料/轨道弹丸（磁约束壳体/弹芯）；
        // 稀土→纳米/聚变燃料/无人机组（催化晶格/增殖催化/电机磁体）；
        // 纳米→轨道弹丸/无人机组/力场单元；聚变燃料→力场单元
        var usedRaws = LoadItems("recipes.json")
            .SelectMany(r => ArrayProperty(r, "inputs").EnumerateArray()
                .Select(i => StringProperty(i, "resource"))).ToHashSet();
        Assert.Contains("era4:res-oil", usedRaws);
        Assert.Contains("era2:res-iron", usedRaws);
        Assert.Contains("era5:res-rare-earth", usedRaws);

        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();
        Assert.Contains("era5:res-nano", outputs);
        Assert.Contains("era5:res-fusion-fuel", outputs);
        Assert.Contains("era5:res-nano", usedRaws);          // 制品也被消费（弹丸/机组/力场单元篮）
        Assert.Contains("era5:res-fusion-fuel", usedRaws);   // 制品也被消费（力场单元篮）
    }

    // ---- 6) 前三关排期：§4 L67 + §11.8 + §7 公式 [F-012] [F-092] ------------

    public static TheoryData<int, string, string, string, int, double, int> LevelScheduleRows => new()
    {
        // 出处：v3.2 §4 L67 排期（关 1 新能源=聚变/关 2 新物流=无人机 20/s/关 3 考古+英雄+系统新能力）
        // +§11.8 三级（对话级→建造级→巡逻/托管级）+§7 公式（邻接敌营→威胁度→胜波）
        { 1, "campaign:level-e5-01", "new_energy", "dialogue", 0, 0.3, 13 },
        { 2, "campaign:level-e5-02", "new_logistics", "build", 1, 0.6, 16 },
        { 3, "campaign:level-e5-03", "archaeology_hero_system", "patrol", 2, 0.9, 19 },
    };

    [Theory]
    [MemberData(nameof(LevelScheduleRows))]
    public void Levels_match_schedule_tier_and_formula_anchors(
        int slot, string id, string focus, string tier, int camps, double threat, int winWaves)
    {
        // [F-012] 前三关排期结构与 §7 公式逐值比对
        var levels = Levels();
        Assert.Equal(3, levels.Count); // 恰前三关（T-134 范围；levels/e5.json 手工关另落）
        var level = Level(slot);

        Assert.Equal(id, StringProperty(level, "id"));
        Assert.Equal(focus, StringProperty(level, "focus"));
        Assert.Equal(tier, StringProperty(level, "schedule_tier"));
        Assert.Equal("era5:era-future", StringProperty(level, "era"));

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
        // [F-012][F-010] 关 1 聚变/关 2 无人机/关 3 考古+英雄+系统新能力：
        // 解锁特征逐关对位
        // L1：聚变系统+过热（沿用）+湮灭风险（SP-4 自本关起生效；T-134 任务卡『关 1 聚变教学』）
        var l1 = Level(1);
        var f1 = ArrayProperty(ObjectProperty(l1, "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("fusion_energy", f1);
        Assert.Contains("overheat_risk", f1);
        Assert.Contains("annihilation_risk", f1);
        Assert.True(l1.GetProperty("teaching_level").GetBoolean());
        Assert.Equal(3.0, NumberProperty(l1, "first_wave_grace_multiplier")); // §6 教学关 ×3
        Assert.False(l1.GetProperty("awakening_cinematic").GetBoolean()); // 觉醒演出=E2 L1 专属锚（§10.4）

        // L2：无人机 20/s（§4 L62 物流列；阶梯第 4 档 20.0 同值断言见时代行测试）
        var f2 = ArrayProperty(ObjectProperty(Level(2), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("drone_logistics", f2);

        // L3：考古+英雄（G.2/SP-6 每代第 3 关）+系统新能力=全托管可选（§4 L62）
        var f3 = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("archaeology", f3);
        Assert.Contains("hero_recruit", f3);
        Assert.Contains("full_autopilot", f3);

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
    public void ActionCatalog_E5_open_set_equals_level_unlock_schedule_exactly()
    {
        // [F-092] 三源互证①：G.2 解锁时代列（代码行源）——19 动作封闭清单中
        // UnlockEra==E5 的动词恰为零（全部已于 ≤E2 解锁），排期侧三关
        // unlocks.actions 并集须同为空——双向漂移即红。
        var catalogE5 = ActionCatalog.All
            .Where(d => d.UnlockEra == EraOrdinals.E5)
            .Select(d => d.Verb)
            .ToList();

        var scheduled = ScheduledActions();
        Assert.Empty(catalogE5);   // 代码侧：无 E5 新动词
        Assert.Empty(scheduled);   // 排期侧：零动作排期
        Assert.Empty(MatrixDrift(catalogE5, scheduled));

        // 代码侧补证：19 动作全体 UnlockEra ≤E2（E5 无未解锁动作=『全托管可选』
        // 非动作扩员的代码侧事实，§4 L62 全托管可选注）
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
    public void Techtree_E5_nodes_cross_reference_only_real_era5_rows()
    {
        // [F-092] 三源互证②（techtree E5 侧前向守卫）：campaign/techtree.json
        // 尚无 E5 节点（T-135/后续进度任务落位）——本守卫保证此后任何
        // era_index==5 节点的 bound_levels/unlocks/前置都解析到实存内容行，
        // 悬空引用即红。
        var campaignDir = FindRepoContentDir("campaign");
        Assert.NotNull(campaignDir);
        var allTech = LoadDocFrom(campaignDir!.FullName, "techtree.json").RootElement
            .GetProperty("items").EnumerateArray().ToList();
        var techE5 = allTech.Where(t => t.GetProperty("era_index").GetInt32() == 5).ToList();

        var levelIds = Levels().Select(l => StringProperty(l, "id")).ToHashSet();
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var allTechIds = allTech.Select(t => t.GetProperty("id").GetString()!).ToHashSet();

        foreach (var node in techE5)
        {
            var nodeId = StringProperty(node, "id");
            foreach (var bound in ArrayProperty(node, "bound_levels").EnumerateArray())
            {
                Assert.Contains(bound.GetString()!, levelIds); // 关卡闸→era5 levels 实存行
            }

            foreach (var unlocked in ArrayProperty(node, "unlocks").EnumerateArray())
            {
                var target = unlocked.GetString()!;
                if (StringProperty(node, "kind") == "tower_unlock")
                {
                    Assert.Contains(target, towerIds); // 塔解锁→era5 towers 实存行
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
    public void Legacy_transition_E5_schedule_entries_align_with_the_frozen_rows()
    {
        // [F-012] 三源互证③（era_transition 对齐）：legacy/era_transition.json
        // 的 E4→E5 first_three_schedule 扩展（target_era=5，T-134 落位）与
        // era5/levels.json 逐值对齐——本断言为 T-134 实锚（排期三行齐备）。
        var legacyDir = FindRepoContentDir("legacy");
        Assert.NotNull(legacyDir);
        var schedule = ArrayProperty(
            LoadDocFrom(legacyDir!.FullName, "era_transition.json").RootElement,
            "first_three_schedule").EnumerateArray()
            .Where(s => s.TryGetProperty("target_era", out var era) && era.GetInt32() == 5)
            .ToList();

        Assert.Equal(3, schedule.Count); // E4→E5 排期三行齐备（T-134 实锚）
        foreach (var frozen in schedule)
        {
            var slot = IntProperty(frozen, "slot");
            var level = Level(slot);
            Assert.Equal(5, IntProperty(frozen, "target_era"));
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
            .Where(d => d.UnlockEra == EraOrdinals.E5)
            .Select(d => d.Verb).ToList();

        // 真数据：零漂移（双侧皆空）
        Assert.Empty(MatrixDrift(catalog, ScheduledActions()));

        // 突变①：代码侧混入 E5 伪动词 → catalog_only:AutoBuild
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

        // 突变：抽走轨道弹丸配方 → 覆盖检查点名轨道炮弹药缺失
        var mutated = outputs.Where(o => o != "era5:ammo-rail-slug").ToHashSet();
        var missingAfter = towers
            .Where(t => t.TryGetProperty("ammo_item", out var a) && a.ValueKind != JsonValueKind.Null)
            .Where(t => !mutated.Contains(t.GetProperty("ammo_item").GetString()!))
            .Select(t => t.GetProperty("ammo_item").GetString()!).ToList();
        Assert.Equal(new[] { "era5:ammo-rail-slug" }, missingAfter);
    }

    // ---- 8) 公式代入 E=5：系统点/卷轴常驻/BuildPlan cells（§4 L68） ----------

    [Fact]
    public void Era_ordinals_substitute_into_the_E_formulas_at_E5()
    {
        // [F-092] v3.2 §4 L68 公式代入 E=5（E1=1…E5=5）：系统点 1+2×E=11、
        // 卷轴常驻上限 6+2×E=16、BuildPlan cells ≤4+2×E=14（批量建造同限）。
        // 时代序号以 EraOrdinals 冻结常量代入（K-3 量纲端点注释同源）。
        int e = EraOrdinals.E5;
        Assert.Equal(5, e);
        Assert.Equal(11, 1 + 2 * e);   // 系统点上限（E2=5→E5=11）
        Assert.Equal(16, 6 + 2 * e);   // 卷轴常驻上限（附录 F.1）
        Assert.Equal(14, 4 + 2 * e);   // BuildPlan cells 上限（附录 G.2）

        // 内容侧时代序号与代码侧常量一致（era.json era_index=5）
        Assert.Equal(EraOrdinals.E5, IntProperty(Era(), "era_index"));
    }

    // ---- 9) 注册表纪律：id 唯一性/命名空间/_new 溯源 [F-156] -----------------

    [Fact]
    public void Era5_item_ids_are_unique_and_follow_namespace_conventions()
    {
        // [F-156] 注册表 id 唯一性与命名空间（K-2 modid:contentid；关卡行归
        // campaign 命名空间=techtree/legacy 同源引用，其余归 era5 域）
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

        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count()); // era5 全域 id 唯一
        Assert.All(all.Where(x => x.File == "era.json"), x => Assert.StartsWith("era5:era-", x.Id));
        Assert.All(all.Where(x => x.File == "recipes.json"), x => Assert.StartsWith("era5:recipe-", x.Id));
        Assert.All(all.Where(x => x.File == "levels.json"), x => Assert.StartsWith("campaign:level-e5-", x.Id));
        Assert.All(all.Where(x => x.File == "towers.json"), x => Assert.StartsWith("era5:tower-", x.Id));
        Assert.All(all.Where(x => x.File == "enemies.json"), x => Assert.StartsWith("era5:enemy-", x.Id));
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

        // E5 时代行：能源逐级绝对值/湮灭块/无人机为 _new（v3.2 仅钉 ×3~5 带
        // 与塔防特色列；SP-4 表体值 [NEW-原型校准] 待 Wave 1 校准）；Boss HP/甲为
        // §4 逐字锚点 → 不得列入 _new_fields（与 era1~era4 同纪律）
        var era = Era();
        Assert.True(era.GetProperty("_new").GetBoolean());
        var eraNewFields = era.GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("energy.levels[0].output", eraNewFields);
        Assert.Contains("energy.levels[1].output", eraNewFields);
        Assert.Contains("energy.levels[2].output", eraNewFields);
        Assert.Contains("energy.levels[2].dangerous_position", eraNewFields);
        Assert.Contains("energy.annihilation", eraNewFields);
        Assert.Contains("logistics.drone", eraNewFields);

        var bossNewFields = ArrayProperty(FindById(LoadItems("enemies.json"), "era5:enemy-boss"), "_new_fields");
        Assert.DoesNotContain("hp", bossNewFields.EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("armor", bossNewFields.EnumerateArray().Select(f => f.GetString()));

        // F.5 基准表仅钉 E2 行——E5 塔三件（成本/DPS/射程）必须 _new（与 era1/era3/era4 同纪律）
        var railgunNewFields = FindById(LoadItems("towers.json"), "era5:tower-railgun")
            .GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("cost_total", railgunNewFields);
        Assert.Contains("dps", railgunNewFields);
        Assert.Contains("range_cells", railgunNewFields);
    }
}
