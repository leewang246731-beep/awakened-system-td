using EraCore.Combat;
using EraCore.Scrolls;
using Xunit;

namespace EraCore.Tests.Scrolls;

// [C-18][F-037] T-071 四规则矩阵（附录 F.1）：tick 限速 / 同塔优先级仲裁 /
// 常驻上限 / 系统仲裁——每规则正反两行（CS-3.1 三行纪律：合法/边界/非法
// 以 // legal // boundary // illegal 节标注）。世界=ScriptedScrollWorld
// （跨层端口 Fake），被测系统=ScrollEngine 本体。
// [C-18][F-037]
public class ScrollEngineRuleMatrixTests
{
    private const string ZoneScrollHigh = "t:scroll-high";
    private const string ZoneScrollLow = "t:scroll-low";
    private const string ZoneScrollEqual = "t:scroll-equal";
    private const string ZoneScrollTop = "t:scroll-top";

    private static (ScrollEngine Engine, ScriptedScrollWorld World, RecordingExecutor Executor) CreateEngine(int era = 2)
    {
        var executor = new RecordingExecutor();
        var engine = new ScrollEngine(era, executor, new RecordingEffectSink());
        var world = new ScriptedScrollWorld { CurrentWave = 1 };
        return (engine, world, executor);
    }

    private static void SeedZoneWorld(ScriptedScrollWorld world, int enemyHp = 90)
    {
        world.ZoneFacts.Add(ScriptedScrollWorld.Zone("z1", (0, 0), (1, 0), (2, 0), (3, 0)));
        world.ZoneFacts.Add(ScriptedScrollWorld.Zone("z2", (0, 1), (1, 1)));
        world.AddTower(101, "z1");
        world.AddEnemy(1, "z1", enemyHp, enemyHp);
    }

    // ================================================================ rule 1
    // 规则 1（tick 限速）：每卷轴每 tick（1 s=30 ticks）至多 1 次条件判定+1 次动作。

    [Fact]
    public void Rule1_condition_card_fires_once_per_scroll_tick_and_budget_blocks_the_second_action()
    {
        // legal: 一个 1 s 窗口内条件成立 → 恰 1 判定+1 动作；同窗第二动作被预算拒绝。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60));

        engine.Step(30, world); // boundary tick (30 % 30 == 0)

        Assert.Single(engine.FireLog, r => r.ScrollId == ZoneScrollHigh && r.Outcome == ScrollFireOutcome.Executed);
        ScrollBudgetProbe budget = engine.GetBudget(ZoneScrollHigh, 30);
        Assert.True(budget.EvaluatedThisWindow);
        Assert.True(budget.ActedThisWindow);
        Assert.Equal(30, budget.CooldownReadyTick); // fired at 30, cooldown 0 → 可再动（下一窗）
    }

    [Fact]
    public void Rule1_off_cadence_tick_does_not_evaluate_and_manual_invoke_spends_the_window_budget()
    {
    // legal: 非整秒 tick 不判定；动作卡手动触发也吃同一窗口预算（1 动作/卷轴/tick）。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        var card = ScrollTestDefs.AttackSpeedActionCard("t:scroll-action", priority: 45, cooldownTicks: 0, durationTicks: 15);
        _ = engine.Equip(card);

        engine.Step(31, world); // off-cadence: no evaluation at all
        Assert.Empty(engine.FireLog);

        Assert.Equal(ScrollInvokeStatus.Ok, engine.TryInvokeActionCard(card.Id, 31, world));
        Assert.Equal(ScrollInvokeStatus.BudgetExhausted, engine.TryInvokeActionCard(card.Id, 32, world)); // same window

        engine.Step(60, world); // effect (until 46) expires in the sweep
        Assert.Equal(ScrollInvokeStatus.Ok, engine.TryInvokeActionCard(card.Id, 61, world)); // next window

        // illegal（同窗第三次调用）：window(61) 预算已被上一句消耗
        Assert.Equal(ScrollInvokeStatus.BudgetExhausted, engine.TryInvokeActionCard(card.Id, 61, world));
    }

