using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Energy;
using EraCore.Pathfinding;
using EraCore.Units;
using EraCore.World;

namespace EraCore.Sim;

/// <summary>
/// Cost policy of the E1 thin build commands (C-12, T-040): which resource
/// pays for player buildings and how much the non-tower buildings cost.
/// Tower costs are NOT here — they come from <see cref="TowerSpec.CostTotal"/>
/// (towers.json, RL7). The non-tower numbers default to 0 because no content
/// anchor exists yet ([NEW-原型校准]: harvest/labor-camp/converter costs land
/// with the T-049 content tables); callers override them from data as soon
/// as it exists. Pure value record.
/// </summary>
/// <param name="Currency">Resource every build cost is paid in (E1 thin: wood points).</param>
/// <param name="LaborCampCostPoints">Labor camp cost in currency points (0 until content).</param>
/// <param name="HarvesterCostPoints">Harvester cost in currency points (0 until content).</param>
/// <param name="ConverterCostPoints">Ammo converter cost in currency points (0 until content).</param>
public sealed record BuildCostPolicy(
    ResourceKey Currency,
    double LaborCampCostPoints = 0.0,
    double HarvesterCostPoints = 0.0,
    double ConverterCostPoints = 0.0);

/// <summary>
/// Level configuration of one <see cref="Simulation"/> run (C-12 minimal
/// orchestration, T-040). Pure data — every gameplay number arrives from the
/// caller (content JSON via the test harness / later C-29 loaders); the
/// engine itself carries no design values (CS-2.3/RL7). Validated by the
/// <see cref="Simulation"/> constructor following the WaveSpawnConfig
/// pattern (record + consumer-side validation).
/// </summary>
/// <param name="Width">Grid width in cells (C-05 GridWorld).</param>
/// <param name="Height">Grid height in cells.</param>
/// <param name="ForestRatio">Procedural forest ratio (row-major rng draws).</param>
/// <param name="RockRatio">Procedural rock ratio.</param>
/// <param name="WaterRatio">Procedural water ratio.</param>
/// <param name="BaseCell">Player base cell — the flow-field target enemies walk to.</param>
/// <param name="EntryCells">Cells enemy batches spawn on (one drawn per unit via IRng).</param>
/// <param name="TowerCatalog">Tower definitions (towers.json items, K-2).</param>
/// <param name="Enemies">Enemy definitions (enemies.json, K-2).</param>
/// <param name="Waves">Wave schedule parameters (F-030/F-031/F-034 knobs).</param>
/// <param name="InitialStock">Starting resource pool deposits (level grant), first-deposit order.</param>
/// <param name="WarehouseCell">
/// Cell of the main-city warehouse (the resource pool's building). When
/// null, <c>assign-carrier</c> deliveries into the pool are rejected — the
/// level has no warehouse stand.
/// </param>
/// <param name="Costs">Build cost policy (see <see cref="BuildCostPolicy"/>).</param>
/// <param name="FlowFrameBudgetMs">
/// Per-tick flow-field rebuild budget (design v3.2 §6: 8 ms/frame; default
/// reuses <see cref="FlowFieldManager.DefaultFrameBudgetMs"/>).
/// </param>
/// <param name="Lane">Flow-field lane id of the single E1 attack lane (K-4 lane domain).</param>
/// <param name="EconomyBuildingHp">
/// Hit points reported for economy buildings (harvest/labor-camp/converter)
/// in snapshots. [NEW-原型校准] structural value until T-049 content rows
/// land — economy buildings are not damageable in the E1 thin version.
/// </param>
/// <param name="Hero">
/// Hero definition of this level (T-067; SP-6: one player-avatar hero per
/// level, unlocked at wave-3 maps). Null (default) = the level fields no
/// hero — nothing hero-related ticks or hashes.
/// </param>
/// <param name="HeroCell">Home cell of the hero (respawn/retreat anchor); required when <paramref name="Hero"/> is set.</param>
/// <param name="RuinRepairCostFraction">
/// Repair-bill fraction of the original build cost carried by every tower
/// ruin of a sale (T-067). Default mirrors
/// <c>EraCore.Checkpoint.FailureTiers.RepairCostFraction</c> (the 30%
/// authority, v3.2 §3) — Sim cannot reference Checkpoint directly
/// (dependency direction Checkpoint → Sim); tests pin the equality so the
/// mirror cannot drift.
/// </param>
/// <param name="ProjectileTwinsEnabled">
/// Whether tower fire events also arm the ballistic shadow layer (弹影层,
/// T-067): deterministic flight twins in the Projectiles group with zero
/// combat authority (hitscan already settled the damage). Default false —
/// the E1 milestone cadence (and its golden) predates the layer.
/// </param>
/// <param name="WarehouseLevel">
/// Warehouse level of the era (v3.2 附录 F.3 cap base 2000×level; E1 = 1).
/// T-067: era-2 levels field level-2 pools so the E2 tower chain sticker
/// prices (fire-oil 2430, F.5) clear the cap; default 1 keeps every E1
/// caller identical.
/// </param>
/// <param name="EnergyRisk">
/// SP-4 energy risk parameter table of the era (T-113, F-010; the E3
/// overheat/E4 meltdown thresholds and consequence magnitudes — data-side
/// = data/eras/{era}.json, RL7). Null (default) = the era has no
/// load-rate ladder risks (E1/E2: era.json risk_events=false) and the
/// energy ledger is constructed exactly as before (bit-identical hashes).
/// </param>
public sealed record SimulationConfig(
    int Width,
    int Height,
    double ForestRatio,
    double RockRatio,
    double WaterRatio,
    CellCoord BaseCell,
    IReadOnlyList<CellCoord> EntryCells,
    IReadOnlyList<TowerSpec> TowerCatalog,
    EnemySpecCatalog Enemies,
    WaveSpawnConfig Waves,
    IReadOnlyList<KeyValuePair<ResourceKey, double>> InitialStock,
    CellCoord? WarehouseCell = null,
    BuildCostPolicy? Costs = null,
    double FlowFrameBudgetMs = FlowFieldManager.DefaultFrameBudgetMs,
    int Lane = 0,
    int EconomyBuildingHp = 1,
    HeroSpec? Hero = null,
    CellCoord? HeroCell = null,
    double RuinRepairCostFraction = 0.30,
    bool ProjectileTwinsEnabled = false,
    int WarehouseLevel = 1,
    EnergyRiskTuning? EnergyRisk = null);
