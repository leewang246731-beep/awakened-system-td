namespace EraCore.Agent.Actions;

/// <summary>
/// Situation summary returned by the LookAround action (F-070 recon ×3,
/// T-051): tick, wave, resource pool, enemy presence. Pure observable
/// facts out of <see cref="IActionWorldView"/> — no formula is invented
/// here (threat multipliers stay in the T-036 formula library).
/// </summary>
/// <param name="Tick">Tick the summary was taken at.</param>
/// <param name="Wave">Current wave number (0 before wave 1).</param>
/// <param name="Resources">Resource pool lines in registry order.</param>
/// <param name="EnemiesPresent">Living enemies right now.</param>
/// <param name="EnemyHpPresent">Aggregate current HP of living enemies.</param>
public sealed record LookAroundSummary(
    long Tick,
    int Wave,
    IReadOnlyList<ResourceStockLine> Resources,
    int EnemiesPresent,
    int EnemyHpPresent);

/// <summary>
/// LookAround handler (C-03 first real handler, T-051): answers the
/// elder's question with the battle overview — wave / resources / enemy
/// presence — read straight off the world view. No fields, 0 pt, allow,
/// E1+ (K-3).
/// </summary>
public sealed class LookAroundAction
{
    /// <summary>Builds the summary for the current world state.</summary>
    public LookAroundSummary Run(IActionWorldView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new LookAroundSummary(
            view.Tick,
            view.CurrentWave,
            view.Resources,
            view.EnemiesPresent,
            view.EnemyHpPresent);
    }
}
