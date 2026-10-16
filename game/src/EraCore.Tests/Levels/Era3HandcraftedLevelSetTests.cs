using System.Text.Json;
using EraCore.Balance;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Tests.Sim;
using EraCore.World;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055][F-025][F-092][F-012] T-116 E3 手工关卡集（levels/e3.json 首批
// 4 关=冻结排期前三关+后续关首张）数据回归：schema 装载即校验（含 era3 岩比
// 放宽=煤位分布声明）、前三关与 era3/levels.json（T-112 冻结排期）逐字段一致、
// 能力解锁链对位、三重目标实例化（SP-8 池+E3 敌特性适配）、资源 map 块落位
// （富煤矿只在 L3-2——T-115 §四 G2 守卫翻转的内容面）、装载确定性。期望值全部
// 锚在内容表/冻结公式（出处写在各断言处注释），解析或校验失败即红。
public class Era3HandcraftedLevelSetTests
{
    private static HandcraftedLevelSet E3() => HandcraftedLevelData.Load("e3.json");

    private static HandcraftedLevel Level(int slot) =>
        E3().Items.Single(level => level.Slot == slot);

    // ---- 1) schema 装载与结构声明 [F-055] --------------------------------

    [Fact]
    public void E3_file_parses_and_declares_the_Level_family_schema()
    {
        // [F-055] levels/e3.json 解析+K-2 Level 族声明+首批 4 关+终局关（前三关
        // =L3-1/2/3 排期关+L3-4 后续关首张，T-116 任务卡『3~4 关』；L3-5=
        // e3-final 堡垒号 Boss 关，T-124 跃迁前置补全）。
        HandcraftedLevelSet set = E3();
        Assert.Equal("era3:era-industrial", set.Era);
        Assert.Equal(5, set.Items.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, set.Items.Select(level => level.Slot).OrderBy(slot => slot).ToArray());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "game", "content", "levels")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        string raw = File.ReadAllText(Path.Combine(dir.FullName, "game", "content", "levels", "e3.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Matches("^urn:eratd:content:levels:1$", doc.RootElement.GetProperty("$schema").GetString()!);
        Assert.Equal("Level", doc.RootElement.GetProperty("content_type").GetString());
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Level", item.GetProperty("content_type").GetString());
        }

        // 关卡行归 campaign 命名空间（techtree/legacy 同源引用，F-156）
        Assert.All(set.Items, level => Assert.StartsWith("campaign:level-e3-", level.Id));
        Assert.Equal(set.Items.Count, set.Items.Select(level => level.Id).Distinct().Count());
    }

    [Fact]
    public void Threats_win_waves_and_graces_follow_the_frozen_formulas()
    {
        // [F-055] 胜波=10+⌊t×10⌋（§7）逐关复核：0.3/0.6/0.9/0.75/0.9→13/16/19/17/19
        //（终局关=SP-9 邻接 2 网格点 0.9，T-124 补）；预算 15~40 分钟带；教学位
        // 仅 L3-1（宽限 ×3），其余 ×1.5~2（§6）。
        foreach (HandcraftedLevel level in E3().Items)
        {
            Assert.Equal(Campaign.WinWaves(level.Threat), level.WinWaves);
            Assert.InRange(level.Threat, 0.3, 1.2);
            Assert.InRange(level.BudgetSeconds, 900, 2400);
        }

        Assert.Equal(new[] { 0.3, 0.6, 0.9, 0.75, 0.9 }, E3().Items.OrderBy(level => level.Slot).Select(level => level.Threat));
        Assert.Equal(new[] { 13, 16, 19, 17, 19 }, E3().Items.OrderBy(level => level.Slot).Select(level => level.WinWaves));
        Assert.True(Level(1).TeachingLevel);
        Assert.All(E3().Items.Skip(1), level => Assert.False(level.TeachingLevel));
        Assert.All(E3().Items.Skip(1), level => Assert.InRange(level.FirstWaveGraceMultiplier, 1.5, 2.0));

        // 后续关威胁带（T-116 任务卡『1~2 张后续关 威胁 0.75~0.9』）
        Assert.InRange(Level(4).Threat, 0.75, 0.9);

        // 前三关威胁递进（0.3<0.6<0.9 → 胜波 13<16<19，§7）
        Assert.True(Level(1).Threat < Level(2).Threat && Level(2).Threat < Level(3).Threat);
        Assert.True(Level(1).WinWaves < Level(2).WinWaves && Level(2).WinWaves < Level(3).WinWaves);
    }

