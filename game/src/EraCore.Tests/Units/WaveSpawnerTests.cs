using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [F-030] 波次生成引擎全参数化（C-10 E1 薄版，T-039）：出生数量逐波对照
// T-036 公式库直接输出（禁止重算公式）；[F-031] 精英线独立公式；
// [F-033] Boss 波 ×1.5HP/×1.3 伤；[F-034] 节奏区间：第 N 波全部入场 ≤ 波间隔。
// 参数集 = T-035 金样校准头（threat 0.75 / base 10 / begin 5 / spacing 5 /
// scaling 2.0，见 game/content/golden/wave-e2-golden.json params 头）。
public class WaveSpawnerTests
{
    private const double ThreatLevelDefault = 0.75; // F.5 wave-5 验算基准威胁度
    private const int Base = 10;
    private const int Begin = 5;
    private const int Spacing = 5;
    private const double Scaling = 2.0;
    private const int Batches = 4;

    private static WaveSpawnConfig NewConfig(
        bool elite = true,
        int bossWave = 20,
        int entryCellCount = 1,
        int batches = Batches,
        double valleySeconds = 15.0,
        double threat = ThreatLevelDefault) => new(
        BaseCount: Base,
        BeginWave: Begin,
        Spacing: Spacing,
        Scaling: Scaling,
        ThreatLevel: threat,
        RegularRoster: new[] { "era1:enemy-beastling" },
        EliteLineEnabled: elite,
        EliteEnemyId: elite ? "era1:enemy-ram" : null,
        BossWave: bossWave,
        BossEnemyId: "era1:enemy-boss",
        BossHpMultiplier: 1.5,
        BossDamageMultiplier: 1.3,
        FirstWaveGraceMultiplier: 1.5,
        ValleySeconds: valleySeconds,
        BatchesPerWave: batches,
        TickRateHz: 30,
        CellSizePx: 32,
        EntryCells: Enumerable.Range(0, entryCellCount).Select(i => new CellCoord(1 + i, 0)).ToList(),
        Trample: new TrampleProfile(Damage: 60, PeriodTicks: 45));

    private static WaveSpawner NewSpawner(WaveSpawnConfig? config = null, long seed = 1) =>
        new(UnitsContent.LoadEnemyCatalog(), config ?? NewConfig(), new SplitMixRng(seed), new FixedClock());

    // ---- 1) 出生数量：1~30 波逐波对照 T-036 直接输出 [F-030] [F-031] [F-033] ----

    [Fact]
    public void Wave_table_1_to_30_matches_the_T036_library_output_exactly()
    {
        // 期望值全部来自对 T-036 公式库的直接调用（非手工重算）——本测试证明
        // spawner 只是公式库的参数化搬运，未复实现任何公式。
        WaveSpawner spawner = NewSpawner();
        double countMultiplier = Threat.Multipliers(ThreatLevelDefault).Count; // t=0.75 → 1.25

        for (int wave = 1; wave <= 30; wave++)
        {
            WavePlan plan = spawner.PlanWave(wave, startTick: 0);

            int expectedRegular = WaveFormula.Count(wave, Base, Begin, Spacing, Scaling, countMultiplier);
            int expectedElite = Elite.Count(wave); // 独立精英线（F-031）
            Assert.Equal(expectedRegular, plan.RegularCount);
            Assert.Equal(expectedElite, plan.EliteCount);
            Assert.Equal(wave == 20, plan.HasBoss); // [F-033] 终局关第 20 波恰为 Boss 波
            // 守恒：普通波条目总数 = 正规+精英；Boss 波按 [NEW-E1 薄版] 独下 Boss
            Assert.Equal(
                plan.HasBoss ? 1 : expectedRegular + expectedElite,
                plan.TotalCount);
        }
    }

