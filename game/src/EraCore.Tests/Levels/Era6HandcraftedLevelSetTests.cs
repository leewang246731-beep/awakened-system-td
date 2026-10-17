using System.Text.Json;
using EraCore.Balance;
using EraCore.Logistics;
using EraCore.Units;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-19][C-02][F-055][F-018][F-060] T-135 终章手工关卡集（levels/e6.json
// 5 关=冻结排期前三关+后续关+终局关）数据回归：schema 装载即校验（era6 rock=0
// 固定布局规则）、前三关与 era6/levels.json（T-135 冻结排期）逐字段一致、能力
// 解锁链对位、三重目标实例化（SP-8 池+终章敌特性适配——堕落守望者画布 96×112/
// 相位突击体克减速语境）、命令流引用闭合（塔/敌/配方→era6 内容表；drone 20/s
// 干线=终章物流可玩面——传送阵 ∞ 档=era.json 数据位+TransportTiers.Teleporter
// 引擎档、时空塔=减速对策位首发）、终局关 Boss 行+纪元之门通关之门数据位
//（requires_wonder 与 WonderFinaleGateBoard/levels/e6.json 两侧同源）、
// era_transition final_levels 缝合边界（E5→终章引用=T-135 随终章实关行落位
// ——两侧行 id 相等即缝合；e6-final 不入 final_levels——键域 1..5 封闭）、
// 装载确定性。期望值全部锚在内容表/冻结公式（出处写在各断言处注释），解析或
// 校验失败即红。
public class Era6HandcraftedLevelSetTests
{
    private static HandcraftedLevelSet E6() => HandcraftedLevelData.Load("e6.json");

    private static HandcraftedLevel Level(int slot) =>
        E6().Items.Single(level => level.Slot == slot);

    // ---- 1) schema 装载与结构声明 [F-055] --------------------------------

    [Fact]
    public void E6_file_parses_and_declares_the_Level_family_schema()
    {
        // [F-055] levels/e6.json 解析+K-2 Level 族声明+首批 5 关（前三关=L6-1/2/3
        // 排期关+L6-4 后续关+e6-final 终局关，T-135 任务卡同构 e4/e5）。
        HandcraftedLevelSet set = E6();
        Assert.Equal("era6:era-finale", set.Era);
        Assert.Equal(5, set.Items.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, set.Items.Select(level => level.Slot).OrderBy(slot => slot).ToArray());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "game", "content", "levels")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        string raw = File.ReadAllText(Path.Combine(dir.FullName, "game", "content", "levels", "e6.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Matches("^urn:eratd:content:levels:1$", doc.RootElement.GetProperty("$schema").GetString()!);
        Assert.Equal("Level", doc.RootElement.GetProperty("content_type").GetString());
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Level", item.GetProperty("content_type").GetString());
        }

        // 关卡行归 campaign 命名空间（techtree/legacy 同源引用，F-156）
        Assert.All(set.Items, level => Assert.StartsWith("campaign:level-e6-", level.Id));
        Assert.Equal(set.Items.Count, set.Items.Select(level => level.Id).Distinct().Count());
    }

    [Fact]
    public void Threats_win_waves_and_graces_follow_the_frozen_formulas()
    {
        // [F-055] 胜波=10+⌊t×10⌋（§7）逐关复核：0.3/0.6/0.9/0.75/0.9→13/16/19/17/19；
        // 预算 15~40 分钟带；教学位仅 L6-1（宽限 ×3），其余 ×1.5~2（§6）。
        foreach (HandcraftedLevel level in E6().Items)
        {
            Assert.Equal(Campaign.WinWaves(level.Threat), level.WinWaves);
            Assert.InRange(level.Threat, 0.3, 1.2);
            Assert.InRange(level.BudgetSeconds, 900, 2400);
        }

        Assert.Equal(
            new[] { 0.3, 0.6, 0.9, 0.75, 0.9 },
            E6().Items.OrderBy(level => level.Slot).Select(level => level.Threat));
        Assert.Equal(
            new[] { 13, 16, 19, 17, 19 },
            E6().Items.OrderBy(level => level.Slot).Select(level => level.WinWaves));
        Assert.True(Level(1).TeachingLevel);
        Assert.All(E6().Items.Skip(1), level => Assert.False(level.TeachingLevel));
        Assert.All(E6().Items.Skip(1), level => Assert.InRange(level.FirstWaveGraceMultiplier, 1.5, 2.0));

        // 后续关威胁带（T-135 任务卡『后续 1~2 关』——L6-4=0.75 带内，e3-04~e5-04 后续关先例）
        Assert.InRange(Level(4).Threat, 0.75, 0.9);

        // 前三关威胁递进（0.3<0.6<0.9 → 胜波 13<16<19，§7——历代首三关同式）
        Assert.True(Level(1).Threat < Level(2).Threat && Level(2).Threat < Level(3).Threat);
        Assert.True(Level(1).WinWaves < Level(2).WinWaves && Level(2).WinWaves < Level(3).WinWaves);

        // 终局关=SP-9 邻接 2 网格点 0.9（e1~e5-final 同档）
        Assert.Equal(0.9, Level(5).Threat, 9);
        Assert.Equal(2, Level(5).AdjacentEnemyCamps!.Value);
    }

