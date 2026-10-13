using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Pathfinding;
using EraCore.Sim;
using EraCore.Units;
using EraCore.World;

namespace EraCore.Tests.Spikes;

/// <summary>
/// One in-flight projectile of the spike① ballistic shadow layer (T-043,
/// F-166): conforms to the K-6 <see cref="IProjectile"/> draft seam. The E1
/// thin combat path (T-038/T-040) is hitscan — <c>WeaponFire.TryFire</c>
/// settles damage inside the firing tick and <see cref="Simulation"/> never
/// carries projectiles in flight — so the "1000 活弹并存" budget load of the
/// G8 benchmark is layered here as a measurement shadow: every spawn is
/// driven by a REAL tower firing of the orchestrator (observed via the
/// public <see cref="WeaponFire.FiredCount"/> deltas), and each projectile
/// then pays the per-tick cost a real ballistic layer would pay (homing
/// advance + proximity hit check against live units + lifetime culling).
/// Damage is NOT re-settled here — the sim already settled it exactly once
/// through its own combat path; this layer only measures the flight load.
/// </summary>
internal sealed class Spike1Projectile : IProjectile
{
    private readonly float _speedPxPerTick;
    private readonly float _hitRadiusPx;
    private float _headingX;
    private float _headingY;

    /// <summary>Arms one projectile at a tower cell centre, homing on its target.</summary>
    public Spike1Projectile(
        EntityId id,
        EntityId sourceBuildingId,
        CellCoord towerCell,
        Unit target,
        long tick,
        float speedPxPerTick,
        float hitRadiusPx)
    {
        Id = id;
        SourceBuildingId = sourceBuildingId;
        X = (towerCell.X * Spike1PerfScenario.CellSize) + (Spike1PerfScenario.CellSize / 2f);
        Y = (towerCell.Y * Spike1PerfScenario.CellSize) + (Spike1PerfScenario.CellSize / 2f);
        Target = target;
        SpawnTick = tick;
        _speedPxPerTick = speedPxPerTick;
        _hitRadiusPx = hitRadiusPx;
        _headingX = 0;
        _headingY = -1;
    }

    public EntityId Id { get; }

    public EntityId SourceBuildingId { get; }

    public float X { get; private set; }

    public float Y { get; private set; }

    public EntityId? TargetUnitId => Target?.Id;

    /// <summary>Raw per-shot damage d before decay (v3.2 §6 symbol binding) — 1 in this spike (settlement stays in the sim).</summary>
    public int Damage => 1;

    public long AgeTicks { get; private set; }

    /// <summary>The homing target; null never holds at spawn (towers only fire with a target).</summary>
    internal Unit? Target { get; }

    /// <summary>Spawn tick of the flight (30 Hz domain).</summary>
    internal long SpawnTick { get; }

    /// <summary>Whether the proximity check has settled this projectile.</summary>
    internal bool Hit { get; private set; }

    /// <summary>
    /// Advances one tick: home on the target while it lives (heading
    /// recomputed each tick — pure IEEE 754 float math, replay-stable),
    /// otherwise fly straight along the last heading; then the proximity
    /// hit check against the target centre.
    /// </summary>
    public void Advance()
    {
        if (Target is { Alive: true })
        {
            float dx = Target.X - X;
            float dy = Target.Y - Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length > 1e-6)
            {
                _headingX = (float)(dx / length);
                _headingY = (float)(dy / length);
            }
        }

        X += _headingX * _speedPxPerTick;
        Y += _headingY * _speedPxPerTick;
        AgeTicks++;

        if (Target is { Alive: true })
        {
            float dx = Target.X - X;
            float dy = Target.Y - Y;
            Hit = (dx * dx) + (dy * dy) <= (_hitRadiusPx * _hitRadiusPx);
        }
    }
}

