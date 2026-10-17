namespace EraCore.Legacy;

/// <summary>
/// The wonder-driven GLOBAL energy-output channel (T-134, 附录 F.3 奇观链第五个:
/// 戴森云=E5 奇观, 能源 ×2). Reads the wonder table plus the built-wonder state
/// of the legacy ledger and exposes the single multiplier the era's energy
/// production scales by — the tier/anchor tables stay wonder-agnostic, the
/// wonder table stays generator-agnostic, this board is the only place they
/// meet (单一通道, same discipline as <see cref="WonderThroughputBoard"/> for
/// the E3 世界博览会 row and <see cref="WonderRadarSharingBoard"/> for the
/// E4 互联网 row — the four-wonder chain of boards gains its fifth member).
/// </summary>
/// <remarks>
/// F.3 lists exactly one energy wonder (戴森云, E5, bonus_value=2.0 as a
/// MULTIPLIER — wonders.json <c>_units</c> row "bonus_value:energy_output":
/// multiplier, i.e. Π(bonus_value) not Π(1+bonus_value); a ×2 cloud doubles
/// the grid, it does not triple it). The board still multiplies over every
/// built row carrying the energy-output bonus kind — one row per era is
/// already enforced by the wonders loader, so the rule generalizes without
/// inventing new stacking semantics (乘法族). Consumers: the era's energy
/// generation scales by <see cref="Multiplier"/> (E5 聚变 grids — the E5
/// annihilation-risk counterplay surface: doubled output halves the load
/// ratio at unchanged demand).
/// </remarks>
public sealed class WonderEnergyOutputBoard
{
    /// <summary>The closed bonus-kind key of the energy-output channel (wonders.json bonus_kind 值域).</summary>
    public const string EnergyOutputBonusKind = "energy_output";

    private readonly IReadOnlyList<WonderSpec> _wonders;
    private readonly LegacyLedger _ledger;

    /// <param name="wonders">The wonder table (wonders.json).</param>
    /// <param name="ledger">The legacy ledger — the built-wonder state source.</param>
    /// <exception cref="ArgumentNullException">wonders or ledger is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// An energy wonder's bonus_value is not a finite multiplier ≥ 1 — content shape violation.
    /// </exception>
    public WonderEnergyOutputBoard(IReadOnlyList<WonderSpec> wonders, LegacyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(wonders);
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (WonderSpec wonder in wonders.Where(w => w.BonusKind == EnergyOutputBonusKind))
        {
            if (!double.IsFinite(wonder.BonusValue) || wonder.BonusValue < 1.0)
            {
                throw new InvalidOperationException(
                    $"Wonder '{wonder.Id}' declares {EnergyOutputBonusKind}={wonder.BonusValue} — must be a finite multiplier ≥ 1 (F.3: only boosts).");
            }
        }

        _wonders = wonders;
        _ledger = ledger;
    }

    /// <summary>
    /// The global energy-output multiplier: <c>Π(bonus_value)</c> over every
    /// BUILT wonder carrying the energy-output bonus kind; 1.0 when none is
    /// built. With the frozen content this is exactly 2.0 once 戴森云
    /// (bonus_value 2.0) stands — 能源 ×2, the F.3 奇观链第五个的建成效果.
    /// </summary>
    public double Multiplier
    {
        get
        {
            double multiplier = 1.0;
            foreach (WonderSpec wonder in _wonders)
            {
                if (wonder.BonusKind == EnergyOutputBonusKind && _ledger.IsWonderBuilt(wonder.Id))
                {
                    multiplier *= wonder.BonusValue;
                }
            }

            return multiplier;
        }
    }
}
