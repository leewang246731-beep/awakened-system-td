using System.Text.Json;
using EraCore.Balance;
using EraCore.Combat;
using EraCore.Legacy;
using EraCore.Logistics;
using EraCore.Units;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Regression;

// [F-172] T-122 无头数值回归 E3 扩展——T-083 先例同法（只加不改：
// NumericRegressionTests/EndlessRegressionTests 原样保持，E3 面全部落本文件）：
// E3 冻结数值面参数化进 Theory，ACTUAL 一律从 content JSON（生产解析器装载）
// + Balance/Legacy/Logistics 公式真算得出，期望值逐行注明文档出处
// （v3.2 §4 L60 / §4 跨代资源行 / §6 护盾 / SP-1b L46~L48 / SP-4 L146 /
// 附录 F.3 / era3 内容行 _source 推导式）。与 Era3ContentTests（T-112 内容
// 侧）与 Era3WaveShieldTests（T-114 行为侧）的分工：那边各自钉装载/实装，
// 这里钉回归数值面——内容表或公式任一侧漂移都会在本套件红（无头回归 CI
// 口径，T-098 同判据）。三行纪律（CS-3.1）以 legal/boundary/illegal 注释节
// 标注；[NEW] 标注沿内容表同款（11-spec-tables L7 新发明数值一律标注）。
public class Era3NumericRegressionTests
{
    // ------------------------------------------------ E3 塔三行（§4 L60 + SP-1b L46~L48 + F.5 类推锚带 [NEW]）

    [Theory] // legal — T-112 三行冻结值：成本|DPS|射程（era3/towers.json 行值；
             // §4 L60 塔防特色=加特林/臼炮/铁甲墙，定类=SP-1b L46~L48 预归类经
             // T-112 复核追认；F.5 基准表仅钉 E2 行——E3 数值为 F.5 类推锚带
             // [NEW]：加特林 270=90×3 跨代基点步、DPS 240=E2 标准线 T4 终点、
             // 射程 8.0=F.5 箭楼线 T2 位值；臼炮 810=270×3/DPS 480=240×2 严格
             // 阶梯、射程 11.0=F.5 弩炮 T3 特化位；铁甲墙 120/0/0=纯阻挡零伤害
    [InlineData("era3:tower-gatling", 1, "pierce", 270, 240.0, 8.0)]
    [InlineData("era3:tower-mortar", 2, "splash", 810, 480.0, 11.0)]
    [InlineData("era3:tower-ironclad-wall", 1, "support", 120, 0.0, 0.0)]
    public void E3_tower_three_rows_match_T112_frozen_values(
        string id, int tier, string towerClass, int cost, double dps, double range)
    {
        TowerSpec spec = FindE3Tower(id);

        Assert.Equal(tier, spec.Tier);
        Assert.Equal(towerClass, spec.Class.ToString().ToLowerInvariant()); // SP-1b L46~L48 定类
        Assert.Equal(cost, spec.CostTotal); // towers.json cost_total（F.5 类推锚带 [NEW]）
        Assert.Equal(dps, spec.Dps, precision: 9);
        Assert.Equal(range, spec.RangeCells, precision: 9);
    }

