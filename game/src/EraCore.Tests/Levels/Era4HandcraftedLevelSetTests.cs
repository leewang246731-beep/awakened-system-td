using System.Text.Json;
using EraCore.Balance;
using EraCore.Contracts.Foundation;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][F-055][F-060][F-092][F-012] T-124 E4 手工关卡集（levels/e4.json 首批
// 5 关=冻结排期前三关+后续关+终局关）数据回归：schema 装载即校验（era4 rock=0
// 固定布局规则）、前三关与 era4/levels.json（T-123 冻结排期）逐字段一致、能力
// 解锁链对位、三重目标实例化（SP-8 池+E4 敌特性适配——夜枭隐形反制语境）、命令
// 流引用闭合（塔/敌/配方→era4 内容表；mine-rail 干线、传送带/卡车=解锁位数据面
// 的诚实披露）、终局关 Boss 行与 era_transition final_levels[4] 双向缝合、装载
// 确定性。期望值全部锚在内容表/冻结公式（出处写在各断言处注释），解析或校验
// 失败即红。
public class Era4HandcraftedLevelSetTests
{
    private static HandcraftedLevelSet E4() => HandcraftedLevelData.Load("e4.json");

    private static HandcraftedLevel Level(int slot) =>
        E4().Items.Single(level => level.Slot == slot);

    // ---- 1) schema 装载与结构声明 [F-055] --------------------------------

    [Fact]
    public void E4_file_parses_and_declares_the_Level_family_schema()
    {
        // [F-055] levels/e4.json 解析+K-2 Level 族声明+首批 5 关（前三关=L4-1/2/3
        // 排期关+L4-4 后续关+e4-final 终局关，T-124 任务卡『4~5 关』上沿）。
        HandcraftedLevelSet set = E4();
        Assert.Equal("era4:era-modern", set.Era);
        Assert.Equal(5, set.Items.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, set.Items.Select(level => level.Slot).OrderBy(slot => slot).ToArray());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "game", "content", "levels")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        string raw = File.ReadAllText(Path.Combine(dir.FullName, "game", "content", "levels", "e4.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Matches("^urn:eratd:content:levels:1$", doc.RootElement.GetProperty("$schema").GetString()!);
        Assert.Equal("Level", doc.RootElement.GetProperty("content_type").GetString());
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Level", item.GetProperty("content_type").GetString());
        }

        // 关卡行归 campaign 命名空间（techtree/legacy 同源引用，F-156）
        Assert.All(set.Items, level => Assert.StartsWith("campaign:level-e4-", level.Id));
        Assert.Equal(set.Items.Count, set.Items.Select(level => level.Id).Distinct().Count());
    }

    [Fact]
    public void Threats_win_waves_and_graces_follow_the_frozen_formulas()
    {
        // [F-055] 胜波=10+⌊t×10⌋（§7）逐关复核：0.3/0.6/0.9/0.75/0.9→13/16/19/17/19；
        // 预算 15~40 分钟带；教学位仅 L4-1（宽限 ×3），其余 ×1.5~2（§6）。
        foreach (HandcraftedLevel level in E4().Items)
        {
            Assert.Equal(Campaign.WinWaves(level.Threat), level.WinWaves);
            Assert.InRange(level.Threat, 0.3, 1.2);
            Assert.InRange(level.BudgetSeconds, 900, 2400);
        }

        Assert.Equal(
            new[] { 0.3, 0.6, 0.9, 0.75, 0.9 },
            E4().Items.OrderBy(level => level.Slot).Select(level => level.Threat));
        Assert.Equal(
            new[] { 13, 16, 19, 17, 19 },
            E4().Items.OrderBy(level => level.Slot).Select(level => level.WinWaves));
        Assert.True(Level(1).TeachingLevel);
        Assert.All(E4().Items.Skip(1), level => Assert.False(level.TeachingLevel));
        Assert.All(E4().Items.Skip(1), level => Assert.InRange(level.FirstWaveGraceMultiplier, 1.5, 2.0));

        // 后续关威胁带（T-124 任务卡『后续 1~2 关』——L4-4=0.75 带内，e3-04 后续关先例）
        Assert.InRange(Level(4).Threat, 0.75, 0.9);

        // 前三关威胁递进（0.3<0.6<0.9 → 胜波 13<16<19，§7）
        Assert.True(Level(1).Threat < Level(2).Threat && Level(2).Threat < Level(3).Threat);
        Assert.True(Level(1).WinWaves < Level(2).WinWaves && Level(2).WinWaves < Level(3).WinWaves);

        // 终局关=SP-9 邻接 2 网格点 0.9（e1/e2/e3-final 同档）
        Assert.Equal(0.9, Level(5).Threat, 9);
        Assert.Equal(2, Level(5).AdjacentEnemyCamps!.Value);
    }

