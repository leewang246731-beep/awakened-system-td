using System.Text;
using EraCore.Agent.Governance;
using EraCore.Agent.Grammar;
using EraCore.Agent.Pipeline;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Economy;
using EraCore.IO;
using EraCore.Presentation;
using EraCore.Sim;
using EraCore.Units;
using Godot;

namespace EraTD;

// T-055 (C-21/C-22): the REAL simulation host behind the scene — this class
// is the Wave-2 call-site replacement of the retired mock producer (the
// former DeterministicMockSnapshotSource): one live C-12 Simulation built
// from repo content (era1 towers/enemies + the L1 grammar books), the T-053
// five-stage AgentPipeline bound over it, and the two bridge ports exposed
// as adapters. Everything below mirrors the proven T-053 harness wiring
// (EraCore.Tests/Agent/Pipeline/PipelineTestHarness: same seed, anchors,
// agent era 2 so system points are enabled, initial wood grant of 200).

/// <summary>
/// Dev-run composition root: content loading (repo walk-up, mirrors the
/// EraCore.Tests loaders), the E1 level config with the frozen T-040
/// anchors, the milestone build stream that feeds the HUD real figures,
/// and the HUD metric extraction. The real level loader (C-29/T-059)
/// replaces the config assembly later; the HUD extraction stays.
/// </summary>
internal sealed class SimHost
{
    /// <summary>E1 fixture seed (SimE1Fixture.Seed anchor, T-040).</summary>
    internal const long Seed = 20261014;

    /// <summary>Agent-governance era ordinal 2: system points enabled, ceiling 5 (T-053 harness anchor).</summary>
    internal const int AgentEra = 2;

    /// <summary>E1 wood resource id (content era1:res-wood).</summary>
    internal static readonly ResourceKey Wood = new("era1:res-wood");

    /// <summary>east_gate build anchor — the HoldRegion ring scan starts here (T-053 anchor).</summary>
    internal static readonly CellCoord EastGateAnchor = new(12, 12);

    /// <summary>south_gate build anchor (T-053 anchor).</summary>
    internal static readonly CellCoord SouthGateAnchor = new(4, 8);

    private static readonly CellCoord WarehouseCell = new(2, 14);
    private static readonly CellCoord LaborCampCell = new(4, 20);
    private static readonly CellCoord HarvesterCell = new(6, 18);
    private static readonly CellCoord ConverterCell = new(9, 18);
    private static readonly CellCoord TowerCell = new(6, 21);
    private static readonly CellCoord Harvester2Cell = new(4, 16);
    private static readonly CellCoord Tower2Cell = new(9, 21);

    /// <summary>War-report JSONL path inside the D-03 data root (dev store).</summary>
    internal string JournalPath { get; }

    internal Simulation Simulation { get; }

    internal AgentPipeline Pipeline { get; }

    internal ISimSnapshotSource SnapshotSource { get; }

    internal IFramebufferProvider Framebuffer { get; }

    /// <summary>
    /// D-03 store the panel settings persist through (T-075, F-097 关闭系统
    /// 开关跨运行记忆) — the same dev root the war report lands in.
    /// </summary>
    internal IFileStore Store { get; }

    /// <summary>
    /// The live trust model the badge tier follows (T-075, F-111/§11.4).
    /// Nothing in the E1 panel mutates it yet — alarm/advice outcomes wire in
    /// their own tasks; the presenter only reads <see cref="TrustModel.Score"/>.
    /// </summary>
    internal TrustModel Trust { get; } = new();

    private readonly EnemySpecCatalog _enemies;
    private readonly WaveSpawnConfig _waveConfig;
    private readonly WaveSpawner _previewSpawner;

