// T2: pure contract — K-1 foundation ports, T-019, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Seeded random number port (K-1) — the root of deterministic simulation:
/// same seed + same call sequence ⇒ same numbers, which is what makes
/// "same seed + same command stream ⇒ same state hash" hold
/// (03-decomposition §1.2). The Godot adapter bridges a PCG64 seeded stream;
/// tests inject fixed sequences. EraCore never touches a shared
/// <c>Random</c> (CS-1.4).
/// </summary>
public interface IRng
{
    /// <summary>
    /// Draws a uniformly distributed integer in the range
    /// [<paramref name="minValue"/>, <paramref name="maxValue"/>) — inclusive
    /// lower bound, exclusive upper bound.
    /// </summary>
    int Next(int minValue, int maxValue);

    /// <summary>Draws a uniformly distributed double in [0.0, 1.0).</summary>
    double NextDouble();

    /// <summary>
    /// Reseeds the stream. After <see cref="Reseed"/> with seed <c>s</c>, the
    /// sequence of subsequent draws must be identical to a freshly created
    /// instance seeded with <c>s</c> (replay and checkpoint requirement).
    /// </summary>
    void Reseed(long seed);
}
