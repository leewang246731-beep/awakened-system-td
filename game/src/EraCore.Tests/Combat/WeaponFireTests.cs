using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-013] weapon loop of the E1 towers (T-038): tick-driven reload on the
// 30 Hz domain, shot accounting (1 ammo item = 10 shots, v3.2 §5), burst
// wind-up from cold (F-028, 落石台 45 ticks), and hit settlement delegating
// to the Damage engine (F-029).
public class WeaponFireTests
{
    /// <summary>Mutable stand-in target for hit settlement (K-6 IUnit read view).</summary>
    private sealed class TestUnit(EntityId id, int armor) : IUnit
    {
        public EntityId Id { get; } = id;

        public string ContentId => "test:unit";

        public float X => 0f;

        public float Y => 0f;

        public int Hp => 100;

        public int MaxHp => 100;

        public int Armor { get; } = armor;

        public int Shield => 0;

        public float SpeedPxPerTick => 0f;
    }

    private static WeaponFire ArmedArrowTower(int shots = 10) =>
        new(new EntityId(1), CombatContent.Tower("era1:tower-wood-arrow"), shots);

    private static WeaponFire ArmedRockfall(int shots = 3) =>
        new(new EntityId(2), CombatContent.Tower("era1:tower-rockfall"), shots);

    // ------------------------------------------------------------ reload sequence

    [Fact] // legal — 30-tick reload cadence: one event every 30 ticks, none between
    public void Wood_arrow_fires_exactly_every_thirty_ticks()
    {
        // [F-013] reload_ticks=30 from the registry (towers.json)
        var weapon = ArmedArrowTower(shots: 10);
        var target = new EntityId(90);
        var fireTicks = new List<long>();

        for (long tick = 0; tick <= 90; tick++)
        {
            var shot = weapon.TryFire(target, tick);
            if (shot is not null)
            {
                fireTicks.Add(shot.Tick);
            }

            weapon.Tick(); // advance to the next simulation tick
        }

        // Fires at 0, 30, 60, 90 — never at 1..29 (cadence = dps anchor:
        // 10 damage × 30 / 30 ticks = 10 dps, the registry value)
        Assert.Equal(new long[] { 0, 30, 60, 90 }, fireTicks);
    }

    [Fact] // legal — each event is structured and carries the registry numbers
    public void Fire_events_carry_structured_registry_fields()
    {
        // [F-013] structured event, no UI coupling
        var weapon = ArmedArrowTower(shots: 10);
        var target = new EntityId(77);
        var shot = weapon.TryFire(target, tick: 0);

        Assert.NotNull(shot);
        Assert.Equal(0L, shot!.Tick);
        Assert.Equal(new EntityId(1), shot.TowerId);
        Assert.Equal("era1:tower-wood-arrow", shot.TowerContentId);
        Assert.Equal(TowerClass.Pierce, shot.TowerClass);
        Assert.Equal(target, shot.TargetId);
        Assert.Equal(10.0, shot.DamagePerShot, 6);
        Assert.Equal(9, shot.AmmoShotsRemaining); // 10 − 1 shot
        Assert.Equal(30, shot.CooldownTicksSet);
        Assert.Equal(1, weapon.FiredCount);
    }

    [Fact] // legal — TryFire refuses while cooling down and touches nothing
    public void TryFire_refuses_during_cooldown_without_side_effects()
    {
        // [F-013] reload gate: no shot before 30 ticks ran
        var weapon = ArmedArrowTower(shots: 10);
        var target = new EntityId(5);

        Assert.NotNull(weapon.TryFire(target, 0));   // cold tower fires at once
        Assert.Equal(30, weapon.CooldownTicksRemaining);

        for (int i = 0; i < 29; i++)
        {
            Assert.Null(weapon.TryFire(target, i + 1)); // still cooling: refused
            weapon.Tick();
        }

        Assert.Equal(1, weapon.CooldownTicksRemaining); // 29 ticks elapsed
        weapon.Tick();
        Assert.Equal(0, weapon.CooldownTicksRemaining); // exactly 30 → ready again
        Assert.NotNull(weapon.TryFire(target, 30));
    }

