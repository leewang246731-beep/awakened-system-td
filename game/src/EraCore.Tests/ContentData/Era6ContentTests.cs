using System.Linq;
using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Combat;
using EraCore.Contracts.Content;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Logistics;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.ContentData;

// [C-02] K-2 终章内容数据行回归（T-135）：game/content/era6/ 五文件
// （era/towers/enemies/recipes/levels）的锚点比对 + 三源一致性矩阵。
// 期望值全部硬编码自文档锚点（出处行写在各断言处注释），JSON 解析失败即红：
//   - 终章时代行     = v3.2 §4 L63（docs/我在游戏觉醒系统-游戏设计方案.md 终章 深空 行）
//                       +§4 能源阶梯行（风险列无终章项→零点能=无风险能源）/跨代资源行
//                       +§4 L68 公式代入 E=6 +§5 L78~L79（物流阶梯末位 ∞；仓库 6）
//   - 能源 L1=10000000/L2=40000000/L3=200000000 = T-135 类推锚带（倍率 ×4/×5 ∈[3,5]）
//   - 能源风险        = 无（SP-4 适用代=E3 起负载率类风险表无终章行——零点能=无风险能源）
//   - 终章塔组        = §4 L63+SP-1b L56~L57（T-135 章节设计复核追认：时空塔=减速/奇点炮=能量）；
//                       数值=F.5 类推锚带 [NEW]（跨代基点步 ×2 校准——推导链见 towers.json _source）
//   - 终章敌表        = 新设计 [NEW]（行形对位 era5 敌表+跨代步 ×2）；Boss 堕落守望者
//                       HP120000/甲40=§4 L63 逐字锚点；Boss 画布 96×112=§13.1 L204 逐字锚点
//   - 配方集          = v3.2 §5 L74~L76（链深 ≤3；1 件弹药=10 发）+§4 L63 暗物质→反物质炼链
//   - 前三关排期      = v3.2 §4 L67 + §11.8（对话级→建造级→巡逻/托管级）+§7 公式
//   - 三源互证        = ActionCatalog 终章开放集（空——19 动作封闭清单全于 ≤E2 解锁）↔ levels 排期侧
//                     ↔ campaign/techtree.json 终章节点（前向守卫）
public class Era6ContentTests
{
    // ---- 装载辅助（与 Era1~Era5ContentTests 同纪律） ------------------------

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

