namespace EraCore;

/// <summary>
/// Single source of truth for the game version string. Keeps the pipeline
/// provably alive end-to-end: EraCore -> tests -> Godot presentation smoke.
/// </summary>
public static class VersionInfo
{
    /// <summary>Wave 0 scaffold version.</summary>
    public const string VersionString = "0.0.1-wave0";

    /// <summary>Current version (e.g. reported by the Godot smoke scene).</summary>
    public static string Current => VersionString;
}