    // ------------------------------------------------------------ ammo accounting

    [Fact] // legal — a full ammo item (10 shots) is spent shot by shot
    public void Ammo_exhausts_after_ten_shots_and_blocks_firing()
    {
        // [F-022] 1 ammo item = 10 shots (v3.2 §5); wood arrow consumes 1 per firing
        var weapon = ArmedArrowTower(shots: 10);
        var target = new EntityId(6);
        var shots = new List<WeaponFireEvent>();

        for (long tick = 0; tick < 600; tick++)
        {
            var shot = weapon.TryFire(target, tick);
            if (shot is not null)
            {
                shots.Add(shot);
            }

            weapon.Tick();
        }

        // Exactly 10 firings at 0,30,...,270; the 11th cycle (tick 300) stays silent
        Assert.Equal(10, shots.Count);
        Assert.Equal(0, weapon.AmmoShotsRemaining);
        Assert.Equal(10, weapon.FiredCount);
        Assert.Equal(new long[] { 0, 30, 60, 90, 120, 150, 180, 210, 240, 270 },
            shots.Select(s => s.Tick).ToArray());
        Assert.All(shots, s => Assert.True(s.AmmoShotsRemaining >= 0));

        // Reserve empty: even cooled down and targeted, no shot leaves the tower.
        Assert.Equal(0, weapon.CooldownTicksRemaining);
        Assert.False(weapon.ReadyToFire);
        Assert.Null(weapon.TryFire(target, 600));
        Assert.Equal(10, weapon.FiredCount); // nothing was consumed by the refusal
    }

    [Fact] // legal — reload tops the reserve back up and firing resumes
    public void Reload_shots_restores_the_cycle()
    {
        // [F-022] logistics hand-off: caller converts items to shots (×10)
        var weapon = ArmedArrowTower(shots: 1);
        var target = new EntityId(7);

        Assert.NotNull(weapon.TryFire(target, 0));
        for (int i = 0; i < 30; i++)
        {
            weapon.Tick();
        }

        Assert.Equal(0, weapon.CooldownTicksRemaining); // cooled down…
        Assert.Null(weapon.TryFire(target, 30));        // …but out of shots
        weapon.ReloadShots(10);
        Assert.True(weapon.ReadyToFire);
        var shot = weapon.TryFire(target, 30);
        Assert.NotNull(shot);
        Assert.Equal(9, shot!.AmmoShotsRemaining); // 0 left + 10 reloaded − 1 fired now
    }

    [Theory] // illegal — constructing a weapon loop on a tower without one is rejected
    [InlineData("era1:tower-spike-trap")]
    [InlineData("era1:tower-campfire")]
    public void Control_and_aura_towers_have_no_weapon_cycle(string towerId)
    {
        // [F-013] reload_ticks null (control/aura towers, SP-1b L38/L39)
        Assert.Throws<ArgumentException>(
            () => new WeaponFire(new EntityId(3), CombatContent.Tower(towerId), 10));
    }

    [Theory] // illegal — negative ammo inputs are rejected
    [InlineData(-1)]
    [InlineData(-10)]
    public void Constructor_rejects_negative_ammo(int shots)
    {
        // [F-013] guard: ammo ≥ 0
        Assert.Throws<ArgumentException>(() => ArmedArrowTower(shots));
    }

    [Fact] // illegal — negative reload top-ups are rejected
    public void Reload_rejects_negative_shots()
    {
        // [F-013] guard: reload shots ≥ 0
        var weapon = ArmedArrowTower();
        Assert.Throws<ArgumentException>(() => weapon.ReloadShots(-1));
    }

    // ------------------------------------------------------------ burst wind-up (F-028)

