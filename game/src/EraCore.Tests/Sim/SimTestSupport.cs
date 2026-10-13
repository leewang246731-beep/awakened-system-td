using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Sim;

// T-040 (C-12) 测试基建：K-1 端口 Fake（IRng——CS-3.4 白名单）、仓库相对
// 寻址的 era1 内容装载（RL9，同 UnitsContent 模式）、E1 关卡夹具（数值全部
// 来自 content JSON 或已冻结锚点，CS-2.3/CS-4.4）与最小命令流的载荷构造器。

/// <summary>
/// SplitMix64 seeded stream (K-1 port fake): same seed + same call sequence
/// ⇒ same draws. Identical algorithm to the Units test fake so cross-module
/// fixtures stay comparable.
/// </summary>
internal sealed class SimRng : IRng
{
    private ulong _state;

    public SimRng(long seed)
    {
        Reseed(seed);
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentException($"minValue ({minValue}) must be below maxValue ({maxValue}).");
        }

        long span = maxValue - (long)minValue;
        return minValue + (int)(NextDouble() * span);
    }

    public double NextDouble()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }

    public void Reseed(long seed) => _state = (ulong)seed;
}

/// <summary>Repo-relative loaders for era1 content (RL9: walk up, no absolute paths).</summary>
internal static class SimEra1Content
{
    private static string FindEra1Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era1");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/era1（测试须在仓库内运行）");
    }

    /// <summary>Parses the real towers.json into the production TowerSpec list.</summary>
    public static IReadOnlyList<TowerSpec> LoadTowerSpecs()
    {
        string path = Path.Combine(FindEra1Dir(), "towers.json");
        Assert.True(File.Exists(path), "内容文件缺失：towers.json");
        var specs = TowerSpec.LoadCollection(File.ReadAllText(path));
        Assert.True(specs.Count > 0, "towers.json items 为空");
        return specs;
    }

    /// <summary>Parses the real enemies.json into the production catalog.</summary>
    public static EnemySpecCatalog LoadEnemyCatalog()
    {
        string path = Path.Combine(FindEra1Dir(), "enemies.json");
        Assert.True(File.Exists(path), "内容文件缺失：enemies.json");
        return EnemySpecCatalog.Parse(File.ReadAllText(path));
    }

    /// <summary>Wood-arrow tower spec straight out of towers.json (cost/HP/dps anchors).</summary>
    public static TowerSpec WoodArrowTower()
    {
        foreach (TowerSpec spec in LoadTowerSpecs())
        {
            if (spec.Id == "era1:tower-wood-arrow")
            {
                return spec;
            }
        }

        throw new InvalidOperationException("towers.json 缺 era1:tower-wood-arrow");
    }
}

/// <summary>
/// E1 level fixture (T-040): a 16×24 map with water disabled so the fixed
/// command-stream coordinates stay buildable (terrain draws still vary by
/// seed — forest layout), one attack lane, entry cells on the top edge and
/// the base on the bottom edge. Every number traces to content or a frozen
/// anchor: wave knobs mirror the UnitsDeterminismTests fixture (F-030 band,
/// t=1.2), the grant of 100 wood covers two 30-point wood-arrow towers
/// (towers.json cost_total) with slack for the rejection-path test.
/// </summary>
internal static class SimE1Fixture
{
    public const int Width = 16;
    public const int Height = 24;
    public const long Seed = 20261014; // same seed family as the economy tests

    public static readonly ResourceKey Wood = new("era1:res-wood");

    // Fixed placement plan (all coordinates inside the grid, water ratio 0
    // ⇒ every cell terrain-buildable). Entity ids issued by the Simulation:
    // warehouse=1, labor camp=2, harvester=3, converter=4, tower=5,
    // harvester2=6, tower2=7.
    public static readonly CellCoord WarehouseCell = new(2, 14);
    public static readonly CellCoord LaborCampCell = new(4, 20);
    public static readonly CellCoord HarvesterCell = new(6, 18);
    public static readonly CellCoord ConverterCell = new(9, 18);
    public static readonly CellCoord TowerCell = new(6, 21);
    public static readonly CellCoord Harvester2Cell = new(4, 16);
    public static readonly CellCoord Tower2Cell = new(9, 21);

