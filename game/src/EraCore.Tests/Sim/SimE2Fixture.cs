using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Sim;

// T-067 (C-12 全量编排) E2 关卡夹具：era2 内容 JSON（towers/enemies/heroes）
// 驱动的全子系统关卡——E2 塔（箭楼→弩塔升级链+火油溅射燃烧）、护盾框架
// （ShieldStartWave=1 的夹具口径校准，设计归属 E3/T-114）、执戈者英雄、
// 六档物流（民夫/马车/矿轨/传送带/无人机/传送阵）、传送阵能量钩（7 座劳工
// 营 70 点/s 对 60 点/s 传送需求+经济消耗的供给余量）。全部数值来自
// era2 内容表或已冻结锚点；夹具校准值（波时序压缩、初始库存）逐处注明。

/// <summary>Repo-relative loaders for era2 content (RL9: walk up, no absolute paths).</summary>
internal static class SimEra2Content
{
    private static string FindEra2Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era2");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/era2（测试须在仓库内运行）");
    }

    /// <summary>Parses the real era2 towers.json into the production TowerSpec list.</summary>
    public static IReadOnlyList<TowerSpec> LoadTowerSpecs()
    {
        string path = Path.Combine(FindEra2Dir(), "towers.json");
        Assert.True(File.Exists(path), "内容文件缺失：era2/towers.json");
        var specs = TowerSpec.LoadCollection(File.ReadAllText(path));
        Assert.True(specs.Count > 0, "era2/towers.json items 为空");
        return specs;
    }

    /// <summary>Parses the real era2 enemies.json into the production catalog.</summary>
    public static EnemySpecCatalog LoadEnemyCatalog()
    {
        string path = Path.Combine(FindEra2Dir(), "enemies.json");
        Assert.True(File.Exists(path), "内容文件缺失：era2/enemies.json");
        return EnemySpecCatalog.Parse(File.ReadAllText(path));
    }

    /// <summary>Parses the real era2 heroes.json into the production catalog.</summary>
    public static HeroSpecCatalog LoadHeroCatalog()
    {
        string path = Path.Combine(FindEra2Dir(), "heroes.json");
        Assert.True(File.Exists(path), "内容文件缺失：era2/heroes.json");
        return HeroSpecCatalog.Parse(File.ReadAllText(path));
    }

    /// <summary>The E2 hero row (SP-6 h2_spearwarden 破甲突刺).</summary>
    public static HeroSpec SpearWarden() => LoadHeroCatalog().Get("era2:hero-spearwarden");
}

/// <summary>
/// E2 full-subsystem level fixture (T-067): a 20×26 map, water disabled so
/// the fixed command-stream coordinates stay buildable, one attack lane,
/// entries on the top edge and the base on the bottom edge. Fixture
/// calibrations ([NEW-原型校准], test-side): threat 2.0 parks the wave
/// interval at its formula floor (60 s), the 0.25 grace multiplier brings
/// wave 1 inside the 1800-tick milestone window (t=450) so the golden
/// covers live combat, ShieldStartWave=1 activates the shield framework
/// for orchestration coverage (its design era is E3, T-114), the initial
/// 3000 timber grant covers the E2 tower chain sticker prices
/// (90+90+2430) plus the upgrade differential, and
/// ProjectileTwinsEnabled=true arms the ballistic shadow layer (弹影层) —
/// the E1 fixture stays flag-off, so the E1 golden predates the layer
/// unchanged.
/// </summary>
internal static class SimE2Fixture
{
    public const int Width = 20;
    public const int Height = 26;
    public const long Seed = 20261015; // E2 seed family (day-2 of the milestone calendar)

    public static readonly ResourceKey Timber = new("era2:res-timber");