    [Fact] // legal — a burst tower winds up from cold, then reloads plainly
    public void Rockfall_winds_up_once_then_reloads_every_ninety_ticks()
    {
        // [F-028] burst price: 45 wind-up ticks BEFORE the first shot
        // (SP-1b L37 anchor), then plain 90-tick reload cycles. Sustained dps
        // still matches the registry: 60 damage × 30 / 90 = 20.
        var weapon = ArmedRockfall(shots: 3);
        var target = new EntityId(8);
        Assert.Equal(45, weapon.CooldownTicksRemaining); // cold-start wind-up armed

        for (long tick = 0; tick <= 135; tick++)
        {
            var shot = weapon.TryFire(target, tick);
            if (tick < 45)
            {
                Assert.Null(shot); // wind-up in progress — no early shot
            }
            else if (tick == 45 || tick == 135)
            {
                Assert.NotNull(shot); // first shot exactly at 45, next at 135
                Assert.Equal(TowerClass.Physical, shot!.TowerClass);
                Assert.Equal(60.0, shot.DamagePerShot, 6);
            }
            else
            {
                Assert.Null(shot);
            }

            weapon.Tick();
        }

        Assert.Equal(2, weapon.FiredCount);
    }

    // ------------------------------------------------------------ hit settlement

    public static TheoryData<string, int, double, double> HitRows => new()
    {
        // legal — decay 1 direct hits: armor subtracts plainly
        { "era1:tower-wood-arrow", 4, 1.0, 6 },    // 10 − 4 (SP-3a armor scale)
        { "era1:tower-rockfall", 8, 1.0, 52 },     // 60 − 8 (e1_ram armor, SP-3a L105)
        // boundary — heavy armor meets the 10% floor
        { "era1:tower-wood-arrow", 20, 1.0, 1 },   // max(10−20, 1) = 1
        // legal — splash-style decay feeds the same settlement
        { "era1:tower-wood-arrow", 0, 0.7, 7 },    // 10×0.7 at the splash half radius
        { "era1:tower-wood-arrow", 0, 0.4, 4 },    // splash edge floor decay
    };

    [Theory] // legal + boundary — ResolveHit delegates to the damage engine
    [MemberData(nameof(HitRows))]
    public void ResolveHit_settles_through_the_damage_engine(
        string towerId, int armor, double decay, double expected)
    {
        // [F-029] weapon → Damage delegation: decay first, then armor, 10% floor
        var weapon = new WeaponFire(new EntityId(9), CombatContent.Tower(towerId), 10);
        var unit = new TestUnit(new EntityId(10), armor);
        var hit = weapon.ResolveHit(unit, decay);
        Assert.Equal(unit.Id, hit.TargetId);
        Assert.Equal(decay, hit.Decay, 6);
        Assert.Equal(expected, hit.DamageDealt, 6);
    }

    [Fact] // legal — splash decay composes with the weapon settlement
    public void ResolveHit_composes_with_splash_decay()
    {
        // [F-029] splash chain: decay = SplashDecay(dist, r) then ResolveHit
        var weapon = ArmedArrowTower();
        var hit = weapon.ResolveHit(new TestUnit(new EntityId(11), 0), Damage.SplashDecay(1.5, 3.0));
        Assert.Equal(7.0, hit.DamageDealt, 6); // 10 × 0.7 − 0
    }

    [Fact] // legal — armor pierce routes through the pierce branch
    public void ResolveHit_honors_armor_pierce_ratio()
    {
        // [F-029] pen 0.5 halves armor: 10 − 2 = 8 (armor 4)
        var weapon = ArmedArrowTower();
        var hit = weapon.ResolveHit(new TestUnit(new EntityId(12), 4), 1.0, armorPenRatio: 0.5);
        Assert.Equal(8.0, hit.DamageDealt, 6);
    }

    [Fact] // illegal — a null target is rejected
    public void ResolveHit_rejects_null_target()
    {
        // [F-029] guard: target required
        var weapon = ArmedArrowTower();
        Assert.Throws<ArgumentNullException>(() => weapon.ResolveHit(null!, 1.0));
    }
}
