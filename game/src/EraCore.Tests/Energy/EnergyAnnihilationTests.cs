using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Sim;
using EraCore.Tests.Sim;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Energy.Sp4RiskTables;

namespace EraCore.Tests.Energy;

// [C-08][F-010] E5 湮灭通道（T-134, SP-4 L148）单元/逻辑/账本集成：表值
// 触发边界（≥150% 连续 10 s / 聚变 HP 归零即时）、无倒计时行差、后果载荷
//（平伤 2000/连锁 ×0.6/力场半径 ×0.5/残骸 30%）、对策可避免（E5 的对策在
// 建筑位：负载纪律/聚变 HP/力场塔——引擎面=降载与归零旗标双输入均不触发时
// 零事件）、确定性与非法参数。表值与 era5/era.json energy.annihilation
// 同源（Era5EnergyRisk 内容装载断言见 Era5LevelPlaythroughTests 装配面）。
public class EnergyAnnihilationEngineTests
{
    private static readonly EnergyNodeId Culprit = new(8501);

    /// <summary>SP-4 L148 E5 湮灭行（触发/半径/后果全列）。</summary>
    public static readonly AnnihilationTuning Annihilation = new(
        OverloadLoadRatio: 1.50,            // 负载率 ≥150% 连续 10 s
        OverloadSustainedSeconds: 10.0,
        ExplosionRadiusCells: 8.0,          // 半径 8 格
        UnitDamage: 2000.0,                 // 半径内一切单位受 2000 伤害
        ChainDamageDecayFraction: 0.6,      // 每连锁一栋伤害 ×0.6 衰减
        ForceFieldRadiusMultiplier: 0.5,    // 力场塔在场则半径减半（SP-1b L54）
        WreckRecoveryCostFraction: 0.30);   // 残骸修复口径 原成本×30%（[NEW-T-134] 内容补位行）

    /// <summary>E5 完整表（E3 过热沿用 + E5 湮灭；E4 熔毁=null——聚变代无核电通道）。</summary>
    public static EnergyRiskTuning Era5() => new(Overheat, Meltdown: null, Annihilation);

    private static List<EnergyRiskOutcome> Run(
        EnergyRiskEngine engine, long fromTick, long ticks, double loadRatio,
        bool fusionHpZero = false, EnergyNodeId? culprit = null)
    {
        var events = new List<EnergyRiskOutcome>();
        for (long i = 0; i < ticks; i++)
        {
            events.AddRange(engine.OnSettle(fromTick + i, dtTicks: 1, loadRatio, coolantSupplied: true, fusionHpZero, culprit ?? Culprit));
        }

        return events;
    }

    // ---- legal：表值触发边界 -------------------------------------------------

    [Fact] // legal — ≥150% 连续 10 s（300 ticks）即时引爆：N−1 不触发、N 触发
    public void Sustained_150_percent_for_ten_seconds_detonates_immediately()
    {
        var engine = new EnergyRiskEngine(Era5());
        Assert.Equal(300, engine.AnnihilationSustainTicks); // 10 s × 30 Hz
        Assert.True(engine.HasAnnihilation);

        // N−1（299 ticks @1.6）：零事件（无倒计时——达标即爆，不 armed）
        Assert.Empty(Run(engine, 1, 299, 1.6));
        Assert.Equal(299, engine.AnnihilationSustainedTicks);

        // N（第 300 tick）：即时引爆，全载荷断言（SP-4 L148 表值）
        var fired = Run(engine, 300, 1, 1.6);
        EnergyRiskOutcome outcome = Assert.Single(fired);
        Assert.Equal(EnergyRiskEventKind.Annihilation, outcome.Kind);
        Assert.Equal(300, outcome.Tick);
        Assert.Equal(Culprit, outcome.Culprit);
        Assert.Equal(1.6, outcome.LoadRatioAtTrigger, 9);
        Assert.Equal(8.0, outcome.RadiusCells, 9);
        Assert.Equal(0.0, outcome.BuildingDamageMaxHpFraction, 9); // 建筑走连锁通道，非最大 HP 份额
        Assert.Equal(2000.0, outcome.EnemyDamage, 9);               // 半径内一切单位平伤
        Assert.Equal(0.6, outcome.ChainDamageDecayFraction, 9);     // 连锁 ×0.6
        Assert.Equal(0.5, outcome.ForceFieldRadiusMultiplier, 9);   // 力场半径 ×0.5（载荷位）
        Assert.Equal(0.30, outcome.WreckRecoveryCostFraction, 9);   // 残骸修复 30%
        Assert.Equal(0, engine.AnnihilationSustainedTicks);         // 引爆后窗口清零
        Assert.Equal(1, engine.AnnihilationFireCount);
    }

