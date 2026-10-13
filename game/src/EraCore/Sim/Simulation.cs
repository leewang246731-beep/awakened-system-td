using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.Energy;
using EraCore.Logistics;
using EraCore.Pathfinding;
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
/// </summary>
public sealed record SimBuilding(
    EntityId Id,
    string ContentId,
    CellCoord Cell,
    int Hp,
    int Tier,
    SimBuildingKind Kind);

/// <summary>
/// One placed weapon tower with its firing loop (C-09 reuse, C-12 wiring).
/// </summary>
/// <param name="Id">Building entity id.</param>
/// <param name="Spec">Tower definition the loop fires for.</param>
/// <param name="Weapon">Reload/fire cycle of this tower.</param>
/// <param name="Cell">Occupied grid cell (targeting anchor, 1 cell = 32 px).</param>
public sealed record SimTower(EntityId Id, TowerSpec Spec, WeaponFire Weapon, CellCoord Cell);

/// <summary>
/// C-12 minimal simulation orchestrator (T-040; F-001/F-154): wires the
/// Wave-1 subsystems — C-05 world, C-08 energy ledger, C-06 economy, C-07
/// porters, C-11 flow fields, C-10 waves/units, C-09 towers — into one
/// fixed-order 30 Hz tick loop and publishes a double-buffered
/// <see cref="SimSnapshot"/> at frame end. All randomness flows through the
/// injected <see cref="IRng"/> (reseeded with the level seed at
/// construction — the IRng replay contract) and all time through the
/// internal <see cref="SimClock"/> (CS-1.4), so: same seed + same command
/// stream ⇒ same <see cref="ComputeStateHash"/> at every tick
/// (03-decomposition §1.2 — the replay invariant T-040 proves first).
/// <para>
/// Commands enter only through <see cref="Enqueue"/> and are applied at
/// tick boundaries strictly via the subsystem public APIs (BuildingRegistry
/// / GridWorld / EnergyLedger / Producer / AmmoConverter / Carrier / A*) —
/// the orchestrator owns no direct state mutation of foreign internals.
/// </para>
/// <para>
/// Per-tick order (task card): commands → terrain/energy settle → economy
/// (harvest + craft, backpressure inside the subsystems) → logistics
/// (porter ticks along their A* routes) → flow-field manager Update
/// (budgeted slice) → units (wave spawn + flow movement) → combat (weapon
/// tick, deterministic first-nearest targeting, Damage.DirectInt settlement
/// inside Unit.ApplyDamage) → whole-state FNV hash → snapshot publish.
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

    private readonly List<Unit> _units = new();
    private readonly List<SimBuilding> _buildings = new();
    private readonly List<SimTower> _towers = new();
    private readonly List<Producer> _producers = new();
    private readonly List<AmmoConverter> _converters = new();
    private readonly List<Carrier> _carriers = new();
    private readonly Dictionary<EntityId, Producer> _producerById = new();
    private readonly Dictionary<EntityId, AmmoConverter> _converterById = new();
    private readonly Dictionary<EntityId, WeaponFire> _magazineById = new();
    private readonly SimCommandQueue _queue = new();
    private readonly List<SimCommandResult> _results = new();

    private readonly ResourceKey _costCurrency;
    private long _nextBuildingId = WarehouseEntityValue + 1;
    private long _nextCarrierId = CarrierEntityBase;
    private long _laborCampNodes;
    private long _harvesterNodes;
    private long _converterNodes;
    private long _appliedCommands;
    private long _kills;
    private long _shotsLanded;

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
    public Simulation(SimulationConfig config, IRng rng, long seed)
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

        if (config.EconomyBuildingHp < 1)
        {
            throw new ArgumentException(
                $"EconomyBuildingHp must be at least 1 (got {config.EconomyBuildingHp}).", nameof(config));
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
        // IRng replay contract: reseed with the level seed, then the fixed
        // consumption order below (terrain → wave entry cells) makes the
        // stream position a pure function of the command-free prefix.
        rng.Reseed(seed);
        Seed = seed;
        _clock = new SimClock();

        // Rng consumption order #1: terrain (one NextDouble per cell,
        // row-major — GridWorld contract).
        _world = new GridWorld(config.Width, config.Height, rng, config.ForestRatio, config.RockRatio, config.WaterRatio);
        _stock = new ResourceStock(_clock, ResourceStock.E1WarehouseLevel);
        _ledger = new EnergyLedger(_clock);
        _registry = new BuildingRegistry(_world);
        _registry.AttachToWorld();
        _flowGrid = new SimFlowGrid(_registry, _world);
        _flows = new FlowFieldManager(_flowGrid, FlowFieldManager.DefaultMaxActiveFields, config.FlowFrameBudgetMs);
        _laneKey = new FlowFieldKey(config.Lane, 0);
        _flows.Acquire(_laneKey, config.BaseCell);
        _pathfinder = new AStarPathfinder(_flowGrid);
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
            _buildings.Add(new SimBuilding(
                warehouseId, "era1:building-warehouse", config.WarehouseCell, config.EconomyBuildingHp, 1,
                SimBuildingKind.Warehouse));
        }
    }

    /// <summary>Level seed this run was created with (replay key #1).</summary>
    public long Seed { get; }

    /// <summary>Current simulation tick (0 = before the first Step).</summary>
    public long CurrentTick => _clock.Tick;

    /// <summary>Wave number of the installed spawn plan; 0 before wave 1.</summary>
    public int CurrentWave => _spawner.CurrentWave;

    /// <summary>Units ever spawned, dead ones included, in spawn order.</summary>
    public IReadOnlyList<Unit> Units => _units;

    /// <summary>Buildings in placement order (warehouse first, then command-built).</summary>
    public IReadOnlyList<SimBuilding> Buildings => _buildings;

    /// <summary>Weapon towers in placement order.</summary>
    public IReadOnlyList<SimTower> Towers => _towers;

    /// <summary>Harvest buildings in placement order.</summary>
    public IReadOnlyList<Producer> Producers => _producers;

    /// <summary>Ammo converters in placement order.</summary>
    public IReadOnlyList<AmmoConverter> Converters => _converters;

    /// <summary>Porters in assignment order.</summary>
    public IReadOnlyList<Carrier> Carriers => _carriers;

    /// <summary>Shared era resource pool (C-06).</summary>
    public ResourceStock Stock => _stock;

    /// <summary>Shared era energy ledger (C-08).</summary>
    public EnergyLedger Ledger => _ledger;

    /// <summary>The grid world (C-05) — read-only reuse for callers.</summary>
    public GridWorld World => _world;

    /// <summary>Command intake (replay key #2: the command stream).</summary>
    public SimCommandQueue Commands => _queue;

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

    /// <summary>Enqueues one command (applied at its IssuedAtTick).</summary>
    public long Enqueue(SimCommand command) => _queue.Enqueue(command);

    /// <summary>Enqueues a command stream in order.</summary>
    public void EnqueueRange(IEnumerable<SimCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (SimCommand command in commands)
        {
            _queue.Enqueue(command);
        }
    }

    /// <summary>
    /// Advances exactly one tick through the fixed pipeline and publishes
    /// the frame-end snapshot. Repeated calls replay the invariant: same
    /// seed + same commands ⇒ same hash after every tick.
    /// </summary>
    public void Step()
    {
        _clock.Advance();
        long tick = _clock.Tick;

        ApplyDueCommands(tick);

        // 1) Terrain/energy settle: pool satisfaction for this tick (granary
        //    stays unattached in the E1 thin level).
        _ledger.Settle();

        // 2) Economy: harvest into yard piles, craft ammo in converters —
        //    backpressure lives inside the subsystems.
        foreach (Producer producer in _producers)
        {
            producer.Tick();
        }

        foreach (AmmoConverter converter in _converters)
        {
            converter.Tick();
        }

        // 3) Logistics: porters advance one tick along their routes.
        foreach (Carrier carrier in _carriers)
        {
            carrier.Tick();
        }

        // 4) Flow-field slice under the frame budget (design v3.2 §6 8 ms).
        _flows.Update(_config.FlowFrameBudgetMs);

        // 5) Units: wave batches materialize, then everyone walks the live
        //    field gradient (units hold position until the first field is
        //    ready — E1 maps complete inside one slice).
        _units.AddRange(_spawner.Advance());
        if (GetLiveField() is { IsReady: true })
        {
            _movement.StepAll(_units);
        }

        // 6) Combat: weapon cycles tick, then every ready tower fires at
        //    the nearest living target inside range (strictly closer wins,
        //    list order breaks ties — deterministic), settling through
        //    Unit.ApplyDamage → Damage.DirectInt.
        foreach (SimTower tower in _towers)
        {
            tower.Weapon.Tick();
            if (!tower.Weapon.ReadyToFire)
            {
                continue;
            }

            Unit? target = FindTarget(tower);
            if (target is null)
            {
                continue;
            }

            WeaponFireEvent? shot = tower.Weapon.TryFire(target.Id, tick);
            if (shot is null)
            {
                continue;
            }

            int damage = (int)Math.Round(shot.DamagePerShot, MidpointRounding.AwayFromZero);
            _ = target.ApplyDamage(damage, tick);
            _shotsLanded++;
            if (!target.Alive)
            {
                _kills++;
            }
        }

        // 7) Whole-state hash + double-buffered publish.
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
    /// life flags, ticks), every building in placement order, tower weapon
    /// state, applied-command counter and wave number. Dictionary iteration
    /// is never hashed — every list is in a deterministic order.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)_clock.Tick);
        hash = Mix(hash, (ulong)_world.ComputeMapHash());
        hash = Mix(hash, (ulong)_ledger.ComputeStateHash());
        hash = Mix(hash, (ulong)_stock.ComputeStateHash());

        hash = Mix(hash, (ulong)_producers.Count);
        foreach (Producer producer in _producers)
        {
            hash = Mix(hash, (ulong)producer.ComputeStateHash());
        }

        hash = Mix(hash, (ulong)_converters.Count);
        foreach (AmmoConverter converter in _converters)
        {
            hash = Mix(hash, (ulong)converter.ComputeStateHash());
        }

        hash = Mix(hash, (ulong)_carriers.Count);
        foreach (Carrier carrier in _carriers)
        {
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

        hash = Mix(hash, (ulong)_units.Count);
        foreach (Unit unit in _units)
        {
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
            hash = Mix(hash, unit.Alive ? 1UL : 0UL);
            hash = Mix(hash, unit.ArrivedAtTarget ? 1UL : 0UL);
            hash = Mix(hash, (ulong)unit.SpawnTick);
            hash = Mix(hash, (ulong)unit.DeathTick);
        }

        hash = Mix(hash, (ulong)_buildings.Count);
        foreach (SimBuilding building in _buildings)
        {
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

        hash = Mix(hash, (ulong)_towers.Count);
        foreach (SimTower tower in _towers)
        {
            hash = Mix(hash, (ulong)tower.Id.Value);
            hash = Mix(hash, (uint)tower.Weapon.AmmoShotsRemaining);
            hash = Mix(hash, (uint)tower.Weapon.CooldownTicksRemaining);
            hash = Mix(hash, (uint)tower.Weapon.FiredCount);
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
        foreach (SimBuilding entry in _buildings)
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
            string? rejection = ApplyOne(entry.Command);
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
    private string? ApplyOne(SimCommand command)
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

        if (!TryPayCost(spec.CostTotal))
        {
            return "insufficient-resources";
        }

        var id = new EntityId(_nextBuildingId++);
        IBuilding placed = _registry.Place(id, spec, cell, tier: 1);
        var weapon = new WeaponFire(id, spec, initialAmmoShots: 0);
        _towers.Add(new SimTower(id, spec, weapon, placed.Cell));
        _magazineById.Add(id, weapon);
        _buildings.Add(new SimBuilding(id, spec.Id, placed.Cell, spec.Hp, 1, SimBuildingKind.Tower));
        _flows.MarkDirty(new CellRegion(cell.X - 1, cell.Y - 1, 3, 3));
        return null;
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

        if (!TryPayCost(_config.Costs?.LaborCampCostPoints ?? 0.0))
        {
            return "insufficient-resources";
        }

        var id = new EntityId(_nextBuildingId++);
        _world.RegisterOccupancy(id, new[] { cell });
        _ledger.RegisterProducer(new EnergyNodeId(LaborCampNodeBase + _laborCampNodes++), EnergyNodeKind.LaborCamp);
        _buildings.Add(new SimBuilding(
            id, "era1:building-labor-camp", cell, _config.EconomyBuildingHp, 1, SimBuildingKind.LaborCamp));
        return null;
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

        if (!TryPayCost(_config.Costs?.HarvesterCostPoints ?? 0.0))
        {
            return "insufficient-resources";
        }

        var resource = new ResourceKey(resourceElement.GetString()!);
        var id = new EntityId(_nextBuildingId++);
        _world.RegisterOccupancy(id, new[] { cell });
        var producer = new Producer(
            id, resource, seats, tier: 1, _ledger, new EnergyNodeId(HarvesterNodeBase + _harvesterNodes++));
        _producers.Add(producer);
        _producerById.Add(id, producer);
        _buildings.Add(new SimBuilding(
            id, "era1:building-harvester", cell, _config.EconomyBuildingHp, 1, SimBuildingKind.Harvester));
        return null;
    }

    private string? ApplyBuildConverter(JsonElement root)
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

        if (!TryPayCost(_config.Costs?.ConverterCostPoints ?? 0.0))
        {
            return "insufficient-resources";
        }

        var id = new EntityId(_nextBuildingId++);
        _world.RegisterOccupancy(id, new[] { cell });
        var converter = new AmmoConverter(
            _clock, _ledger, new EnergyNodeId(ConverterNodeBase + _converterNodes++), AmmoConverter.E1ArrowRecipe);
        _converters.Add(converter);
        _converterById.Add(id, converter);
        _buildings.Add(new SimBuilding(
            id, "era1:building-arrow-converter", cell, _config.EconomyBuildingHp, 1, SimBuildingKind.Converter));
        return null;
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

        var fromId = new EntityId(fromValue);
        var toId = new EntityId(toValue);

        // Supplier side: harvesters (raw resources) and converters (ammo).
        ResourceKey resource;
        ICargoSupplier supplier;
        if (_producerById.TryGetValue(fromId, out Producer? producer))
        {
            supplier = producer;
            resource = producer.Resource;
        }
        else if (_converterById.TryGetValue(fromId, out AmmoConverter? converter))
        {
            supplier = converter;
            resource = AmmoConverter.E1ArrowRecipe.AmmoResource;
        }
        else
        {
            return $"from-not-a-supplier:{fromValue}";
        }

        // Receiver side: converter hopper, tower magazine or the warehouse
        // pool stand.
        ICargoReceiver receiver;
        if (toValue == WarehouseEntityValue)
        {
            if (_config.WarehouseCell is null)
            {
                return "to-warehouse-absent";
            }

            receiver = new StockDepositPort(_stock);
        }
        else if (_converterById.TryGetValue(toId, out AmmoConverter? hopper))
        {
            receiver = hopper;
        }
        else if (_magazineById.TryGetValue(toId, out WeaponFire? weapon))
        {
            TowerSpec? spec = null;
            foreach (SimTower candidate in _towers)
            {
                if (candidate.Id.Equals(toId))
                {
                    spec = candidate.Spec;
                    break;
                }
            }

            receiver = new TowerMagazinePort(spec!.AmmoItem, weapon);
        }
        else
        {
            return $"to-not-a-receiver:{toValue}";
        }

        if (!TryGetBuilding(fromId, out SimBuilding fromBuilding)
            || !TryGetBuilding(toId, out SimBuilding toBuilding))
        {
            return "endpoint-unknown";
        }

        if (!TryRoute(fromBuilding.Cell, toBuilding.Cell, out IReadOnlyList<CellCoord> route))
        {
            return "no-route";
        }

        var carrierId = new EntityId(_nextCarrierId++);
        _carriers.Add(new Carrier(carrierId, resource, supplier, receiver, route));
        return null;
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

        return null;
    }

    private bool TryPayCost(double points)
    {
        if (points <= 0)
        {
            return true;
        }

        if (_stock.AmountOf(_costCurrency) < points)
        {
            return false;
        }

        return _stock.TryWithdraw(_costCurrency, points) >= points;
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

        PathQueryResult result = _pathfinder.FindPath(fromNeighbour, toNeighbour);
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
        foreach (Unit unit in _units)
        {
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
        var unitViews = new SimUnitView[_units.Count];
        int liveCount = 0;
        foreach (Unit unit in _units)
        {
            if (!unit.Alive)
            {
                continue;
            }

            unitViews[liveCount++] = new SimUnitView(
                unit.Id, unit.ContentId, unit.X, unit.Y, unit.Hp, unit.MaxHp, unit.Armor, unit.Shield,
                unit.SpeedPxPerTick);
        }

        Array.Resize(ref unitViews, liveCount);

        var buildingViews = new SimBuildingView[_buildings.Count];
        for (int i = 0; i < _buildings.Count; i++)
        {
            SimBuilding building = _buildings[i];
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

    /// <summary>
    /// Tower-side magazine port (弹药入塔, F-020 seam): accepts only the
    /// tower's ammo item and tops the weapon's shot reserve up by
    /// ×<see cref="Carrier.ShotsPerItem"/> per accepted item (v3.2 §5).
    /// </summary>
    private sealed class TowerMagazinePort : ICargoReceiver
    {
        private readonly string? _ammoItem;
        private readonly WeaponFire _weapon;

        public TowerMagazinePort(string? ammoItem, WeaponFire weapon)
        {
            _ammoItem = ammoItem;
            _weapon = weapon;
        }

        public int TryAccept(ResourceKey resource, int amount)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Accept amount must be non-negative.");
            }

            if (_ammoItem is null || !string.Equals(resource.Value, _ammoItem, StringComparison.Ordinal))
            {
                return 0;
            }

            _weapon.ReloadShots(amount * Carrier.ShotsPerItem);
            return amount;
        }
    }
}
