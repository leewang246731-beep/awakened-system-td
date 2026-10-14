using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Combat;

/// <summary>
/// One splash candidate: a unit plus its distance from the impact center in
/// cells (callers convert pixel positions to the cell unit — 1 cell = 32 px,
/// K-1 domain; the settlement itself is unit-agnostic).
/// </summary>
/// <param name="Unit">Candidate enemy (inside or outside the radius).</param>
/// <param name="DistanceCells">Distance from the impact center in cells, ≥ 0.</param>
public sealed record SplashCandidate(IUnit Unit, double DistanceCells);

/// <summary>
/// One settled splash hit: the struck unit, its distance, the applied decay
/// factor and the final damage, plus whether the strike ignited a burn.
/// </summary>
/// <param name="TargetId">Struck unit id.</param>
/// <param name="DistanceCells">Distance from the impact center in cells.</param>
/// <param name="Decay">Applied <see cref="Damage.SplashDecay"/> factor in [0.4, 1.0].</param>
/// <param name="DamageDealt">Final damage after decay, armor and the 10% floor (v3.2 §6).</param>
/// <param name="BurnApplied">True when this strike ignited the unit (tower carries burn traits).</param>
public sealed record SplashHit(
    EntityId TargetId,
    double DistanceCells,
    double Decay,
    double DamageDealt,
    bool BurnApplied);

/// <summary>
/// Multi-target splash settlement (C-09 full, T-065; v3.2 §6 溅射): the
/// fire-oil tower fires at a primary target and every enemy within the
/// tower's splash radius settles a hit — per-target decay through
/// <see cref="Damage.SplashDecay"/> (full damage at the impact center, 40%
/// floor at the edge), then the branch-1 formula
/// <c>max(d×decay − 甲, 10%×d×decay)</c> per target via
/// <see cref="Damage.Direct"/>. The firing loop (<see cref="WeaponFire"/>)
/// stays single-target and unaware of this settlement; the C-12 combat tick
/// (T-067) feeds the candidates. Towers declaring
/// <c>traits.burn_dps</c>/<c>traits.burn_ticks</c> ignite every struck unit
/// on the passed <see cref="BurnTracker"/> (火油矢燃烧, [NEW] values from
/// content — RL7).
/// </summary>
public static class SplashAttack
{
    /// <summary>
    /// Settles one splash firing. Candidates at a distance ≤ radius are hit
    /// (edge inclusive — the 40% floor applies AT the radius); candidates
    /// strictly beyond are untouched. Results keep candidate order.
    /// </summary>
    /// <param name="spec">Firing tower's registry row (must carry <c>splash_radius_cells</c>).</param>
    /// <param name="candidates">Enemies with their distances from the impact center.</param>
    /// <param name="burn">
    /// Optional burn tracker; when the tower carries burn traits, every
    /// struck unit ignites (per-tick rate from <c>burn_dps</c>).
    /// </param>
    /// <exception cref="ArgumentException">
    /// spec is splash-classed but declares no positive
    /// <c>splash_radius_cells</c>, or a candidate carries a negative distance.
    /// </exception>
    public static IReadOnlyList<SplashHit> Settle(
        TowerSpec spec,
        IReadOnlyList<SplashCandidate> candidates,
        BurnTracker? burn = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(candidates);

        double radius = spec.Traits?.SplashRadiusCells ?? 0;
        if (radius <= 0)
        {
            throw new ArgumentException(
                $"{spec.Id} declares no positive traits.splash_radius_cells — splash settlement needs the radius from content (RL7).",
                nameof(spec));
        }

        double burnDps = spec.Traits?.BurnDps ?? 0;
        int burnTicks = spec.Traits?.BurnTicks ?? 0;
        bool burns = burnDps > 0 && burnTicks > 0;

        var hits = new List<SplashHit>(candidates.Count);
        foreach (SplashCandidate candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.DistanceCells < 0)
            {
                throw new ArgumentException(
                    $"Candidate {candidate.Unit.Id.Value} carries a negative distance ({candidate.DistanceCells}).",
                    nameof(candidates));
            }

            if (candidate.DistanceCells > radius)
            {
                continue; // strictly outside — untouched
            }

            double decay = Damage.SplashDecay(candidate.DistanceCells, radius);
            double dealt = Damage.Direct(spec.DamagePerShot, decay, candidate.Unit.Armor);
            bool burnApplied = false;
            if (burns && burn is not null)
            {
                burn.Ignite(candidate.Unit.Id, burnDps, burnTicks);
                burnApplied = true;
            }

            hits.Add(new SplashHit(candidate.Unit.Id, candidate.DistanceCells, decay, dealt, burnApplied));
        }

        return hits;
    }
}