    [Fact] // legal — 卸载即中断计数（窗口 reset；对策面=负载纪律）
    public void Unloading_resets_the_sustained_window()
    {
        var engine = new EnergyRiskEngine(Era5());
        Assert.Empty(Run(engine, 1, 250, 1.6));    // tick 1..250 超载（250/300）
        Assert.Empty(Run(engine, 251, 10, 0.9));   // tick 251..260 卸载：窗口清零
        Assert.Equal(0, engine.AnnihilationSustainedTicks);
        Assert.Empty(Run(engine, 261, 299, 1.6));  // tick 261..559 重新计数的 N−1（299/300）
        Assert.Equal(299, engine.AnnihilationSustainedTicks);
        EnergyRiskOutcome outcome = Assert.Single(Run(engine, 560, 1, 1.6)); // 第 300 个超载 tick=达标即爆
        Assert.Equal(EnergyRiskEventKind.Annihilation, outcome.Kind); // 无倒计时：达标结算当刻引爆
    }

    [Fact] // legal — 聚变 HP 归零即时引爆：任意负载、零窗口（SP-4 触发列第一支）
    public void Fusion_hp_zero_detonates_at_any_load_without_a_window()
    {
        var engine = new EnergyRiskEngine(Era5());
        var fired = Run(engine, 1, 1, 0.5, fusionHpZero: true); // 半载也爆
        EnergyRiskOutcome outcome = Assert.Single(fired);
        Assert.Equal(EnergyRiskEventKind.Annihilation, outcome.Kind);
        Assert.Equal(0.5, outcome.LoadRatioAtTrigger, 9); // 引爆刻负载原样入档
        Assert.Equal(2000.0, outcome.EnemyDamage, 9);
    }

    [Fact] // legal — 无湮灭行的引擎无视归零旗标（E3/E4 行为与哈希不变）
    public void Engines_without_the_row_ignore_the_fusion_flag()
    {
        var e3 = new EnergyRiskEngine(OverheatOnly());
        Assert.False(e3.HasAnnihilation);
        Assert.Equal(long.MaxValue, e3.AnnihilationSustainTicks);
        Assert.Empty(Run(e3, 1, 100, 0.5, fusionHpZero: true)); // 旗标被无视

        // 确定性对照：同结算序列下旗标输入不改变无行引擎的哈希
        var withFlag = new EnergyRiskEngine(OverheatOnly());
        var withoutFlag = new EnergyRiskEngine(OverheatOnly());
        _ = Run(withFlag, 1, 50, 0.5, fusionHpZero: true);
        _ = Run(withoutFlag, 1, 50, 0.5, fusionHpZero: false);
        Assert.Equal(withoutFlag.ComputeStateHash(), withFlag.ComputeStateHash());
    }

    [Fact] // legal — 确定性：同表+同结算序列双跑 → 事件与哈希全等；湮灭态折哈希
    public void Same_sequence_twice_hashes_identically()
    {
        long HashOnce()
        {
            var engine = new EnergyRiskEngine(Era5());
            _ = Run(engine, 1, 299, 1.6);
            _ = Run(engine, 300, 1, 1.6);
            return engine.ComputeStateHash();
        }

        Assert.Equal(HashOnce(), HashOnce());
    }

    [Fact] // legal — 账本集成：湮灭引爆=肇事聚变机永久退册 + 事件入档 + 归零旗标一次性消费
    public void The_ledger_detonates_and_unregisters_the_fusion_culprit()
    {
        var clock = new SteppedClock();
        var ledger = new EnergyLedger(clock, risk: new EnergyRiskEngine(Era5()));
        ledger.RegisterProducer(new EnergyNodeId(1), EnergyNodeKind.LaborCamp);
        ledger.RegisterProducer(new EnergyNodeId(2), EnergyNodeKind.LaborCamp);
        ledger.RegisterConsumer(new EnergyNodeId(3), demandPerSecond: 40.0); // 40/20=200% ≥150%

        for (int i = 0; i < 299; i++) // tick 0..298：299 个满载结算（<300 窗）
        {
            ledger.Settle();
            _ = clock.Advance();
        }

        Assert.Empty(ledger.RiskEvents);

        _ = clock.Advance();
        ledger.Settle(); // 第 300 个满载结算（tick 299）：达标即爆（无倒计时）
        EnergyRiskOutcome outcome = Assert.Single(ledger.RiskEvents); // 第 300 满载结算引爆
        Assert.Equal(EnergyRiskEventKind.Annihilation, outcome.Kind);
        Assert.Equal(1, ledger.ProducerCount); // 肇事机退册（最大产出者=node1）
        Assert.Equal(10.0, ledger.TotalOutputPerSecond, 9); // 余单营

        // 归零旗标一次性：SetFusionHpZero → 下一结算引爆 → 旗标被消费（再结算不重复爆）
        ledger.SetFusionHpZero(true);
        _ = clock.Advance();
        ledger.Settle();
        Assert.Equal(2, ledger.RiskEvents.Count);
        _ = clock.Advance();
        ledger.Settle();
        Assert.Equal(2, ledger.RiskEvents.Count); // 旗标已消费
    }

