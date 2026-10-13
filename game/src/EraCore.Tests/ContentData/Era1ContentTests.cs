using System.Linq;
using System.Text.Json;
using EraCore.Contracts.Content;
using Xunit;

namespace EraCore.Tests.ContentData;

// [C-02] K-2 内容数据首版回归（T-049）：game/content/era1/ 三个 JSON 的锚点比对。
// 期望值全部硬编码自文档锚点（出处行写在各断言处注释），JSON 解析失败即红：
//   - 塔定类/特性锚点 = 11-spec-tables.md SP-1b L36~L39 + v3.2 §6 L82
//   - 敌表九行        = 11-spec-tables.md SP-3a L99~L107
//   - E1 时代行       = v3.2（我在游戏觉醒系统-游戏设计方案.md）§4 L58/L65 + §5 L78~L79
//   - 能源 L1=10/L2/40 = T-049 任务卡锚点（倍率 4∈[3,5]，F-009 验收）
public class Era1ContentTests
{
    // ---- 装载辅助 ----------------------------------------------------------

    private static string FindEra1Dir()
    {
        // 仓库相对寻址（RL9：禁绝对路径）：从测试程序集向上找 game/content/era1。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era1");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到 game/content/era1（测试须在仓库内运行）");
    }

    private static JsonDocument LoadDoc(string fileName)
    {
        var path = Path.Combine(FindEra1Dir(), fileName);
        Assert.True(File.Exists(path), $"内容文件缺失：{fileName}");
        // 严格模式解析：非法 JSON 直接抛异常 → 测试红（任务要求：解析失败=测试红）
        return JsonDocument.Parse(File.ReadAllText(path));
    }

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

    private static string StringProperty(JsonElement parent, string name)
    {
        Assert.True(parent.TryGetProperty(name, out var value), $"缺字段 {name}");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString()!;
    }