    [Fact] // legal — F.5 锚点抽核：第 5 波 6 敌（附录 F.5 wave-5 行）
    public void Wave_5_matches_the_F5_anchor_six_enemies()
    {
        WavePlan plan = NewSpawner().PlanWave(5, startTick: 0);
        Assert.Equal(6, plan.RegularCount); // (10+0)/2×1.25 = 6.25 → 6（F.5）
        Assert.False(plan.HasBoss);
    }

    [Fact] // boundary — 波 0/负波：拒绝（1-based 契约）
    public void PlanWave_rejects_non_positive_waves()
    {
        WaveSpawner spawner = NewSpawner();
        Assert.Throws<ArgumentOutOfRangeException>(() => spawner.PlanWave(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => spawner.PlanWave(-3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => spawner.PlanWave(1, -1));
    }

    // ---- 2) 节奏区间：全部入场时间 ≤ 波间隔 [F-034] [NEW 波内出生节奏] ----

    [Theory] // legal — 各波全部入场时间落在波间隔内（谷保留后窗口）
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(30)]
    public void Every_wave_fully_enters_within_its_interval(int wave)
    {
        WaveSpawner spawner = NewSpawner();
        WavePlan plan = spawner.PlanWave(wave, startTick: 1000);

        Assert.Equal(1000, plan.StartTick);
        Assert.Equal(spawner.IntervalTicks, plan.IntervalTicks);
        Assert.True(
            plan.LastEntryTick - plan.StartTick <= plan.IntervalTicks,
            $"wave {wave}: 入场窗口 {plan.LastEntryTick - plan.StartTick} ticks 超过波间隔 {plan.IntervalTicks}");
        Assert.True(plan.Batches.Count >= 1); // 有敌人的波必有入场批次
    }

    [Fact] // legal — 分批节奏：4 批均匀铺满（间隔−谷），批距一致
    public void Batches_spread_evenly_over_interval_minus_valley()
    {
        const int Wave = 12; // 6 正规（(10+1)/2×1.25→6）+ 2 精英（1+⌊4/4⌋）
        WaveSpawner spawner = NewSpawner();
        long interval = spawner.IntervalTicks;
        long valley = 15 * 30; // ValleySeconds=15 → 450 ticks
        WavePlan plan = spawner.PlanWave(Wave, startTick: 0);

        var regularBatches = plan.Batches.Where(b => b.IsRegular).ToList();
        Assert.Equal(4, regularBatches.Count); // BatchesPerWave=4
        long expectedSpacing = (interval - valley) / 3; // entryWindow/(批数−1)
        for (int i = 0; i < regularBatches.Count; i++)
        {
            Assert.Equal(i * expectedSpacing, regularBatches[i].TickOffset);
        }

        // 波内谷：末批之后到下一波前 ≥ 15 s 低密度窗口（F-034）
        Assert.True(plan.LastEntryTick + valley <= plan.IntervalTicks);
    }

    [Fact] // boundary — 单批退化（BatchesPerWave=1）：全部同刻入场仍满足区间
    public void Single_batch_wave_enters_at_offset_zero()
    {
        WaveSpawner spawner = NewSpawner(NewConfig(batches: 1));
        WavePlan plan = spawner.PlanWave(8, startTick: 50);

        var regular = plan.Batches.Where(b => b.IsRegular).ToList();
        Assert.Single(regular);
        Assert.Equal(0, regular[0].TickOffset);
        Assert.Equal(50, plan.LastEntryTick);
    }

    [Fact] // legal — 自主推进（IClock 驱动）：首波经宽限、逐波间隔推进、跨波不重叠
    public void Autonomous_advance_respects_grace_and_intervals()
    {
        var clock = new FixedClock();
        var spawner = new WaveSpawner(
            UnitsContent.LoadEnemyCatalog(),
            NewConfig(entryCellCount: 3),
            new SplitMixRng(7),
            clock);

        long grace = spawner.GraceTicks;
        long interval = spawner.IntervalTicks;
        Assert.Equal((long)Math.Round(120.0 * Threat.Multipliers(ThreatLevelDefault).Interval * 1.5 * 30), grace);

        var allSpawns = new List<Unit>();
        var waveBounds = new Dictionary<int, (long First, long Last)>();
        for (long tick = 0; tick <= grace + (2 * interval) + 10; tick++)
        {
            clock.Set(tick);
            foreach (Unit unit in spawner.Advance())
            {
                allSpawns.Add(unit);
                var (first, last) = waveBounds.TryGetValue(spawner.CurrentWave, out var b) ? b : (tick, tick);
                waveBounds[spawner.CurrentWave] = (Math.Min(first, tick), Math.Max(last, tick));
            }
        }

        // 首个出生不早于宽限结束（F-034 首波宽限 ×1.5）
        Assert.True(allSpawns[0].SpawnTick >= grace, $"首出生 {allSpawns[0].SpawnTick} 早于宽限 {grace}");
        // 每波最后出生 ≤ 该波开始 + 间隔（节奏区间在自主模式下同样成立）
        foreach ((int wave, (long first, long last)) in waveBounds)
        {
            long start = spawner.WaveStartTick(wave);
            Assert.True(first >= start, $"wave {wave} 早生");
            Assert.True(last - start <= interval, $"wave {wave} 末生越界");
        }

        // 推进了三波（1/2/3），实体 id 从 1 起单调发号
        Assert.Equal(new[] { 1, 2, 3 }, waveBounds.Keys.OrderBy(k => k));
        Assert.Equal(Enumerable.Range(1, allSpawns.Count).Select(i => (long)i), allSpawns.Select(u => u.Id.Value));
    }

    // ---- 3) Boss 波：×1.5 HP / ×1.3 伤 [F-033] ----

    [Fact]
    public void Boss_wave_applies_state_multipliers_to_the_stomper()
    {
        // SP-3a L107 + v3.2 §6：2500/甲6 ×1.5HP；践踏基础伤 60 ×1.3 = 78 [NEW]
        WaveSpawner spawner = NewSpawner();
        spawner.BeginWave(20, startTick: 0);

        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        Unit boss = Assert.Single(spawned);
        Assert.Equal("era1:enemy-boss", boss.ContentId);
        Assert.True(boss.IsBoss);
        Assert.Equal(3750, boss.MaxHp);              // ceil(2500 × 1.5)
        Assert.Equal(3750, boss.Hp);
        Assert.Equal(6, boss.Armor);
        Assert.Equal(0.75f, boss.SpeedPxPerTick);
        Assert.Equal(0, boss.SpawnTick);
        Assert.Equal(78, boss.TrampleDamage);       // ceil(60 × 1.3)
        Assert.Equal(64f, boss.TrampleRadiusPx);    // 2 格 × 32 px（SP-3a L107）
        Assert.Equal(45, boss.TramplePeriodTicks);  // TrampleProfile 注入值
    }

    [Fact] // legal — 非 Boss 波：无 Boss 状态乘子，威胁 HP 乘子照乘
    public void Regular_units_carry_threat_hp_multiplier_but_no_boss_multipliers()
    {
        // t=0.75 → HP 乘子 0.5+(0.75−0.3)/0.9 = 1.0（F.2 表），兽崽 40 保持 40
        WaveSpawner spawner = NewSpawner();
        WavePlan plan = spawner.BeginWave(5, startTick: 0);

        IReadOnlyList<Unit> spawned = spawner.Poll(plan.LastEntryTick); // 整波物化

        Assert.Equal(6, spawned.Count); // F.5 第 5 波锚点
        Assert.All(spawned, u => Assert.False(u.IsBoss));
        Assert.All(spawned, u => Assert.Equal(40, u.MaxHp)); // 40 × 1.0
        Assert.All(spawned, u => Assert.Equal(0, u.TrampleDamage)); // 无践踏注入
        Assert.All(spawned, u => Assert.Equal("era1:enemy-beastling", u.ContentId));
    }

    [Fact] // legal — 威胁 HP 乘子生效面：t=0.3（带下沿）HP 减半
    public void Threat_hp_multiplier_scales_spawned_units()
    {
        WaveSpawner spawner = NewSpawner(NewConfig(threat: 0.3));
        spawner.BeginWave(5, startTick: 0);

        IReadOnlyList<Unit> spawned = spawner.Poll(0);

        // t=0.3 → HP 乘子 0.5：兽崽 40 → 20（F.2 表 0.3 行）
        Assert.All(spawned, u => Assert.Equal(20, u.MaxHp));
    }

    // ---- 4) 结构非法行 [F-030] ----

    [Fact] // illegal — 配置违约：空花名册/空入口/批数<1/谷≥间隔/负威胁/非正乘子
    public void Constructor_rejects_structurally_invalid_configs()
    {
        EnemySpecCatalog catalog = UnitsContent.LoadEnemyCatalog();
        var rng = new SplitMixRng(1);
        var clock = new FixedClock();

        Assert.Throws<ArgumentException>(() => new WaveSpawner(
            catalog, NewConfig() with { RegularRoster = Array.Empty<string>() }, rng, clock));
        Assert.Throws<ArgumentException>(() => new WaveSpawner(
            catalog, NewConfig() with { EntryCells = Array.Empty<CellCoord>() }, rng, clock));
        Assert.Throws<ArgumentException>(() => new WaveSpawner(
            catalog, NewConfig(batches: 0), rng, clock));
        Assert.Throws<ArgumentException>(() => new WaveSpawner(
            catalog, NewConfig(valleySeconds: 200.0), rng, clock)); // t=0.75 间隔 156 s（120×1.3），谷吞尽即拒
        Assert.Throws<ArgumentException>(() => new WaveSpawner(
            catalog, NewConfig(threat: -0.1), rng, clock));
        Assert.Throws<ArgumentException>(() => new WaveSpawner(
            catalog, NewConfig() with { BossHpMultiplier = 0 }, rng, clock));
    }

    [Fact] // illegal — 未开波先 Poll：明确拒绝（不静默返回）
    public void Poll_without_a_begun_wave_throws()
    {
        WaveSpawner spawner = NewSpawner();
        Assert.Throws<InvalidOperationException>(() => spawner.Poll(0));
    }

    [Fact] // legal — 手动开波物化：批次按偏移逐批入场、花名册轮转、入口随机
    public void Poll_materializes_batches_at_their_offsets()
    {
        WaveSpawner spawner = NewSpawner(NewConfig(entryCellCount: 2));
        WavePlan plan = spawner.BeginWave(12, startTick: 100); // 6 正规 + 2 精英

        Assert.Equal(8, plan.TotalCount);
        Assert.Empty(spawner.Poll(99));         // 波开始前无出生
        var first = spawner.Poll(100);          // 批 0（正+精同刻）
        Assert.Equal(3, first.Count);           // 2 正规（6→[2,2,1,1]）+ 1 精英（2→[1,1,0,0]）
        Assert.Contains(first, u => u.IsElite && u.ContentId == "era1:enemy-ram"); // 精英行独立物种
        Assert.Equal(2, first.Count(u => !u.IsBoss && !u.IsElite && u.ContentId == "era1:enemy-beastling"));

        var second = spawner.Poll(100 + ((spawner.IntervalTicks - 450) / 3));
        Assert.Equal(3, second.Count);          // 批 1：2 正规 + 1 精英
        var rest = spawner.Poll(plan.LastEntryTick + 10000);
        Assert.Equal(2, rest.Count);            // 剩余两批（各 1 正规）一次性到期
        // 物化总量守恒
        Assert.Equal(plan.TotalCount, first.Count + second.Count + rest.Count);
    }
}
