using EraCore.Contracts.Foundation;

namespace EraCore.Legacy;

/// <summary>
/// The closed output-channel set of one dig (T-074, F-058 三通道):
/// 知识残卷 / 远古图纸 / 修复.
/// </summary>
public enum ArchaeologyChannelKind
{
    /// <summary>知识残卷: a knowledge scroll restoring one memory fragment + node relief (F-099/F-059).</summary>
    KnowledgeScroll = 0,

    /// <summary>远古图纸: a cross-era mixed-building blueprint unlock.</summary>
    AncientBlueprint = 1,

    /// <summary>修复: wreck → 折算建筑 or 纪念馆 (07 #28: ×30%×0.8).</summary>
    Restoration = 2,
}

/// <summary>
/// The deterministic dig schedule of one archaeology site (T-074): the
/// fixed costs and the drill length in the K-1 tick domain.
/// </summary>
/// <param name="ManpowerCost">Manpower points.</param>
/// <param name="EnergyCost">Energy points.</param>
/// <param name="DurationSeconds">Site-tier duration (content tier table).</param>
/// <param name="DrillTicks">Duration × drill_ticks_per_second (30 Hz domain).</param>
public sealed record DigQuote(int ManpowerCost, int EnergyCost, int DurationSeconds, long DrillTicks);

/// <summary>One dig's terminal payload — exactly one channel arm is set.</summary>
/// <param name="Channel">The drawn channel.</param>
/// <param name="Scroll">Knowledge-scroll row (KnowledgeScroll channel).</param>
/// <param name="Blueprint">Ancient-blueprint row (AncientBlueprint channel).</param>
/// <param name="Restoration">Restoration outcome (Restoration channel).</param>
public sealed record ArchaeologyDigResult(
    ArchaeologyChannelKind Channel,
    KnowledgeScrollSpec? Scroll,
    AncientBlueprintSpec? Blueprint,
    RestorationOutcome? Restoration);

/// <summary>
/// The restoration channel outcome (T-074, F-059/07 #28): the wreck folds
/// into either a functional building (折算建筑) or a memorial (纪念馆). The
/// repair quote is <c>残值 × wreck_repair_cost_fraction × new_era_conversion_rate</c>
/// (×0.3×0.8 叠加), with the active memorial <c>repair_discount</c> aura
/// merged through <see cref="StackingRule"/> (single choke).
/// </summary>
/// <param name="AsMemorial">True → 纪念馆 (registers on the aura board); false → 折算建筑.</param>
/// <param name="RepairCost">Deterministic repair quote in resource points.</param>
public sealed record RestorationOutcome(bool AsMemorial, double RepairCost);

/// <summary>
/// Archaeology service (T-074, F-058): one dig = fixed manpower/energy
/// cost, a tier-driven duration in the 30 Hz drill domain, then ONE
/// channel draw over the content weights (知识残卷/远古图纸/修复), then the
/// channel payload draw (which scroll / which blueprint / 折算 vs 纪念馆).
/// All randomness flows through the injected <see cref="IRng"/> — same
/// seed + same call order ⇒ identical dig sequence (CS-1.4).
/// </summary>
public sealed class ArchaeologyService
{
    private readonly ArchaeologyTable _table;

    /// <param name="table">Archaeology content table.</param>
    /// <exception cref="ArgumentNullException">table is null.</exception>
    public ArchaeologyService(ArchaeologyTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Scrolls.Count == 0 || table.Blueprints.Count == 0)
        {
            throw new ArgumentException("The archaeology table needs at least one scroll and one blueprint.", nameof(table));
        }

