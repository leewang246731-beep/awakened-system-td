using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.Energy;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.Sim.EntityGroups;
using EraCore.Supply;
using EraCore.Units;
using EraCore.World;

namespace EraCore.Sim;

/// <summary>Kind tag of one bookkeeping building entry (snapshot/hash ordering).</summary>
public enum SimBuildingKind
{
    /// <summary>Weapon-cycle tower placed through the combat registry (C-09).</summary>
    Tower = 0,

    /// <summary>Energy producer building (labor camp, C-08).</summary>
    LaborCamp = 1,

    /// <summary>Harvest building (C-06 Producer).</summary>
    Harvester = 2,

    /// <summary>Ammo converter building (C-06 AmmoConverter).</summary>
    Converter = 3,

    /// <summary>Main-city warehouse — the resource pool stand (always id 1).</summary>
    Warehouse = 4,
}

/// <summary>
/// Placement-order bookkeeping entry of one building (C-12, T-040): id,
/// content id, cell, hp, tier and kind. The authoritative state stays in the
/// owning subsystems (occupancy in GridWorld, towers in BuildingRegistry);
/// this list only pins a deterministic iteration order for hashing and
/// snapshot views — dictionaries are never iterated.
/// <para>
/// T-123 (energy-risk world consumer): the optional <see cref="MaxHp"/> pins
/// the blast-damage denominator (SP-4 后果列=「其最大 HP×fraction」—— damage
/// reads MAX, not the current row, so overlapping blasts take the same bite).
/// Every pre-T-123 call site leaves it null and reads back its own
/// <see cref="Hp"/> (construction hp = max hp — the E1..E3 rows are never
/// damaged); only risk blasts write sub-max rows.
/// </para>
/// </summary>
/// <param name="MaxHp">Hit-point ceiling; null = the placement hp (undamaged row).</param>
public sealed record SimBuilding(
    EntityId Id,
    string ContentId,
    CellCoord Cell,
    int Hp,
    int Tier,
    SimBuildingKind Kind,
    int? MaxHp = null)
{
    /// <summary>Hit-point ceiling of this row (the placement hp when never damaged).</summary>
    public int MaxHpResolved => MaxHp ?? Hp;
}

/// <summary>
/// One placed weapon tower with its firing loop (C-09 reuse, C-12 wiring).
/// </summary>
/// <param name="Id">Building entity id.</param>
/// <param name="Spec">Tower definition the loop fires for.</param>
/// <param name="Weapon">Reload/fire cycle of this tower.</param>
/// <param name="Cell">Occupied grid cell (targeting anchor, 1 cell = 32 px).</param>
public sealed record SimTower(EntityId Id, TowerSpec Spec, WeaponFire Weapon, CellCoord Cell);

/// <summary>
/// C-12 full simulation orchestrator (T-040 minimal, T-067 全量; F-001/F-154/
/// F-155): wires the subsystems — C-05 world, C-08 energy ledger, C-06
/// economy, C-07 six-tier logistics, C-11 flow fields, C-10 waves/units
/// (shields, war-drum auras, heroes), C-09 towers (upgrade/sell estate,
/// splash+burn) — into one fixed-order 30 Hz tick loop and publishes a
/// double-buffered <see cref="SimSnapshot"/> at frame end. All randomness
/// flows through the injected <see cref="IRng"/> (reseeded with the level
/// seed at construction — the IRng replay contract) and all time through
/// the internal <see cref="SimClock"/> (CS-1.4), so: same seed + same
/// command stream ⇒ same <see cref="ComputeStateHash"/> at every tick
/// (03-decomposition §1.2 — the replay invariant, re-proven by T-067 on the
/// era-2 full-subsystem golden).
/// <para>
/// Commands enter only through <see cref="Enqueue"/> and are applied at
/// tick boundaries strictly via the subsystem public APIs (BuildingRegistry
/// / GridWorld / EnergyLedger / Producer / AmmoConverter / Carrier /
/// TowerEstateOps / Hero / A*) — the orchestrator owns no direct state
/// mutation of foreign internals. Every entity collection lives in a typed
/// <see cref="SimEntityGroups"/> group (G.1 条 2, F-155) instead of a bare
/// list.
/// </para>
/// <para>
/// Fixed per-tick order (T-067 成文, aligned with design v3.2 §6 system
/// timing — the hash depends on this exact order):
/// (1) commands due this tick → (2) energy settle (satisfaction, granary)
/// → (3) economy (harvest + craft, backpressure inside the subsystems)
/// → (4) logistics six tiers: trip carriers (民夫/马车/矿轨/无人机 tick
/// along their routes), belt lines (advance), teleporter lanes (settle the
/// energy bank from the ledger satisfaction, then the instant transfer)
/// → (5) flow-field manager Update (budgeted slice) → (6) unit layer:
/// wave batches materialize into the Units group, shield regeneration
/// ticks, the war-drum aura pass recomputes the aura channel, every unit
/// folds its three speed channels (slow + charge + aura,
/// <see cref="Unit.ResolveSpeedEffects"/>), enemies walk the live field
/// gradient, the hero (when fielded) respawns on its timer and strides
/// toward its ordered point → (7) combat: tower delivery shadow counters
/// fold into the monotonic supply stream, the supply monitor ticks, every
/// tower's loop advances through its SuppliedWeaponFire pacer and fires —
/// single-target shots settle through <see cref="Unit.ApplyDamage"/>
/// (shield → armor → hp), splash towers settle per-target decay through
/// <see cref="SplashAttack"/> and ignite burns, the burn tracker banks its
/// DOT ticks onto living units, flight twins (when enabled) advance and
/// expire → (8) whole-state FNV hash → snapshot publish.
/// </para>
/// </summary>
public sealed class Simulation
{
    /// <summary>Fixed tick rate of the simulation domain (K-1: 30 Hz).</summary>
    public const int TicksPerSecond = 30;

    /// <summary>One grid cell = 32 px (K-1 domain convention).</summary>
    public const int CellSizePx = 32;

    /// <summary>Energy node id base for labor camps (opaque numbering, unique per ledger).</summary>
    public const long LaborCampNodeBase = 1_000;

    /// <summary>Energy node id base for harvesters (opaque numbering).</summary>
    public const long HarvesterNodeBase = 2_000;

    /// <summary>Energy node id base for ammo converters (opaque numbering).</summary>
    public const long ConverterNodeBase = 3_000;

    /// <summary>Entity id base for porters (units issue from the WaveSpawner counter below this).</summary>
    public const long CarrierEntityBase = 1_000_000;

    /// <summary>Entity id of the warehouse stand (the resource pool building), when placed.</summary>
    public const long WarehouseEntityValue = 1;

    // T-067: entity id bases of the new orchestration members (opaque
    // numbering, unique per run — all far above every content-era counter).
    /// <summary>Entity id of the fielded hero (SP-6: at most one per level).</summary>
    public const long HeroEntityValue = 500_000;

    /// <summary>Entity id base of teleporter lanes (tier-6 logistics members).</summary>
    public const long TeleporterEntityBase = 600_000;

    /// <summary>Entity id base of tower ruins (estate sale ledger entries).</summary>
    public const long TowerRuinEntityBase = 900_000;

    /// <summary>Entity id base of projectile twins (弹影层, K-6 载体).</summary>
    public const long ProjectileEntityBase = 5_000_000;

    /// <summary>
    /// Flight twin speed in px per tick ([NEW-原型校准] T-067: spike① flight
    /// envelope — 9 px/tick crosses the 128..256 px tower-to-target band in
    /// 14..28 ticks, the measured mean time-in-flight of the ballistic load).
    /// </summary>
    public const float ProjectileTwinSpeedPxPerTick = 9f;

    /// <summary>
    /// Flight twin lifetime cap in ticks ([NEW-原型校准] T-067: spike① culling
    /// bound — 32 ticks covers the whole tower-to-target flight band with
    /// margin; Presentation-era pacing re-derives from content).
    /// </summary>
    public const int ProjectileTwinLifetimeTicks = 32;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static readonly (int Dx, int Dy)[] NeighbourOrder =
    {
        (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1),
    };

    private readonly SimulationConfig _config;
    private readonly SimClock _clock;
    private readonly GridWorld _world;
    private readonly ResourceStock _stock;
    private readonly EnergyLedger _ledger;
    private readonly BuildingRegistry _registry;
    private readonly SimFlowGrid _flowGrid;
    private readonly FlowFieldManager _flows;
    private readonly FlowFieldKey _laneKey;
    private readonly WaveSpawner _spawner;
    private readonly UnitMovement _movement;
    private readonly AStarPathfinder _pathfinder;

    // T-056 (spike2-pit#6): route cache in front of the pathfinder — the
    // affected-only invalidation seam every occupancy-adding placement uses
    // (放置→受影响路线失效→在途重算改道), plus the canonical outbound route
    // of every assigned porter (the polyline push decisions walk).
    private readonly PathCache _routeCache;
    private readonly Dictionary<EntityId, IReadOnlyList<CellCoord>> _carrierRoutes = new();

    // T-067 (F-155, G.1 条 2): every orchestrator entity collection is a
    // typed EntityGroup (dense array + swap-remove) — the former bare
    // List<Unit>/List<SimBuilding>/… fields migrated 1:1; public properties
    // below return the groups (they implement IReadOnlyList<T>), so the
    // public surface is unchanged.
    private readonly SimEntityGroups _groups = new();

    private readonly Dictionary<EntityId, Producer> _producerById = new();
    private readonly Dictionary<EntityId, AmmoConverter> _converterById = new();

    // T-053 DEBT ①: Supply.TowerMagazine is the AUTHORITATIVE tower-side
    // magazine adapter (single source — the retired Sim-embedded port
    // delegated to nothing); porters unload through it and its
    // CumulativeShotsDelivered counter is what the supply monitor polls.
    private readonly Dictionary<EntityId, TowerMagazine> _magazineById = new();

    // T-053 DEBT ②: per-tower SuppliedWeaponFire pacer — the wrapper is the
    // ONLY caller of the wrapped WeaponFire.Tick (直调 WeaponFire.Tick 会双计
    // 冷却), pacing each loop by the monitor's efficiency curve.
    private readonly Dictionary<EntityId, SuppliedWeaponFire> _pacersById = new();
    private readonly TowerSupplyMonitor _supplyMonitor;
    private readonly SimCommandQueue _queue = new();
    private readonly List<SimCommandResult> _results = new();

    // T-067: upgrade/sell estate (T-065 TowerEstateOps composed on this
    // sim's registry/stock/catalog) and its sale-ruin ledger.
    private readonly TowerEstateOps _estate;

    // T-067: burn DOT bank of splash towers with burn traits (T-065).
    private readonly BurnTracker _burn = new();

    // T-067: monotonic per-tower delivery shadow. TowerSupplyMonitor requires
    // the polled counter to never regress, but an in-place upgrade swaps the
    // magazine (its ItemsAccepted restarts at 0) — the shadow folds the
    // magazine deltas so the monitor's stream stays monotonic across swaps.
    private readonly Dictionary<EntityId, long> _towerDeliveredShadow = new();
    private readonly Dictionary<EntityId, long> _towerDeliveredLastSeen = new();

    private readonly ResourceKey _costCurrency;
    private long _nextBuildingId = WarehouseEntityValue + 1;
    private long _nextCarrierId = CarrierEntityBase;
    private long _laborCampNodes;
    private long _harvesterNodes;
    private long _converterNodes;
    private long _teleporterNodes; // T-067: ledger consumer ids of teleporter lanes
    private long _nextTeleporterId = TeleporterEntityBase; // T-067
    private long _nextProjectileId = ProjectileEntityBase; // T-067: 弹影层 twin ids
    private long _appliedCommands;
    private long _kills;
    private long _shotsLanded;

