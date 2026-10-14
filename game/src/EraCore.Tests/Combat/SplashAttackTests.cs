using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-014] [C-09] T-065 火油塔 AOE 多目标命中结算：半径内敌逐个
// Damage.SplashDecay（v3.2 §6 溅射边缘保底 40%）+ 燃烧点燃。数值自
// era2 内容（火油 d=64/半径 1.5），期望值逐发手算写在断言处注释。
public class SplashAttackTests
{
    private static Unit Enemy(long id, int hp, int armor, double distance) =>
        new(new EntityId(id), $"e2_enemy_{id}", (float)(distance * 32), 0f, hp, armor, speedPxPerTick: 0f);

    private static SplashCandidate At(Unit unit, double distanceCells) => new(unit, distanceCells);

    [Fact] // legal — 三敌等距 1.0 格：同衰减 0.6、同伤害 38.4（手算：1−1/1.5=1/3 衰落×0.6+0.4）
    public void Three_equidistant_enemies_settle_identically()
    {
        // [F-029] 手算：radius 1.5、dist 1.0 → falloff=1−0.6667=0.3333；
        // decay=0.3333×0.6+0.4=0.6；damage=64×0.6=38.4（甲 0）
        var enemies = new[]
        {
            At(Enemy(1, 200, 0, 1.0), 1.0),
            At(Enemy(2, 200, 0, 1.0), 1.0),
            At(Enemy(3, 200, 0, 1.0), 1.0),
        };

        var hits = SplashAttack.Settle(Era2Content.FireOil, enemies);

        Assert.Equal(3, hits.Count);
        Assert.All(hits, hit =>
        {
            Assert.Equal(0.6, hit.Decay, 6);       // SplashDecay(1.0, 1.5)
            Assert.Equal(38.4, hit.DamageDealt, 6); // 64×0.6，甲 0 无折减
        });
        Assert.Equal(new EntityId(1), hits[0].TargetId); // 候选序保留（确定性）
        Assert.Equal(new EntityId(3), hits[2].TargetId);
    }

    [Fact] // legal — 不等距四敌：0/0.75/1.5 命中衰减 1.0/0.7/0.4，2.0 格外不命中
    public void Non_equidistant_enemies_settle_per_distance_and_edge_floor()
    {
        // [F-029] 手算：dist 0 → decay 1.0（爆心全额 64）；dist 0.75 →
        // falloff 0.5 → 0.5×0.6+0.4=0.7 → 44.8；dist 1.5（=半径边缘）→
        // 0.4 保底 → 25.6（v3.2 §6 溅射边缘保底 40%）；dist 2.0 > 半径 → 不入结算
        var candidates = new[]
        {
            At(Enemy(1, 400, 0, 0.0), 0.0),
            At(Enemy(2, 400, 0, 0.75), 0.75),
            At(Enemy(3, 400, 0, 1.5), 1.5),
            At(Enemy(4, 400, 0, 2.0), 2.0),
        };

        var hits = SplashAttack.Settle(Era2Content.FireOil, candidates);

        Assert.Equal(3, hits.Count); // 边缘敌命中、界外敌排除
        Assert.Equal(1.0, hits[0].Decay, 9);
        Assert.Equal(64.0, hits[0].DamageDealt, 6);
        Assert.Equal(0.7, hits[1].Decay, 6);
        Assert.Equal(44.8, hits[1].DamageDealt, 6);
        Assert.Equal(0.4, hits[2].Decay, 9);
        Assert.Equal(25.6, hits[2].DamageDealt, 6);
        Assert.DoesNotContain(hits, hit => hit.TargetId.Equals(new EntityId(4)));
    }

    [Fact] // legal — 重甲（攻城锤 甲 12）：先衰减后减甲（v3.2 §6 伤害式）
    public void Armored_enemies_settle_decay_first_then_armor()
    {
        // [F-029] 手算：dist 1.0 → decay 0.6 → 衰后 38.4；甲 12 → 38.4−12=26.4
        // （>10% 保底 3.84，全额减甲生效）
        var ram = At(Enemy(7, 1500, 12, 1.0), 1.0);

        var hits = SplashAttack.Settle(Era2Content.FireOil, new[] { ram });

        var hit = Assert.Single(hits);
        Assert.Equal(26.4, hit.DamageDealt, 6);
    }

    [Fact] // legal — 命中敌全部点燃（BurnApplied），界外敌不点燃
    public void Hits_ignite_the_burn_tracker_for_struck_enemies_only()
    {
        var tracker = new BurnTracker();
        var candidates = new[]
        {
            At(Enemy(1, 200, 0, 0.5), 0.5),
            At(Enemy(2, 200, 0, 2.5), 2.5), // 界外
        };

        var hits = SplashAttack.Settle(Era2Content.FireOil, candidates, tracker);

        var hit = Assert.Single(hits);
        Assert.True(hit.BurnApplied);
        Assert.True(tracker.IsBurning(new EntityId(1)));
        Assert.False(tracker.IsBurning(new EntityId(2)));
        var burn = Assert.Single(tracker.Active);
        Assert.Equal(12.0 / 30.0, burn.DamagePerTick, 9); // 内容 burn_dps 12 → 0.4/tick
        Assert.Equal(90, burn.TicksRemaining);           // 内容 burn_ticks（3 s）
    }

    [Theory] // illegal — 非溅射行无半径特性 / 负距离 → ArgumentException
    [InlineData("era2:tower-arrow")]
    [InlineData("era2:tower-crossbow")]
    public void Non_splash_rows_without_radius_are_rejected(string id)
    {
        // [F-029] RL7：半径必须来自内容 traits.splash_radius_cells——无特性即拒绝
        var enemy = At(Enemy(1, 100, 0, 0.0), 0.0);
        Assert.Throws<ArgumentException>(() => SplashAttack.Settle(Era2Content.Tower(id), new[] { enemy }));
    }

    [Fact] // illegal — 负距离候选拒绝
    public void Negative_distances_are_rejected()
    {
        var enemy = At(Enemy(1, 100, 0, -0.1), -0.1);
        Assert.Throws<ArgumentException>(() => SplashAttack.Settle(Era2Content.FireOil, new[] { enemy }));
    }
}