    // ---- 2) map 块与命令流引用闭合 [F-055] [F-023] -----------------------

    [Fact]
    public void Map_blocks_stay_on_the_fixed_layout_and_digs_bind_to_the_archaeology_slot()
    {
        // [F-055] E4 全关固定布局：water=0（装载器规则）、rock=0（E1/E2/E4 规则——
        // E4 矿位稀缺声明通道随 T-115 §四跨代扩展另落，本批零声明=零矿格）、
        // forest=0.05（E3 传承刷新档沿用）；考古 dig_sites 只在 L4-3（冻结排期
        // slot 3 focus=archaeology_hero_system，e2-03/e3-03 同位先例）。
        foreach (HandcraftedLevel level in E4().Items)
        {
            Assert.Equal(0.0, level.Map.WaterRatio, 9);
            Assert.Equal(0.05, level.Map.ForestRatio, 9);
            Assert.Equal(0.0, level.Map.RockRatio, 9);
        }

        Assert.Equal(2, Level(3).Map.DigSites.Count);
        Assert.All(E4().Items.Where(level => level.Slot != 3), level => Assert.Empty(level.Map.DigSites));
    }

    [Fact]
    public void Command_script_and_rosters_resolve_against_the_era4_content_tables()
    {
        // [F-055] 引用闭合：塔→era4/towers.json、敌种（含 Boss 行）→era4/enemies.json、
        // converter 配方→era4/recipes.json、初始资源→era.json 资源行（混凝土=E4
        // 建造货币行，E1 木/E2 铜/E3 钢/E4 混凝土跨代递进）；运输档位=封闭档位表
        // trip 形（porter/cart/mine-rail——传送带=独立动词、卡车=数据位先行，e4.json
        // 注记的诚实披露面）；drone（E5 行 20/s 档）不得提前入 E4 命令流。
        var towers = Towers("era4");
        var enemies = Enemies("era4");

        foreach (HandcraftedLevel level in E4().Items)
        {
            foreach (ScriptStep step in level.CommandScript)
            {
                if (step.Tower is not null)
                {
                    Assert.Contains(step.Tower, towers.Keys);
                }

                if (step.ConverterRecipe is not null)
                {
                    Assert.True(RecipeExists("era4", step.ConverterRecipe),
                        $"{level.Id}: 配方 {step.ConverterRecipe} 未见于 era4/recipes.json");
                }

                if (step.CarrierTier is not null)
                {
                    Assert.Contains(step.CarrierTier, HandcraftedLevelData.CarrierTiers);
                    Assert.NotEqual("drone", step.CarrierTier); // E5 档不提前（物流阶梯 §5 L78）
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
                Assert.Contains(line.Resource, EraResources("era4"));
            }
        }

        // 传承刷新资源以采集原料身份入命令流（『精确打击本代化』旧资源=制导头/
        // 推进剂/战斗部壳体，T-115 §二裁定延续）：铁（era2 命名空间）+煤（era3
        // 命名空间）采集器存在、era3:ammo-* 不见于任何 e4 引用
        string raw = File.ReadAllText(Path.Combine(FindLevelsDir(), "e4.json"));
        Assert.Contains("\"era2:res-iron\"", raw);
        Assert.Contains("\"era3:res-coal\"", raw);
        Assert.DoesNotContain("era3:ammo-", raw);
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

    /// <summary>The aliases of one authored e4.json row (raw-JSON face; the parsed record carries no aliases).</summary>
    private static IReadOnlyList<string> AuthoredAliases(string levelId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindLevelsDir(), "e4.json")));
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
        // ≥ 脚本塔价+升级差价合计；授予 ≤ 仓库池上限（F.3 基 2000×era4 仓库等级
        // 4=8000）；E4 无英雄行（era4 无 heroes.json——hero-order 不得出现）。
        var towers = Towers("era4");
        foreach (HandcraftedLevel level in E4().Items)
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
            Assert.True(grant <= 2000.0 * WarehouseLevel("era4"), $"{level.Id}: 授予 {grant} 超过仓库池上限");
        }

        // e4-final 升级差价覆盖（导弹塔 810→激光塔 2430：差额 1620=TowerEstateOps
        // DifferentialCost 口径）——授予 6000 ≥ 塔 3240+升级 1620=4860
        HandcraftedLevel final = Level(5);
        Assert.Equal(6000.0, final.InitialStock.Sum(line => line.Amount), 9);
        Assert.Contains(final.CommandScript, step => step.Verb == "upgrade-tower" && step.ToTier == 2);
    }

    // ---- 3) 三重目标实例化（SP-8 池 + E4 敌特性适配） [F-055] -------------

    [Fact]
    public void Every_level_instantiates_survival_plus_distinct_strategy_and_challenge_objectives()
    {
        // [F-055] 每关 ≥1 策略+1 挑战目标实例（生存=结构性），实例两两不同；
        // ★ 难位旗标对齐池行（c1/c2/c3/c4 hard）。E4 敌特性适配（任务卡）：终局
        // 关（夜枭 traits.stealth 隐形 Boss）的挑战=c4 不使用系统建造——纯玩家
        // 命令流对位『反制语境』（雷达=唯一反制数据位、互联网奇观=全图共享解法
        // 面，池内无隐形专属目标行——语境物化在 Boss 行+奇观通道，非目标池自造）。
        (IReadOnlyList<string> strategyPool, IReadOnlyList<string> challengePool) = ObjectivePools();
        var challengeHard = ChallengeHardFlags();

        var seenInstances = new HashSet<string>();
        foreach (HandcraftedLevel level in E4().Items)
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

        // E4 语境锚（任务卡『隐形反制挑战』）：终局关挑战=c4（不使用系统建造）
        HandcraftedLevel final = Level(5);
        Assert.Contains("campaign:obj-c4", final.Objectives.Challenge);
    }

    [Fact]
    public void Limit_challenge_objectives_are_satisfiable_by_the_level_shape()
    {
        // [F-055] 限塔挑战（c3 ≤8）与脚本塔数一致——目标不是空头支票；
        // L4-4 雷场=面封锁利基行（era4:tower-mine tier_line=specialization）
        // 的首发位（e4.json _source 同锚）。
        var towers = Towers("era4");
        foreach (HandcraftedLevel level in E4().Items)
        {
            if (level.Objectives.Challenge.Contains("campaign:obj-c3"))
            {
                int towerCount = level.CommandScript.Count(step => step.Verb == "build-tower");
                Assert.True(towerCount <= 8, $"{level.Id}: 脚本塔数 {towerCount} > c3 限 8");
            }
        }

        HandcraftedLevel minefield = Level(4);
        Assert.Contains(minefield.CommandScript, step => step.Verb == "build-tower"
            && towers[step.Tower!].Id == "era4:tower-mine");
        Assert.All(E4().Items.Where(level => level.Slot != 4),
            level => Assert.DoesNotContain(level.CommandScript,
                step => step.Verb == "build-tower" && towers[step.Tower!].Id == "era4:tower-mine"));
    }

    // ---- 4) 前三关排期一致性（T-123 冻结元数据） [F-092] [F-012] ----------

    [Fact]
    public void First_three_rows_match_the_frozen_era4_levels_schedule_row_by_row()
    {
        // [F-092][F-012] 排期一致性断言：levels/e4.json 前三行与 era4/levels.json
        //（T-123 冻结排期侧）逐字段一致——id/slot/focus/schedule_tier/邻接/威胁/
        // 胜波/宽限/教学/觉醒/关名/别名/解锁双列，任何一侧漂移即红；L4-4/e4-final
        // 不在冻结表（后续关+终局关——era4/levels.json 恰 3 行）。
        var schedule = Era4ScheduleRows();
        Assert.Equal(3, schedule.Count);
        foreach (HandcraftedLevel level in E4().Items.Where(level => level.Slot <= 3))
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

            // SP-9：E4 行威胁度可由邻接敌营派生（手工行与程序口径合流）
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
    public void E4_capability_unlock_chain_matches_the_schedule_focus_of_each_slot()
    {
        // [F-092][F-012] 能力解锁链逐关对位冻结排期：L4-1 电网+过热/熔毁风险
        //（教学位）、L4-2 传送带+卡车物流、L4-3 考古+英雄招募+多核巡逻（托管级
        // 末档位）；19 动作清单已于 E2 全解锁 ⇒ E4 各关 unlocks.actions 恒空
        //（era4/levels.json 注）；L4-4/e4-final 无新增解锁位（e3-04/e1-final 先例）。
        Assert.Equal(
            new[] { "grid_energy", "meltdown_risk", "overheat_risk" }.Order().ToList(),
            Level(1).Unlocks.Features.Order().ToList());
        Assert.Equal(new[] { "belt_logistics", "truck_logistics" }.Order().ToList(), Level(2).Unlocks.Features.Order().ToList());
        Assert.Equal(
            new[] { "archaeology", "hero_recruit", "multicore_patrol" }.Order().ToList(),
            Level(3).Unlocks.Features.Order().ToList());
        Assert.All(E4().Items, level => Assert.Empty(level.Unlocks.Actions));
        Assert.Empty(Level(4).Unlocks.Features);
        Assert.Empty(Level(5).Unlocks.Features);

        // 英雄行=章节设计待定案（era4 无 heroes.json 行；hero_id/hero_cell 成对缺席）
        Assert.All(E4().Items, level => Assert.Null(level.HeroId));
        Assert.All(E4().Items, level => Assert.Null(level.Map.HeroCell));

        // 非觉醒位不带钩子（era4/levels.json awakening_cinematic 恒 false——
        // §10.4 觉醒演出=E2 第一关专属锚）
        Assert.All(E4().Items, level => Assert.False(level.AwakeningCinematic));
        Assert.All(E4().Items, level => Assert.Empty(level.CinematicHooks));
    }

    // ---- 5) 终局关 Boss 行 + 跃迁前置缝合（era_transition ↔ 关卡集） ------

    [Fact]
    public void Final_boss_row_carries_the_boss_wave_and_matches_the_era_transition_ids()
    {
        // [F-003][F-055][F-060] 跃迁前置缝合：era_transition.json final_levels{3,4}
        // 引用的终局关 id 必须真实存在于 levels/e3.json+e4.json（T-125 只落了
        // E3 行引用、T-124 落实关行——本断言钉死两侧行 id 双向相等，a58c86f
        // 『引用先行、实关缺失』教训的防复发面）；每文件恰一 Boss 行，Boss 波=
        // 胜波（终局一搏），Boss 敌种=era 敌表 is_boss 行且威胁=SP-9 网格点 0.9。
        var transition = EraCore.Tests.Legacy.LegacyFixture.Transition();
        Assert.Equal("campaign:level-e3-final", transition.FinalLevels[3]); // T-125 行（E3→E4 硬前置）
        Assert.Equal("campaign:level-e4-final", transition.FinalLevels[4]); // T-124 行（E4→E5 引用先行+实关同落）

        var bossRows = E4().Items.Where(l => l.Waves.BossEnemyId is not null).ToList();
        HandcraftedLevel final = Assert.Single(bossRows); // 每文件恰一 Boss 行
        Assert.Equal("campaign:level-e4-final", final.Id); // id=era_transition 引用值（缝合点）
        Assert.Equal(final.Id, transition.FinalLevels[4]);
        Assert.Equal("era4:enemy-boss", final.Waves.BossEnemyId);
        Assert.Equal(final.WinWaves, final.Waves.BossWave); // Boss 波=胜波（终局一搏）
        Assert.Equal(19, final.WinWaves); // 威胁 0.9 → §7
        Assert.Equal(0.9, final.Threat, 9);

        // Boss 敌种解析到 era 敌表的 is_boss 行（enemies.json e4_boss 夜枭）
        var enemies = Enemies("era4");
        Assert.True(enemies["era4:enemy-boss"].IsBoss, "era4:enemy-boss 须为敌表 is_boss 行");

        // E3 侧缝合：e3.json 同款恰一 Boss 行=e3-final（T-125 引用的实关行）
        var e3BossRows = HandcraftedLevelData.Load("e3.json").Items
            .Where(l => l.Waves.BossEnemyId is not null).ToList();
        Assert.Equal("campaign:level-e3-final", Assert.Single(e3BossRows).Id);
    }

    [Fact]
    public void Stealth_counter_hooks_stay_cross_referenced_between_boss_and_radar_rows()
    {
        // [F-060] 夜枭隐形反制语境（任务卡）：era4:enemy-boss traits.stealth
        //（invisible_to_targeting+countered_by_radar）与 era4:tower-radar
        // traits.reveal_stealth 两行同源互指（era4/towers.json _source 同锚）——
        // 互联网奇观（E4，雷达全图共享）的建成效果通道=Legacy.WonderRadarSharingBoard
        //（效果断言见 WonderRadarSharingBoardTests）。雷达为零伤支援行：不入
        // 任何命令流（引擎 build-tower 通道拒绝无武器循环塔行——e4.json 注记的
        // 诚实披露面，此处为内容侧守卫）。
        string towersRaw = File.ReadAllText(Path.Combine(
            Directory.GetParent(FindLevelsDir())!.FullName, "era4", "towers.json"));
        using (var doc = JsonDocument.Parse(towersRaw))
        {
            JsonElement radar = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "era4:tower-radar");
            Assert.True(radar.GetProperty("traits").GetProperty("reveal_stealth").GetBoolean());
            Assert.Equal(0, radar.GetProperty("dps").GetDouble(), 9); // 零伤支援行
        }

        string enemiesRaw = File.ReadAllText(Path.Combine(
            Directory.GetParent(FindLevelsDir())!.FullName, "era4", "enemies.json"));
        using (var doc = JsonDocument.Parse(enemiesRaw))
        {
            JsonElement stealth = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "era4:enemy-boss")
                .GetProperty("traits").GetProperty("stealth");
            Assert.True(stealth.GetProperty("invisible_to_targeting").GetBoolean());
            Assert.True(stealth.GetProperty("countered_by_radar").GetBoolean());
        }

        Assert.All(E4().Items, level => Assert.DoesNotContain(level.CommandScript,
            step => step.Verb == "build-tower" && step.Tower == "era4:tower-radar"));
    }

    // ---- 6) 装载确定性 + era4 装载器边界/非法（CS-3.1） -------------------

    [Fact]
    public void Double_load_of_e4_json_is_structurally_identical()
    {
        // [F-055] 确定性①：同文件双装载 → 序列化快照逐字节全等。
        HandcraftedLevelSet first = HandcraftedLevelData.Load("e4.json");
        HandcraftedLevelSet second = HandcraftedLevelData.Load("e4.json");
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    private static string MinimalEra4LevelJson() => """
        {
          "$schema": "urn:eratd:content:levels:1",
          "content_type": "Level",
          "era": "era4:era-modern",
          "items": [
            {
              "id": "campaign:level-e4-99",
              "content_type": "Level",
              "era": "era4:era-modern",
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
              "initial_stock": [ { "resource": "era4:res-concrete", "amount": 100.0 } ],
              "waves": {
                "base_count": 6,
                "begin_wave": 2,
                "spacing": 2,
                "scaling": 2.0,
                "batches_per_wave": 2,
                "valley_seconds": 15.0,
                "regular_roster": ["era4:enemy-militia"],
                "elite_line_enabled": false
              },
              "command_script": [
                { "wave": 1, "verb": "build-labor-camp", "cell": { "x": 1, "y": 1 } },
                { "wave": 1, "verb": "build-converter", "converter_recipe": "era4:recipe-electronics", "cell": { "x": 2, "y": 2 } },
                { "wave": 1, "verb": "build-tower", "tower": "era4:tower-missile", "cell": { "x": 3, "y": 3 } },
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
    public void Loader_accepts_the_minimal_era4_level_and_rejects_the_rock_declaration()
    {
        // [F-055] legal — 最小合法 era4 关；illegal — E4 rock_ratio>0（E1/E2/E4
        // 固定布局规则：矿位稀缺声明通道未随本批跨代扩展）。
        HandcraftedLevelSet set = HandcraftedLevelData.Parse(MinimalEra4LevelJson());
        HandcraftedLevel level = Assert.Single(set.Items);
        Assert.Equal(0.0, level.Map.RockRatio, 9);

        string rocky = MinimalEra4LevelJson().Replace("\"rock_ratio\": 0.0", "\"rock_ratio\": 0.1");
        Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(rocky));
    }

    public static TheoryData<string, string, string> IllegalEra4Mutations => new()
    {
        // illegal — E4 结构违规逐条点名拒绝（CS-3.5；每条规则一条故意违例）
        { "E4 转换器缺配方引用",
            "{ \"wave\": 1, \"verb\": \"build-converter\", \"converter_recipe\": \"era4:recipe-electronics\", \"cell\": { \"x\": 2, \"y\": 2 } }",
            "{ \"wave\": 1, \"verb\": \"build-converter\", \"cell\": { \"x\": 2, \"y\": 2 } }" },
        { "未登记运输档位", "\"carrier_tier\": \"porter\"", "\"carrier_tier\": \"truck\"" },
        { "水比非零（era4 同禁）", "\"water_ratio\": 0.0", "\"water_ratio\": 0.1" },
        { "威胁度越带", "\"threat\": 0.3", "\"threat\": 1.5" },
        { "胜波与公式不符", "\"win_waves\": 13", "\"win_waves\": 14" },
        { "教学宽限非 ×3", "\"teaching_level\": false", "\"teaching_level\": true" },
        { "策略目标缺失", "\"strategy\": [\"campaign:obj-s1\"]", "\"strategy\": []" },
        { "命令波锚越胜波窗", "\"wave\": 1, \"verb\": \"build-labor-camp\"", "\"wave\": 99, \"verb\": \"build-labor-camp\"" },
        { "Boss 波与敌种不成对", "\"elite_line_enabled\": false",
            "\"elite_line_enabled\": false,\n \"boss_wave\": 13" },
    };

    [Theory]
    [MemberData(nameof(IllegalEra4Mutations))]
    public void Loader_rejects_each_era4_structural_violation_by_name(string caseName, string from, string to)
    {
        // illegal — 每条结构规则一条"故意违反应被拦截"（防恒绿）。
        string json = MinimalEra4LevelJson().Replace(from, to);
        var exception = Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(json));
        Assert.NotEqual("", exception.Message.Trim());
        Assert.Contains("campaign:level-e4-99", exception.Message); // 消息定位到关卡行
        Assert.True(caseName.Length > 0, $"caseName={caseName}");
    }
}
