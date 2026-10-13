using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// Joint integration of the C-05 grid world with the C-11 flow field
/// (T-032×T-037 seam, [F-043]/[F-044]): a real <see cref="GridWorld"/>
/// assembled from seeded procedural terrain drives
/// <see cref="FlowFieldManager"/> through the <c>IFlowGrid</c> port — no
/// MapGrid double, no mocked gradient (RL2). Covers: gradient walks that
/// actually reach the target across the generated map, terrain edits
/// (player walls) picked up via MarkDirty and producing a detour, and the
/// terrain semantics the field relies on (water never traversable, forest
/// cost raising integrated distances).
/// </summary>
// [F-043] GridWorld→FlowFieldManager joint integration (C-05×C-11)
public class GridWorldFlowFieldIntegrationTests
{
    private const long Seed = 20261014;

    // Blocker ratio picked so the seeded 64×40 world has zero corner-rule
    // sealed pockets (verified for this seed): 2% water + 2% rock — sparse
    // enough that every passable cell stays reachable, realistic enough that
    // the gradient actually routes around blockers. Note the diagonal corner
    // rule (对角卡角检测) can seal a passable cell even under sparse blockers
    // (rock above + below + water flank + both diagonal corners cut) — the
    // field would correctly answer WallTear for such maps; this seed avoids
    // that so Normal-mode end-to-end walks are the expectation.
    private static GridWorld SeededWorld(
        int width, int height, double forest = 0.20, double rock = 0.02, double water = 0.02) =>
        new(width, height, new EraCore.Tests.World.LcgRng(Seed), forest, rock, water);

    private static FlowField Settle(FlowFieldManager manager, FlowFieldKey key)
    {
        while (!manager.Update(1000))
        {
        }

        var field = manager.GetField(key);
        Assert.NotNull(field);
        return field!;
    }