    private static List<JsonElement> LoadItems(string fileName)
    {
        // 注意不 using-dispose：返回的 JsonElement 只在 JsonDocument 存活期间可访问，
        // 测试内文件小且条目固定，由 GC 回收（每个测试方法至多装载三份文档）。
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

    // ---- 1) 解析与结构声明 [F-008] ------------------------------------------

    [Fact]
    public void All_three_era1_files_parse_and_declare_K2_content_types()
    {
        // [F-008] 时代行数据载体：三文件解析+K-2 content_type 声明
        foreach (var (file, expectedType) in new[]
                 {
                     ("towers.json", ContentType.Building),
                     ("enemies.json", ContentType.Enemy),
                     ("era.json", ContentType.Era),
                 })
        {
            using var doc = LoadDoc(file);
            var root = doc.RootElement;

            // $schema 预留字段（T-049 任务要求）
            var schema = StringProperty(root, "$schema");
            Assert.Matches("^urn:eratd:content:[a-z]+:1$", schema);

            // content_type 声明须与 K-2 ContentType 枚举逐字对应（12-contracts-frozen K-2）
            Assert.Equal(expectedType.ToString(), StringProperty(root, "content_type"));

            // 每个 item 也声明同枚举值
            foreach (var item in ArrayProperty(root, "items").EnumerateArray())
            {
                Assert.Equal(expectedType.ToString(), StringProperty(item, "content_type"));
            }
        }
    }

    // ---- 2) 塔组：SP-1b 定类与数量 [F-013] ----------------------------------

    public static TheoryData<string, string> TowerClassRows => new()
    {
        // 出处：11-spec-tables.md SP-1b L36~L39（E1 四塔逐塔定类）
        { "era1:tower-wood-arrow", "pierce" },   // L36 木箭塔=穿刺
        { "era1:tower-rockfall", "physical" },   // L37 落石台=物理
        { "era1:tower-spike-trap", "slow" },     // L38 陷阱=减速
        { "era1:tower-campfire", "support" },    // L39 篝火=支援
    };

    [Theory]
    [MemberData(nameof(TowerClassRows))]
    public void Towers_match_SP1b_class_assignment(string id, string towerClass)
    {
        // [F-013] E1 塔组四座定类
        var items = LoadItems("towers.json");
        Assert.Equal(4, items.Count); // SP-1b E1 行恰四塔（v3.2 §4 L58 塔防特色列同集）
        var tower = FindById(items, id);
        Assert.Equal(towerClass, StringProperty(tower, "class"));
    }

    [Fact]
    public void Tower_trait_anchors_match_SP1b_new_values()
    {
        // [F-013] E1 塔特性锚点（前摇/定身/减速/光环）
        var items = LoadItems("towers.json");

        // 落石台前摇 1.5 s（SP-1b L37 [NEW-原型校准]）× 30 Hz = 45 ticks（K-1 IClock）
        var rockfall = ObjectProperty(FindById(items, "era1:tower-rockfall"), "traits");
        Assert.Equal(45, NumberProperty(rockfall, "burst_windup_ticks"));

        // 尖刺陷阱：定身 2 s（60 ticks）+ 减速 30%×4 s（120 ticks）（SP-1b L38 [NEW-原型校准]）
        var trap = ObjectProperty(FindById(items, "era1:tower-spike-trap"), "traits");
        Assert.Equal(60, NumberProperty(trap, "root_ticks"));
        Assert.Equal(30, NumberProperty(trap, "slow_percent"));
        Assert.Equal(120, NumberProperty(trap, "slow_ticks"));
        Assert.True(trap.GetProperty("one_shot").GetBoolean()); // L38「一次性触发」

        // 篝火光环半径 3 格（SP-1b L39 [NEW-原型校准]）
        var campfire = ObjectProperty(FindById(items, "era1:tower-campfire"), "traits");
        Assert.Equal(3, NumberProperty(campfire, "aura_radius_cells"));
    }

    [Fact]
    public void Tower_growth_law_anchors_match_v32_section6()
    {
        // [F-026] 六类塔成长律
        // v3.2 §6 L82：DPS ×2/级(±15%)、成本合计 ×3/级（严格成立）、射程 +25%/级
        foreach (var tower in LoadItems("towers.json"))
        {
            var growth = ObjectProperty(tower, "tier_growth");
            Assert.Equal(2.0, NumberProperty(growth, "dps_multiplier_per_tier"));
            Assert.Equal(3.0, NumberProperty(growth, "cost_multiplier_per_tier"));
            Assert.Equal(1.25, NumberProperty(growth, "range_multiplier_per_tier"));
        }
    }

    [Fact]
    public void Combat_tower_dps_equals_damage_times_tickrate_over_reload()
    {
        // [F-027] DPS 与单发/攻速拆分自洽
        // 内部一致性：dps = damage_per_shot × 30 / reload_ticks（§6 符号约定 d=单发伤害，30 Hz）
        var items = LoadItems("towers.json");
        foreach (var id in new[] { "era1:tower-wood-arrow", "era1:tower-rockfall" })
        {
            var tower = FindById(items, id);
            var expected = NumberProperty(tower, "damage_per_shot") * 30.0
                           / NumberProperty(tower, "reload_ticks");
            Assert.Equal(expected, NumberProperty(tower, "dps"), 3);
        }
    }

    [Fact]
    public void Ammo_convention_is_ten_shots_per_item()
    {
        // [F-022] 弹药映射约定
        // v3.2 §5 L78「1 件弹药 = 10 发」
        using var doc = LoadDoc("towers.json");
        var ammo = ObjectProperty(doc.RootElement, "ammo_convention");
        Assert.Equal(10, NumberProperty(ammo, "shots_per_ammo_item"));
    }

    // ---- 3) 敌表：SP-3a 九行逐项比对 [F-035] --------------------------------

    public static TheoryData<string, double, double, double> EnemyStatRows => new()
    {
        // 出处：11-spec-tables.md SP-3a L99~L107（hp/armor/speed_px_per_tick@30Hz）
        { "e1_beastling", 40, 0, 1.60 },     // L99
        { "e1_hunter", 70, 0, 2.56 },        // L100
        { "e1_boar", 150, 2, 1.07 },         // L101
        { "e1_tribesman", 100, 0, 1.28 },    // L102
        { "e1_warband", 220, 2, 1.07 },      // L103
        { "e1_champion", 280, 3, 0.96 },     // L104
        { "e1_ram", 800, 8, 0.64 },          // L105
        { "e1_boomshrew", 50, 0, 1.92 },     // L106
        { "e1_boss", 2500, 6, 0.75 },        // L107（HP/甲为 v3.2 §4 L58 逐字锚点）
    };

    [Theory]
    [MemberData(nameof(EnemyStatRows))]
    public void Enemies_match_SP3a_hp_armor_speed(string spId, double hp, double armor, double speed)
    {
        // [F-035] E1 敌表九种数值锚点
        var items = LoadItems("enemies.json");
        Assert.Equal(9, items.Count); // SP-3a 恰九种

        var enemy = items.Single(i => StringProperty(i, "sp_id") == spId);
        Assert.Equal(hp, NumberProperty(enemy, "hp"));
        Assert.Equal(armor, NumberProperty(enemy, "armor"));
        Assert.Equal(speed, NumberProperty(enemy, "speed_px_per_tick"));

        // 换算列自洽：speed × 30 / 32 = 格/s（SP-3 头部 L92 换算式，1 格=32 px）
        Assert.Equal(speed * 30.0 / 32.0, NumberProperty(enemy, "speed_cells_per_s"), 2);
    }

    [Fact]
    public void Enemy_counters_use_closed_tower_class_or_focus_fire()
    {
        // [F-035] 克制指向封闭枚举
        // 克制提示列（SP-3a）只允许六类枚举（SP-1 L19）+ 集火意图保留值 focus_fire
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

    [Fact]
    public void Boss_trample_trait_matches_SP3a_row9()
    {
        // [F-033] Boss 踏群者技能锚点
        // SP-3a L107：践踏=半径 2 格周期伤害（周期由 T-039 定，本表只锚半径）
        var items = LoadItems("enemies.json");
        var boss = FindById(items, "era1:enemy-boss");
        Assert.True(boss.GetProperty("is_boss").GetBoolean());
        var trample = ObjectProperty(ObjectProperty(boss, "traits"), "trample");
        Assert.Equal(2, NumberProperty(trample, "radius_cells"));
    }

    // ---- 4) 时代行：v3.2 §4 E1 行逐项 [F-008] [F-009] ------------------------

    [Fact]
    public void Era_energy_ladder_is_10_then_40_within_v32_growth_band()
    {
        // [F-009] 能源阶梯 10→40、倍率 ∈[3,5]
        // 能源阶梯锚点：L1=10、L2=40（T-049 任务卡）；倍率 40/10=4 须落在
        // v3.2 §4 L65「能源阶梯 ×3~5/级」带内（F-009 验收：相邻级产量倍率在 [3,5]）
        var era = LoadItems("era.json").Single();
        var energy = ObjectProperty(era, "energy");

        var levels = ArrayProperty(energy, "levels");
        Assert.Equal(2, levels.GetArrayLength());
        Assert.Equal(1, NumberProperty(levels[0], "tier"));
        Assert.Equal(10, NumberProperty(levels[0], "output"));
        Assert.Equal(2, NumberProperty(levels[1], "tier"));
        Assert.Equal(40, NumberProperty(levels[1], "output"));

        var ratio = NumberProperty(levels[1], "output") / NumberProperty(levels[0], "output");
        Assert.InRange(ratio, 3.0, 5.0);

        // E1 人力无风险事件（SP-4 L142：风险事件 E3 起）
        Assert.False(energy.GetProperty("risk_events").GetBoolean());
    }

    [Fact]
    public void Era_row_matches_v32_section4_E1_line()
    {
        // [F-008] E1 时代行逐项（资源/物流/仓库/系统形态/Boss）
        var era = LoadItems("era.json").Single();

        Assert.Equal(1, NumberProperty(era, "era_index"));      // §4 L70 E1=1
        Assert.Equal("manpower", StringProperty(ObjectProperty(era, "energy"), "type")); // L58 能源=人力

        // L58 资源=木材/石料/食物
        var resources = ArrayProperty(era, "resources");
        var resourceIds = resources.EnumerateArray().Select(r => StringProperty(r, "id")).ToList();
        Assert.Equal(new[] { "era1:res-wood", "era1:res-stone", "era1:res-food" }, resourceIds);

        // L58 物流=民夫 0.3 件/s（§5 L78 阶梯 0.3→0.6→10→15→20→∞ 的第 0 档）
        var logistics = ObjectProperty(era, "logistics");
        Assert.Equal(0.3, NumberProperty(logistics, "throughput_items_per_s"));
        var ladder = ArrayProperty(logistics, "ladder_items_per_s");
        Assert.Equal(6, ladder.GetArrayLength());
        Assert.Equal(0.3, ladder[0].GetDouble());
        Assert.Equal(0.6, ladder[1].GetDouble());
        Assert.Equal(10.0, ladder[2].GetDouble());
        Assert.Equal(15.0, ladder[3].GetDouble());
        Assert.Equal(20.0, ladder[4].GetDouble());
        Assert.Equal(JsonValueKind.Null, ladder[5].ValueKind); // 末位 null=∞

        // §5 L79 仓库等级 E1=1
        Assert.Equal(1, NumberProperty(era, "warehouse_level"));

        // L58 系统形态=绑定期
        Assert.Equal("bound", StringProperty(era, "system_stage"));

        // L58 Boss=踏群者 HP2500/甲6
        var boss = ObjectProperty(era, "boss");
        Assert.Equal(2500, NumberProperty(boss, "hp"));
        Assert.Equal(6, NumberProperty(boss, "armor"));
        Assert.Equal("踏群者", StringProperty(boss, "name_zh"));
    }

    [Fact]
    public void Era_tower_and_boss_references_resolve_to_sibling_files()
    {
        // [F-008] 时代行引用跨文件可解析
        // 跨文件引用完整性：era.towers ⊆ towers.json ids；era.boss 指向 enemies.json Boss 行
        var era = LoadItems("era.json").Single();
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var enemyIds = LoadItems("enemies.json").Select(e => StringProperty(e, "id")).ToHashSet();

        var eraTowers = ArrayProperty(era, "towers").EnumerateArray().Select(t => t.GetString()!).ToList();
        Assert.Equal(4, eraTowers.Count);
        Assert.All(eraTowers, id => Assert.Contains(id, towerIds));

        var bossRef = StringProperty(ObjectProperty(era, "boss"), "enemy_id");
        Assert.Contains(bossRef, enemyIds);

        // Boss HP/甲在两文件中同值（v3.2 §4 L58 锚点不得漂移）
        var bossEnemy = FindById(LoadItems("enemies.json"), bossRef);
        var bossEra = ObjectProperty(era, "boss");
        Assert.Equal(NumberProperty(bossEnemy, "hp"), NumberProperty(bossEra, "hp"));
        Assert.Equal(NumberProperty(bossEnemy, "armor"), NumberProperty(bossEra, "armor"));
    }

    // ---- 5) 注册表纪律：id 唯一性/命名空间/_new 溯源 ------------------------

    [Fact]
    public void All_item_ids_are_unique_and_follow_era1_namespace_format()
    {
        // [F-156] 注册表 id 唯一性与命名空间
        // K-2 命名空间（IContentItem.Id：modid:contentid 形态；本库首方域=era1）
        var pattern = "^era1:[a-z][a-z0-9]*(-[a-z0-9]+)*$";
        var all = new List<(string File, string Id)>();
        foreach (var file in new[] { "towers.json", "enemies.json", "era.json" })
        {
            foreach (var item in LoadItems(file))
            {
                var id = StringProperty(item, "id");
                Assert.Matches(pattern, id);       // 通用命名空间格式
                all.Add((file, id));
            }
        }

        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count()); // 全库 id 唯一
        Assert.All(
            all.Where(x => x.File == "towers.json"), x => Assert.StartsWith("era1:tower-", x.Id));
        Assert.All(
            all.Where(x => x.File == "enemies.json"), x => Assert.StartsWith("era1:enemy-", x.Id));
        Assert.All(
            all.Where(x => x.File == "era.json"), x => Assert.StartsWith("era1:era-", x.Id));
    }

    [Fact]
    public void New_flag_policy_marks_invented_values_with_sources()
    {
        // [F-156] _new/_source 溯源纪律
        // _new 纪律（11-spec-tables L7：新发明数值一律标注，待原型校准）
        foreach (var file in new[] { "towers.json", "enemies.json", "era.json" })
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

        // 木箭塔整行无文档数值锚点（F.5 基准为 E2 塔）→ 必须 _new
        var woodArrow = FindById(LoadItems("towers.json"), "era1:tower-wood-arrow");
        Assert.True(woodArrow.GetProperty("_new").GetBoolean());
        Assert.Contains("cost_total", woodArrow.GetProperty("_new_fields").EnumerateArray()
            .Select(f => f.GetString()));

        // Boss HP/甲为 v3.2 §4 逐字锚点 → 不得列入 _new_fields
        var boss = FindById(LoadItems("enemies.json"), "era1:enemy-boss");
        var newFields = boss.GetProperty("_new_fields").EnumerateArray()
            .Select(f => f.GetString()).ToList();
        Assert.DoesNotContain("hp", newFields);
        Assert.DoesNotContain("armor", newFields);
    }
}
