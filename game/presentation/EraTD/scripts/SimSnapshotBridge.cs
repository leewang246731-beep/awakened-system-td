using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Presentation;

namespace EraTD;

// T-042 (C-21): the presentation-side snapshot adapter layer.
//
// Replacement plan for the mock data source: DeterministicMockSnapshotSource
// is a PRESENTATION DEVELOPMENT FIXTURE only — 03-decomposition granted C-21
// the right to build against a mock producer before C-12 lands ("mock 快照
// 可提前动工"). From Wave 2 (C-12 minimal orchestration, T-040/T-053) the
// fixture is replaced by the real double-buffered SimSnapshot producer; only
// the two ports below (ISimSnapshotSource, IFramebufferProvider) survive —
// the renderer skeleton and the dirty-chunk path stay untouched. The RED
// LINE (CS-3.4, RL2) is mocking the system under test; what is under test
// here is the render skeleton, so the fixture does not violate it.

/// <summary>
/// Port: read-only view of the latest double-buffered <see cref="SimSnapshot"/>
/// (K-6 producer seam). The Godot layer polls this every render frame and
/// never mutates simulation state through it (03-decomposition §1.2-3
/// snapshot principle).
/// </summary>
public interface ISimSnapshotSource
{
    /// <summary>Latest published snapshot, or null before the first tick.</summary>
    SimSnapshot? Latest { get; }
}

/// <summary>
/// Port: ground-layer cell color view (terrain + floor tint) feeding the
/// chunked MultiMesh ground renderer. Cell colors change rarely (terrain
/// edit, building placement), which is why the ground layer is driven by a
/// coarse <see cref="IFramebufferProvider.FramebufferVersion"/> bump rather
/// than per-cell diffs.
/// </summary>
public interface IFramebufferProvider
{
    /// <summary>Ground grid width in cells.</summary>
    int WidthCells { get; }

    /// <summary>Ground grid height in cells.</summary>
    int HeightCells { get; }

    /// <summary>Monotonic content version; bump ⇒ renderer rebuilds all chunks.</summary>
    long FramebufferVersion { get; }

    /// <summary>Cell color as 0xRRGGBB at cell (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <exception cref="ArgumentException">Cell outside the grid.</exception>
    int ColorAt(int x, int y);
}

/// <summary>
/// MOCK DATA SOURCE (presentation development fixture — see the replacement
/// plan in the file header). Generates a deterministic fake world from a
/// fixed seed: a stable per-cell color field and N units that advance along
/// straight, bounce-bounded lanes. Same seed ⇒ same snapshots, so the smoke
/// run and future gdUnit scene tests are reproducible.
/// </summary>
public sealed class DeterministicMockSnapshotSource : ISimSnapshotSource, IFramebufferProvider
{
    public const long DefaultSeed = 20261042L;

    private static readonly int[] Palette = { 0x2E4A34, 0x36523C, 0x405C42, 0x2A4430, 0x4A6648 };

    private readonly int[] _cellColors;
    private readonly (float Vx, float Vy)[] _velocities;
    private readonly float[] _x;
    private readonly float[] _y;
    private readonly int[] _hp;
    private readonly int[] _maxHp;
    private readonly long _seed;

    public DeterministicMockSnapshotSource(
        long seed, int widthCells, int heightCells, int unitCount)
    {
        if (widthCells <= 0 || heightCells <= 0)
        {
            throw new ArgumentException($"grid must be positive, got {widthCells}x{heightCells}.");
        }

        if (unitCount < 0)
        {
            throw new ArgumentException($"unit count must be >= 0, got {unitCount}.");
        }

        _seed = seed;
        WidthCells = widthCells;
        HeightCells = heightCells;

        var rng = new MockLcgRng(seed);

        // Stable color field — generated once, never changes afterwards, so
        // the ground MultiMesh rebuilds exactly once (first frame).
        _cellColors = new int[widthCells * heightCells];
        for (int i = 0; i < _cellColors.Length; i++)
        {
            _cellColors[i] = Palette[rng.Next(0, Palette.Length)];
        }

        // N units with seed-fixed spawn positions, lane velocities and HP.
        float maxPxX = widthCells * SimSnapshotBridge.CellSizePx;
        float maxPxY = heightCells * SimSnapshotBridge.CellSizePx;
        _velocities = new (float, float)[unitCount];
        _x = new float[unitCount];
        _y = new float[unitCount];
        _hp = new int[unitCount];
        _maxHp = new int[unitCount];
        for (int i = 0; i < unitCount; i++)
        {
            _x[i] = (float)(rng.NextDouble() * maxPxX);
            _y[i] = (float)(rng.NextDouble() * maxPxY);
            float speed = 0.75f + (float)rng.NextDouble() * 1.75f; // 0.75..2.5 px/tick
            double angle = rng.NextDouble() * Math.PI * 2.0;
            _velocities[i] = ((float)Math.Cos(angle) * speed, (float)Math.Sin(angle) * speed);
            _maxHp[i] = 60 + rng.Next(0, 61);
            _hp[i] = _maxHp[i];
        }

        Latest = BuildSnapshot();
    }

