using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Units;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Units;

// [C-10] UnitMovement（T-039）：沿 FlowField.NextStep 梯度移动
// （px/tick@30Hz，速度保距）+ [F-033] 踏群者践踏（半径 2 格周期伤害，
// 周期/伤害 = TrampleProfile 注入的 [NEW-原型校准] 值）+ [F-033] 战吼
// （T-114：era3 Boss trait war_cry 主动化周期脉冲，600-tick 冷却网格）。
// 网格用真实 GridWorld（T-032，只读复用）+ 真实 FlowField（T-037）。
public class UnitMovementTests
{
    private const int CellSize = 32;

    // 全平原直走廊（比例全 0 → 无地形阻挡），目标在 (7,3)。
    private static FlowField StraightField(int width = 8, int height = 8)
    {
        var world = new GridWorld(width, height, new SplitMixRng(1), forestRatio: 0, rockRatio: 0, waterRatio: 0);
        return FlowField.BuildComplete(world, new CellCoord(width - 1, 3));
    }

    private static Unit NewUnit(float x, float y, float speed, long spawnTick = 0)
    {
        var unit = new Unit(new EntityId(1), "era1:enemy-test", x, y, 100, 0, speed);
        unit.MarkSpawned(spawnTick);
        return unit;
    }

    [Fact] // legal — 梯度直进：直走廊上每 tick 位移恰为速度（保距、无漂移）
    public void Straight_corridor_moves_exactly_speed_pixels_per_tick()
    {
        FlowField field = StraightField(); // 行 3 直走廊：目标 (7,3)
        var movement = new UnitMovement(() => field, CellSize);
        Unit unit = NewUnit(16f, 3 * CellSize + 16f, speed: 0.75f); // 格 (0,3) 中心

        float previousX = unit.X;
        float previousY = unit.Y;
        for (int tick = 1; tick <= 40; tick++)
        {
            movement.Step(unit);
            float dx = MathF.Abs(unit.X - previousX);
            float dy = MathF.Abs(unit.Y - previousY);
            float travelled = MathF.Sqrt((dx * dx) + (dy * dy));
            Assert.Equal(0.75f, travelled, precision: 4); // 容差：float 步进的 1e-4 量级
            previousX = unit.X;
            previousY = unit.Y;
        }

        // 40 tick × 0.75 = 30 px：沿走廊向东推进 30 px，Y 不变（直线走廊）
        Assert.Equal(16f + 30f, unit.X, precision: 2);
        Assert.Equal(3 * CellSize + 16f, unit.Y);
        Assert.False(unit.ArrivedAtTarget);
    }

    [Fact] // legal — 对角保距：对角步长也恰为速度（归一化方向，不因对角变快）
    public void Diagonal_step_length_equals_speed()
    {
        // 目标 (7,7)：从 (0,0) 出发对角线严格更省（6×14 < 任意含直步方案）
        var world = new GridWorld(8, 8, new SplitMixRng(1), 0, 0, 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(7, 7));
        var movement = new UnitMovement(() => field, CellSize);
        Unit unit = NewUnit(16f, 16f, speed: 1.6f); // 格 (0,0) 中心 → 首步对角 (1,1)

        float previousX = unit.X;
        float previousY = unit.Y;
        movement.Step(unit);

        float dx = unit.X - previousX;
        float dy = unit.Y - previousY;
        float travelled = MathF.Sqrt((dx * dx) + (dy * dy));
        Assert.Equal(1.6f, travelled, precision: 5);
        Assert.Equal(MathF.Abs(dx), MathF.Abs(dy), precision: 4); // 45° 对角
        Assert.True(dx > 0 && dy > 0); // 朝目标方向
    }

    [Fact] // legal — 到达：进入目标格即标记 ArrivedAtTarget 且停止移动
    public void Arrival_marks_unit_and_stops_movement()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit unit = NewUnit(7 * CellSize + 16f, 3 * CellSize + 16f, speed: 0.75f); // 已在目标格中心

        movement.Step(unit);

        Assert.True(unit.ArrivedAtTarget);
        Assert.Equal(7 * CellSize + 16f, unit.X);
        Assert.Equal(3 * CellSize + 16f, unit.Y);

        // 到达后继续 Step：位置冻结
        for (int i = 0; i < 10; i++)
        {
            movement.Step(unit);
        }

