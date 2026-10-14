using EraCore.Contracts.Foundation;
using EraCore.Endless;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-080] 混编跨时代敌人池：全池/角色子池的权重映射精确可表（累积区间按
// 构造序），时代跨度=池的时代集合，校验拒绝空池/重复 id/越时代/非正权重/无常规行。
public class EndlessEnemyPoolTests
{
    private static EndlessEnemyPoolEntry Row(
        string id, int era, EndlessEnemyRole role, double weight) => new(id, era, role, weight);

    // ------------------------------------------------ 权重映射 ---------

    [Fact] // legal：全池权重映射——累积区间按构造序（draw 落点→行）精确钉死
    public void Whole_pool_weight_walk_is_exact()
    {
        var pool = new EndlessEnemyPool(new[]
        {
            Row("a", 1, EndlessEnemyRole.Regular, 1.0),
            Row("b", 2, EndlessEnemyRole.Regular, 1.0),
            Row("c", 3, EndlessEnemyRole.Regular, 2.0),
        });

        Assert.Equal(4.0, pool.TotalWeight);
        Assert.Equal("a", pool.Pick(0.0).EnemyId);
        Assert.Equal("a", pool.Pick(0.999).EnemyId);
        Assert.Equal("b", pool.Pick(1.0).EnemyId);
        Assert.Equal("b", pool.Pick(1.999).EnemyId);
        Assert.Equal("c", pool.Pick(2.0).EnemyId);
        Assert.Equal("c", pool.Pick(3.999).EnemyId);   // 域顶最后残差=末行兜底
    }

    [Fact] // legal：角色子池映射——各线在自身角色内归一（混编按线、权重按角色域）
    public void Role_sub_pools_normalise_inside_the_role()
    {
        var pool = new EndlessEnemyPool(new[]
        {
            Row("r1", 1, EndlessEnemyRole.Regular, 2.0),
            Row("r2", 2, EndlessEnemyRole.Regular, 1.0),
            Row("e1", 1, EndlessEnemyRole.Elite, 1.0),
            Row("b1", 3, EndlessEnemyRole.Boss, 1.0),
        });

        Assert.Equal(5.0, pool.TotalWeight);   // 全池权重=各行合计（角色域各自归一另计）
        Assert.Equal(3.0, pool.RoleWeight(EndlessEnemyRole.Regular)); // r1(2.0)+r2(1.0)
        Assert.Equal(1.0, pool.RoleWeight(EndlessEnemyRole.Elite));
        Assert.Equal(1.0, pool.RoleWeight(EndlessEnemyRole.Boss));
        Assert.Equal(0.0, pool.RoleWeight((EndlessEnemyRole)99));

        Assert.Equal("r1", pool.Pick(0.0, EndlessEnemyRole.Regular).EnemyId);
        Assert.Equal("r1", pool.Pick(1.999, EndlessEnemyRole.Regular).EnemyId);
        Assert.Equal("r2", pool.Pick(2.0, EndlessEnemyRole.Regular).EnemyId);
        Assert.Equal("e1", pool.Pick(0.5, EndlessEnemyRole.Elite).EnemyId);
        Assert.Equal("b1", pool.Pick(0.0, EndlessEnemyRole.Boss).EnemyId);
        Assert.True(pool.HasRole(EndlessEnemyRole.Elite));
        Assert.False(pool.HasRole((EndlessEnemyRole)99));
    }

    [Fact] // legal：时代跨度=池时代集合升序（混编跨时代证明位）
    public void Era_indexes_are_the_distinct_ascending_set()
    {
        var pool = new EndlessEnemyPool(new[]
        {
            Row("r2", 2, EndlessEnemyRole.Regular, 1.0),
            Row("r1", 1, EndlessEnemyRole.Regular, 1.0),
            Row("b6", 6, EndlessEnemyRole.Boss, 1.0),
            Row("e4", 4, EndlessEnemyRole.Elite, 1.0),
        });

        Assert.Equal(new[] { 1, 2, 4, 6 }, pool.EraIndexes);
    }

    // ------------------------------------------------ 种子抽取 ---------

    [Fact] // legal：种子抽取走 IRng 端口（CS-1.4）——同种子同池→同行；不同种子允许分行
    public void Seeded_pick_is_deterministic_per_seed()
    {
        var poolA = new EndlessEnemyPool(new[]
        {
            Row("a", 1, EndlessEnemyRole.Regular, 1.0),
            Row("b", 2, EndlessEnemyRole.Regular, 1.0),
        });

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 64; i++)
        {
            EndlessEnemyPoolEntry left = poolA.Pick(new LcgRng(EndlessFixture.Seed + i));
            EndlessEnemyPoolEntry right = poolA.Pick(new LcgRng(EndlessFixture.Seed + i));
            Assert.Equal(left.EnemyId, right.EnemyId);
            Assert.Contains(left.EnemyId, new[] { "a", "b" });
            ids.Add(left.EnemyId);
        }

        Assert.Contains("a", ids); // 两种子族两种子域都该被 64 次抽样覆盖
        Assert.Contains("b", ids);
    }

    // ------------------------------------------------ 校验 -------------

    [Fact] // boundary：空池/空 id/重复 id/越时代/非正权重/无常规行 全拒绝
    public void Invalid_pools_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new EndlessEnemyPool(Array.Empty<EndlessEnemyPoolEntry>()));
        Assert.Throws<ArgumentException>(() => new EndlessEnemyPool(new[]
        {
            Row(" ", 1, EndlessEnemyRole.Regular, 1.0),
        }));
        Assert.Throws<ArgumentException>(() => new EndlessEnemyPool(new[]
        {
            Row("a", 1, EndlessEnemyRole.Regular, 1.0),
            Row("a", 1, EndlessEnemyRole.Elite, 1.0),
        }));
        Assert.Throws<ArgumentException>(() => new EndlessEnemyPool(new[]
        {
            Row("a", 0, EndlessEnemyRole.Regular, 1.0),
        }));
        Assert.Throws<ArgumentException>(() => new EndlessEnemyPool(new[]
        {
            Row("a", 7, EndlessEnemyRole.Regular, 1.0),
        }));
        Assert.Throws<ArgumentException>(() => new EndlessEnemyPool(new[]
        {
            Row("a", 1, EndlessEnemyRole.Regular, 0.0),
        }));
        Assert.Throws<ArgumentException>(() => new EndlessEnemyPool(new[]
        {
            Row("e", 1, EndlessEnemyRole.Elite, 1.0),
            Row("b", 1, EndlessEnemyRole.Boss, 1.0),
        })); // 无常规行——常规线永不落空
    }

    [Fact] // boundary：draw 越界拒绝（[0,总权) 半开域）；缺席角色拒绝
    public void Out_of_domain_draws_are_rejected()
    {
        var pool = new EndlessEnemyPool(new[]
        {
            Row("a", 1, EndlessEnemyRole.Regular, 1.0),
            Row("b", 2, EndlessEnemyRole.Elite, 1.0),
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Pick(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Pick(2.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Pick(1.0, EndlessEnemyRole.Regular));
        Assert.Throws<ArgumentException>(() => pool.Pick(0.0, EndlessEnemyRole.Boss));
        Assert.Throws<ArgumentNullException>(() => pool.Pick((IRng)null!));
    }
}