    [Fact]
    public void Rule1_cooldown_gate_spends_no_evaluation()
    {
    // boundary: 冷却中的卷轴不消耗判定额度（每窗判定数=0），冷却结束恢复判定
    //（效果持续 30t 已于第二窗前过期，不与 AlreadyActive 守卫纠缠）。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60, cooldownTicks: 90, durationTicks: 30));

        engine.Step(30, world);
        engine.Step(60, world); // inside cooldown (ready at 120)

        ScrollBudgetProbe gated = engine.GetBudget(ZoneScrollHigh, 60);
        Assert.False(gated.EvaluatedThisWindow); // 冷却门在判定之前
        Assert.Equal(120, gated.CooldownReadyTick);

        engine.Step(120, world); // cooldown over → evaluates + acts again
        Assert.Equal(2, engine.FireLog.Count(r => r.ScrollId == ZoneScrollHigh && r.Outcome == ScrollFireOutcome.Executed));
    }

    [Fact]
    public void Rule1_non_monotonic_tick_is_rejected()
    {
        // illegal: 回退/重复 tick 抛 ArgumentException（确定性单线程纪律）。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        engine.Step(30, world);
        _ = Assert.Throws<ArgumentException>(() => engine.Step(30, world));
        _ = Assert.Throws<ArgumentException>(() => engine.Step(29, world));
    }

    // ================================================================ rule 2
    // 规则 2（同塔优先级仲裁）：高优先级压制低优先级；同优先级后到者不生效。