    [Fact] // legal — 标准线唯一步加特林 T1→臼炮 T2：成本 ×3 严格（07 决议 D-15）、
           // DPS ×2 精确落锚（F.5 锚 2.0）、类变步（穿刺→溅射）射程带豁免但
           // 11/8=×1.375 仍落 1.25±15% 带内（F.5/07 修正 #2 双保险——towers.json
           // _source 推导式原文）；铁甲墙 max_tier 1 无后继不发明
    public void E3_tower_chain_laws_gatling_to_mortar_hold()
    {
        TowerSpec gatling = FindE3Tower("era3:tower-gatling");
        TowerSpec mortar = FindE3Tower("era3:tower-mortar");
        TowerSpec wall = FindE3Tower("era3:tower-ironclad-wall");

        Assert.Equal("era3:tower-mortar", gatling.UpgradesTo); // 升级链接线（T-112）
        Assert.Null(mortar.UpgradesTo); // 标准线 T2 终点
        Assert.Null(wall.UpgradesTo); // 墙体不升级
        Assert.NotNull(gatling.TierGrowth);

        // 成本 ×3/级 严格（UpgradeLaw 同锚执法）
        Assert.Equal(
            gatling.CostTotal * gatling.TierGrowth!.CostMultiplierPerTier,
            mortar.CostTotal,
            precision: 9);

        // DPS ×2/级（F.5 锚 2.0；240→480 精确落锚）
        Assert.Equal(gatling.TierGrowth.DpsMultiplierPerTier, mortar.Dps / gatling.Dps, precision: 9);

        // 类变步（pierce→splash）+25% 带豁免；豁免步仍须落在锚带 ±15% 内
        Assert.NotEqual(gatling.Class, mortar.Class);
        double rangeRatio = mortar.RangeCells / gatling.RangeCells;
        Assert.InRange(
            rangeRatio,
            gatling.TierGrowth.RangeMultiplierPerTier * 0.85,
            gatling.TierGrowth.RangeMultiplierPerTier * 1.15);
    }

    [Fact] // legal — DPS 拆分自洽：damage_per_shot × 30 / reload_ticks = dps（towers.json
           // 拆分口径；加特林 16@15 发/s：16×30/2=240；臼炮 480@1 发/s：480×30/30=480）
    public void E3_weapon_towers_split_self_consistent_on_tick_grid()
    {
        foreach (TowerSpec spec in RegressionContent.Era3Towers().Where(t => t.ReloadTicks is not null))
        {
            Assert.Equal(
                spec.DamagePerShot * 30.0 / spec.ReloadTicks!.Value,
                spec.Dps,
                precision: 9);
        }
    }

    [Fact] // legal — 铁甲墙纯阻挡位（SP-1b L48『纯阻挡、零伤害（不计 DPS 带；HP
           // 计入流场代价 ×5 规则）』T-112 复核落定）：零伤/零射程/无武器循环/
           // 无弹药/HP 1600（[NEW] 阻挡利基）/tier 1 封顶
    public void E3_ironclad_wall_is_the_pure_blocker_row()
    {
        TowerSpec wall = FindE3Tower("era3:tower-ironclad-wall");

        Assert.Equal(0.0, wall.Dps, precision: 9);
        Assert.Equal(0.0, wall.DamagePerShot, precision: 9);
        Assert.Null(wall.ReloadTicks); // 无武器循环（E1 陷阱/篝火先例）
        Assert.Equal(0.0, wall.RangeCells, precision: 9);
        Assert.Null(wall.AmmoItem);
        Assert.Equal(0, wall.AmmoPerShot);
        Assert.Equal(1600, wall.Hp);
        Assert.Equal(1, wall.MaxTier);
    }

    // ------------------------------------------------ E3 敌九行（era3/enemies.json；行形对位 SP-3b L113~L121 跨代步 ×2 [NEW]）