    [Fact] // illegal — 非法参数行：半径/时长/平伤非正、衰减出带、力场乘子非 (0,1]、残骸出带
    public void Malformed_annihilation_rows_reject_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(
            Overheat, null, Annihilation with { ExplosionRadiusCells = 0 })));
        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(
            Overheat, null, Annihilation with { OverloadSustainedSeconds = 0 })));
        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(
            Overheat, null, Annihilation with { UnitDamage = 0 })));
        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(
            Overheat, null, Annihilation with { ChainDamageDecayFraction = 1.5 })));
        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(
            Overheat, null, Annihilation with { ForceFieldRadiusMultiplier = 0.0 })));
        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(
            Overheat, null, Annihilation with { ForceFieldRadiusMultiplier = 1.5 })));
        Assert.Throws<ArgumentException>(() => new EnergyRiskEngine(new EnergyRiskTuning(
            Overheat, null, Annihilation with { WreckRecoveryCostFraction = 2.0 })));
    }

    [Fact] // illegal — 无肇事机引爆=接线 bug（与 E3/E4 同哨兵）
    public void Arming_without_a_producer_is_a_wiring_bug()
    {
        var engine = new EnergyRiskEngine(Era5());
        Assert.Throws<InvalidOperationException>(
            () => engine.OnSettle(1, 1, 0.5, coolantSupplied: true, fusionHpZero: true, culprit: null));
    }
}

// [C-12][F-010] E5 湮灭世界层（T-134）：连锁规划器 PlanAnnihilationChain 的
// BFS/衰减/力场半径/一次性/确定性 + ApplyBlast 半径乘子。纯逻辑层测试
//（位置驱动，无需 Simulation）。
public class EnergyAnnihilationWorldTests
{
    private static EnergyRiskOutcome Outcome() => new(
        Tick: 9000,
        Kind: EnergyRiskEventKind.Annihilation,
        Culprit: new EnergyNodeId(100),
        LoadRatioAtTrigger: 1.6,
        RadiusCells: 8.0,
        BuildingDamageMaxHpFraction: 0.0,
        EnemyDamage: 2000.0,
        GeneratorDowntimeSeconds: 0.0,
        GeneratorDerateFraction: 0.0,
        GeneratorDerateSeconds: 0.0,
        RadiationZoneSeconds: 0.0,
        RadiationEnemySlowFraction: 0.0,
        WreckRecoveryCostFraction: 0.30,
        ChainDamageDecayFraction: 0.6,
        ForceFieldRadiusMultiplier: 0.5);

    [Fact] // legal — 半径内其他能源建筑连锁：深度衰减 ×0.6^depth、逐栋一次
    public void Chain_plan_decays_per_depth_and_detonates_each_building_once()
    {
        // culprit@(0,0)；A@(3,0) 距 3 ≤8 → depth1（伤 2000×0.6=1200）；
        // B@(6,0) 距 culprit 6 ≤8、距 A 3 ≤8 → depth1；C@(20,0) 距 20 >8 → 不连锁。
        var buildings = new (EnergyNodeId, CellCoord)[]
        {
            (new(201), new CellCoord(3, 0)),
            (new(202), new CellCoord(6, 0)),
            (new(203), new CellCoord(20, 0)),
        };

        IReadOnlyList<AnnihilationChainLink> links =
            EnergyRiskWorldConsumer.PlanAnnihilationChain(Outcome(), new CellCoord(0, 0), buildings, forceFieldPresent: false);

        Assert.Equal(3, links.Count); // culprit + A + B（C 出圈）
        Assert.Equal(0, links[0].LinkDepth);
        Assert.Equal(2000.0, links[0].UnitDamage, 9);
        Assert.Equal(8.0, links[0].RadiusCells, 9);

        // 深度 1 两栋（node-id 序）、伤害恰 ×0.6
        Assert.All(links.Skip(1), link => Assert.Equal(1, link.LinkDepth));
        Assert.Equal(new EnergyNodeId(201), links[1].NodeId);
        Assert.Equal(new EnergyNodeId(202), links[2].NodeId);
        Assert.All(links.Skip(1), link => Assert.Equal(1200.0, link.UnitDamage, 9));
    }