    // T-123 (F-010, W6-exit P1「AOE 消费面」清账): world-layer consumer of the
    // energy-risk outcomes — blasts (buildings/enemies) and the E4 radiation
    // zones. The ledger is position-free (C-08), so this map pins every
    // producer node's anchor cell at registration (the labor-camp build verb
    // is the Sim's only producer site) and the cursor drains the ledger's
    // append-only RiskEvents queue exactly once per outcome.
    private readonly EnergyRiskWorldConsumer _riskWorld;
    private readonly Dictionary<EnergyNodeId, CellCoord> _producerCells = new();
    private int _riskEventsConsumed;

    /// <summary>
    /// Builds the whole level from one config and one seeded rng stream:
    /// the rng is reseeded with <paramref name="seed"/> (IRng replay
    /// contract), then consumed in a fixed order — terrain generation
    /// (W×H draws), wave entry cells, and nothing else (the E1 level
    /// attaches no granary, so the ledger draws no randomness).
    /// </summary>
    /// <exception cref="ArgumentNullException">config or rng is null.</exception>
    /// <exception cref="ArgumentException">
    /// Config violations: non-positive dimensions over the GridWorld cap,
    /// missing base/entry cells/towers/catalog/waves, non-positive flow
    /// budget, non-positive economy building hp, or a warehouse/base/entry
    /// cell outside the grid.
    /// </exception>
    public Simulation(SimulationConfig config, IRng rng, long seed, TowerSupplyMonitor? supplyMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rng);

        if (config.Width < 1 || config.Height < 1 || config.Width > GridWorld.MaxSide || config.Height > GridWorld.MaxSide)
        {
            throw new ArgumentException(
                $"Grid dimensions must lie in [1,{GridWorld.MaxSide}] (got {config.Width}×{config.Height}).",
                nameof(config));
        }

        if (config.EntryCells is null || config.EntryCells.Count == 0)
        {
            throw new ArgumentException("EntryCells must not be empty.", nameof(config));
        }

        if (config.TowerCatalog is null)
        {
            throw new ArgumentException("TowerCatalog is required (towers.json items).", nameof(config));
        }

        if (config.Enemies is null)
        {
            throw new ArgumentException("Enemies catalog is required (enemies.json).", nameof(config));
        }

        if (config.Waves is null)
        {
            throw new ArgumentException("Waves config is required.", nameof(config));
        }

        if (config.InitialStock is null)
        {
            throw new ArgumentException("InitialStock list is required (may be empty).", nameof(config));
        }

        if (!double.IsFinite(config.FlowFrameBudgetMs) || config.FlowFrameBudgetMs <= 0)
        {
            throw new ArgumentException(
                $"FlowFrameBudgetMs must be finite and positive (got {config.FlowFrameBudgetMs}).", nameof(config));
        }

        // T-123: the radiation-zone HP-decay channel (era4.json content seam) —
        // hostile values fail at construction, never mid-run.
        if (!double.IsFinite(config.RadiationZoneHpDecayPerSecond) || config.RadiationZoneHpDecayPerSecond < 0)
        {
            throw new ArgumentException(
                $"RadiationZoneHpDecayPerSecond must be finite and non-negative (got {config.RadiationZoneHpDecayPerSecond}).",
                nameof(config));
        }

        if (config.EconomyBuildingHp < 1)
        {
            throw new ArgumentException(
                $"EconomyBuildingHp must be at least 1 (got {config.EconomyBuildingHp}).", nameof(config));
        }

        // T-067: hero wiring is a pair — a definition without a home cell (or
        // a home cell without a definition) is a config defect.
        if (config.Hero is null != (config.HeroCell is null))
        {
            throw new ArgumentException(
                "Hero and HeroCell must be set together (a fielded hero needs its home cell).",
                nameof(config));
        }

        if (config.HeroCell is not null)
        {
            EnsureInsideGrid(config.HeroCell, config, "HeroCell");
        }

