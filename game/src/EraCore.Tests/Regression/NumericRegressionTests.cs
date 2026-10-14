using EraCore.Agent.Governance;
using EraCore.Balance;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Energy;
using EraCore.Progression;
using EraCore.Scrolls;
using EraCore.Units;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Regression;

// T-083 (F-172) 无头数值回归首版——全参数化断言套件：把 v3.2 附录 F 冻结数值
// 参数化进 Theory，ACTUAL 一律从 content JSON（生产解析器装载）+ Balance/
// Combat/Agent/Progression 公式真算得出；期望值表逐行注明文档出处
// （附录 F.x / §11.4 / SP-3b 行号），期望值永不回灌为输入（CS-4.4）。
// 与 Balance/ 既有单测的分工：那边测公式行为，这里钉冻结数值面——内容表或
// 公式任一侧漂移都会在本套件红。三行纪律（CS-3.1）以 legal/boundary/
// illegal 注释节标注；浮点容差逐处注明来源（文档印刷精度）。
// [F-172] 数值无头回归（验收 08：全参数化断言+快照对比入 CI）
public class NumericRegressionTests
{
    public NumericRegressionTests(ITestOutputHelper output) => Output = output;

    private readonly ITestOutputHelper Output;

    // ------------------------------------------------ 塔基准五项（附录 F.5 E2 塔基准表）

    [Theory] // legal — F.5 五行冻结值：成本|DPS|射程（11-spec-tables/方案 v3.2 附录 F.5）
    [InlineData("era2:tower-arrow", 1, 90, 30.0, 6.5)]      // 箭楼 T1 90|30|6.5
    [InlineData("era2:tower-crossbow", 2, 270, 60.0, 8.0)]  // 弩塔 T2 270|60|8
    [InlineData("era2:tower-repeating-crossbow", 3, 810, 120.0, 9.5)] // 连弩塔 T3 810|120|9.5
    [InlineData("era2:tower-onager", 3, 700, 80.0, 11.0)]   // 弩炮 T3 特化 700|80|11
    [InlineData("era2:tower-fire-oil", 4, 2430, 240.0, 7.5)] // 火油塔 T4 2430|240AOE|7.5
    public void Tower_benchmark_five_rows_match_F5(string id, int tier, int cost, double dps, double range)
    {
        TowerSpec spec = FindTower(id);

        Assert.Equal(tier, spec.Tier);
        Assert.Equal(cost, spec.CostTotal); // F.5 成本列（07 决议 D-15：×3/级严格）
        Assert.Equal(dps, spec.Dps, precision: 9); // F.5 DPS 列（整数/一位小数）
        Assert.Equal(range, spec.RangeCells, precision: 9); // F.5 射程列（±15% 设计带内合法非整值 9.5/7.5 原样冻结）
    }

    [Fact] // legal — 标准链 tier_line 词汇与 DPS 自洽拆分（F.5「特化」+towers.json 拆分口径）
    public void Tower_line_vocabulary_and_shot_split_stay_consistent()
    {
        TowerSpec onager = FindTower("era2:tower-onager");
        Assert.Equal("specialization", onager.TierLine); // F.5 原文术语（700 不入 ×3 阶梯——特化线豁免）

        foreach (TowerSpec spec in RegressionContent.Era2Towers())
        {
            if (spec.TierLine == "standard")
            {
                // towers.json 拆分口径：damage_per_shot × 30 / reload_ticks = dps（整 tick 网格自洽）
                Assert.NotNull(spec.ReloadTicks);
                Assert.Equal(
                    spec.Dps,
                    spec.DamagePerShot * 30.0 / spec.ReloadTicks!.Value,
                    precision: 9);
            }
        }
    }