    // Fixed placement plan (all coordinates inside the grid, water ratio 0).
    // Entity ids issued by the Simulation in command order:
    // warehouse=1, camps=2..8, harvester1=9, converter1=10, tower1=11,
    // harvester2=12, harvester3=13, converter3=14, tower2=15, fire-oil=16,
    // converter2=17.
    public static readonly CellCoord WarehouseCell = new(2, 21);
    public static readonly CellCoord[] CampCells =
    {
        new(4, 21), new(5, 21), new(6, 21), new(4, 22), new(5, 22), new(6, 22), new(3, 22),
    };
    public static readonly CellCoord Harvester1Cell = new(3, 18);
    public static readonly CellCoord Harvester2Cell = new(3, 15);
    public static readonly CellCoord Harvester3Cell = new(3, 12);
    public static readonly CellCoord Converter1Cell = new(8, 19);
    public static readonly CellCoord Converter2Cell = new(12, 19);
    public static readonly CellCoord Converter3Cell = new(14, 19);
    public static readonly CellCoord Tower1Cell = new(10, 22);
    public static readonly CellCoord Tower2Cell = new(16, 22);
    public static readonly CellCoord FireOilCell = new(13, 22);
    public static readonly CellCoord HeroCell = new(10, 24);

    public const long IdHarvester1 = 9;
    public const long IdConverter1 = 10;
    public const long IdTower1 = 11;
    public const long IdHarvester2 = 12;
    public const long IdHarvester3 = 13;
    public const long IdConverter3 = 14;
    public const long IdTower2 = 15;
    public const long IdFireOil = 16;
    public const long IdConverter2 = 17;

    /// <summary>Teleporter lane demand in energy points/s (7 camps = 70/s supply vs 60/s lane + economy demand).</summary>
    public const double TeleporterDemandPerSecond = 60.0; // 夹具校准：满供给下 2 单位/tick=0.4 件/tick 的瞬时档演示锚点

    /// <summary>
    /// The canonical E2 milestone command stream: the energy backbone (7
    /// camps), three harvest lines, the era-2 ammo chains (arrows → tower 1,
    /// stone bolts → upgraded tower 1, fire-oil → tower 3), every logistics
    /// tier (porter / cart / mine-rail / belt / drone / teleporter — each
    /// supplier line sized so one tier does not starve the others: the belt
    /// drains its yard at up to 15 件/s so it owns harvester3 exclusively),
    /// the in-place upgrade (arrow → crossbow), hero orders (move + skill)
    /// and one confirmed sale leaving its ruin.
    /// </summary>
    public static IReadOnlyList<SimCommand> MilestoneCommands() => new[]
    {
        SimE2Commands.BuildLaborCamp(30, CampCells[0]),
        SimE2Commands.BuildLaborCamp(60, CampCells[1]),
        SimE2Commands.BuildLaborCamp(90, CampCells[2]),
        SimE2Commands.BuildLaborCamp(120, CampCells[3]),
        SimE2Commands.BuildLaborCamp(150, CampCells[4]),
        SimE2Commands.BuildLaborCamp(180, CampCells[5]),
        SimE2Commands.BuildLaborCamp(210, CampCells[6]),
        SimE2Commands.BuildHarvester(240, Harvester1Cell, Timber, seats: 1),
        SimE2Commands.BuildConverter(270, Converter1Cell, SimE2Commands.Era2ArrowRecipe),
        SimE2Commands.BuildTower(300, "era2:tower-arrow", Tower1Cell),
        SimE2Commands.AssignCarrier(330, IdHarvester1, IdConverter1, tier: "porter"),
        SimE2Commands.AssignCarrier(400, IdConverter1, IdTower1, tier: "cart"),
        SimE2Commands.BuildHarvester(450, Harvester2Cell, Timber, seats: 1),
        SimE2Commands.AssignCarrier(500, IdHarvester2, to: 1, tier: "mine-rail"),
        SimE2Commands.BuildHarvester(520, Harvester3Cell, Timber, seats: 1),
        SimE2Commands.BuildConverter(560, Converter3Cell, SimE2Commands.Era2StoneBoltRecipe),
        SimE2Commands.BuildBelt(600, IdHarvester3, IdConverter3),
        SimE2Commands.BuildTower(620, "era2:tower-arrow", Tower2Cell),
        SimE2Commands.AssignCarrier(660, IdConverter1, IdTower2, tier: "porter"),
        SimE2Commands.BuildTower(720, "era2:tower-fire-oil", FireOilCell),
        SimE2Commands.BuildConverter(760, Converter2Cell, SimE2Commands.Era2FireOilRecipe),
        SimE2Commands.AssignCarrier(800, IdHarvester2, IdConverter2, tier: "drone"),
        SimE2Commands.BuildTeleporter(860, IdConverter2, IdFireOil, TeleporterDemandPerSecond),
        SimE2Commands.UpgradeTower(940, IdTower1, toTier: 2),
        SimE2Commands.AssignCarrier(1020, IdConverter3, IdTower1, tier: "porter"),
        SimE2Commands.HeroOrderMove(1100, new CellCoord(9, 20)),
        SimE2Commands.HeroOrderSkill(1300),
        SimE2Commands.SellTower(1600, IdTower2, confirm: true),
    };