    [Fact] // legal — 力场在场半径减半：8→4 格，出圈栋不连锁（含传递圈；SP-1b L54 对策面）
    public void Force_field_presence_halves_the_radius_and_shrinks_the_chain()
    {
        // 201@(3,0)：直距 3 ≤4 入链；202@(8,0)：直距 8 >4 且距 201 5 >4——
        // 半径减半后直接圈与传递圈都够不着（全半径 8 时 202 直距恰达边界入链——
        // 对比断言钉死『减半真实收缩了连锁面』）。
        var buildings = new (EnergyNodeId, CellCoord)[]
        {
            (new(201), new CellCoord(3, 0)),
            (new(202), new CellCoord(8, 0)),
        };

        IReadOnlyList<AnnihilationChainLink> full =
            EnergyRiskWorldConsumer.PlanAnnihilationChain(Outcome(), new CellCoord(0, 0), buildings, forceFieldPresent: false);
        Assert.Equal(3, full.Count); // 全半径：culprit+201+202（8 ≤8 边界含）

        IReadOnlyList<AnnihilationChainLink> halved =
            EnergyRiskWorldConsumer.PlanAnnihilationChain(Outcome(), new CellCoord(0, 0), buildings, forceFieldPresent: true);
        Assert.Equal(2, halved.Count); // 减半：culprit + 201（202 出圈）
        Assert.All(halved, link => Assert.Equal(4.0, link.RadiusCells, 9)); // 8×0.5
    }

    [Fact] // legal — 二级连锁（BFS 传递）：A 出 culprit 半径但入 A' 半径 → depth 2（×0.36）
    public void Second_degree_links_chain_through_intermediate_anchors()
    {
        // culprit@(0,0)；A@(7,0) 距 7 ≤8 → depth1；B@(13,0) 距 culprit 13 >8、
        // 距 A 6 ≤8 → depth2（伤 2000×0.36=720）。
        var buildings = new (EnergyNodeId, CellCoord)[]
        {
            (new(201), new CellCoord(7, 0)),
            (new(202), new CellCoord(13, 0)),
        };

        IReadOnlyList<AnnihilationChainLink> links =
            EnergyRiskWorldConsumer.PlanAnnihilationChain(Outcome(), new CellCoord(0, 0), buildings, forceFieldPresent: false);

        Assert.Equal(3, links.Count);
        AnnihilationChainLink second = links.Single(link => link.NodeId.Equals(new EnergyNodeId(202)));
        Assert.Equal(2, second.LinkDepth);
        Assert.Equal(720.0, second.UnitDamage, 9); // 2000×0.6²
    }

    [Fact] // legal — 确定性：候选乱序输入 → 同一计划（node-id 序 BFS）
    public void Chain_plan_is_order_independent()
    {
        var ordered = new (EnergyNodeId, CellCoord)[]
        {
            (new(201), new CellCoord(3, 0)),
            (new(202), new CellCoord(6, 0)),
        };
        var shuffled = new (EnergyNodeId, CellCoord)[]
        {
            (new(202), new CellCoord(6, 0)),
            (new(201), new CellCoord(3, 0)),
        };

        var first = EnergyRiskWorldConsumer.PlanAnnihilationChain(Outcome(), new CellCoord(0, 0), ordered, false);
        var second = EnergyRiskWorldConsumer.PlanAnnihilationChain(Outcome(), new CellCoord(0, 0), shuffled, false);
        Assert.Equal(first, second);
    }

    [Fact] // illegal — 通道守卫：非湮灭 outcome 拒绝入链规划；null 锚拒绝
    public void Chain_plan_guards_reject_foreign_channels_and_null_anchors()
    {
        var overheat = new EnergyRiskOutcome(
            1, EnergyRiskEventKind.OverheatExplosion, new EnergyNodeId(1), 1.0,
            4.0, 0.5, 300.0, 60.0, 0.5, 30.0, 0.0, 0.0, 0.0);
        Assert.Throws<ArgumentException>(() =>
            EnergyRiskWorldConsumer.PlanAnnihilationChain(overheat, new CellCoord(0, 0), Array.Empty<(EnergyNodeId, CellCoord)>(), false));

        Assert.Throws<ArgumentNullException>(() =>
            EnergyRiskWorldConsumer.PlanAnnihilationChain(Outcome(), null!, Array.Empty<(EnergyNodeId, CellCoord)>(), false));
    }
}