    /// <summary>
    /// The canonical E1 milestone command stream: build the manpower chain,
    /// the ammo chain, the pool-delivery chain and two towers — ten
    /// commands exercising all five E1 thin verbs (both towers get an
    /// arrow porter so the combat leg of the integration run is fed).
    /// </summary>
    public static IReadOnlyList<SimCommand> MilestoneCommands() => new[]
    {
        SimCommands.BuildLaborCamp(60, LaborCampCell),
        SimCommands.BuildHarvester(120, HarvesterCell, Wood, seats: 1),
        SimCommands.BuildConverter(180, ConverterCell),
        SimCommands.BuildTower(240, "era1:tower-wood-arrow", TowerCell),
        SimCommands.AssignCarrier(300, from: 3, to: 4),          // harvester → converter (wood)
        SimCommands.AssignCarrier(360, from: 4, to: 5),          // converter → tower1 (arrows)
        SimCommands.BuildHarvester(420, Harvester2Cell, Wood, seats: 1),
        SimCommands.AssignCarrier(480, from: 6, to: 1),          // harvester2 → warehouse (pool growth)
        SimCommands.BuildTower(540, "era1:tower-wood-arrow", Tower2Cell),
        SimCommands.AssignCarrier(600, from: 4, to: 7),          // converter → tower2 (arrows)
    };

    /// <summary>
    /// Builds the fixture config. Ratios/tick knobs come from GridWorld
    /// defaults and K-1; wave knobs mirror the Units determinism fixture
    /// (t=1.2 ⇒ interval 72 s, count multiplier 2.0, hp multiplier 1.5).
    /// <paramref name="warehouseOverride"/> replaces the default warehouse
    /// cell when non-null; a warehouse-less level is expressed by
    /// <c>Config() with &#123; WarehouseCell = null &#125;</c>.
    /// </summary>
    public static SimulationConfig Config(
        double waterRatio = 0.0,
        IReadOnlyList<KeyValuePair<ResourceKey, double>>? initialStock = null,
        CellCoord? warehouseOverride = null)
    {
        return new SimulationConfig(
            Width,
            Height,
            ForestRatio: 0.10,
            RockRatio: 0.05,
            WaterRatio: waterRatio,
            BaseCell: new CellCoord(8, 23),
            EntryCells: new[] { new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1) },
            TowerCatalog: SimEra1Content.LoadTowerSpecs(),
            Enemies: SimEra1Content.LoadEnemyCatalog(),
            Waves: new WaveSpawnConfig(
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
                RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
                BatchesPerWave: 4,
                EntryCells: new[] { new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1) }),
            InitialStock: initialStock ?? new List<KeyValuePair<ResourceKey, double>>
            {
                new(Wood, 100.0),
            },
            WarehouseCell: warehouseOverride ?? WarehouseCell,
            Costs: new BuildCostPolicy(Wood));
    }
}

/// <summary>Payload builders for the five E1 thin command verbs (strict JSON).</summary>
internal static class SimCommands
{
    public static SimCommand BuildTower(long tick, string towerId, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-tower\",\"tower\":\"" + towerId + "\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand BuildLaborCamp(long tick, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-labor-camp\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand BuildHarvester(long tick, CellCoord cell, ResourceKey resource, int seats = 1) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-harvester\",\"resource\":\"" + resource.Value + "\",\"seats\":" + seats
        + ",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand BuildConverter(long tick, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-converter\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand AssignCarrier(long tick, long from, long to) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"assign-carrier\",\"from\":" + from + ",\"to\":" + to + "}");

    /// <summary>Malformed-payload command for rejection tests (not valid JSON).</summary>
    public static SimCommand Malformed(long tick) => new(
        tick, SimCommandKind.PlayerBuild, "{\"verb\":");

    /// <summary>Unknown-verb command for rejection tests.</summary>
    public static SimCommand UnknownVerb(long tick) => new(
        tick, SimCommandKind.PlayerBuild, "{\"verb\":\"sell-everything\"}");

    /// <summary>SystemAction command — kind not implemented in the E1 thin core.</summary>
    public static SimCommand SystemAction(long tick) => new(
        tick, SimCommandKind.SystemAction, "{\"verb\":\"build-tower\"}");
}

/// <summary>JSON envelope of the golden file read back in tests.</summary>
internal static class SimGolden
{
    public const string Schema = "urn:eratd:golden:sim:1";
    public const string RegenEnvironmentVariable = "ERA_SIM_GOLDEN_UPDATE";

    public static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "golden");
            if (Directory.Exists(probe))
            {
                return Path.Combine(probe, "sim-e1-golden.json");
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/golden（测试须在仓库内运行）");
    }

    public static string Serialize(SimGoldenFile golden) =>
        JsonSerializer.Serialize(golden, SimReplayJsonContext.Default.SimGoldenFile);

    public static SimGoldenFile Deserialize(string json) =>
        JsonSerializer.Deserialize(json, SimReplayJsonContext.Default.SimGoldenFile)
            ?? throw new JsonException("Golden file deserialized to null.");
}
