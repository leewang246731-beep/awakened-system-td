using Xunit;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Economy;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Progression;
using EraCore.Sim;
using EraCore.Tests.World;

namespace EraCore.Tests.Legacy;

/// <summary>
/// Shared fixture of the C-20 legacy test suite (T-074): repo-relative
/// discovery of <c>game/content/legacy/</c>, one-call loading of the five
/// content tables, a deterministic relic-city fact generator (seeded
/// <see cref="LcgRng"/>) and the counting/throwing save-port wrapper the
/// transaction tests inject. No test may embed a workstation path (CS-4.1).
/// </summary>
internal static class LegacyFixture
{
    /// <summary>Test seed family (same value family as the campaign fixtures).</summary>
    public const long Seed = 20261074;

    /// <summary>The E1 core anchor of the fixture city (center of the E2 map too).</summary>
    public static readonly CellCoord Anchor = new(48, 48);

    /// <summary>Finds game/content/legacy from the test assembly location (RL9: repo-relative).</summary>
    public static string FindLegacyDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "legacy");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/legacy（测试须在仓库内运行）");
    }

    /// <summary>
    /// One era content dir under game/content (e.g. "era3") — the cross-era
    /// policy tests read the real era.json documents through it (T-115).
    /// </summary>
    public static string EraDir(string era)
    {
        string contentRoot = Directory.GetParent(FindLegacyDir())!.FullName;
        string dir = Path.Combine(contentRoot, era);
        return Directory.Exists(dir)
            ? dir
            : throw new InvalidOperationException($"未找到 game/content/{era}（测试须在仓库内运行）");
    }

    private static string LoadText(string fileName) =>
        File.ReadAllText(Path.Combine(FindLegacyDir(), fileName));

    /// <summary>The real relic-mapping parameter table.</summary>
    public static RelicMappingParams Mapping() =>
        LegacyDataLoader.ParseRelicMapping(LoadText("relic_mapping.json"));

    /// <summary>The real wonder table.</summary>
    public static IReadOnlyList<WonderSpec> Wonders() =>
        LegacyDataLoader.ParseWonders(LoadText("wonders.json"));

    /// <summary>The real archaeology table.</summary>
    public static ArchaeologyTable Archaeology() =>
        LegacyDataLoader.ParseArchaeology(LoadText("archaeology.json"));

    /// <summary>The real heritage-shop shelf.</summary>
    public static HeritageShopTable Shop() =>
        LegacyDataLoader.ParseHeritageShop(LoadText("heritage_shop.json"));

    /// <summary>The real era-transition parameter table.</summary>
    public static EraTransitionSpec Transition() =>
        LegacyDataLoader.ParseEraTransition(LoadText("era_transition.json"));

    /// <summary>E1 wonder spec of the real table.</summary>
    public static WonderSpec E1Wonder() =>
        Wonders().Single(wonder => wonder.EraIndex == 1);

    /// <summary>A campaign pool at E1 warehouse level with known whole-item amounts.</summary>
    public static ResourceStock Pool(int wood, int stone, int food)
    {
        var pool = new ResourceStock(new SimClock(), ResourceStock.E1WarehouseLevel);
        _ = pool.Deposit(new ResourceKey("era1:res-wood"), wood);
        _ = pool.Deposit(new ResourceKey("era1:res-stone"), stone);
        _ = pool.Deposit(new ResourceKey("era1:res-food"), food);
        return pool;
    }

    /// <summary>A one-node tech tree of the given era with no gates beyond it.</summary>
    public static TechTree Tree(int eraIndex, params string[] capturedLevels)
    {
        var node = new TechNodeSpec(
            $"test:tech-e{eraIndex}-solo",
            eraIndex,
            TechBranch.Main,
            TechNodeKind.TowerUnlock,
            Prerequisites: Array.Empty<string>(),
            Materials: Array.Empty<TechMaterial>(),
            BoundLevelIds: Array.Empty<string>(),
            CostSum: 10,
            CostMix: new[] { new TechCostLine(new ResourceKey("era1:res-wood"), 1.0) },
            Unlocks: Array.Empty<string>());
        var tree = new TechTree(new[] { node }, Pool(0, 0, 0), eraIndex);
        foreach (string level in capturedLevels)
        {
            tree.RegisterCapture(level);
        }

        return tree;
    }

    /// <summary>
    /// Deterministic relic-city building facts: <paramref name="count"/>
    /// buildings scattered inside the Chebyshev <paramref name="radius"/>
    /// of <paramref name="anchor"/> on a plain synthetic grid (tiers drawn
    /// 1..4, facings from the content orientation set).
    /// </summary>
    public static List<RelicCityBuildingFact> CityFacts(
        long citySeed, int count, int radius, IReadOnlyList<int> orientations)
    {
        var rng = new LcgRng(citySeed);
        var taken = new HashSet<CellCoord>();
        var facts = new List<RelicCityBuildingFact>(count);
        while (facts.Count < count)
        {
            var cell = new CellCoord(
                Anchor.X + rng.Next(-radius, radius + 1),
                Anchor.Y + rng.Next(-radius, radius + 1));
            if (!taken.Add(cell))
            {
                continue;
            }

            facts.Add(new RelicCityBuildingFact(
                100_000 + facts.Count,
                $"era1:tower-{facts.Count % 4}",
                rng.Next(1, 5),
                orientations[rng.Next(0, orientations.Count)],
                cell));
        }

        return facts;
    }

    /// <summary>
    /// Deterministic hostile-placement relic-city facts (T-125): one row of
    /// buildings at Chebyshev distance 6 from the anchor — OUTSIDE the F.7
    /// shift radius (2), so on an all-water continent every building
    /// degrades (ruin), none shifts onto the single plain cell a radius-0
    /// yield pass leaves at the anchor. No rng: fixed cells, stable across
    /// every seed.
    /// </summary>
    public static List<RelicCityBuildingFact> HostileCityFacts(int count, IReadOnlyList<int> orientations)
    {
        var facts = new List<RelicCityBuildingFact>(count);
        for (int i = 0; i < count; i++)
        {
            facts.Add(new RelicCityBuildingFact(
                500_000 + i,
                $"era1:tower-{i % 4}",
                1 + (i % 4),
                orientations[i % orientations.Count],
                new CellCoord(Anchor.X - 6 + (i % 12), Anchor.Y + 6)));
        }

        return facts;
    }

    /// <summary>A minimal previous campaign document of the given era.</summary>
    public static CampaignMetaDto EmptyMeta(long worldSeed, int eraIndex) =>
        new(worldSeed, eraIndex, new Dictionary<string, long>(), new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>());

    /// <summary>
    /// The standard success-shaped transition request over the given live
    /// objects (wonder built, final level captured, wood/stone useful,
    /// food folds).
    /// </summary>
    public static EraTransitionRequest Request(
        ResourceStock oldPool,
        TechTree tech,
        LegacyLedger ledger,
        IReadOnlyList<RelicCityBuildingFact> city,
        ISavePort port,
        ICampaignMeta previousMeta,
        Action<EraTransitionStep>? probe = null)
    {
        return new EraTransitionRequest(
            Seed,
            FromEraIndex: 1,
            oldPool,
            tech,
            ledger,
            city,
            Anchor,
            new HashSet<ResourceKey> { new("era1:res-wood"), new("era1:res-stone") },
            new SimClock(),
            new LcgRng(Seed + 7),
            port,
            previousMeta,
            Mapping(),
            Transition(),
            Wonders(),
            probe);
    }
}

/// <summary>
/// Save-port wrapper of the transaction tests: counts campaign writes and
/// can be told to fail the write (the step-8 fault injection). Delegates
/// everything else to the real port (no mock of the system under test,
/// CS-3.4 — the port is the cross-layer seam).
/// </summary>
internal sealed class CountingSavePort : ISavePort
{
    private readonly ISavePort _inner;

    public CountingSavePort(ISavePort inner)
    {
        _inner = inner;
    }

    public int CampaignWrites { get; set; }

    public bool ThrowOnWrite { get; set; }

    public void WriteCampaign(ICampaignMeta meta, int formatVersion)
    {
        if (ThrowOnWrite)
        {
            throw new IOException("injected campaign-write failure");
        }

        CampaignWrites++;
        _inner.WriteCampaign(meta, formatVersion);
    }

    public ICampaignMeta ReadCampaign(int formatVersion) => _inner.ReadCampaign(formatVersion);

    public void WriteLevel(string levelSlotId, ILevelSaveBlob blob, int formatVersion) =>
        _inner.WriteLevel(levelSlotId, blob, formatVersion);

    public ILevelSaveBlob ReadLevel(string levelSlotId, int formatVersion) => _inner.ReadLevel(levelSlotId, formatVersion);
}