    [Theory] // legal — 九行 HP/甲/速度锚（px/tick@30Hz）：E3 敌表=新设计 [NEW]
             //（行形对位 SP-3b 各位+E2 同位 ×2 跨代步，era3/enemies.json 行
             // _source 推导式逐行注明）；唯 Boss HP/甲=v3.2 §4 L60 逐字锚点
             //（堡垒号 HP9000/甲16，与 era.json boss 行同值不得漂移）
    [InlineData("era3:enemy-conscript", 120, 0, 1.60)]       // 散兵位：E2 散兵 60×2
    [InlineData("era3:enemy-line-infantry", 600, 6, 1.17)]   // 中坚位：勇士 300×2、甲 4→6
    [InlineData("era3:enemy-ram", 3000, 16, 0.53)]           // 攻城位：攻城锤 1500×2、甲 12→16
    [InlineData("era3:enemy-scout-rider", 280, 2, 2.67)]     // 快速位：轻骑 140×2
    [InlineData("era3:enemy-gunner", 360, 4, 1.07)]          // 远程位：弩手 180×2、甲 3→4
    [InlineData("era3:enemy-ironclad-guard", 1200, 12, 0.85)]// 重甲位：卫士 600×2、甲 8→12
    [InlineData("era3:enemy-sapper", 240, 1, 1.71)]          // 自爆位：火矢手 120×2
    [InlineData("era3:enemy-overseer", 1800, 10, 0.96)]      // 精英光环位：百夫长 900×2、甲 6→10
    [InlineData("era3:enemy-boss", 9000, 16, 0.53)]          // Boss：§4 L60 逐字 9000/16；速度 0.53 [NEW]
    public void E3_enemy_nine_rows_match_frozen_values(string id, int hp, int armor, double speedPxPerTick)
    {
        EnemySpec spec = RegressionContent.Era3Enemies().Get(id);

        Assert.Equal(hp, spec.Hp); // Boss 行=§4 L60 逐字锚点；常规行=[NEW] 跨代步 ×2
        Assert.Equal(armor, spec.Armor);
        Assert.Equal(speedPxPerTick, spec.SpeedPxPerTick, precision: 2); // 表体印 2 位小数→容差半位（T-083 先例）
    }

    [Fact] // legal — 个体盾数据位（v3.2 §6『护盾 E3 起』个体盾首例 [NEW]）：
           // 重甲卫兵 240/Boss 600；轻中甲行不带盾
    public void E3_individual_shields_pin_the_heavy_and_boss_rows()
    {
        Assert.Equal(240, RegressionContent.Era3Enemies().Get("era3:enemy-ironclad-guard").ShieldPoints);
        Assert.Equal(600, RegressionContent.Era3Enemies().Get("era3:enemy-boss").ShieldPoints);

        foreach (string id in new[]
                 { "era3:enemy-conscript", "era3:enemy-line-infantry", "era3:enemy-overseer" })
        {
            Assert.Equal(0, RegressionContent.Era3Enemies().Get(id).ShieldPoints); // 非重甲位无个体盾
        }
    }

    [Fact] // illegal — 敌表外 id 被生产解析目录拒绝（T-083 同款装载器严格性）
    public void E3_enemy_catalog_rejects_unknown_id()
    {
        Assert.Throws<KeyNotFoundException>(
            () => RegressionContent.Era3Enemies().Get("era3:enemy-does-not-exist"));
    }

    // ------------------------------------------------ 波次护盾公式（v3.2 §6 L84 + T-114 实装值）

    [Theory] // legal — 10+8×波（波 ≥ 起始波 31；T-114 任务卡锚波）：
             // 31/40/50 = 258/330/410，32=266（+8/波 斜率），30=0（起始波前无盾）
    [InlineData(31, 258)] // 首个 E3 波（wave-e2-golden.json shield_start_wave 记录值）
    [InlineData(32, 266)] // 次波：斜率 +8/波
    [InlineData(40, 330)] // E3 Boss 波（Era3WaveShieldTests/FortressBattleTests 同锚）
    [InlineData(50, 410)] // 表外延伸（公式不封顶）
    public void E3_wave_shield_formula_matches_T114_values(int wave, int expected)
    {
        // 起始波单一事实源=wave-e2-golden.json 冻结头（与 T-083 互锁口径同源）
        int startWave = RegressionContent.WaveGoldenHeader().ShieldStartWave;
        Assert.Equal(31, startWave); // 30 行 E2 表外首个 E3 波记录值
        Assert.Equal(expected, Shield.At(wave, startWave)); // Balance 冻结公式本体
    }