    /// <summary>
    /// Builds the E2 fixture config (see class remarks for the calibrations;
    /// every content number comes from the era2 JSONs loaded through the
    /// production parsers).
    /// </summary>
    public static SimulationConfig Config()
    {
        return new SimulationConfig(
            Width,
            Height,
            ForestRatio: 0.05,
            RockRatio: 0.0, // 夹具校准：岩石比 0——固定命令流坐标的可建性对任何 seed 确定（森林可通行不阻建）
            WaterRatio: 0.0,
            BaseCell: new CellCoord(10, 25),
            EntryCells: new[] { new CellCoord(10, 0), new CellCoord(6, 0), new CellCoord(14, 0) },
            TowerCatalog: SimEra2Content.LoadTowerSpecs(),
            Enemies: SimEra2Content.LoadEnemyCatalog(),
            Waves: new WaveSpawnConfig(
                BaseCount: 8, BeginWave: 99, Spacing: 5, Scaling: 2.0, ThreatLevel: 2.0,
                RegularRoster: new[]
                {
                    "era2:enemy-skirmisher", "era2:enemy-warrior", "era2:enemy-raider",
                    "era2:enemy-crossbowman", "era2:enemy-centurion",
                },
                EliteLineEnabled: true,
                EliteEnemyId: "era2:enemy-ram",
                BossWave: 20,
                BossEnemyId: "era2:enemy-boss",
                FirstWaveGraceMultiplier: 0.25, // 夹具校准：首波 t=450 进入 1800 金样窗（F-034 带下沿之上的压缩）
                ValleySeconds: 15.0,
                BatchesPerWave: 4,
                TickRateHz: 30,
                CellSizePx: 32,
                EntryCells: new[] { new CellCoord(10, 0), new CellCoord(6, 0), new CellCoord(14, 0) },
                Trample: null,
                ShieldStartWave: 1), // 夹具校准：护盾框架编排覆盖（设计归属 E3，T-114 实装）
            InitialStock: new List<KeyValuePair<ResourceKey, double>>
            {
                new(Timber, 3000.0), // 夹具校准：覆盖 E2 塔链 90+90+2430+升级差 180 的初始授予（2 级池上限 4000 内）
            },
            WarehouseCell: WarehouseCell,
            WarehouseLevel: 2, // era2 关卡口径：2 级仓库池（F.3 基 2000×2=4000 ≥ 火油 2430 标价）
            Costs: new BuildCostPolicy(Timber),
            Lane: 0,
            EconomyBuildingHp: 1,
            Hero: SimEra2Content.SpearWarden(),
            HeroCell: HeroCell,
            RuinRepairCostFraction: EraCore.Checkpoint.FailureTiers.RepairCostFraction,
            ProjectileTwinsEnabled: true);
    }
}

