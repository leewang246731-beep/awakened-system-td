using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using Xunit;

namespace EraCore.Tests.Logistics;

/// <summary>
/// Determinism tests for the transport stack (C-07 precursor, T-047,
/// F-040): same seed + same request sequence on two independent
/// world→A*→cache stacks yields the identical hit/miss sequence, path
/// hashes and invalidation outcomes; same porter scenario replayed twice
/// yields the identical state trace. Nothing here uses a clock or RNG
/// (CS-1.4) — the entire pipeline is a pure function of its inputs.
/// </summary>
// [F-040] 确定性：同 seed 同请求序列 → 缓存命中序列与路径哈希一致
public class LogisticsDeterminismTests
{
    private const long Seed = 20261014;

    private sealed record StackState(
        List<(bool Hit, PathQueryStatus Status, long Hash, long Cost)> Outcomes,
        List<int> RemovedCounts);

    private static StackState RunScriptedScenario()
    {
        var world = LogisticsWorlds.SeededWorld(24, 16, Seed);
        var cache = new PathCache(new AStarPathfinder(world));

        // Deterministic endpoint harvest: the first eight passable cells in
        // row-major order, paired fixed→walking — same seed ⇒ same cells.
        var endpoints = new List<CellCoord>();
        for (int y = 0; y < world.Height && endpoints.Count < 8; y++)
        {
            for (int x = 0; x < world.Width && endpoints.Count < 8; x++)
            {
                var cell = new CellCoord(x, y);
                if (world.Passable(cell))
                {
                    endpoints.Add(cell);
                }
            }
        }

        // Same request sequence for both runs: mix of fresh queries, repeats
        // (hits) and reversed pairs.
        var requests = new List<(CellCoord From, CellCoord To)>();
        for (int i = 0; i + 1 < endpoints.Count; i += 2)
        {
            requests.Add((endpoints[i], endpoints[i + 1]));
        }

        requests.Add((endpoints[0], endpoints[1])); // repeat → hit
        requests.Add((endpoints[1], endpoints[0])); // reversed → fresh query
        requests.Add((endpoints[2], endpoints[3])); // repeat → hit

        var outcomes = new List<(bool Hit, PathQueryStatus Status, long Hash, long Cost)>();
        foreach (var (from, to) in requests)
        {
            long hitsBefore = cache.Hits;
            PathQueryResult result = cache.GetPath(from, to);
            outcomes.Add((cache.Hits > hitsBefore, result.Status, result.Hash, result.Cost));
        }

        // Deterministic invalidation sequence: the middle cells of every
        // successfully cached route, plus one passable cell no route touches.
        var removedCounts = new List<int>();
        var invalidateCells = new List<CellCoord>();
        foreach (var (from, to) in requests)
        {
            if (cache.TryGetCachedPath(from, to, out PathQueryResult? route) && route!.Cells.Count >= 3)
            {
                invalidateCells.Add(route.Cells[route.Cells.Count / 2]);
            }
        }

        invalidateCells.Add(new CellCoord(world.Width - 1, 0));
        removedCounts.Add(cache.Invalidate(invalidateCells));

        // Second wave after invalidation: same requests again — the hit/miss
        // pattern must replay identically for both stacks.
        foreach (var (from, to) in requests)
        {
            long hitsBefore = cache.Hits;
            PathQueryResult result = cache.GetPath(from, to);
            outcomes.Add((cache.Hits > hitsBefore, result.Status, result.Hash, result.Cost));
        }

        removedCounts.Add(cache.Invalidate(new CellCoord(0, 0)));

        return new StackState(outcomes, removedCounts);
    }

    private static List<int> RunPorterScenario()
    {
        var world = LogisticsWorlds.PlainWorld(15, 7, Seed);
        var cache = new PathCache(new AStarPathfinder(world));
        PathQueryResult route = cache.GetPath(new CellCoord(0, 3), new CellCoord(14, 3));
        var supplier = new StockSupplier(int.MaxValue);
        var receiver = new GateReceiver(int.MaxValue);
        var porter = new Carrier(new EntityId(42), new ResourceKey("core:arrows"), supplier, receiver, route.Cells);

        var deliveryTicks = new List<int>();
        for (int tick = 1; tick <= 800; tick++)
        {
            porter.Tick();
            if (porter.DeliveredItems > deliveryTicks.Count * 2L)
            {
                deliveryTicks.Add(tick);
            }
        }

        return deliveryTicks;
    }

    [Fact]
    public void SameSeedAndRequestSequenceYieldSameHitSequenceAndHashes()
    {
        // [F-040] 命中序列+哈希逐项全等（两波各 7 条请求 + 两次失效计数）
        StackState first = RunScriptedScenario();
        StackState second = RunScriptedScenario();

        Assert.Equal(first.Outcomes.Count, second.Outcomes.Count);
        for (int i = 0; i < first.Outcomes.Count; i++)
        {
            Assert.Equal(first.Outcomes[i].Hit, second.Outcomes[i].Hit);
            Assert.Equal(first.Outcomes[i].Status, second.Outcomes[i].Status);
            Assert.Equal(first.Outcomes[i].Hash, second.Outcomes[i].Hash);
            Assert.Equal(first.Outcomes[i].Cost, second.Outcomes[i].Cost);
        }

        Assert.Equal(first.RemovedCounts, second.RemovedCounts);
    }

    [Fact]
    public void PorterScenarioReplayYieldsIdenticalDeliveryTicks()
    {
        // [F-042] 民夫重放：同 seed 世界同路线同请求序列 → 交付 tick 序列全等。
        // 手算：单程 14 格 = 448 px → 224 tick/趟；t=20 出发 → t=244 到 →
        // t=264 首交付；周期 488 tick → t=752 次交付
        List<int> first = RunPorterScenario();
        List<int> second = RunPorterScenario();

        Assert.Equal(new[] { 264, 752 }, first);
        Assert.Equal(first, second);
    }
}