    // ---- 2) map 块与命令流引用闭合 [F-055] [F-023] -----------------------

    [Fact]
    public void Map_blocks_stay_on_the_fixed_layout_and_digs_bind_to_the_archaeology_slot()
    {
        // [F-055] 终章全关固定布局：water=0（装载器规则）、rock=0（E1/E2/E4/E5/E6
        // 规则——终章矿位稀缺声明通道=暗物质矿脉，随跨代扩展另落，本批零声明=
        // 零矿格）、forest=0.05（E3~E6 传承刷新档沿用）；考古 dig_sites 只在
        // L6-3（冻结排期 slot 3 focus=archaeology_hero_system，e2-03~e5-03 同位先例）。
        foreach (HandcraftedLevel level in E6().Items)
        {
            Assert.Equal(0.0, level.Map.WaterRatio, 9);
            Assert.Equal(0.05, level.Map.ForestRatio, 9);
            Assert.Equal(0.0, level.Map.RockRatio, 9);
        }

        Assert.Equal(2, Level(3).Map.DigSites.Count);
        Assert.All(E6().Items.Where(level => level.Slot != 3), level => Assert.Empty(level.Map.DigSites));
    }

    [Fact]
    public void Command_script_and_rosters_resolve_against_the_era6_content_tables()
    {
        // [F-055] 引用闭合：塔→era6/towers.json、敌种（含 Boss 行）→era6/enemies.json、
        // converter 配方→era6/recipes.json、初始资源→era.json 资源行（反物质=终章
        // 建造货币行，E1 木/E2 铜/E3 钢/E4 混凝土/E5 纳米/E6 反物质跨代递进）；
        // 运输档位=封闭档位表 trip 形（porter/cart/drone——drone 20/s=终章干线，
        // 传送阵 ∞ 档非 trip 形态不入 assign-carrier 表=e4 传送带 data-first 同款
        // 先例；e1~e5 命令流不得提前使用 drone——era6 前引守卫）。
        var towers = Towers("era6");
        var enemies = Enemies("era6");

        foreach (HandcraftedLevel level in E6().Items)
        {
            foreach (ScriptStep step in level.CommandScript)
            {
                if (step.Tower is not null)
                {
                    Assert.Contains(step.Tower, towers.Keys);
                }

                if (step.ConverterRecipe is not null)
                {
                    Assert.True(RecipeExists("era6", step.ConverterRecipe),
                        $"配方 {step.ConverterRecipe} 不在 era6/recipes.json");
                }
            }

            foreach (string enemy in level.Waves.RegularRoster)
            {
                Assert.Contains(enemy, enemies.Keys);
            }

            if (level.Waves.EliteEnemyId is not null)
            {
                Assert.Contains(level.Waves.EliteEnemyId!, enemies.Keys);
            }

            if (level.Waves.BossEnemyId is not null)
            {
                Assert.Contains(level.Waves.BossEnemyId!, enemies.Keys);
                Assert.True(enemies[level.Waves.BossEnemyId!].IsBoss, "Boss 行须为敌表 is_boss 行");
            }

            foreach (StockLine line in level.InitialStock)
            {
                Assert.Contains(line.Resource, EraResources("era6"));
            }
        }

        // e1~e5 关卡集不前引 era6 塔位（时代排期单向性；Era6ContentTests 同款文本扫描）
        var levelsDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (levelsDir is not null && !Directory.Exists(Path.Combine(levelsDir.FullName, "game", "content", "levels")))
        {
            levelsDir = levelsDir.Parent!;
        }

        foreach (var file in new[] { "e1.json", "e2.json", "e3.json", "e4.json", "e5.json" })
        {
            Assert.DoesNotContain("era6:",
                File.ReadAllText(Path.Combine(levelsDir!.FullName, "game", "content", "levels", file)));
        }
    }

