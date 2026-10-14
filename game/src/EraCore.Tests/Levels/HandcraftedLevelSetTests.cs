using System.Text.Json;
using EraCore.Balance;
using EraCore.Combat;
using Xunit;

namespace EraCore.Tests.Levels;

// [C-19][F-055][F-092][F-156] T-085 手工关卡集首批（E1×4+E2×3）数据回归：
// game/content/levels/e1.json+e2.json 的 schema 装载即校验、七关数据完整性、
// 三重目标实例化（SP-8 池）、E2 行与 era2/levels.json（T-079 冻结排期）逐字段
// 一致、装载确定性。期望值全部锚在文档/冻结公式（出处写在各断言处注释），
// 解析或校验失败即红。
public class HandcraftedLevelSetTests
{
    private static HandcraftedLevelSet E1() => HandcraftedLevelData.Load("e1.json");

    private static HandcraftedLevelSet E2() => HandcraftedLevelData.Load("e2.json");

    private static IEnumerable<HandcraftedLevel> AllLevels() => E1().Items.Concat(E2().Items);

    private static HandcraftedLevel FindLevel(string id)
    {
        HandcraftedLevel? level = E1().TryGet(id) ?? E2().TryGet(id);
        Assert.NotNull(level);
        return level!;
    }

    private static string LevelsDir()
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

    /// <summary>One build event of the entity walk: wave, entity ordinal, tower row (towers only).</summary>
    private sealed record BuildEvent(int Wave, long Entity, string? TowerRow);

    /// <summary>Walks the script in wave order handing out entity ordinals (warehouse=1) and resolved tower rows.</summary>
    private static IEnumerable<BuildEvent> WalkBuilds(
        HandcraftedLevel level,
        IReadOnlyDictionary<string, TowerSpec> towers)
    {
        var rows = new Dictionary<long, string>();
        long entity = 2;
        foreach (ScriptStep step in level.CommandScript.OrderBy(s => s.Wave))
        {
            switch (step.Verb)
            {
                case "build-labor-camp" or "build-harvester" or "build-converter":
                    yield return new BuildEvent(step.Wave, entity++, null);
                    break;
                case "build-tower":
                    rows[entity] = step.Tower!;
                    yield return new BuildEvent(step.Wave, entity++, step.Tower);
                    break;
                case "upgrade-tower":
                    string row = rows[step.Entity!.Value];
                    for (int i = 1; i < step.ToTier!.Value; i++)
                    {
                        row = towers[row].UpgradesTo
                            ?? throw new InvalidOperationException($"{level.Id}: 升级链在 {row} 断链");
                    }

                    rows[step.Entity!.Value] = row;
                    break;
            }
        }
    }

    // ---- 1) schema 装载与结构声明 [F-055] --------------------------------