/// <summary>
/// World-only <see cref="IFlowGrid"/> view for the spike① attribution twin
/// (T-043): terrain cost + terrain passability + occupancy, no
/// BuildingRegistry tear prices. Used to run a SECOND
/// <see cref="FlowFieldManager"/> beside the simulation's internal one so
/// the flow-field slice of the tick can be timed from outside —
/// <see cref="Simulation.Step"/> has no segment hooks (the coarse-wrapping
/// limitation the task card allows). In the steady window both managers see
/// the identical state (nothing dirty, nothing pending), so the twin's
/// Update cost equals the sim's internal slice; during tower placement the
/// twin receives the same 3×3 dirty regions the sim's own
/// <c>ApplyBuildTower</c> issues, so rebuild costs are represented too.
/// </summary>
internal sealed class Spike1WorldFlowView : IFlowGrid
{
    private readonly GridWorld _world;

    public Spike1WorldFlowView(GridWorld world)
    {
        _world = world;
    }

    public int Width => _world.Width;

    public int Height => _world.Height;

    public int Cost(CellCoord cell) => TerrainInfo.Cost(_world.TerrainAt(cell));

    public bool Passable(CellCoord cell) =>
        TerrainInfo.IsPassable(_world.TerrainAt(cell)) && !_world.IsOccupied(cell);

    public int BloodCost(CellCoord cell) => 0;
}

/// <summary>
/// T-043 spike① performance-gate benchmark world (F-166, 13-quality-gates
/// G8 "300 敌+1000 弹迷宫复合"): a deterministic 128×128 maze composite —
/// 2-thick border ring + six untearable Wall-terrain serpentine columns
/// (three west of the base band, three east) with alternating top/bottom
/// gaps — with the player base at the map centre. The stress vehicle is the
/// REAL <see cref="Simulation"/> orchestrator (T-040): waves enter through
/// <see cref="WaveSpawner"/>, enemies descend the live flow field, and 100
/// towers placed through the real command stream
/// (<c>build-tower</c> → cost payment → registry placement → flow-field
/// dirty marking) fire through the real <see cref="WeaponFire"/> combat
/// path. Live-enemy steady state (≥300 through the whole window) is held by
/// wave sizing (170+170 across two waves, near-zero kill rate by hp
/// headroom); the 1000-live-projectile load is the
/// <see cref="Spike1Projectile"/> shadow layer fed by the towers' real fire
/// events (spawn rate = towers/reload = 100/2 = 50 shots/tick; steady-state
/// live count = spawn rate × mean time-in-flight ≈ 50 × ~20 ticks ≈ 1000 —
/// the exact parameters and their derivation are archived in
/// docs/engineering/spike1-report.md §2).
/// <para>
/// All knobs are spike-local stress parameters ([NEW-原型校准], CS-2.3 does
/// not bind test-side fixtures): enemy speeds are compressed above the E1
/// content band (3~7 px/tick vs 1.6~2.56) so the arrival pile at the base
/// forms inside a bounded harness runtime — per-tick simulation cost is
/// independent of speed; tower damage 1 / enemy hp 2600×3.0 keep the kill
/// rate ~0 so the in-field count does not decay below the 300 line.
/// </para>
/// </summary>
internal static class Spike1PerfScenario
{
    public const int Size = 128;

    public const int CellSize = 32;

    public const long Seed = 20261015;

    /// <summary>Flow-field target = player base, map centre of the base band [49..79].</summary>
    public static readonly CellCoord Base = new(64, 64);

    /// <summary>Serpentine Wall columns, west trio + east trio of the base band.</summary>
    public static readonly int[] SerpentineColumns = { 16, 32, 48, 80, 96, 112 };

    /// <summary>Columns whose wall opens at the TOP (y 2..9); the others open at the bottom (y 118..125).</summary>
    public static readonly int[] TopGapColumns = { 16, 48, 96 };

    /// <summary>Number of stress towers ringing the base (Chebyshev 4..8, north gate kept clear).</summary>
    public const int TowerCount = 100;

