using System.Text.Json;
using EraCore.Balance;
using EraCore.Contracts.Foundation;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055][F-060][F-092][F-012] T-134 E5 手工关卡集（levels/e5.json 首批
// 5 关=冻结排期前三关+后续关+终局关）数据回归：schema 装载即校验（era5 rock=0
// 固定布局规则）、前三关与 era5/levels.json（T-134 冻结排期）逐字段一致、能力
// 解锁链对位、三重目标实例化（SP-8 池+E5 敌特性适配——主宰重力脉冲/湮灭对策
// 语境）、命令流引用闭合（塔/敌/配方→era5 内容表；drone 20/s 干线=E5 新物流
// 档的可玩面、力场塔=湮灭对策位首发）、终局关 Boss 行与 era_transition
// final_levels 缝合边界（E5→终章引用=T-135 域不预置——悬空引用禁律）、装载
// 确定性。期望值全部锚在内容表/冻结公式（出处写在各断言处注释），解析或校验
// 失败即红。
public class Era5HandcraftedLevelSetTests
{
    private static HandcraftedLevelSet E5() => HandcraftedLevelData.Load("e5.json");

    private static HandcraftedLevel Level(int slot) =>
        E5().Items.Single(level => level.Slot == slot);

    // ---- 1) schema 装载与结构声明 [F-055] --------------------------------

    [Fact]
    public void E5_file_parses_and_declares_the_Level_family_schema()
    {
        // [F-055] levels/e5.json 解析+K-2 Level 族声明+首批 5 关（前三关=L5-1/2/3
        // 排期关+L5-4 后续关+e5-final 终局关，T-134 任务卡『4~5 关』上沿同构 e4）。
        HandcraftedLevelSet set = E5();
        Assert.Equal("era5:era-future", set.Era);
        Assert.Equal(5, set.Items.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, set.Items.Select(level => level.Slot).OrderBy(slot => slot).ToArray());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "game", "content", "levels")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        string raw = File.ReadAllText(Path.Combine(dir.FullName, "game", "content", "levels", "e5.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Matches("^urn:eratd:content:levels:1$", doc.RootElement.GetProperty("$schema").GetString()!);
        Assert.Equal("Level", doc.RootElement.GetProperty("content_type").GetString());
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Level", item.GetProperty("content_type").GetString());
        }

        // 关卡行归 campaign 命名空间（techtree/legacy 同源引用，F-156）
        Assert.All(set.Items, level => Assert.StartsWith("campaign:level-e5-", level.Id));
        Assert.Equal(set.Items.Count, set.Items.Select(level => level.Id).Distinct().Count());
    }

    [Fact]
    public void Threats_win_waves_and_graces_follow_the_frozen_formulas()
    {
        // [F-055] 胜波=10+⌊t×10⌋（§7）逐关复核：0.3/0.6/0.9/0.75/0.9→13/16/19/17/19；
        // 预算 15~40 分钟带；教学位仅 L5-1（宽限 ×3），其余 ×1.5~2（§6）。
        foreach (HandcraftedLevel level in E5().Items)
        {
            Assert.Equal(Campaign.WinWaves(level.Threat), level.WinWaves);
            Assert.InRange(level.Threat, 0.3, 1.2);
            Assert.InRange(level.BudgetSeconds, 900, 2400);
        }

        Assert.Equal(
            new[] { 0.3, 0.6, 0.9, 0.75, 0.9 },
            E5().Items.OrderBy(level => level.Slot).Select(level => level.Threat));
        Assert.Equal(
            new[] { 13, 16, 19, 17, 19 },
            E5().Items.OrderBy(level => level.Slot).Select(level => level.WinWaves));
        Assert.True(Level(1).TeachingLevel);
        Assert.All(E5().Items.Skip(1), level => Assert.False(level.TeachingLevel));
        Assert.All(E5().Items.Skip(1), level => Assert.InRange(level.FirstWaveGraceMultiplier, 1.5, 2.0));

        // 后续关威胁带（T-134 任务卡『后续 1~2 关』——L5-4=0.75 带内，e3-04/e4-04 后续关先例）
        Assert.InRange(Level(4).Threat, 0.75, 0.9);

        // 前三关威胁递进（0.3<0.6<0.9 → 胜波 13<16<19，§7）
        Assert.True(Level(1).Threat < Level(2).Threat && Level(2).Threat < Level(3).Threat);
        Assert.True(Level(1).WinWaves < Level(2).WinWaves && Level(2).WinWaves < Level(3).WinWaves);

        // 终局关=SP-9 邻接 2 网格点 0.9（e1/e2/e3/e4-final 同档）
        Assert.Equal(0.9, Level(5).Threat, 9);
        Assert.Equal(2, Level(5).AdjacentEnemyCamps!.Value);
    }

