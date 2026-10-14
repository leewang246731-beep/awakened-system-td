using System.Globalization;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Sim;
using EraCore.Tests.Sim;
using EraCore.World;

namespace EraCore.Tests.Combat;

/// <summary>
/// Shared loader of the real era2 tower table for the T-065 combat tests
/// (CS-2.3/RL7: engine numbers come from the registry JSON). Same
/// repository-relative probing discipline as <see cref="CombatContent"/>
/// (walks up from the test assembly to <c>game/content/era2</c>, never a
/// drive letter — RL9). Results are cached: the records are immutable.
/// </summary>
internal static class Era2Content
{
    private static readonly Lazy<IReadOnlyList<TowerSpec>> TowersCache = new(LoadTowersCore);

    /// <summary>All five E2 tower definitions from game/content/era2/towers.json.</summary>
    public static IReadOnlyList<TowerSpec> Towers => TowersCache.Value;

    /// <summary>One tower by content id; fails the test if the id is unknown.</summary>
    public static TowerSpec Tower(string id) =>
        Towers.FirstOrDefault(t => t.Id == id)
        ?? throw new InvalidOperationException($"Tower '{id}' not present in era2 towers.json.");

    public static TowerSpec Arrow => Tower("era2:tower-arrow");

    public static TowerSpec Crossbow => Tower("era2:tower-crossbow");

    public static TowerSpec Repeater => Tower("era2:tower-repeating-crossbow");

    public static TowerSpec Onager => Tower("era2:tower-onager");

    public static TowerSpec FireOil => Tower("era2:tower-fire-oil");

    /// <summary>
    /// Deserializes a synthetic era2-shaped collection through the real
    /// loader (RL7 — broken samples enter through content JSON too).
    /// </summary>
    public static IReadOnlyList<TowerSpec> Load(string json) => TowerSpec.LoadCollection(json);

    /// <summary>
    /// A minimal E2-shaped pair collection with line wiring, for upgrade-law
    /// violation samples: the same JSON pipeline as the real table.
    /// </summary>
    public static string PairLineJson(
        string clsLow,
        string clsHigh,
        double dpsLow,
        double dpsHigh,
        int costLow,
        int costHigh,
        double rangeLow,
        double rangeHigh,
        int tierLow = 1,
        int tierHigh = 2) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        {
          "content_type": "Building",
          "items": [
            {"id":"era2:tower-pair-low","class":"{{clsLow}}","tier":{{tierLow}},"max_tier":4,
             "upgrades_to":"era2:tower-pair-high","cost_total":{{costLow}},"dps":{{dpsLow}},
             "damage_per_shot":{{dpsLow}},"reload_ticks":30,"range_cells":{{rangeLow}},"hp":100,
             "ammo_item":null,"ammo_per_shot":1,
             "tier_growth":{"dps_multiplier_per_tier":2.0,"cost_multiplier_per_tier":3.0,"range_multiplier_per_tier":1.25 } },
            {"id":"era2:tower-pair-high","class":"{{clsHigh}}","tier":{{tierHigh}},"max_tier":4,
             "cost_total":{{costHigh}},"dps":{{dpsHigh}},"damage_per_shot":{{dpsHigh}},"reload_ticks":30,
             "range_cells":{{rangeHigh}},"hp":100,"ammo_item":null,"ammo_per_shot":1,
             "tier_growth":{"dps_multiplier_per_tier":2.0,"cost_multiplier_per_tier":3.0,"range_multiplier_per_tier":1.25 } }
          ]
        }
        """);

    private static IReadOnlyList<TowerSpec> LoadTowersCore()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era2");
            if (Directory.Exists(probe))
            {
                var path = Path.Combine(probe, "towers.json");
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException("game/content/era2/towers.json is missing.");
                }

                return TowerSpec.LoadCollection(File.ReadAllText(path));
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "game/content/era2 not found (tests must run inside the repository).");
    }
}

/// <summary>
/// Combat estate fixture for the T-065 tests: a real
/// <see cref="BuildingRegistry"/> over a plain generated world plus a real
/// <see cref="ResourceStock"/> pool and the real era2 catalog (CS-3.4: the
/// system under test is never mocked). The ruin repair fraction wires the
/// Checkpoint authority (<c>FailureTiers.RepairCostFraction</c>) exactly the
/// way the composition root will.
/// </summary>
internal sealed class Era2EstateFixture
{
    /// <summary>
    /// Seed deposit in cost-currency points — exactly the E2 pool cap
    /// (2000×仓库等级 2=4000, v3.2 附录 F.3；满额入池合法「入池只计上限内」)。
    /// Covers the densest fixture (箭楼 90+火油 2430+弩炮 700=3220) and the
    /// full upgrade chain (净 −1755) while refunds stay under the cap.
    /// </summary>
    public const double SeedStock = 4000.0;

    private Era2EstateFixture(BuildingRegistry buildings, ResourceStock stock, TowerEstateOps estate)
    {
        Buildings = buildings;
        Stock = stock;
        Estate = estate;
    }

    public BuildingRegistry Buildings { get; }

    public ResourceStock Stock { get; }

    public TowerEstateOps Estate { get; }

    /// <summary>The cost currency key of the fixture pool (E2 costs are iron+wood totals).</summary>
    public static ResourceKey Currency { get; } = new("era2:res-iron");

    public static Era2EstateFixture Create(double seedStock = SeedStock)
    {
        var world = new GridWorld(24, 24, new SimRng(20261013), forestRatio: 0, rockRatio: 0, waterRatio: 0);
        var buildings = new BuildingRegistry(world);
        buildings.AttachToWorld();
        var stock = new ResourceStock(new SimClock(), warehouseLevel: 2);
        _ = stock.Deposit(Currency, seedStock);
        var estate = new TowerEstateOps(
            buildings,
            Era2Content.Towers,
            stock,
            Currency,
            EraCore.Checkpoint.FailureTiers.RepairCostFraction);
        return new Era2EstateFixture(buildings, stock, estate);
    }
}

/// <summary>
/// K-3 payload builders for the T-065 registry tests — payloads are built
/// here so test method bodies never carry literal JSON braces (T4 scanner
/// discipline, same convention as ActionJsonSamples).
/// </summary>
internal static class EstateActionJson
{
    public static string UpgradeTower(long targetId, int toTier) =>
        "{\"action\":\"UpgradeTower\",\"target_id\":" + targetId.ToString(CultureInfo.InvariantCulture)
        + ",\"to_tier\":" + toTier.ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>confirmSell null = the danger bit is absent from the payload.</summary>
    public static string SellTower(long targetId, string? confirmSell = "true") =>
        confirmSell is null
            ? "{\"action\":\"SellTower\",\"target_id\":" + targetId.ToString(CultureInfo.InvariantCulture) + "}"
            : "{\"action\":\"SellTower\",\"target_id\":" + targetId.ToString(CultureInfo.InvariantCulture)
              + ",\"confirm_sell\":" + confirmSell + "}";
}