    /// <summary>Tower fire cycle: every tick, half of all towers fire (100/2 = 50 shots/tick).</summary>
    public const int TowerReloadTicks = 2;

    /// <summary>First tower command tick; one placement every <see cref="TowerPlacementSpacingTicks"/> ticks.</summary>
    public const int TowerPlacementStartTick = 200;

    public const int TowerPlacementSpacingTicks = 6;

    /// <summary>Measurement window: 1800 ticks = 60 s @ 30 Hz, starting at the second wave's entry (t=1980).</summary>
    public const int WindowStartTick = 1980;

    public const int WindowTicks = 1800;

    /// <summary>Total harness ticks: the third wave (t=3780) never enters the run.</summary>
    public const long TotalTicks = WindowStartTick + WindowTicks;

    /// <summary>Projectile flight parameters: equilibrium ≈ 50 spawns/tick × ~20 ticks in flight ≈ 1000 live.</summary>
    public const float ProjectileSpeedPxPerTick = 9f;

    public const float ProjectileHitRadiusPx = 10f;

    public const int ProjectileLifetimeTicks = 32;

    /// <summary>Stress tower spec id (spike-local catalog, real <see cref="WeaponFire"/> cycle).</summary>
    public const string TowerId = "spike1:arrow-hedge";

    /// <summary>
    /// Maze-ratio probe: the entry-block corner NEAREST the base (tightest
    /// straight line of all spawn cells) — the 迷宫化 assertion is measured
    /// from the easiest spawn so the ratio cannot be inflated by picking a
    /// far corner.
    /// </summary>
    public static readonly CellCoord PathProbe = new(28, 106);

    /// <summary>Enemy entry cells: south end of the band between columns 16 and 32.</summary>
    public static IReadOnlyList<CellCoord> EntryCells()
    {
        var cells = new List<CellCoord>();
        for (int y = 106; y <= 118; y++)
        {
            for (int x = 20; x <= 28; x++)
            {
                cells.Add(new CellCoord(x, y));
            }
        }

        return cells;
    }

    /// <summary>
    /// Carves the maze into the simulation's world. Must run after the
    /// <see cref="Simulation"/> constructor (which acquires the flow-field
    /// bucket but builds no field yet) and before the first
    /// <c>Step()</c>, so the first field build already sees the maze.
    /// Terrain mutation goes through the public <see cref="GridWorld"/>
    /// API only; replay determinism of this perf harness is not asserted
    /// (T-040 owns that proof on command-stream-only worlds).
    /// </summary>
    public static void CarveMaze(GridWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // 2-thick border ring reaching the map edge (a 1-thick ring would
        // leave a sealed-but-walkable band and flip the field into
        // WallTear mode — spike② lesson).
        for (int x = 0; x < Size; x++)
        {
            SetWall(world, x, 0);
            SetWall(world, x, 1);
            SetWall(world, x, Size - 2);
            SetWall(world, x, Size - 1);
        }

        for (int y = 0; y < Size; y++)
        {
            SetWall(world, 0, y);
            SetWall(world, 1, y);
            SetWall(world, Size - 2, y);
            SetWall(world, Size - 1, y);
        }

        foreach (int col in SerpentineColumns)
        {
            bool topGap = TopGapColumns.Contains(col);
            for (int y = 2; y <= Size - 3; y++)
            {
                if (topGap && y <= 9)
                {
                    continue;
                }

                if (!topGap && y >= Size - 10)
                {
                    continue;
                }

                SetWall(world, col, y);
            }
        }
    }