    [Fact] // boundary — 起始波前不回灌：E2 段（波 ≤30）恒 0 盾（护盾 E3 起不向前代泄漏）
    public void Pre_E3_waves_stay_unshielded()
    {
        int startWave = RegressionContent.WaveGoldenHeader().ShieldStartWave;
        Assert.Equal(0, Shield.At(30, startWave));
        Assert.Equal(0, Shield.At(1, startWave));
    }

    [Fact] // illegal — 负起始波被拒（Balance.Shield 定义域）
    public void Negative_shield_start_wave_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Shield.At(10, -1));
    }

    // ------------------------------------------------ SP-4 风险十四参数（11-spec-tables L146 表体）

    [Theory] // legal — SP-4 L146『E3 过热爆炸』行全值（触发 3+热量动力学 5+
             // 半径 1+后果 5=十四参数，[NEW-原型校准] 已定案——数值入内容数据
             // era3/era.json energy.risk，RL7 禁引擎硬编码；漂移即红）
    [InlineData("trigger.overload_threshold_percent", 100)]            // 负载率 ≥100%
    [InlineData("trigger.sustained_s", 30)]                            // 连续 30 s
    [InlineData("trigger.heat_capacity", 100)]                         // 热量槽达 100
    [InlineData("heat_dynamics.gain_per_s_overloaded", 2)]             // 过热期 +2/s
    [InlineData("heat_dynamics.overload_step_percent", 10)]            // 每 +10% 负载率一步
    [InlineData("heat_dynamics.bonus_gain_per_s_per_extra_10pct_overload", 1)] // 每步追加 +1/s
    [InlineData("heat_dynamics.decay_per_s_unloaded", 5)]              // 卸载后 −5/s 回落
    [InlineData("heat_dynamics.min_seconds_to_full_heat_at_sustained_overload", 50)] // 0→100 需 ≥50 s
    [InlineData("explosion_radius_cells", 4)]                          // 爆炸半径 4 格
    [InlineData("consequences.building_damage_fraction_of_max_hp", 0.5)] // 对建筑最大 HP×50%
    [InlineData("consequences.enemy_damage", 300)]                     // 对敌 300 伤害
    [InlineData("consequences.culprit_outage_s", 60)]                  // 肇事发电机停机 60 s
    [InlineData("consequences.culprit_degraded_output_fraction", 0.5)] // 后以 50% 功率运行
    [InlineData("consequences.culprit_degraded_duration_s", 30)]       // 运行 30 s
    public void SP4_overheat_fourteen_params_match_the_frozen_row(string jsonPath, double expected)
    {
        RegressionContent.RiskParamRow row = RegressionContent.Era3RiskParams()
            .Single(r => r.JsonPath == jsonPath);
        Assert.Equal(expected, row.Value, precision: 9);
    }

    [Fact] // legal — 内容行 ↔ 生产参数通道互锁（T-113 装配口径）：era3/era.json
           // energy.risk 十四值经 HandcraftedLevelContent.Era3EnergyRisk 装配为
           // OverheatTuning 后逐字段等值（百分比列 ÷100 入比值域）——任一侧
           //（内容表或装配映射）漂移即红
    public void SP4_params_map_verbatim_into_the_production_overheat_tuning()
    {
        var tuning = Levels.HandcraftedLevelContent.Era3EnergyRisk().Overheat;
        var byPath = RegressionContent.Era3RiskParams().ToDictionary(r => r.JsonPath, r => r.Value);

        Assert.Equal(byPath["trigger.overload_threshold_percent"] / 100.0, tuning.OverloadLoadRatio, precision: 9);
        Assert.Equal(byPath["trigger.sustained_s"], tuning.OverloadSustainedSeconds, precision: 9);
        Assert.Equal(byPath["trigger.heat_capacity"], tuning.HeatCapacity, precision: 9);
        Assert.Equal(byPath["heat_dynamics.gain_per_s_overloaded"], tuning.BaseHeatPerSecond, precision: 9);
        Assert.Equal(byPath["heat_dynamics.overload_step_percent"] / 100.0, tuning.HeatLoadStepRatio, precision: 9);
        Assert.Equal(byPath["heat_dynamics.bonus_gain_per_s_per_extra_10pct_overload"], tuning.HeatPerLoadStepPerSecond, precision: 9);
        Assert.Equal(byPath["heat_dynamics.decay_per_s_unloaded"], tuning.HeatCooldownPerSecond, precision: 9);
        Assert.Equal(byPath["explosion_radius_cells"], tuning.ExplosionRadiusCells, precision: 9);
        Assert.Equal(byPath["consequences.building_damage_fraction_of_max_hp"], tuning.BuildingDamageMaxHpFraction, precision: 9);
        Assert.Equal(byPath["consequences.enemy_damage"], tuning.EnemyDamage, precision: 9);
        Assert.Equal(byPath["consequences.culprit_outage_s"], tuning.GeneratorDowntimeSeconds, precision: 9);
        Assert.Equal(byPath["consequences.culprit_degraded_output_fraction"], tuning.GeneratorDerateFraction, precision: 9);
        Assert.Equal(byPath["consequences.culprit_degraded_duration_s"], tuning.GeneratorDerateSeconds, precision: 9);
        tuning.Validate(); // 通道守卫全过（T-113 构造期校验）
    }

    // ------------------------------------------------ 能源阶梯 E3 行（v3.2 §4 能源阶梯行 + T-112 任务卡锚点）

    [Fact] // legal — E3 阶梯：L1=300（蒸汽机）/L2=1200（锅炉）（T-112 任务卡锚点，
           // v3.2 未定逐级绝对值 _new 待原型校准）；倍率 1200/300=4 真算落
           // ×3~5/级带（v3.2 §4 能源阶梯行——带界自 era3/era.json 同文件读）
    public void E3_energy_ladder_matches_task_card_anchor_within_band()
    {
        IReadOnlyList<RegressionContent.EnergyLadderRow> ladder = RegressionContent.Era3EnergyLadder();

        Assert.Equal(2, ladder.Count);
        Assert.Equal(300.0, ladder[0].Output, precision: 9);
        Assert.Equal(1200.0, ladder[1].Output, precision: 9);

        double multiplier = ladder[1].Output / ladder[0].Output;
        Assert.Equal(4.0, multiplier, precision: 9);
        Assert.InRange(multiplier, ladder[0].MinMultiplier, ladder[0].MaxMultiplier); // §4 ×3~5/级

        // E3 起负载率类阶梯风险开闸（SP-4 适用代=E3 起；era3/era.json risk_events）
        Assert.Equal("overheat_explosion", Era3RiskKind());
        Assert.Equal(14, RegressionContent.Era3RiskParams().Count); // 十四参数恰满（防增删漂移）
    }

    // ------------------------------------------------ 世博会吞吐 1.25×（附录 F.3 + T-117 WonderThroughputBoard）

    [Fact] // legal — 世界博览会（E3 奇观，附录 F.3『物流吞吐 +25%』）：未建=1.0，
           // 建成=Π(1+0.25)=1.25 精确落锚——逐值源=legacy/wonders.json 内容行
           //（bonus_kind=logistics_throughput/bonus_value=0.25/era_index=3），
           // 生产通道 WonderThroughputBoard（T-117）真算
    public void Expo_wonder_freezes_the_1_25x_global_throughput_multiplier()
    {
        WonderSpec expo = Wonders().Single(w => w.BonusKind == WonderThroughputBoard.ThroughputBonusKind);
        Assert.Equal("campaign:wonder-expo", expo.Id); // F.3 唯一吞吐奇观（E3）
        Assert.Equal(3, expo.EraIndex);
        Assert.Equal(0.25, expo.BonusValue, precision: 9); // F.3 +25%

        var ledger = new LegacyLedger(Archaeology(), Shop());
        var board = new WonderThroughputBoard(Wonders(), ledger);
        Assert.Equal(1.0, board.Multiplier, precision: 12); // 未建：无加成

        ledger.MarkWonderBuilt(expo.Id);
        Assert.Equal(1.25, board.Multiplier, precision: 12); // 建成：1+0.25 精确
        Assert.Equal(1.0 + expo.BonusValue, board.Multiplier, precision: 12); // 逐值源=内容行
    }

    [Fact] // legal — 1.25× 通道级手算对照：E3 矿轨档（10 件/s）×1.25=12.5（附录 F.3
           // 加成作用于全物流吞吐——TransportTier 真算，WonderThroughputBoardTests
           // 通道先例的回归侧钉值）
    public void Expo_multiplier_scales_the_E3_mine_rail_tier_to_12_5()
    {
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt("campaign:wonder-expo");
        double multiplier = new WonderThroughputBoard(Wonders(), ledger).Multiplier;

        TransportTier rail = TransportTiers.Ladder.Single(t => t.ThroughputAnchorItemsPerSecond == 10.0);
        Assert.Equal(10.0, rail.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx), precision: 9);
        Assert.Equal(
            12.5,
            rail.ThroughputItemsPerSecondAt(TransportTiers.ReferenceOneWayPx, multiplier),
            precision: 9); // 10×1.25（era3/era.json logistics tier 2=矿轨 10/s 同锚）
    }

    // ------------------------------------------------ 传承刷新 ×0.5（v3.2 §4 跨代资源行 + T-115 CrossEraResourcePolicy）

    [Theory] // legal — 上代（E2）2 种基础资源刷新（丰度 ×0.5，§4 跨代资源行原文）：
             // 铁/铜=0.5（保留 era2 命名空间跨代沿用）；本代原生资源（煤/钢）=1.0
             //（无行减半）；铁=§4 E3 行点名资源（炼钢主原料）双职行
    [InlineData("era2:res-iron", 0.5)]
    [InlineData("era2:res-copper", 0.5)]
    [InlineData("era3:res-coal", 1.0)]
    [InlineData("era3:res-steel", 1.0)]
    public void Legacy_refresh_halves_the_E2_base_resources_only(string resource, double expected)
    {
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era3"), "era.json")));

        Assert.Equal(3, policy.TargetEraIndex); // E3 行声明
        Assert.Equal(expected, policy.AbundanceOf(new Contracts.Foundation.ResourceKey(resource)), precision: 9);

        // 刷新行恰两条且都来自紧邻前代 E2（§4『上代』=紧邻前代口径）
        Assert.Equal(2, policy.LegacyRefresh.Count);
        Assert.All(policy.LegacyRefresh, row => Assert.Equal(2, row.FromEraIndex));
    }

    [Fact] // illegal — [F-011] 刷新乘子 ≥1（不得增益）被 CrossEraResourcePolicy.Parse 拒绝
           //（T-115 通道守卫——(0,1) 开带）
    public void Refresh_multiplier_at_or_above_one_is_rejected()
    {
        string json = "{\"items\":[{\"era_index\":3,\"resources\":["
            + "{\"id\":\"era2:res-iron\",\"legacy_refresh\":{\"from_era\":2,\"abundance_multiplier\":1.0}}]}]}";
        Assert.Throws<InvalidOperationException>(() => CrossEraResourcePolicy.Parse(json));
    }

    // ------------------------------------------------ 装载辅助

    private static TowerSpec FindE3Tower(string id)
    {
        foreach (TowerSpec spec in RegressionContent.Era3Towers())
        {
            if (spec.Id == id)
            {
                return spec;
            }
        }

        throw new InvalidOperationException($"era3/towers.json 缺 {id}");
    }

    private static string Era3RiskKind()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(EraDir("era3"), "era.json")));
        return doc.RootElement.GetProperty("items")[0]
            .GetProperty("energy").GetProperty("risk").GetProperty("kind").GetString()!;
    }
}
