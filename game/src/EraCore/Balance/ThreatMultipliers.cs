namespace EraCore.Balance;

/// <summary>
/// Threat multiplier triple (v3.2 appendix F.2, one row of the 12-cell
/// mapping table; feature F-049). Pure data — produced by
/// <see cref="Threat.Multipliers"/>, consumed by wave spawning (F-030) and
/// campaign pacing (F-048). Immutable value type with structural equality.
/// </summary>
/// <param name="Count">
/// Enemy count multiplier (5/3)×t — computed as 5t/3 so the F.2 anchor
/// columns (0.5/1.0/1.5/2.0) are hit exactly in binary floating point.
/// </param>
/// <param name="Hp">Enemy HP multiplier 0.5+(t−0.3)/0.9.</param>
/// <param name="Interval">
/// Wave interval multiplier clamp(2−1.4(t−0.3)/0.9, 0.5, 2) — the only
/// clamped member; Count/Hp extrapolate linearly outside [0.3, 1.2].
/// </param>
public sealed record ThreatMultipliers(double Count, double Hp, double Interval);