    /// <summary>
    /// The 100 tower cells: deterministic row-major scan of the Chebyshev
    /// 4..8 ring around the base, minus the north gate corridor
    /// (|x−64| ≤ 6, y &lt; 64) the enemies funnel through on their final
    /// approach. Ring geometry keeps every tower inside weapon range of the
    /// arrival pile that accumulates on the base cell.
    /// </summary>
    public static List<CellCoord> TowerCells()
    {
        var cells = new List<CellCoord>(TowerCount);
        for (int y = Base.Y - 8; y <= Base.Y + 8 && cells.Count < TowerCount; y++)
        {
            for (int x = Base.X - 8; x <= Base.X + 8 && cells.Count < TowerCount; x++)
            {
                int chebyshev = Math.Max(Math.Abs(x - Base.X), Math.Abs(y - Base.Y));
                if (chebyshev < 4 || chebyshev > 8)
                {
                    continue;
                }

                if (y < Base.Y && Math.Abs(x - Base.X) <= 6)
                {
                    continue; // north gate stays tower-free
                }

                cells.Add(new CellCoord(x, y));
            }
        }

        return cells;
    }

    /// <summary>
    /// The stress level config. Wave arithmetic (all through the frozen
    /// T-036 formula library, never re-implemented here): threat 2.55 ⇒
    /// count multiplier 4.25, hp multiplier 3.0, interval multiplier 0.5
    /// (60 s); BaseCount 40 with the ramp parked above the run
    /// (BeginWave 99) ⇒ floor(clamp(40)×4.25) = 170 per wave. Grace 0.1
    /// (below the F-034 product band — spike stress knob, archived in the
    /// report) puts wave 1 at t=180 and wave 2 (the window's reinforcement
    /// wave) at t=1980; the interval floor keeps wave 3 (t=3780) outside
    /// the run. Single batch per wave: the whole 170 materializes on the
    /// wave's first tick (the spawn spike itself is part of the load).
    /// </summary>
    public static SimulationConfig BuildConfig()
    {
        return new SimulationConfig(
            Width: Size,
            Height: Size,
            ForestRatio: 0.0,
            RockRatio: 0.0,
            WaterRatio: 0.0,
            BaseCell: Base,
            EntryCells: EntryCells(),
            TowerCatalog: TowerCatalog(),
            Enemies: EnemyCatalog(),
            Waves: new WaveSpawnConfig(
                BaseCount: 40,
                BeginWave: 99,
                Spacing: 1,
                Scaling: 1.0,
                ThreatLevel: 2.55,
                RegularRoster: new[]
                {
                    "spike1:sprinter-3", "spike1:sprinter-4", "spike1:sprinter-5",
                    "spike1:sprinter-6", "spike1:sprinter-7",
                },
                EliteLineEnabled: false,
                EliteEnemyId: null,
                BossWave: 20,
                BossEnemyId: null,
                FirstWaveGraceMultiplier: 0.1,
                ValleySeconds: 15.0,
                BatchesPerWave: 1,
                TickRateHz: 30,
                CellSizePx: CellSize,
                EntryCells: EntryCells()),
            InitialStock: new List<KeyValuePair<ResourceKey, double>>
            {
                new(new ResourceKey("era1:res-wood"), 500.0),
            },
            WarehouseCell: null,
            Costs: new BuildCostPolicy(new ResourceKey("era1:res-wood")),
            FlowFrameBudgetMs: FlowFieldManager.DefaultFrameBudgetMs,
            Lane: 0,
            EconomyBuildingHp: 1);
    }

    /// <summary>
    /// Spike-local stress tower (Pierce hedge around the base): real weapon
    /// cycle (reload 2, range 9 cells covers the arrival pile from the
    /// Chebyshev 4..8 ring), damage 1 per shot so the near-zero kill rate
    /// keeps ≥300 enemies in field, no ammo logistics (AmmoPerShot 0 — the
    /// supply chain is spike②'s scope, not this benchmark's).
    /// </summary>
    public static IReadOnlyList<TowerSpec> TowerCatalog() => new[]
    {
        new TowerSpec(
            Id: TowerId,
            Class: TowerClass.Pierce,
            Tier: 1,
            MaxTier: 1,
            CostTotal: 1,
            Dps: 1 * 30.0 / TowerReloadTicks,
            DamagePerShot: 1,
            ReloadTicks: TowerReloadTicks,
            RangeCells: 9,
            Hp: 200,
            AmmoItem: null,
            AmmoPerShot: 0,
            TierGrowth: null,
            Traits: null),
    };

