using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.World;

namespace EraCore.Procedural;

/// <summary>
/// Raised when every reroll attempt of one generation failed the
/// playability gates (T-140 不可玩生成=重摇, 预算耗尽=失败诚实抛出).
/// Carries the attempt count so callers see how deep the chain went.
/// </summary>
public sealed class ProceduralGenerationException : Exception
{
    /// <summary>Attempts drawn before giving up (attempt 0 = first try).</summary>
    public int Attempts { get; }

    public ProceduralGenerationException(int attempts, string message)
        : base(message) => Attempts = attempts;
}

/// <summary>
/// Procedural level generator (T-140, F-007): one request → one level
/// isomorphic to a handcrafted row. Determinism chain — the run seed
/// derives per-attempt child seeds (<see cref="ProceduralSeed.Derive"/>
/// FNV-1a; attempt counter inside, so 重摇次数入哈希); the layout draw
/// (size/ratios/entry count/side) consumes an injected rng over the
/// layout seed, the terrain is the production
/// <see cref="GridWorld"/> draw over the terrain seed — the SAME primitive
/// and draw order the loading pipeline replays later, so generator-side
/// buildability checks hold downstream verbatim (同 seed 同图: two calls
/// with the same request and factory emit byte-equal JSON and equal
/// terrain hashes).
/// <para>
/// Playability gates per attempt (T-140 可玩性保障): ①出生点→基地可达性 —
/// every entry cell must reach the base on walkable terrain through a real
/// <see cref="FlowField"/> flood (the C-11 reachability primitive, reuse);
/// ②建筑位充足 — the drawn grid must offer enough buildable cells for the
/// whole plan (ring around the base ordered by flow distance — the kill
/// zone sits where enemies converge); ③出生点不落阻隔格 and distinct.
/// A failed attempt rerolls deterministically (child seed of the next
/// attempt); exhausting <see cref="ProceduralConstraints.MaxRerolls"/>
/// throws <see cref="ProceduralGenerationException"/> — never a silently
/// degraded map.
/// </para>
/// </summary>
public static class ProceduralLevelGenerator
{
    private enum Edge
    {
        Top = 0,
        Left = 1,
        Right = 2,
        Bottom = 3,
    }

    private const int EdgeCount = 4; // structural: the four grid edges an entry line may sit on

    /// <summary>
    /// Chebyshev spacing between two picked building cells (sparse packing —
    /// see the placement gate; [NEW-T-140] lane-routeability structural value,
    /// mirrors the e1-final handcrafted ring's ~2-cell pitch).
    /// </summary>
    private const int SparseSpacingCells = 2; // ci-allow: R7 T-140 [NEW-T-140] 稀疏间距结构值（车道可路由性——e1-final 手工环 ~2 格节距先例；非平衡数值）

    private static int Chebyshev(CellCoord left, CellCoord right) =>
        Math.Max(Math.Abs(left.X - right.X), Math.Abs(left.Y - right.Y));

