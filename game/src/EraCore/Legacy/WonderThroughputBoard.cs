namespace EraCore.Legacy;

/// <summary>
/// The wonder-driven GLOBAL logistics throughput channel (T-117, F-060:
/// 世界博览会=E3 奇观, 全物流吞吐 +25%). Reads the wonder table plus the
/// built-wonder state of the legacy ledger and exposes the single
/// multiplier every <c>Logistics.TransportTier</c> throughput computation
/// accepts — the tier table stays wonder-agnostic, the wonder table stays
/// tier-agnostic, this board is the only place they meet (单一通道, same
/// discipline as <see cref="StackingRule"/> for reliefs).
/// </summary>
/// <remarks>
/// F.3 lists exactly one throughput wonder (世界博览会, E3); the board still
/// multiplies over every built row carrying the throughput bonus kind —
/// one row per era is already enforced by the wonders loader, so the rule
/// generalizes without inventing new stacking semantics (乘法族，同 F.3 叠加
/// 原则的方向面：增益不设减免式 50% 下限——下限只约束成本减免，F.3 原文).
/// </remarks>
public sealed class WonderThroughputBoard
{
    /// <summary>The closed bonus-kind key of the throughput channel (wonders.json bonus_kind 值域).</summary>
    public const string ThroughputBonusKind = "logistics_throughput";

    private readonly IReadOnlyList<WonderSpec> _wonders;
    private readonly LegacyLedger _ledger;

    /// <param name="wonders">The wonder table (wonders.json).</param>
    /// <param name="ledger">The legacy ledger — the built-wonder state source.</param>
    /// <exception cref="ArgumentNullException">wonders or ledger is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A throughput wonder's bonus_value is not a positive finite fraction — content shape violation.
    /// </exception>
    public WonderThroughputBoard(IReadOnlyList<WonderSpec> wonders, LegacyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(wonders);
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (WonderSpec wonder in wonders.Where(w => w.BonusKind == ThroughputBonusKind))
        {
            if (!double.IsFinite(wonder.BonusValue) || wonder.BonusValue <= 0)
            {
                throw new InvalidOperationException(
                    $"Wonder '{wonder.Id}' declares {ThroughputBonusKind}={wonder.BonusValue} — must be a positive finite fraction.");
            }
        }

        _wonders = wonders;
        _ledger = ledger;
    }

    /// <summary>
    /// The global throughput multiplier: <c>Π(1 + bonus_value)</c> over every
    /// BUILT wonder carrying the throughput bonus kind; 1.0 when none is
    /// built. With the frozen content this is exactly 1.25 once 世界博览会
    /// (0.25) stands.
    /// </summary>
    public double Multiplier
    {
        get
        {
            double multiplier = 1.0;
            foreach (WonderSpec wonder in _wonders)
            {
                if (wonder.BonusKind == ThroughputBonusKind && _ledger.IsWonderBuilt(wonder.Id))
                {
                    multiplier *= 1.0 + wonder.BonusValue;
                }
            }

            return multiplier;
        }
    }
}