    [Theory] // legal — F.5 链式法则：成本 ×3 严格、DPS ×2（锚 2.0）、射程 +25%（±15% 设计带）
    [InlineData("era2:tower-arrow", "era2:tower-crossbow")]           // 6.5→8.0（+23.1% ∈ 带）
    [InlineData("era2:tower-crossbow", "era2:tower-repeating-crossbow")] // 8.0→9.5（+18.75% ∈ 带）
    [InlineData("era2:tower-repeating-crossbow", "era2:tower-fire-oil")] // 9.5→7.5：类变终点档（pierce→splash），射程步不套 +25% 带（F.5 注）
    public void Tower_chain_laws_hold_per_step(string lowerId, string upperId)
    {
        TowerSpec lower = FindTower(lowerId);
        TowerSpec upper = FindTower(upperId);
        Assert.Equal(lower.UpgradesTo, upperId); // 升级链数据接线（T-065）
        Assert.NotNull(lower.TierGrowth);

        // 成本 ×3/级 严格（07 决议 D-15 恢复；UpgradeLaw 同锚执法）
        Assert.Equal(
            lower.CostTotal * lower.TierGrowth!.CostMultiplierPerTier,
            upper.CostTotal,
            precision: 9);

        // DPS ×2/级（F.5 锚 2.0；内容行 30→60→120→240 精确落锚）
        double dpsRatio = upper.Dps / lower.Dps;
        Assert.Equal(lower.TierGrowth.DpsMultiplierPerTier, dpsRatio, precision: 9);

        // 射程 +25%/级 ±15% 设计带（F.5/07 修正 #2）——类变步豁免
        bool classChange = lower.Class != upper.Class;
        if (!classChange)
        {
            double rangeRatio = upper.RangeCells / lower.RangeCells;
            double anchor = lower.TierGrowth.RangeMultiplierPerTier;
            Assert.InRange(rangeRatio, anchor * 0.85, anchor * 1.15); // 容差=±15% 设计带原文
        }
    }

    // ------------------------------------------------ 敌表锚点（SP-3b L113~L121 九行）

    [Theory] // legal — SP-3b 表行：HP/甲/速度（px/tick@30Hz；11-spec-tables.md L113~L121）
    [InlineData("era2:enemy-skirmisher", 60, 0, 1.60)]    // e2_skirmisher 60/0/1.60
    [InlineData("era2:enemy-warrior", 300, 4, 1.17)]      // e2_warrior 300/4/1.17
    [InlineData("era2:enemy-ram", 1500, 12, 0.53)]        // e2_ram 1500/12/0.53
    [InlineData("era2:enemy-raider", 140, 2, 2.67)]       // e2_raider 140/2/2.67
    [InlineData("era2:enemy-crossbowman", 180, 3, 1.07)]  // e2_crossbowman 180/3/1.07
    [InlineData("era2:enemy-guard", 600, 8, 0.85)]        // e2_guard 600/8/0.85
    [InlineData("era2:enemy-firearrow", 120, 1, 1.71)]    // e2_firearrow 120/1/1.71
    [InlineData("era2:enemy-centurion", 900, 6, 0.96)]    // e2_centurion 900/6/0.96
    [InlineData("era2:enemy-boss", 4000, 10, 0.64)]       // e2_boss 4000/10/0.64
    public void Enemy_table_anchors_match_SP3b(string id, int hp, int armor, double speedPxPerTick)
    {
        EnemySpec spec = RegressionContent.Era2Enemies().Get(id);

        Assert.Equal(hp, spec.Hp); // SP-3b HP 锚（F.5 验算互证：散兵 60/勇士 300/锤 1500）
        Assert.Equal(armor, spec.Armor); // SP-3b 甲锚（勇士 4→23%、锤 12→92.5% 有效比的甲源）
        Assert.Equal(speedPxPerTick, spec.SpeedPxPerTick, precision: 2); // SP-3b 速度列印 2 位小数→容差半位
    }

    [Fact] // illegal — 敌表外 id 被生产解析目录拒绝（装载器严格全量）
    public void Enemy_catalog_rejects_unknown_id()
    {
        Assert.Throws<KeyNotFoundException>(
            () => RegressionContent.Era2Enemies().Get("era2:enemy-does-not-exist"));
    }

    // ------------------------------------------------ 威胁乘子 12 格（附录 F.2 映射表）

    [Theory] // legal — F.2 全表 4 行×3 列：印刷 2 位小数（0.83/1.17/1.53/1.07 为四舍五入）
    [InlineData(0.3, 0.5, 0.50, 2.0)]  // t=0.3：0.50/2.0（带下沿）
    [InlineData(0.6, 1.0, 0.83, 1.53)] // t=0.6：0.8333/1.5333→F.2 印刷 0.83/1.53
    [InlineData(0.9, 1.5, 1.17, 1.07)] // t=0.9：1.1667/1.0667→F.2 印刷 1.17/1.07
    [InlineData(1.2, 2.0, 1.50, 0.6)]  // t=1.2：0.60（带上沿）
    public void Threat_multiplier_twelve_cells_match_F2(double t, double count, double hp, double interval)
    {
        ThreatMultipliers m = Threat.Multipliers(t);

        // hp/interval 容差 0.005（precision 2）=F.2 末位印刷精度的一半——F.2 印
        // 0.83/1.17/1.53/1.07，真值 0.8333/1.1667/1.5333/1.0667（先例
        // ThreatMultipliersTests 同源同容差）；count 列锚值精确落点（5t/3）。
        Assert.Equal(count, m.Count, precision: 3);
        Assert.Equal(hp, m.Hp, precision: 2);
        Assert.Equal(interval, m.Interval, precision: 2);
    }