    [Fact]
    public void Command_scripts_use_the_closed_carrier_tiers_with_drone_as_the_trunk()
    {
        // [F-042][F-055] 终章干线=drone 20/s（TransportTiers.Drone 引擎档位同值；
        // 传送阵=∞ 档数据位先行——非 trip 形态不入 assign-carrier 封闭档位表，
        // e4 传送带 data-first 同款先例）；档位值域封闭（装载器 CarrierTiers）。
        var tiers = new HashSet<string>();
        foreach (HandcraftedLevel level in E6().Items)
        {
            foreach (ScriptStep step in level.CommandScript.Where(step => step.CarrierTier is not null))
            {
                tiers.Add(step.CarrierTier!);
            }
        }

        Assert.Contains("drone", tiers); // 终章干线档在用
        Assert.Subset(
            new HashSet<string> { "porter", "cart", "mine-rail", "drone" },
            tiers); // ⊆ 封闭 trip 档位表（teleporter 不入表——非 trip 形态）
        Assert.DoesNotContain("teleporter", tiers);
        Assert.Equal(20.0, TransportTiers.Drone.ThroughputAnchorItemsPerSecond, 9); // 干线档位锚
        Assert.Equal(double.PositiveInfinity, TransportTiers.Teleporter.ThroughputAnchorItemsPerSecond); // 终章 ∞ 档引擎位
    }

    // ---- 3) 排期一致性：levels/e6.json ↔ era6/levels.json ↔ era_transition [F-012]

    [Fact]
    public void First_three_levels_match_the_frozen_schedule_rows_field_by_field()
    {
        // [F-012] 前三关行与 era6/levels.json（T-135 冻结排期侧）同 id 逐字段一致
        //（id/slot/focus/focus_zh/schedule_tier/邻接/威胁度/胜波/宽限/教学位/
        // 觉醒位/关名/解锁双列——排期一致性互证，e5 同款纪律）。
        var frozen = Era6ScheduleRows();
        for (int slot = 1; slot <= 3; slot++)
        {
            HandcraftedLevel level = Level(slot);
            JsonElement row = frozen[slot];

            Assert.Equal(GetString(row, "id"), level.Id);
            Assert.Equal(GetString(row, "era"), level.Era);
            Assert.Equal(GetInt(row, "slot"), level.Slot);
            Assert.Equal(GetString(row, "name_zh"), level.NameZh);
            Assert.Equal(GetString(row, "name_en"), level.NameEn);
            Assert.Equal(GetString(row, "focus"), level.Focus);
            Assert.Equal(GetString(row, "schedule_tier"), level.ScheduleTier);
            Assert.Equal(GetInt(row, "adjacent_enemy_camps"), level.AdjacentEnemyCamps!.Value);
            Assert.Equal(GetDouble(row, "threat"), level.Threat, 9);
            Assert.Equal(GetInt(row, "win_waves"), level.WinWaves);
            Assert.Equal(GetDouble(row, "first_wave_grace_multiplier"), level.FirstWaveGraceMultiplier, 9);
            Assert.Equal(GetBool(row, "teaching_level"), level.TeachingLevel);
            Assert.Equal(GetBool(row, "awakening_cinematic"), level.AwakeningCinematic);

            var actions = row.GetProperty("unlocks").GetProperty("actions").EnumerateArray()
                .Select(a => a.GetString()!).ToList();
            var features = row.GetProperty("unlocks").GetProperty("features").EnumerateArray()
                .Select(f => f.GetString()!).ToList();
            Assert.Equal(actions, level.Unlocks.Actions.ToList());
            Assert.Equal(features, level.Unlocks.Features.ToList());
        }
    }

    private static string GetString(JsonElement el, string name) => el.GetProperty(name).GetString()!;

    private static int GetInt(JsonElement el, string name) => el.GetProperty(name).GetInt32();

    private static double GetDouble(JsonElement el, string name) => el.GetProperty(name).GetDouble();

    private static bool GetBool(JsonElement el, string name) => el.GetProperty(name).GetBoolean();

    // ---- 4) 能力解锁链与三重目标 [F-092] [F-054] --------------------------