    // ---- 2) map 块与命令流引用闭合 [F-055] [F-023] -----------------------

    [Fact]
    public void Map_blocks_stay_on_the_fixed_layout_and_digs_bind_to_the_archaeology_slot()
    {
        // [F-055] E5 全关固定布局：water=0（装载器规则）、rock=0（E1/E2/E4/E5 规则——
        // E5 矿位稀缺声明通道=稀土矿脉，随跨代扩展另落，本批零声明=零矿格）、
        // forest=0.05（E3~E5 传承刷新档沿用）；考古 dig_sites 只在 L5-3（冻结排期
        // slot 3 focus=archaeology_hero_system，e2/e3/e4-03 同位先例）。
        foreach (HandcraftedLevel level in E5().Items)
        {
            Assert.Equal(0.0, level.Map.WaterRatio, 9);
            Assert.Equal(0.05, level.Map.ForestRatio, 9);
            Assert.Equal(0.0, level.Map.RockRatio, 9);
        }

        Assert.Equal(2, Level(3).Map.DigSites.Count);
        Assert.All(E5().Items.Where(level => level.Slot != 3), level => Assert.Empty(level.Map.DigSites));
    }

    [Fact]
    public void Command_script_and_rosters_resolve_against_the_era5_content_tables()
    {
        // [F-055] 引用闭合：塔→era5/towers.json、敌种（含 Boss 行）→era5/enemies.json、
        // converter 配方→era5/recipes.json、初始资源→era.json 资源行（纳米=E5
        // 建造货币行，E1 木/E2 铜/E3 钢/E4 混凝土/E5 纳米跨代递进）；运输档位=
        // 封闭档位表 trip 形（porter/cart/drone——drone 20/s=E5 新物流档的可玩面，
        // TransportTiers.Drone 引擎档位；e1~e4 命令流不得提前使用 drone）。
        var towers = Towers("era5");
        var enemies = Enemies("era5");

        bool anyDrone = false;
        foreach (HandcraftedLevel level in E5().Items)
        {
            foreach (ScriptStep step in level.CommandScript)
            {
                if (step.Tower is not null)
                {
                    Assert.Contains(step.Tower, towers.Keys);
                }

                if (step.ConverterRecipe is not null)
                {
                    Assert.True(RecipeExists("era5", step.ConverterRecipe),
                        $"{level.Id}: 配方 {step.ConverterRecipe} 未见于 era5/recipes.json");
                }

                if (step.CarrierTier is not null)
                {
                    Assert.Contains(step.CarrierTier, HandcraftedLevelData.CarrierTiers);
                    anyDrone |= step.CarrierTier == "drone";
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

            if (level.Waves.BossEnemyId is not null)
            {
                Assert.Contains(level.Waves.BossEnemyId!, enemies.Keys);
            }

            foreach (StockLine line in level.InitialStock)
            {
                Assert.Contains(line.Resource, EraResources("era5"));
            }
        }

        // 无人机档确实入命令流（E5 新物流可玩面——e5.json _source『无人机 20/s』）
        Assert.True(anyDrone, "E5 命令流须使用 drone 运输档（20/s 阶梯第 4 档的可玩面）");

        // e1~e4 命令流不得提前使用 drone（物流阶梯 §5 L78——E5 行档位不提前；
        // 解装载后逐流断言 carrier_tier，非字符串扫描）
        foreach (var file in new[] { "e1.json", "e2.json", "e3.json", "e4.json" })
        {
            Assert.All(HandcraftedLevelData.Load(file).Items,
                level => Assert.DoesNotContain(level.CommandScript,
                    step => step.CarrierTier == "drone"));
        }

        // 传承刷新资源以采集原料身份入命令流（『弹链本代化』旧资源=分散介质/
        // 磁约束壳体/弹芯，T-115 §二裁定延续）：石油（era4 命名空间）+铁（era2
        // 命名空间）采集器存在、era4:ammo-* 不见于任何 e5 引用
        string raw5 = File.ReadAllText(Path.Combine(FindLevelsDir(), "e5.json"));
        Assert.Contains("\"era4:res-oil\"", raw5);
        Assert.Contains("\"era2:res-iron\"", raw5);
        Assert.DoesNotContain("era4:ammo-", raw5);
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

    /// <summary>The aliases of one authored e5.json row (raw-JSON face; the parsed record carries no aliases).</summary>
    private static IReadOnlyList<string> AuthoredAliases(string levelId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindLevelsDir(), "e5.json")));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == levelId)
            .GetProperty("aliases").EnumerateArray()
            .Select(alias => alias.GetString()!)
            .ToList();
    }

    [Fact]
    public void First_wave_has_a_damage_tower_and_stock_covers_the_script_tower_costs()
    {
        // [F-055] 教学不变式：wave 1 已有伤害塔（三级炼链供弹的首防）；初始授予
        // ≥ 脚本塔价+升级差价合计；授予 ≤ 仓库池上限（F.3 基 2000×era5 仓库等级
        // 5=10000，e2-04 满额上沿先例）；E5 无英雄行（era5 无 heroes.json——
        // hero-order 不得出现）。
        var towers = Towers("era5");
        foreach (HandcraftedLevel level in E5().Items)
        {
            Assert.Contains(level.CommandScript, step => step.Verb == "build-tower"
                && towers[step.Tower!].Dps > 0.0
                && step.Wave == 1);
            Assert.DoesNotContain(level.CommandScript, step => step.Verb == "hero-order");

            double cost = level.CommandScript
                .Where(step => step.Verb == "build-tower")
                .Sum(step => towers[step.Tower!].CostTotal);
            double grant = level.InitialStock.Sum(line => line.Amount);
            Assert.True(grant >= cost, $"{level.Id}: 授予 {grant} < 脚本塔价 {cost}");
            Assert.True(grant <= 2000.0 * WarehouseLevel("era5"), $"{level.Id}: 授予 {grant} 超过仓库池上限");
        }

        // e5-final 升级差价覆盖（轨道炮 2430→无人机巢 7290：差额 4860=
        // TowerEstateOps DifferentialCost 口径）——授予 10000 ≥ 塔 4860+升级 4860=9720
        HandcraftedLevel final = Level(5);
        Assert.Equal(10000.0, final.InitialStock.Sum(line => line.Amount), 9);
        Assert.Contains(final.CommandScript, step => step.Verb == "upgrade-tower" && step.ToTier == 2);
        double finalCost = final.CommandScript
            .Where(step => step.Verb == "build-tower")
            .Sum(step => towers[step.Tower!].CostTotal)
            + (7290 - 2430); // 升级差价
        Assert.True(final.InitialStock.Sum(line => line.Amount) >= finalCost,
            $"e5-final 授予 10000 须覆盖塔价+升级差价 {finalCost}");
    }

    // ---- 3) 三重目标实例化（SP-8 池 + E5 敌特性适配） [F-055] -------------

    [Fact]
    public void Every_level_instantiates_survival_plus_distinct_strategy_and_challenge_objectives()
    {
        // [F-055] 每关 ≥1 策略+1 挑战目标实例（生存=结构性），实例两两不同；
        // ★ 难位旗标对齐池行（c1/c2/c3/c4 hard）。E5 敌特性适配（任务卡）：终局
        // 关（主宰 traits.gravity_pulse+盾 2400）的挑战=c4 不使用系统建造——纯玩家
        // 命令流对位『湮灭语境』（轨道炮穿甲=盾再生打断反制位、力场塔=湮灭半径
        // 减半对策位，池内无湮灭专属目标行——语境物化在 Boss 行+力场钩子，非目标池自造）。
        (IReadOnlyList<string> strategyPool, IReadOnlyList<string> challengePool) = ObjectivePools();
        var challengeHard = ChallengeHardFlags();

        var seenInstances = new HashSet<string>();
        foreach (HandcraftedLevel level in E5().Items)
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

        // E5 语境锚（任务卡『湮灭对策挑战』）：终局关挑战=c4（不使用系统建造）
        HandcraftedLevel final = Level(5);
        Assert.Contains("campaign:obj-c4", final.Objectives.Challenge);
    }

    [Fact]
    public void Limit_challenge_objectives_are_satisfiable_by_the_level_shape()
    {
        // [F-055] 限塔挑战（c3 ≤8）与脚本塔数一致——目标不是空头支票；
        // L5-4 力场=湮灭对策位（era5:tower-force-field 减速类+反湮灭钩子）
        // 的首发位（e5.json _source 同锚）。
        var towers = Towers("era5");
        foreach (HandcraftedLevel level in E5().Items)
        {
            if (level.Objectives.Challenge.Contains("campaign:obj-c3"))
            {
                int towerCount = level.CommandScript.Count(step => step.Verb == "build-tower");
                Assert.True(towerCount <= 8, $"{level.Id}: 脚本塔数 {towerCount} > c3 限 8");
            }
        }

        HandcraftedLevel counterplay = Level(4);
        Assert.Contains(counterplay.CommandScript, step => step.Verb == "build-tower"
            && towers[step.Tower!].Id == "era5:tower-force-field");
        Assert.All(E5().Items.Where(level => level.Slot != 4),
            level => Assert.DoesNotContain(level.CommandScript,
                step => step.Verb == "build-tower" && towers[step.Tower!].Id == "era5:tower-force-field"));
    }

    // ---- 4) 前三关排期一致性（T-134 冻结元数据） [F-092] [F-012] ----------

    [Fact]
    public void First_three_rows_match_the_frozen_era5_levels_schedule_row_by_row()
    {
        // [F-092][F-012] 排期一致性断言：levels/e5.json 前三行与 era5/levels.json
        //（T-134 冻结排期侧）逐字段一致——id/slot/focus/schedule_tier/邻接/威胁/
        // 胜波/宽限/教学/觉醒/关名/别名/解锁双列，任何一侧漂移即红；L5-4/e5-final
        // 不在冻结表（后续关+终局关——era5/levels.json 恰 3 行）。
        var schedule = Era5ScheduleRows();
        Assert.Equal(3, schedule.Count);
        foreach (HandcraftedLevel level in E5().Items.Where(level => level.Slot <= 3))
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
            Assert.True(frozenAliases.Count >= 3); // 别名行 ≥3（e4 先例 3；e5-03 托管关 4）
            Assert.Equal(frozenAliases, AuthoredAliases(level.Id)); // 别名行逐字一致

            // SP-9：E5 行威胁度可由邻接敌营派生（手工行与程序口径合流）
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
    public void E5_capability_unlock_chain_matches_the_schedule_focus_of_each_slot()
    {
        // [F-092][F-012] 能力解锁链逐关对位冻结排期：L5-1 聚变+过热/湮灭风险
        //（教学位）、L5-2 无人机物流、L5-3 考古+英雄招募+全托管（巡逻/托管级
        // 末档位）；19 动作清单已于 E2 全解锁 ⇒ E5 各关 unlocks.actions 恒空
        //（era5/levels.json 注）；L5-4/e5-final 无新增解锁位（e3-04/e4-04 先例）。
        Assert.Equal(
            new[] { "annihilation_risk", "fusion_energy", "overheat_risk" }.Order().ToList(),
            Level(1).Unlocks.Features.Order().ToList());
        Assert.Equal(new[] { "drone_logistics" }.Order().ToList(), Level(2).Unlocks.Features.Order().ToList());
        Assert.Equal(
            new[] { "archaeology", "full_autopilot", "hero_recruit" }.Order().ToList(),
            Level(3).Unlocks.Features.Order().ToList());
        Assert.All(E5().Items, level => Assert.Empty(level.Unlocks.Actions));
        Assert.Empty(Level(4).Unlocks.Features);
        Assert.Empty(Level(5).Unlocks.Features);

        // 英雄行=章节设计待定案（era5 无 heroes.json 行；hero_id/hero_cell 成对缺席）
        Assert.All(E5().Items, level => Assert.Null(level.HeroId));
        Assert.All(E5().Items, level => Assert.Null(level.Map.HeroCell));

        // 非觉醒位不带钩子（era5/levels.json awakening_cinematic 恒 false——
        // §10.4 觉醒演出=E2 第一关专属锚）
        Assert.All(E5().Items, level => Assert.False(level.AwakeningCinematic));
        Assert.All(E5().Items, level => Assert.Empty(level.CinematicHooks));
    }

    // ---- 5) 终局关 Boss 行 + 跃迁缝合边界（era_transition ↔ 关卡集） ------

    [Fact]
    public void Final_boss_row_carries_the_boss_wave_and_stays_out_of_the_undelivered_E6_reference()
    {
        // [F-003][F-055] 跃迁缝合边界（T-134 口径）：e5-final 是 E5 章压轴 Boss
        // 关，但 E5→终章跃迁=T-135 域——final_levels[5] 引用必须由 T-135 随终章
        // 实关行一并落位（本断言钉死『不预置悬空引用』：键 5 不存在，a58c86f
        // 『引用先行、实关缺失』教训的防复发面——与 E4 行的反面先例相对照：
        // final_levels[4]=campaign:level-e4-final 引用与实关两侧行 id 相等）。
        var transition = EraCore.Tests.Legacy.LegacyFixture.Transition();
        Assert.Equal("campaign:level-e4-final", transition.FinalLevels[4]); // E4→E5 硬前置（T-124 落位，本任务域引用侧不变）
        Assert.False(transition.FinalLevels.ContainsKey(5),
            "final_levels[5]（E5→终章）=T-135 域——终章实关行落地前不得预置悬空引用");

        var bossRows = E5().Items.Where(l => l.Waves.BossEnemyId is not null).ToList();
        HandcraftedLevel final = Assert.Single(bossRows); // 每文件恰一 Boss 行
        Assert.Equal("campaign:level-e5-final", final.Id);
        Assert.Equal("era5:enemy-boss", final.Waves.BossEnemyId);
        Assert.Equal(final.WinWaves, final.Waves.BossWave); // Boss 波=胜波（终局一搏）
        Assert.Equal(19, final.WinWaves); // 威胁 0.9 → §7
        Assert.Equal(0.9, final.Threat, 9);

        // Boss 敌种解析到 era 敌表的 is_boss 行（enemies.json e5_boss 主宰）
        var enemies = Enemies("era5");
        Assert.True(enemies["era5:enemy-boss"].IsBoss, "era5:enemy-boss 须为敌表 is_boss 行");
        Assert.Equal(45000, enemies["era5:enemy-boss"].Hp); // §4 L62 逐字锚点

        // e4.json 侧缝合：e4-final=e4 文件恰一 Boss 行=final_levels[4] 引用值
        var e4BossRows = HandcraftedLevelData.Load("e4.json").Items
            .Where(l => l.Waves.BossEnemyId is not null).ToList();
        Assert.Equal("campaign:level-e4-final", Assert.Single(e4BossRows).Id);
    }

    [Fact]
    public void Force_field_counter_hooks_stay_cross_referenced_between_towers_and_the_annihilation_row()
    {
        // [F-010] 主宰湮灭对策语境（任务卡）：era5:tower-force-field
        // traits.force_field_annihilation_radius_multiplier 与 era5/era.json
        // energy.annihilation.consequences.force_field_radius_multiplier 两行
        // 同源互指（SP-1b L54『可吸收湮灭 50% 半径（联动 SP-4）』两文件同源）。
        string contentRoot = Directory.GetParent(FindLevelsDir())!.FullName;
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(contentRoot, "era5", "towers.json"))))
        {
            JsonElement field = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "era5:tower-force-field");
            Assert.Equal(0.5, field.GetProperty("traits")
                .GetProperty("force_field_annihilation_radius_multiplier").GetDouble(), 9);
        }

        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(contentRoot, "era5", "era.json"))))
        {
            JsonElement annihilation = doc.RootElement.GetProperty("items").EnumerateArray().Single()
                .GetProperty("energy").GetProperty("annihilation");
            Assert.Equal("annihilation", annihilation.GetProperty("kind").GetString());
            Assert.Equal(0.5, annihilation.GetProperty("consequences")
                .GetProperty("force_field_radius_multiplier").GetDouble(), 9);
        }

        // 力场塔入命令流（L5-4 首发=湮灭对策的可玩面；其余关不混用）
        Assert.All(E5().Items.Where(level => level.Slot != 4),
            level => Assert.DoesNotContain(level.CommandScript,
                step => step.Verb == "build-tower" && step.Tower == "era5:tower-force-field"));
    }

    // ---- 6) 装载确定性 + era5 装载器边界/非法（CS-3.1） -------------------

    [Fact]
    public void Double_load_of_e5_json_is_structurally_identical()
    {
        // [F-055] 确定性①：同文件双装载 → 序列化快照逐字节全等。
        HandcraftedLevelSet first = HandcraftedLevelData.Load("e5.json");
        HandcraftedLevelSet second = HandcraftedLevelData.Load("e5.json");
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    private static string MinimalEra5LevelJson() => """
        {
          "$schema": "urn:eratd:content:levels:1",
          "content_type": "Level",
          "era": "era5:era-future",
          "items": [
            {
              "id": "campaign:level-e5-99",
              "content_type": "Level",
              "era": "era5:era-future",
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
              "initial_stock": [ { "resource": "era5:res-nano", "amount": 100.0 } ],
              "waves": {
                "base_count": 6,
                "begin_wave": 2,
                "spacing": 2,
                "scaling": 2.0,
                "batches_per_wave": 2,
                "valley_seconds": 15.0,
                "regular_roster": ["era5:enemy-nano-swarm"],
                "elite_line_enabled": false
              },
              "command_script": [
                { "wave": 1, "verb": "build-labor-camp", "cell": { "x": 1, "y": 1 } },
                { "wave": 1, "verb": "build-converter", "converter_recipe": "era5:recipe-nano", "cell": { "x": 2, "y": 2 } },
                { "wave": 1, "verb": "build-tower", "tower": "era5:tower-railgun", "cell": { "x": 3, "y": 3 } },
                { "wave": 1, "verb": "assign-carrier", "from_entity": 2, "to_entity": 3, "carrier_tier": "drone" }
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
    public void Loader_accepts_the_minimal_era5_level_and_rejects_the_rock_declaration()
    {
        // [F-055] legal — 最小合法 era5 关（drone 档位合法入流——E5 行 20/s 档）；
        // illegal — E5 rock_ratio>0（E1/E2/E4/E5 固定布局规则：矿位稀缺声明通道
        // 未随本批跨代扩展）。
        HandcraftedLevelSet set = HandcraftedLevelData.Parse(MinimalEra5LevelJson());
        HandcraftedLevel level = Assert.Single(set.Items);
        Assert.Equal(0.0, level.Map.RockRatio, 9);

        string rocky = MinimalEra5LevelJson().Replace("\"rock_ratio\": 0.0", "\"rock_ratio\": 0.1");
        Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(rocky));
    }

    public static TheoryData<string, string, string> IllegalEra5Mutations => new()
    {
        // illegal — E5 结构违规逐条点名拒绝（CS-3.5；每条规则一条故意违例）
        { "E5 转换器缺配方引用",
            "{ \"wave\": 1, \"verb\": \"build-converter\", \"converter_recipe\": \"era5:recipe-nano\", \"cell\": { \"x\": 2, \"y\": 2 } }",
            "{ \"wave\": 1, \"verb\": \"build-converter\", \"cell\": { \"x\": 2, \"y\": 2 } }" },
        { "未登记运输档位", "\"carrier_tier\": \"drone\"", "\"carrier_tier\": \"teleporter\"" },
        { "水比非零（era5 同禁）", "\"water_ratio\": 0.0", "\"water_ratio\": 0.1" },
        { "威胁度越带", "\"threat\": 0.3", "\"threat\": 1.5" },
        { "胜波与公式不符", "\"win_waves\": 13", "\"win_waves\": 14" },
        { "教学宽限非 ×3", "\"teaching_level\": false", "\"teaching_level\": true" },
        { "策略目标缺失", "\"strategy\": [\"campaign:obj-s1\"]", "\"strategy\": []" },
        { "命令波锚越胜波窗", "\"wave\": 1, \"verb\": \"build-labor-camp\"", "\"wave\": 99, \"verb\": \"build-labor-camp\"" },
        { "Boss 波与敌种不成对", "\"elite_line_enabled\": false",
            "\"elite_line_enabled\": false,\n \"boss_wave\": 13" },
    };

    [Theory]
    [MemberData(nameof(IllegalEra5Mutations))]
    public void Loader_rejects_each_era5_structural_violation_by_name(string caseName, string from, string to)
    {
        // illegal — 每条结构规则一条"故意违反应被拦截"（防恒绿）。
        string json = MinimalEra5LevelJson().Replace(from, to);
        var exception = Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(json));
        Assert.NotEqual("", exception.Message.Trim());
        Assert.Contains("campaign:level-e5-99", exception.Message); // 消息定位到关卡行
        Assert.True(caseName.Length > 0, $"caseName={caseName}");
    }
}
