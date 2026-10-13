using EraCore.World;
using Xunit;

namespace EraCore.Tests.World;

// [C-05] terrain enum rules: passability and movement cost of the E1 terrain set
public class TerrainKindTests
{
    [Theory] // legal — walkable kinds
    [InlineData(TerrainKind.Plain)]
    [InlineData(TerrainKind.Forest)]
    [InlineData(TerrainKind.Road)]
    public void IsPassable_accepts_walkable_terrain(TerrainKind kind)
    {
        Assert.True(TerrainInfo.IsPassable(kind));
    }

    [Theory] // illegal — hard blockers
    [InlineData(TerrainKind.Rock)]
    [InlineData(TerrainKind.Water)]
    [InlineData(TerrainKind.Wall)]
    public void IsPassable_rejects_hard_blockers(TerrainKind kind)
    {
        Assert.False(TerrainInfo.IsPassable(kind));
    }

    [Theory] // legal — finite costs on walkable kinds
    [InlineData(TerrainKind.Plain, 1)]
    [InlineData(TerrainKind.Road, 1)]
    [InlineData(TerrainKind.Forest, 2)] // [NEW-原型校准] woods slow movement
    public void Cost_matches_terrain_cost_table(TerrainKind kind, int expected)
    {
        Assert.Equal(expected, TerrainInfo.Cost(kind));
    }

    [Theory] // boundary — blockers report the neutral cost 1 (value unused, gated by Passable)
    [InlineData(TerrainKind.Rock)]
    [InlineData(TerrainKind.Water)]
    [InlineData(TerrainKind.Wall)]
    public void Cost_of_blockers_stays_at_neutral_one(TerrainKind kind)
    {
        Assert.Equal(1, TerrainInfo.Cost(kind));
    }

    [Fact] // boundary — every enum value must be classified by both rule sets
    public void Rules_cover_the_whole_E1_terrain_set()
    {
        var all = (TerrainKind[])Enum.GetValues(typeof(TerrainKind));
        Assert.Equal(
            new[] { "Forest", "Plain", "Road", "Rock", "Wall", "Water" },
            all.Select(k => k.ToString()).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(all, k => Assert.True(TerrainInfo.Cost(k) >= 1));
        Assert.Equal(3, all.Count(TerrainInfo.IsPassable));
    }
}