    private static string Era6Dir()
    {
        var dir = FindRepoContentDir("era6");
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

    private static JsonDocument LoadDoc(string fileName) => LoadDocFrom(Era6Dir(), fileName);

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
        // 测试内文件小且条目固定，由 GC 回收（与 Era1~Era5ContentTests 同约定）。
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

    private static IReadOnlyList<TowerSpec> Towers() =>
        TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era6Dir(), "towers.json")));

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
        // 矩阵比较器（era2~era5 先例复刻）：任一侧多出/缺失即点名（负向用例喂突变集）。
        var drift = new List<string>();
        drift.AddRange(catalogSide.Except(scheduleSide, StringComparer.Ordinal)
            .Select(v => $"catalog_only:{v}"));
        drift.AddRange(scheduleSide.Except(catalogSide, StringComparer.Ordinal)
            .Select(v => $"schedule_only:{v}"));
        return drift;
    }

    // ---- 1) 解析与结构声明 [F-008] ------------------------------------------

    [Fact]
    public void All_five_era6_files_parse_and_declare_K2_content_types()
    {
        // [F-008] 终章行数据载体：五文件解析+K-2 content_type 声明。
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

            // $schema 预留字段（与 era1~era5 同格式约定）
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
    public void Era6_towers_and_enemies_load_through_the_real_loaders()
    {
        // [F-018][F-035] RL7 实装载器回归：TowerSpec.LoadCollection /
        // EnemySpecCatalog.Parse 全量吃下 era6 两表（坏形状直接抛即红）。
        var towers = TowerSpec.LoadCollection(File.ReadAllText(Path.Combine(Era6Dir(), "towers.json")));
        Assert.Equal(2, towers.Count); // §4 L63 塔防特色恰两名：时空塔/奇点炮
        Assert.All(towers, t => Assert.StartsWith("era6:tower-", t.Id, StringComparison.Ordinal));

        var enemies = EnemySpecCatalog.Parse(File.ReadAllText(Path.Combine(Era6Dir(), "enemies.json")));
        Assert.Equal(9, enemies.Count);
        var boss = enemies.Get("era6:enemy-boss");
        Assert.True(boss.IsBoss);
        Assert.Equal(120000, boss.Hp);  // §4 L63 逐字锚点（全六代顶点）
        Assert.Equal(40, boss.Armor);   // §4 L63 逐字锚点（全六代顶点）
        Assert.Equal(4800, boss.ShieldPoints); // 护盾跨代步（era5 主宰 2400×2 [NEW]）
        Assert.Equal(0.2667, boss.SpeedPxPerTick, 4); // 0.25 格/s 全六代最重移动要塞 [NEW-T-135 校准]
    }

    // ---- 2) 时代行：v3.2 §4 终章行逐项 [F-008] [F-009] [F-011] --------------

    [Fact]
    public void Era_row_matches_v32_section4_finale_line()
    {
        // [F-008] 终章时代行逐项（资源/能源/物流/仓库/系统形态/Boss）
        var era = Era();

        Assert.Equal(6, IntProperty(era, "era_index")); // §4 L68 时代序号 终章=6（E1=1…E5=5）
        Assert.Equal("era6:era-finale", StringProperty(era, "id"));
        Assert.Equal("深空", StringProperty(era, "name_zh"));
        Assert.Equal("Deep Space", StringProperty(era, "name_en"));

        // §4 L63 资源=反物质/暗物质 + 上代（E5）2 种基础资源传承刷新（稀土/铁，丰度 ×0.5）
        var resources = ArrayProperty(era, "resources").EnumerateArray().ToList();
        var resourceIds = resources.Select(r => StringProperty(r, "id")).ToList();
        Assert.Equal(
            new[]
            {
                "era6:res-dark-matter", "era6:res-antimatter", "era5:res-rare-earth", "era2:res-iron",
            },
            resourceIds);

        // 传承刷新行：丰度乘数恰 0.5、来自 E5（§4 跨代资源行——上代=紧邻前代 E5，
        // 其原料集=稀土/石油/铁，取稀土（E5 本代新矿）+铁（连续第四代刷新）；
        // 反物质=制品不入刷新对，era3『钢』~era5『纳米·聚变燃料』分立先例）
        foreach (var legacyId in new[] { "era5:res-rare-earth", "era2:res-iron" })
        {
            var refresh = ObjectProperty(
                resources.Single(r => StringProperty(r, "id") == legacyId), "legacy_refresh");
            Assert.Equal(5, IntProperty(refresh, "from_era"));
            Assert.Equal(0.5, NumberProperty(refresh, "abundance_multiplier"));
        }

        // §4 L63 能源=零点能
        Assert.Equal("zero_point", StringProperty(ObjectProperty(era, "energy"), "type"));
        Assert.Equal("零点能", StringProperty(ObjectProperty(era, "energy"), "name_zh"));

        // §4 L63 物流=传送阵（§5 L78 阶梯第 6 档=末位 ∞；throughput null=∞）
        var logistics = ObjectProperty(era, "logistics");
        Assert.Equal(5, IntProperty(logistics, "tier"));
        Assert.Equal("teleporter", StringProperty(logistics, "carrier"));
        Assert.Equal("传送阵", StringProperty(logistics, "carrier_zh"));
        Assert.Equal(JsonValueKind.Null, logistics.GetProperty("throughput_items_per_s").ValueKind); // null=∞
        var ladder = ArrayProperty(logistics, "ladder_items_per_s");
        Assert.Equal(6, ladder.GetArrayLength());
        Assert.Equal(0.3, ladder[0].GetDouble());
        Assert.Equal(0.6, ladder[1].GetDouble());
        Assert.Equal(10.0, ladder[2].GetDouble());
        Assert.Equal(15.0, ladder[3].GetDouble()); // E4 传送带档
        Assert.Equal(20.0, ladder[4].GetDouble()); // E5 无人机档
        Assert.Equal(JsonValueKind.Null, ladder[5].ValueKind); // 末位 null=∞（终章传送阵档=本档）
        var teleporter = ObjectProperty(logistics, "teleporter");
        Assert.True(BoolProperty(teleporter, "available"));
        Assert.Equal("传送阵", StringProperty(teleporter, "name_zh"));

        // §5 L79 仓库等级跃迁自动 +1：E5=5→终章=6（仓库等级=时代序号，终章=6）
        Assert.Equal(6, IntProperty(era, "warehouse_level"));

        // §4 L63 系统形态=真相揭示（播片对话）——播片本体=T-136 域，本行=数据位
        Assert.Equal("truth_revelation", StringProperty(era, "system_stage"));
        Assert.Equal("真相揭示（播片对话）", StringProperty(era, "system_stage_zh"));
        var features = ArrayProperty(era, "system_stage_features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("truth_revelation", features);

        // §4 L63 Boss=堕落守望者 HP120000/甲40（逐字锚点）
        var boss = ObjectProperty(era, "boss");
        Assert.Equal("era6:enemy-boss", StringProperty(boss, "enemy_id"));
        Assert.Equal(120000, NumberProperty(boss, "hp"));
        Assert.Equal(40, NumberProperty(boss, "armor"));
        Assert.Equal("堕落守望者", StringProperty(boss, "name_zh"));
    }

    [Fact]
    public void Era_energy_ladder_is_1e7_4e7_2e8_within_v32_growth_band()
    {
        // [F-009] 能源阶梯 10000000→40000000→200000000、逐级倍率 ∈[3,5]
        // 能源阶梯锚点：L1=10000000（零点能电池组）/L2=40000000（零点能矩阵）/
        // L3=200000000（真空涨落井）（T-135 类推锚带）；倍率 4e7/1e7=4、2e8/4e7=5
        // 须逐级落在 v3.2 §4 能源阶梯行「×3~5/级」带内（F-009 验收；
        // E5 湮灭芯堆 2000000→10000000=×5 跨代步——E4→E5 ×5 同式）
        var energy = ObjectProperty(Era(), "energy");

        var levels = ArrayProperty(energy, "levels");
        Assert.Equal(3, levels.GetArrayLength());
        Assert.Equal(1, IntProperty(levels[0], "tier"));
        Assert.Equal(10000000, NumberProperty(levels[0], "output"));
        Assert.Equal(2, IntProperty(levels[1], "tier"));
        Assert.Equal(40000000, NumberProperty(levels[1], "output"));
        Assert.Equal(3, IntProperty(levels[2], "tier"));
        Assert.Equal(200000000, NumberProperty(levels[2], "output"));

        Assert.InRange(NumberProperty(levels[1], "output") / NumberProperty(levels[0], "output"), 3.0, 5.0);
        Assert.InRange(NumberProperty(levels[2], "output") / NumberProperty(levels[1], "output"), 3.0, 5.0);

        // 跨代步 ×5（E5 L3 2000000 → 终章 L1 10000000）
        Assert.Equal(5.0, NumberProperty(levels[0], "output") / 2000000.0, 9);
    }

    [Fact]
    public void Zero_point_energy_carries_no_risk_channels_per_the_spec_silence()
    {
        // [F-010] 零点能=无风险能源（T-135 终章风险口径——依据写明）：
        // ①v3.2 §4 能源阶梯行风险列=E1 粮仓满溢/E3 过热爆炸/E4 熔毁/E5 湮灭
        //   （E2 无）——终章未列风险项（与 E2 同为无风险档）；
        // ②SP-4 L142 适用代=E3 起的负载率类阶梯风险表，表体仅 E3/E4/E5 三行，
        //   无终章行——两处真相源一致。
        // 落位：era.json risk_events=false + 全 energy 块无 risk/annihilation/
        // meltdown 通道 + 三级均无 dangerous_position（危险位=风险后果建筑位的
        // 前置闸——era4 核电/era5 湮灭芯堆反例对照，无风险即无闸）。
        var energy = ObjectProperty(Era(), "energy");

        Assert.False(energy.GetProperty("risk_events").GetBoolean()); // 与 era1/era2 同形（人力/水力无负载率风险先例）
        Assert.False(energy.TryGetProperty("risk", out _));           // 无过热通道（E3+ 沿用行不在终章）
        Assert.False(energy.TryGetProperty("meltdown", out _));       // 无熔毁通道（E4 行域）
        Assert.False(energy.TryGetProperty("annihilation", out _));   // 无湮灭通道（E5 行域——湮灭到 E5 止）

        foreach (var level in ArrayProperty(energy, "levels").EnumerateArray())
        {
            Assert.False(level.TryGetProperty("dangerous_position", out _),
                $"零点能 L{IntProperty(level, "tier")} 不携带危险位（无风险能源无后果即无闸）");
        }

        // E5 对照面：era5 的 risk/annihilation 通道仍在其位（终章只关自己——不改前代）
        var era5Energy = ObjectProperty(LoadDocFrom(FindRepoContentDir("era5")!.FullName, "era.json")
            .RootElement.GetProperty("items").EnumerateArray().Single(), "energy");
        Assert.True(era5Energy.GetProperty("risk_events").GetBoolean());
        Assert.True(era5Energy.TryGetProperty("annihilation", out _));
    }

    [Fact]
    public void Teleporter_declares_the_unbounded_ladder_rung_matching_the_engine_tier()
    {
        // [F-042][F-008] 传送阵 ∞ 档（T-135 任务卡『查全文』结论）：
        // v3.2 §4 L63 终章行物流列只写『传送阵』未标速率——§5 L78 物流阶梯
        // 0.3→0.6→10→15→20→∞ 六值恰对六代、末位 ∞ 无他代可指 → 传送阵=∞ 件/s
        //（非与无人机 20/s 同档——20=E5 无人机档位值）。内容侧（era.json
        // throughput null=∞）与引擎侧（TransportTiers.Teleporter）同值对齐。
        Assert.Equal(double.PositiveInfinity, TransportTiers.Teleporter.ThroughputAnchorItemsPerSecond);
        Assert.Equal(TransportForm.Teleport, TransportTiers.Teleporter.Form);
        Assert.Equal("teleporter", StringProperty(ObjectProperty(Era(), "logistics"), "carrier"));
        Assert.Equal(TransportTiers.Teleporter.Id, StringProperty(ObjectProperty(Era(), "logistics"), "carrier"));

        // 阶梯档位自检：tier 5（0 基）=第 6 档=末位；六档与 TransportTiers.Ladder 同序同值
        Assert.Equal(5, IntProperty(ObjectProperty(Era(), "logistics"), "tier"));
        Assert.Equal(6, TransportTiers.Ladder.Count);
        Assert.Same(TransportTiers.Teleporter, TransportTiers.Ladder[5]);

        // ∞ 的引擎语义：任何有限乘子下吞吐恒 ∞（世博会 WonderThroughputBoard 通道）
        Assert.Equal(double.PositiveInfinity,
            TransportTiers.Teleporter.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx, 1.25));
    }

    // ---- 3) 塔组：SP-1b 定类追认 + F.5 类推锚带 [F-018] [F-026] [F-027] [F-028]

    public static TheoryData<string, double, double, double, string, int> TowerRows => new()
    {
        // F.5 类推锚带 [NEW]（值入表随检——漂移即红）：奇点炮 cost 4860=2430×2
        //（跨代基点步 ×2——×3 链值 7290×2=14580=池上限 12000 的 121.5% 破产，
        // ×2 步=敌 HP×2/塔 DPS×2 同幅；推导链 towers.json _source）；时空塔
        // 2430=4860×0.5（支援/控制档折半带）。
        { "era6:tower-singularity", 4860, 1920.0, 15.5, "energy", 1 },
        { "era6:tower-spacetime", 2430, 384.0, 6.5, "slow", 1 },
    };

    [Theory]
    [MemberData(nameof(TowerRows))]
    public void Towers_match_SP1b_classes_and_F5_analog_anchor_band(
        string id, double cost, double dps, double range, string towerClass, int tier)
    {
        // [F-018] 两塔定类（SP-1b L56~L57 T-135 追认）+F.5 类推锚带值（[NEW] 入表随检）
        var spec = Towers().Single(t => t.Id == id);
        Assert.Equal(towerClass, spec.Class.ToString().ToLowerInvariant());
        Assert.Equal(tier, spec.Tier);
        Assert.Equal(cost, spec.CostTotal);
        Assert.Equal(dps, spec.Dps, 9);
        Assert.Equal(range, spec.RangeCells, 9);
    }

    [Fact]
    public void Spacetime_carries_the_slow_class_band_and_the_phase_counter_theme()
    {
        // [F-018] SP-1b L56『终章 时空塔=减速』：减速类 DPS 带=0.2×同级单体
        //（SP-1a L29 [NEW-原型校准]——384=0.2×1920 恰带内，E5 力场塔 192=0.2×960 同式）
        //+相位突击体（克减速行）对位反制主题。
        var towers = Towers();
        var singularity = towers.Single(t => t.Id == "era6:tower-singularity");
        var spacetime = towers.Single(t => t.Id == "era6:tower-spacetime");

        Assert.Empty(UpgradeLaw.ValidateClassBand(spacetime, singularity.Dps)); // slow 0.2×带内
        Assert.Equal(0.2 * singularity.Dps, spacetime.Dps, 9); // 恰 0.2×同级单体（1920 基准）
        Assert.Equal(1, spacetime.MaxTier);   // 控制场设施不升级（无后继行不发明，E5 力场塔同款）
        Assert.Null(spacetime.UpgradesTo);

        // 减速参数行锚（E1 陷阱 slow_percent 30/slow_ticks 120 跨代沿用——E5 力场塔同值沿用先例）
        var spacetimeRow = FindById(LoadItems("towers.json"), "era6:tower-spacetime");
        var traits = ObjectProperty(spacetimeRow, "traits");
        Assert.Equal(30, NumberProperty(traits, "slow_percent"));
        Assert.Equal(120, NumberProperty(traits, "slow_ticks"));

        // 对位反制主题：克减速行的相位突击体在本代敌表实存（enemies.json counters=slow）
        var phase = FindById(LoadItems("enemies.json"), "era6:enemy-phase-striker");
        Assert.Contains("slow", ArrayProperty(phase, "counters").EnumerateArray().Select(c => c.GetString()!));
    }

    [Fact]
    public void Singularity_carries_the_energy_class_pierce_marker()
    {
        // [F-018] SP-1b L57『终章 奇点炮=能量』：SP-1a L28 能量类『穿甲』标记
        //（可打断护盾再生——E4 激光/E5 轨道炮同款类标记；堕落守望者盾 4800
        // 的反制位）。
        var singularityRow = FindById(LoadItems("towers.json"), "era6:tower-singularity");
        Assert.True(BoolProperty(ObjectProperty(singularityRow, "traits"), "armor_pierce"));

        var singularity = Towers().Single(t => t.Id == "era6:tower-singularity");
        Assert.Null(singularity.UpgradesTo);
        Assert.Equal(1, singularity.MaxTier); // §4 L63 塔防特色恰两名——不发明 T2 行
    }

    [Fact]
    public void DPS_split_stays_self_consistent_on_the_tick_grid()
    {
        // [F-027] 内部一致性：dps = damage_per_shot × 30 / reload_ticks（§6 符号
        // 约定 d=单发伤害，30 Hz）——两塔全过（全部携带武器周期，终章无零伤行）
        foreach (var spec in Towers().Where(t => t.ReloadTicks is not null))
        {
            var expected = spec.DamagePerShot * 30.0 / spec.ReloadTicks!.Value;
            Assert.Equal(expected, spec.Dps, 3);
        }
    }

    [Fact]
    public void UpgradeLaw_validates_the_whole_era6_table()
    {
        // [F-027] 任务验收「塔过 UpgradeLaw」：声明块全过（终章无线内步——
        // 两塔均 max_tier 1 无后继，声明块合法性即全表合法性）
        foreach (var spec in Towers())
        {
            Assert.Empty(UpgradeLaw.ValidateGrowthDeclaration(spec));
        }

        // 跨代基点哨兵：奇点炮 DPS=前代标准线终点 1920（E5 T2 无人机巢同值——
        // 跨代基点锚；防越带抬价）——『新时代 T1 不弱于前代任何塔』第三步重演
        var singularity = Towers().Single(t => t.Id == "era6:tower-singularity");
        Assert.Equal(1920.0, singularity.Dps, 9);

        // 终章无 upgrade 步：UpgradesTo 全空（E5 力场塔 max_tier 1 先例的整表形态）
        Assert.All(Towers(), t =>
        {
            Assert.Null(t.UpgradesTo);
            Assert.Equal(1, t.MaxTier);
        });
    }

    [Fact]
    public void Class_bands_follow_the_SP1a_policy_with_the_sentinel_kept()
    {
        // [F-026] 按塔类验证带（SP-1a）：奇点炮（能量=标准单体行）对自身基准
        // 恰在 1.0 带；时空塔=减速 0.2× 带（奇点炮基准）——终章同级无第二标准
        // 单体参照行→类带以自身基准断言（E5 无人机巢同款先例）
        var towers = Towers();
        var singularity = towers.Single(t => t.Id == "era6:tower-singularity");
        var spacetime = towers.Single(t => t.Id == "era6:tower-spacetime");

        Assert.Empty(UpgradeLaw.ValidateClassBand(singularity, singularity.Dps)); // 能量=标准单体行
        Assert.Empty(UpgradeLaw.ValidateClassBand(spacetime, singularity.Dps));   // slow 0.2×带
    }

    [Fact]
    public void Burst_price_law_accepts_the_era6_rows()
    {
        // [F-028] 爆发型代价：奇点炮/时空塔对自身基准——持续型不超基准 → 无需前摇代价
        var towers = Towers();
        foreach (var spec in towers)
        {
            var verdict = Damage.ValidateBurstCost(spec, spec.Dps);
            Assert.True(verdict.Allowed, $"{spec.Id}: {verdict.Violation}");
        }
    }

    [Fact]
    public void Cross_era_cost_basis_is_calibrated_at_two_not_the_bankrupt_three()
    {
        // [F-018][F-024] 跨代基点步校准留档（[NEW-T-135 原型校准]——推导链防失忆）：
        // 池上限公式 2000×仓库等级 线性（终章=12000）vs 跨代成本 ×3 链指数
        //（E5 T1 2430×3=7290）——×3 链值 7290×2 塔=14580=池上限 121.5%：双炮
        // 压轴线（终局对位 e5-final 三塔线）不可建造。×2 步=敌表 HP×2/塔 DPS×2
        // 同幅（单位 EHP 造价持平），4860×2=9720=池上限 81%（E5 终局 9720/10000
        // =97% 同形）。F.5 严格 ×3/级律=线内档步冻结律（本代无线内步），跨代
        // 基点=类推惯例 [NEW] 可校准。
        var singularity = Towers().Single(t => t.Id == "era6:tower-singularity");
        Assert.Equal(2430 * 2, singularity.CostTotal); // ×2 跨代步（E5 T1 2430 基点）
        Assert.Equal(0.5 * singularity.CostTotal,
            Towers().Single(t => t.Id == "era6:tower-spacetime").CostTotal); // 支援/控制档折半带

        // 池上限面（附录 F.3：2000×仓库等级，无金字塔基准）：双炮 9720 ≤ 12000
        Assert.Equal(6, IntProperty(Era(), "warehouse_level"));
        Assert.True(2 * singularity.CostTotal <= 2000 * 6,
            "双炮线必须在终章池上限内（×3 链值 14580>12000 即破产——本断言钉 ×2 校准的存在理由）");
    }

    // ---- 4) 敌表：新设计行 + §4 Boss 逐字锚点 + 画布 96×112 [F-035][F-032][F-033]

    public static TheoryData<string, double, double, double> EnemyStatRows => new()
    {
        // 终章敌表=新设计 [NEW]（行形对位 era5 敌表各位+跨代步 ×2；深空回响·
        // 堕落守望者残军）；表体值入表随检——漂移即红。Boss 堕落守望者
        // HP120000/甲40=v3.2 §4 L63 逐字锚点。
        { "e6_void_swarm", 960, 0, 1.6 },        // 散兵位（E5 纳米蜂群 480×2）
        { "e6_null_frame", 4800, 28, 1.17 },     // 中坚位（作战机框 2400×2、甲 24→28）
        { "e6_siege_colossus", 24000, 32, 0.53 },// 攻城位（攻城泰坦 12000×2、甲 26→32）
        { "e6_phase_striker", 2240, 4, 2.67 },   // 快速位（掠空突击体 1120×2；克减速→时空塔对位）
        { "e6_lancer_array", 2880, 20, 1.07 },   // 远程位（长枪无人机 1440×2、甲 16→20、射程 8）
        { "e6_aegis_paladin", 9600, 28, 0.85 },  // 重甲位（屏障机甲 4800×2、甲 24→28、盾 1920）
        { "e6_entropy_wraith", 1920, 4, 1.71 },  // 自爆位（熔蚀无人机 960×2、自爆 960）
        { "e6_choir_core", 14400, 26, 0.96 },    // 精英光环位（枢纽核 7200×2、甲 22→26）
        { "e6_boss", 120000, 40, 0.2667 },       // Boss（§4 逐字 120000/40；速度 0.25 格/s 全六代最重 [NEW-T-135 校准]）
    };

    [Theory]
    [MemberData(nameof(EnemyStatRows))]
    public void Enemies_match_the_finale_rows_hp_armor_speed(string spId, double hp, double armor, double speed)
    {
        // [F-035] 终章敌表九种数值锚点（表体 [NEW] 入表随检）
        var items = LoadItems("enemies.json");
        Assert.Equal(9, items.Count); // 终章恰九种（8 常规+Boss，行形对位 era3~era5 九行制）

        var enemy = items.Single(i => StringProperty(i, "sp_id") == spId);
        Assert.Equal(hp, NumberProperty(enemy, "hp"));
        Assert.Equal(armor, NumberProperty(enemy, "armor"));
        Assert.Equal(speed, NumberProperty(enemy, "speed_px_per_tick"));

        // 换算列自洽：speed × 30 / 32 = 格/s（SP-3 头部 L92 换算式，1 格=32 px）
        Assert.Equal(speed * 30.0 / 32.0, NumberProperty(enemy, "speed_cells_per_s"), 2);
    }

    [Fact]
    public void Boss_carries_the_96x112_canvas_and_the_terminal_gravity_pulse()
    {
        // [F-033][F-018] T-135 任务卡『终章 Boss 画布 96×112』+『堕落守望者=初代守望者』：
        // 画布 96×112=v3.2 §13.1 L204 逐字锚点（宽 96=常规 Boss 64~96 上沿、高 112=
        // 超出常规带的终章专属纵幅——canvas_size_px=96 宽+canvas_height_px=112 高双字段
        // 钉死，T-142 美术包（era6 贴图域）消费的数据位）。
        var boss = FindById(LoadItems("enemies.json"), "era6:enemy-boss");
        Assert.True(boss.GetProperty("is_boss").GetBoolean());
        Assert.Equal(96, NumberProperty(boss, "canvas_size_px"));
        Assert.Equal(112, NumberProperty(boss, "canvas_height_px"));

        // 重力脉冲：30 s 周期（900 ticks@30 Hz）半径 3.5 格 800 点（E5 主宰
        // 900t/3格/600 行锚对位的终章增强档 [NEW]：奇点炮 hp 2560 可受三发——
        // E5 轨道炮 1520 受两发的对位步；引擎消费随 Boss 战宿主扩展，E5 同款
        // 数据位先行纪律）
        var pulse = ObjectProperty(ObjectProperty(boss, "traits"), "gravity_pulse");
        Assert.Equal(900, NumberProperty(pulse, "interval_ticks"));
        Assert.Equal(3.5, NumberProperty(pulse, "radius_cells"));
        Assert.Equal(800, NumberProperty(pulse, "damage"));

        // 克制指向=能量+减速（奇点炮=穿甲伤害位、时空塔=减速位——Boss 与本代
        // 塔组的双向克制锚，E5 主宰同构；两行都在 towers.json 实存）
        var counters = ArrayProperty(boss, "counters").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Contains("energy", counters);
        Assert.Contains("slow", counters);
        Assert.Contains("era6:tower-singularity", LoadItems("towers.json").Select(t => StringProperty(t, "id")));
        Assert.Contains("era6:tower-spacetime", LoadItems("towers.json").Select(t => StringProperty(t, "id")));

        // 叙事锚数据位（台词本体=T-136 域）：堕落守望者=初代守望者（v3.2 §12 L190）
        Assert.Contains("初代守望者", StringProperty(boss, "_source"));
    }

    [Fact]
    public void Shields_are_present_from_E3_on_the_heavy_rows()
    {
        // [F-032] v3.2 §6「护盾 E3 起」沿用：重甲行+Boss 携带个体 shield 键（[NEW]
        // 跨代步 era5×2；穿甲打断反制位=奇点炮 armor_pierce）；轻中甲行不携带
        var items = LoadItems("enemies.json");
        Assert.Equal(1920, NumberProperty(
            FindById(items, "era6:enemy-aegis-paladin"), "shield"));
        Assert.Equal(4800, NumberProperty(
            FindById(items, "era6:enemy-boss"), "shield"));

        foreach (var id in new[]
                 { "era6:enemy-void-swarm", "era6:enemy-null-frame", "era6:enemy-choir-core" })
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
                Assert.Contains(counter.GetString()!, allowed);
            }
        }
    }

    // ---- 5) 配方集：链深/篮子/覆盖 [F-023] [F-022] [F-011] ------------------

    [Fact]
    public void Raw_resources_exclude_the_crafted_era6_resources()
    {
        // [F-023] 原料表口径：暗物质/稀土/铁三件（稀土/铁=传承刷新）；反物质=
        // era.json 资源行成员（§4 L63 点名）但为炼链制品——SupplyChain one key
        // one meaning，制品不入原料表（era3~era5 分立先例）。
        var eraResourceIds = ArrayProperty(Era(), "resources").EnumerateArray()
            .Select(r => StringProperty(r, "id")).ToHashSet();
        var raws = ArrayProperty(LoadDoc("recipes.json").RootElement, "raw_resources").EnumerateArray()
            .Select(r => StringProperty(r, "resource")).ToList();

        Assert.Equal(new[] { "era6:res-dark-matter", "era5:res-rare-earth", "era2:res-iron" }, raws);
        Assert.All(raws, r => Assert.Contains(r, eraResourceIds)); // 原料 ⊆ 时代资源行
        Assert.Contains("era6:res-antimatter", eraResourceIds);    // 反物质在资源行（§4 点名）……

        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();
        Assert.Contains("era6:res-antimatter", outputs);           // ……且为配方输出（非原料）+本代建造货币
    }

    [Fact]
    public void Recipes_cover_every_era6_weapon_tower_ammo_item_exactly_once()
    {
        // [F-023] 弹药覆盖闭合：武器塔（ammo_item 非空）弹药件恰有一条配方；
        // 终章两塔全部携带弹药。配方集构成=中间件 1（反物质）+弹药 2（奇点弹/
        // 时序单元）。
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

        // boundary：奇点弹/时序单元=链深天花板锚（暗物质/稀土(1)→反物质(2)
        // →奇点弹/时序单元(3)，恰用满 ≤3）
        Assert.Equal(2, chain.DepthOf(new ResourceKey("era6:res-antimatter")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era6:ammo-singularity-shell")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era6:ammo-chrono-cell")));
        Assert.Equal(SupplyChain.MaxDepth, chain.DepthOf(new ResourceKey("era6:ammo-singularity-shell")));
    }

    [Fact]
    public void Every_era6_resource_has_a_use_in_the_baskets()
    {
        // [F-011] 传承刷新资源+本代制品确有用途（§4 跨代资源行刷新语义+无死端资源）：
        // 暗物质→反物质/奇点弹/时序单元（引力囚禁/坍缩引信/曲率场源）；稀土→反物质
        //（磁约束阱催化）；铁→奇点弹（弹芯——连续第五代弹芯位）；反物质→奇点弹/时序单元
        var usedRaws = LoadItems("recipes.json")
            .SelectMany(r => ArrayProperty(r, "inputs").EnumerateArray()
                .Select(i => StringProperty(i, "resource"))).ToHashSet();
        Assert.Contains("era6:res-dark-matter", usedRaws);
        Assert.Contains("era5:res-rare-earth", usedRaws);
        Assert.Contains("era2:res-iron", usedRaws);

        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();
        Assert.Contains("era6:res-antimatter", outputs);
        Assert.Contains("era6:res-antimatter", usedRaws); // 制品也被消费（奇点弹/时序单元篮）
    }

    // ---- 6) 前三关排期：§4 L67 + §11.8 + §7 公式 [F-012] [F-092] ------------

    public static TheoryData<int, string, string, string, int, double, int> LevelScheduleRows => new()
    {
        // 出处：v3.2 §4 L67 排期（关 1 新能源=零点能/关 2 新物流=传送阵 ∞/关 3 考古+英雄+系统新能力）
        // +§11.8 三级（对话级→建造级→巡逻/托管级）+§7 公式（邻接敌营→威胁度→胜波）
        { 1, "campaign:level-e6-01", "new_energy", "dialogue", 0, 0.3, 13 },
        { 2, "campaign:level-e6-02", "new_logistics", "build", 1, 0.6, 16 },
        { 3, "campaign:level-e6-03", "archaeology_hero_system", "patrol", 2, 0.9, 19 },
    };

    [Theory]
    [MemberData(nameof(LevelScheduleRows))]
    public void Levels_match_schedule_tier_and_formula_anchors(
        int slot, string id, string focus, string tier, int camps, double threat, int winWaves)
    {
        // [F-012] 前三关排期结构与 §7 公式逐值比对
        var levels = Levels();
        Assert.Equal(3, levels.Count); // 恰前三关（T-135 排期侧；levels/e6.json 手工关另落）
        var level = Level(slot);

        Assert.Equal(id, StringProperty(level, "id"));
        Assert.Equal(focus, StringProperty(level, "focus"));
        Assert.Equal(tier, StringProperty(level, "schedule_tier"));
        Assert.Equal("era6:era-finale", StringProperty(level, "era"));

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
        // [F-012][F-010] 关 1 零点能/关 2 传送阵/关 3 考古+英雄+系统新能力：
        // 解锁特征逐关对位
        // L1：零点能（era.json energy zero_point——risk_events=false 的无风险能源
        // 自本关开放；T-135 任务卡『关 1 零点能教学』）
        var l1 = Level(1);
        var f1 = ArrayProperty(ObjectProperty(l1, "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("zero_point_energy", f1);
        Assert.DoesNotContain("overheat_risk", f1);    // 终章无过热通道（零点能=无风险）
        Assert.DoesNotContain("annihilation_risk", f1); // 湮灭到 E5 止
        Assert.True(l1.GetProperty("teaching_level").GetBoolean());
        Assert.Equal(3.0, NumberProperty(l1, "first_wave_grace_multiplier")); // §6 教学关 ×3
        Assert.False(l1.GetProperty("awakening_cinematic").GetBoolean()); // 觉醒演出=E2 L1 专属锚（§10.4）

        // L2：传送阵 ∞ 档（§4 L63 物流列；阶梯末位 ∞ 同值断言见传送阵测试）
        var f2 = ArrayProperty(ObjectProperty(Level(2), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("teleporter_logistics", f2);

        // L3：考古+英雄（G.2/SP-6 每代第 3 关）+系统新能力=真相揭示（§4 L63；
        // 播片对话本体=T-136 域，本行=解锁数据位）
        var f3 = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("archaeology", f3);
        Assert.Contains("hero_recruit", f3);
        Assert.Contains("truth_revelation", f3);

        // 非教学关首波宽限在 §6 带 ×1.5~2 内
        Assert.InRange(NumberProperty(Level(2), "first_wave_grace_multiplier"), 1.5, 2.0);
        Assert.InRange(NumberProperty(Level(3), "first_wave_grace_multiplier"), 1.5, 2.0);

        // 前三关难度递进（威胁 0.3<0.6<0.9 → 胜波 13<16<19——历代首三关同式）
        Assert.True(NumberProperty(Level(1), "threat") < NumberProperty(Level(2), "threat"));
        Assert.True(NumberProperty(Level(2), "threat") < NumberProperty(Level(3), "threat"));
        Assert.True(IntProperty(Level(1), "win_waves") < IntProperty(Level(2), "win_waves"));
        Assert.True(IntProperty(Level(2), "win_waves") < IntProperty(Level(3), "win_waves"));
    }

    // ---- 7) 三源一致性矩阵：ActionCatalog ↔ levels ↔ techtree [F-092] -------

    [Fact]
    public void ActionCatalog_finale_open_set_equals_level_unlock_schedule_exactly()
    {
        // [F-092] 三源互证①：G.2 解锁时代列（代码行源）——19 动作封闭清单中
        // UnlockEra==终章(6) 的动词恰为零（全部已于 ≤E2 解锁），排期侧三关
        // unlocks.actions 并集须同为空——双向漂移即红。
        var catalogFinale = ActionCatalog.All
            .Where(d => d.UnlockEra == EraOrdinals.Finale)
            .Select(d => d.Verb)
            .ToList();

        var scheduled = ScheduledActions();
        Assert.Empty(catalogFinale);   // 代码侧：无终章新动词
        Assert.Empty(scheduled);       // 排期侧：零动作排期
        Assert.Empty(MatrixDrift(catalogFinale, scheduled));

        // 代码侧补证：19 动作全体 UnlockEra ≤E2（终章无未解锁动作=『真相揭示』
        // 非动作扩员的代码侧事实，§4 L63 系统形态注）
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
    public void Techtree_finale_nodes_cross_reference_only_real_era6_rows()
    {
        // [F-092] 三源互证②（techtree 终章侧前向守卫）：campaign/techtree.json
        // 尚无终章节点（后续进度任务落位——T-134 E5 同款前向守卫）——本守卫
        // 保证此后任何 era_index==6 节点的 bound_levels/unlocks/前置都解析到
        // 实存内容行，悬空引用即红。
        var campaignDir = FindRepoContentDir("campaign");
        Assert.NotNull(campaignDir);
        var allTech = LoadDocFrom(campaignDir!.FullName, "techtree.json").RootElement
            .GetProperty("items").EnumerateArray().ToList();
        var techFinale = allTech.Where(t => t.GetProperty("era_index").GetInt32() == 6).ToList();

        var levelIds = Levels().Select(l => StringProperty(l, "id")).ToHashSet();
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var allTechIds = allTech.Select(t => t.GetProperty("id").GetString()!).ToHashSet();

        foreach (var node in techFinale)
        {
            var nodeId = StringProperty(node, "id");
            foreach (var bound in ArrayProperty(node, "bound_levels").EnumerateArray())
            {
                Assert.Contains(bound.GetString()!, levelIds); // 关卡闸→era6 levels 实存行
            }

            foreach (var unlocked in ArrayProperty(node, "unlocks").EnumerateArray())
            {
                var target = unlocked.GetString()!;
                if (StringProperty(node, "kind") == "tower_unlock")
                {
                    Assert.Contains(target, towerIds); // 塔解锁→era6 towers 实存行
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
    public void Legacy_transition_finale_schedule_entries_align_with_the_frozen_rows()
    {
        // [F-012] 三源互证③（era_transition 对齐）：legacy/era_transition.json
        // 的 E5→终章 first_three_schedule 扩展（target_era=6，T-135 落位）与
        // era6/levels.json 逐值对齐——本断言为 T-135 实锚（排期三行齐备）。
        var legacyDir = FindRepoContentDir("legacy");
        Assert.NotNull(legacyDir);
        var schedule = ArrayProperty(
            LoadDocFrom(legacyDir!.FullName, "era_transition.json").RootElement,
            "first_three_schedule").EnumerateArray()
            .Where(s => s.TryGetProperty("target_era", out var era) && era.GetInt32() == 6)
            .ToList();

        Assert.Equal(3, schedule.Count); // E5→终章排期三行齐备（T-135 实锚）
        foreach (var frozen in schedule)
        {
            var slot = IntProperty(frozen, "slot");
            var level = Level(slot);
            Assert.Equal(6, IntProperty(frozen, "target_era"));
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
            .Where(d => d.UnlockEra == EraOrdinals.Finale)
            .Select(d => d.Verb).ToList();

        // 真数据：零漂移（双侧皆空）
        Assert.Empty(MatrixDrift(catalog, ScheduledActions()));

        // 突变①：代码侧混入终章伪动词 → catalog_only:AutoBuild
        var drift1 = MatrixDrift(catalog.Append("AutoBuild"), ScheduledActions());
        Assert.Contains("catalog_only:AutoBuild", drift1);

        // 突变②：排期侧混入未登记动作 → schedule_only:Bogus
        var drift2 = MatrixDrift(catalog, new List<string> { "Bogus" });
        Assert.Contains("schedule_only:Bogus", drift2);
    }

    // ---- 8) 公式代入 E=6：系统点/卷轴常驻/BuildPlan cells（§4 L68） ----------

    [Fact]
    public void Era_ordinals_substitute_into_the_E_formulas_at_the_finale()
    {
        // [F-092] v3.2 §4 L68 公式代入 E=6（E1=1…终章=6）：系统点 1+2×E=13、
        // 卷轴常驻上限 6+2×E=18、BuildPlan cells ≤4+2×E=16（批量建造同限）。
        // 时代序号以 EraOrdinals 冻结常量代入（K-3 量纲端点注释同源）。
        int e = EraOrdinals.Finale;
        Assert.Equal(6, e);
        Assert.Equal(13, 1 + 2 * e);   // 系统点上限（E2=5→E5=11→终章=13）
        Assert.Equal(18, 6 + 2 * e);   // 卷轴常驻上限（附录 F.1；E2=10→终章=18）
        Assert.Equal(16, 4 + 2 * e);   // BuildPlan cells 上限（附录 G.2；批量建造同限）

        // 内容侧时代序号与代码侧常量一致（era.json era_index=6）
        Assert.Equal(EraOrdinals.Finale, IntProperty(Era(), "era_index"));
    }

    [Fact]
    public void Finale_formula_channels_reach_the_E6_values_through_production_apis()
    {
        // [F-092][F-064][F-037][F-036] 生产 API 面 E=6 到值（引擎零改动自动扩展
        // 的实证——三条通道全部时代序号参数化）：
        // ①系统点上限（SystemPointLedger.Ceiling 实例面，era 1..6 宿主域）
        Assert.Equal(13, new EraCore.Agent.Governance.SystemPointLedger(EraOrdinals.Finale).Ceiling);
        // ②卷轴常驻上限（ScrollEngine.ResidentCap 实例面）
        Assert.Equal(18, new EraCore.Scrolls.ScrollEngine(EraOrdinals.Finale).ResidentCap);
        // ③BuildPlan cells ≤4+2×era（JsonSchemaValidator @era 6：16 格过、17 格拒；
        // 对照 era 5=14 格上限——时代序号参数化的双向证据）
        var validatorFinale = new EraCore.Agent.Actions.JsonSchemaValidator(() => EraOrdinals.Finale);
        Assert.True(validatorFinale.Validate(
            EraCore.Tests.Agent.Actions.ActionJsonSamples.BuildPlan(16)).IsValid);
        Assert.False(validatorFinale.Validate(
            EraCore.Tests.Agent.Actions.ActionJsonSamples.BuildPlan(17)).IsValid);
        var validatorE5 = new EraCore.Agent.Actions.JsonSchemaValidator(() => EraOrdinals.E5);
        Assert.True(validatorE5.Validate(
            EraCore.Tests.Agent.Actions.ActionJsonSamples.BuildPlan(14)).IsValid);
        Assert.False(validatorE5.Validate(
            EraCore.Tests.Agent.Actions.ActionJsonSamples.BuildPlan(15)).IsValid);
    }

    // ---- 9) 注册表纪律：id 唯一性/命名空间/_new 溯源 [F-156] -----------------

    [Fact]
    public void Era6_item_ids_are_unique_and_follow_namespace_conventions()
    {
        // [F-156] 注册表 id 唯一性与命名空间（K-2 modid:contentid；关卡行归
        // campaign 命名空间=techtree/legacy 同源引用，其余归 era6 域）
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

        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count()); // era6 全域 id 唯一
        Assert.All(all.Where(x => x.File == "era.json"), x => Assert.StartsWith("era6:era-", x.Id));
        Assert.All(all.Where(x => x.File == "recipes.json"), x => Assert.StartsWith("era6:recipe-", x.Id));
        Assert.All(all.Where(x => x.File == "levels.json"), x => Assert.StartsWith("campaign:level-e6-", x.Id));
        Assert.All(all.Where(x => x.File == "towers.json"), x => Assert.StartsWith("era6:tower-", x.Id));
        Assert.All(all.Where(x => x.File == "enemies.json"), x => Assert.StartsWith("era6:enemy-", x.Id));
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

        // 终章时代行：能源逐级绝对值/传送阵块为 _new（v3.2 仅钉 ×3~5 带、
        // 阶梯末位 ∞ 与塔防特色列）；Boss HP/甲为 §4 逐字锚点 → 不得列入
        // _new_fields（与 era1~era5 同纪律）
        var era = Era();
        Assert.True(era.GetProperty("_new").GetBoolean());
        var eraNewFields = era.GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("energy.levels[0].output", eraNewFields);
        Assert.Contains("energy.levels[1].output", eraNewFields);
        Assert.Contains("energy.levels[2].output", eraNewFields);
        Assert.Contains("logistics.teleporter", eraNewFields);
        Assert.Contains("logistics.throughput_items_per_s", eraNewFields);

        var bossNewFields = ArrayProperty(FindById(LoadItems("enemies.json"), "era6:enemy-boss"), "_new_fields");
        Assert.DoesNotContain("hp", bossNewFields.EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("armor", bossNewFields.EnumerateArray().Select(f => f.GetString()));

        // F.5 基准表仅钉 E2 行——终章塔两件（成本/DPS/射程）必须 _new（与 era1~era5 同纪律）
        var singularityNewFields = FindById(LoadItems("towers.json"), "era6:tower-singularity")
            .GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("cost_total", singularityNewFields);
        Assert.Contains("dps", singularityNewFields);
        Assert.Contains("range_cells", singularityNewFields);
    }

    // ---- 10) 命名空间卫生：e1~e5 关卡集不前引 era6 域 [F-055] ----------------

    [Fact]
    public void Earlier_era_level_files_reference_no_era6_content_ids()
    {
        // [F-055][F-012] 前引守卫（e1~e4 命令流 drone 档不提前守卫的 era6 版）：
        // 终章内容 id 不得出现在任何前代关卡集/前代 era 域——时代排期的
        // 单向性（era6 只被 era_transition target_era=6 行与终章自身引用）。
        var levelsDir = FindRepoContentDir("levels")!;
        foreach (var file in new[] { "e1.json", "e2.json", "e3.json", "e4.json", "e5.json" })
        {
            string text = File.ReadAllText(Path.Combine(levelsDir.FullName, file));
            Assert.DoesNotContain("era6:", text);
        }

        foreach (var eraDir in new[] { "era1", "era2", "era3", "era4", "era5" })
        {
            foreach (var file in new[] { "era.json", "towers.json", "enemies.json", "recipes.json", "levels.json" })
            {
                var path = Path.Combine(FindRepoContentDir(eraDir)!.FullName, file);
                if (File.Exists(path))
                {
                    Assert.DoesNotContain("era6:", File.ReadAllText(path));
                }
            }
        }
    }
}