        _table = table;
    }

    /// <summary>The content table this service draws from.</summary>
    public ArchaeologyTable Table => _table;

    /// <summary>
    /// The deterministic dig schedule of one site tier: the tier row of
    /// <c>dig.tier_duration_seconds</c> when present, else the tier-less
    /// bounds midpoint; drill ticks = seconds × drill resolution.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">siteTier is outside 1..4.</exception>
    public DigQuote Quote(int siteTier)
    {
        if (siteTier is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(siteTier), siteTier, "Site tier must be 1..4.");
        }

        int seconds = _table.Dig.TierDurationSeconds.TryGetValue(siteTier, out int tierSeconds)
            ? tierSeconds
            : (_table.Dig.MinDurationSeconds + _table.Dig.MaxDurationSeconds) / 2;
        return new DigQuote(
            _table.Dig.ManpowerCost,
            _table.Dig.EnergyCost,
            seconds,
            (long)seconds * _table.Dig.DrillTicksPerSecond);
    }

    /// <summary>
    /// Runs one dig. The channel is drawn over the content weights
    /// (cumulative walk in content order), the payload draw picks the exact
    /// scroll/blueprint or the 折算 vs 纪念馆 coin. The restoration quote
    /// folds the wreck value through the content rates and the live
    /// memorial repair-discount aura via <see cref="StackingRule"/>.
    /// </summary>
    /// <param name="siteTier">Ruin tier (1..4) — drives the duration quote.</param>
    /// <param name="wreckValue">Wreck value in resource points (restoration quote base).</param>
    /// <param name="rng">Draw port.</param>
    /// <param name="memorials">Live memorial board (repair-discount aura; null = no aura).</param>
    /// <exception cref="ArgumentNullException">rng is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">siteTier outside 1..4 or wreckValue negative.</exception>
    public ArchaeologyDigResult Dig(
        int siteTier, double wreckValue, IRng rng, MemorialAuraBoard? memorials = null)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (wreckValue < 0 || !double.IsFinite(wreckValue))
        {
            throw new ArgumentOutOfRangeException(nameof(wreckValue), wreckValue, "Wreck value must be finite and non-negative.");
        }

        _ = Quote(siteTier); // validates the tier

        double total = 0;
        foreach ((_, double weight) in _table.Channels)
        {
            total += weight;
        }

        double draw = rng.NextDouble() * total;
        double cumulative = 0;
        string channel = _table.Channels[^1].Channel;
        foreach ((string key, double weight) in _table.Channels)
        {
            cumulative += weight;
            if (draw < cumulative)
            {
                channel = key;
                break;
            }
        }

        switch (channel)
        {
            case "knowledge_scroll":
                return new ArchaeologyDigResult(
                    ArchaeologyChannelKind.KnowledgeScroll,
                    Scroll: _table.Scrolls[rng.Next(0, _table.Scrolls.Count)],
                    Blueprint: null,
                    Restoration: null);
            case "ancient_blueprint":
                return new ArchaeologyDigResult(
                    ArchaeologyChannelKind.AncientBlueprint,
                    Scroll: null,
                    Blueprint: _table.Blueprints[rng.Next(0, _table.Blueprints.Count)],
                    Restoration: null);
            default:
                return new ArchaeologyDigResult(
                    ArchaeologyChannelKind.Restoration,
                    Scroll: null,
                    Blueprint: null,
                    Restoration: Restore(wreckValue, rng, memorials));
        }
    }

    /// <summary>
    /// The restoration fold (07 #28): quote = 残值 × 0.3 × 0.8 with the
    /// memorial repair-discount aura merged through the single choke;
    /// outcome = 折算建筑 vs 纪念馆 over the content weights.
    /// </summary>
    public RestorationOutcome Restore(double wreckValue, IRng rng, MemorialAuraBoard? memorials = null)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (!double.IsFinite(wreckValue) || wreckValue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wreckValue), wreckValue, "Wreck value must be finite and non-negative.");
        }

        double baseQuote = wreckValue * _table.Restoration.WreckRepairCostFraction
            * _table.Restoration.NewEraConversionRate;
        double repairCost = memorials is null
            ? baseQuote
            : StackingRule.Apply(baseQuote, new[] { memorials.EffectiveRepairDiscount() });
        double total = _table.Restoration.FunctionalWeight + _table.Restoration.MemorialWeight;
        bool asMemorial = rng.NextDouble() * total >= _table.Restoration.FunctionalWeight;
        return new RestorationOutcome(asMemorial, repairCost);
    }
}
