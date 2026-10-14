namespace EraCore.Legacy;

/// <summary>
/// One registered memorial (T-074, F-059): carries its aura values by
/// kind. Auras are read through <see cref="MemorialAuraBoard"/>, which
/// takes the MAX per kind (同类光环取最大不叠加) and counts at most the
/// first three memorials (全图至多 3 座生效, F.3).
/// </summary>
public sealed record MemorialRegistration(int Ordinal, IReadOnlyDictionary<string, double> Auras);

/// <summary>
/// Memorial aura board (T-074, F-059/F.3): restoration-channel memorials
/// register here; the effective aura of each kind is the MAXIMUM over the
/// first three memorials only — additional memorials stay standing but
/// contribute nothing (第 4 座不生效口径, T-117 extends the same rule).
/// </summary>
public sealed class MemorialAuraBoard
{
    /// <summary>Maximum memorials whose auras are effective (F.3: 全图至多 3 座生效).</summary>
    public const int EffectiveLimit = 3;

    private readonly RestorationSpec _spec;
    private readonly List<MemorialRegistration> _memorials = new();

    /// <param name="table">Archaeology content table (default auras of a content memorial).</param>
    /// <exception cref="ArgumentNullException">table is null.</exception>
    public MemorialAuraBoard(ArchaeologyTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _spec = table.Restoration;
    }

    /// <summary>Memorials registered so far, in registration order.</summary>
    public IReadOnlyList<MemorialRegistration> Memorials => _memorials;

    /// <summary>Number of memorials whose auras can possibly count (registration order, cap 3).</summary>
    public int EffectiveCount => Math.Min(_memorials.Count, EffectiveLimit);

    /// <summary>
    /// Registers one memorial. The <paramref name="auras"/> override is the
    /// test/host seam for varying aura sets; null takes the content auras
    /// (<c>restoration.memorial_auras</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException">auras is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An aura value is outside [0,1).</exception>
    public MemorialRegistration RegisterMemorial(IReadOnlyDictionary<string, double>? auras = null)
    {
        IReadOnlyDictionary<string, double> effective = auras ?? _spec.MemorialAuras;
        ArgumentNullException.ThrowIfNull(effective);
        foreach ((string kind, double value) in effective)
        {
            _ = StackingRule.ValidateShare(value, $"memorial-aura:{kind}");
        }

        var registration = new MemorialRegistration(_memorials.Count + 1, effective);
        _memorials.Add(registration);
        return registration;
    }

    /// <summary>
    /// The effective aura of one kind: MAXIMUM over the first three
    /// memorials carrying that kind (同类取最大不叠加), 0 when none.
    /// </summary>
    public double EffectiveAura(string kind)
    {
        double best = 0.0;
        for (int i = 0; i < EffectiveCount; i++)
        {
            if (_memorials[i].Auras.TryGetValue(kind, out double value) && value > best)
            {
                best = value;
            }
        }

        return best;
    }

    /// <summary>Effective <c>research_relief</c> aura (feeds the research pricing through StackingRule).</summary>
    public double EffectiveResearchRelief() => EffectiveAura("research_relief");

    /// <summary>Effective <c>repair_discount</c> aura (feeds the restoration quote through StackingRule).</summary>
    public double EffectiveRepairDiscount() => EffectiveAura("repair_discount");
}
