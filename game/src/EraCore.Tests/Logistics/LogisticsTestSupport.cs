using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.World;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Mutable in-memory cargo ports for the carrier tests (T-047): a stocked
/// supplier and a gated receiver, both recording every call so tests can
/// assert the exact accept/serve discipline (待卸队列) and supply starvation.
/// </summary>
internal sealed class StockSupplier : ICargoSupplier
{
    public StockSupplier(int initialStock) => Stock = initialStock;

    public int Stock { get; set; }

    public List<(ResourceKey Resource, int MaxAmount)> Requests { get; } = new();

    public int TryTake(ResourceKey resource, int maxAmount)
    {
        Requests.Add((resource, maxAmount));
        int given = Math.Min(Stock, maxAmount);
        Stock -= given;
        return given;
    }
}

/// <summary>
/// Destination-side gate: accepts up to its remaining capacity per call and
/// records the call sequence — set <see cref="RemainingCapacity"/> to 0 to
/// force the 待卸队列 refusal path.
/// </summary>
internal sealed class GateReceiver : ICargoReceiver
{
    public GateReceiver(int initialCapacity) => RemainingCapacity = initialCapacity;

    public int RemainingCapacity { get; set; }

    public List<(ResourceKey Resource, int Amount)> AcceptCalls { get; } = new();

    public int TryAccept(ResourceKey resource, int amount)
    {
        AcceptCalls.Add((resource, amount));
        int accepted = Math.Min(RemainingCapacity, amount);
        RemainingCapacity -= accepted;
        return accepted;
    }
}

/// <summary>
/// Deterministic world factories and cell scans for the Logistics joint
/// tests (real <see cref="GridWorld"/> terrain, no MapGrid doubles — RL2).
/// </summary>
internal static class LogisticsWorlds
{
    /// <summary>
    /// All-plain world (ratios 0) — the RNG still draws per cell, so two
    /// worlds from the same seed are identical (C-05 determinism).
    /// </summary>
    public static GridWorld PlainWorld(int width, int height, long seed) =>
        new(width, height, new EraCore.Tests.World.LcgRng(seed), 0, 0, 0);

    /// <summary>
    /// All-forest world (cost 2 everywhere) — the spike② background choice:
    /// a paved corridor (Road, cost 1) is then genuinely cheaper for
    /// porters, which the corridor traffic-rule tests (T-064) rely on.
    /// </summary>
    public static GridWorld ForestWorld(int width, int height, long seed) =>
        new(width, height, new EraCore.Tests.World.LcgRng(seed), 1.0, 0, 0);

    /// <summary>Seeded sparse world (same shape the C-05×C-11 joint tests verified pocket-free).</summary>
    public static GridWorld SeededWorld(int width, int height, long seed) =>
        new(width, height, new EraCore.Tests.World.LcgRng(seed), 0.20, 0.02, 0.02);

    /// <summary>First passable cell in row-major order.</summary>
    public static CellCoord FirstPassable(GridWorld world)
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

        throw new InvalidOperationException("World has no passable cell.");
    }

    /// <summary>Last passable cell in row-major order.</summary>
    public static CellCoord LastPassable(GridWorld world)
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

        throw new InvalidOperationException("World has no passable cell.");
    }

    /// <summary>
    /// First pair of side-adjacent passable cells, scanning from the far
    /// corner backwards — a short local route guaranteed to exist and be
    /// found (used as the "unaffected other route" in invalidation tests).
    /// </summary>
    public static (CellCoord From, CellCoord To) AdjacentPassablePairFromEnd(GridWorld world)
    {
        for (int y = world.Height - 1; y >= 0; y--)
        {
            for (int x = world.Width - 1; x >= 1; x--)
            {
                var right = new CellCoord(x, y);
                var left = new CellCoord(x - 1, y);
                if (world.Passable(right) && world.Passable(left))
                {
                    return (left, right);
                }
            }
        }

        throw new InvalidOperationException("World has no adjacent passable pair.");
    }
}
