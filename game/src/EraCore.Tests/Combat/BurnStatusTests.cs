using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-014] [C-09] T-065 燃烧 DOT 序列（StatusEffect 薄版：最小 DOT 字段+
// 后续挂 C-10 状态效果框架——数值 [NEW] 自内容）。银行化整发（banking）
// 保证 12 DPS×3 s 恒等于 36 点、绝不因每 tick 0.4 取整归零。
public class BurnStatusTests
{
    private const double FireOilBurnDps = 12.0; // era2:ammo-fire-oil 矢燃烧 [NEW]（traits.burn_dps）
    private const int FireOilBurnTicks = 90;    // 3 s × 30 Hz（traits.burn_ticks）

    [Fact] // legal — 全序列总伤恰 36（12 DPS×3 s），到期清空
    public void Full_burn_sequence_totals_exactly_thirty_six_points()
    {
        var tracker = new BurnTracker();
        tracker.Ignite(new EntityId(1), FireOilBurnDps, FireOilBurnTicks);

        double total = 0;
        int emittingTicks = 0;
        for (int i = 0; i < FireOilBurnTicks; i++)
        {
            foreach (BurnTick due in tracker.Tick())
            {
                total += due.Damage;
                emittingTicks++;
            }
        }

        Assert.Equal(36.0, total, 6); // 12×3——银行化整+到期尾差回合保证恒等
        Assert.Equal(36, emittingTicks); // 每次发射恰 1 点（0.4/tick 银行至 1）
        Assert.Equal(0, tracker.ActiveCount);
        Assert.Empty(tracker.Active);
        Assert.Empty(tracker.Tick()); // 到期后不再发射
    }

    [Fact] // legal — 序列头部手算：tick 3/5/8/10 各发 1 点（0.4 累加过 1 即发）
    public void Banking_emits_on_the_hand_computed_schedule()
    {
        // [F-014] 手算：累加 0.4/tick——t1:0.4 t2:0.8（均 <1 不发）t3:1.2→发 1 余 0.2；
        // t4:0.6 t5:1.0→发 1 余 0；t6:0.4 t7:0.8 t8:1.2→发 1 余 0.2；t9:0.6 t10:1.0→发 1
        var tracker = new BurnTracker();
        tracker.Ignite(new EntityId(1), FireOilBurnDps, FireOilBurnTicks);

        var emitTicks = new List<int>();
        for (int tick = 1; tick <= 10; tick++)
        {
            foreach (BurnTick due in tracker.Tick())
            {
                emitTicks.Add(tick);
                Assert.Equal(1, due.Damage);
            }
        }

        Assert.Equal(new[] { 3, 5, 8, 10 }, emitTicks);
        Assert.Equal(80, tracker.Active[0].TicksRemaining); // 90−10
    }

    [Fact] // legal — 真 Unit 上的可致死序列（Units 拥有 HP 突变，单向缝）
    public void Applied_sequence_burns_a_real_unit_by_the_declared_total()
    {
        var unit = new Unit(new EntityId(21), "e2_skirmisher", 0f, 0f, maxHp: 60, armor: 0, speedPxPerTick: 0f);
        var tracker = new BurnTracker();
        tracker.Ignite(unit.Id, FireOilBurnDps, FireOilBurnTicks);

        for (int i = 0; i < FireOilBurnTicks; i++)
        {
            foreach (BurnTick due in tracker.Tick())
            {
                _ = unit.ApplyDamage(due.Damage, tick: i);
            }
        }

        Assert.Equal(60 - 36, unit.Hp); // 60−36=24：燃烧恰烧掉声明总量
        Assert.True(unit.Alive);        // 未致死（F.5 散兵 60 HP 存活线以下口径）
    }

    [Fact] // boundary — 再点燃刷新时长且取更强速率（[NEW] 约定：叠加不叠层）
    public void Re_ignition_refreshes_duration_and_keeps_the_stronger_rate()
    {
        var tracker = new BurnTracker();
        var unit = new EntityId(5);
        tracker.Ignite(unit, FireOilBurnDps, FireOilBurnTicks);
        for (int i = 0; i < 50; i++)
        {
            _ = tracker.Tick();
        }

        Assert.Equal(40, tracker.Active.Single().TicksRemaining);

        tracker.Ignite(unit, 30.0, FireOilBurnTicks); // 更强速率再点燃
        var burn = tracker.Active.Single();           // 仍是单条（不叠层）
        Assert.Equal(FireOilBurnTicks, burn.TicksRemaining); // 时长刷新回 90
        Assert.Equal(30.0 / 30.0, burn.DamagePerTick, 9);    // 速率取强（1.0/tick）
    }

    [Fact] // legal — 多单位按点燃序结算（确定性次序）
    public void Multiple_units_settle_in_ignition_order()
    {
        var tracker = new BurnTracker();
        tracker.Ignite(new EntityId(2), 30.0, 30); // 1/tick
        tracker.Ignite(new EntityId(1), 30.0, 30);

        IReadOnlyList<BurnTick> due = tracker.Tick();

        Assert.Equal(2, due.Count);
        Assert.Equal(new EntityId(2), due[0].UnitId); // 先点燃者先结算
        Assert.Equal(new EntityId(1), due[1].UnitId);
        Assert.All(due, tick => Assert.Equal(1, tick.Damage));
    }

    [Theory] // illegal — 非正速率/时长拒绝（守卫）
    [InlineData(0.0, 90)]
    [InlineData(-1.0, 90)]
    [InlineData(12.0, 0)]
    [InlineData(12.0, -5)]
    public void Non_positive_rates_and_durations_are_rejected(double dps, int ticks)
    {
        var tracker = new BurnTracker();
        Assert.Throws<ArgumentException>(() => tracker.Ignite(new EntityId(1), dps, ticks));
    }

    [Fact] // boundary — 速率换算基线：30 Hz 域常量（K-1 IClock）
    public void Tick_rate_domain_constant_is_thirty_hertz()
    {
        // [F-014] K-1 IClock 30 Hz：内容以 points/s 声明，tick 域换算基线固定
        Assert.Equal(30, BurnTracker.TicksPerSecond);
        var tracker = new BurnTracker();
        tracker.Ignite(new EntityId(1), 30.0, 1);
        var due = tracker.Tick(); // 30 DPS→1/tick：单 tick 到期即发 1
        var tick = Assert.Single(due);
        Assert.Equal(1, tick.Damage);
    }
}
