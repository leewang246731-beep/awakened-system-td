using EraCore.Contracts.Foundation;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-155][K-6] 弹影层实体（T-067）：直线飞行孪生——冻结航向推进、寿命
// 到期、K-6 IProjectile 契约面、参数纪律。战斗结算权威在 hitscan 主路径，
// 弹影只承载飞行载荷（零战斗权威）。
public class SimProjectileTests
{
    private static SimProjectile Twin(float ox, float oy, float tx, float ty, int lifetime = 32, float speed = 9f) =>
        new(new EntityId(1), new EntityId(2), ox, oy, tx, ty, damage: 15, speedPxPerTick: speed, lifetimeTicks: lifetime);

    [Fact] // legal — 直线推进：每 tick 沿冻结航向前进 speed 像素
    public void Advance_moves_along_the_frozen_heading()
    {
        // 正东目标：航向 (1,0)，每 tick +9px
        SimProjectile twin = Twin(0f, 0f, 100f, 0f);
        Assert.Equal(0f, twin.X);
        Assert.Equal(0f, twin.AgeTicks);
        twin.Advance();
        Assert.Equal(9f, twin.X, 5);
        Assert.Equal(0f, twin.Y, 5);
        Assert.Equal(1L, twin.AgeTicks);
        twin.Advance();
        Assert.Equal(18f, twin.X, 5);
    }

    [Fact] // legal — 寿命到期：AgeTicks 达 LifetimeTicks 即过期（含等号）
    public void Twin_expires_exactly_at_its_lifetime()
    {
        SimProjectile twin = Twin(0f, 0f, 100f, 0f, lifetime: 3);
        Assert.False(twin.IsExpired);
        twin.Advance();
        twin.Advance();
        Assert.False(twin.IsExpired);
        twin.Advance();
        Assert.Equal(3L, twin.AgeTicks);
        Assert.True(twin.IsExpired);
    }

    [Fact] // legal — 对角航向归一化（IEEE 754 纯浮点，重放稳定）
    public void Diagonal_target_normalizes_the_heading()
    {
        // 东北 45°：dx=dy=10 → 航向分量 ≈ 0.7071
        SimProjectile twin = Twin(0f, 0f, 10f, 10f);
        twin.Advance();
        Assert.Equal(9f * 0.70710678118f, twin.X, 4);
        Assert.Equal(9f * 0.70710678118f, twin.Y, 4);
    }

    [Fact] // legal — 目标重合的退化航向固定为正北（确定性退化分支）
    public void Degenerate_target_flies_north()
    {
        SimProjectile twin = Twin(5f, 5f, 5f, 5f);
        twin.Advance();
        Assert.Equal(5f, twin.X, 5);
        Assert.Equal(-4f, twin.Y, 5); // 5 − 9
    }

    [Fact] // legal — K-6 契约面：Id/源建筑/伤害/年龄与构造参数一致；TargetUnitId 恒 null（hitscan 孪生）
    public void Contract_surface_matches_construction()
    {
        var id = new EntityId(42);
        var source = new EntityId(7);
        var twin = new SimProjectile(id, source, 0f, 0f, 64f, 64f, damage: 15, speedPxPerTick: 8f, lifetimeTicks: 6);
        Assert.Same(id, twin.Id);
        Assert.Same(source, twin.SourceBuildingId);
        Assert.Equal(15, twin.Damage);
        Assert.Equal(6, twin.LifetimeTicks);
        Assert.Null(twin.TargetUnitId);
        Assert.IsAssignableFrom<EraCore.Contracts.Sim.IProjectile>(twin);
    }

    [Fact] // illegal — 参数纪律：非正速度/寿命、负伤害、null 身份必须拒绝
    public void Invalid_construction_arguments_are_rejected()
    {
        var id = new EntityId(1);
        var source = new EntityId(2);
        Assert.Throws<ArgumentNullException>(() => new SimProjectile(null!, source, 0f, 0f, 1f, 1f, 1, 9f, 32));
        Assert.Throws<ArgumentNullException>(() => new SimProjectile(id, null!, 0f, 0f, 1f, 1f, 1, 9f, 32));
        Assert.Throws<ArgumentException>(() => new SimProjectile(id, source, 0f, 0f, 1f, 1f, 1, 0f, 32));
        Assert.Throws<ArgumentException>(() => new SimProjectile(id, source, 0f, 0f, 1f, 1f, 1, -1f, 32));
        Assert.Throws<ArgumentException>(() => new SimProjectile(id, source, 0f, 0f, 1f, 1f, 1, 9f, 0));
        Assert.Throws<ArgumentException>(() => new SimProjectile(id, source, 0f, 0f, 1f, 1f, 1, 9f, -3));
        Assert.Throws<ArgumentException>(() => new SimProjectile(id, source, 0f, 0f, 1f, 1f, -1, 9f, 32));
    }
}