    [Fact]
    public void Both_files_parse_and_declare_the_Level_family_schema()
    {
        // [F-055] levels/e1.json+e2.json 解析+K-2 Level 族声明+首批 4+3 关。
        foreach (var (file, eraId, expectedCount) in new[]
                 {
                     ("e1.json", "era1:era-ancient", 4),
                     ("e2.json", "era2:era-classical", 3),
                 })
        {
            HandcraftedLevelSet set = HandcraftedLevelData.Load(file);
            Assert.Equal(eraId, set.Era);
            Assert.Equal(expectedCount, set.Items.Count);

            // 根 $schema/content_type 与 K-2 封闭枚举逐字对应（12-contracts-frozen K-2）
            string raw = File.ReadAllText(Path.Combine(LevelsDir(), file));
            using var doc = JsonDocument.Parse(raw);
            Assert.Matches("^urn:eratd:content:levels:1$", doc.RootElement.GetProperty("$schema").GetString()!);
            Assert.Equal("Level", doc.RootElement.GetProperty("content_type").GetString());
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                Assert.Equal("Level", item.GetProperty("content_type").GetString());
            }

            // 关卡行归 campaign 命名空间（techtree/legacy 同源引用，F-156）
            Assert.All(set.Items, level => Assert.StartsWith("campaign:level-", level.Id));
            Assert.Equal(set.Items.Count, set.Items.Select(l => l.Id).Distinct().Count());
        }
    }

    // ---- 2) 威胁度/胜波/宽限/预算：冻结公式与带 [F-055] -------------------

    [Fact]
    public void Win_waves_follow_the_frozen_formula_and_threats_progress_as_tasked()
    {
        // [F-055] 胜波=10+⌊t×10⌋（§7）逐关复核；E1 任务卡递进锚点
        // L1-1=0.3 教学、L1-2~4=0.3/0.45/0.6 严格递进；E2 行=排期冻结值。
        foreach (HandcraftedLevel level in AllLevels())
        {
            Assert.Equal(Campaign.WinWaves(level.Threat), level.WinWaves);
            Assert.InRange(level.Threat, 0.3, 1.2);
            Assert.InRange(level.BudgetSeconds, 900, 2400); // §3 手工关 15~40 分钟带
        }

        var e1 = E1().Items.OrderBy(l => l.Slot).ToList();
        Assert.Equal(0.3, e1[0].Threat, 3); // 教学关=带下限
        Assert.True(e1[0].TeachingLevel);
        Assert.Equal(new[] { 0.3, 0.45, 0.6 }, e1.Skip(1).Select(l => l.Threat));
        Assert.True(e1[1].Threat < e1[2].Threat && e1[2].Threat < e1[3].Threat); // 递进
        Assert.All(e1.Skip(1), l => Assert.False(l.TeachingLevel));
        Assert.Equal(new[] { 1, 2, 3, 4 }, e1.Select(l => l.Slot)); // slot 连续

        // 宽限带：教学 ×3（§6）；非教学 ×1.5~2（§6）——装载器已校验，此处锚值
        Assert.Equal(3.0, e1[0].FirstWaveGraceMultiplier, 3);
        Assert.All(e1.Skip(1), l => Assert.InRange(l.FirstWaveGraceMultiplier, 1.5, 2.0));
    }

    [Fact]
    public void Maps_are_fixed_seed_deterministic_layouts()
    {
        // [F-055] 手工关=确定性 seed+布局参数（非随机重掷）：water/rock=0
        // （命令流坐标可建性，E2E 夹具先例）、seed 七关互异且非零、出生点≠核心位。
        var seeds = new HashSet<long>();
        foreach (HandcraftedLevel level in AllLevels())
        {
            Assert.Equal(0.0, level.Map.WaterRatio, 9);
            Assert.Equal(0.0, level.Map.RockRatio, 9);
            Assert.NotEqual(0, level.Map.Seed);
            Assert.True(seeds.Add(level.Map.Seed), $"{level.Id}: seed 与他关重复");
            Assert.NotEqual(level.Map.BaseCell, level.Map.WarehouseCell);
            Assert.InRange(level.Map.ForestRatio, 0.0, 1.0);
        }
    }

    // ---- 3) 命令流与内容表引用闭合 [F-055] [F-023] ------------------------

    public static IEnumerable<object[]> LevelIds()
    {
        foreach (HandcraftedLevel level in E1().Items.Concat(E2().Items))
        {
            yield return new object[] { level.Id, HandcraftedLevelContent.EraDir(level) };
        }
    }

    [Theory]
    [MemberData(nameof(LevelIds))]
    public void Command_script_and_rosters_resolve_against_the_era_content_tables(string id, string eraDir)
    {
        // [F-055] 命令流/波表/英雄引用闭合：塔→era towers.json、敌种→enemies.json、
        // 英雄→heroes.json、converter 配方→recipes.json、初始资源→era 资源行。
        HandcraftedLevel level = FindLevel(id);
        var towers = HandcraftedLevelContent.Towers(eraDir);
        var enemies = HandcraftedLevelContent.Enemies(eraDir);

        foreach (ScriptStep step in level.CommandScript)
        {
            if (step.Tower is not null)
            {
                Assert.Contains(step.Tower, towers.Keys);
            }

            if (step.ConverterRecipe is not null)
            {
                Assert.True(HandcraftedLevelContent.RecipeExists(eraDir, step.ConverterRecipe),
                    $"{id}: 配方 {step.ConverterRecipe} 未见于 {eraDir}/recipes.json");
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

        if (level.HeroId is not null)
        {
            Assert.NotNull(level.Map.HeroCell); // 装载器成对校验的锚值面
            _ = HandcraftedLevelContent.Heroes("era2").Get(level.HeroId); // 未解析即抛
        }

        // 初始资源行解析到时代资源表（E2 传承刷新木材=era.json legacy_refresh 行）
        foreach (StockLine line in level.InitialStock)
        {
            Assert.Contains(line.Resource, HandcraftedLevelContent.EraResources(eraDir));
        }
    }

    [Theory]
    [MemberData(nameof(LevelIds))]
    public void First_wave_has_a_damage_tower_and_stock_covers_the_script_tower_costs(string id, string eraDir)
    {
        // [F-055] 教学不变式：wave 1 已有伤害塔（首波宽限内完成首防）；初始
        // 授予 ≥ 脚本塔价合计（含升级差：沿 towers.json UpgradesTo 链取目标档价）。
        HandcraftedLevel level = FindLevel(id);
        var towers = HandcraftedLevelContent.Towers(eraDir);
        var events = WalkBuilds(level, towers).ToList();

        Assert.Contains(events, e => e.TowerRow is not null
            && towers[e.TowerRow!].Dps > 0.0
            && e.Wave == 1);

        // 塔价合计=建造价 Σ + 升级差 Σ（目标档价−当前档价，沿 UpgradesTo 链）
        double cost = level.CommandScript
            .Where(s => s.Verb == "build-tower")
            .Sum(s => towers[s.Tower!].CostTotal);

        var baseRows = events.Where(e => e.TowerRow is not null)
            .ToDictionary(e => e.Entity, e => e.TowerRow!);
        var currentRows = new Dictionary<long, string>(baseRows);
        foreach (ScriptStep step in level.CommandScript.OrderBy(s => s.Wave).Where(s => s.Verb == "upgrade-tower"))
        {
            string target = ChainRow(towers, baseRows[step.Entity!.Value], step.ToTier!.Value);
            cost += towers[target].CostTotal - towers[currentRows[step.Entity!.Value]].CostTotal;
            currentRows[step.Entity!.Value] = target;
        }

        double grant = level.InitialStock.Sum(line => line.Amount);
        Assert.True(grant >= cost, $"{id}: 授予 {grant} < 脚本塔价 {cost}");
        Assert.True(grant <= 2000.0 * HandcraftedLevelContent.WarehouseLevel(eraDir),
            $"{id}: 授予 {grant} 超过仓库池上限（F.3 基 2000×等级）");
    }

    private static string ChainRow(IReadOnlyDictionary<string, TowerSpec> towers, string baseRow, int toTier)
    {
        string row = baseRow;
        for (int i = 1; i < toTier; i++)
        {
            row = towers[row].UpgradesTo
                ?? throw new InvalidOperationException($"升级链 {baseRow} 在 {row} 断链");
        }

        return row;
    }

    // ---- 4) 三重目标实例化（SP-8 池） [F-055] ------------------------------

    [Fact]
    public void Every_level_instantiates_survival_plus_strategy_and_challenge_objectives()
    {
        // [F-055] 每关 ≥1 策略+1 挑战目标实例（objectives 抽选 seed 化=map.seed
        // 家族，data 手工定稿）：strategy ⊆ SP-8a 池、challenge ⊆ SP-8b 池，
        // ★ 难位（c1~c4 hard）旗标对齐池行，七关实例两两不同。
        (IReadOnlyList<string> strategyPool, IReadOnlyList<string> challengePool) =
            HandcraftedLevelContent.ObjectivePools();
        Assert.Equal(8, strategyPool.Count); // SP-8a S1~S8
        Assert.Equal(8, challengePool.Count); // SP-8b C1~C8

        var challengeHard = HandcraftedLevelContent.ChallengeHardFlags();
        var seenInstances = new HashSet<string>();
        foreach (HandcraftedLevel level in AllLevels())
        {
            Assert.NotEmpty(level.Objectives.Strategy);
            Assert.NotEmpty(level.Objectives.Challenge);
            Assert.All(level.Objectives.Strategy, o => Assert.Contains(o, strategyPool));
            Assert.All(level.Objectives.Challenge, o => Assert.Contains(o, challengePool));

            foreach (string challengeId in level.Objectives.Challenge)
            {
                bool expectHard = challengeId is "campaign:obj-c1" or "campaign:obj-c2"
                    or "campaign:obj-c3" or "campaign:obj-c4";
                Assert.True(challengeHard[challengeId] == expectHard,
                    $"{level.Id}: {challengeId} 的 hard 旗标与 SP-8b 池不符");
            }

            string instance = string.Join('|',
                level.Objectives.Strategy.Concat(level.Objectives.Challenge).Order());
            Assert.True(seenInstances.Add(instance), $"{level.Id}: 目标实例与他关重复");
        }

        // 七关实例覆盖：策略 7/8 池、挑战 7/8 池（不同关不同目标，抽选面铺开）
        var usedStrategy = AllLevels().SelectMany(l => l.Objectives.Strategy).ToHashSet();
        var usedChallenge = AllLevels().SelectMany(l => l.Objectives.Challenge).ToHashSet();
        Assert.Equal(7, usedStrategy.Count);
        Assert.Equal(7, usedChallenge.Count);
    }

    [Fact]
    public void Limit_challenge_objectives_are_satisfiable_by_the_level_shape()
    {
        // [F-055] 限类挑战（c2 仅穿刺+支援）与脚本塔类一致；限塔挑战（c3 ≤8）
        // 与脚本塔数一致——目标不是空头支票。
        foreach (HandcraftedLevel level in AllLevels())
        {
            if (level.Objectives.Challenge.Contains("campaign:obj-c2"))
            {
                var towers = HandcraftedLevelContent.Towers(HandcraftedLevelContent.EraDir(level));
                foreach (ScriptStep step in level.CommandScript.Where(s => s.Verb == "build-tower"))
                {
                    string towerClass = towers[step.Tower!].Class.ToString().ToLowerInvariant();
                    Assert.Contains(towerClass, new[] { "pierce", "support" });
                }
            }

            if (level.Objectives.Challenge.Contains("campaign:obj-c3"))
            {
                int towerCount = level.CommandScript.Count(s => s.Verb == "build-tower");
                Assert.True(towerCount <= 8, $"{level.Id}: 脚本塔数 {towerCount} > c3 限 8");
            }
        }
    }

    // ---- 5) E2 排期一致性（T-079 冻结元数据） [F-092] ----------------------

    [Fact]
    public void E2_rows_match_the_frozen_era2_levels_schedule_row_by_row()
    {
        // [F-092] 排期一致性断言：levels/e2.json 三行与 era2/levels.json
        // （T-079 冻结）逐字段一致——id/slot/focus/tier/邻接/威胁/胜波/宽限/
        // 教学/觉醒/解锁双列，任何一侧漂移即红。
        var schedule = HandcraftedLevelContent.Era2ScheduleRows();
        Assert.Equal(3, schedule.Count);
        foreach (HandcraftedLevel level in E2().Items)
        {
            JsonElement frozen = schedule[level.Slot];
            Assert.Equal(frozen.GetProperty("id").GetString(), level.Id);
            Assert.Equal(frozen.GetProperty("slot").GetInt32(), level.Slot);
            Assert.Equal(frozen.GetProperty("focus").GetString(), level.Focus);
            Assert.Equal(frozen.GetProperty("schedule_tier").GetString(), level.ScheduleTier);
            Assert.Equal(frozen.GetProperty("adjacent_enemy_camps").GetInt32(), level.AdjacentEnemyCamps);
            Assert.Equal(frozen.GetProperty("threat").GetDouble(), level.Threat, 3);
            Assert.Equal(frozen.GetProperty("win_waves").GetInt32(), level.WinWaves);
            Assert.Equal(frozen.GetProperty("first_wave_grace_multiplier").GetDouble(),
                level.FirstWaveGraceMultiplier, 3);
            Assert.Equal(frozen.GetProperty("teaching_level").GetBoolean(), level.TeachingLevel);
            Assert.Equal(frozen.GetProperty("awakening_cinematic").GetBoolean(), level.AwakeningCinematic);
            Assert.Equal(frozen.GetProperty("name_zh").GetString(), level.NameZh);
            Assert.Equal(frozen.GetProperty("name_en").GetString(), level.NameEn);

            // SP-9：E2 行威胁度可由邻接敌营派生（手工行与程序口径在此合流）
            Assert.Equal(Threat.ClampThreat(level.AdjacentEnemyCamps!.Value), level.Threat, 9);

            var frozenActions = frozen.GetProperty("unlocks").GetProperty("actions").EnumerateArray()
                .Select(a => a.GetString()!).Order().ToList();
            var frozenFeatures = frozen.GetProperty("unlocks").GetProperty("features").EnumerateArray()
                .Select(f => f.GetString()!).Order().ToList();
            Assert.Equal(frozenActions, level.Unlocks.Actions.Order().ToList());
            Assert.Equal(frozenFeatures, level.Unlocks.Features.Order().ToList());
        }
    }

    [Fact]
    public void E2_capability_unlock_chain_is_progressive_and_per_focus()
    {
        // [F-092] 能力解锁链：L2-1 只水力（对话级两动作）、L2-2 增马车+水渠
        // （建造级九动作）、L2-3 增考古+英雄（巡逻级 SpawnScout/HeroOrder）——
        // G.2 锚点动作只出现在其排期位，E1 首批不新增闸位。
        var bySlot = E2().Items.OrderBy(l => l.Slot).ToList();

        Assert.Equal(new[] { "TodoBoard", "StopTask" }.Order().ToList(), bySlot[0].Unlocks.Actions.Order().ToList());
        Assert.Equal(new[] { "waterpower_energy" }, bySlot[0].Unlocks.Features); // 只水力

        Assert.Contains("carriage_logistics", bySlot[1].Unlocks.Features);
        Assert.Contains("aqueduct", bySlot[1].Unlocks.Features);
        Assert.Equal(new[] { "SpawnScout", "HeroOrder" }.Order().ToList(), bySlot[2].Unlocks.Actions.Order().ToList());
        Assert.Contains("archaeology", bySlot[2].Unlocks.Features);
        Assert.Contains("hero_recruit", bySlot[2].Unlocks.Features);

        // G.2：巡逻级动作不得早于 slot 3（Era2ContentTests 三源矩阵的排期侧同锚）
        Assert.DoesNotContain("SpawnScout", bySlot[0].Unlocks.Actions);
        Assert.DoesNotContain("SpawnScout", bySlot[1].Unlocks.Actions);
        Assert.DoesNotContain("HeroOrder", bySlot[0].Unlocks.Actions);
        Assert.DoesNotContain("HeroOrder", bySlot[1].Unlocks.Actions);

        // E2 三威胁递进（0.3<0.6<0.9 → 胜波 13<16<19，§7 公式）
        Assert.True(bySlot[0].Threat < bySlot[1].Threat && bySlot[1].Threat < bySlot[2].Threat);
        Assert.True(bySlot[0].WinWaves < bySlot[1].WinWaves && bySlot[1].WinWaves < bySlot[2].WinWaves);

        // E1 首批不新增闸位（E1 能力=时代级解锁，G.2 解锁时代=E1）
        Assert.All(E1().Items, level =>
        {
            Assert.Empty(level.Unlocks.Actions);
            Assert.Empty(level.Unlocks.Features);
        });
    }

    // ---- 6) 觉醒演出钩子（§10.4） [F-055] ---------------------------------

    [Fact]
    public void Awakening_cinematic_hooks_are_bound_wherever_the_flag_is_set()
    {
        // [F-055] 觉醒位=演出素材接入点：E1 首关=§10.4 ① 断弹啊哈（第 2~3 波）
        // +胜利交底；E2 首关（T-079 awakening_cinematic）=§10.4 ② level_start
        // 觉醒演出；非觉醒位不带钩子。
        HandcraftedLevel e1First = E1().Items.Single(l => l.Slot == 1);
        Assert.True(e1First.AwakeningCinematic);
        Assert.Contains(e1First.CinematicHooks, h => h.Beat == "wave_start" && h.Wave is >= 2 and <= 3);
        Assert.Contains(e1First.CinematicHooks, h => h.Beat == "victory");

        HandcraftedLevel e2First = E2().Items.Single(l => l.Slot == 1);
        Assert.True(e2First.AwakeningCinematic); // era2/levels.json level-e2-01 冻结位
        Assert.Contains(e2First.CinematicHooks, h => h.Beat == "level_start");

        foreach (HandcraftedLevel level in AllLevels().Where(l => !l.AwakeningCinematic))
        {
            Assert.Empty(level.CinematicHooks);
        }
    }

    // ---- 7) 装载确定性（同文件双装载相等） [F-055] -------------------------

    [Fact]
    public void Double_load_of_both_files_is_structurally_identical()
    {
        // [F-055] 确定性①：同文件双装载 → 数据面全等（固定 seed+布局参数）。
        // 记录携带列表成员（record 相等性对 IReadOnlyList 走引用比较），故以
        // 序列化快照逐字节比对——更强的结构等值证明；引擎面的双构造等值见
        // HandcraftedLevelAssemblyTests。
        foreach (var file in new[] { "e1.json", "e2.json" })
        {
            HandcraftedLevelSet first = HandcraftedLevelData.Load(file);
            HandcraftedLevelSet second = HandcraftedLevelData.Load(file);
            Assert.Equal(first.Era, second.Era);
            Assert.Equal(first.Items.Count, second.Items.Count);

            Assert.Equal(
                JsonSerializer.Serialize(first),
                JsonSerializer.Serialize(second));
            for (int i = 0; i < first.Items.Count; i++)
            {
                Assert.Equal(first.Items[i].Id, second.Items[i].Id);
                Assert.Equal(first.Items[i].Map.Seed, second.Items[i].Map.Seed);
                Assert.Equal(first.Items[i].Map.EntryCells.Count, second.Items[i].Map.EntryCells.Count);
                Assert.Equal(first.Items[i].CommandScript.Count, second.Items[i].CommandScript.Count);
            }
        }
    }

    // ---- 8) 装载器合法/边界/非法（CS-3.1） --------------------------------

    private static string MinimalLevelJson() => """
        {
          "$schema": "urn:eratd:content:levels:1",
          "content_type": "Level",
          "era": "era1:era-ancient",
          "items": [
            {
              "id": "campaign:level-e1-99",
              "content_type": "Level",
              "era": "era1:era-ancient",
              "slot": 1,
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
                "forest_ratio": 0.1,
                "rock_ratio": 0.0,
                "water_ratio": 0.0,
                "base_cell": { "x": 2, "y": 5 },
                "warehouse_cell": { "x": 0, "y": 0 },
                "entry_cells": [ { "x": 2, "y": 0 } ]
              },
              "initial_stock": [ { "resource": "era1:res-wood", "amount": 100.0 } ],
              "waves": {
                "base_count": 6,
                "begin_wave": 2,
                "spacing": 2,
                "scaling": 2.0,
                "batches_per_wave": 2,
                "valley_seconds": 15.0,
                "regular_roster": ["era1:enemy-beastling"],
                "elite_line_enabled": false
              },
              "command_script": [
                { "wave": 1, "verb": "build-labor-camp", "cell": { "x": 1, "y": 1 } },
                { "wave": 1, "verb": "build-tower", "tower": "era1:tower-wood-arrow", "cell": { "x": 2, "y": 2 } },
                { "wave": 1, "verb": "assign-carrier", "from_entity": 2, "to_entity": 3 }
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
    public void Loader_accepts_the_minimal_legal_level_and_band_edges()
    {
        // [F-055] legal — 最小合法关；boundary — 威胁 1.2/胜波 22、宽限 2.0、预算 2400 带沿
        HandcraftedLevelSet set = HandcraftedLevelData.Parse(MinimalLevelJson());
        HandcraftedLevel level = Assert.Single(set.Items);
        Assert.Equal(13, level.WinWaves);

        string upperThreat = MinimalLevelJson()
            .Replace("\"threat\": 0.3", "\"threat\": 1.2")
            .Replace("\"win_waves\": 13", "\"win_waves\": 22");
        Assert.Equal(22, Assert.Single(HandcraftedLevelData.Parse(upperThreat).Items).WinWaves);

        string upperBudget = MinimalLevelJson().Replace("\"budget_seconds\": 900", "\"budget_seconds\": 2400");
        Assert.Equal(2400, Assert.Single(HandcraftedLevelData.Parse(upperBudget).Items).BudgetSeconds);

        string upperGrace = MinimalLevelJson()
            .Replace("\"first_wave_grace_multiplier\": 1.5", "\"first_wave_grace_multiplier\": 2.0");
        Assert.Equal(2.0, Assert.Single(HandcraftedLevelData.Parse(upperGrace).Items).FirstWaveGraceMultiplier, 3);
    }

    public static TheoryData<string, string, string> IllegalMutations => new()
    {
        // illegal — 结构违规必须点名拒绝（每条规则一条故意违例，CS-3.5）
        { "win_waves 与公式不符", "\"win_waves\": 13", "\"win_waves\": 14" },
        { "威胁度越带", "\"threat\": 0.3", "\"threat\": 1.5" },
        { "非教学宽限越带", "\"first_wave_grace_multiplier\": 1.5", "\"first_wave_grace_multiplier\": 2.5" },
        { "教学宽限非 ×3", "\"teaching_level\": false", "\"teaching_level\": true" },
        { "预算越带", "\"budget_seconds\": 900", "\"budget_seconds\": 100" },
        { "水比非零", "\"water_ratio\": 0.0", "\"water_ratio\": 0.1" },
        { "出生点越界", "\"entry_cells\": [ { \"x\": 2, \"y\": 0 } ]", "\"entry_cells\": [ { \"x\": 99, \"y\": 0 } ]" },
        { "出生点与核心重合", "\"base_cell\": { \"x\": 2, \"y\": 5 }", "\"base_cell\": { \"x\": 2, \"y\": 0 }" },
        { "未登记动词", "\"verb\": \"build-labor-camp\"", "\"verb\": \"sell-everything\"" },
        { "塔步缺塔 id", "\"verb\": \"build-tower\", \"tower\": \"era1:tower-wood-arrow\"", "\"verb\": \"build-tower\"" },
        { "民夫引用未建实体", "\"from_entity\": 2, \"to_entity\": 3", "\"from_entity\": 2, \"to_entity\": 9" },
        { "挑战目标缺失", "\"challenge\": [\"campaign:obj-c8\"]", "\"challenge\": []" },
        { "波谷不小于波间隔", "\"valley_seconds\": 15.0", "\"valley_seconds\": 999.0" },
        { "一格重复建造", "\"cell\": { \"x\": 2, \"y\": 2 }", "\"cell\": { \"x\": 1, \"y\": 1 }" },
        { "命令波锚越胜波窗", "\"wave\": 1, \"verb\": \"assign-carrier\"", "\"wave\": 99, \"verb\": \"assign-carrier\"" },
        { "E2 转换器缺配方引用",
            "{ \"wave\": 1, \"verb\": \"assign-carrier\", \"from_entity\": 2, \"to_entity\": 3 }",
            "{ \"wave\": 1, \"verb\": \"build-converter\", \"cell\": { \"x\": 3, \"y\": 3 } }" },
    };

    [Theory]
    [MemberData(nameof(IllegalMutations))]
    public void Loader_rejects_each_structural_violation_by_name(string caseName, string from, string to)
    {
        // [F-055] illegal — 每条结构规则一条"故意违反应被拦截"（防恒绿）；E2 转换器
        // 缺配方一条以 era2 版最小关触发（era 字段同步替换）。
        string json = MinimalLevelJson().Replace(from, to);
        if (caseName.StartsWith("E2", StringComparison.Ordinal))
        {
            json = json
                .Replace("\"era\": \"era1:era-ancient\"", "\"era\": \"era2:era-classical\"")
                .Replace("era1:res-wood", "era2:res-copper")
                .Replace("era1:enemy-beastling", "era2:enemy-skirmisher")
                .Replace("era1:tower-wood-arrow", "era2:tower-arrow");
        }

        var exception = Assert.Throws<HandcraftedLevelSchemaException>(
            () => HandcraftedLevelData.Parse(json));
        Assert.NotEqual("", exception.Message.Trim());
        Assert.Contains("campaign:level-e1-99", exception.Message); // 消息定位到关卡行
    }
}