    [Fact]
    public void Rule2_lower_priority_scroll_is_suppressed_on_a_contested_tower()
    {
        // legal: 高优先级先到并占据塔 → 低优先级同塔被压制（SuppressedAllTargets）。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60));
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollLow, priority: 40));

        engine.Step(30, world);

        Assert.True(engine.TryGetTowerController(101, out string? controller));
        Assert.Equal(ZoneScrollHigh, controller);
        Assert.Contains(engine.FireLog, r => r.ScrollId == ZoneScrollLow && r.Outcome == ScrollFireOutcome.SuppressedAllTargets);
        Assert.Single(engine.ActiveEffects, e => e.ScrollId == ZoneScrollHigh);
        Assert.DoesNotContain(engine.ActiveEffects, e => e.ScrollId == ZoneScrollLow);
    }

    [Fact]
    public void Rule2_equal_priority_latecomer_does_not_take_over()
    {
        // illegal-ish（负向）: 同优先级后到者不生效——在位者保持控制。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60));
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollEqual, priority: 60));

        engine.Step(30, world);

        Assert.True(engine.TryGetTowerController(101, out string? controller));
        Assert.Equal(ZoneScrollHigh, controller); // incumbent wins the tie
        Assert.Contains(engine.FireLog, r => r.ScrollId == ZoneScrollEqual && r.Outcome == ScrollFireOutcome.SuppressedAllTargets);
    }

    [Fact]
    public void Rule2_higher_priority_preempts_the_incumbent()
    {
        // legal: 更高优先级后来 → 跨窗接管在位者的塔控制（压制关系反转）。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60, cooldownTicks: 3000));

        engine.Step(30, world);
        Assert.True(engine.TryGetTowerController(101, out string? first));
        Assert.Equal(ZoneScrollHigh, first);

        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollTop, priority: 80, cooldownTicks: 3000));
        engine.Step(60, world); // high still holds (cooldown) → top evaluates and preempts

        Assert.True(engine.TryGetTowerController(101, out string? controller));
        Assert.Equal(ZoneScrollTop, controller);
        Assert.Contains(engine.FireLog, r => r.ScrollId == ZoneScrollTop && r.Outcome == ScrollFireOutcome.Executed);
    }

    [Fact]
    public void Rule2_control_expires_with_the_effect_and_the_waiter_takes_over()
    {
        // boundary: 在位效果到期 → 塔重新可争，先到者（本例=唯一冷却就绪者）接管。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60, cooldownTicks: 3000, durationTicks: 30));
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollLow, priority: 40, cooldownTicks: 0, durationTicks: 30));

        engine.Step(30, world); // high wins; low suppressed
        engine.Step(60, world); // high effect expired at 60 (until 60), high in cooldown → low now fires

        Assert.True(engine.TryGetTowerController(101, out string? controller));
        Assert.Equal(ZoneScrollLow, controller);
        Assert.Single(engine.ActiveEffects, e => e.ScrollId == ZoneScrollLow); // low's own 30t effect now live
    }

    // ================================================================ rule 3
    // 规则 3（常驻上限）：6+2×E，超出拒绝。

    public static TheoryData<int, int> CapRows => new()
    {
        // legal/boundary: E1=8, E2=10, E6=18（6+2×E，附录 F.1）。
        { 1, 8 },
        { 2, 10 },
        { 6, 18 },
    };

    [Theory]
    [MemberData(nameof(CapRows))]
    public void Rule3_resident_cap_is_6_plus_2_times_era(int era, int cap)
    {
        var (engine, world, _) = CreateEngine(era);
        Assert.Equal(cap, engine.ResidentCap);

        for (int i = 0; i < cap; i++)
        {
            // unlock era 1 so every era can fill its cap
            var scroll = ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-" + i, priority: 10 + i);
            var unlocked = new ScrollDefinition(
                scroll.Id, scroll.SpId, scroll.Kind, scroll.NameZh, scroll.NameEn, scroll.Aliases,
                scroll.Priority, scroll.CooldownTicks, UnlockEra: 1, scroll.Condition, scroll.Action,
                scroll.PerTowerPerWaveLimit, scroll.Acquisition);
            Assert.Equal(ScrollEquipResult.Ok, engine.Equip(unlocked));
        }

        // illegal: the (cap+1)-th equip is refused, roster unchanged.
        var overflowBase = ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-overflow", priority: 99);
        var overflow = new ScrollDefinition(
            overflowBase.Id, overflowBase.SpId, overflowBase.Kind, overflowBase.NameZh, overflowBase.NameEn,
            overflowBase.Aliases, overflowBase.Priority, overflowBase.CooldownTicks, UnlockEra: 1,
            overflowBase.Condition, overflowBase.Action, overflowBase.PerTowerPerWaveLimit, overflowBase.Acquisition);
        Assert.Equal(ScrollEquipResult.RejectedCapExceeded, engine.Equip(overflow));
        Assert.Equal(cap, engine.EquippedScrollIds.Count);
    }

    [Fact]
    public void Rule3_duplicate_and_era_locked_equips_are_refused()
    {
        // illegal: 同 id 重复装备拒绝；unlock_era>当前时代拒绝。
        var (engine, world, _) = CreateEngine(era: 2);
        var scroll = ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-dup", priority: 50);
        Assert.Equal(ScrollEquipResult.Ok, engine.Equip(scroll));
        Assert.Equal(ScrollEquipResult.RejectedDuplicate, engine.Equip(scroll));

        var locked = ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-future", priority: 50);
        var era3 = new ScrollDefinition(
            locked.Id, locked.SpId, locked.Kind, locked.NameZh, locked.NameEn, locked.Aliases,
            locked.Priority, locked.CooldownTicks, UnlockEra: 3, locked.Condition, locked.Action,
            locked.PerTowerPerWaveLimit, locked.Acquisition);
        Assert.Equal(ScrollEquipResult.RejectedEraLocked, engine.Equip(era3));

        // boundary: era transition re-anchors the cap and unlocks the equip.
        engine.SetEra(3);
        Assert.Equal(12, engine.ResidentCap);
        Assert.Equal(ScrollEquipResult.Ok, engine.Equip(era3));
    }

    [Fact]
    public void Rule3_unequip_releases_control_and_frees_a_slot()
    {
        // legal: 卸下释放其控制与效果，腾出常驻位。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60));
        engine.Step(30, world);
        Assert.True(engine.TryGetTowerController(101, out _));
        Assert.Single(engine.ActiveEffects);

        Assert.True(engine.Unequip(ZoneScrollHigh));
        Assert.False(engine.TryGetTowerController(101, out _));
        Assert.Empty(engine.ActiveEffects);
        Assert.Empty(engine.EquippedScrollIds);

        Assert.False(engine.Unequip(ZoneScrollHigh)); // illegal: idempotent refusal
    }

    // ================================================================ rule 4
    // 规则 4（系统仲裁）：系统显式指令 > 卷轴，至本波结束。

    [Fact]
    public void Rule4_system_order_suspends_scroll_control_until_wave_end()
    {
        // legal: 系统对塔 T 下显式指令 → 该塔的卷轴动作被压制至本波结束。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        world.AddTower(102, "z1");
        var scroll = ScrollTestDefs.FocusFireScroll("t:scroll-ff", priority: 50, cooldownTicks: 0);
        _ = engine.Equip(scroll);

        engine.Step(30, world);
        Assert.True(engine.TryGetTowerController(101, out _)); // scroll claims both towers

        engine.NotifySystemTowerOrder(new[] { 101L }, tick: 31, wave: 1);
        Assert.True(engine.IsSystemOverridden(101));
        Assert.False(engine.IsSystemOverridden(102));

        engine.Step(60, world); // both towers contestable again, but 101 is system-overridden
        var fired = engine.FireLog.Last(r => r.ScrollId == "t:scroll-ff");
        Assert.Equal(ScrollFireOutcome.Executed, fired.Outcome); // 102 still scroll-addressable
        Assert.False(engine.TryGetTowerController(101, out _)); // 101 not re-claimed
        Assert.True(engine.TryGetTowerController(102, out _));

        // negative（波结束解除）: wave change clears the override → control restored next fire.
        world.CurrentWave = 2;
        engine.Step(90, world);
        Assert.False(engine.IsSystemOverridden(101));
        Assert.True(engine.TryGetTowerController(101, out _));
    }

    [Fact]
    public void Rule4_full_suppression_when_every_addressed_tower_is_system_overridden()
    {
        // illegal（负向）: 目标塔全部被系统接管 → 整次动作 SuppressedAllTargets，不进冷却。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        var scroll = ScrollTestDefs.FocusFireScroll("t:scroll-ff", priority: 50, cooldownTicks: 3000);
        _ = engine.Equip(scroll);

        engine.NotifySystemTowerOrder(new[] { 101L }, tick: 1, wave: 1);
        engine.Step(30, world);

        Assert.Contains(engine.FireLog, r =>
            r.ScrollId == "t:scroll-ff" && r.Outcome == ScrollFireOutcome.SuppressedAllTargets);
        Assert.Equal(0, engine.GetBudget("t:scroll-ff", 30).CooldownReadyTick); // suppressed fires bill no cooldown
    }

    [Fact]
    public void Rule4_explicit_end_wave_clears_overrides_and_wave_scoped_state()
    {
        // boundary: 手动 EndWave 与自动波变等效（系统仲裁至「本波」结束）。
        var (engine, world, _) = CreateEngine();
        SeedZoneWorld(world);
        _ = engine.Equip(ScrollTestDefs.AlwaysTrueZoneScroll(ZoneScrollHigh, priority: 60));
        engine.NotifySystemTowerOrder(new[] { 101L }, tick: 1, wave: 3);
        Assert.True(engine.IsSystemOverridden(101));

        engine.EndWave(3);
        Assert.False(engine.IsSystemOverridden(101));
    }

    // ================================================================ per-tower-per-wave (SC-004)
    [Fact]
    public void PerTowerPerWave_limit_caps_heals_at_one_and_resets_next_wave()
    {
        // legal: SC-004 每塔每波 1 次——同一波第二次低血不回复；下一波恢复。
        var sink = new RecordingEffectSink();
        var healing = new ScrollEngine(2, new RecordingExecutor(), sink);
        var world = new ScriptedScrollWorld { CurrentWave = 1 };
        world.ZoneFacts.Add(ScriptedScrollWorld.Zone("z1", (0, 0)));
        _ = world.AddTower(201, "z1", hp: 80, maxHp: 200); // 40% ≤ 50%
        _ = healing.Equip(ScrollTestDefs.HealScroll("t:scroll-heal", priority: 70));

        healing.Step(30, world);
        Assert.Single(sink.Heals);
        Assert.Equal((201L, 40), sink.Heals[0]); // 20% × maxHp 200 = 40

        healing.Step(60, world); // same wave, tower still low → per-tower limit blocks
        var second = healing.FireLog.Last(r => r.ScrollId == "t:scroll-heal");
        Assert.Equal(ScrollFireOutcome.PerTowerLimitReached, second.Outcome);
        Assert.Single(sink.Heals);

        world.CurrentWave = 2;
        healing.Step(90, world);
        Assert.Equal(2, sink.Heals.Count); // new wave resets the per-tower budget
    }

    // ================================================================ condition vocabulary (SP-5 覆盖)
    [Theory]
    [InlineData("enemy_hp_below_percent", true)]   // legal: 敌 HP<阈值成立
    [InlineData("enemy_hp_below_percent", false)]  // negative: 阈值之上不触发
    public void Condition_family_enemy_hp_threshold(string conditionKind, bool below)
    {
        // legal: 敌 HP<阈值（[NEW] 条件族示例，percent=0.5 严格小于）
        var (engine, world, _) = CreateEngine();
        world.AddEnemy(1, null, hp: below ? 40 : 90, maxHp: 100, x: 0, y: 0);
        var scroll = ScrollTestDefs.AlwaysTrueZoneScroll("t:scroll-hp", priority: 50);
        var conditioned = ReplaceCondition(scroll, new ScrollCondition(
            ScrollConditionKind.EnemyHpBelowPercent, new[] { new ScrollConditionArg("percent", 0.5, null) }));
        _ = engine.Equip(conditioned);
        _ = conditionKind;
        // scope=trigger_zone 但该条件无 zone 上下文 → 动作无塔目标，效果作用于空集：
        // 触发事实以 FireLog 为准。
        engine.Step(30, world);
        ScrollFireRecord? record = engine.FireLog.FirstOrDefault(r => r.ScrollId == conditioned.Id);
        if (below)
        {
            Assert.NotNull(record);
            Assert.Equal(ScrollFireOutcome.Executed, record.Outcome);
            Assert.Single(engine.ActiveEffects); // zone effect with empty claims still lands
        }
        else
        {
            Assert.Null(record);
            Assert.Empty(engine.ActiveEffects);
        }
    }

    [Fact]
    public void Condition_family_wave_number_and_resource_and_distance()
    {
        // legal: 波次=N / 资源>阈值 / 位置距离三族各一正例（SP-5 条件四族覆盖）。
        var (engine, world, _) = CreateEngine();
        world.AddEnemy(1, null, 90, 90, x: 5, y: 5);
        world.ResourceAmounts["era2:res-timber"] = 300.0;

        foreach (var (id, condition) in new[]
                 {
                     ("t:scroll-wave", new ScrollCondition(
                         ScrollConditionKind.WaveNumberIs, new[] { new ScrollConditionArg("wave", 1, null) })),
                     ("t:scroll-res", new ScrollCondition(
                         ScrollConditionKind.ResourceAbove, new[]
                         {
                             new ScrollConditionArg("resource", 0, "era2:res-timber"),
                             new ScrollConditionArg("amount", 200, null),
                         })),
                     ("t:scroll-dist", new ScrollCondition(
                         ScrollConditionKind.EnemyDistanceToCellBelow, new[]
                         {
                             new ScrollConditionArg("x", 5, null),
                             new ScrollConditionArg("y", 5, null),
                             new ScrollConditionArg("cells", 1.5, null),
                         })),
                 })
        {
            var scroll = ScrollTestDefs.AlwaysTrueZoneScroll(id, priority: 50, cooldownTicks: 3000);
            _ = engine.Equip(ReplaceCondition(scroll, condition));
        }

        world.CurrentWave = 1;
        engine.Step(30, world);

        Assert.Equal(3, engine.FireLog.Count(r => r.Outcome == ScrollFireOutcome.Executed));
    }

    [Fact]
    public void Condition_family_zone_count_any_zone_picks_first_satisfied_zone()
    {
        // legal: zone="*"（某分区）——id 序第一个满足阈值的分区成为触发分区。
        var (engine, world, executor) = CreateEngine();
        world.ZoneFacts.Add(ScriptedScrollWorld.Zone("aa", (0, 0)));
        world.ZoneFacts.Add(ScriptedScrollWorld.Zone("zz", (1, 0)));
        world.AddEnemy(1, "zz", 90, 90);
        world.AddEnemy(2, "zz", 90, 90);
        world.AddTower(301, "zz");

        var scroll = ScrollTestDefs.FocusFireScroll("t:scroll-count", priority: 50);
        var conditioned = ReplaceCondition(scroll, new ScrollCondition(
            ScrollConditionKind.ZoneEnemyCountAtLeast,
            new[] { new ScrollConditionArg("zone", 0, "*"), new ScrollConditionArg("count", 2, null) }));
        _ = engine.Equip(conditioned);

        engine.Step(30, world);

        Assert.Single(executor.Calls);
        Assert.Contains("\"target_id\":1", executor.Calls[0].Json); // strongest (tie) → min id
        Assert.True(engine.TryGetTowerController(301, out string? controller));
        Assert.Equal("t:scroll-count", controller); // zz towers claimed by the trigger zone
    }

    private static ScrollDefinition ReplaceCondition(ScrollDefinition scroll, ScrollCondition condition) => new(
        scroll.Id, scroll.SpId, scroll.Kind, scroll.NameZh, scroll.NameEn, scroll.Aliases,
        scroll.Priority, scroll.CooldownTicks, scroll.UnlockEra, condition, scroll.Action,
        scroll.PerTowerPerWaveLimit, scroll.Acquisition);
}
