using Godot;

namespace EraTD;

/// <summary>
/// T-029 headless smoke scene entry point. Proves three things at once:
/// the pinned Godot 4.7.2 mono runtime boots, the managed assembly loads,
/// and the EraCore project reference is alive (version comes from EraCore).
/// tools/ci/smoke_godot.sh asserts the emitted markers.
/// </summary>
public partial class Main : Node
{
    public override void _Ready()
    {
        GD.Print($"SMOKE_OK core={EraCore.VersionInfo.Current}");
        GetTree().Quit(0);
    }
}
