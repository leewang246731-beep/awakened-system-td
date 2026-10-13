// T2: pure contract — K-1 foundation ports, T-019, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Time port (K-1). Wall time and simulation time enter EraCore exclusively
/// through this port (CODE_STANDARDS CS-1.4: no <c>DateTime.Now</c> and no
/// shared <c>Random</c> inside the logic library). The Godot adapter bridges
/// the engine clock; tests inject fixed values.
/// </summary>
public interface IClock
{
    /// <summary>
    /// Current simulation tick. The simulation runs a fixed 30 Hz tick domain
    /// (03-decomposition §1.2); this counter is the deterministic time source
    /// for all gameplay logic and for replay.
    /// </summary>
    long Tick { get; }

    /// <summary>
    /// Wall-clock milliseconds since the Unix epoch, for diagnostics and UI
    /// only. Gameplay logic must never branch on this value — determinism
    /// outranks realism (CS-1.4).
    /// </summary>
    long NowMs { get; }
}