    // ---- 2) 资源 map 块落位（富煤矿只在 L3-2） [F-025] --------------------

    [Fact]
    public void Coal_deposits_are_declared_only_on_the_scarcity_flagged_rail_level()
    {
        // [F-025] T-115 §四 G2 守卫翻转的内容面：矿格（rock 位=煤位代理）注入
        // 唯一通道=逐关 map 比率显式声明——L3-2 矿轨关=富煤矿（rock_ratio=0.10，
        // 与 T-115 §四表 2/表 4 跨代大陆同值）、其余关 rock_ratio=0（煤稀：零声明
        // =零矿格，T-115 §四表 2b 零矿稳健性）。全 E3 关显式声明三比率（显式
        // 声明纪律，e1/e2 先例）。水比恒 0（装载器规则）。
        foreach (HandcraftedLevel level in E3().Items)
        {
            Assert.Equal(0.0, level.Map.WaterRatio, 9);
            Assert.Equal(0.05, level.Map.ForestRatio, 9); // E2 传承刷新档 0.10×0.5 沿用
            Assert.InRange(level.Map.RockRatio, 0.0, 1.0);
        }

        HandcraftedLevel rail = Level(2);
        Assert.Equal(0.10, rail.Map.RockRatio, 9); // 富煤矿 map 参数
        Assert.All(E3().Items.Where(level => level.Slot != 2), level => Assert.Equal(0.0, level.Map.RockRatio, 9));
    }