    /// <exception cref="InvalidOperationException">Repo content directory not found (dev runs only).</exception>
    internal SimHost()
    {
        string era1Dir = FindContentDir(
            System.IO.Path.Combine("game", "content", "era1"), "game/content/era1");
        string grammarDir = FindContentDir(
            System.IO.Path.Combine("game", "content", "grammar"), "game/content/grammar");

        _enemies = EnemySpecCatalog.Parse(ReadFile(era1Dir, "enemies.json"));
        _waveConfig = BuildWaveConfig();
        // Read-only projection spawner for the next-wave preview (T-075):
        // PlanWave/WaveStartTick are pure schedule math and never consume the
        // rng or clock, so a private instance cannot perturb live determinism.
        _previewSpawner = new WaveSpawner(_enemies, _waveConfig, new SplitMix64Rng(Seed), new FixedTickClock());

        SimulationConfig config = BuildConfig(era1Dir);

        Simulation = new Simulation(config, new SplitMix64Rng(Seed), Seed);

        var store = new DevFileStore();
        Store = store;
        JournalPath = System.IO.Path.Combine(
            store.Root, "reports", "t055-panel-war-report.jsonl");
        var journal = new JsonlJournalWriter(JournalPath);

        var parser = L1IntentParser.FromJson(
            ReadFile(grammarDir, "patterns-zh.json"),
            ReadFile(grammarDir, "patterns-en.json"),
            ReadFile(grammarDir, "lexicon.json"),
            ReadFile(era1Dir, "towers.json"));

        Pipeline = AgentPipeline.OverSimulation(
            Simulation,
            config,
            parser,
            AgentEra,
            journal,
            new GodotLogSink(),
            new Dictionary<string, CellCoord>
            {
                ["east_gate"] = EastGateAnchor,
                ["south_gate"] = SouthGateAnchor,
            });
        Pipeline.Ledger.BeginWave(1, isBossWave: false);

        SnapshotSource = new LiveSnapshotSource(Simulation);
        Framebuffer = new TerrainFramebuffer(Simulation, version: 1L);

        // The T-040 milestone stream: manpower chain, ammo chain, pool
        // delivery and two towers — it gives the HUD real producers and a
        // real defense line within ~20 s of sim time (last command tick 600).
        Simulation.EnqueueRange(MilestoneCommands());
    }

    /// <summary>Advances the real simulation by one 30 Hz tick (publishes a snapshot per tick).</summary>
    internal void Step() => Simulation.Step();

    /// <summary>
    /// Extracts the HUD three metrics from the live simulation (F-111):
    /// wave counter, summed effective producer output and the tower-line HP
    /// ratio. Pure extraction — every decision after this lives in
    /// <see cref="EraCore.Presentation.HudMetrics"/> (EraCore, tested).
    /// </summary>
    internal HudSnapshot BuildHudSnapshot()
    {
        double production = 0d;
        foreach (EraCore.Economy.Producer producer in Simulation.Producers)
        {
            production += producer.OutputItemsPerSecond * producer.Efficiency;
        }

        var towerHealths = new List<(int Hp, int MaxHp)>();
        foreach (SimTower tower in Simulation.Towers)
        {
            if (Simulation.TryGetBuilding(tower.Id, out SimBuilding building))
            {
                towerHealths.Add((building.Hp, tower.Spec.Hp));
            }
        }

        return HudMetrics.Compute(Simulation.CurrentWave, production, towerHealths);
    }

    /// <summary>
    /// Extracts the FULL third HUD metric (T-075, F-111 全口径 — DEBT v1.5 F8
    /// 清账): current-wave composition aggregated from the live snapshot's
    /// enemy units, plus the next-wave preview projected read-only from the
    /// wave schedule. Pure extraction — grouping/ordering/formatting decisions
    /// live in <see cref="EraCore.Presentation.WaveComposition"/> (tested).
    /// Preview ordinal rule (frozen): current wave + 1, clamped up to 1 for
    /// the pre-first-wave build phase.
    /// </summary>
    internal WaveCompositionView BuildWaveCompositionView()
    {
        int currentWave = Simulation.CurrentWave;
        System.Collections.Generic.IReadOnlyList<IUnit> units =
            Simulation.Snapshots.Latest?.Units ?? Array.Empty<IUnit>();
        var enemies = units
            .Where(unit => _enemies.TryGet(unit.ContentId, out _))
            .Select(unit => (unit.ContentId, unit.Armor));
        IReadOnlyList<WaveCompositionRow> current = WaveComposition.Aggregate(enemies);

        int previewWave = Math.Max(1, currentWave + 1);
        WavePlan plan = _previewSpawner.PlanWave(
            previewWave, _previewSpawner.WaveStartTick(previewWave));
        IReadOnlyList<WaveCompositionRow> preview = WaveComposition.Preview(
            _waveConfig.RegularRoster,
            plan.RegularCount,
            _waveConfig.EliteLineEnabled ? _waveConfig.EliteEnemyId : null,
            plan.EliteCount,
            plan.HasBoss ? _waveConfig.BossEnemyId : null,
            id => _enemies.Get(id).Armor);
        return new WaveCompositionView(currentWave, current, previewWave, preview);
    }