    [Fact]
    public void Unlock_chain_and_objectives_instantiate_the_SP8_pools()
    {
        // [F-092][F-054] 解锁链逐关对位（era6/levels.json 同源）+三重目标=SP-8 池
        // 引用（e5 同款纪律：slot1 零点能/slot2 传送阵/slot3 考古+英雄+真相揭示/
        // slot4 空/slot5 空）。
        var (strategy, challenge) = ObjectivePools();

        Assert.Equal(new[] { "zero_point_energy" }, Level(1).Unlocks.Features);
        Assert.Equal(new[] { "teleporter_logistics" }, Level(2).Unlocks.Features);
        Assert.Equal(
            new[] { "archaeology", "hero_recruit", "truth_revelation" },
            Level(3).Unlocks.Features.Order().ToList());
        Assert.All(E6().Items, level => Assert.Empty(level.Unlocks.Actions));
        Assert.Empty(Level(4).Unlocks.Features);
        Assert.Empty(Level(5).Unlocks.Features);

        // 英雄行=章节设计待定案（era6 无 heroes.json 行；hero_id/hero_cell 成对缺席）
        Assert.All(E6().Items, level => Assert.Null(level.HeroId));
        Assert.All(E6().Items, level => Assert.Null(level.Map.HeroCell));

        // 非觉醒位不带钩子（era6/levels.json awakening_cinematic 恒 false——
        // §10.4 觉醒演出=E2 第一关专属锚）
        Assert.All(E6().Items, level => Assert.False(level.AwakeningCinematic));
        Assert.All(E6().Items, level => Assert.Empty(level.CinematicHooks));

        foreach (HandcraftedLevel level in E6().Items)
        {
            Assert.NotEmpty(level.Objectives.Strategy);
            Assert.NotEmpty(level.Objectives.Challenge);
            Assert.All(level.Objectives.Strategy, id => Assert.Contains(id, strategy));
            Assert.All(level.Objectives.Challenge, id => Assert.Contains(id, challenge));
        }
    }

    // ---- 5) 终局关 Boss 行 + 纪元之门通关之门 + 跃迁缝合边界 [F-033][F-060][F-003]

    [Fact]
    public void Final_boss_row_carries_the_gate_of_eras_and_the_finale_jump_boundary()
    {
        // [F-060][F-033][F-055] 通关之门语义（T-135）：e6-final=全游戏终局关
        //（堕落守望者 Boss 关），requires_wonder=campaign:wonder-era-gate（附录
        // F.3 奇观链第六环『纪元之门（终章，通关之门）』——建成后终局关可通的
        // 跃迁硬条件语义终点形态）；终章无后继跃迁——era_transition final_levels
        // 键域 1..5 封闭，e6-final 不入表（E5→终章键 5 落位指向 campaign:level-e5-final
        // =T-135 随终章实关行落位，两侧行 id 相等即缝合）。
        var transition = EraCore.Tests.Legacy.LegacyFixture.Transition();
        Assert.Equal("campaign:level-e5-final", transition.FinalLevels[5]); // E5→终章（第五跳）硬前置
        Assert.Equal(5, transition.FinalLevels.Count); // 键域 1..5 封闭：无 E6 键（无第七代）
        Assert.DoesNotContain(transition.FinalLevels.Values, id => id == "campaign:level-e6-final");

        var bossRows = E6().Items.Where(l => l.Waves.BossEnemyId is not null).ToList();
        HandcraftedLevel final = Assert.Single(bossRows); // 每文件恰一 Boss 行
        Assert.Equal("campaign:level-e6-final", final.Id);
        Assert.Equal("era6:enemy-boss", final.Waves.BossEnemyId);
        Assert.Equal(final.WinWaves, final.Waves.BossWave); // Boss 波=胜波（终局一搏）
        Assert.Equal(19, final.WinWaves); // 威胁 0.9 → §7
        Assert.Equal(0.9, final.Threat, 9);

        // 通关之门数据位：requires_wonder=纪元之门（唯一携带者=e6-final；与
        // wonders.json finale_gate 行同 id——消费面 WonderFinaleGateBoardTests）
        Assert.Equal("campaign:wonder-era-gate", final.RequiresWonder);
        Assert.All(E6().Items.Where(level => level.Slot != 5), level => Assert.Null(level.RequiresWonder));
        var wonders = EraCore.Tests.Legacy.LegacyFixture.Wonders();
        var gate = wonders.Single(w => w.BonusKind == "finale_gate");
        Assert.Equal("campaign:wonder-era-gate", gate.Id);
        Assert.Equal(6, gate.EraIndex); // 附录 F.3 奇观链第六环（终章）
        Assert.Equal(final.RequiresWonder, gate.Id); // 关行与奇观行两侧同 id

        // Boss 敌种解析到 era 敌表的 is_boss 行（enemies.json e6_boss 堕落守望者）
        var enemies = Enemies("era6");
        Assert.True(enemies["era6:enemy-boss"].IsBoss, "era6:enemy-boss 须为敌表 is_boss 行");
        Assert.Equal(120000, enemies["era6:enemy-boss"].Hp); // §4 L63 逐字锚点
        Assert.Equal(40, enemies["era6:enemy-boss"].Armor);  // §4 L63 逐字锚点

        // e5.json 侧缝合：e5-final=e5 文件恰一 Boss 行=final_levels[5] 引用值
        var e5BossRows = HandcraftedLevelData.Load("e5.json").Items
            .Where(l => l.Waves.BossEnemyId is not null).ToList();
        Assert.Equal("campaign:level-e5-final", Assert.Single(e5BossRows).Id);
    }

