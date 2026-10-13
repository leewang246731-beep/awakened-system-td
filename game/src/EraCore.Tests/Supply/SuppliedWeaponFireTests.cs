using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Supply;
using Xunit;

namespace EraCore.Tests.Supply;

/// <summary>
/// Logic tests of the WeaponFire 联动 (T-048, F-046): the paced fire loop
/// of a real <see cref="WeaponFire"/> under a real
/// <see cref="TowerSupplyMonitor"/> curve — 断链 DPS 按效率下降不停摆.
/// Fire cadence is hand-computed per efficiency (inter-fire gap =
/// reload ÷ efficiency: 3/6/15 ticks at eff 1.0/0.5/0.2 ⇒ DPS
/// 100/50/20) and asserted as exact gap sequences; the decline from full
/// speed is monotone (从满速平滑降至此，不是立即停); efficiency 0 freezes
/// the cooldown without draining magazine shots and recovery resumes
/// firing. Absolute fire ticks are phase-dependent (the curve ramps while
/// the window fills), so the hand-computed contract is the GAP sequence —
/// the phase-independent form of 塔射速乘 efficiency.
/// </summary>
// [C-09][F-046] 塔射速乘 efficiency：发火间隔手算对照 + 降速单调 + 冻结/恢复
public class SuppliedWeaponFireTests
{
    private static readonly EntityId Tower = new(1);

    private static readonly EntityId Target = new(77);

    /// <summary>
    /// Synthetic fast-cycle spec (reload 3 ticks, 1 shot/firing, damage 10
    /// ⇒ full DPS 100): pacing math needs a dense fire cycle for exact
    /// hand-computed gap sequences — the registry wood-arrow (reload 30) is
    /// the integration anchor; this record exercises the same loader shape.
    /// </summary>
    private static TowerSpec FastSpec { get; } = new(
        "test:fast-arrow", TowerClass.Pierce, 1, 4, 10,
        Dps: 100.0, DamagePerShot: 10.0, ReloadTicks: 3, RangeCells: 5.0, Hp: 100,
        AmmoItem: "era1:ammo-arrow", AmmoPerShot: 1, TierGrowth: null, Traits: null);

    /// <summary>Pacer bundle over a scripted delivery stream (demand 10 发/s: reload 3).</summary>
    private sealed class Harness
    {
        public readonly DeliveryCounter Counter = new();
        public readonly TowerSupplyMonitor Monitor;
        public readonly SuppliedWeaponFire Pacer;

        public Harness(int windowTicks = 90, int warmupTicks = 0, int initialShots = 100_000)
        {
            Monitor = new TowerSupplyMonitor(windowTicks, warmupTicks);
            Monitor.RegisterTower(new TowerSupplyRegistration(
                Tower, new CellCoord(4, 2), ReloadTicks: 3, AmmoPerShot: 1, DamagePerShot: 10.0,
                Counter.AsFunc()));
            var weapon = new WeaponFire(Tower, FastSpec, initialShots);
            Pacer = new SuppliedWeaponFire(weapon, Monitor, Tower);
        }

        /// <summary>One orchestrator tick: deliver → monitor → pacer → try-fire.</summary>
        public WeaponFireEvent? Step(long tick, int shotsThisTick = 0)
        {
            if (shotsThisTick > 0)
            {
                Counter.Add(shotsThisTick);
            }

            Monitor.Tick();
            Pacer.Tick();
            return Pacer.TryFire(Target, tick);
        }

        /// <summary>Drives ticks with one delivered shot every period ticks; returns fire ticks.</summary>
        public List<long> Drive(long fromTick, int ticks, int period)
        {
            var fireTicks = new List<long>();
            for (int i = 0; i < ticks; i++)
            {
                long tick = fromTick + i + 1;
                WeaponFireEvent? shot = Step(tick, (i + 1) % period == 0 ? 1 : 0);
                if (shot is not null)
                {
                    fireTicks.Add(shot.Tick);
                }
            }

            return fireTicks;
        }
    }

    private sealed class DeliveryCounter
    {
        private long _shots;

