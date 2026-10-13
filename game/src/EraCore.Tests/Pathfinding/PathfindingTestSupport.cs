using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;

namespace EraCore.Tests.Pathfinding;

/// <summary>
/// Mutable in-memory <see cref="IFlowGrid"/> for pathfinding tests: base cost 1
/// everywhere unless overridden, hard walls via <see cref="Block"/>, tearable
/// buildings via <see cref="Blood"/> (C-11 test seam for the future
/// World.GridWorld implementation).
/// </summary>
internal sealed class MapGrid : IFlowGrid
{
    private readonly HashSet<long> _blocked = new();
    private readonly Dictionary<long, int> _cost = new();
    private readonly Dictionary<long, int> _blood = new();

    public MapGrid(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Grid dimensions must be positive.");
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public int Cost(CellCoord c) => _cost.TryGetValue(Pack(c.X, c.Y), out int value) ? value : 1;

    public bool Passable(CellCoord c) => !_blocked.Contains(Pack(c.X, c.Y));

    public int BloodCost(CellCoord c) => _blood.TryGetValue(Pack(c.X, c.Y), out int value) ? value : 0;

    public MapGrid Block(int x, int y)
    {
        _blocked.Add(Pack(x, y));
        return this;
    }

    public MapGrid BlockColumn(int x, int yFrom, int yTo)
    {
        for (int y = yFrom; y <= yTo; y++)
        {
            _blocked.Add(Pack(x, y));
        }

        return this;
    }

    public MapGrid Blood(int x, int y, int hp)
    {
        _blood[Pack(x, y)] = hp;
        return this;
    }

    public MapGrid BloodColumn(int x, int yFrom, int yTo, int hp)
    {
        for (int y = yFrom; y <= yTo; y++)
        {
            _blood[Pack(x, y)] = hp;
        }

        return this;
    }

    public MapGrid SetCost(int x, int y, int cost)
    {
        _cost[Pack(x, y)] = cost;
        return this;
    }

    public bool IsBlocked(int x, int y) => _blocked.Contains(Pack(x, y));

    public int BloodAt(int x, int y) => _blood.TryGetValue(Pack(x, y), out int value) ? value : 0;

    private long Pack(int x, int y) => (long)y * Width + x;
}

/// <summary>
/// Counter probe wrapping another grid: records how often each port method is
/// consulted, so tests can prove exactly which buckets a rebuild touched
/// (CS-4.5: real sampling, no cached shortcuts).
/// </summary>
internal sealed class CountingGrid : IFlowGrid
{
    private readonly IFlowGrid _inner;

    public CountingGrid(IFlowGrid inner)
    {
        _inner = inner;
    }

    public int CostCalls { get; private set; }

    public int PassableCalls { get; private set; }

    public int BloodCalls { get; private set; }

    public void Reset()
    {
        CostCalls = 0;
        PassableCalls = 0;
        BloodCalls = 0;
    }

    public int Width => _inner.Width;

    public int Height => _inner.Height;

    public int Cost(CellCoord c)
    {
        CostCalls++;
        return _inner.Cost(c);
    }

    public bool Passable(CellCoord c)
    {
        PassableCalls++;
        return _inner.Passable(c);
    }

    public int BloodCost(CellCoord c)
    {
        BloodCalls++;
        return _inner.BloodCost(c);
    }
}

/// <summary>Deterministic linear congruential generator (test-side, seeded per instance).</summary>
internal sealed class Lcg
{
    private uint _state;

    public Lcg(uint seed) => _state = seed;

    public uint Next() => _state = _state * 1664525u + 1013904223u;

    public int NextInt(int bound) => (int)(Next() % (uint)bound);

    public bool Chance(double threshold) => Next() / 4294967296.0 < threshold;
}

/// <summary>Deterministic obstacle-map factories shared by the pathfinding tests.</summary>
internal static class TestMazes
{
    /// <summary>
    /// Sparse random obstacle map; spawn and target cells are kept free so the
    /// field always has at least one reachable query point.
    /// </summary>
    public static MapGrid SparseObstacles(int size, uint seed, double density)
    {
        var grid = new MapGrid(size, size);
        var rng = new Lcg(seed);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if ((x == 0 && y == 0) || (x == size - 1 && y == size - 1))
                {
                    rng.Next();
                    continue;
                }

                if (rng.Chance(density))
                {
                    grid.Block(x, y);
                }
            }
        }

        return grid;
    }
}
