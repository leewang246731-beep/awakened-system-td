using System.Linq;
using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Contracts.Content;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using Xunit;

namespace EraCore.Tests.ContentData;

// [C-02] K-2 E2 内容数据行回归（T-079）：game/content/era2/ 新增三文件
// （era.json/recipes.json/levels.json）的锚点比对 + 三源一致性矩阵。
// 期望值全部硬编码自文档锚点（出处行写在各断言处注释），JSON 解析失败即红：
//   - E2 时代行       = v3.2 §4 L59（docs/我在游戏觉醒系统-游戏设计方案.md）+§4 能源阶梯/跨代资源行+§5 L78~L79
//   - 能源 L1=30/L2=100 = T-079 任务卡锚点（倍率 100/30≈3.33∈[3,5]，F-009 验收）
//   - 配方集          = v3.2 §5 L74~L76（链深 ≤3；1 件弹药=10 发）+T-034 E1 锚点（2 木→1 箭、60 ticks）
//   - 前三关排期      = v3.2 §4 L67 + §11.8（对话级→建造级→巡逻/托管级）+附录 G.2 解锁时代列
//   - 威胁度/胜波     = v3.2 §7 公式 clamp(0.3+0.3×邻接敌营,0.3,1.2) / 10+⌊威胁度×10⌋
//   - 三源互证        = ActionCatalog E2 开放集 ↔ levels 解锁排期 ↔ campaign/techtree.json E2 节点
//     （关卡 id 同时对齐 legacy/era_transition.json first_three_schedule，T-074 冻结）
public class Era2ContentTests
{
    // ---- 装载辅助 ----------------------------------------------------------

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