        public void Add(int shots) => _shots += shots;

        public Func<long> AsFunc() => () => _shots;
    }

    private static List<long> Gaps(List<long> fireTicks)
    {
        var gaps = new List<long>();
        for (int i = 1; i < fireTicks.Count; i++)
        {
            gaps.Add(fireTicks[i] - fireTicks[i - 1]);
        }

        return gaps;
    }

    [Fact] // legal — efficiency 1: the pacer is transparent (bare-weapon cadence)
    public void Full_efficiency_fires_on_the_bare_weapon_cadence()
    {
        // [F-046] eff 1 ⇒ inter-fire gap = reload 3 exactly (the unpaced
        // WeaponFire cadence), 30 gaps in a row — 塔射速乘 efficiency at 1
        var h = new Harness();
        h.Drive(0, 180, period: 3); // prime: window full ⇒ eff exactly 1

        List<long> fires = h.Drive(180, 120, period: 3);

        Assert.True(fires.Count >= 30, $"expected a fully firing tower, got {fires.Count}");
        Assert.All(Gaps(fires), gap => Assert.Equal(3L, gap));
        Assert.Equal(1.0, h.Pacer.Efficiency, 9);
        Assert.Equal(100.0, h.Pacer.FullDps, 9); // 10 damage × 10 发/s
        Assert.Equal(100.0, h.Pacer.EffectiveDps, 9);
    }

    [Fact] // legal — 断链 0.2: inter-fire gap = reload ÷ 0.2 = 15 (hand-computed)
    public void Zero_point_two_efficiency_fires_every_fifteen_ticks()
    {
        // [F-046] DPS 按效率下降: eff 0.2 (one shot per 15 ticks vs demand
        // 1/3 per tick ⇒ window 6/30) ⇒ gap 3/0.2 = 15 ⇒ effective 20 DPS
        var h = new Harness();
        h.Drive(0, 180, period: 15); // prime: window full ⇒ eff exactly 0.2

        List<long> fires = h.Drive(180, 300, period: 15);

        Assert.True(fires.Count >= 10, $"expected a slowed but firing tower, got {fires.Count}");
        Assert.All(Gaps(fires), gap => Assert.Equal(15L, gap));
        Assert.Equal(0.2, h.Pacer.Efficiency, 9);
        Assert.Equal(20.0, h.Pacer.EffectiveDps, 3); // 100 × 0.2
    }

    [Fact] // legal — half supply: the gap doubles (reload ÷ 0.5 = 6)
    public void Half_efficiency_fires_every_six_ticks()
    {
        // [F-046] half-rate cadence hand calc
        var h = new Harness();
        h.Drive(0, 180, period: 6); // eff 0.5: window 15/30

        List<long> fires = h.Drive(180, 180, period: 6);

        Assert.True(fires.Count >= 15, $"expected a half-rate tower, got {fires.Count}");
        Assert.All(Gaps(fires), gap => Assert.Equal(6L, gap));
        Assert.Equal(0.5, h.Pacer.Efficiency, 9);
        Assert.Equal(50.0, h.Pacer.EffectiveDps, 3);
    }

    [Fact] // legal — 从满速平滑降至此: early gaps stay short, late gaps settle at 15
    public void Declining_curve_slows_the_cadence_to_the_cut_rate()
    {
        // [F-046] prime at eff 1 (gap 3), then thin the stream to the 0.2
        // rate: the window slides, the curve falls, the cadence stretches —
        // a smooth slowdown, not an immediate stop (断链减速不停摆). The
        // sawtooth of integer bursts makes single gaps ±1, so the contract
        // is early-vs-late: the first gaps stay at the full cadence, the
        // last gaps sit at the 0.2 cadence, and the late minimum dominates
        // the early maximum.
        var h = new Harness();
        h.Drive(0, 180, period: 3);

        List<long> fires = h.Drive(180, 240, period: 15);

        Assert.True(fires.Count >= 8, $"expected a slowed but firing tower, got {fires.Count}");
        List<long> gaps = Gaps(fires);
        List<long> early = gaps.Take(5).ToList();
        List<long> late = gaps.Skip(Math.Max(0, gaps.Count - 5)).ToList();

        Assert.All(early, gap => Assert.True(gap <= 6, $"early gap {gap} must sit at the full cadence"));
        Assert.All(late, gap => Assert.Equal(15L, gap)); // settles at reload ÷ 0.2
        Assert.True(early.Max() <= late.Min(), $"late cadence must dominate: early max {early.Max()} vs late min {late.Min()}");
        Assert.True(gaps.Max() <= 16, $"no gap may exceed the settled cadence by more than burst jitter, got {gaps.Max()}");
    }