    [Fact] // legal — F.2 与威胁度钳制口径互锁：0.3+0.3n 公式锚（v3.2 §7/SP-9）
    public void Threat_clamp_anchors_stay_frozen()
    {
        Assert.Equal(0.3, Threat.ClampThreat(0), precision: 9); // n=0 → 下沿
        Assert.Equal(1.2, Threat.ClampThreat(3), precision: 9); // n=3 恰落上沿
        Assert.Equal(1.2, Threat.ClampThreat(5), precision: 9); // SP-9 围城示例：n=5 钳 1.2
    }

    // ------------------------------------------------ 系统点各代（§11.4 / 附录 F.4）

    [Theory] // legal — 上限=1+2×E：E1 不开启（Enabled=false），E2=5→终章 E6=13
    [InlineData(1, 3, false)] // E1 绑定期：点未开启（§11.4）
    [InlineData(2, 5, true)]  // E2=5
    [InlineData(3, 7, true)]  // E3=7
    [InlineData(4, 9, true)]  // E4=9
    [InlineData(5, 11, true)] // E5=11
    [InlineData(6, 13, true)] // 终章 E6=13（§4 时代序号代入）
    public void System_point_ceiling_per_era_matches_S11_4(int era, int ceiling, bool enabled)
    {
        var ledger = new SystemPointLedger(era);

        Assert.Equal(ceiling, ledger.Ceiling); // 1+2×era（F.4 系统点行）
        Assert.Equal(enabled, ledger.Enabled); // E1 不开启
    }

