using EraCore.Procedural;
using Xunit;

namespace EraCore.Tests.Procedural;

// [C-19][F-007] T-140 程序关 seed 派生基元：确定性/域分离/重摇链派生
//（attempt 入哈希——同 seed 不同 attempt 必不同流）。
public class ProceduralSeedTests
{
    private const long Seed = 20261018;

    [Fact]
    public void Same_inputs_derive_the_same_child_seed()
    {
        // legal — 确定性：同 seed+域+attempt 双调全等（跨实例，非缓存）。
        long first = ProceduralSeed.Derive(Seed, "terrain", 0);
        long second = ProceduralSeed.Derive(Seed, "terrain", 0);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("terrain", "layout")]
    [InlineData("terrain", "reroll")]
    [InlineData("layout", "waves")]
    public void Different_domains_derive_different_streams(string left, string right)
    {
        // legal — 域分离：两个子流（地形/布局）不得共用一条随机流。
        Assert.NotEqual(
            ProceduralSeed.Derive(Seed, left, 0),
            ProceduralSeed.Derive(Seed, right, 0));
    }

    [Fact]
    public void Different_attempts_derive_different_streams()
    {
        // legal — 重摇链：同 seed 同域下 attempt 0..3 两两互异
        //（重摇次数入哈希的基元面——不同重摇路径不撞同一地形流）。
        long[] streams =
        {
            ProceduralSeed.Derive(Seed, "terrain", 0),
            ProceduralSeed.Derive(Seed, "terrain", 1),
            ProceduralSeed.Derive(Seed, "terrain", 2),
            ProceduralSeed.Derive(Seed, "terrain", 3),
        };
        Assert.Equal(streams.Length, streams.Distinct().Count());
    }

    [Fact]
    public void Different_seeds_derive_different_streams()
    {
        // legal — 异 seed 相异（同 seed 同图的对偶面）。
        Assert.NotEqual(
            ProceduralSeed.Derive(Seed, "terrain", 0),
            ProceduralSeed.Derive(Seed + 1, "terrain", 0));
    }

    [Fact]
    public void ToMapSeed_projects_into_the_positive_int32_window()
    {
        // legal — map.seed 装载面（T-085 schema int 读）投影：正值 int 窗。
        long derived = ProceduralSeed.Derive(Seed, "terrain", 0);
        int mapSeed = ProceduralSeed.ToMapSeed(derived);
        Assert.InRange(mapSeed, 1, int.MaxValue);

        // boundary — 投影确定性：同 derived 同投影。
        Assert.Equal(mapSeed, ProceduralSeed.ToMapSeed(derived));

        // boundary — 全 int32 窗内的 derived（含负值/极值）都落入 [1,int.MaxValue]。
        Assert.InRange(ProceduralSeed.ToMapSeed(long.MinValue), 1, int.MaxValue);
        Assert.InRange(ProceduralSeed.ToMapSeed(-1), 1, int.MaxValue);
        Assert.InRange(ProceduralSeed.ToMapSeed(0), 1, int.MaxValue);
    }

    [Fact]
    public void Null_domain_or_negative_attempt_is_rejected()
    {
        // illegal — 入参守卫：空域/负 attempt 抛（确定性派生不吞错）。
        Assert.Throws<ArgumentNullException>(() => ProceduralSeed.Derive(Seed, null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProceduralSeed.Derive(Seed, "terrain", -1));
    }
}