    [Fact]
    public void Rich_coal_seed_builds_a_real_deposit_and_keeps_every_authored_cell_buildable()
    {
        // [F-025] L3-2 固定 seed 的真引擎面：SimRng(seed) 首抽即 GridWorld 地形
        //（Simulation 构造序 #1：Reseed→terrain），岩格=煤位代理——①矿格覆盖率
        // ∈ 0.10±4σ 二项带（σ=√(p(1−p)/格数)，T-115 §四表 2 同式）；②全部命令流
        // 建造格+仓库位+核心位+出生点落非岩格（命令流坐标可建性——era3 岩比
        // 放宽的安全前提）；③出生点→核心通路连通（岩格不封路——流场可达）。
        HandcraftedLevel rail = Level(2);
        var world = new GridWorld(
            rail.Map.Width, rail.Map.Height, new SimRng(rail.Map.Seed),
            rail.Map.ForestRatio, rail.Map.RockRatio, rail.Map.WaterRatio);

        long rockCells = 0;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                if (world.TerrainAt(new CellCoord(x, y)) == TerrainKind.Rock)
                {
                    rockCells++;
                }
            }
        }

        double fraction = rockCells / (double)(world.Width * world.Height);
        double sigma = Math.Sqrt(0.10 * 0.90 / (world.Width * world.Height));
        Assert.InRange(fraction, 0.10 - 4 * sigma, 0.10 + 4 * sigma);
        Assert.True(rockCells > 0, "富煤矿关必须真的有矿格（防 0 格假绿）");

        var critical = new List<CellPoint> { rail.Map.BaseCell, rail.Map.WarehouseCell };
        critical.AddRange(rail.Map.EntryCells);
        critical.AddRange(rail.CommandScript.Where(step => step.Cell is not null).Select(step => step.Cell!));
        foreach (CellPoint cell in critical)
        {
            Assert.True(
                world.TerrainAt(new CellCoord(cell.X, cell.Y)) is TerrainKind.Plain or TerrainKind.Forest,
                $"L3-2 关键格 ({cell.X},{cell.Y}) 落岩格——命令流/核心/出生点不可建");
        }

        // 连通性：核心位起洪水填充（岩格=硬阻挡），全部出生点可达
        var seen = new HashSet<CellCoord> { new(rail.Map.BaseCell.X, rail.Map.BaseCell.Y) };
        var queue = new Queue<CellCoord>(seen);
        while (queue.Count > 0)
        {
            CellCoord cell = queue.Dequeue();
            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var next = new CellCoord(cell.X + dx, cell.Y + dy);
                if (next.X < 0 || next.X >= world.Width || next.Y < 0 || next.Y >= world.Height
                    || seen.Contains(next) || !world.IsPassable(next))
                {
                    continue;
                }

                seen.Add(next);
                queue.Enqueue(next);
            }
        }

        Assert.All(rail.Map.EntryCells, entry => Assert.Contains(new CellCoord(entry.X, entry.Y), seen));
    }

    // ---- 3) 命令流与内容表引用闭合 [F-055] [F-023] ------------------------

    [Fact]
    public void Command_script_and_rosters_resolve_against_the_era3_content_tables()
    {
        // [F-055] 引用闭合：塔→era3/towers.json、敌种（含精英线）→era3/enemies.json、
        // converter 配方→era3/recipes.json、初始资源→era.json 资源行（钢=建造
        // 货币行，E1 木/E2 铜/E3 钢跨代递进）；运输档位=封闭档位表（ porter/
        // cart/mine-rail/drone）且 mine-rail 只出现在矿轨关（L3-2 主题锚）。
        var towers = Towers("era3");
        var enemies = Enemies("era3");

        foreach (HandcraftedLevel level in E3().Items)
        {
            foreach (ScriptStep step in level.CommandScript)
            {
                if (step.Tower is not null)
                {
                    Assert.Contains(step.Tower, towers.Keys);
                }

                if (step.ConverterRecipe is not null)
                {
                    Assert.True(RecipeExists("era3", step.ConverterRecipe),
                        $"{level.Id}: 配方 {step.ConverterRecipe} 未见于 era3/recipes.json");
                }

                if (step.CarrierTier is not null)
                {
                    Assert.Contains(step.CarrierTier, HandcraftedLevelData.CarrierTiers);
                    if (step.CarrierTier == "mine-rail")
                    {
                        Assert.Equal(2, level.Slot); // 矿轨档位=矿轨关专属（10 件/s 首发位）
                    }
                }
            }

            foreach (string enemyId in level.Waves.RegularRoster)
            {
                Assert.Contains(enemyId, enemies.Keys);
            }

            if (level.Waves.EliteEnemyId is not null)
            {
                Assert.Contains(level.Waves.EliteEnemyId, enemies.Keys);
            }

            foreach (StockLine line in level.InitialStock)
            {
                Assert.Contains(line.Resource, EraResources("era3"));
            }
        }

        // 传承刷新资源以采集原料身份入命令流（『弹链本代化』旧资源=作料，
        // T-115 §二裁定）：铁/铜采集器存在、era2:ammo-* 不见于任何 e3 引用
        string raw = File.ReadAllText(Path.Combine(FindLevelsDir(), "e3.json"));
        Assert.Contains("\"era2:res-iron\"", raw);
        Assert.Contains("\"era2:res-copper\"", raw);
        Assert.DoesNotContain("era2:ammo-", raw);
    }

    private static string FindLevelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "levels");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/levels（测试须在仓库内运行）");
    }

    /// <summary>The aliases of one authored e3.json row (raw-JSON face; the parsed record carries no aliases).</summary>
    private static IReadOnlyList<string> AuthoredAliases(string levelId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindLevelsDir(), "e3.json")));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == levelId)
            .GetProperty("aliases").EnumerateArray()
            .Select(alias => alias.GetString()!)
            .ToList();
    }

    [Fact]
    public void First_wave_has_a_damage_tower_and_stock_covers_the_script_tower_costs()
    {
        // [F-055] 教学不变式：wave 1 已有伤害塔（三级炼链供弹的首防）；初始
        // 授予 ≥ 脚本塔价合计（E3 无升级步——臼炮为新建非升级）；授予 ≤ 仓库
        // 池上限（F.3 基 2000×era3 仓库等级 3=6000）。
        foreach (HandcraftedLevel level in E3().Items)
        {
            var towers = Towers("era3");
            Assert.Contains(level.CommandScript, step => step.Verb == "build-tower"
                && towers[step.Tower!].Dps > 0.0
                && step.Wave == 1);
            Assert.DoesNotContain(level.CommandScript, step => step.Verb == "upgrade-tower");
            Assert.DoesNotContain(level.CommandScript, step => step.Verb == "hero-order"); // E3 英雄行=占位（era3 无 heroes.json）

            double cost = level.CommandScript
                .Where(step => step.Verb == "build-tower")
                .Sum(step => towers[step.Tower!].CostTotal);
            double grant = level.InitialStock.Sum(line => line.Amount);
            Assert.True(grant >= cost, $"{level.Id}: 授予 {grant} < 脚本塔价 {cost}");
            Assert.True(grant <= 2000.0 * WarehouseLevel("era3"), $"{level.Id}: 授予 {grant} 超过仓库池上限");
        }
    }

    // ---- 4) 三重目标实例化（SP-8 池 + E3 敌特性适配） [F-055] -------------

    [Fact]
    public void Every_level_instantiates_survival_plus_distinct_strategy_and_challenge_objectives()
    {
        // [F-055] 每关 ≥1 策略+1 挑战目标实例（生存=结构性），实例两两不同；
        // ★ 难位旗标对齐池行（c2/c3 hard）。E3 敌特性适配（任务卡）：护盾关
        // （L3-4=铁甲卫兵精英线）的挑战目标=c2 仅穿刺+支援类塔（穿甲击杀类
        // ——T-114 裁定穿甲打断盾再生 ⇒ 穿刺线=盾对策的关卡物化）。
        (IReadOnlyList<string> strategyPool, IReadOnlyList<string> challengePool) = ObjectivePools();
        var challengeHard = ChallengeHardFlags();

        var seenInstances = new HashSet<string>();
        foreach (HandcraftedLevel level in E3().Items)
        {
            Assert.NotEmpty(level.Objectives.Strategy);
            Assert.NotEmpty(level.Objectives.Challenge);
            Assert.All(level.Objectives.Strategy, objective => Assert.Contains(objective, strategyPool));
            Assert.All(level.Objectives.Challenge, objective => Assert.Contains(objective, challengePool));

            foreach (string challengeId in level.Objectives.Challenge)
            {
                bool expectHard = challengeId is "campaign:obj-c1" or "campaign:obj-c2"
                    or "campaign:obj-c3" or "campaign:obj-c4";
                Assert.True(challengeHard[challengeId] == expectHard,
                    $"{level.Id}: {challengeId} 的 hard 旗标与 SP-8b 池不符");
            }

            string instance = string.Join('|', level.Objectives.Strategy.Concat(level.Objectives.Challenge).Order());
            Assert.True(seenInstances.Add(instance), $"{level.Id}: 目标实例与他关重复");
        }

        // E3 敌特性适配锚：护盾关（精英线=era3:enemy-ironclad-guard 个体盾行）
        // 携带 c2；护盾敌只出现在带盾对策（c2 全穿刺线）或臼炮反重甲线的关
        HandcraftedLevel shield = Level(4);
        Assert.Equal("era3:enemy-ironclad-guard", shield.Waves.EliteEnemyId);
        Assert.Contains("campaign:obj-c2", shield.Objectives.Challenge);
    }

    [Fact]
    public void Limit_challenge_objectives_are_satisfiable_by_the_level_shape()
    {
        // [F-055] 限类挑战（c2 仅穿刺+支援）与脚本塔类一致；限塔挑战（c3 ≤8）
        // 与脚本塔数一致——目标不是空头支票。
        var towers = Towers("era3");
        foreach (HandcraftedLevel level in E3().Items)
        {
            if (level.Objectives.Challenge.Contains("campaign:obj-c2"))
            {
                foreach (ScriptStep step in level.CommandScript.Where(step => step.Verb == "build-tower"))
                {
                    string towerClass = towers[step.Tower!].Class.ToString().ToLowerInvariant();
                    Assert.Contains(towerClass, new[] { "pierce", "support" });
                }
            }

            if (level.Objectives.Challenge.Contains("campaign:obj-c3"))
            {
                int towerCount = level.CommandScript.Count(step => step.Verb == "build-tower");
                Assert.True(towerCount <= 8, $"{level.Id}: 脚本塔数 {towerCount} > c3 限 8");
            }
        }

        // L3-4 全穿甲线 vs 重甲精英（甲 12）：加特林单发 16 对甲 12 吃 75%——
        // 弹链经济最贵对位，威胁取带下沿 0.75（供弹平衡约束，e3.json _source 同锚）
        var enemies = Enemies("era3");
        Assert.Equal(12, enemies["era3:enemy-ironclad-guard"].Armor);
        Assert.Equal(240, enemies["era3:enemy-ironclad-guard"].ShieldPoints);
        Assert.True(Level(4).Threat <= Level(3).Threat);
    }

    // ---- 5) 前三关排期一致性（T-112 冻结元数据） [F-092] [F-012] ----------

    [Fact]
    public void First_three_rows_match_the_frozen_era3_levels_schedule_row_by_row()
    {
        // [F-092][F-012] 排期一致性断言：levels/e3.json 前三行与 era3/levels.json
        //（T-112 冻结排期侧）逐字段一致——id/slot/focus/schedule_tier/邻接/威胁/
        // 胜波/宽限/教学/觉醒/关名/别名/解锁双列，任何一侧漂移即红；L3-4 不在
        // 冻结表（后续关首批——era3/levels.json 恰 3 行）。
        var schedule = Era3ScheduleRows();
        Assert.Equal(3, schedule.Count);
        foreach (HandcraftedLevel level in E3().Items.Where(level => level.Slot <= 3))
        {
            JsonElement frozen = schedule[level.Slot];
            Assert.Equal(frozen.GetProperty("id").GetString(), level.Id);
            Assert.Equal(frozen.GetProperty("slot").GetInt32(), level.Slot);
            Assert.Equal(frozen.GetProperty("focus").GetString(), level.Focus);
            Assert.Equal(frozen.GetProperty("schedule_tier").GetString(), level.ScheduleTier);
            Assert.Equal(frozen.GetProperty("adjacent_enemy_camps").GetInt32(), level.AdjacentEnemyCamps);
            Assert.Equal(frozen.GetProperty("threat").GetDouble(), level.Threat, 3);
            Assert.Equal(frozen.GetProperty("win_waves").GetInt32(), level.WinWaves);
            Assert.Equal(frozen.GetProperty("first_wave_grace_multiplier").GetDouble(), level.FirstWaveGraceMultiplier, 3);
            Assert.Equal(frozen.GetProperty("teaching_level").GetBoolean(), level.TeachingLevel);
            Assert.Equal(frozen.GetProperty("awakening_cinematic").GetBoolean(), level.AwakeningCinematic);
            Assert.Equal(frozen.GetProperty("name_zh").GetString(), level.NameZh);
            Assert.Equal(frozen.GetProperty("name_en").GetString(), level.NameEn);

            var frozenAliases = frozen.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()!).ToList();
            Assert.Equal(3, frozenAliases.Count);
            Assert.Equal(frozenAliases, AuthoredAliases(level.Id)); // 别名行逐字一致

            // SP-9：E3 行威胁度可由邻接敌营派生（手工行与程序口径合流）
            Assert.Equal(Threat.ClampThreat(level.AdjacentEnemyCamps!.Value), level.Threat, 9);

            var frozenActions = frozen.GetProperty("unlocks").GetProperty("actions").EnumerateArray()
                .Select(action => action.GetString()!).Order().ToList();
            var frozenFeatures = frozen.GetProperty("unlocks").GetProperty("features").EnumerateArray()
                .Select(feature => feature.GetString()!).Order().ToList();
            Assert.Equal(frozenActions, level.Unlocks.Actions.Order().ToList());
            Assert.Equal(frozenFeatures, level.Unlocks.Features.Order().ToList());
        }
    }

    [Fact]
    public void E3_capability_unlock_chain_matches_the_schedule_focus_of_each_slot()
    {
        // [F-092][F-012] 能力解锁链逐关对位冻结排期：L3-1 蒸汽+过热风险（教学
        // 位）、L3-2 矿轨物流、L3-3 考古+英雄招募+自主执行（托管级末档）；19
        // 动作清单已于 E2 全解锁 ⇒ E3 各关 unlocks.actions 恒空（era3/levels.json
        // 注）；L3-4 后续关首批无新增解锁位（e1 后续关先例）。
        Assert.Equal(new[] { "steam_energy", "overheat_risk" }.Order().ToList(), Level(1).Unlocks.Features.Order().ToList());
        Assert.Equal(new[] { "mine_rail_logistics" }, Level(2).Unlocks.Features);
        Assert.Equal(
            new[] { "archaeology", "hero_recruit", "autonomous_execution" }.Order().ToList(),
            Level(3).Unlocks.Features.Order().ToList());
        Assert.All(E3().Items, level => Assert.Empty(level.Unlocks.Actions));
        Assert.Empty(Level(4).Unlocks.Features);

        // 英雄行=章节设计待定案（era3 无 heroes.json 行；hero_id/hero_cell 成对缺席）
        Assert.All(E3().Items, level => Assert.Null(level.HeroId));
        Assert.All(E3().Items, level => Assert.Null(level.Map.HeroCell));

        // 非觉醒位不带钩子（era3/levels.json awakening_cinematic 恒 false——
        // §10.4 觉醒演出=E2 第一关专属锚）
        Assert.All(E3().Items, level => Assert.False(level.AwakeningCinematic));
        Assert.All(E3().Items, level => Assert.Empty(level.CinematicHooks));
    }

    // ---- 6) 装载确定性 + era3 装载器边界/非法（CS-3.1） -------------------

    [Fact]
    public void Double_load_of_e3_json_is_structurally_identical()
    {
        // [F-055] 确定性①：同文件双装载 → 序列化快照逐字节全等。
        HandcraftedLevelSet first = HandcraftedLevelData.Load("e3.json");
        HandcraftedLevelSet second = HandcraftedLevelData.Load("e3.json");
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    private static string MinimalEra3LevelJson() => """
        {
          "$schema": "urn:eratd:content:levels:1",
          "content_type": "Level",
          "era": "era3:era-industrial",
          "items": [
            {
              "id": "campaign:level-e3-99",
              "content_type": "Level",
              "era": "era3:era-industrial",
              "slot": 9,
              "name_zh": "最小合法关",
              "name_en": "Minimal Legal",
              "teaching_level": false,
              "awakening_cinematic": false,
              "threat": 0.3,
              "win_waves": 13,
              "first_wave_grace_multiplier": 1.5,
              "budget_seconds": 900,
              "map": {
                "seed": 99,
                "width": 6,
                "height": 6,
                "forest_ratio": 0.05,
                "rock_ratio": 0.0,
                "water_ratio": 0.0,
                "base_cell": { "x": 2, "y": 5 },
                "warehouse_cell": { "x": 0, "y": 0 },
                "entry_cells": [ { "x": 2, "y": 0 } ]
              },
              "initial_stock": [ { "resource": "era3:res-steel", "amount": 100.0 } ],
              "waves": {
                "base_count": 6,
                "begin_wave": 2,
                "spacing": 2,
                "scaling": 2.0,
                "batches_per_wave": 2,
                "valley_seconds": 15.0,
                "regular_roster": ["era3:enemy-conscript"],
                "elite_line_enabled": false
              },
              "command_script": [
                { "wave": 1, "verb": "build-labor-camp", "cell": { "x": 1, "y": 1 } },
                { "wave": 1, "verb": "build-converter", "converter_recipe": "era3:recipe-steel", "cell": { "x": 2, "y": 2 } },
                { "wave": 1, "verb": "build-tower", "tower": "era3:tower-gatling", "cell": { "x": 3, "y": 3 } },
                { "wave": 1, "verb": "assign-carrier", "from_entity": 2, "to_entity": 3, "carrier_tier": "porter" }
              ],
              "objectives": {
                "strategy": ["campaign:obj-s1"],
                "challenge": ["campaign:obj-c8"]
              },
              "unlocks": { "actions": [], "features": [] }
            }
          ]
        }
        """;

    [Fact]
    public void Loader_accepts_the_minimal_era3_level_and_the_coal_deposit_ratio()
    {
        // [F-055][F-025] legal — 最小合法 era3 关；boundary — rock_ratio=0.5
        //（era3 煤位分布声明带沿）与 rock_ratio=1.0 均可装载（E1/E2 仍拒绝）。
        HandcraftedLevelSet set = HandcraftedLevelData.Parse(MinimalEra3LevelJson());
        HandcraftedLevel level = Assert.Single(set.Items);
        Assert.Equal(0.0, level.Map.RockRatio, 9);

        string rich = MinimalEra3LevelJson().Replace("\"rock_ratio\": 0.0", "\"rock_ratio\": 0.5");
        Assert.Equal(0.5, Assert.Single(HandcraftedLevelData.Parse(rich).Items).Map.RockRatio, 9);

        string full = MinimalEra3LevelJson().Replace("\"rock_ratio\": 0.0", "\"rock_ratio\": 1.0");
        Assert.Equal(1.0, Assert.Single(HandcraftedLevelData.Parse(full).Items).Map.RockRatio, 9);

        string era1Rock = MinimalEra3LevelJson()
            .Replace("\"era\": \"era3:era-industrial\"", "\"era\": \"era1:era-ancient\"")
            .Replace("\"rock_ratio\": 0.0", "\"rock_ratio\": 0.1");
        Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(era1Rock));
    }

    public static TheoryData<string, string, string> IllegalEra3Mutations => new()
    {
        // illegal — E3 结构违规逐条点名拒绝（CS-3.5；每条规则一条故意违例）
        { "E3 转换器缺配方引用",
            "{ \"wave\": 1, \"verb\": \"build-converter\", \"converter_recipe\": \"era3:recipe-steel\", \"cell\": { \"x\": 2, \"y\": 2 } }",
            "{ \"wave\": 1, \"verb\": \"build-converter\", \"cell\": { \"x\": 2, \"y\": 2 } }" },
        { "未登记运输档位", "\"carrier_tier\": \"porter\"", "\"carrier_tier\": \"wagon\"" },
        { "水比非零（era3 同禁）", "\"water_ratio\": 0.0", "\"water_ratio\": 0.1" },
        { "威胁度越带", "\"threat\": 0.3", "\"threat\": 1.5" },
        { "胜波与公式不符", "\"win_waves\": 13", "\"win_waves\": 14" },
        { "教学宽限非 ×3", "\"teaching_level\": false", "\"teaching_level\": true" },
        { "策略目标缺失", "\"strategy\": [\"campaign:obj-s1\"]", "\"strategy\": []" },
        { "命令波锚越胜波窗", "\"wave\": 1, \"verb\": \"build-labor-camp\"", "\"wave\": 99, \"verb\": \"build-labor-camp\"" },
        { "煤位声明越 [0,1] 带", "\"rock_ratio\": 0.0", "\"rock_ratio\": 1.5" },
    };

    [Theory]
    [MemberData(nameof(IllegalEra3Mutations))]
    public void Loader_rejects_each_era3_structural_violation_by_name(string caseName, string from, string to)
    {
        // illegal — 每条结构规则一条"故意违反应被拦截"（防恒绿）。
        string json = MinimalEra3LevelJson().Replace(from, to);
        var exception = Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(json));
        Assert.NotEqual("", exception.Message.Trim());
        Assert.NotEqual(from, exception.Message.Trim()); // 违例 {caseName} 的消息非占位
        Assert.True(exception.Message.Contains("campaign:level-e3-99") || caseName.Length > 0,
            $"caseName={caseName}"); // 消息定位到关卡行（caseName 携带于失败面）
        Assert.Contains("campaign:level-e3-99", exception.Message);
    }
}