    [Theory] // illegal — 时代序号越域被 Guard 拒绝（MinEra..MaxEra）
    [InlineData(0)]
    [InlineData(7)]
    public void System_point_ledger_rejects_era_out_of_range(int era)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemPointLedger(era));
    }

    [Fact] // boundary — Boss 波回满 50% 与 40% 单波支出线（§11.4 两道阈值，F.4 同文）
    public void System_point_wave_limits_stay_frozen()
    {
        var ledger = new SystemPointLedger(2); // E2=5

        // Boss 波回满至上限 50%（向下取整）：⌊5/2⌋=2（§11.4）
        Assert.True(ledger.BeginWave(waveNumber: 1, isBossWave: true));
        Assert.Equal(2, ledger.Points);

        // 普通波回满至上限=5，40% 限单波支出=⌊5×2/5⌋=2：FocusFire（战斗 1 点）
        // 第 1/2 次放行，第 3 次撞 40% 线拒绝（ErrBudgetDenied，失败不计费）
        Assert.True(ledger.BeginWave(waveNumber: 2, isBossWave: false));
        Assert.Equal(5, ledger.Points);
        Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.FocusFire).Status);
        Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.FocusFire).Status);
        ActionResult denied = ledger.TrySpend(SystemActionKind.FocusFire);
        Assert.Equal(ActionResultStatus.ErrBudgetDenied, denied.Status);
        Assert.Equal(0, denied.PointsSpent); // 拒绝不扣点
        Assert.Equal(3, ledger.Points); // 5−2，封顶线内实际支出=2
    }

    // ------------------------------------------------ 卷轴常驻上限（附录 F.1）

    [Theory] // legal — 常驻上限=6+2×E：E1=8 … 终章 E6=18（F.1「常驻激活上限 6+2×时代序号」）
    [InlineData(1, 8)]
    [InlineData(2, 10)] // F.1/scrolls.json rules_anchor 注 E2=10 张
    [InlineData(3, 12)]
    [InlineData(4, 14)]
    [InlineData(5, 16)]
    [InlineData(6, 18)] // §4 时代序号代入：6+2×6=18
    public void Scroll_resident_cap_per_era_matches_F1(int era, int cap)
    {
        Assert.Equal(cap, new ScrollEngine(era).ResidentCap);
    }

    // ------------------------------------------------ 研究成本锚（附录 F.5 研究例 + F.5 公式）

    [Fact] // legal — 箭楼 n=90 → ≈3013（F.5「研究例：箭楼 n=90→≈3000（33 倍，对齐参照系 31 倍）」；
           // 33 倍取整口径=60+90^1.11×20=3012.84→3013，techtree.json 注同源）
    public void Research_cost_anchor_arrow_matches_F5()
    {
        // 数据源单一化：n 从 campaign/techtree.json 的箭楼节点读（Σ成本），不手抄
        RegressionContent.TechNodeRow arrow = RegressionContent.TechNodes()
            .Single(node => node.Id == "campaign:tech-e2-arrow");
        Assert.Equal(90, arrow.CostSum); // 塔节点 Σ成本=towers.json cost_total（techtree 装载约定）
        Assert.Equal("era2:tower-arrow", arrow.Unlocks); // 与 F.5 五行表的箭楼行同一条目

        long raw = (long)Math.Round(TechTree.RawCost(arrow.CostSum, unlockedCount: 0), MidpointRounding.AwayFromZero);
        Assert.Equal(3013, raw); // F.5 锚 ≈3000 的取整口径（TechTreeTests 公式表同锚）
        Assert.InRange(raw / (double)arrow.CostSum, 33.0, 34.0); // 33 倍（对齐参照系 31 倍，F.5 原文）
    }

    [Fact] // legal — 研究公式的冻结形状：基项 60/指数 1.11/尺度 20/解锁增项 2%/级（v3.2 §7）
    public void Research_cost_formula_constants_stay_frozen()
    {
        Assert.Equal(60.0, TechTree.CostBaseTerm, precision: 9);
        Assert.Equal(1.11, TechTree.CostExponent, precision: 9);
        Assert.Equal(20.0, TechTree.CostScale, precision: 9);
        Assert.Equal(0.02, TechTree.CostPerUnlockedFactor, precision: 9);

        // 已解锁数增项单调（每解锁 1 项 +2%）——boundary 行：增项只乘尺度项，
        // 基项 60 不缩放（RawCost=60+尺度项×(1+0.02u)），u=1 增量=尺度项×0.02
        double scaleTerm0 = TechTree.RawCost(90, 0) - TechTree.CostBaseTerm;
        double raw1 = TechTree.RawCost(90, 1);
        Assert.Equal(TechTree.CostBaseTerm + (scaleTerm0 * 1.02), raw1, precision: 6);
        Assert.True(raw1 > TechTree.RawCost(90, 0), "研究成本须随已解锁数单调上升");
    }

    // ------------------------------------------------ 能源阶梯（v3.2 §4 L65 ×3~5/级 + era1.json）

    [Fact] // legal — E1 阶梯：L1=10/L2=40（T-049 锚点），倍率 ×4 ∈ [3,5]（v3.2 §4 L65）
    public void Energy_ladder_era1_matches_content_and_band()
    {
        IReadOnlyList<RegressionContent.EnergyLadderRow> ladder = RegressionContent.Era1EnergyLadder();

        Assert.Equal(2, ladder.Count);
        Assert.Equal(10.0, ladder[0].Output, precision: 9); // era1.json energy.levels[0]（T-049）
        Assert.Equal(40.0, ladder[1].Output, precision: 9); // era1.json energy.levels[1]（T-049）

        // 带界自同文件读（ladder_multiplier_min/max=3/5），倍率=40/10 真算落带
        double multiplier = ladder[1].Output / ladder[0].Output;
        Assert.Equal(4.0, multiplier, precision: 9);
        Assert.InRange(multiplier, ladder[0].MinMultiplier, ladder[0].MaxMultiplier); // §4 L65 ×3~5/级

        // 产品侧锚点常量与内容互锁（E1EnergyAnchors 同源双写，漂移即红）
        Assert.Equal(E1EnergyAnchors.LaborCampOutputPerSecond, ladder[0].Output, precision: 9);
        Assert.Equal(E1EnergyAnchors.BeastPenOutputPerSecond, ladder[1].Output, precision: 9);
        Assert.Equal(E1EnergyAnchors.L1ToL2Multiplier, multiplier, precision: 9);
        E1EnergyAnchors.EnsureLadderInBand(); // 带外抛 InvalidOperationException（T-033 验收）
    }

    // ------------------------------------------------ 护甲公式边界（v3.2 §6 L83 + 附录 A 内联式）

    [Theory] // legal — max(d×衰减−甲, 10%×d×衰减)、先衰减后减甲、甲 clamp≥0（v3.2 §6）
    [InlineData(15, 1.0, 0, 15.0)]    // 甲 0 直通（F.5 组 1：箭楼 15 对散兵甲 0）
    [InlineData(17, 1.0, 4, 13.0)]    // SP-3c：弩矢 17 对勇士甲 4 → 吃 4/17≈23%（F.5 组 2）
    [InlineData(160, 1.0, 12, 148.0)] // SP-3c：弩炮 160 对锤甲 12 → 92.5% 有效比（F.5 组 3）
    [InlineData(15, 1.0, 15, 1.5)]    // 边界：甲≥d → 10%×d 保底（§6「甲不能完全免伤」）
    [InlineData(15, 1.0, 999, 1.5)]   // 边界：深过甲仍钳在 10% 保底
    [InlineData(100, 0.5, 30, 20.0)]  // 次序：先衰减后减甲（100×0.5−30=20；先减甲=50−35=15 → 可区分）
    [InlineData(100, 0.5, 60, 5.0)]   // 保底基数=衰减后 d（10%×50=5，非 10%×100）
    public void Armor_direct_branch_boundaries_match_S6(double d, double decay, int armor, double expected)
    {
        Assert.Equal(expected, Damage.Direct(d, decay, armor), precision: 9);
    }

    [Theory] // boundary — 负甲 clamp 0（§6 甲 clamp≥0；Integer 孪生入口的确定性回放域）
    [InlineData(15, -7, 15)] // 负甲按 0 处理（不加分）
    [InlineData(18, 4, 14)]  // 整数孪生：36−12 线的邻行（连弩 18 对勇士甲 4）
    [InlineData(18, 12, 6)]  // 整数孪生：连弩 18 对锤甲 12
    [InlineData(18, 18, 2)]  // ceil(0.1×18)=2（(18+9)/10 纯整数实现）
    [InlineData(18, 99, 2)]  // 深过甲钳保底
    public void Armor_integer_twin_boundaries_clamp_and_floor(int d, int armor, int expected)
    {
        Assert.Equal(expected, Damage.DirectInt(d, armor));
        if (armor >= 0 && armor < d)
        {
            // 未触保底时孪生与浮点分支 decay=1 逐点一致（DamageTests 发散网格
            // 同一不变式；触保底行的有意 ceil 发散见下一条边界测试）
            Assert.Equal(Damage.Direct(d, 1.0, armor), Damage.DirectInt(d, armor), precision: 9);
        }
    }

    [Fact] // boundary — 保底行的孪生发散是有意设计：DirectInt 取 ceil(0.1×d)
           //（Damage.DirectInt XML：纯整数 ceil 语义，(d+9)/10），浮点分支取
           // 0.1×d 原值——18 的保底=2 对 1.8（确定性回放走整数入口，v3.2 §6
           // 「甲不能完全免伤」的整数域收口）
    public void Armor_floor_ceiling_divergence_between_twin_and_float_is_intentional()
    {
        Assert.Equal(2, Damage.DirectInt(18, 18));
        Assert.Equal(1.8, Damage.Direct(18, 1.0, 18), precision: 9);
        Assert.Equal(2, Damage.DirectInt(18, 999));
        Assert.Equal(1.8, Damage.Direct(18, 1.0, 999), precision: 9);
    }

    [Theory] // illegal — 非法域：负伤害/衰减越 [0,1] 被拒（附录 A 式定义域）
    [InlineData(-1.0, 1.0, 0)]
    [InlineData(10.0, -0.1, 0)]
    [InlineData(10.0, 1.1, 0)]
    public void Armor_branch_rejects_out_of_domain(double d, double decay, int armor)
    {
        Assert.Throws<ArgumentException>(() => Damage.Direct(d, decay, armor));
    }

    // ------------------------------------------------ 波次参数头互锁（wave-e2-golden.json ↔ 循环模拟参数源）

    [Fact] // legal — T-035 校准集冻结头（[NEW-原型校准] 标记的原样钉住，模拟器同源读它）
    public void Wave_golden_header_pins_the_T035_calibration()
    {
        RegressionContent.WaveGoldenParams p = RegressionContent.WaveGoldenHeader();

        Assert.Equal(0.75, p.Threat, precision: 9); // F.5 组 1 基准威胁
        Assert.Equal(10, p.Base); // 校准至第 5 波=6 散兵（F.5 锚）
        Assert.Equal(5, p.Begin);
        Assert.Equal(5, p.Spacing);
        Assert.Equal(2.0, p.Scaling, precision: 9);
        Assert.Equal(31, p.ShieldStartWave); // 30 行 E2 表外首个 E3 波记录值（护盾 E3 起）
        Assert.Equal(120.0, p.IntervalBaseSeconds, precision: 9); // v3.2 §6 波间隔基值
    }

    private static TowerSpec FindTower(string id)
    {
        foreach (TowerSpec spec in RegressionContent.Era2Towers())
        {
            if (spec.Id == id)
            {
                return spec;
            }
        }

        throw new InvalidOperationException($"era2/towers.json 缺 {id}");
    }
}