        Assert.Equal(7 * CellSize + 16f, unit.X);
        Assert.Equal(3 * CellSize + 16f, unit.Y);
    }

    [Fact] // legal — 一步跨程内吸附到下一格中心（防绕心振荡），下一步即到达
    public void Within_one_stride_the_unit_lands_on_the_next_cell_centre()
    {
        FlowField field = StraightField(); // 行 3 走廊
        var movement = new UnitMovement(() => field, CellSize);
        // 格 (6,3) 内距下格中心 (240,112) 31.5 px < 速度 40 → 吸附
        Unit unit = NewUnit(6 * CellSize + 16f + 0.5f, 3 * CellSize + 16f, speed: 40f);

        movement.Step(unit);

        Assert.False(unit.ArrivedAtTarget); // 已吸附到目标格中心但尚未再 Step
        Assert.Equal(7 * CellSize + 16f, unit.X, precision: 5);
        Assert.Equal(3 * CellSize + 16f, unit.Y, precision: 5);

        movement.Step(unit); // 现在位于目标格 → 到达
        Assert.True(unit.ArrivedAtTarget);
    }

    [Fact] // legal — 死亡单位不动
    public void Dead_units_do_not_move()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit unit = NewUnit(16f, 112f, speed: 2.56f);
        unit.ApplyDamage(1000, tick: 1);

        movement.Step(unit);

        Assert.Equal(16f, unit.X);
        Assert.Equal(112f, unit.Y);
    }

    [Fact] // illegal — 未就绪流场：NextStep 契约的 InvalidOperationException 透传
    public void Stepping_against_an_unready_field_throws()
    {
        var world = new GridWorld(4, 4, new SplitMixRng(1), 0, 0, 0);
        FlowField unready = FlowField.StartBuild(world, new CellCoord(3, 3)); // 未 Advance
        var movement = new UnitMovement(() => unready, CellSize);
        Unit unit = NewUnit(16f, 16f, 1.0f);

        Assert.Throws<InvalidOperationException>(() => movement.Step(unit));
    }

    [Fact] // illegal — 供应商返回 null 与非正 cellSize 均拒绝
    public void Constructor_and_supplier_reject_invalid_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => new UnitMovement(null!));
        Assert.Throws<ArgumentException>(() => new UnitMovement(() => StraightField(), 0));
        var movement = new UnitMovement(() => null!);
        Assert.Throws<InvalidOperationException>(() => movement.Step(NewUnit(16f, 16f, 1f)));
    }

    // ---- 踏群者践踏 [F-033]（SP-3a L107：半径 2 格；周期/伤害 [NEW-原型校准]）----

    private sealed class FakeTarget : ITrampleTarget
    {
        public FakeTarget(float x, float y, int hp)
        {
            X = x;
            Y = y;
            Hp = hp;
        }

        public float X { get; }

        public float Y { get; }

        public bool Alive { get; private set; } = true;

        public int Hp { get; private set; }

        public int Hits { get; private set; }

        public void TakeTrample(int damage)
        {
            Hits++;
            Hp = Math.Max(0, Hp - damage);
            if (Hp == 0)
            {
                Alive = false;
            }
        }
    }

    [Fact] // legal — 周期脉冲：相位整周期才触发，半径内命中、半径外不命中
    public void Trample_pulses_only_on_whole_periods_and_only_inside_radius()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit boss = NewUnit(112f, 112f, speed: 0.75f, spawnTick: 0);
        boss.SetTrample(60, radiusPx: 64f, periodTicks: 45); // 2 格 × 32 px

        var inside = new FakeTarget(112f + 32f, 112f, 120);      // 距离 32 px ≤ 64
        var onEdge = new FakeTarget(112f + 64f, 112f, 120);      // 恰在半径边界（含）
        var outside = new FakeTarget(112f + 64.001f, 112f, 120); // 边界外（浮点外侧）
        var targets = new ITrampleTarget[] { inside, onEdge, outside };

        int pulses = 0;
        for (long tick = 1; tick <= 90; tick++)
        {
            if (movement.PulseTrample(boss, targets, tick).Count > 0)
            {
                pulses++;
            }
        }

        // 90 tick / 45 = 2 次脉冲（tick 45、90）；相位 0（tick==spawnTick）不触发
        Assert.Equal(2, pulses);
        Assert.Equal(2, inside.Hits);
        Assert.Equal(2, onEdge.Hits);   // 边界含（≤）
        Assert.Equal(0, outside.Hits);  // 界外零命中
        Assert.False(inside.Alive);     // 2×60=120 恰清空
        Assert.False(onEdge.Alive);
        Assert.True(outside.Alive);
    }

    [Fact] // boundary — 相位边界：tick 0/44/46 不触发，恰 45 触发
    public void Trample_phase_boundaries()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit boss = NewUnit(112f, 112f, 0.75f, spawnTick: 0);
        boss.SetTrample(60, 64f, 45);
        var target = new FakeTarget(130f, 112f, 1000);
        var targets = new ITrampleTarget[] { target };

        Assert.Empty(movement.PulseTrample(boss, targets, 0));   // 相位 0 排除
        Assert.Empty(movement.PulseTrample(boss, targets, 44));
        Assert.Single(movement.PulseTrample(boss, targets, 45)); // 首脉冲
        Assert.Empty(movement.PulseTrample(boss, targets, 46));
        Assert.Single(movement.PulseTrample(boss, targets, 90)); // 第二脉冲
        Assert.Equal(2, target.Hits);
    }

    [Fact] // legal — 死亡 Boss 与未配置践踏的单位：永不脉冲
    public void Dead_or_unarmed_units_never_pulse()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        var target = new FakeTarget(120f, 112f, 100);
        var targets = new ITrampleTarget[] { target };

        Unit dead = NewUnit(112f, 112f, 0.75f);
        dead.SetTrample(60, 64f, 45);
        dead.ApplyDamage(9999, tick: 1);
        Unit unarmed = NewUnit(112f, 112f, 0.75f); // 无 TrampleProfile

        for (long tick = 1; tick <= 100; tick++)
        {
            Assert.Empty(movement.PulseTrample(dead, targets, tick));
            Assert.Empty(movement.PulseTrample(unarmed, targets, tick));
        }

        Assert.Equal(0, target.Hits);
    }

    [Fact] // legal — 集成小回路：Boss 沿场走向目标并跨过路中塔，塔被践踏摧毁
    public void Walking_boss_destroys_a_tower_on_his_path_by_trample()
    {
        // 走廊 1×N：Boss 从 (0,0) 到 (0,9)；塔在 (0,4) 同路
        var world = new GridWorld(1, 10, new SplitMixRng(1), 0, 0, 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(0, 9));
        var movement = new UnitMovement(() => field, CellSize);

        var clock = new FixedClock();
        var spawner = new WaveSpawner(
            UnitsContent.LoadEnemyCatalog(),
            new WaveSpawnConfig(
                BaseCount: 6, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 0.75,
                RegularRoster: new[] { "era1:enemy-boss" },
                EliteLineEnabled: false,
                BossWave: 1, BossEnemyId: "era1:enemy-boss",
                BatchesPerWave: 1, EntryCells: new[] { new CellCoord(0, 0) },
                Trample: new TrampleProfile(60, 45)),
            new SplitMixRng(3),
            clock);
        spawner.BeginWave(1, startTick: 0);
        Unit boss = spawner.Poll(0).Single(u => u.IsBoss);

        // 塔在 Boss 必经之路（同列第 4 行，中心 (16,144)）；Boss 中心沿 x=16 直线
        var stats = UnitsContent.LoadWoodArrowTower();
        var tower = new TestTower(0 * CellSize + 16f, 4 * CellSize + 16f, stats);
        var units = new List<Unit> { boss };
        var towers = new TestTower[] { tower };
        bool towerDestroyed = false;
        long destroyedAt = -1;

        for (long tick = 1; tick <= 1200 && !boss.ArrivedAtTarget; tick++)
        {
            clock.Set(tick);
            movement.Step(boss);
            movement.PulseTrample(boss, towers, tick);
            tower.FireAt(units, tick);
            if (!tower.Alive && !towerDestroyed)
            {
                towerDestroyed = true;
                destroyedAt = tick;
            }
        }

        // 践踏摧毁路中塔：3×60=180 ≥ 120 HP（45 tick 周期，穿行窗口 ~171 tick）
        Assert.True(towerDestroyed, $"塔未摧毁（践踏命中 {tower.TrampleHitsTaken} 次，Boss 到达={boss.ArrivedAtTarget}）");
        Assert.True(tower.TrampleHitsTaken >= 2, "至少两个完整脉冲（2×60=120 恰摧毁）");
        Assert.True(destroyedAt > 0);
        // Boss 最终仍走完（薄版不因塔而停留）
        Assert.True(boss.ArrivedAtTarget);
        Assert.True(boss.Alive); // 单塔伤害远小于 3750
    }

    // ---- 战吼（T-114；era3 Boss trait war_cry：半径 3 格友军 +15%×180 ticks、
    // 冷却 600 ticks——主动化周期脉冲，区别于战鼓常驻光环）--------------------

    private static Unit NewCrier(float x, float y, long spawnTick = 0)
    {
        Unit crier = NewUnit(x, y, speed: 0.53f, spawnTick);
        crier.SetWarCry(radiusPx: 96f, speedBonusPercent: 15, durationTicks: 180, cooldownTicks: 600);
        return crier;
    }

    [Fact] // legal — 节拍+半径：600 网格开嗓、半径内（含边界）友军受鼓、界外与自身除外
    public void War_cry_pulses_on_the_cooldown_grid_and_buffs_allies_inside_the_radius()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit crier = NewCrier(112f, 112f);
        var near = NewUnit(112f + 32f, 112f, 1.0f);            // 距 32 px < 96
        var onEdge = NewUnit(112f + 96f, 112f, 1.0f);          // 恰在半径边界（含）
        var far = NewUnit(112f + 96.001f, 112f, 1.0f);         // 边界外（浮点外侧）
        var allies = new List<Unit> { crier, near, onEdge, far };

        Assert.Equal(2, movement.PulseWarCry(crier, allies, 600).Count); // 首拍：near+onEdge 双受鼓

        near.ResolveSpeedEffects(601);
        onEdge.ResolveSpeedEffects(601);
        far.ResolveSpeedEffects(601);
        crier.ResolveSpeedEffects(601);
        Assert.Equal(15, near.ResolvedSpeedBonusPercent);   // 半径内 +15%
        Assert.Equal(15, onEdge.ResolvedSpeedBonusPercent); // 边界含（≤）
        Assert.Equal(0, far.ResolvedSpeedBonusPercent);     // 界外零受益
        Assert.Equal(0, crier.ResolvedSpeedBonusPercent);   // 自身除外（战吼点友军）

        // 窗口界：tick<600+180=780 内持续、780 起过期（严格界）
        near.ResolveSpeedEffects(779);
        Assert.Equal(15, near.ResolvedSpeedBonusPercent);
        near.ResolveSpeedEffects(780);
        Assert.Equal(0, near.ResolvedSpeedBonusPercent);
    }

    [Fact] // boundary — 相位边界：tick 0/599/601/1199 不触发，恰 600/1200 触发
    public void War_cry_phase_boundaries()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit crier = NewCrier(112f, 112f);
        var ally = NewUnit(112f, 112f + 32f, 1.0f);
        var allies = new List<Unit> { crier, ally };

        Assert.Empty(movement.PulseWarCry(crier, allies, 0));    // 相位 0 排除
        Assert.Empty(movement.PulseWarCry(crier, allies, 599));
        Assert.Single(movement.PulseWarCry(crier, allies, 600)); // 首拍
        Assert.Empty(movement.PulseWarCry(crier, allies, 601));
        Assert.Single(movement.PulseWarCry(crier, allies, 1200)); // 第二拍（20 s 冷却）
    }

    [Fact] // legal — 死亡吼者与未配置战吼的单位：永不脉冲；死者也不受鼓
    public void Dead_or_uncried_units_never_pulse_and_dead_allies_are_skipped()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit dead = NewCrier(112f, 112f);
        dead.ApplyDamage(9999, tick: 1);
        Unit uncried = NewUnit(112f, 112f, 0.53f); // 无 war_cry 配置
        var deadAlly = NewUnit(112f + 32f, 112f, 1.0f);
        deadAlly.ApplyDamage(9999, tick: 1);
        var allies = new List<Unit> { dead, uncried, deadAlly };

        for (long tick = 1; tick <= 1200; tick++)
        {
            Assert.Empty(movement.PulseWarCry(dead, allies, tick));
            Assert.Empty(movement.PulseWarCry(uncried, allies, tick));
        }

        Assert.Equal(long.MinValue, deadAlly.CryUntilTick); // 死者不受鼓
    }

    [Fact] // legal — 受鼓折算入移动速度：+15% 窗口内有效步速放大、过期回落
    public void War_cry_buff_inflates_the_ally_stride()
    {
        FlowField field = StraightField();
        var movement = new UnitMovement(() => field, CellSize);
        Unit crier = NewCrier(96f, 96f);
        var ally = NewUnit(96f, 96f + 32f, speed: 1.0f);
        var allies = new List<Unit> { crier, ally };

        _ = movement.PulseWarCry(crier, allies, 600);
        ally.ResolveSpeedEffects(601);
        Assert.Equal(1.0f * 1.15f, ally.EffectiveSpeedPxPerTick, 5); // +15% 折算（步幅=速度×系数）

        ally.ResolveSpeedEffects(780);
        Assert.Equal(1.0f, ally.EffectiveSpeedPxPerTick, 5); // 600+180 窗口外回落原速
    }

    [Theory] // illegal — SetWarCry 非法四元组确实被拒绝（半径>0 而三元不全正/负值）
    [InlineData(-1, 15, 180, 600)]
    [InlineData(96, 0, 180, 600)]
    [InlineData(96, 15, 0, 600)]
    [InlineData(96, 15, 180, 0)]
    [InlineData(96, -15, 180, 600)]
    public void SetWarCry_rejects_incomplete_or_negative_quads(float radius, int bonus, int duration, int cooldown)
    {
        Unit unit = NewUnit(112f, 112f, 0.53f);
        Assert.Throws<ArgumentException>(() => unit.SetWarCry(radius, bonus, duration, cooldown));
    }
}