    /// <summary>
    /// Spike-local enemy roster parsed through the production
    /// <see cref="EnemySpecCatalog.Parse"/> (strict schema, K-2): five
    /// species differing only in speed (3/4/5/6/7 px per tick). The spread
    /// stretches each wave's single batch along the serpentine so fast
    /// groups form the arrival pile at the base while slow groups keep the
    /// maze flowing; hp 2600 × threat hp multiplier 3.0 = 7800 effective
    /// keeps the kill rate near zero against 1-damage shots.
    /// </summary>
    public static EnemySpecCatalog EnemyCatalog() => EnemySpecCatalog.Parse(
        """
        {
          "content_type": "Enemy",
          "tick_rate_hz": 30,
          "cell_size_px": 32,
          "items": [
            { "content_type": "Enemy", "id": "spike1:sprinter-3", "sp_id": "sp1_s3", "name_zh": "压测三速", "hp": 2600, "armor": 0, "speed_px_per_tick": 3.0, "canvas_size_px": 32 },
            { "content_type": "Enemy", "id": "spike1:sprinter-4", "sp_id": "sp1_s4", "name_zh": "压测四速", "hp": 2600, "armor": 0, "speed_px_per_tick": 4.0, "canvas_size_px": 32 },
            { "content_type": "Enemy", "id": "spike1:sprinter-5", "sp_id": "sp1_s5", "name_zh": "压测五速", "hp": 2600, "armor": 0, "speed_px_per_tick": 5.0, "canvas_size_px": 32 },
            { "content_type": "Enemy", "id": "spike1:sprinter-6", "sp_id": "sp1_s6", "name_zh": "压测六速", "hp": 2600, "armor": 0, "speed_px_per_tick": 6.0, "canvas_size_px": 32 },
            { "content_type": "Enemy", "id": "spike1:sprinter-7", "sp_id": "sp1_s7", "name_zh": "压测七速", "hp": 2600, "armor": 0, "speed_px_per_tick": 7.0, "canvas_size_px": 32 }
          ]
        }
        """);

    private static void SetWall(GridWorld world, int x, int y)
    {
        world.SetTerrain(new CellCoord(x, y), TerrainKind.Wall);
    }
}

/// <summary>
/// One run's sampled metrics of the spike① benchmark (T-043): per-tick
/// timings of the full simulation <c>Step()</c> and of the projectile
/// shadow layer, the flow-field attribution twin, scenario integrity
/// counters and the coarse segment micro-samples. Pure value record.
/// </summary>
internal sealed record Spike1RunMetrics(
    int RunIndex,
    double SimMeanMs,
    double SimP95Ms,
    double SimP99Ms,
    double SimMaxMs,
    double ProjMeanMs,
    double ProjMaxMs,
    double FrameMeanMs,
    double FrameP95Ms,
    double FrameP99Ms,
    double FrameMaxMs,
    double FlowWindowMeanMs,
    double FlowWindowMaxMs,
    double FlowSliceMaxMs,
    double FlowInitialBuildMs,
    int FlowRebuildCount,
    double FlowRebuildMeanMs,
    double FlowRebuildMaxMs,
    int LiveEnemiesMin,
    double LiveEnemiesMean,
    int MoversMin,
    int ArrivedAtWindowEnd,
    int LiveProjMin,
    double LiveProjMean,
    int LiveProjMax,
    long ShotsLandedInWindow,
    long ShotsLandedTotal,
    long KillsTotal,
    int TowersPlaced,
    long CommandsApplied,
    double MazePathRatio,
    int MazePathCells,
    double HashMeanMs,
    double MoveTwinMeanMs,
    double ScanTwinMeanMs);