    [Fact]
    public void Spacetime_counter_theme_stays_bound_to_the_slow_countered_fast_line()
    {
        // [F-018] 相位突击体对策语境（任务卡）：时空塔=减速类控制场，首发且仅发
        // 于 L6-3（era6:tower-spacetime 的可玩面）；对位敌=克减速行的快速线
        //（相位突击体/熵蚀幽体——本关 roster 成员）；其余关不混用（e5 力场塔
        // 只在 L5-4 的同款纪律）。
        Assert.Contains(Level(3).CommandScript,
            step => step.Verb == "build-tower" && step.Tower == "era6:tower-spacetime");
        Assert.All(E6().Items.Where(level => level.Slot != 3),
            level => Assert.DoesNotContain(level.CommandScript,
                step => step.Verb == "build-tower" && step.Tower == "era6:tower-spacetime"));

        Assert.Contains("era6:enemy-phase-striker", Level(3).Waves.RegularRoster);
        var phase = Enemies("era6")["era6:enemy-phase-striker"];
        Assert.Equal(2.5, phase.SpeedPxPerTick * 30.0 / 32.0, 2); // 快速位 2.5 格/s（SP-3 换算式）
    }

    // ---- 6) 装载确定性 + era6 装载器边界/非法（CS-3.1） -------------------

    [Fact]
    public void Double_load_of_e6_json_is_structurally_identical()
    {
        // [F-055] 确定性①：同文件双装载 → 序列化快照逐字节全等。
        HandcraftedLevelSet first = HandcraftedLevelData.Load("e6.json");
        HandcraftedLevelSet second = HandcraftedLevelData.Load("e6.json");
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void Mutated_e6_json_rows_fail_the_loader_with_field_pathed_messages()
    {
        // illegal — [F-055] 负向突变（装载即校验证据，CS-3.1）：胜负波不符/
        // 命令引未建造实体/波表 boss 行不成对——各自点名拒绝。
        string raw = File.ReadAllText(Path.Combine(FindLevelsDir(), "game", "content", "levels", "e6.json"));

        // ①胜波与 §7 公式不符
        string badWin = raw.Replace("\"win_waves\": 19,\n      \"first_wave_grace_multiplier\": 1.5,\n      \"budget_seconds\": 1800",
            "\"win_waves\": 18,\n      \"first_wave_grace_multiplier\": 1.5,\n      \"budget_seconds\": 1800");
        Assert.NotEqual(raw, badWin);
        Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(badWin));

        // ②assign-carrier 引用未建造实体（序号越界）
        string badEntity = raw.Replace(
            "{ \"wave\": 4, \"verb\": \"assign-carrier\", \"from_entity\": 11, \"to_entity\": 13, \"carrier_tier\": \"drone\" }",
            "{ \"wave\": 4, \"verb\": \"assign-carrier\", \"from_entity\": 11, \"to_entity\": 99, \"carrier_tier\": \"drone\" }");
        Assert.NotEqual(raw, badEntity);
        Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(badEntity));

        // ③e6 build-converter 缺 converter_recipe（E2+ 闭表守卫的 era6 扩展面）
        string badConverter = raw.Replace(
            "{ \"wave\": 12, \"verb\": \"build-converter\", \"converter_recipe\": \"era6:recipe-singularity-shell\", \"cell\": { \"x\": 16, \"y\": 16 } }",
            "{ \"wave\": 12, \"verb\": \"build-converter\", \"cell\": { \"x\": 16, \"y\": 16 } }");
        Assert.NotEqual(raw, badConverter);
        Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(badConverter));

        // ④未登记命令动词（封闭动词表）
        string badVerb = raw.Replace("\"verb\": \"build-tower\", \"tower\": \"era6:tower-singularity\", \"cell\": { \"x\": 14, \"y\": 22 }",
            "\"verb\": \"build-teleporter\", \"cell\": { \"x\": 14, \"y\": 22 }");
        Assert.NotEqual(raw, badVerb);
        Assert.Throws<HandcraftedLevelSchemaException>(() => HandcraftedLevelData.Parse(badVerb));
    }

    private static string FindLevelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "game", "content", "levels")))
        {
            dir = dir.Parent!;
        }

        return dir!.FullName;
    }
}
