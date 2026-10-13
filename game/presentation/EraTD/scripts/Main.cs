using EraCore.Presentation;
using Godot;

namespace EraTD;

/// <summary>
/// T-042 scene entry point (C-21 skeleton). Extends the T-029 headless smoke
/// with the three skeleton acceptance points:
/// <list type="number">
/// <item>integer-scale init: the pure math lives in
/// <see cref="ViewportScale"/> (EraCore), the engine-side letterboxing in
/// project.godot (canvas_items + keep + integer) — both decisions agree;
/// the computed factor feeds the DebugLabel and the smoke line;</item>
/// <item>snapshot adapter: <see cref="SimSnapshotBridge"/> consumes the
/// MOCK source (see the replacement plan in SimSnapshotBridge.cs — C-12
/// real producer lands Wave 2) and turns snapshot diffs into dirty
/// chunks;</item>
/// <item>MultiMesh dirty rebuild: both layers rewrite only the chunks the
/// bridge drained this frame.</item>
/// </list>
/// The 60-frame driver loop runs synchronously inside _Ready because
/// headless `--quit` executes a single frame iteration — a _Process-driven
/// loop would never reach frame 2 under the smoke runner.
/// </summary>
public sealed partial class Main : Control
{
    private const long MockSeed = DeterministicMockSnapshotSource.DefaultSeed;
    private const int MapWidthCells = 64;
    private const int MapHeightCells = 48; // → 2×2 chunks of 32×32 cells
    private const int UnitCount = 24;
    private const int SmokeFrames = 60;    // 60 render frames = 2 s of 30 Hz mock ticks

    private DeterministicMockSnapshotSource _source = null!;
    private SimSnapshotBridge _bridge = null!;
    private MultiMeshRenderer _groundLayer = null!;
    private MultiMeshRenderer _unitLayer = null!;
    private Label _debugLabel = null!;
    private Camera2D _camera = null!;

    public override void _Ready()
    {
        // ---- 1) integer-scale init (F-125) --------------------------------
        // Headless runs report a dummy window size; fall back to the design
        // resolution so the smoke output stays deterministic (scale=1).
        Vector2I window = DisplayServer.WindowGetSize();
        if (window.X < ViewportScale.BaseWidth || window.Y < ViewportScale.BaseHeight)
        {
            window = new Vector2I(ViewportScale.BaseWidth, ViewportScale.BaseHeight);
        }

        ViewportFit fit = ViewportScale.Fit(window.X, window.Y);

        // ---- 2) scene wiring ----------------------------------------------
        _camera = GetNode<Camera2D>("WorldCamera");
        _debugLabel = GetNode<Label>("DebugLabel");
        _groundLayer = GetNode<MultiMeshRenderer>("GroundLayer");
        _unitLayer = GetNode<MultiMeshRenderer>("UnitLayer");

        float cell = SimSnapshotBridge.CellSizePx;
        _camera.Position = new Vector2(MapWidthCells * cell / 2f, MapHeightCells * cell / 2f);

        // ---- 3) snapshot adapter + mock producer ---------------------------
        _source = new DeterministicMockSnapshotSource(MockSeed, MapWidthCells, MapHeightCells, UnitCount);
        _bridge = new SimSnapshotBridge(_source, _source, MapWidthCells, MapHeightCells);

        _groundLayer.SetupGround(MapWidthCells, MapHeightCells, DirtyRegionSet.DefaultChunkCells);
        _unitLayer.SetupUnits(MapWidthCells, MapHeightCells, DirtyRegionSet.DefaultChunkCells, capacity: UnitCount);

        // ---- 4) 60-frame dirty-chunk render loop (headless-safe) -----------
        int framesWithGroundWork = 0;
        int framesWithUnitWork = 0;
        int groundChunksRebuilt = 0;
        int unitChunksRebuilt = 0;

        for (int frame = 0; frame < SmokeFrames; frame++)
        {
            _source.AdvanceOneTick(); // mock 30 Hz tick (C-12 replaces this call site only)
            _bridge.SyncFrame();      // snapshot diff → dirty chunks

            var groundChunks = _bridge.TakeDirtyGroundChunks();
            if (groundChunks.Count > 0)
            {
                _groundLayer.RebuildGroundChunks(groundChunks, _source);
                framesWithGroundWork++;
                groundChunksRebuilt += groundChunks.Count;
            }

            var unitChunks = _bridge.TakeDirtyUnitChunks();
            if (unitChunks.Count > 0)
            {
                _unitLayer.RebuildUnitChunks(unitChunks, _bridge.Current!);
                framesWithUnitWork++;
                unitChunksRebuilt += unitChunks.Count;
            }

            UpdateDebugLabel(fit);
        }

        // Reverse-seam sanity: the command queue round-trips SimCommands.
        _bridge.EnqueueCommand(new EraCore.Contracts.Sim.SimCommand(
            _bridge.Current!.Tick, EraCore.Contracts.Sim.SimCommandKind.TimeControl, "{\"speed\":1}"));

        // ---- 5) markers (smoke_godot.sh asserts SMOKE_OK; scale= is new) --
        // Canonical table recomputed LIVE inside the engine process so the
        // smoke log proves the EraCore math is what the Godot side runs.
        GD.Print(
            "SCALE_TABLE " +
            $"1280x720={ViewportScale.Fit(1280, 720).Scale} " +
            $"1920x1080={ViewportScale.Fit(1920, 1080).Scale} " +
            $"2560x1440={ViewportScale.Fit(2560, 1440).Scale} " +
            $"1600x900={ViewportScale.Fit(1600, 900).Scale} " +
            $"offset1600x900=({ViewportScale.Fit(1600, 900).OffsetX},{ViewportScale.Fit(1600, 900).OffsetY})");
        GD.Print($"SMOKE_OK core={EraCore.VersionInfo.Current} scale={fit.Scale}");
        GD.Print(
            $"BRIDGE frames={SmokeFrames} ticks={_bridge.Current.Tick} units={UnitCount} " +
            $"ground_chunk_rebuilds={groundChunksRebuilt} unit_chunk_rebuilds={unitChunksRebuilt} " +
            $"frames_with_ground_work={framesWithGroundWork} frames_with_unit_work={framesWithUnitWork} " +
            $"pending_commands={_bridge.PendingCommandCount} state_hash={_bridge.Current.StateHash}");

        GetTree().Quit(0);
    }

    private void UpdateDebugLabel(in ViewportFit fit) =>
        _debugLabel.Text =
            $"scale={fit.Scale} off=({fit.OffsetX},{fit.OffsetY}) " +
            $"fps={Engine.GetFramesPerSecond()} tick={_bridge.Current?.Tick.ToString() ?? "-"}";
}
