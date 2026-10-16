using EraCore.Endless;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-118] 时代内容集 → 无尽混编池构建器：is_boss→Boss 子池、最重
// 非Boss 行→精英线、其余→常规线；跨时代合集；权重 1.0 均匀；输入守卫。
// 真 era3 内容集的入口级混编断言在 EndlessEntryTests（E3 无尽可用）。
public class EndlessEraPoolsTests
{
    private static EnemySpec Enemy(string id, int hp, bool isBoss = false) =>
        new(id, $"sp-{id}", $"名-{id}", isBoss, hp, 0, 1.0, 0, isBoss ? 96 : 32);

    private static EndlessEraEnemySet Set(int era, params EnemySpec[] enemies) => new(era, enemies);

    [Fact] // legal：角色三分——最重非 Boss 行进精英线、is_boss 进 Boss 池、其余进常规线
    public void Heaviest_rows_become_the_elite_line()
    {
        EndlessEnemyPool pool = EndlessEraPools.Build(new[]
        {
            Set(1,
                Enemy("era1:light", 100),
                Enemy("era1:heavy", 900),
                Enemy("era1:mid", 300),
                Enemy("era1:boss", 5000, isBoss: true)),
        });

        Assert.Equal(new[] { "era1:light", "era1:mid" }, IdsOfRole(pool, EndlessEnemyRole.Regular));
        Assert.Equal(new[] { "era1:heavy" }, IdsOfRole(pool, EndlessEnemyRole.Elite)); // HP 900 最重
        Assert.Equal(new[] { "era1:boss" }, IdsOfRole(pool, EndlessEnemyRole.Boss));   // is_boss 行
        Assert.Equal(new[] { 1 }, pool.EraIndexes);
        Assert.True(pool.HasRole(EndlessEnemyRole.Elite));
        Assert.True(pool.HasRole(EndlessEnemyRole.Boss));
    }

    [Fact] // legal：HP 平手按 id 序破平——精英选择与角色映射是输入行序无关的确定规则
    public void Equal_hp_breaks_ties_by_id_order()
    {
        EndlessEnemyPool left = EndlessEraPools.Build(new[]
        {
            Set(2, Enemy("era2:b", 500), Enemy("era2:a", 500), Enemy("era2:c", 100)),
        });
        EndlessEnemyPool right = EndlessEraPools.Build(new[]
        {
            Set(2, Enemy("era2:c", 100), Enemy("era2:b", 500), Enemy("era2:a", 500)),
        });

        Assert.Equal(new[] { "era2:a" }, IdsOfRole(left, EndlessEnemyRole.Elite)); // id 序最前的平手行
        Assert.Equal(new[] { "era2:a" }, IdsOfRole(right, EndlessEnemyRole.Elite));
        Assert.Equal(RoleMap(left), RoleMap(right)); // id→角色映射恒定（条目序随构造序，映射不随）
    }

    private static IReadOnlyDictionary<string, EndlessEnemyRole> RoleMap(EndlessEnemyPool pool) =>
        pool.Entries.ToDictionary(entry => entry.EnemyId, entry => entry.Role);

    [Fact] // legal：跨时代合集——每时代各出三条线，时代集合并集且角色内混编
    public void Multiple_eras_mix_into_one_pool()
    {
        EndlessEnemyPool pool = EndlessEraPools.Build(new[]
        {
            Set(1, Enemy("era1:a", 100), Enemy("era1:b", 900), Enemy("era1:boss", 5000, true)),
            Set(2, Enemy("era2:a", 100), Enemy("era2:b", 800), Enemy("era2:boss", 6000, true)),
            Set(3, Enemy("era3:a", 100), Enemy("era3:b", 700), Enemy("era3:boss", 9000, true)),
        });

        Assert.Equal(new[] { 1, 2, 3 }, pool.EraIndexes);
        Assert.Equal(3, IdsOfRole(pool, EndlessEnemyRole.Regular).Count); // 每时代一条常规行
        Assert.Equal(3, IdsOfRole(pool, EndlessEnemyRole.Elite).Count);
        Assert.Equal(3, IdsOfRole(pool, EndlessEnemyRole.Boss).Count);
        Assert.Contains("era3:boss", IdsOfRole(pool, EndlessEnemyRole.Boss)); // E3 Boss 在池（堡垒号）
    }

    [Fact] // legal：每时代精英线行数可调（elitesPerEra=2）且权重一律 1.0
    public void Elites_per_era_is_tunable_and_weights_are_uniform()
    {
        EndlessEnemyPool pool = EndlessEraPools.Build(new[]
        {
            Set(1, Enemy("era1:a", 100), Enemy("era1:b", 200), Enemy("era1:c", 300), Enemy("era1:d", 400)),
        }, elitesPerEra: 2);

        Assert.Equal(new[] { "era1:a", "era1:b" }, IdsOfRole(pool, EndlessEnemyRole.Regular));
        Assert.Equal(new[] { "era1:c", "era1:d" }, IdsOfRole(pool, EndlessEnemyRole.Elite)); // 构造序（HP 300/400 两行入选）
        Assert.All(pool.Entries, entry => Assert.Equal(1.0, entry.Weight));
    }

    // illegal：守卫矩阵——空合集/时代越界/非 Boss 行不足以同时供精英线与常规线/跨集重复 id/零精英行数
    [Fact]
    public void Build_guards_reject_structural_violations()
    {
        Assert.Throws<ArgumentException>(() => EndlessEraPools.Build(Array.Empty<EndlessEraEnemySet>()));
        Assert.Throws<ArgumentException>(() => EndlessEraPools.Build(new[] { Set(0, Enemy("e:a", 1), Enemy("e:b", 2)) }));
        Assert.Throws<ArgumentException>(() => EndlessEraPools.Build(new[] { Set(7, Enemy("e:a", 1), Enemy("e:b", 2)) }));
        Assert.Throws<ArgumentException>(
            () => EndlessEraPools.Build(new[] { Set(1, Enemy("era1:only", 100)) })); // 1 行养不活两条线
        Assert.Throws<ArgumentException>(() => EndlessEraPools.Build(
            new[] { Set(1, Enemy("dup", 100), Enemy("era1:b", 200)), Set(2, Enemy("dup", 100), Enemy("era2:b", 200)) }));
        Assert.Throws<ArgumentException>(
            () => EndlessEraPools.Build(new[] { Set(1, Enemy("era1:a", 1), Enemy("era1:b", 2)) }, elitesPerEra: 0));
    }

    private static IReadOnlyList<string> IdsOfRole(EndlessEnemyPool pool, EndlessEnemyRole role) =>
        pool.Entries.Where(entry => entry.Role == role).Select(entry => entry.EnemyId).ToArray();
}