    public int WidthCells { get; }

    public int HeightCells { get; }

    public long FramebufferVersion { get; private set; }

    public SimSnapshot? Latest { get; private set; }

    /// <summary>Advances the fake simulation by one 30 Hz tick and republishes the snapshot.</summary>
    public void AdvanceOneTick()
    {
        float maxPxX = WidthCells * SimSnapshotBridge.CellSizePx;
        float maxPxY = HeightCells * SimSnapshotBridge.CellSizePx;

        for (int i = 0; i < _x.Length; i++)
        {
            _x[i] += _velocities[i].Vx;
            _y[i] += _velocities[i].Vy;

            if (_x[i] < 0f || _x[i] > maxPxX)
            {
                _velocities[i].Vx = -_velocities[i].Vx;
                _x[i] = Math.Clamp(_x[i], 0f, maxPxX);
            }

            if (_y[i] < 0f || _y[i] > maxPxY)
            {
                _velocities[i].Vy = -_velocities[i].Vy;
                _y[i] = Math.Clamp(_y[i], 0f, maxPxY);
            }
        }

        Latest = BuildSnapshot();
    }

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

        return _cellColors[y * WidthCells + x];
    }

    private MockSnapshot BuildSnapshot()
    {
        long tick = (Latest?.Tick ?? -1) + 1;

        var units = new IUnit[_x.Length];
        for (int i = 0; i < units.Length; i++)
        {
            float vx = _velocities[i].Vx;
            float vy = _velocities[i].Vy;
            units[i] = new MockUnit(
                Id: new EntityId(1000 + i),
                ContentId: "era1:enemy-beastling",
                X: _x[i],
                Y: _y[i],
                Hp: _hp[i],
                MaxHp: _maxHp[i],
                Armor: 2,
                Shield: 0,
                SpeedPxPerTick: MathF.Sqrt(vx * vx + vy * vy));
        }

        // Two static towers keep the IBuilding seam of the frozen snapshot
        // contract populated; structural rendering lands with T-045/T-056.
        var buildings = new IBuilding[]
        {
            new MockBuilding(new EntityId(500), "era1:tower-wood-arrow", new CellCoord(10, 12), 260, 260, 1),
            new MockBuilding(new EntityId(501), "era1:tower-wood-arrow", new CellCoord(52, 36), 260, 260, 1),
        };

        return new MockSnapshot(tick, units, Array.Empty<IProjectile>(), buildings, ComputeHash(tick, units));
    }

    private long ComputeHash(long tick, IUnit[] units)
    {
        // FNV-1a over tick + rounded positions: cheap, deterministic,
        // sufficient to observe "same seed ⇒ same stream" in the smoke log.
        long hash = 1469598103934665603L ^ _seed;
        Mix(tick);
        foreach (IUnit u in units)
        {
            Mix((long)MathF.Round(u.X));
            Mix((long)MathF.Round(u.Y));
        }

        return hash;

        void Mix(long value)
        {
            hash ^= value;
            hash *= 1099511628211L;
        }
    }

    private sealed record MockUnit(
        EntityId Id,
        string ContentId,
        float X,
        float Y,
        int Hp,
        int MaxHp,
        int Armor,
        int Shield,
        float SpeedPxPerTick) : IUnit;

    private sealed record MockBuilding(
        EntityId Id,
        string ContentId,
        CellCoord Cell,
        int Hp,
        int MaxHp,
        int Tier) : IBuilding;

    private sealed record MockSnapshot(
        long Tick,
        IReadOnlyList<IUnit> Units,
        IReadOnlyList<IProjectile> Projectiles,
        IReadOnlyList<IBuilding> Buildings,
        long StateHash) : SimSnapshot;
}

/// <summary>
/// C# adapter between the simulation seam and the Godot render skeleton
/// (03-decomposition §1.2 layer-3 adapters). Owns the two dirty trackers and
/// the reverse command queue:
/// <list type="bullet">
/// <item>forward: snapshot + framebuffer → chunk-level dirty sets (the
/// MultiMesh renderers drain them once per frame and rebuild only dirty
/// chunks);</item>
/// <item>reverse: every presentation mutation is queued as a
/// <see cref="SimCommand"/> — the only path into the deterministic core
/// (§1.2-3); the C-12 orchestrator consumes this queue from Wave 2 on
/// (T-053/T-056 wire the real handlers).</item>
/// </list>
/// </summary>
public sealed class SimSnapshotBridge
{
    /// <summary>1 grid cell = 32 px (C-05 world convention).</summary>
    public const int CellSizePx = 32;