        EnsureInsideGrid(config.BaseCell, config, "BaseCell");
        foreach (CellCoord entry in config.EntryCells)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(config));
            EnsureInsideGrid(entry, config, "EntryCells[]");
        }

        if (config.WarehouseCell is not null)
        {
            EnsureInsideGrid(config.WarehouseCell, config, "WarehouseCell");
        }

        _config = config;
        // T-053 DEBT ②: the ammo-supply monitor every placed tower registers
        // on. The DEFAULT instance holds the efficiency curve in its grace
        // window (warm-up effectively boundless) so the wrapper paces exactly
        // like the bare loop — the E1 thin core's firing cadence is pinned by
        // the sim/checkpoint goldens (DEBT ② acceptance: 射击节奏不变). A host
        // that wants live 断供降速 throttling (T-048 semantics) injects its
        // own monitor with a finite warm-up.
        _supplyMonitor = supplyMonitor
            ?? new TowerSupplyMonitor(TowerSupplyMonitor.DefaultWindowTicks, int.MaxValue);
        // IRng replay contract: reseed with the level seed, then the fixed
        // consumption order below (terrain → wave entry cells) makes the
        // stream position a pure function of the command-free prefix.
        rng.Reseed(seed);
        Seed = seed;
        _clock = new SimClock();

        // Rng consumption order #1: terrain (one NextDouble per cell,
        // row-major — GridWorld contract).
        _world = new GridWorld(config.Width, config.Height, rng, config.ForestRatio, config.RockRatio, config.WaterRatio);
        _stock = new ResourceStock(_clock, config.WarehouseLevel); // T-067: 仓库等级经配置（E1 缺省=1 不变；era2 关卡挂 2 级池）
        // T-113 (F-010): SP-4 能源风险参数表经配置注入——E1/E2（era.json
        // risk_events=false）传 null，账本行为与哈希同前；E3+ 关卡把
        // data/eras/{era}.json 的阈值行装成 EnergyRiskTuning 挂进来，风险
        // tick 在每步第 (2) 段的 Settle 内推进（过热/熔毁/降载/损毁全在
        // 账本域内结算，AOE/辐射区负载经 RiskEvents 交世界层）。
        _ledger = new EnergyLedger(
            _clock,
            risk: config.EnergyRisk is null ? null : new EnergyRiskEngine(config.EnergyRisk));
        // T-123: the world-layer risk consumer (blast + radiation zones); the
        // decay channel arrives from content (era4.json → config, validated
        // above with the other config rows).
        _riskWorld = new EnergyRiskWorldConsumer(TicksPerSecond, config.RadiationZoneHpDecayPerSecond);
        _registry = new BuildingRegistry(_world);
        _registry.AttachToWorld();
        _flowGrid = new SimFlowGrid(_registry, _world);
        // T-067 DEBT 清账: 第三参 defaultFrameBudgetMs 只喂遗留墙钟入口
        // Update(double?) 的默认值；本模拟的 tick 切片已切至
        // UpdateDeterministic（见 Step 第 (5) 段），配额走
        // FlowFieldManager.DefaultCellsPerTick。
        _flows = new FlowFieldManager(_flowGrid, FlowFieldManager.DefaultMaxActiveFields, config.FlowFrameBudgetMs);
        _laneKey = new FlowFieldKey(config.Lane, 0);
        _flows.Acquire(_laneKey, config.BaseCell);
        _pathfinder = new AStarPathfinder(_flowGrid);
        _routeCache = new PathCache(_pathfinder);
        _spawner = new WaveSpawner(config.Enemies, config.Waves, rng, _clock);
        // The supplier only ever runs inside the readiness gate below (Step
        // itself is never called before the first field is live).
        _movement = new UnitMovement(
            () => _flows.GetField(_laneKey)
                ?? throw new InvalidOperationException("No flow field acquired for the lane."),
            CellSizePx);

        _costCurrency = config.Costs?.Currency ?? new ResourceKey("era1:res-wood");

        // Level grant: initial stock deposits, first-deposit order.
        foreach (KeyValuePair<ResourceKey, double> grant in config.InitialStock)
        {
            _stock.Deposit(grant.Key, grant.Value);
        }

        // The warehouse stand occupies its cell from level start; porters
        // deliver into the pool through the StockDepositPort adapter.
        if (config.WarehouseCell is not null)
        {
            var warehouseId = new EntityId(WarehouseEntityValue);
            _world.RegisterOccupancy(warehouseId, new[] { config.WarehouseCell });
            _groups.Buildings.Add(new SimBuilding(
                warehouseId, "era1:building-warehouse", config.WarehouseCell, config.EconomyBuildingHp, 1,
                SimBuildingKind.Warehouse));
        }

        // T-067: the upgrade/sell estate over this sim's own registry, stock
        // and catalog. Ruin ids issue above every content-era counter; the
        // repair fraction arrives from config (the FailureTiers authority is
        // Checkpoint-side — Sim cannot reference it, direction is reversed).
        _estate = new TowerEstateOps(
            _registry, config.TowerCatalog, _stock, _costCurrency,
            config.RuinRepairCostFraction, firstFreeId: TowerRuinEntityBase);

        // T-067 (SP-6): the optional player-avatar hero, fielded at its home
        // cell centre — levels without one never receive hero orders.
        if (config.Hero is not null && config.HeroCell is not null)
        {
            _groups.Heroes.Add(new Hero(
                config.Hero, new EntityId(HeroEntityValue),
                (config.HeroCell.X * CellSizePx) + (CellSizePx / 2f),
                (config.HeroCell.Y * CellSizePx) + (CellSizePx / 2f)));
        }
    }

    /// <summary>Level seed this run was created with (replay key #1).</summary>
    public long Seed { get; }

    /// <summary>Current simulation tick (0 = before the first Step).</summary>
    public long CurrentTick => _clock.Tick;

    /// <summary>Wave number of the installed spawn plan; 0 before wave 1.</summary>
    public int CurrentWave => _spawner.CurrentWave;

    /// <summary>Units ever spawned, dead ones included, in spawn order.</summary>
    public IReadOnlyList<Unit> Units => _groups.Units; // T-067: 直接列表 → 类型化分组（公共 API 不变）

    /// <summary>Buildings in placement order (warehouse first, then command-built).</summary>
    public IReadOnlyList<SimBuilding> Buildings => _groups.Buildings; // T-067: 同上

    /// <summary>Weapon towers in placement order.</summary>
    public IReadOnlyList<SimTower> Towers => _groups.Towers; // T-067: 同上

    /// <summary>Harvest buildings in placement order.</summary>
    public IReadOnlyList<Producer> Producers => _groups.Producers; // T-067: 同上

    /// <summary>Ammo converters in placement order.</summary>
    public IReadOnlyList<AmmoConverter> Converters => _groups.Converters; // T-067: 同上

    /// <summary>Porters in assignment order.</summary>
    public IReadOnlyList<Carrier> Carriers => _groups.Carriers; // T-067: 同上

    /// <summary>Belt lines (tier 4) in build order (T-067).</summary>
    public IReadOnlyList<ConveyorBelt> Belts => _groups.Belts;

    /// <summary>Teleporter lanes (tier 6, energy-priced) in build order (T-067).</summary>
    public IReadOnlyList<SimTeleporterLane> Teleporters => _groups.Teleporters;

    /// <summary>Projectile twins in flight (弹影层, K-6 载体; empty unless enabled, T-067).</summary>
    public IReadOnlyList<SimProjectile> Projectiles => _groups.Projectiles;

    /// <summary>The fielded hero, when the level carries one (SP-6: at most one).</summary>
    public Hero? Hero => _groups.Heroes.Count > 0 ? _groups.Heroes[0] : null;

    /// <summary>The upgrade/sell estate of this level (T-067; T-065 engine composed here).</summary>
    public TowerEstateOps Estate => _estate;

    /// <summary>The burn DOT bank fed by splash towers with burn traits (T-067).</summary>
    public BurnTracker Burn => _burn;

    /// <summary>Shared era resource pool (C-06).</summary>
    public ResourceStock Stock => _stock;

    /// <summary>Shared era energy ledger (C-08).</summary>
    public EnergyLedger Ledger => _ledger;

    /// <summary>The grid world (C-05) — read-only reuse for callers.</summary>
    public GridWorld World => _world;

    /// <summary>
    /// The flow-field manager this sim drives (C-11; read-only reuse seam).
    /// W4 门禁 F1: the binding guard SimulationFlowFieldBindingTests asserts
    /// its <see cref="FlowFieldManager.LastAdvanceMode"/> stays Deterministic
    /// across ticks — Step's slice must never re-bind to the wall-clock entry
    /// (T-086 same-seed divergence finding; T-067 DEBT 清账).
    /// </summary>
    public FlowFieldManager Flows => _flows;

    /// <summary>Command intake (replay key #2: the command stream).</summary>
    public SimCommandQueue Commands => _queue;

    /// <summary>
    /// The ammo-supply monitor every placed weapon tower registers on
    /// (T-053 DEBT ②): hosts poll efficiencies or register relays on it.
    /// </summary>
    public TowerSupplyMonitor SupplyMonitor => _supplyMonitor;

    /// <summary>
    /// World-layer consumer of the energy-risk outcomes (T-123, F-010 —
    /// W6-exit P1「AOE 消费面」清账): blasts settle through
    /// <see cref="EnergyRiskWorldConsumer.ApplyBlast"/> inside the phase-(2)
    /// drain, the E4 radiation zones live on its
    /// <see cref="EnergyRiskWorldConsumer.RadiationZones"/> list (read seam
    /// for hosts/presentation; empty unless a meltdown fired).
    /// </summary>
    public EnergyRiskWorldConsumer RiskWorld => _riskWorld;

    /// <summary>
    /// Looks up the AUTHORITATIVE magazine of one placed tower (T-053 DEBT ①,
    /// 弹匣单源): the Supply-side <see cref="TowerMagazine"/> porters unload
    /// into — its <c>ItemsAccepted</c>/<c>CumulativeShotsDelivered</c> counters
    /// are the single delivery-accounting source (Sim-side visible).
    /// </summary>
    public bool TryGetTowerMagazine(EntityId id, out TowerMagazine magazine)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _magazineById.TryGetValue(id, out magazine!);
    }

    /// <summary>
    /// Looks up the unique fire pacer of one placed tower (T-053 DEBT ②,
    /// wrapper 唯一化): the ONLY component that ticks the tower's
    /// <see cref="WeaponFire"/> loop — asserting
    /// <c>pacer.Weapon == tower.Weapon</c> pins that no second ticker exists.
    /// </summary>
    public bool TryGetFirePacer(EntityId id, out SuppliedWeaponFire pacer)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _pacersById.TryGetValue(id, out pacer!);
    }

    /// <summary>Double-buffered snapshot output (K-6 producer seam).</summary>
    public SimSnapshotBuffer Snapshots { get; } = new();

    /// <summary>Applied + rejected command outcomes, application order.</summary>
    public IReadOnlyList<SimCommandResult> CommandResults => _results;

    /// <summary>Commands that mutated state so far (rejected ones excluded by design).</summary>
    public long AppliedCommandCount => _appliedCommands;

    /// <summary>Enemies killed so far.</summary>
    public long KillCount => _kills;

    /// <summary>Shots that settled damage on a target so far.</summary>
    public long ShotsLanded => _shotsLanded;

    /// <summary>
    /// T-160b 外部泳道命令观察缝（表现层战役军师面板）：每条经
    /// <see cref="Enqueue"/>/<see cref="EnqueueRange"/> 入队的命令在入队后
    /// 通知一次观察者（入队序）。纯观测面——不读不改任何 sim 状态，零哈希
    /// 影响；<see cref="EraCore.CampaignLoop.LevelSession"/> 借它把外部泳道
    /// （AgentPipeline 执行器直投 sim 的军令命令）也记进会话命令流，令
    /// 波界/退出快照的重放键覆盖所有来源的命令（无损续玩不变量）。
    /// </summary>
    public event Action<SimCommand>? CommandEnqueued;

    /// <summary>Enqueues one command (applied at its IssuedAtTick).</summary>
    public long Enqueue(SimCommand command)
    {
        long sequence = _queue.Enqueue(command);
        CommandEnqueued?.Invoke(command);
        return sequence;
    }

    /// <summary>Enqueues a command stream in order.</summary>
    public void EnqueueRange(IEnumerable<SimCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (SimCommand command in commands)
        {
            _ = Enqueue(command);
        }
    }

    /// <summary>
    /// Advances exactly one tick through the fixed pipeline (class remarks:
    /// the eight numbered phases) and publishes the frame-end snapshot.
    /// Repeated calls replay the invariant: same seed + same commands ⇒
    /// same hash after every tick.
    /// </summary>
    public void Step()
    {
        _clock.Advance();
        long tick = _clock.Tick;

        // (1) Commands due this tick.
        ApplyDueCommands(tick);

        // (2) Terrain/energy settle: pool satisfaction for this tick (granary
        //     stays unattached in the E1 thin level) and — when the level
        //     carries an SP-4 risk table (T-113) — one risk tick of the
        //     overheat/meltdown channels (满足度历史窗口→负载率驱动；后果：
        //     肇事机停机/降载/注销在账本内生效，AOE/辐射区负载入
        //     Ledger.RiskEvents 供世界层应用). Teleporter lanes read
        //     the settled satisfaction in phase (4).
        //     T-123 (W6-exit P1 清账): the world-layer consumption now drains
        //     those outcomes right here — blasts settle on buildings/enemies
        //     and E4 meltdowns spawn the radiation zone — followed by the
        //     per-tick zone pass (slow + HP decay) BEFORE the unit layer's
        //     ResolveSpeedEffects of phase (6), so the debuff bites the same
        //     tick it stands in. A level without a risk table never fires an
        //     outcome and never holds a zone: both passes are strict no-ops
        //     and the E1~E3 hash/orchestration is bit-identical.
        _ledger.Settle();
        ConsumeRiskOutcomes(tick);

        // (3) Economy: harvest into yard piles, craft ammo in converters —
        //     backpressure lives inside the subsystems.
        for (int i = 0; i < _groups.Producers.Count; i++)
        {
            _groups.Producers[i].Tick();
        }

        for (int i = 0; i < _groups.Converters.Count; i++)
        {
            _groups.Converters[i].Tick();
        }

        // (4) Logistics, six ladder tiers (v3.2 §5): trip carriers of tiers
        //     1/2/3/5 advance along their routes, tier-4 belt lines flow,
        //     tier-6 teleporter lanes settle their energy bank from the
        //     ledger satisfaction then move the affordable amount
        //     (传送阵能量钩子——能量是 ∞ 档的唯一限流器).
        for (int i = 0; i < _groups.Carriers.Count; i++)
        {
            _groups.Carriers[i].Tick();
        }

        for (int i = 0; i < _groups.Belts.Count; i++)
        {
            _groups.Belts[i].Tick();
        }

        for (int i = 0; i < _groups.Teleporters.Count; i++)
        {
            SimTeleporterLane lane = _groups.Teleporters[i];
            lane.SettleEnergy(_ledger, TicksPerSecond);
            _ = lane.Transfer();
        }

        // (5) Flow-field slice under the deterministic cell quota (design
        //     v3.2 §6 8 ms slice, T-067 DEBT 清账: wall-clock budgeting made
        //     rebuild completion load-dependent and diverged same-seed hashes
        //     — T-086 finding; quota = workload-based, hash load-independent).
        //     Sim/ 最小改调用点：原 _flows.Update(_config.FlowFrameBudgetMs)。
        //     config.FlowFrameBudgetMs 仍传入 ctor（仅作管理器遗留墙钟入口的
        //     默认值校验），不再参与 tick 切片。
        _flows.UpdateDeterministic(FlowFieldManager.DefaultCellsPerTick);

        // (6) Unit layer. Wave batches materialize into the Units group;
        //     shield regeneration ticks (T-066 framework); the war-drum aura
        //     pass recomputes the aura channel (reset-then-apply, max wins);
        //     every unit folds its three speed channels (slow + charge +
        //     aura) into the resolved stride; everyone walks the live field
        //     gradient (units hold position until the first field is ready —
        //     E1 maps complete inside one slice). The hero (T-066) then
        //     respawns on its timer and strides toward its ordered point.
        _groups.Units.AddRange(_spawner.Advance());
        if (GetLiveField() is { IsReady: true })
        {
            for (int i = 0; i < _groups.Units.Count; i++)
            {
                _groups.Units[i].TickShield(tick);
            }

            _ = UnitAuras.ApplyWarDrums(_groups.Units);
            for (int i = 0; i < _groups.Units.Count; i++)
            {
                _groups.Units[i].ResolveSpeedEffects(tick);
            }

            _movement.StepAll(_groups.Units);
        }
        else
        {
            // No live field yet: the framework channels still advance (the
            // shield clock and the aura folds are position-independent).
            for (int i = 0; i < _groups.Units.Count; i++)
            {
                Unit unit = _groups.Units[i];
                unit.TickShield(tick);
                unit.ResolveSpeedEffects(tick);
            }
        }

        foreach (Hero hero in _groups.Heroes)
        {
            hero.Tick(tick);
            if (hero.Alive && hero.HasOrder && !hero.ArrivedAtTarget)
            {
                _movement.StepOrdered(hero, hero.OrderedX!.Value, hero.OrderedY!.Value);
            }
        }

        // (7) Combat. Delivery shadow fold first (T-067): the per-tower
        //     monotonic stream the monitor polls absorbs magazine swaps
        //     (in-place upgrades restart the magazine counter). T-053 DEBT ②:
        //     the supply monitor ticks next (after logistics, before
        //     weapons — TowerSupplyMonitor contract), then every tower's
        //     loop advances through its SuppliedWeaponFire pacer — the
        //     wrapper is the ONLY WeaponFire.Tick caller, so the cooldown
        //     advances at most one tick per Step (双计冷却不可能) and the
        //     firing cadence at efficiency 1 equals the bare loop's.
        //     Targeting is unchanged: nearest living target inside range
        //     (strictly closer wins, list order breaks ties — deterministic),
        //     settling through Unit.ApplyDamage → shield → Damage.DirectInt.
        //     Splash towers (T-065) settle per-target decay through
        //     SplashAttack and ignite the burn bank; burn ticks land on
        //     living units after the firing sweep. Flight twins (弹影层,
        //     when enabled) advance and expire through the group's
        //     index-corrected sweep.
        for (int i = 0; i < _groups.Towers.Count; i++)
        {
            FoldDeliveryShadow(_groups.Towers[i].Id);
        }

        _supplyMonitor.Tick();
        for (int ti = 0; ti < _groups.Towers.Count; ti++)
        {
            SimTower tower = _groups.Towers[ti];
            _pacersById[tower.Id].Tick();
            if (!tower.Weapon.ReadyToFire)
            {
                continue;
            }

            Unit? target = FindTarget(tower);
            if (target is null)
            {
                continue;
            }

            WeaponFireEvent? shot = _pacersById[tower.Id].TryFire(target.Id, tick);
            if (shot is null)
            {
                continue;
            }

            SettleShot(tower, target, shot, tick);
        }

        SettleBurns(tick);

        if (_config.ProjectileTwinsEnabled)
        {
            foreach (SimProjectile twin in _groups.Projectiles)
            {
                twin.Advance();
            }

            _ = _groups.Projectiles.RemoveWhere(static p => p.IsExpired);
        }

        // (8) Whole-state hash + double-buffered publish.
        long hash = ComputeStateHash();
        PublishSnapshot(tick, hash);
    }

    /// <summary>Runs <paramref name="ticks"/> steps (0 = no-op).</summary>
    /// <exception cref="ArgumentOutOfRangeException">ticks is negative.</exception>
    public void Run(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Tick count must be non-negative.");
        }

        for (long i = 0; i < ticks; i++)
        {
            Step();
        }
    }

    /// <summary>
    /// FNV-1a aggregation over every subsystem's observable state (the
    /// SimSnapshot's StateHash is exactly this value): world map hash,
    /// energy ledger hash, resource pool hash, per-producer/converter
    /// hashes, per-porter observable state, every unit (position bits, hp,
    /// shield, life flags, ticks), every building in placement order, tower
    /// weapon state, applied-command counter and wave number. Dictionary
    /// iteration is never hashed — every group is in a deterministic order.
    /// <para>
    /// T-067 golden-stability domain: components introduced with the full
    /// orchestration (hero, projectile twins, teleporter lanes, belt lines,
    /// sale ruins, per-unit shields) fold into the hash ONLY when they are
    /// actually present — an E1 scenario (none of them fielded) hashes
    /// bit-identically to the T-040 first-creation golden, while an E2
    /// scenario's shields/lanes/twins are fully covered. Absent entities
    /// contribute no state, so the guard is not a hash of zero counts.
    /// </para>
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)_clock.Tick);
        hash = Mix(hash, (ulong)_world.ComputeMapHash());
        hash = Mix(hash, (ulong)_ledger.ComputeStateHash());
        // T-123: the risk world consumer's live radiation zones fold into
        // the hash ONLY when one exists (absent zones contribute nothing —
        // E1~E3-without-meltdown runs stay bit-identical, teleporter/hero
        // precedent). Blast damage itself is already covered: the struck
        // building rows and unit hp/shield bits hash below.
        if (_riskWorld.RadiationZones.Count > 0)
        {
            hash = Mix(hash, (ulong)_riskWorld.ComputeStateHash());
        }
        hash = Mix(hash, (ulong)_stock.ComputeStateHash());

        hash = Mix(hash, (ulong)_groups.Producers.Count);
        for (int i = 0; i < _groups.Producers.Count; i++)
        {
            hash = Mix(hash, (ulong)_groups.Producers[i].ComputeStateHash());
        }

        hash = Mix(hash, (ulong)_groups.Converters.Count);
        for (int i = 0; i < _groups.Converters.Count; i++)
        {
            hash = Mix(hash, (ulong)_groups.Converters[i].ComputeStateHash());
        }

        hash = Mix(hash, (ulong)_groups.Carriers.Count);
        for (int i = 0; i < _groups.Carriers.Count; i++)
        {
            Carrier carrier = _groups.Carriers[i];
            hash = Mix(hash, (ulong)carrier.Id.Value);
            hash = Mix(hash, (ulong)carrier.Phase);
            hash = Mix(hash, (uint)carrier.CargoItems);
            hash = Mix(hash, (ulong)carrier.DeliveredItems);
            hash = Mix(hash, (ulong)carrier.TicksAlive);
            hash = Mix(hash, (ulong)carrier.CurrentCell.X);
            hash = Mix(hash, (ulong)carrier.CurrentCell.Y);
            foreach (char c in carrier.Resource.Value)
            {
                hash = Mix(hash, c);
            }
        }

        // T-067: tier-4 belt lines (present ⇒ hashed).
        if (_groups.Belts.Count > 0)
        {
            hash = Mix(hash, (ulong)_groups.Belts.Count);
            foreach (ConveyorBelt belt in _groups.Belts)
            {
                hash = Mix(hash, (ulong)belt.TicksAlive);
                hash = Mix(hash, (ulong)belt.DeliveredItems);
                hash = Mix(hash, (ulong)belt.StalledTicks);
                hash = Mix(hash, (uint)belt.ItemsOnBelt);
                foreach (int slot in belt.SlotSnapshot)
                {
                    hash = Mix(hash, (uint)slot);
                }
            }
        }

        // T-067: tier-6 teleporter lanes (present ⇒ hashed).
        if (_groups.Teleporters.Count > 0)
        {
            hash = Mix(hash, (ulong)_groups.Teleporters.Count);
            foreach (SimTeleporterLane lane in _groups.Teleporters)
            {
                hash = Mix(hash, (ulong)lane.Id.Value);
                hash = Mix(hash, (uint)lane.Port.Available);
                hash = Mix(hash, (ulong)lane.Link.DeliveredItems);
                hash = Mix(hash, (uint)lane.Link.BufferedItems);
                hash = Mix(hash, (ulong)lane.Link.EnergySpent);
                hash = Mix(hash, (ulong)lane.Link.TransferCalls);
            }
        }

        hash = Mix(hash, (ulong)_groups.Units.Count);
        for (int i = 0; i < _groups.Units.Count; i++)
        {
            Unit unit = _groups.Units[i];
            hash = Mix(hash, (ulong)unit.Id.Value);
            foreach (char c in unit.ContentId)
            {
                hash = Mix(hash, c);
            }

            hash = Mix(hash, BitConverter.SingleToUInt32Bits(unit.X));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(unit.Y));
            hash = Mix(hash, (uint)unit.Hp);
            hash = Mix(hash, (uint)unit.MaxHp);
            hash = Mix(hash, (uint)unit.Armor);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(unit.SpeedPxPerTick));
            if (unit.Shield > 0)
            {
                // T-067: shield framework state (present ⇒ hashed; E1/E2
                // content rows carry none by default, so E1 goldens are
                // untouched).
                hash = Mix(hash, (uint)unit.Shield);
            }

            hash = Mix(hash, unit.Alive ? 1UL : 0UL);
            hash = Mix(hash, unit.ArrivedAtTarget ? 1UL : 0UL);
            hash = Mix(hash, (ulong)unit.SpawnTick);
            hash = Mix(hash, (ulong)unit.DeathTick);
        }

        // T-067: the fielded hero (present ⇒ hashed).
        foreach (Hero hero in _groups.Heroes)
        {
            hash = Mix(hash, (ulong)hero.Id.Value);
            foreach (char c in hero.ContentId)
            {
                hash = Mix(hash, c);
            }

            hash = Mix(hash, BitConverter.SingleToUInt32Bits(hero.X));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(hero.Y));
            hash = Mix(hash, (uint)hero.Hp);
            hash = Mix(hash, (uint)hero.MaxHp);
            hash = Mix(hash, hero.Alive ? 1UL : 0UL);
            hash = Mix(hash, hero.ArrivedAtTarget ? 1UL : 0UL);
            hash = Mix(hash, (ulong)hero.DeathTick);
            hash = Mix(hash, (ulong)hero.RespawnTick);
            hash = Mix(hash, (ulong)hero.Stance);
            hash = Mix(hash, hero.HasOrder ? 1UL : 0UL);
            hash = Mix(hash, (ulong)Math.Max(0L, hero.NextSkillReadyTick));
            hash = Mix(hash, (uint)hero.WavesHeld);
        }

        hash = Mix(hash, (ulong)_groups.Buildings.Count);
        for (int i = 0; i < _groups.Buildings.Count; i++)
        {
            SimBuilding building = _groups.Buildings[i];
            hash = Mix(hash, (ulong)building.Id.Value);
            foreach (char c in building.ContentId)
            {
                hash = Mix(hash, c);
            }

            hash = Mix(hash, (ulong)building.Cell.X);
            hash = Mix(hash, (ulong)building.Cell.Y);
            hash = Mix(hash, (uint)building.Hp);
            hash = Mix(hash, (ulong)building.Tier);
            hash = Mix(hash, (ulong)building.Kind);
        }

        hash = Mix(hash, (ulong)_groups.Towers.Count);
        for (int i = 0; i < _groups.Towers.Count; i++)
        {
            SimTower tower = _groups.Towers[i];
            hash = Mix(hash, (ulong)tower.Id.Value);
            hash = Mix(hash, (uint)tower.Weapon.AmmoShotsRemaining);
            hash = Mix(hash, (uint)tower.Weapon.CooldownTicksRemaining);
            hash = Mix(hash, (uint)tower.Weapon.FiredCount);
        }

        // T-067: sale ruins (present ⇒ hashed) — the 残骸账 of the estate.
        if (_estate.Ruins.Count > 0)
        {
            hash = Mix(hash, (ulong)_estate.Ruins.Count);
            foreach (TowerRuin ruin in _estate.Ruins)
            {
                hash = Mix(hash, (ulong)ruin.RuinId.Value);
                hash = Mix(hash, (ulong)ruin.Cell.X);
                hash = Mix(hash, (ulong)ruin.Cell.Y);
                foreach (char c in ruin.ContentId)
                {
                    hash = Mix(hash, c);
                }

                hash = Mix(hash, (ulong)ruin.Tier);
                hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(ruin.OriginalCostPoints));
                hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(ruin.RepairCostPoints));
                hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(ruin.SellRefundPoints));
            }
        }

        // T-067: burn DOT bank (active burns ⇒ hashed).
        if (_burn.ActiveCount > 0)
        {
            hash = Mix(hash, (ulong)_burn.ActiveCount);
            foreach (BurnState state in _burn.Active)
            {
                hash = Mix(hash, (ulong)state.UnitId.Value);
                hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(state.DamagePerTick));
                hash = Mix(hash, (uint)state.TicksRemaining);
            }
        }

        // T-067: flight twins (弹影层; present ⇒ hashed).
        if (_groups.Projectiles.Count > 0)
        {
            hash = Mix(hash, (ulong)_groups.Projectiles.Count);
            foreach (SimProjectile twin in _groups.Projectiles)
            {
                hash = Mix(hash, (ulong)twin.Id.Value);
                hash = Mix(hash, BitConverter.SingleToUInt32Bits(twin.X));
                hash = Mix(hash, BitConverter.SingleToUInt32Bits(twin.Y));
                hash = Mix(hash, (ulong)twin.AgeTicks);
            }
        }

        hash = Mix(hash, (ulong)_appliedCommands);
        hash = Mix(hash, (ulong)_spawner.CurrentWave);
        return unchecked((long)hash);
    }

    /// <summary>Looks up a harvester by building id.</summary>
    public bool TryGetProducer(EntityId id, out Producer producer) => _producerById.TryGetValue(id, out producer!);

    /// <summary>Looks up a converter by building id.</summary>
    public bool TryGetConverter(EntityId id, out AmmoConverter converter) =>
        _converterById.TryGetValue(id, out converter!);

    /// <summary>Looks up a bookkeeping building by id.</summary>
    public bool TryGetBuilding(EntityId id, out SimBuilding building)
    {
        foreach (SimBuilding entry in _groups.Buildings) // T-067: 直接列表 → 分组
        {
            if (entry.Id.Equals(id))
            {
                building = entry;
                return true;
            }
        }

        building = null!;
        return false;
    }

    private static void EnsureInsideGrid(CellCoord cell, SimulationConfig config, string name)
    {
        if (cell.X < 0 || cell.X >= config.Width || cell.Y < 0 || cell.Y >= config.Height)
        {
            throw new ArgumentException(
                $"{name} ({cell.X},{cell.Y}) is outside the {config.Width}×{config.Height} grid.", nameof(config));
        }
    }

    private FlowField? GetLiveField() => _flows.GetField(_laneKey);

    private void ApplyDueCommands(long tick)
    {
        foreach (SimCommandQueue.Entry entry in _queue.DequeueDue(tick))
        {
            string? rejection = ApplyOne(entry.Command, tick); // T-067: tick 传入（hero-order 技能结算时点）
            if (rejection is null)
            {
                _appliedCommands++;
                _results.Add(SimCommandResult.Applied(tick, entry.Seq, entry.Command.Kind));
            }
            else
            {
                _results.Add(SimCommandResult.Rejected(tick, entry.Seq, entry.Command.Kind, rejection));
            }
        }
    }

    /// <summary>
    /// Applies one command through subsystem public APIs. Returns null on
    /// success or a machine-readable rejection code; a rejected command
    /// mutates nothing (full pre-validation before any subsystem call —
    /// the T-040 boundary acceptance pins this with hash equality).
    /// </summary>
    private string? ApplyOne(SimCommand command, long tick) // T-067: +tick（hero-order 技能结算时点）
    {
        if (command.Kind != SimCommandKind.PlayerBuild)
        {
            // E1 thin implements the player build lane only; SystemAction
            // (K-3 wiring) and TimeControl (presentation-side) land later.
            return $"kind-unsupported:{command.Kind}";
        }

        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(command.PayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "payload-not-object";
            }

            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "payload-malformed-json";
        }

        if (!root.TryGetProperty("verb", out JsonElement verbElement)
            || verbElement.ValueKind != JsonValueKind.String)
        {
            return "verb-missing";
        }

        string verb = verbElement.GetString()!;
        try
        {
            return verb switch
            {
                "build-tower" => ApplyBuildTower(root),
                "build-labor-camp" => ApplyBuildLaborCamp(root),
                "build-harvester" => ApplyBuildHarvester(root),
                "build-converter" => ApplyBuildConverter(root),
                "assign-carrier" => ApplyAssignCarrier(root),
                // T-067 (C-12 全量编排): era-2 verbs — six-tier logistics
                // (tiered trips / belt / teleporter), the estate lane
                // (upgrade/sell, 残骸账) and hero orders.
                "build-belt" => ApplyBuildBelt(root),
                "build-teleporter" => ApplyBuildTeleporter(root),
                "upgrade-tower" => ApplyUpgradeTower(root),
                "sell-tower" => ApplySellTower(root),
                "hero-order" => ApplyHeroOrder(root, tick),
                _ => $"verb-unknown:{verb}",
            };
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidOperationException
                or KeyNotFoundException
                or ArgumentOutOfRangeException)
        {
            // Defensive net: subsystem APIs reject malformed input by
            // contract and leave their own state untouched; the command is
            // recorded as refused instead of aborting the tick.
            return $"subsystem-rejected:{ex.GetType().Name}:{ex.Message}";
        }
    }

    private string? ApplyBuildTower(JsonElement root)
    {
        if (!root.TryGetProperty("tower", out JsonElement towerElement)
            || towerElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(towerElement.GetString()))
        {
            return "tower-missing";
        }

        string towerId = towerElement.GetString()!;
        TowerSpec? spec = null;
        foreach (TowerSpec candidate in _config.TowerCatalog)
        {
            if (string.Equals(candidate.Id, towerId, StringComparison.Ordinal))
            {
                spec = candidate;
                break;
            }
        }

        if (spec is null)
        {
            return $"tower-unknown:{towerId}";
        }

        if (spec.ReloadTicks is null || spec.ReloadTicks <= 0)
        {
            // Control/aura towers (spike trap, campfire) carry no weapon
            // cycle in the E1 thin combat — they arrive with the unit
            // status system.
            return $"tower-no-weapon-cycle:{towerId}";
        }

        if (!TryReadCell(root, out CellCoord cell))
        {
            return "cell-missing";
        }

        string? violation = ValidateBuildableCell(cell);
        if (violation is not null)
        {
            return violation;
        }

        // T-053 DEBT ③: pay-then-place inside the compensation window — a
        // throwing placement refunds the exact withdrawal.
        string? towerResult = BuildCostCompensation.TryPayThenPlace(_stock, _costCurrency, spec.CostTotal, () =>
        {
            var id = new EntityId(_nextBuildingId++);
            IBuilding placed = _registry.Place(id, spec, cell, tier: 1);
            try
            {
                // 注册顺序（W2 批 3 F2 半注册根因）：所有会抛白名单异常的
                // 子系统注册先于任何 sim 侧列表入账——原顺序 _towers.Add 在
                // RegisterTower/_pacersById 之前，RegisterTower 拒绝（如
                // ammo_per_shot<0）会留下"_towers 有塔、_pacersById 无 pacer"
                // 的半注册，后续 Step 的 _pacersById[tower.Id] 抛
                // KeyNotFoundException 且占用格永久泄漏。现约定：拒绝发生时
                // 已突变的只有 registry 占用（RegisterTower 全部校验先于自身
                // 变更），由 catch 撤销；退费由 TryPayThenPlace 补偿窗负责 ⇒
                // 零半注册残留。
                var weapon = new WeaponFire(id, spec, initialAmmoShots: 0);
                // T-053 DEBT ②: demand registration (all argument validation
                // happens here, before any sim-side list gains the tower).
                // T-067: the polled counter is the per-tower delivery SHADOW
                // (monotonic across magazine swaps — in-place upgrades restart
                // the magazine's own counter; FoldDeliveryShadow keeps this
                // stream strictly non-decreasing for the monitor contract).
                _supplyMonitor.RegisterTower(new TowerSupplyRegistration(
                    id, placed.Cell, spec.ReloadTicks!.Value, spec.AmmoPerShot, spec.DamagePerShot,
                    () => _towerDeliveredShadow.TryGetValue(id, out long shadow)
                        ? shadow
                        : 0L));
                var pacer = new SuppliedWeaponFire(weapon, _supplyMonitor, id);
                // T-053 DEBT ①: the authoritative Supply-side magazine.
                TowerMagazine? magazine = spec.AmmoItem is null
                    ? null
                    : new TowerMagazine(new ResourceKey(spec.AmmoItem), weapon);

                // 提交段（不再抛：List.Add、新鲜自增 id 的 Dictionary.Add、
                // 定宽 3×3 的脏区标记——CellRegion 只校验非负 extent）。
                _groups.Towers.Add(new SimTower(id, spec, weapon, placed.Cell)); // T-067: 分组注册
                if (magazine is not null)
                {
                    _magazineById.Add(id, magazine);
                }

                _groups.Buildings.Add(new SimBuilding(id, spec.Id, placed.Cell, spec.Hp, 1, SimBuildingKind.Tower)); // T-067: 分组注册
                _pacersById.Add(id, pacer);
                _flows.MarkDirty(new CellRegion(cell.X - 1, cell.Y - 1, 3, 3));
            }
            catch
            {
                _registry.Remove(id); // 撤销占用（含 world occupancy）——半注册零残留
                _magazineById.Remove(id); // 提交段防御（新鲜 id 下不可达，双保险）
                throw; // 退费交给 TryPayThenPlace 的白名单补偿窗
            }
        });
        if (towerResult is null)
        {
            OnOccupancyAdded(cell);
        }

        return towerResult;
    }

    private string? ApplyBuildLaborCamp(JsonElement root)
    {
        if (!TryReadCell(root, out CellCoord cell))
        {
            return "cell-missing";
        }

        string? violation = ValidateBuildableCell(cell);
        if (violation is not null)
        {
            return violation;
        }

        // T-053 DEBT ③: compensated pay-then-place (see ApplyBuildTower).
        string? campResult = BuildCostCompensation.TryPayThenPlace(
            _stock, _costCurrency, _config.Costs?.LaborCampCostPoints ?? 0.0, () =>
            {
                var id = new EntityId(_nextBuildingId++);
                _world.RegisterOccupancy(id, new[] { cell });
                var node = new EnergyNodeId(LaborCampNodeBase + _laborCampNodes++);
                _ledger.RegisterProducer(node, EnergyNodeKind.LaborCamp);
                _producerCells.Add(node, cell); // T-123: blast anchor of this producer node
                _groups.Buildings.Add(new SimBuilding(
                    id, "era1:building-labor-camp", cell, _config.EconomyBuildingHp, 1, SimBuildingKind.LaborCamp)); // T-067: 分组注册
            });
        if (campResult is null)
        {
            OnOccupancyAdded(cell);
        }

        return campResult;
    }

    private string? ApplyBuildHarvester(JsonElement root)
    {
        if (!root.TryGetProperty("resource", out JsonElement resourceElement)
            || resourceElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(resourceElement.GetString()))
        {
            return "resource-missing";
        }

        int seats = 1;
        if (root.TryGetProperty("seats", out JsonElement seatsElement))
        {
            if (seatsElement.ValueKind != JsonValueKind.Number
                || !seatsElement.TryGetInt32(out seats) || seats < 1)
            {
                return "seats-invalid";
            }
        }

        if (!TryReadCell(root, out CellCoord cell))
        {
            return "cell-missing";
        }

        string? violation = ValidateBuildableCell(cell);
        if (violation is not null)
        {
            return violation;
        }

        // T-053 DEBT ③: compensated pay-then-place (see ApplyBuildTower).
        string? harvesterResult = BuildCostCompensation.TryPayThenPlace(
            _stock, _costCurrency, _config.Costs?.HarvesterCostPoints ?? 0.0, () =>
            {
                var resource = new ResourceKey(resourceElement.GetString()!);
                var id = new EntityId(_nextBuildingId++);
                _world.RegisterOccupancy(id, new[] { cell });
                var producer = new Producer(
                    id, resource, seats, tier: 1, _ledger, new EnergyNodeId(HarvesterNodeBase + _harvesterNodes++));
                _groups.Producers.Add(producer); // T-067: 分组注册
                _producerById.Add(id, producer);
                _groups.Buildings.Add(new SimBuilding(
                    id, "era1:building-harvester", cell, _config.EconomyBuildingHp, 1, SimBuildingKind.Harvester)); // T-067: 分组注册
            });
        if (harvesterResult is null)
        {
            OnOccupancyAdded(cell);
        }

        return harvesterResult;
    }

    private string? ApplyBuildConverter(JsonElement root)
    {
        if (!TryReadCell(root, out CellCoord cell))
        {
            return "cell-missing";
        }

        // T-067: optional recipe override (content-table shape, F-023) —
        // absent payload keeps the frozen E1 arrow anchor; era-2 levels wire
        // their ammo chains through the same verb with their own recipe
        // rows (data via the command stream, RL7 — the engine invents no
        // baskets).
        AmmoConverter.AmmoRecipe recipe = AmmoConverter.E1ArrowRecipe;
        if (root.TryGetProperty("recipe", out JsonElement recipeElement))
        {
            if (recipeElement.ValueKind != JsonValueKind.Object
                || !recipeElement.TryGetProperty("inputs", out JsonElement inputsElement)
                || inputsElement.ValueKind != JsonValueKind.Array
                || !recipeElement.TryGetProperty("output", out JsonElement outputElement)
                || outputElement.ValueKind != JsonValueKind.String
                || !recipeElement.TryGetProperty("craft_ticks", out JsonElement craftElement)
                || craftElement.ValueKind != JsonValueKind.Number
                || !craftElement.TryGetInt32(out int craftTicks))
            {
                return "recipe-malformed";
            }

            var inputs = new List<AmmoConverter.AmmoRecipe.Input>(inputsElement.GetArrayLength());
            foreach (JsonElement inputElement in inputsElement.EnumerateArray())
            {
                if (!inputElement.TryGetProperty("resource", out JsonElement resourceElement)
                    || resourceElement.ValueKind != JsonValueKind.String
                    || !inputElement.TryGetProperty("items", out JsonElement itemsElement)
                    || itemsElement.ValueKind != JsonValueKind.Number
                    || !itemsElement.TryGetInt32(out int items))
                {
                    return "recipe-input-malformed";
                }

                inputs.Add(new AmmoConverter.AmmoRecipe.Input(new ResourceKey(resourceElement.GetString()!), items));
            }

            try
            {
                recipe = new AmmoConverter.AmmoRecipe(
                    inputs, new ResourceKey(outputElement.GetString()!), craftTicks);
            }
            catch (ArgumentException ex)
            {
                return $"recipe-invalid:{ex.Message}";
            }
        }

        string? violation = ValidateBuildableCell(cell);
        if (violation is not null)
        {
            return violation;
        }

        // T-053 DEBT ③: compensated pay-then-place (see ApplyBuildTower). The
        // converter (whose recipe validation is the only throwing step) is
        // constructed BEFORE the world occupancy so a refused recipe leaves
        // no cell leak — the consumed id skip is deterministic.
        string? converterResult = BuildCostCompensation.TryPayThenPlace(
            _stock, _costCurrency, _config.Costs?.ConverterCostPoints ?? 0.0, () =>
            {
                var id = new EntityId(_nextBuildingId++);
                var converter = new AmmoConverter(
                    _clock, _ledger, new EnergyNodeId(ConverterNodeBase + _converterNodes++),
                    recipe);
                _world.RegisterOccupancy(id, new[] { cell });
                _groups.Converters.Add(converter); // T-067: 分组注册
                _converterById.Add(id, converter);
                _groups.Buildings.Add(new SimBuilding(
                    id, "era1:building-arrow-converter", cell, _config.EconomyBuildingHp, 1,
                    SimBuildingKind.Converter)); // T-067: 分组注册
            });
        if (converterResult is null)
        {
            OnOccupancyAdded(cell);
        }

        return converterResult;
    }

    private string? ApplyAssignCarrier(JsonElement root)
    {
        if (!root.TryGetProperty("from", out JsonElement fromElement)
            || fromElement.ValueKind != JsonValueKind.Number
            || !fromElement.TryGetInt64(out long fromValue))
        {
            return "from-missing";
        }

        if (!root.TryGetProperty("to", out JsonElement toElement)
            || toElement.ValueKind != JsonValueKind.Number
            || !toElement.TryGetInt64(out long toValue))
        {
            return "to-missing";
        }

        // T-067: shared endpoint resolution with the belt/teleporter verbs.
        if (!TryResolveCargoLane(fromValue, toValue, out ResourceKey resource, out ICargoSupplier supplier, out ICargoReceiver receiver, out string? laneRejection))
        {
            return laneRejection;
        }

        if (!TryGetBuilding(new EntityId(fromValue), out SimBuilding fromBuilding)
            || !TryGetBuilding(new EntityId(toValue), out SimBuilding toBuilding))
        {
            return "endpoint-unknown";
        }

        // T-067: optional ladder tier of the trip (default 民夫 tier 1; cart /
        // mine-rail / drone are the trip forms of tiers 2/3/5 — belt and
        // teleport are separate verbs because they are not trips).
        TransportTier tier = TransportTiers.Porter;
        RailLine? rail = null;
        if (root.TryGetProperty("tier", out JsonElement tierElement))
        {
            if (tierElement.ValueKind != JsonValueKind.String)
            {
                return "tier-invalid";
            }

            string tierId = tierElement.GetString()!;
            TransportTier? resolved = TransportTiers.ById(tierId);
            if (resolved is null)
            {
                return $"tier-unknown:{tierId}";
            }

            if (resolved.Form is not (TransportForm.TripGrid or TransportForm.TripRail or TransportForm.TripFlight))
            {
                return $"tier-not-a-trip:{tierId}";
            }

            tier = resolved;
        }

        IReadOnlyList<CellCoord> route;
        if (tier.Form == TransportForm.TripFlight)
        {
            // Drone (tier 5): the direct home→destination segment (two
            // cells, no adjacency, terrain ignored) — derived from the same
            // endpoint pair the A* route serves.
            route = new[] { fromBuilding.Cell, toBuilding.Cell };
        }
        else if (!TryRoute(fromBuilding.Cell, toBuilding.Cell, out route))
        {
            return "no-route";
        }

        if (tier.Form == TransportForm.TripRail)
        {
            // T-067: the rail line IS the walked route (its integrity rule
            // reads the composed passability view live, so breaks degrade the
            // speed on the spot — 断轨降级).
            try
            {
                rail = new RailLine(route, _flowGrid);
            }
            catch (ArgumentException ex)
            {
                return $"rail-invalid:{ex.Message}";
            }
        }

        var carrierId = new EntityId(_nextCarrierId++);
        _groups.Carriers.Add(new Carrier(carrierId, resource, supplier, receiver, route, tier, rail)); // T-067: 分组注册+档位参数化
        // T-056 (spike2-pit#6): the canonical outbound polyline — the push
        // rule walks this when a later placement lands on a cell the porter
        // has not passed yet (A* routes never revisit a cell, so the
        // CurrentCell index inside the polyline is unambiguous).
        _carrierRoutes[carrierId] = route;
        return null;
    }

    // ------------------------------------------------ T-067: era-2 verbs --

    /// <summary>
    /// Shared endpoint resolution of the three lane verbs (assign-carrier /
    /// build-belt / build-teleporter): the supplier side is a harvester or a
    /// converter, the receiver side a converter hopper, a tower magazine or
    /// the warehouse pool stand.
    /// </summary>
    private bool TryResolveCargoLane(
        long fromValue,
        long toValue,
        out ResourceKey resource,
        out ICargoSupplier supplier,
        out ICargoReceiver receiver,
        out string? rejection)
    {
        var fromId = new EntityId(fromValue);
        var toId = new EntityId(toValue);
        resource = null!;
        supplier = null!;
        receiver = null!;

        if (_producerById.TryGetValue(fromId, out Producer? producer))
        {
            supplier = producer;
            resource = producer.Resource;
        }
        else if (_converterById.TryGetValue(fromId, out AmmoConverter? converter))
        {
            supplier = converter;
            // T-067: the converter's OWN recipe output — build-converter may
            // wire any era's recipe, so the lane resource follows the
            // machine, not the frozen E1 anchor.
            resource = converter.Recipe.AmmoResource;
        }
        else
        {
            rejection = $"from-not-a-supplier:{fromValue}";
            return false;
        }

        if (toValue == WarehouseEntityValue)
        {
            if (_config.WarehouseCell is null)
            {
                rejection = "to-warehouse-absent";
                return false;
            }

            receiver = new StockDepositPort(_stock);
        }
        else if (_converterById.TryGetValue(toId, out AmmoConverter? hopper))
        {
            receiver = hopper;
        }
        else if (_magazineById.TryGetValue(toId, out TowerMagazine? magazine))
        {
            receiver = magazine;
        }
        else
        {
            rejection = $"to-not-a-receiver:{toValue}";
            return false;
        }

        rejection = null;
        return true;
    }

    /// <summary>build-belt (T-067, ladder tier 4): a conveyor line over the A* route between the two endpoints.</summary>
    private string? ApplyBuildBelt(JsonElement root)
    {
        if (!root.TryGetProperty("from", out JsonElement fromElement)
            || fromElement.ValueKind != JsonValueKind.Number
            || !fromElement.TryGetInt64(out long fromValue))
        {
            return "from-missing";
        }

        if (!root.TryGetProperty("to", out JsonElement toElement)
            || toElement.ValueKind != JsonValueKind.Number
            || !toElement.TryGetInt64(out long toValue))
        {
            return "to-missing";
        }

        if (!TryResolveCargoLane(fromValue, toValue, out ResourceKey resource, out ICargoSupplier supplier, out ICargoReceiver receiver, out string? laneRejection))
        {
            return laneRejection;
        }

        if (!TryGetBuilding(new EntityId(fromValue), out SimBuilding fromBuilding)
            || !TryGetBuilding(new EntityId(toValue), out SimBuilding toBuilding))
        {
            return "endpoint-unknown";
        }

        if (!TryRoute(fromBuilding.Cell, toBuilding.Cell, out IReadOnlyList<CellCoord> route))
        {
            return "no-route";
        }

        try
        {
            _groups.Belts.Add(new ConveyorBelt(route, resource, supplier, receiver)); // T-067: 分组注册
        }
        catch (ArgumentException ex)
        {
            return $"belt-invalid:{ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// build-teleporter (T-067, ladder tier 6): one instant-transfer lane
    /// with its C-08 energy presence — the demand in points/s arrives as
    /// level wiring data in the payload (RL7), the lane settles its bank
    /// from the ledger satisfaction every tick.
    /// </summary>
    private string? ApplyBuildTeleporter(JsonElement root)
    {
        if (!root.TryGetProperty("from", out JsonElement fromElement)
            || fromElement.ValueKind != JsonValueKind.Number
            || !fromElement.TryGetInt64(out long fromValue))
        {
            return "from-missing";
        }

        if (!root.TryGetProperty("to", out JsonElement toElement)
            || toElement.ValueKind != JsonValueKind.Number
            || !toElement.TryGetInt64(out long toValue))
        {
            return "to-missing";
        }

        if (!root.TryGetProperty("demand_per_second", out JsonElement demandElement)
            || demandElement.ValueKind != JsonValueKind.Number
            || !demandElement.TryGetDouble(out double demandPerSecond))
        {
            return "demand-missing";
        }

        if (!double.IsFinite(demandPerSecond) || demandPerSecond < 0)
        {
            return "demand-invalid";
        }

        if (!TryResolveCargoLane(fromValue, toValue, out ResourceKey resource, out ICargoSupplier supplier, out ICargoReceiver receiver, out string? laneRejection))
        {
            return laneRejection;
        }

        var laneId = new EntityId(_nextTeleporterId++);
        var lane = new SimTeleporterLane(
            laneId,
            new EnergyNodeId(SimTeleporterLane.NodeIdBase + _teleporterNodes++),
            resource, supplier, receiver, demandPerSecond, _ledger);
        _groups.Teleporters.Add(lane); // T-067: 分组注册
        return null;
    }

    /// <summary>
    /// upgrade-tower (T-067): one in-place tier step through the estate
    /// (T-065 economics — differential charged, 50% trade-in refunded, same
    /// id and cell). Sim-side bookkeeping rebuilds the tower's weapon loop,
    /// pacer and magazine (升级换弹系：新弹药行从满弹匣起步，余弹随旧行退役)
    /// and the building row (tier/hp of the successor).
    /// </summary>
    private string? ApplyUpgradeTower(JsonElement root)
    {
        if (!root.TryGetProperty("target", out JsonElement targetElement)
            || targetElement.ValueKind != JsonValueKind.Number
            || !targetElement.TryGetInt64(out long targetValue))
        {
            return "target-missing";
        }

        if (!root.TryGetProperty("to_tier", out JsonElement tierElement)
            || tierElement.ValueKind != JsonValueKind.Number
            || !tierElement.TryGetInt32(out int toTier))
        {
            return "to-tier-missing";
        }

        var towerId = new EntityId(targetValue);
        int towerIndex = FindTowerIndex(towerId);
        if (towerIndex < 0)
        {
            return $"target-not-a-sim-tower:{targetValue}";
        }

        UpgradeTowerReceipt? receipt = _estate.ApplyUpgrade(towerId, toTier, out string? refusal);
        if (receipt is null)
        {
            return refusal;
        }

        TowerSpec successor = receipt.Quote.Target;
        if (successor.ReloadTicks is not > 0)
        {
            // Defensive: the successor row carries no weapon cycle — the
            // estate already mutated, so this is logged as a refusal of the
            // sim-side rebuild. UpgradeLaw pins weapon chains, making this
            // unreachable for lawful content.
            return $"successor-no-weapon-cycle:{successor.Id}";
        }

        CellCoord cell = receipt.Quote.Cell;
        var weapon = new WeaponFire(towerId, successor, initialAmmoShots: 0);
        var pacer = new SuppliedWeaponFire(weapon, _supplyMonitor, towerId);
        TowerMagazine? magazine = successor.AmmoItem is null
            ? null
            : new TowerMagazine(new ResourceKey(successor.AmmoItem), weapon);

        _groups.Towers.ReplaceAt(towerIndex, new SimTower(towerId, successor, weapon, cell));
        if (magazine is null)
        {
            _magazineById.Remove(towerId);
        }
        else
        {
            _magazineById[towerId] = magazine;
        }

        _pacersById[towerId] = pacer;
        int buildingIndex = FindBuildingIndex(towerId);
        if (buildingIndex >= 0)
        {
            _groups.Buildings.ReplaceAt(buildingIndex, new SimBuilding(
                towerId, successor.Id, cell, successor.Hp, successor.Tier, SimBuildingKind.Tower));
        }

        // The tear price of the cell changed with the row's hp — the flow
        // field must see the new registry row.
        _flows.MarkDirty(new CellRegion(cell.X - 1, cell.Y - 1, 3, 3));
        return null;
    }

    /// <summary>
    /// sell-tower (T-067): full teardown through the estate (30% refund,
    /// v3.2 §11.4) leaving the 残骸账 — the ruin row (repair bill per the
    /// wired FailureTiers fraction) AND its grid attach: the ruin occupies
    /// the freed cell, so the rubble blocks rebuilding until the repair
    /// entry point of the C-13 action matrix clears it.
    /// </summary>
    private string? ApplySellTower(JsonElement root)
    {
        if (!root.TryGetProperty("target", out JsonElement targetElement)
            || targetElement.ValueKind != JsonValueKind.Number
            || !targetElement.TryGetInt64(out long targetValue))
        {
            return "target-missing";
        }

        if (!root.TryGetProperty("confirm_sell", out JsonElement confirmElement)
            || confirmElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !confirmElement.GetBoolean())
        {
            // F-014: the explicit danger bit is required and default-denied.
            return "confirm-required";
        }

        var towerId = new EntityId(targetValue);
        int towerIndex = FindTowerIndex(towerId);
        if (towerIndex < 0)
        {
            return $"target-not-a-sim-tower:{targetValue}";
        }

        SellTowerReceipt? receipt = _estate.ApplySell(towerId, out string? refusal);
        if (receipt is null)
        {
            return refusal;
        }

        // Sim-side retire (swap-remove: the swapped-in tower is visited next
        // sweep — the group's index-corrected protocol).
        _ = _groups.Towers.RemoveAtSwap(towerIndex);
        _ = _pacersById.Remove(towerId);
        _ = _magazineById.Remove(towerId);
        int buildingIndex = FindBuildingIndex(towerId);
        if (buildingIndex >= 0)
        {
            _ = _groups.Buildings.RemoveAtSwap(buildingIndex);
        }

        // 残骸账 grid attach: the ruin holds the cell.
        _world.RegisterOccupancy(receipt.Ruin.RuinId, new[] { receipt.Ruin.Cell });
        _flows.MarkDirty(new CellRegion(receipt.Ruin.Cell.X - 1, receipt.Ruin.Cell.Y - 1, 3, 3));
        return null;
    }

    /// <summary>
    /// hero-order (T-067): the three G.2 verbs onto the fielded hero —
    /// move (ordered stride to a cell), retreat (stride home) and skill
    /// (settle the active now; cooldown fires only on a successful cast).
    /// </summary>
    private string? ApplyHeroOrder(JsonElement root, long tick)
    {
        Hero? hero = Hero;
        if (hero is null)
        {
            return "hero-absent";
        }

        if (!root.TryGetProperty("order", out JsonElement orderElement)
            || orderElement.ValueKind != JsonValueKind.String)
        {
            return "order-missing";
        }

        switch (orderElement.GetString())
        {
            case "move":
                if (!TryReadCell(root, out CellCoord cell))
                {
                    return "cell-missing";
                }

                if (!_world.Contains(cell))
                {
                    return "cell-out-of-bounds";
                }

                hero.OrderMove(cell, CellSizePx);
                return null;

            case "retreat":
                hero.OrderRetreat();
                return null;

            case "skill":
                if (!hero.Alive)
                {
                    return "hero-down";
                }

                // Runtime outcomes (cooldown / no targets) are legitimate
                // applied results — the order was accepted and evaluated;
                // only an impossible order is refused.
                _ = hero.TryUseSkill(tick, _groups.Units, CellSizePx);
                return null;

            default:
                return $"order-unknown:{orderElement.GetString()}";
        }
    }

    // ------------------------------------------------ T-067: combat helpers --

    /// <summary>
    /// Folds one tower's magazine counter delta into the monotonic delivery
    /// shadow the supply monitor polls (see the registration site in
    /// <see cref="ApplyBuildTower"/>). A magazine swap (in-place upgrade
    /// restarts the counter at 0) adopts the fresh counter instead of
    /// regressing the stream.
    /// </summary>
    private void FoldDeliveryShadow(EntityId towerId)
    {
        long current = _magazineById.TryGetValue(towerId, out TowerMagazine? magazine)
            ? magazine.CumulativeShotsDelivered
            : 0L;
        long last = _towerDeliveredLastSeen.TryGetValue(towerId, out long seen) ? seen : 0L;
        if (current < last)
        {
            // Magazine swap: adopt the fresh counter (stream stays monotonic).
            _towerDeliveredLastSeen[towerId] = current;
            return;
        }

        if (current > last)
        {
            _towerDeliveredShadow[towerId] =
                (_towerDeliveredShadow.TryGetValue(towerId, out long shadow) ? shadow : 0L) + (current - last);
        }

        _towerDeliveredLastSeen[towerId] = current;
    }

    /// <summary>
    /// Settles one fired shot: single-target towers settle the rounded
    /// per-shot damage through <see cref="Unit.ApplyDamage"/> (shield →
    /// armor → hp, exactly once); splash towers (T-065) feed every living
    /// candidate through <see cref="SplashAttack"/> — per-target decay
    /// settles through ApplyDamage on the reconstructed raw damage
    /// (<c>d × decay</c>), burn ignition happened inside the settle, and
    /// Settle's own DamageDealt stays its no-shield accounting view.
    /// Flight twins (弹影层) arm on every landed shot when enabled.
    /// </summary>
    private void SettleShot(SimTower tower, Unit target, WeaponFireEvent shot, long tick)
    {
        _shotsLanded++;
        bool splash = tower.Spec.Class == TowerClass.Splash
            || (tower.Spec.Traits?.SplashRadiusCells ?? 0) > 0;
        if (splash)
        {
            float impactX = target.X;
            float impactY = target.Y;
            var candidates = new List<SplashCandidate>(_groups.Units.Count);
            var byId = new Dictionary<EntityId, Unit>(_groups.Units.Count);
            foreach (Unit unit in _groups.Units)
            {
                if (!unit.Alive)
                {
                    continue;
                }

                float dx = unit.X - impactX;
                float dy = unit.Y - impactY;
                candidates.Add(new SplashCandidate(unit, Math.Sqrt((dx * dx) + (dy * dy)) / CellSizePx));
                byId[unit.Id] = unit;
            }

            foreach (SplashHit hit in SplashAttack.Settle(tower.Spec, candidates, _burn))
            {
                if (!byId.TryGetValue(hit.TargetId, out Unit? struck) || !struck.Alive)
                {
                    continue;
                }

                int raw = (int)Math.Round(tower.Spec.DamagePerShot * hit.Decay, MidpointRounding.AwayFromZero);
                _ = struck.ApplyDamage(raw, tick);
                if (!struck.Alive)
                {
                    _kills++;
                }
            }
        }
        else
        {
            int damage = (int)Math.Round(shot.DamagePerShot, MidpointRounding.AwayFromZero);
            _ = target.ApplyDamage(damage, tick);
            if (!target.Alive)
            {
                _kills++;
            }
        }

        if (_config.ProjectileTwinsEnabled)
        {
            _groups.Projectiles.Add(new SimProjectile(
                new EntityId(_nextProjectileId++),
                tower.Id,
                (tower.Cell.X * CellSizePx) + (CellSizePx / 2f),
                (tower.Cell.Y * CellSizePx) + (CellSizePx / 2f),
                target.X,
                target.Y,
                (int)Math.Round(shot.DamagePerShot, MidpointRounding.AwayFromZero),
                ProjectileTwinSpeedPxPerTick,
                ProjectileTwinLifetimeTicks));
        }
    }

    /// <summary>
    /// Phase-(2) drain of the ledger's energy-risk outcomes (T-123, F-010 —
    /// the W6-exit deferred AOE consumption, now wired): every outcome the
    /// ledger appended since the last step is settled onto the world exactly
    /// once — the blast (E3 overheat AND E4 meltdown share the path:
    /// buildings inside the radius take the max-HP fraction, enemies the
    /// flat damage through the single <see cref="Unit.ApplyDamage"/>
    /// pipeline) anchored at the culprit producer's registered cell, then a
    /// positive radiation duration spawns the E4 zone. The per-tick zone
    /// pass (slow re-arm + HP-decay batch + pruning) follows, still ahead
    /// of the phase-(6) speed resolve. An unresolvable culprit node is a
    /// wiring bug and throws — the ledger only fires on producers the Sim
    /// itself registered, so a miss means the anchor map lost an entry
    /// (same standing as the T-113 null-culprit throw).
    /// </summary>
    private void ConsumeRiskOutcomes(long tick)
    {
        var events = _ledger.RiskEvents;
        while (_riskEventsConsumed < events.Count)
        {
            EnergyRiskOutcome outcome = events[_riskEventsConsumed++];
            if (!_producerCells.TryGetValue(outcome.Culprit, out CellCoord? anchor))
            {
                throw new InvalidOperationException(
                    $"Energy risk outcome at tick {tick} (culprit node {outcome.Culprit.Value}) has no registered anchor cell — the producer map is incomplete.");
            }

            if (outcome.Kind == EnergyRiskEventKind.Annihilation)
            {
                // T-134 (SP-4 L148): the annihilation blast halves its radius
                // when a force-field tower stands (SP-1b L54) and chains
                // through the OTHER energy buildings inside the radius —
                // each chained producer detonates (×0.6 damage decay per
                // link, plan in node-id order) and leaves the grid. The
                // unit damage of every link settles through the same blast
                // path (flat damage inside the link radius).
                bool forceFieldPresent = _config.AnnihilationForceFieldTowerId is not null
                    && _groups.Towers.Any(tower => tower.Spec.Id == _config.AnnihilationForceFieldTowerId);

                IReadOnlyList<AnnihilationChainLink> chain = EnergyRiskWorldConsumer.PlanAnnihilationChain(
                    outcome,
                    anchor,
                    _producerCells.Select(row => (row.Key, row.Value)).ToList(),
                    forceFieldPresent);
                foreach (AnnihilationChainLink link in chain)
                {
                    if (link.LinkDepth > 0)
                    {
                        // 连锁引爆：chained energy buildings leave the grid
                        // (the ledger side of the detonation; their world
                        // rows stay as wrecks — estate semantics, e4 mine
                        // precedent).
                        _ = _ledger.Unregister(link.NodeId);
                        _ = _producerCells.Remove(link.NodeId);
                    }

                    _ = _riskWorld.ApplyBlast(
                        outcome with { EnemyDamage = link.UnitDamage, RadiusCells = link.RadiusCells },
                        tick,
                        link.Cell,
                        _groups.Buildings,
                        (index, damaged) => _groups.Buildings.ReplaceAt(index, damaged),
                        _groups.Units,
                        onKill: _ => _kills++,
                        radiusMultiplier: 1.0); // radius already halved at plan time
                }

                continue; // no radiation zone on the annihilation channel (SP-4 L148 has none)
            }

            _ = _riskWorld.ApplyBlast(
                outcome,
                tick,
                anchor,
                _groups.Buildings,
                (index, damaged) => _groups.Buildings.ReplaceAt(index, damaged),
                _groups.Units,
                onKill: _ => _kills++);
            _ = _riskWorld.SpawnZone(outcome, anchor, tick);
        }

        _riskWorld.TickZones(tick, _groups.Units, onKill: _ => _kills++);
    }

    /// <summary>
    /// One burn DOT pass (T-065 bank, T-067 wiring): every banked tick lands
    /// on its living unit through <see cref="Unit.ApplyDamage"/> (the 10%
    /// armor floor keeps 1-point ticks meaningful against armor).
    /// </summary>
    private void SettleBurns(long tick)
    {
        if (_burn.ActiveCount == 0)
        {
            return;
        }

        foreach (BurnTick burnTick in _burn.Tick())
        {
            if (!TryGetUnit(burnTick.UnitId, out Unit? unit) || !unit.Alive)
            {
                continue;
            }

            _ = unit.ApplyDamage(burnTick.Damage, tick);
            if (!unit.Alive)
            {
                _kills++;
            }
        }
    }

    private bool TryGetUnit(EntityId id, out Unit unit)
    {
        foreach (Unit candidate in _groups.Units)
        {
            if (candidate.Id.Equals(id))
            {
                unit = candidate;
                return true;
            }
        }

        unit = null!;
        return false;
    }

    private int FindTowerIndex(EntityId towerId)
    {
        for (int i = 0; i < _groups.Towers.Count; i++)
        {
            if (_groups.Towers[i].Id.Equals(towerId))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindBuildingIndex(EntityId buildingId)
    {
        for (int i = 0; i < _groups.Buildings.Count; i++)
        {
            if (_groups.Buildings[i].Id.Equals(buildingId))
            {
                return i;
            }
        }

        return -1;
    }

    private string? ValidateBuildableCell(CellCoord cell)
    {
        if (!_world.Contains(cell))
        {
            return "cell-out-of-bounds";
        }

        if (!_world.IsPassable(cell))
        {
            // Water/rock/wall terrain is not buildable — the "locked cell"
            // rejection of the T-040 boundary set.
            return "cell-not-buildable";
        }

        if (_world.IsOccupied(cell))
        {
            return "cell-occupied";
        }

        // T-056 (spike2-pit#6): unit-layer mutual exclusion — a living enemy
        // or a servicing porter on the cell refuses the placement with the
        // SAME code the SimulationActionView preview layer produces (the
        // rule body is shared; the codes cannot drift apart).
        return BuildOccupancyRule.UnitLayerRejection(cell, _groups.Units, _groups.Carriers); // T-067: 分组视图
    }

    /// <summary>
    /// T-056 (spike2-pit#6) placement follow-up, run once per successfully
    /// placed occupancy-adding building: the route cache drops exactly the
    /// stored routes through the new cell (受影响失效 — never a full clear),
    /// then every OUTBOUND porter whose remaining canonical route crosses
    /// the cell is pushed — rerouted around the building via a fresh A*
    /// query over the already-updated occupancy. Inbound porters and
    /// porters still inside a service window are NOT displaced here: their
    /// cached-route replay staleness is spike2-pit#5's own debt and lands
    /// with the C-12 orchestrator's route-invalidation reroute (T-059).
    /// </summary>
    /// <returns>How many in-flight porters were rerouted (推移计数).</returns>
    private int OnOccupancyAdded(CellCoord cell)
    {
        _routeCache.Invalidate(cell);
        int pushed = 0;
        foreach (Carrier carrier in _groups.Carriers) // T-067: 分组视图
        {
            if (carrier.Phase != CarrierPhase.Outbound
                || !_carrierRoutes.TryGetValue(carrier.Id, out IReadOnlyList<CellCoord>? route))
            {
                continue;
            }

            int currentIndex = IndexOfCell(route, carrier.CurrentCell);
            int blockedIndex = IndexOfCell(route, cell);
            if (currentIndex < 0 || blockedIndex < 0 || blockedIndex <= currentIndex)
            {
                // Unknown polyline, cell not on this porter's route, or the
                // porter already walked past it — nothing to push.
                continue;
            }

            if (!TryPushAround(carrier, cell, out IReadOnlyList<CellCoord> detour))
            {
                continue;
            }

            carrier.Reroute(detour);
            _carrierRoutes[carrier.Id] = detour;
            pushed++;
        }

        return pushed;
    }

    /// <summary>
    /// One pushed porter's detour: A* from the porter's current cell to its
    /// destination over the post-placement grid. When the porter stands ON
    /// the freshly occupied cell itself (edge of the one-tick validation
    /// window), the detour legitimately starts there and steps out to the
    /// first free neighbour — the porter walks out of the building instead
    /// of clipping through it (移动层不查通行性 is exactly what the spike
    /// tolerated and this rule retires).
    /// </summary>
    private bool TryPushAround(Carrier carrier, CellCoord blockedCell, out IReadOnlyList<CellCoord> detour)
    {
        detour = Array.Empty<CellCoord>();
        CellCoord start = carrier.CurrentCell;
        bool standsOnBlocked = start.Equals(blockedCell);
        if (standsOnBlocked)
        {
            if (!TryPickFreeNeighbour(blockedCell, out CellCoord exit))
            {
                return false; // fully boxed in — the next departure reroute owns it
            }

            start = exit;
        }

        PathQueryResult result = _routeCache.GetPath(start, carrier.Destination);
        if (result.Status != PathQueryStatus.Found || result.Cells.Count < 1)
        {
            return false; // no detour exists (e.g. walled off) — porter keeps its route
        }

        if (!standsOnBlocked)
        {
            detour = result.Cells;
            return result.Cells[0].Equals(carrier.CurrentCell);
        }

        var walked = new List<CellCoord>(result.Cells.Count + 1) { carrier.CurrentCell };
        walked.AddRange(result.Cells);
        detour = walked;
        return true;
    }

    private static int IndexOfCell(IReadOnlyList<CellCoord> route, CellCoord cell)
    {
        for (int i = 0; i < route.Count; i++)
        {
            if (route[i].Equals(cell))
            {
                return i;
            }
        }

        return -1;
    }

    private bool TryReadCell(JsonElement root, out CellCoord cell)
    {
        cell = null!;
        if (!root.TryGetProperty("cell", out JsonElement cellElement)
            || cellElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!cellElement.TryGetProperty("x", out JsonElement xElement)
            || xElement.ValueKind != JsonValueKind.Number || !xElement.TryGetInt32(out int x))
        {
            return false;
        }

        if (!cellElement.TryGetProperty("y", out JsonElement yElement)
            || yElement.ValueKind != JsonValueKind.Number || !yElement.TryGetInt32(out int y))
        {
            return false;
        }

        cell = new CellCoord(x, y);
        return true;
    }

    /// <summary>
    /// Computes the porter route between the two buildings: A* over the
    /// composed grid between the first free neighbours (fixed clockwise
    /// scan order) of each endpoint — buildings themselves are not
    /// walkable.
    /// </summary>
    private bool TryRoute(CellCoord fromCell, CellCoord toCell, out IReadOnlyList<CellCoord> route)
    {
        route = Array.Empty<CellCoord>();
        if (!TryPickFreeNeighbour(fromCell, out CellCoord fromNeighbour)
            || !TryPickFreeNeighbour(toCell, out CellCoord toNeighbour))
        {
            return false;
        }

        if (fromNeighbour.Equals(toNeighbour))
        {
            return false;
        }

        PathQueryResult result = _routeCache.GetPath(fromNeighbour, toNeighbour);
        if (result.Status != PathQueryStatus.Found || result.Cells.Count < 2)
        {
            return false;
        }

        route = result.Cells;
        return true;
    }

    private bool TryPickFreeNeighbour(CellCoord cell, out CellCoord neighbour)
    {
        foreach ((int dx, int dy) in NeighbourOrder)
        {
            var candidate = new CellCoord(cell.X + dx, cell.Y + dy);
            if (_world.Contains(candidate) && _flowGrid.Passable(candidate) && !_world.IsOccupied(candidate))
            {
                neighbour = candidate;
                return true;
            }
        }

        neighbour = null!;
        return false;
    }

    private Unit? FindTarget(SimTower tower)
    {
        double rangePx = tower.Spec.RangeCells * CellSizePx;
        float rangeSquared = (float)(rangePx * rangePx);
        float centreX = (tower.Cell.X * CellSizePx) + (CellSizePx / 2f);
        float centreY = (tower.Cell.Y * CellSizePx) + (CellSizePx / 2f);
        Unit? best = null;
        float bestDistance = rangeSquared;
        for (int ui = 0; ui < _groups.Units.Count; ui++) // T-067: 分组视图（索引化热路径）
        {
            Unit unit = _groups.Units[ui];
            if (!unit.Alive)
            {
                continue;
            }

            float dx = unit.X - centreX;
            float dy = unit.Y - centreY;
            float distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                // strictly closer only — ties keep the earlier list entry
                best = unit;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void PublishSnapshot(long tick, long hash)
    {
        var unitViews = new SimUnitView[_groups.Units.Count]; // T-067: 分组视图
        int liveCount = 0;
        for (int i = 0; i < _groups.Units.Count; i++)
        {
            Unit unit = _groups.Units[i];
            if (!unit.Alive)
            {
                continue;
            }

            unitViews[liveCount++] = new SimUnitView(
                unit.Id, unit.ContentId, unit.X, unit.Y, unit.Hp, unit.MaxHp, unit.Armor, unit.Shield,
                unit.SpeedPxPerTick);
        }

        Array.Resize(ref unitViews, liveCount);

        var buildingViews = new SimBuildingView[_groups.Buildings.Count]; // T-067: 分组视图
        for (int i = 0; i < _groups.Buildings.Count; i++)
        {
            SimBuilding building = _groups.Buildings[i];
            buildingViews[i] = new SimBuildingView(
                building.Id, building.ContentId, building.Cell, building.Hp, building.Hp, building.Tier);
        }

        Snapshots.Stage(new ImmutableSimSnapshot(
            tick, unitViews, Array.Empty<SimProjectileView>(), buildingViews, hash));
        Snapshots.SwapBuffers();
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    /// <summary>
    /// Composed C-05×C-09×C-06 walkability the porters' A* runs on: tower
    /// cells block (registry view, tear price intact) and economy-building
    /// occupancy blocks too — a pure composition of the two public grid
    /// views, no subsystem internals touched.
    /// </summary>
    private sealed class SimFlowGrid : IFlowGrid
    {
        private readonly BuildingRegistry _registry;
        private readonly GridWorld _world;

        public SimFlowGrid(BuildingRegistry registry, GridWorld world)
        {
            _registry = registry;
            _world = world;
        }

        public int Width => _world.Width;

        public int Height => _world.Height;

        public int Cost(CellCoord cell) => _registry.Cost(cell);

        public bool Passable(CellCoord cell) =>
            _registry.Passable(cell) && !_world.IsOccupied(cell);

        public int BloodCost(CellCoord cell) => _registry.BloodCost(cell);
    }

    /// <summary>
    /// Warehouse-side unload port: deposits accepted items straight into
    /// the shared resource pool (入池只计上限内 — the pool itself decides
    /// how much fits).
    /// </summary>
    private sealed class StockDepositPort : ICargoReceiver
    {
        private readonly ResourceStock _stock;

        public StockDepositPort(ResourceStock stock)
        {
            _stock = stock;
        }

        public int TryAccept(ResourceKey resource, int amount)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Accept amount must be non-negative.");
            }

            return (int)_stock.Deposit(resource, amount);
        }
    }
}