    [Fact] // legal — efficiency 0 freezes the cooldown, magazine untouched, no stall-crash
    public void Zero_efficiency_freezes_the_cycle_without_consuming_shots()
    {
        // [F-046] 断链效率降 0: prime full (eff 1), cut completely — the
        // window keeps draining for its length (credits keep flowing at a
        // falling rate), then no credits accrue at all: firing stops dead
        // for the rest of the cut, the cooldown freezes armed at a stable
        // value, the magazine stops draining, nothing throws (系统不崩)
        var h = new Harness(initialShots: 500);
        h.Drive(0, 180, period: 3);
        int shotsBefore = h.Pacer.Weapon.AmmoShotsRemaining;

        List<long> fires = h.Drive(180, 300, period: 1_000); // hard cut

        Assert.Equal(0.0, h.Pacer.Efficiency, 9);
        Assert.Equal(0.0, h.Pacer.EffectiveDps, 9);
        Assert.True(fires.Count < 60, $"the window drain may fire a while, not full cadence — got {fires.Count}");
        Assert.Equal(0, fires.Count(t => t > 330 && t <= 480)); // deep cut: dead silence
        Assert.Equal(shotsBefore - fires.Count, h.Pacer.Weapon.AmmoShotsRemaining); // no silent drain

        int frozenCooldown = h.Pacer.Weapon.CooldownTicksRemaining;
        h.Drive(480, 50, period: 1_000);
        Assert.Equal(frozenCooldown, h.Pacer.Weapon.CooldownTicksRemaining); // frozen, not reset
        Assert.True(frozenCooldown is > 0 and <= 3, $"cooldown must be armed mid-cycle, got {frozenCooldown}");
    }

    [Fact] // legal — 恢复: the stream resumes and the cadence climbs back to full
    public void Recovery_restores_the_full_cadence()
    {
        // [F-046] recovery cadence
        var h = new Harness();
        h.Drive(0, 180, period: 3);
        h.Drive(180, 300, period: 1_000); // cut
        Assert.Equal(0.0, h.Pacer.Efficiency, 9);

        h.Drive(480, 270, period: 3); // 恢复路径后效率回升

        Assert.Equal(1.0, h.Pacer.Efficiency, 9);
        List<long> tail = h.Drive(750, 120, period: 3);
        Assert.True(tail.Count >= 30, $"expected a recovered tower, got {tail.Count}");
        Assert.All(Gaps(tail), gap => Assert.Equal(3L, gap));
    }

    [Fact] // legal — the wrapped loop keeps its own contract visible
    public void Wrapped_weapon_surface_stays_coherent()
    {
        // [F-046] wrapped loop surface
        var h = new Harness();
        Assert.Equal(Tower, h.Pacer.TowerId);
        Assert.Equal("test:fast-arrow", h.Pacer.Weapon.TowerContentId);
        Assert.Equal(3, h.Pacer.Weapon.ReloadTicks);
        Assert.Equal(100_000, h.Pacer.Weapon.AmmoShotsRemaining);
    }

    [Fact] // illegal — unregistered tower cannot be paced
    public void Unregistered_tower_is_rejected()
    {
        // [F-046] registration gate
        var monitor = new TowerSupplyMonitor();
        Assert.Throws<ArgumentException>(
            () => new SuppliedWeaponFire(new WeaponFire(Tower, FastSpec, 10), monitor, Tower));
    }
}