    private static CellCoord FirstPassableFromOrigin(GridWorld world)
    {
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new CellCoord(x, y);
                if (world.Passable(cell))
                {
                    return cell;
                }
            }
        }

        throw new InvalidOperationException("No passable cell in the generated world.");
    }

    private static CellCoord LastPassableFromFarCorner(GridWorld world)
    {
        for (int y = world.Height - 1; y >= 0; y--)
        {
            for (int x = world.Width - 1; x >= 0; x--)
            {
                var cell = new CellCoord(x, y);
                if (world.Passable(cell))
                {
                    return cell;
                }
            }
        }

        throw new InvalidOperationException("No passable cell in the generated world.");
    }

    // ------------------------------------------------------------ ① end-to-end path

    [Fact] // [F-043] seeded terrain → real field → sampled NextStep chains reach the target end to end
    public void SeededWorldGradientWalksReachTheTargetFromSampledStarts()
    {
        var world = SeededWorld(64, 40);
        var target = LastPassableFromFarCorner(world);
        var manager = new FlowFieldManager(world);
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, target);
        var field = Settle(manager, key);

        Assert.True(field.IsReady);
        Assert.Equal(FlowFieldMode.Normal, field.Mode); // pocket-free seed: every passable cell reachable on walk terrain alone

        // Eight start points sampled across the map (deterministic Lcg), every
        // passable one must walk the gradient to the target cell itself.
        var rng = new Lcg(7);
        int walked = 0;
        for (int i = 0; i < 8; i++)
        {
            var start = new CellCoord(rng.NextInt(world.Width), rng.NextInt(world.Height));
            if (!world.Passable(start))
            {
                continue;
            }

            Assert.True(field.IsReachable(start), $"sampled start ({start.X},{start.Y}) must be reachable");

            var current = start;
            long previous = field.Distance(current);
            int stepBudget = world.Width * world.Height + 2;
            while (current.X != target.X || current.Y != target.Y)
            {
                var next = field.NextStep(current);
                Assert.NotEqual(current, next); // gradient must make progress on a reachable cell
                Assert.True(world.Passable(next), $"gradient stepped onto impassable terrain at ({next.X},{next.Y})");
                long distance = field.Distance(next);
                Assert.True(distance < previous, "integrated distance must strictly decrease along the walk");
                previous = distance;
                current = next;
                if (--stepBudget == 0)
                {
                    throw new InvalidOperationException("Gradient walk exceeded the cell budget.");
                }
            }

            walked++;
        }

        Assert.True(walked >= 4, $"expected several passable samples, got {walked}");
    }

    // ------------------------------------------------------------ ② edit → dirty → rebuild → detour

    [Fact] // [F-043] walling the old corridor forces MarkDirty → rebuild → a different route that avoids the walls
    public void WallEditMarksDirtyRebuildsAndDetours()
    {
        var world = SeededWorld(64, 40);
        var start = FirstPassableFromOrigin(world);
        var target = LastPassableFromFarCorner(world);
        var manager = new FlowFieldManager(world);
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, target);
        var field = Settle(manager, key);

        var oldPath = field.BuildPath(start);
        Assert.Equal(target, oldPath[^1]);

        // Wall the middle third of the old route (player-built walls).
        int from = oldPath.Count / 3;
        int to = Math.Min(oldPath.Count * 2 / 3, oldPath.Count - 1);
        var walled = new HashSet<CellCoord>();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = from; i <= to; i++)
        {
            var cell = oldPath[i];
            if (cell == target || world.TerrainAt(cell) == TerrainKind.Rock)
            {
                continue; // never wall the target itself
            }

            world.SetTerrain(cell, TerrainKind.Wall);
            walled.Add(cell);
            minX = Math.Min(minX, cell.X);
            minY = Math.Min(minY, cell.Y);
            maxX = Math.Max(maxX, cell.X);
            maxY = Math.Max(maxY, cell.Y);
        }

        Assert.NotEmpty(walled);
        Assert.Contains(oldPath, c => walled.Contains(c)); // the old route really ran through them

        // Dirty region tightly covering the edit; the rebuild must re-read the mutated terrain.
        manager.MarkDirty(new CellRegion(minX, minY, maxX - minX + 1, maxY - minY + 1));
        var rebuilt = Settle(manager, key);
        Assert.Equal(2, manager.GetRebuildCount(key));

        var newPath = rebuilt.BuildPath(start);
        Assert.Equal(target, newPath[^1]); // still arrives end to end
        Assert.NotEqual(oldPath, newPath); // and it is a detour, not the walled route
        Assert.All(newPath, c => Assert.False(walled.Contains(c), $"detour stepped on a walled cell at ({c.X},{c.Y})"));
    }

    // ------------------------------------------------------------ ③ terrain semantics → field expectations

    [Fact] // [F-044] water is a hard blocker to the field in every mode; the gradient never proposes impassable terrain
    public void WaterIsNeverTraversableToTheField()
    {
        var world = SeededWorld(48, 32, forest: 0.2, rock: 0.1, water: 0.3);
        var target = LastPassableFromFarCorner(world);
        var field = FlowField.BuildComplete(world, target);

        int waterCells = 0;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                var cell = new CellCoord(x, y);
                if (world.TerrainAt(cell) == TerrainKind.Water)
                {
                    waterCells++;
                    Assert.False(world.Passable(cell), "water must be impassable on the terrain layer");
                    Assert.False(field.IsReachable(cell), "water must stay unreachable in every field mode");
                }
            }
        }

        Assert.True(waterCells > 0, "the seeded map must actually contain water for this test");

        // Sampled gradient reads only ever step onto passable terrain.
        var rng = new Lcg(31);
        for (int i = 0; i < 16; i++)
        {
            var cell = new CellCoord(rng.NextInt(world.Width), rng.NextInt(world.Height));
            if (!world.Passable(cell) || !field.IsReachable(cell) || (cell.X == target.X && cell.Y == target.Y))
            {
                continue;
            }

            var next = field.NextStep(cell);
            if (next != cell)
            {
                Assert.True(world.Passable(next), $"gradient proposed impassable terrain at ({next.X},{next.Y})");
            }
        }
    }

    [Fact] // [F-043] forest cost flows through the seam: integrated distance rises exactly by the forest premium
    public void ForestCostRaisesIntegratedDistanceThroughTheManager()
    {
        // Controlled single-row world (no diagonals, no procedural noise):
        // straight step weight = 10 (FlowField), plain cost 1, forest cost 2
        // (TerrainInfo [NEW-原型校准]).
        var world = new GridWorld(5, 1, new EraCore.Tests.World.LcgRng(Seed), 0, 0, 0);
        var target = new CellCoord(4, 0);
        var manager = new FlowFieldManager(world);
        var key = new FlowFieldKey(0, 0);
        manager.Acquire(key, target);
        var plain = Settle(manager, key);

        // All plain: 0→4 = 4 straight entries × 10 × cost 1 = 40.
        Assert.Equal(40L, plain.Distance(new CellCoord(0, 0)));

        // Grow a forest over cells 1..3, dirty-mark exactly that region, rebuild.
        world.SetTerrain(new CellCoord(1, 0), TerrainKind.Forest);
        world.SetTerrain(new CellCoord(2, 0), TerrainKind.Forest);
        world.SetTerrain(new CellCoord(3, 0), TerrainKind.Forest);
        manager.MarkDirty(new CellRegion(1, 0, 3, 1));
        var forest = Settle(manager, key);
        Assert.Equal(2, manager.GetRebuildCount(key));

        // 0→4 = 3 forest entries × 10 × 2 + 1 plain entry × 10 × 1 = 70.
        Assert.Equal(70L, forest.Distance(new CellCoord(0, 0)));

        // The gradient still walks the row to the target under the new costs.
        var path = forest.BuildPath(new CellCoord(0, 0));
        Assert.Equal(target, path[^1]);
    }
}