    private static string Era2Dir()
    {
        var dir = FindRepoContentDir("era2");
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

    private static JsonDocument LoadDoc(string fileName) => LoadDocFrom(Era2Dir(), fileName);

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
        // 测试内文件小且条目固定，由 GC 回收（与 Era1ContentTests 同约定）。
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

    private static List<string> CatalogE2Verbs()
    {
        // ActionCatalog 的"E2 开放集"（三源矩阵的"注册表侧"全集，G.2 解锁时代列）。
        return ActionCatalog.All
            .Where(d => d.UnlockEra == EraOrdinals.E2)
            .Select(d => d.Verb)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> MatrixDrift(IEnumerable<string> catalogSide, IEnumerable<string> scheduleSide)
    {
        // 三源矩阵比较器：任一侧多出/缺失即点名（漂移即红的数据源；负向用例喂突变集）。
        var drift = new List<string>();
        drift.AddRange(catalogSide.Except(scheduleSide, StringComparer.Ordinal)
            .Select(v => $"catalog_only:{v}"));
        drift.AddRange(scheduleSide.Except(catalogSide, StringComparer.Ordinal)
            .Select(v => $"schedule_only:{v}"));
        return drift;
    }

    // ---- 1) 解析与结构声明 [F-008] ------------------------------------------

    [Fact]
    public void All_three_new_era2_files_parse_and_declare_K2_content_types()
    {
        // [F-008] E2 行数据载体：era/recipes/levels 三文件解析+K-2 content_type 声明。
        // recipes 无 K-2 独立类 → 薄版声明 Resource 族（先例=objectives.json Level 族，T-073）。
        foreach (var (file, expectedType) in new[]
                 {
                     ("era.json", ContentType.Era),
                     ("recipes.json", ContentType.Resource),
                     ("levels.json", ContentType.Level),
                 })
        {
            using var doc = LoadDoc(file);
            var root = doc.RootElement;

            // $schema 预留字段（与 era1/scrolls 同格式约定）
            Assert.Matches("^urn:eratd:content:[a-z]+:1$", StringProperty(root, "$schema"));

            // content_type 声明须与 K-2 ContentType 枚举逐字对应（12-contracts-frozen K-2）
            Assert.Equal(expectedType.ToString(), StringProperty(root, "content_type"));

            foreach (var item in ArrayProperty(root, "items").EnumerateArray())
            {
                Assert.Equal(expectedType.ToString(), StringProperty(item, "content_type"));
            }
        }
    }

    // ---- 2) 时代行：v3.2 §4 E2 行逐项 [F-008] [F-009] ------------------------

    [Fact]
    public void Era_row_matches_v32_section4_E2_line()
    {
        // [F-008] E2 时代行逐项（资源/能源/物流/仓库/系统形态/Boss）
        var era = LoadItems("era.json").Single();

        Assert.Equal(2, IntProperty(era, "era_index")); // §4 L70 时代序号 E2=2
        Assert.Equal("era2:era-classical", StringProperty(era, "id"));

        // §4 L59 资源=铜/铁/粮/马 + 上代 2 种基础资源传承刷新（木材/石料，丰度 ×0.5）
        var resources = ArrayProperty(era, "resources").EnumerateArray().ToList();
        var resourceIds = resources.Select(r => StringProperty(r, "id")).ToList();
        Assert.Equal(
            new[]
            {
                "era2:res-copper", "era2:res-iron", "era2:res-grain", "era2:res-horse",
                "era1:res-wood", "era1:res-stone",
            },
            resourceIds);

        // 传承刷新行：丰度乘数恰 0.5、来自 E1（§4 跨代资源行锚点）
        foreach (var legacyId in new[] { "era1:res-wood", "era1:res-stone" })
        {
            var refresh = ObjectProperty(
                resources.Single(r => StringProperty(r, "id") == legacyId), "legacy_refresh");
            Assert.Equal(1, IntProperty(refresh, "from_era"));
            Assert.Equal(0.5, NumberProperty(refresh, "abundance_multiplier"));
        }

        // §4 L59 能源=水力
        Assert.Equal("waterpower", StringProperty(ObjectProperty(era, "energy"), "type"));

        // §4 L59 物流=马车 0.6、水渠（§5 L78 阶梯第 1 档）
        var logistics = ObjectProperty(era, "logistics");
        Assert.Equal(1, IntProperty(logistics, "tier"));
        Assert.Equal("carriage", StringProperty(logistics, "carrier"));
        Assert.Equal("马车", StringProperty(logistics, "carrier_zh"));
        Assert.Equal(0.6, NumberProperty(logistics, "throughput_items_per_s"));
        var ladder = ArrayProperty(logistics, "ladder_items_per_s");
        Assert.Equal(6, ladder.GetArrayLength());
        Assert.Equal(0.3, ladder[0].GetDouble());
        Assert.Equal(0.6, ladder[1].GetDouble()); // E2 档位=阶梯第 1 档，与 throughput 同值
        Assert.Equal(10.0, ladder[2].GetDouble());
        Assert.Equal(15.0, ladder[3].GetDouble());
        Assert.Equal(20.0, ladder[4].GetDouble());
        Assert.Equal(JsonValueKind.Null, ladder[5].ValueKind); // 末位 null=∞
        Assert.True(ObjectProperty(logistics, "aqueduct").GetProperty("available").GetBoolean()); // 水渠

        // §5 L79 仓库等级跃迁自动 +1：E1=1→E2=2
        Assert.Equal(2, IntProperty(era, "warehouse_level"));

        // §4 L59 系统形态=执行期
        Assert.Equal("executive", StringProperty(era, "system_stage"));
        Assert.Equal("执行期", StringProperty(era, "system_stage_zh"));

        // §4 L59 Boss=铁蹄 HP4000/甲10（逐字锚点）
        var boss = ObjectProperty(era, "boss");
        Assert.Equal("era2:enemy-boss", StringProperty(boss, "enemy_id"));
        Assert.Equal(4000, NumberProperty(boss, "hp"));
        Assert.Equal(10, NumberProperty(boss, "armor"));
        Assert.Equal("铁蹄", StringProperty(boss, "name_zh"));
    }

    [Fact]
    public void Era_energy_ladder_is_30_then_100_within_v32_growth_band()
    {
        // [F-009] 能源阶梯 30→100、倍率 ∈[3,5]
        // 能源阶梯锚点：L1=30（水力）、L2=100（风车）（T-079 任务卡）；倍率
        // 100/30≈3.33 须落在 v3.2 §4 能源阶梯行「×3~5/级」带内（F-009 验收）
        var energy = ObjectProperty(LoadItems("era.json").Single(), "energy");

        var levels = ArrayProperty(energy, "levels");
        Assert.Equal(2, levels.GetArrayLength());
        Assert.Equal(1, IntProperty(levels[0], "tier"));
        Assert.Equal(30, NumberProperty(levels[0], "output"));
        Assert.Equal(2, IntProperty(levels[1], "tier"));
        Assert.Equal(100, NumberProperty(levels[1], "output"));

        var ratio = NumberProperty(levels[1], "output") / NumberProperty(levels[0], "output");
        Assert.InRange(ratio, 3.0, 5.0);

        // E2 无负载率类阶梯风险（SP-4 L142：风险事件 E3 起）
        Assert.False(energy.GetProperty("risk_events").GetBoolean());
    }

    [Fact]
    public void Era_row_references_resolve_across_all_sibling_era2_files()
    {
        // [F-008] 时代行跨文件引用完整性：塔组/Boss/时代 id 四文件合流
        var era = LoadItems("era.json").Single();
        var eraId = StringProperty(era, "id");

        // era.towers 恰等于 towers.json 全体 id（§4 L59 塔防特色列 ↔ T-065 实存行）
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var eraTowers = ArrayProperty(era, "towers").EnumerateArray().Select(t => t.GetString()!).ToList();
        Assert.Equal(5, eraTowers.Count);
        Assert.All(eraTowers, id => Assert.Contains(id, towerIds));
        Assert.Equal(towerIds.OrderBy(i => i, StringComparer.Ordinal),
            eraTowers.OrderBy(i => i, StringComparer.Ordinal));

        // era.boss 指向 enemies.json Boss 行且 HP/甲同值（v3.2 §4 逐字锚点不得漂移）
        var bossRef = StringProperty(ObjectProperty(era, "boss"), "enemy_id");
        var bossEnemy = FindById(LoadItems("enemies.json"), bossRef);
        Assert.True(bossEnemy.GetProperty("is_boss").GetBoolean());
        Assert.Equal(NumberProperty(bossEnemy, "hp"), NumberProperty(ObjectProperty(era, "boss"), "hp"));
        Assert.Equal(NumberProperty(bossEnemy, "armor"), NumberProperty(ObjectProperty(era, "boss"), "armor"));

        // 时代 id 合流：towers/enemies 根 era 字段+heroes 的 E2 行 era 字段+scrolls 根 era 字段全部同指
        //（heroes.json 内 era1:hero-firstfire 为跨代传承行——era 指回 E1，不参与本代合流断言）
        Assert.Equal(eraId, StringProperty(LoadDoc("towers.json").RootElement, "era"));
        Assert.Equal(eraId, StringProperty(LoadDoc("enemies.json").RootElement, "era"));
        foreach (var hero in LoadItems("heroes.json")
                     .Where(h => StringProperty(h, "id").StartsWith("era2:", StringComparison.Ordinal)))
        {
            Assert.Equal(eraId, StringProperty(hero, "era"));
        }

        var scrollsDir = FindRepoContentDir("scrolls");
        Assert.NotNull(scrollsDir);
        Assert.Equal(eraId, StringProperty(LoadDocFrom(scrollsDir!.FullName, "era2.json").RootElement, "era"));

        // 执行期系统形态标记 ↔ L1 觉醒演出（§4 L59『执行期（可代建；首个啊哈演出）』↔§10.4）
        var features = ArrayProperty(era, "system_stage_features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("delegate_build", features);
        Assert.Contains("first_ahwa_cinematic", features);
        Assert.True(Level(1).GetProperty("awakening_cinematic").GetBoolean());
    }

    // ---- 3) 配方集：§5 链深守卫 + T-034 E1 锚点 [F-023] ----------------------

    [Fact]
    public void Recipes_cover_every_era2_tower_ammo_item_exactly_once()
    {
        // [F-023] 弹药覆盖闭合：era2 每座塔的弹药件恰有一条配方（换弹系五档全接）
        var recipeOutputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource"))
            .ToList();

        foreach (var tower in LoadItems("towers.json"))
        {
            var ammo = StringProperty(tower, "ammo_item");
            Assert.True(recipeOutputs.Count(o => o == ammo) == 1, $"弹药 {ammo} 的配方数须恰为 1");
        }

        // 配方集构成：中间件 2（铜锭/铁锭）+ 弹药 5（T-079 任务卡四类——弩箭[铁矢系]含石矢/铁矢/攻城矢三档+火油+箭矢）
        Assert.Equal(7, recipeOutputs.Count);
        var kinds = LoadItems("recipes.json").Select(r => StringProperty(r, "kind")).ToList();
        Assert.Equal(2, kinds.Count(k => k == "intermediate"));
        Assert.Equal(5, kinds.Count(k => k == "ammo"));
    }

    [Fact]
    public void Recipe_baskets_follow_the_AmmoRecipe_shape_rules()
    {
        // [F-023] 篮子形状守卫：对齐 AmmoRecipe 构造约束（输入非空/整件 ≥1/无重复资源/时长 ≥1 tick/输出唯一）
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
        // [F-022][F-023] 链深 ≤3 守卫（接 T-034 SupplyChain.Validate）：装载即校验，违规即红
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

        // 铁矢/攻城矢=链深天花板锚（铁(1)→锭(2)→矢(3)，§5 L74 恰用满 ≤3）
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era2:ammo-iron-bolt")));
        Assert.Equal(3, chain.DepthOf(new ResourceKey("era2:ammo-siege-bolt")));
        Assert.Equal(SupplyChain.MaxDepth, chain.DepthOf(new ResourceKey("era2:ammo-iron-bolt")));
    }

    [Fact]
    public void Arrow_recipe_carries_the_T034_E1_two_wood_anchor()
    {
        // [F-023] E1 锚点配方 2 木→1 箭、60 ticks（AmmoConverter.E1ArrowRecipe [NEW-T-034]）
        // ——E2 箭矢沿用同篮同时长（弹链本代化=换输出 id，不改锚点值）：与代码常量互证。
        var arrow = FindById(LoadItems("recipes.json"), "era2:recipe-arrow");

        var inputs = ArrayProperty(arrow, "inputs").EnumerateArray().ToList();
        var e1Inputs = AmmoConverter.E1ArrowRecipe.Inputs;
        Assert.Equal(e1Inputs.Count, inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            Assert.Equal(e1Inputs[i].Resource.Value, StringProperty(inputs[i], "resource"));
            Assert.Equal(e1Inputs[i].Items, IntProperty(inputs[i], "amount"));
        }

        Assert.Equal("era2:ammo-arrow", StringProperty(ObjectProperty(arrow, "output"), "resource"));
        Assert.Equal(AmmoConverter.E1ArrowRecipe.CraftTicks, IntProperty(arrow, "craft_ticks"));
        Assert.Equal(AmmoConverter.E1CraftTicks, IntProperty(arrow, "craft_ticks")); // 60 ticks=2 s@30 Hz
    }

    [Fact]
    public void Recipe_inputs_resolve_to_era_row_resources()
    {
        // [F-023] 配方输入闭包：原料表 ⊆ era.json 资源行；中间件输入须出自原料或他条配方输出
        var eraResources = ArrayProperty(LoadItems("era.json").Single(), "resources").EnumerateArray()
            .Select(r => StringProperty(r, "id")).ToHashSet();
        var doc = LoadDoc("recipes.json");
        var raws = ArrayProperty(doc.RootElement, "raw_resources").EnumerateArray()
            .Select(r => StringProperty(r, "resource")).ToHashSet();
        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();

        Assert.Equal(eraResources, raws); // 原料表=时代资源全集（铜/铁/粮/马+木材/石料传承刷新）
        foreach (var recipe in LoadItems("recipes.json"))
        {
            foreach (var input in ArrayProperty(recipe, "inputs").EnumerateArray())
            {
                var resource = StringProperty(input, "resource");
                Assert.Contains(resource, raws.Concat(outputs)); // 供应链输入可解析
            }
        }

        // 传承刷新资源确有本代用途（木材/石料入篮——§4 跨代资源行刷新语义落位）
        Assert.Contains("era1:res-wood", raws);
        var usedRaws = LoadItems("recipes.json")
            .SelectMany(r => ArrayProperty(r, "inputs").EnumerateArray()
                .Select(i => StringProperty(i, "resource"))).ToHashSet();
        Assert.Contains("era1:res-wood", usedRaws);
        Assert.Contains("era1:res-stone", usedRaws);
    }

    // ---- 4) 前三关排期：§4 L67 + §11.8 + §7 公式 [F-012] [F-092] -------------

    public static TheoryData<int, string, string, string, int, double, int> LevelScheduleRows => new()
    {
        // 出处：v3.2 §4 L67 排期（关 1 新能源/关 2 新物流/关 3 考古+英雄+系统新能力）
        // +§11.8 三级（对话级→建造级→巡逻/托管级）+§7 公式（邻接敌营→威胁度→胜波）
        { 1, "campaign:level-e2-01", "new_energy", "dialogue", 0, 0.3, 13 },
        { 2, "campaign:level-e2-02", "new_logistics", "build", 1, 0.6, 16 },
        { 3, "campaign:level-e2-03", "archaeology_hero_system", "patrol", 2, 0.9, 19 },
    };

    [Theory]
    [MemberData(nameof(LevelScheduleRows))]
    public void Levels_match_schedule_tier_and_formula_anchors(
        int slot, string id, string focus, string tier, int camps, double threat, int winWaves)
    {
        // [F-012] 前三关排期结构与 §7 公式逐值比对
        var levels = Levels();
        Assert.Equal(3, levels.Count); // 恰前三关（T-079 范围；后续关随 T-085 手工关集）
        var level = Level(slot);

        Assert.Equal(id, StringProperty(level, "id"));
        Assert.Equal(focus, StringProperty(level, "focus"));
        Assert.Equal(tier, StringProperty(level, "schedule_tier"));
        Assert.Equal("era2:era-classical", StringProperty(level, "era"));

        // §7：威胁度 = clamp(0.3+0.3×邻接敌营, 0.3, 1.2)；胜波 = 10+⌊威胁度×10⌋
        Assert.Equal(camps, IntProperty(level, "adjacent_enemy_camps"));
        Assert.Equal(threat, NumberProperty(level, "threat"), 3);
        Assert.Equal(Math.Clamp(0.3 + 0.3 * camps, 0.3, 1.2), NumberProperty(level, "threat"), 3);
        Assert.Equal(winWaves, IntProperty(level, "win_waves"));
        Assert.Equal(10 + (int)Math.Floor(threat * 10), IntProperty(level, "win_waves"));
    }

    [Fact]
    public void Level_ids_and_focus_align_with_frozen_legacy_transition_schedule()
    {
        // [F-012] 排期第四源互证：legacy/era_transition.json first_three_schedule（T-074 冻结）
        // ——slot/level_id/focus 三列逐值对齐，任何一侧漂移即红。
        var legacyDir = FindRepoContentDir(Path.Combine("legacy"));
        Assert.NotNull(legacyDir);
        var schedule = ArrayProperty(LoadDocFrom(legacyDir!.FullName, "era_transition.json").RootElement,
            "first_three_schedule").EnumerateArray().ToList();
        Assert.Equal(3, schedule.Count);

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
    public void Level_unlock_features_match_the_schedule_focus_of_each_slot()
    {
        // [F-092] 关 1 新能源/关 2 新物流/关 3 考古+英雄：解锁特征逐关对位
        // L1：水力系统（§10.4 觉醒演出+§6 教学关首波宽限 ×3）
        var l1 = Level(1);
        var f1 = ArrayProperty(ObjectProperty(l1, "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("waterpower_energy", f1);
        Assert.True(l1.GetProperty("teaching_level").GetBoolean());
        Assert.True(l1.GetProperty("awakening_cinematic").GetBoolean()); // §10.4 E2 第一关觉醒演出
        Assert.Equal(3.0, NumberProperty(l1, "first_wave_grace_multiplier")); // 教学关 ×3

        // L2：马车+水渠（§4 L59 物流列）
        var f2 = ArrayProperty(ObjectProperty(Level(2), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("carriage_logistics", f2);
        Assert.Contains("aqueduct", f2);

        // L3：考古+英雄（§4 L67+SP-6 每代第 3 关解锁招募）
        var f3 = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "features").EnumerateArray()
            .Select(f => f.GetString()!).ToList();
        Assert.Contains("archaeology", f3);
        Assert.Contains("hero_recruit", f3);

        // 非教学关首波宽限在 §6 带 ×1.5~2 内
        Assert.InRange(NumberProperty(Level(2), "first_wave_grace_multiplier"), 1.5, 2.0);
        Assert.InRange(NumberProperty(Level(3), "first_wave_grace_multiplier"), 1.5, 2.0);

        // 前三关难度递进（威胁 0.3<0.6<0.9 → 胜波 13<16<19）
        Assert.True(NumberProperty(Level(1), "threat") < NumberProperty(Level(2), "threat"));
        Assert.True(NumberProperty(Level(2), "threat") < NumberProperty(Level(3), "threat"));
        Assert.True(IntProperty(Level(1), "win_waves") < IntProperty(Level(2), "win_waves"));
        Assert.True(IntProperty(Level(2), "win_waves") < IntProperty(Level(3), "win_waves"));
    }

    // ---- 5) 三源一致性矩阵：ActionCatalog ↔ levels ↔ techtree [F-092] --------

    [Fact]
    public void ActionCatalog_E2_open_set_equals_level_unlock_schedule_exactly()
    {
        // [F-092] 三源互证①：G.2 解锁时代列（代码行源）=E2 全体动词恰等于
        // levels.json 三关 unlocks.actions 的并集——双向差集为空，任何漂移即红。
        var scheduled = ScheduledActions().OrderBy(v => v, StringComparer.Ordinal).ToList();
        var catalog = CatalogE2Verbs();

        Assert.Equal(13, catalog.Count); // G.2 E2 列恰 13 动作（19-E1 六动作）
        Assert.Empty(MatrixDrift(catalog, scheduled));
    }

    [Theory]
    [MemberData(nameof(ScheduledActionRows))]
    public void Every_scheduled_action_resolves_in_catalog_unlocked_no_later_than_E2(
        string verb, int slot)
    {
        // [F-092] 三源互证②：排期表每个动作可解析且解锁时代 ≤E2（E3+ 动作
        // 出现在 E2 排期=漂移；E1 已解锁动作重复排期=冗余漂移）。
        Assert.True(ActionCatalog.TryResolveName(verb, out var kind), $"排期出现未登记动作 {verb}");
        var descriptor = ActionCatalog.Descriptor(kind);
        Assert.Equal(EraOrdinals.E2, descriptor.UnlockEra); // 恰为 E2 解锁档
        Assert.Equal(slot, Levels().Single(l => ArrayProperty(ObjectProperty(l, "unlocks"), "actions")
            .EnumerateArray().Any(a => a.GetString() == verb)).GetProperty("slot").GetInt32());
    }

    public static TheoryData<string, int> ScheduledActionRows
    {
        get
        {
            // 期望排期（§11.8 三级 + G.2 锚点）：对话级=管理组两动作、建造级=建造+
            // 物流+战斗基础组、巡逻/托管级=SpawnScout+HeroOrder。
            var rows = new TheoryData<string, int>
            {
                { "TodoBoard", 1 },
                { "StopTask", 1 },
                { "BuildPlan", 2 },
                { "UpgradeTower", 2 },
                { "SellTower", 2 },
                { "SetRally", 2 },
                { "RerouteLogistics", 2 },
                { "SetStockpile", 2 },
                { "FocusFire", 2 },
                { "SetTowerMode", 2 },
                { "TriggerScroll", 2 },
                { "SpawnScout", 3 },
                { "HeroOrder", 3 },
            };
            return rows;
        }
    }

    [Fact]
    public void SpawnScout_and_HeroOrder_unlock_at_patrol_slot_exactly()
    {
        // [F-092] G.2 逐字锚点：SpawnScout=E2 巡逻级解锁（E1 返回
        // ERR_CAPABILITY_LOCKED）；HeroOrder=每代第 3 关。巡逻级=§11.8 三级
        // 排期末档=slot 3；两者不得出现在 slot 1/2。
        var slot3 = ArrayProperty(ObjectProperty(Level(3), "unlocks"), "actions").EnumerateArray()
            .Select(a => a.GetString()!).ToList();
        Assert.Equal(new[] { "HeroOrder", "SpawnScout" },
            slot3.OrderBy(v => v, StringComparer.Ordinal)); // 集合恰等于二动作（排序后比对，免 JSON 序敏感）

        foreach (var slot in new[] { 1, 2 })
        {
            var actions = ArrayProperty(ObjectProperty(Level(slot), "unlocks"), "actions").EnumerateArray()
                .Select(a => a.GetString()!);
            Assert.DoesNotContain("SpawnScout", actions);
            Assert.DoesNotContain("HeroOrder", actions);
        }
    }

    [Fact]
    public void Techtree_E2_nodes_cross_reference_levels_towers_and_scrolls()
    {
        // [F-092] 三源互证③：campaign/techtree.json E2 节点的 bound_levels/unlocks
        // 全部解析到实存内容行（关卡→era2/levels.json；塔→era2/towers.json；卷轴→scrolls/era2.json）。
        var campaignDir = FindRepoContentDir("campaign");
        Assert.NotNull(campaignDir);
        var techItems = LoadDocFrom(campaignDir!.FullName, "techtree.json").RootElement
            .GetProperty("items").EnumerateArray().Where(t => t.GetProperty("era_index").GetInt32() == 2)
            .ToList();
        Assert.True(techItems.Count >= 8); // T-073 冻结的 E2 节点底数（塔 5+卷轴 2+主线物流 1+旁支 2）

        var levelIds = Levels().Select(l => StringProperty(l, "id")).ToHashSet();
        var towerIds = LoadItems("towers.json").Select(t => StringProperty(t, "id")).ToHashSet();
        var scrollsDir = FindRepoContentDir("scrolls");
        Assert.NotNull(scrollsDir);
        var scrollIds = LoadDocFrom(scrollsDir!.FullName, "era2.json").RootElement
            .GetProperty("items").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()!).ToHashSet();

        foreach (var node in techItems)
        {
            var nodeId = StringProperty(node, "id");
            foreach (var bound in ArrayProperty(node, "bound_levels").EnumerateArray())
            {
                Assert.Contains(bound.GetString()!, levelIds); // 关卡闸→levels 实存行
            }

            foreach (var unlocked in ArrayProperty(node, "unlocks").EnumerateArray())
            {
                var target = unlocked.GetString()!;
                if (StringProperty(node, "kind") == "tower_unlock")
                {
                    Assert.Contains(target, towerIds); // 塔解锁→towers 实存行
                }
                else if (StringProperty(node, "kind") == "scroll_unlock")
                {
                    Assert.Contains(target, scrollIds); // 卷轴解锁→scrolls 实存行
                }
                else
                {
                    Assert.StartsWith("campaign:", target); // 能力 id 归战役命名空间
                }
            }

            // 拓扑前置在本表内可解析（三重闸门 prerequisites）
            foreach (var prereq in ArrayProperty(node, "prerequisites").EnumerateArray())
            {
                Assert.Contains(prereq.GetString()!,
                    LoadDocFrom(campaignDir.FullName, "techtree.json").RootElement
                        .GetProperty("items").EnumerateArray()
                        .Select(t => t.GetProperty("id").GetString()!));
            }
        }
    }

    [Fact]
    public void Techtree_spawn_scout_capability_joins_the_patrol_slot_schedule()
    {
        // [F-092] 三源互证④（能力↔动作合流）：techtree 旁支恰有一个节点产出
        // campaign:capability-spawn-scout（G.2 SpawnScout=E2 巡逻级解锁的科技侧
        // 落点），且 levels 排期确将 SpawnScout 排入巡逻级 slot 3——研究闸
        // （bound_levels）与运行时解锁位（排期）双源并存、动作名双向可join。
        var campaignDir = FindRepoContentDir("campaign");
        Assert.NotNull(campaignDir);
        var techItems = LoadDocFrom(campaignDir!.FullName, "techtree.json").RootElement
            .GetProperty("items").EnumerateArray().ToList();

        var capabilityNodes = techItems.Where(t =>
                ArrayProperty(t, "unlocks").EnumerateArray()
                    .Any(u => u.GetString() == "campaign:capability-spawn-scout"))
            .ToList();
        var node = Assert.Single(capabilityNodes); // 恰一节点（一键一义）
        Assert.Equal("system", StringProperty(node, "branch")); // F-052 系统能力独立旁支
        Assert.Equal("campaign:tech-sys-e2-patrol", StringProperty(node, "id"));

        // 研究闸：占领 E2 关卡后可研究（闸位须为本时代实存关）
        var levelIds = Levels().Select(l => StringProperty(l, "id")).ToHashSet();
        foreach (var bound in ArrayProperty(node, "bound_levels").EnumerateArray())
        {
            Assert.Contains(bound.GetString()!, levelIds);
        }

        // 运行时解锁位：SpawnScout 排入巡逻级（slot 3，G.2『E2 巡逻级』）
        Assert.Contains("SpawnScout", ScheduledActions());
        Assert.Contains("SpawnScout",
            ArrayProperty(ObjectProperty(Level(3), "unlocks"), "actions").EnumerateArray()
                .Select(a => a.GetString()!));
    }

    [Fact]
    public void Scrolls_are_E2_unlocked_and_one_is_era_gifted_before_trigger_unlock()
    {
        // [F-092][F-038] 卷轴侧互证：SP-5 七张全部 unlock_era=2（E2 解锁）；
        // 跃迁赠送卡（SC-005 烽火）使 TriggerScroll 自 E2 起有可触发对象
        //（排期 slot 2 解锁 TriggerScroll ↔ 赠送卡随跃迁即得）。
        var scrollsDir = FindRepoContentDir("scrolls");
        Assert.NotNull(scrollsDir);
        var scrolls = LoadDocFrom(scrollsDir!.FullName, "era2.json").RootElement
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(7, scrolls.Count); // SP-5 E2 首发恰 7 张

        foreach (var scroll in scrolls)
        {
            Assert.Equal(2, IntProperty(scroll, "unlock_era"));
        }

        Assert.Contains(scrolls, s => StringProperty(ObjectProperty(s, "acquisition"), "kind") == "era_gift");
        Assert.Contains("TriggerScroll", ScheduledActions()); // TriggerScroll 在 E2 排期内
    }

    [Fact]
    public void Consistency_comparator_flags_drift_on_a_mutated_matrix()
    {
        // illegal — [F-092] 负向用例：矩阵比较器对突变集必须点名漂移
        //（防"比较器恒空"假绿——喂入伪造的 catalog_only/schedule_only 两侧漂移）。
        var catalog = CatalogE2Verbs();

        // 真数据：零漂移
        Assert.Empty(MatrixDrift(catalog, ScheduledActions()));

        // 突变①：排期侧丢失 StopTask → catalog_only:StopTask
        var dropped = ScheduledActions().Where(v => v != "StopTask").ToList();
        var drift1 = MatrixDrift(catalog, dropped);
        Assert.Contains("catalog_only:StopTask", drift1);

        // 突变②：排期侧混入未登记动作 → schedule_only:FocusFire 已删+Bogus 已加
        var mutated = ScheduledActions().Where(v => v != "FocusFire").Append("Bogus").ToList();
        var drift2 = MatrixDrift(catalog, mutated);
        Assert.Contains("catalog_only:FocusFire", drift2);
        Assert.Contains("schedule_only:Bogus", drift2);
    }

    [Fact]
    public void Ammo_coverage_helper_flags_a_missing_recipe_on_a_mutated_output_set()
    {
        // illegal — [F-023] 负向用例：弹药覆盖闭合对缺失配方必须点名（防恒绿）。
        var towers = LoadItems("towers.json");
        var outputs = LoadItems("recipes.json")
            .Select(r => StringProperty(ObjectProperty(r, "output"), "resource")).ToHashSet();

        // 真数据：每塔弹药恰有配方
        var missing = towers.Where(t => !outputs.Contains(StringProperty(t, "ammo_item")))
            .Select(t => StringProperty(t, "ammo_item")).ToList();
        Assert.Empty(missing);

        // 突变：抽走铁矢配方 → 覆盖检查点名连弩塔弹药缺失
        var mutated = outputs.Where(o => o != "era2:ammo-iron-bolt").ToHashSet();
        var missingAfter = towers.Where(t => !mutated.Contains(StringProperty(t, "ammo_item")))
            .Select(t => StringProperty(t, "ammo_item")).ToList();
        Assert.Equal(new[] { "era2:ammo-iron-bolt" }, missingAfter);
    }

    // ---- 6) 注册表纪律：id 唯一性/命名空间/_new 溯源 [F-156] ------------------

    [Fact]
    public void Era2_item_ids_are_unique_and_follow_namespace_conventions()
    {
        // [F-156] 注册表 id 唯一性与命名空间（K-2 modid:contentid；关卡行归
        // campaign 命名空间=techtree/legacy 同源引用，其余归 era2 域）
        var all = new List<(string File, string Id)>();
        foreach (var file in new[]
                 { "era.json", "recipes.json", "levels.json", "towers.json", "enemies.json", "heroes.json" })
        {
            foreach (var item in LoadItems(file))
            {
                var id = StringProperty(item, "id");
                Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*:[a-z][a-z0-9]*(-[a-z0-9]+)*$", id);
                all.Add((file, id));
            }
        }

        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count()); // era2 全域 id 唯一
        Assert.All(all.Where(x => x.File == "era.json"), x => Assert.StartsWith("era2:era-", x.Id));
        Assert.All(all.Where(x => x.File == "recipes.json"), x => Assert.StartsWith("era2:recipe-", x.Id));
        Assert.All(all.Where(x => x.File == "levels.json"), x => Assert.StartsWith("campaign:level-e2-", x.Id));
        Assert.All(all.Where(x => x.File == "towers.json"), x => Assert.StartsWith("era2:tower-", x.Id));
        Assert.All(all.Where(x => x.File == "enemies.json"), x => Assert.StartsWith("era2:enemy-", x.Id));
    }

    [Fact]
    public void New_flag_policy_marks_invented_values_with_sources()
    {
        // [F-156] _new/_source 溯源纪律（11-spec-tables L7：新发明数值一律标注）
        foreach (var file in new[] { "era.json", "recipes.json", "levels.json" })
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

        // E2 时代行：能源逐级绝对值为 _new（v3.2 仅钉 ×3~5 带）；Boss HP/甲为
        // §4 逐字锚点 → 不得列入 _new_fields（与 era1 同纪律）
        var era = LoadItems("era.json").Single();
        Assert.True(era.GetProperty("_new").GetBoolean());
        Assert.Contains("energy.levels[0].output",
            era.GetProperty("_new_fields").EnumerateArray().Select(f => f.GetString()));

        var bossNewFields = ArrayProperty(FindById(LoadItems("enemies.json"), "era2:enemy-boss"), "_new_fields");
        Assert.DoesNotContain("hp", bossNewFields.EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("armor", bossNewFields.EnumerateArray().Select(f => f.GetString()));
    }
}