    /// <summary>Chinese display name of an enemy content id (falls back to the id).</summary>
    internal string NameOfEnemy(string id) =>
        _enemies.TryGet(id, out EnemySpec? spec) ? spec.NameZh : id;

    /// <summary>War-report digest for the smoke line (report bubble evidence).</summary>
    internal string JournalDigest()
    {
        if (!System.IO.File.Exists(JournalPath))
        {
            return "no-journal";
        }

        long lines = 0;
        // Read sharing must include Write: the journal writer keeps the file
        // open for the whole run (JsonlJournalWriter), so a plain StreamReader
        // (share=Read) would collide with it.
        using var stream = new System.IO.FileStream(
            JournalPath, System.IO.FileMode.Open, System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is not null)
        {
            lines++;
        }

        return $"jsonl_lines={lines} path={JournalPath}";
    }

    private SimulationConfig BuildConfig(string era1Dir)
    {
        var towers = TowerSpec.LoadCollection(ReadFile(era1Dir, "towers.json"));

        // Frozen T-040 E1 anchors (SimE1Fixture.Config): 16×24 waterless
        // map, one attack lane, top-edge entries, base on the bottom edge,
        // 200 wood grant (milestone stream + one HoldRegion order with
        // slack — the T-053 harness grant).
        return new SimulationConfig(
            Width: 16,
            Height: 24,
            ForestRatio: 0.10,
            RockRatio: 0.05,
            WaterRatio: 0.0,
            BaseCell: new CellCoord(8, 23),
            EntryCells: new[]
            {
                new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1),
            },
            TowerCatalog: towers,
            Enemies: _enemies,
            Waves: _waveConfig,
            InitialStock: new List<KeyValuePair<ResourceKey, double>> { new(Wood, 200.0) },
            WarehouseCell: WarehouseCell,
            Costs: new BuildCostPolicy(Wood));
    }

    /// <summary>
    /// The E1 wave schedule (T-040 anchors) as one shared value: the live
    /// simulation and the T-075 next-wave projection spawner both consume it,
    /// so the HUD preview can never drift from what will actually spawn.
    /// </summary>
    private static WaveSpawnConfig BuildWaveConfig() => new(
        BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 1.2,
        RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
        BatchesPerWave: 4,
        EntryCells: new[]
        {
            new CellCoord(8, 0), new CellCoord(6, 0), new CellCoord(10, 0), new CellCoord(2, 1),
        });

    /// <summary>The canonical T-040 milestone command stream (SimE1Fixture.MilestoneCommands anchors).</summary>
    private static IReadOnlyList<SimCommand> MilestoneCommands() => new[]
    {
        BuildJson(60, "build-labor-camp", LaborCampCell),
        BuildHarvesterJson(120, HarvesterCell),
        BuildJson(180, "build-converter", ConverterCell),
        BuildTowerJson(240, TowerCell),
        AssignCarrierJson(300, from: 3, to: 4),   // harvester → converter (wood)
        AssignCarrierJson(360, from: 4, to: 5),   // converter → tower1 (arrows)
        BuildHarvesterJson(420, Harvester2Cell),
        AssignCarrierJson(480, from: 6, to: 1),   // harvester2 → warehouse (pool)
        BuildTowerJson(540, Tower2Cell),
        AssignCarrierJson(600, from: 4, to: 7),   // converter → tower2 (arrows)
    };

    private static string CellJson(CellCoord cell) =>
        "{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}";

    private static SimCommand BuildJson(long tick, string verb, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"" + verb + "\",\"cell\":" + CellJson(cell) + "}");

    private static SimCommand BuildTowerJson(long tick, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-tower\",\"tower\":\"era1:tower-wood-arrow\",\"cell\":" + CellJson(cell) + "}");

    private static SimCommand BuildHarvesterJson(long tick, CellCoord cell) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"build-harvester\",\"resource\":\"" + Wood.Value + "\",\"seats\":1,\"cell\":" + CellJson(cell) + "}");

    private static SimCommand AssignCarrierJson(long tick, long from, long to) => new(
        tick, SimCommandKind.PlayerBuild,
        "{\"verb\":\"assign-carrier\",\"from\":" + from + ",\"to\":" + to + "}");

    private static string ReadFile(string dir, string fileName) =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(dir, fileName));

    /// <summary>Repo walk-up locator (RL9-safe: relative probes only, mirrors the EraCore.Tests loaders).</summary>
    private static string FindContentDir(string relative, string label)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            $"content directory {label} not found above the executable (dev runs need the repo checkout)");
    }

    /// <summary>
    /// Frozen-tick clock for the projection spawner (T-075): PlanWave and
    /// WaveStartTick never read the clock, so a constant 0 keeps the preview
    /// instance pure while satisfying the WaveSpawner constructor contract.
    /// </summary>
    private sealed class FixedTickClock : IClock
    {
        public long Tick => 0;

        public long NowMs => 0;
    }

    /// <summary>
    /// ISimSnapshotSource adapter over the REAL double-buffered producer
    /// (K-6 seam): <see cref="Simulation.Snapshots"/>.Latest — replaces the
    /// retired mock at the T-042 bridge call sites.
    /// </summary>
    private sealed class LiveSnapshotSource(Simulation simulation) : ISimSnapshotSource
    {
        public SimSnapshot? Latest => simulation.Snapshots.Latest;
    }

    /// <summary>
    /// IFramebufferProvider adapter: E1 terrain (static per level) mapped
    /// onto the atlas palette colors the ground MultiMesh already knows
    /// (TerrainAtlas.TileIndexForColor). The version never bumps in the E1
    /// thin level — terrain is immutable — so the ground layer rebuilds
    /// exactly once, on the first frame.
    /// </summary>
    private sealed class TerrainFramebuffer(Simulation simulation, long version) : IFramebufferProvider
    {
        public int WidthCells => simulation.World.Width;

        public int HeightCells => simulation.World.Height;

        public long FramebufferVersion => version;

        public int ColorAt(int x, int y)
        {
            if (x < 0 || x >= WidthCells)
            {
                throw new ArgumentException($"cell X {x} outside grid [0,{WidthCells}).", nameof(x));
            }

            if (y < 0 || y >= HeightCells)
            {
                throw new ArgumentException($"cell Y {y} outside grid [0,{HeightCells}).", nameof(y));
            }

            return simulation.World.TerrainAt(new CellCoord(x, y)) switch
            {
                EraCore.World.TerrainKind.Forest => 0x36523C, // atlas slot 1 (floor-dirt-pebbled)
                EraCore.World.TerrainKind.Rock => 0x2A4430,   // atlas slot 3 (floor-rubble)
                EraCore.World.TerrainKind.Water => 0x4A6648,  // atlas slot 7 (water-pool)
                EraCore.World.TerrainKind.Wall => 0x405C42,   // atlas slot 2 (floor-dirt-mottled)
                _ => 0x2E4A34,                                // atlas slot 0 (floor-dirt) — Plain/Road
            };
        }
    }

    /// <summary>
    /// Diagnostic sink (K-1 ILogSink) routing registry diagnostics to the
    /// Godot console. The war report itself is the K-8 JSONL journal — this
    /// sink never substitutes for it (RL10).
    /// </summary>
    private sealed class GodotLogSink : ILogSink
    {
        public void Write(LogLevel level, long tick, string message) =>
            GD.Print($"[agent:{level.ToString().ToLowerInvariant()}] tick={tick} {message}");
    }

    /// <summary>
    /// SplitMix64 IRng seam implementation for the live host (K-1 port; the
    /// same stream the Sim tests use — same seed ⇒ same terrain/waves).
    /// </summary>
    private sealed class SplitMix64Rng(long seed) : IRng
    {
        private ulong _state = (ulong)seed;

        public int Next(int minValue, int maxValue)
        {
            if (minValue >= maxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(maxValue));
            }

            long span = (long)maxValue - minValue;
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

        public void Reseed(long newSeed) => _state = (ulong)newSeed;
    }
}