/// <summary>Payload builders for the E2 command verbs (strict JSON, test-side).</summary>
internal static class SimE2Commands
{
    /// <summary>Era-2 arrow recipe ([NEW-原型校准] 夹具口径：2 件木材 → 1 件 era2 箭矢，60t 工艺=E1 锚形状).</summary>
    public static readonly string Era2ArrowRecipe =
        "{\"inputs\":[{\"resource\":\"era2:res-timber\",\"items\":2}],\"output\":\"era2:ammo-arrow\",\"craft_ticks\":60}";

    /// <summary>Era-2 fire-oil recipe ([NEW-原型校准] 夹具口径：3 件木材 → 1 件火油矢，90t 工艺).</summary>
    public static readonly string Era2FireOilRecipe =
        "{\"inputs\":[{\"resource\":\"era2:res-timber\",\"items\":3}],\"output\":\"era2:ammo-fire-oil\",\"craft_ticks\":90}";

    /// <summary>Era-2 stone-bolt recipe ([NEW-原型校准] 夹具口径：2 件木材 → 1 件石矢，60t——弩塔 T2 的换弹系).</summary>
    public static readonly string Era2StoneBoltRecipe =
        "{\"inputs\":[{\"resource\":\"era2:res-timber\",\"items\":2}],\"output\":\"era2:ammo-stone-bolt\",\"craft_ticks\":60}";

    public static SimCommand BuildLaborCamp(long tick, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-labor-camp\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand BuildHarvester(long tick, CellCoord cell, ResourceKey resource, int seats = 1) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-harvester\",\"resource\":\"" + resource.Value + "\",\"seats\":" + seats
        + ",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand BuildConverter(long tick, CellCoord cell, string recipeJson) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-converter\",\"recipe\":" + recipeJson
        + ",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand BuildTower(long tick, string towerId, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-tower\",\"tower\":\"" + towerId + "\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand AssignCarrier(long tick, long from, long to, string? tier = null) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"assign-carrier\",\"from\":" + from + ",\"to\":" + to
        + (tier is null ? "" : ",\"tier\":\"" + tier + "\"") + "}");

    public static SimCommand BuildBelt(long tick, long from, long to) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-belt\",\"from\":" + from + ",\"to\":" + to + "}");

    public static SimCommand BuildTeleporter(long tick, long from, long to, double demandPerSecond) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-teleporter\",\"from\":" + from + ",\"to\":" + to
        + ",\"demand_per_second\":" + demandPerSecond.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "}");

    public static SimCommand UpgradeTower(long tick, long target, int toTier) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"upgrade-tower\",\"target\":" + target + ",\"to_tier\":" + toTier + "}");

    public static SimCommand SellTower(long tick, long target, bool confirm) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"sell-tower\",\"target\":" + target + ",\"confirm_sell\":" + (confirm ? "true" : "false") + "}");

    public static SimCommand HeroOrderMove(long tick, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"hero-order\",\"order\":\"move\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}");

    public static SimCommand HeroOrderSkill(long tick) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"hero-order\",\"order\":\"skill\"}");

    public static SimCommand HeroOrderRetreat(long tick) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"hero-order\",\"order\":\"retreat\"}");
}

/// <summary>E2 golden file helpers (path/serialize/deserialize, T-067 first creation).</summary>
internal static class SimE2Golden
{
    public const string RegenEnvironmentVariable = "ERA_SIM_E2_GOLDEN_UPDATE";

    public static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "golden");
            if (Directory.Exists(probe))
            {
                return Path.Combine(probe, "sim-e2-golden.json");
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/golden（测试须在仓库内运行）");
    }

    public static string Serialize(SimGoldenFile golden) =>
        JsonSerializer.Serialize(golden, SimReplayJsonContext.Default.SimGoldenFile);

    public static SimGoldenFile Deserialize(string json) =>
        JsonSerializer.Deserialize(json, SimReplayJsonContext.Default.SimGoldenFile)
            ?? throw new JsonException("E2 golden file deserialized to null.");
}