    private readonly ISimSnapshotSource _source;
    private readonly IFramebufferProvider _framebuffer;
    private readonly Queue<SimCommand> _commands = new();
    private SimSnapshot? _previous;
    private long _lastFramebufferVersion = -1;

    public SimSnapshotBridge(
        ISimSnapshotSource source,
        IFramebufferProvider framebuffer,
        int widthCells,
        int heightCells,
        int chunkCells = DirtyRegionSet.DefaultChunkCells)
    {
        _source = source;
        _framebuffer = framebuffer;
        GroundDirty = new DirtyRegionSet(widthCells, heightCells, chunkCells);
        UnitDirty = new DirtyRegionSet(widthCells, heightCells, chunkCells);
    }

    /// <summary>Ground-layer chunk dirty tracker (version-driven).</summary>
    public DirtyRegionSet GroundDirty { get; }

    /// <summary>Unit-layer chunk dirty tracker (position-diff-driven).</summary>
    public DirtyRegionSet UnitDirty { get; }

    /// <summary>Snapshot the renderers should currently show (null before the first tick).</summary>
    public SimSnapshot? Current { get; private set; }

    /// <summary>Commands queued by the presentation layer, not yet consumed by the core.</summary>
    public int PendingCommandCount => _commands.Count;

    /// <summary>
    /// One render frame of adapter work: reads the latest snapshot, diffs it
    /// against the previously observed one and marks the owning chunks dirty.
    /// Call once per frame, BEFORE the renderers drain the dirty sets.
    /// </summary>
    public void SyncFrame()
    {
        // Ground: coarse version bump ⇒ full chunk rebuild (cell colors are
        // chunk-static between bumps, so per-cell diffing would be waste).
        if (_framebuffer.FramebufferVersion != _lastFramebufferVersion)
        {
            GroundDirty.MarkAllDirty();
            _lastFramebufferVersion = _framebuffer.FramebufferVersion;
        }

        SimSnapshot? snapshot = _source.Latest;
        if (snapshot is not null)
        {
            if (_previous is null)
            {
                UnitDirty.MarkAllDirty(); // first frame: everything is new
            }
            else
            {
                foreach (IUnit unit in _previous.Units)
                {
                    MarkUnitChunk(unit);
                }
            }

            foreach (IUnit unit in snapshot.Units)
            {
                MarkUnitChunk(unit);
            }

            _previous = snapshot;
        }

        Current = snapshot;
    }

    /// <summary>Drains the ground-layer dirty chunks for this frame's rebuild.</summary>
    public IReadOnlyCollection<(int ChunkX, int ChunkY)> TakeDirtyGroundChunks() =>
        GroundDirty.TakeDirtyChunks();

    /// <summary>Drains the unit-layer dirty chunks for this frame's rebuild.</summary>
    public IReadOnlyCollection<(int ChunkX, int ChunkY)> TakeDirtyUnitChunks() =>
        UnitDirty.TakeDirtyChunks();

    /// <summary>Reverse seam: queues the only legal way back into the core (§1.2-3).</summary>
    public void EnqueueCommand(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Enqueue(command);
    }

    /// <summary>Hands the queued commands to the simulation side (C-12 consumer, Wave 2+).</summary>
    public IReadOnlyList<SimCommand> DrainCommands()
    {
        if (_commands.Count == 0)
        {
            return Array.Empty<SimCommand>();
        }

        var drained = _commands.ToArray();
        _commands.Clear();
        return drained;
    }

    private void MarkUnitChunk(IUnit unit)
    {
        // Pixel→cell→chunk; clamped at the seam so a float position resting
        // exactly on the border pixel cannot address a cell outside the grid.
        int cellX = Math.Clamp((int)(unit.X / CellSizePx), 0, GroundDirty.WidthCells - 1);
        int cellY = Math.Clamp((int)(unit.Y / CellSizePx), 0, GroundDirty.HeightCells - 1);
        UnitDirty.MarkDirty(new CellCoord(cellX, cellY));
    }
}

/// <summary>
/// Deterministic 64-bit LCG behind the mock source, routed through the K-1
/// <see cref="IRng"/> port (CS-1.4: even fixtures do not use a shared
/// Random; same seed ⇒ same stream keeps the smoke log reproducible).
/// </summary>
internal sealed class MockLcgRng : IRng
{
    private ulong _state;

    public MockLcgRng(long seed) => Reseed(seed);

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValue));
        }

        long span = maxValue - (long)minValue;
        return minValue + (int)(NextDouble() * span);
    }

    public double NextDouble()
    {
        _state = unchecked(_state * 6364136223846793005UL + 1442695040888963407UL);
        return (_state >> 11) * (1.0 / (1UL << 53));
    }

    public void Reseed(long seed) => _state = unchecked((ulong)seed);
}