    /// <summary>
    /// Generates one level. <paramref name="rngFactory"/> builds the
    /// per-attempt seeded rng streams (the same implementation the loading
    /// pipeline will use to replay the terrain — e.g. the SplitMix64 test
    /// fake or the host PCG64 adapter), keeping the generator itself free
    /// of any rng implementation (CS-1.4).
    /// </summary>
    /// <exception cref="ArgumentNullException">request or rngFactory is null.</exception>
    /// <exception cref="ArgumentException">request.Validate rejects the input.</exception>
    /// <exception cref="ProceduralGenerationException">
    /// Every attempt (first + MaxRerolls) failed a playability gate.
    /// </exception>
    public static ProceduralLevel Generate(
        ProceduralRequest request,
        Func<long, IRng> rngFactory)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rngFactory);
        request.Validate();

        ProceduralTier tier = request.Tier;
        ProceduralConstraints bounds = request.Constraints;
        int attempts = bounds.MaxRerolls + 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ProceduralLevel? level = TryAttempt(request, rngFactory, attempt);
            if (level is not null)
            {
                return level;
            }
        }

        throw new ProceduralGenerationException(
            attempts,
            $"seed={request.Seed} tier={tier.Id}: {attempts} 次尝试全部未过可玩性闸（可达性/建筑位/出生点），重摇预算耗尽");
    }

    // ---- one attempt --------------------------------------------------------

    private static ProceduralLevel? TryAttempt(
        ProceduralRequest request,
        Func<long, IRng> rngFactory,
        int attempt)
    {
        ProceduralTier tier = request.Tier;
        ProceduralConstraints bounds = request.Constraints;

        // Layout draw (sizes/ratios/entry count/side) over its own derived stream.
        IRng layout = rngFactory(ProceduralSeed.Derive(request.Seed, "layout", attempt));
        int width = layout.Next(bounds.WidthMin, bounds.WidthMax + 1);
        int height = layout.Next(bounds.HeightMin, bounds.HeightMax + 1);
        double forest = BandDraw(layout, bounds.ForestRatioMin, bounds.ForestRatioMax);
        double rock = BandDraw(layout, bounds.RockRatioMin, bounds.RockRatioMax);
        double water = BandDraw(layout, bounds.WaterRatioMin, bounds.WaterRatioMax);
        int entryCount = layout.Next(bounds.EntryCountMin, bounds.EntryCountMax + 1);
        var entryEdge = (Edge)layout.Next(0, EdgeCount);
        double grace = BandDraw(layout, bounds.GraceMin, bounds.GraceMax);

        // Terrain = the production GridWorld draw over the terrain child seed —
        // bit-equal to the pipeline's replay of the emitted row.
        int mapSeed = ProceduralSeed.ToMapSeed(ProceduralSeed.Derive(request.Seed, "terrain", attempt));
        var world = new GridWorld(
            width, height, rngFactory(mapSeed), forest, rock, water);

        // Base anchor: centre cell of the edge OPPOSITE the entry line, walked
        // toward the map centre until passable (deterministic walk).
        CellCoord baseCell = FindBaseCell(world, Opposite(entryEdge));
        var entries = PickEntries(world, entryEdge, baseCell, entryCount, layout);
        if (entries.Count < bounds.EntryCountMin)
        {
            return null; // not enough distinct passable edge cells → reroll
        }

        // Gate ①: 出生点→基地可达性 — real flow field over the drawn terrain.
        FlowField flow = FlowField.BuildComplete(world, baseCell);
        if (entries.Any(entry => !flow.IsReachable(entry)))
        {
            return null;
        }

        int economyCells = tier.LaborCamps + tier.Harvesters + tier.Converters;
        int warehouseCells = 1;
        // Gate ②: 建筑位充足 — buildable cells (passable, unoccupied by the level
        // furniture) ordered by flow distance ascending = kill ring nearest the
        // base along the enemy approach, then the economy band behind it.
        // SPARSE packing: every picked cell keeps chebyshev ≥2 from every other
        // picked cell — porter lanes route between the first FREE neighbours of
        // their endpoints (Simulation.TryRoute), so a densely packed blob would
        // leave buildings without a free neighbour and the lanes would be
        // rejected (no-route) — starving the towers of ammo. Sparse towers also
        // leave gaps the enemy flow threads through (the e1-final 手工 ring
        // shape), with range 4~5 still covering the approach.
        var reserved = new HashSet<CellCoord> { baseCell };
        reserved.UnionWith(entries);
        List<(CellCoord Cell, long Distance)> buildable = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var cell = new CellCoord(x, y);
                if (reserved.Contains(cell) || !world.IsPassable(cell))
                {
                    continue;
                }

                long distance = flow.Distance(cell);
                if (distance == FlowField.Unreached)
                {
                    continue; // sealed pocket — no tower value, no build value
                }

                buildable.Add((cell, distance));
            }
        }

        buildable.Sort(static (left, right) => left.Distance != right.Distance
            ? left.Distance.CompareTo(right.Distance)
            : left.Cell.X != right.Cell.X
                ? left.Cell.X.CompareTo(right.Cell.X)
                : left.Cell.Y.CompareTo(right.Cell.Y));

        var picked = new List<CellCoord>();
        foreach ((CellCoord cell, _) in buildable)
        {
            if (picked.Count >= tier.TotalTowerCount + economyCells + warehouseCells)
            {
                break;
            }

            if (picked.All(other => Chebyshev(cell, other) >= SparseSpacingCells))
            {
                picked.Add(cell);
            }
        }

        if (picked.Count < tier.TotalTowerCount + economyCells + warehouseCells)
        {
            return null;
        }

        // Tower ring first (closest to the base along the approach), economy
        // chain next, warehouse stand behind it.
        var cursor = 0;
        List<CellCoord> towerCells = Take(picked, ref cursor, tier.TotalTowerCount);
        List<CellCoord> economyChain = Take(picked, ref cursor, economyCells);
        CellCoord warehouse = picked[cursor];

        ProceduralScriptPlan plan = PlanScript(tier, request.Era, towerCells, economyChain);
        double totalTowerCost = (tier.ArrowTowerCount * request.Era.TowerCost(tier.ArrowTowerId)!.Value)
            + (tier.RockTowerCount * request.Era.TowerCost(tier.RockTowerId)!.Value);
        double grant = Math.Ceiling(totalTowerCost * tier.StockMultiplier);

        return new ProceduralLevel(
            Id: LevelId(request, mapSeed),
            Era: request.Era.EraId,
            Slot: tier.Slot,
            NameZh: $"{tier.NameZh}·{mapSeed:X}",
            NameEn: $"{tier.NameEn} #{mapSeed:X}",
            Threat: tier.Threat,
            WinWaves: tier.WinWaves,
            GraceMultiplier: grace,
            BudgetSeconds: tier.BudgetSeconds,
            AdjacentEnemyCamps: tier.AdjacentEnemyCamps,
            Map: new ProceduralMap(
                mapSeed, width, height, forest, rock, water, baseCell, entries, warehouse),
            InitialStock: new[] { new ProceduralStockLine(request.Era.CurrencyResource, grant) },
            Waves: new ProceduralWaveTable(
                tier.WaveBaseCount, tier.WaveBeginWave, tier.WaveSpacing, tier.WaveScaling,
                tier.BatchesPerWave, tier.ValleySeconds, request.Era.RegularRoster,
                EliteLineEnabled: false, EliteEnemyId: null,
                BossWave: tier.BossLineEnabled ? tier.WinWaves : null,
                BossEnemyId: tier.BossEnemyId),
            CommandScript: plan.Steps,
            Rerolls: attempt,
            TerrainHash: world.ComputeMapHash())
        {
            StrategyObjectiveIds = tier.StrategyObjectives,
            ChallengeObjectiveIds = tier.ChallengeObjectives,
        };
    }

    /// <summary>
    /// Auto-build plan (the 命令流脚本 of the generated row, wave-anchored
    /// like the T-085 teaching scripts): wave 1 = the full economy chain +
    /// the wave-one tower tranche + every lane; later tranches every
    /// TowerWaveStep waves. Entity ordinals follow the Simulation
    /// convention (warehouse=1, later builds 2..N in script order) — the
    /// loader's assign-carrier reference check holds by construction.
    /// Arrow towers are fed by the converters round-robin; rock towers run
    /// un-fed (the E1 era has no stone recipe — e1-final 手工先例).
    /// </summary>
    private sealed record ProceduralScriptPlan(IReadOnlyList<ProceduralScriptStep> Steps);

    private static ProceduralScriptPlan PlanScript(
        ProceduralTier tier,
        ProceduralEraContent era,
        List<CellCoord> towerCells,
        List<CellCoord> economyChain)
    {
        var steps = new List<ProceduralScriptStep>();
        // Ordinal convention: warehouse occupies 1; every later build is 2..N
        // in issue order (Simulation convention, T-085 loader mirrors it).
        var builds = new List<(long Ordinal, string Kind, CellCoord Cell)>();
        long NextOrdinal() => builds.Count + 2;

        // ---- wave 1: economy chain ----------------------------------------
        int economyCursor = 0;
        for (int i = 0; i < tier.LaborCamps; i++)
        {
            builds.Add((NextOrdinal(), "camp", economyChain[economyCursor++]));
        }

        for (int i = 0; i < tier.Harvesters; i++)
        {
            builds.Add((NextOrdinal(), "harvester", economyChain[economyCursor++]));
        }

        long firstConverterOrdinal = NextOrdinal();
        for (int i = 0; i < tier.Converters; i++)
        {
            builds.Add((NextOrdinal(), "converter", economyChain[economyCursor++]));
        }

        long firstTowerOrdinal = NextOrdinal();
        // Tower order: arrow line first, then the rock line (handcrafted ladder shape).
        var towerOrder = new List<(string TowerId, bool Fed)>();
        for (int i = 0; i < tier.ArrowTowerCount; i++)
        {
            towerOrder.Add((tier.ArrowTowerId, true));
        }

        for (int i = 0; i < tier.RockTowerCount; i++)
        {
            towerOrder.Add((tier.RockTowerId, false));
        }

        int waveOne = Math.Min(tier.WaveOneTowerCount, towerOrder.Count);
        for (int i = 0; i < waveOne; i++)
        {
            builds.Add((NextOrdinal(), "tower:" + towerOrder[i].TowerId, towerCells[i]));
        }

        // Emit the wave-1 build steps in issue order, then the lanes.
        foreach ((_, string kind, CellCoord cell) in builds)
        {
            steps.Add(BuildStep(kind, cell, era, wave: 1));
        }

        for (int i = 0; i < tier.Harvesters; i++)
        {
            long harvester = 1 + tier.LaborCamps + 1 + i;
            long converter = firstConverterOrdinal + (i % tier.Converters);
            for (int p = 0; p < tier.HarvesterLanePorters; p++)
            {
                steps.Add(new ProceduralScriptStep(
                    1, "assign-carrier", null, null, null, null,
                    harvester, converter, era.CarrierTier));
            }
        }

        for (int i = 0; i < waveOne; i++)
        {
            if (!towerOrder[i].Fed)
            {
                continue;
            }

            long tower = firstTowerOrdinal + i;
            long converter = firstConverterOrdinal + (i % tier.Converters);
            steps.Add(new ProceduralScriptStep(
                1, "assign-carrier", null, null, null, null, converter, tower, era.CarrierTier));
        }

        // ---- later tranches -------------------------------------------------
        int placed = waveOne;
        int wave = 1 + tier.TowerWaveStep;
        while (placed < towerOrder.Count)
        {
            long tower = firstTowerOrdinal + placed;
            steps.Add(BuildStep("tower:" + towerOrder[placed].TowerId, towerCells[placed], era, wave));
            if (towerOrder[placed].Fed)
            {
                long converter = firstConverterOrdinal + (placed % tier.Converters);
                steps.Add(new ProceduralScriptStep(
                    wave, "assign-carrier", null, null, null, null, converter, tower, era.CarrierTier));
            }

            placed++;
            wave = Math.Min(wave + tier.TowerWaveStep, tier.WinWaves);
        }

        return new ProceduralScriptPlan(steps);
    }

    private static ProceduralScriptStep BuildStep(
        string kind,
        CellCoord cell,
        ProceduralEraContent era,
        int wave) => kind switch
    {
        "camp" => new ProceduralScriptStep(wave, "build-labor-camp", cell, null, null, null, null, null, null),
        "harvester" => new ProceduralScriptStep(
            wave, "build-harvester", cell, null, era.CurrencyResource, 1, null, null, null),
        "converter" => new ProceduralScriptStep(wave, "build-converter", cell, null, null, null, null, null, null),
        _ => new ProceduralScriptStep(
            wave, "build-tower", cell, kind["tower:".Length..], null, null, null, null, null),
    };

    // ---- placement helpers ---------------------------------------------------

    private static CellCoord FindBaseCell(GridWorld world, Edge anchorEdge)
    {
        int width = world.Width;
        int height = world.Height;
        int centreX = width >> 1;
        int centreY = height >> 1;
        var start = anchorEdge switch
        {
            Edge.Top => new CellCoord(centreX, 0),
            Edge.Bottom => new CellCoord(centreX, height - 1),
            Edge.Left => new CellCoord(0, centreY),
            _ => new CellCoord(width - 1, centreY),
        };

        // Deterministic walk toward the map centre, then a row-major sweep —
        // the first passable cell wins; the walk stays inside the grid.
        int dx = Math.Sign(centreX - start.X);
        int dy = Math.Sign(centreY - start.Y);
        var probe = start;
        while (world.Contains(probe))
        {
            if (world.IsPassable(probe))
            {
                return probe;
            }

            probe = new CellCoord(probe.X + dx, probe.Y + dy);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var cell = new CellCoord(x, y);
                if (world.IsPassable(cell))
                {
                    return cell;
                }
            }
        }

        throw new InvalidOperationException("GridWorld with sum-of-ratios ≤ 1 always has a passable cell.");
    }

    private static IReadOnlyList<CellCoord> PickEntries(
        GridWorld world,
        Edge edge,
        CellCoord baseCell,
        int count,
        IRng layout)
    {
        // Candidate line: the outermost ring of the chosen edge minus corners.
        List<CellCoord> line = new();
        int width = world.Width;
        int height = world.Height;
        int lastX = width - 1;
        int lastY = height - 1;
        for (int x = 1; x < lastX; x++)
        {
            int y = edge == Edge.Top ? 0 : lastY;
            if (edge is Edge.Top or Edge.Bottom)
            {
                line.Add(new CellCoord(x, y));
            }
        }

        for (int y = 1; y < lastY; y++)
        {
            int x = edge == Edge.Left ? 0 : lastX;
            if (edge is Edge.Left or Edge.Right)
            {
                line.Add(new CellCoord(x, y));
            }
        }

        // Passable, not the base, distinct — evenly spread with a jitter draw.
        List<CellCoord> usable = line.Where(c => world.IsPassable(c) && c != baseCell).ToList();
        if (usable.Count < count)
        {
            return Array.Empty<CellCoord>();
        }

        List<CellCoord> picked = new();
        int stride = usable.Count / count;
        int offset = stride > 1 ? layout.Next(0, stride) : 0;
        for (int i = 0; i < count; i++)
        {
            picked.Add(usable[Math.Min(usable.Count - 1, offset + (i * stride))]);
        }

        return picked.Distinct().ToList();
    }

    private static List<CellCoord> Take(List<CellCoord> source, ref int cursor, int count)
    {
        var slice = new List<CellCoord>(count);
        for (int i = 0; i < count; i++)
        {
            slice.Add(source[cursor++]);
        }

        return slice;
    }

    private static double BandDraw(IRng rng, double min, double max) =>
        min + (rng.NextDouble() * (max - min));

    private static Edge Opposite(Edge edge) => edge switch
    {
        Edge.Top => Edge.Bottom,
        Edge.Bottom => Edge.Top,
        Edge.Left => Edge.Right,
        _ => Edge.Left,
    };

    private static string LevelId(ProceduralRequest request, int mapSeed)
    {
        string eraTag = request.Era.EraId.Split(':')[0];
        return $"procedural:level-{eraTag}-{mapSeed:X8}";
    }
}
