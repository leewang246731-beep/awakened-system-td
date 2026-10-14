using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Combat;

/// <summary>
/// Per-tier growth multipliers declared by the content registry
/// (<c>tier_growth</c> block of towers.json, v3.2 §6): DPS ×2/level (±15%
/// band), total cost ×3/level (strict), range +25%/level (±15% design band).
/// The engine only READS these numbers (RL7); law enforcement lives in
/// <see cref="UpgradeLaw"/>.
/// </summary>
/// <param name="DpsMultiplierPerTier">DPS multiplier per tier (anchor 2.0, v3.2 §6).</param>
/// <param name="CostMultiplierPerTier">Total-cost multiplier per tier (anchor 3.0, strict).</param>
/// <param name="RangeMultiplierPerTier">Range multiplier per tier (anchor 1.25).</param>
public sealed record TowerGrowthSpec(
    double DpsMultiplierPerTier,
    double CostMultiplierPerTier,
    double RangeMultiplierPerTier);

/// <summary>
/// Trait block of a tower definition (<c>traits</c> object of towers.json).
/// Fields are optional per tower kind: burst towers carry a wind-up, the
/// spike trap carries its control effect, the campfire carries its aura.
/// Unknown trait keys are ignored by the loader (mod extension seam).
/// </summary>
/// <param name="BurstWindupTicks">
/// Cold-start wind-up before the first shot, 30 Hz ticks — the burst price
/// demanded by v3.2 §6「爆发型必有前摇/充能/热量代价」(SP-1b L37: 落石台
/// 1.5 s × 30 Hz = 45 ticks).
/// </param>
/// <param name="OneShot">Consumed on trigger (spike trap, SP-1b L38).</param>
/// <param name="RootTicks">Root duration in ticks (2 s × 30 Hz = 60, SP-1b L38).</param>
/// <param name="SlowPercent">Slow magnitude in percent (30, SP-1b L38).</param>
/// <param name="SlowTicks">Slow duration in ticks (4 s × 30 Hz = 120, SP-1b L38).</param>
/// <param name="AuraRadiusCells">Aura radius in cells (campfire 3, SP-1b L39).</param>
/// <param name="AuraEffects">Aura effect ids (alert_vision/ally_regen, SP-1b L39).</param>
/// <param name="AuraRegenHpPerS">Ally HP regen inside the aura (points per second).</param>
/// <param name="SplashRadiusCells">
/// Splash radius in cells of an AOE tower (T-065): every enemy within this
/// radius of the impact settles a hit with <see cref="Damage.SplashDecay"/>
/// (edge floor 40%, v3.2 §6). Required for splash-class weapon settlement.
/// </param>
/// <param name="BurnDps">
/// Burn damage-over-time rate in points per second (T-065): enemies struck
/// by a burning splash ignite for <paramref name="BurnDps"/> over
/// <paramref name="BurnTicks"/> ticks — an independent scorch source on top
/// of the registry DPS, outside the F.5 four-group verification accounting.
/// </param>
/// <param name="BurnTicks">Burn duration in 30 Hz ticks.</param>
public sealed record TowerTraits(
    int? BurstWindupTicks,
    bool? OneShot,
    int? RootTicks,
    int? SlowPercent,
    int? SlowTicks,
    int? AuraRadiusCells,
    IReadOnlyList<string>? AuraEffects,
    double? AuraRegenHpPerS,
    double? SplashRadiusCells = null,
    double? BurnDps = null,
    int? BurnTicks = null);

/// <summary>
/// One tower definition deserialized from the content registry (towers.json
/// item, K-2 pipeline; C-09 reads numbers only from this table — RL7). Field
/// semantics per the E1 anchors: <c>reload_ticks</c> lives on the 30 Hz tick
/// domain (K-1 IClock), <c>range_cells</c> on the grid cell unit (1 cell =
/// 32 px), <c>ammo_per_shot</c> counts shots (1 ammo item = 10 shots, v3.2
/// §5). Control/aura towers (spike trap, campfire) carry
/// <see cref="ReloadTicks"/> = null — they have no weapon cycle.
/// </summary>
/// <param name="Id">Namespaced content id (<c>era1:tower-wood-arrow</c>).</param>
/// <param name="Class">One of the six frozen classes (SP-1a).</param>
/// <param name="Tier">Current tier 1..4.</param>
/// <param name="MaxTier">Upgrade ceiling of this line.</param>
/// <param name="CostTotal">Total build cost in resource points.</param>
/// <param name="Dps">Sustained damage per second (consistency: damage_per_shot × 30 / reload_ticks).</param>
/// <param name="DamagePerShot">Single-shot undecayed damage d (v3.2 §6 symbol binding).</param>
/// <param name="ReloadTicks">Reload cycle length in 30 Hz ticks; null = no weapon cycle.</param>
/// <param name="RangeCells">Attack range in cells (0 = melee/contact line).</param>
/// <param name="Hp">Building hit points — feeds the flow-field tear price (×5 in FlowField).</param>
/// <param name="AmmoItem">Consumed ammo item id; null = no ammo need.</param>
/// <param name="AmmoPerShot">Shots consumed per firing (0 = no consumption).</param>
/// <param name="TierGrowth">Declared growth law multipliers (may be missing in broken content; validators reject).</param>
/// <param name="Traits">Optional trait block; null when the item has none.</param>
/// <param name="UpgradesTo">
/// Content id of the next-tier row of the SAME upgrade line (T-065): the
/// in-place upgrade path is data wiring, not code — e.g. the E2 standard
/// line arrow → crossbow → repeating crossbow → fire oil (F.5 strict ×3
/// cost ladder 90→270→810→2430). Null = the line has no successor row
/// (terminal row, or a specialization line capped at its only tier).
/// </param>
/// <param name="TierLine">
/// Line membership per the F.5 vocabulary: "standard" (the ×3 strict cost
/// ladder) or "specialization" (F.5 <c>tier_line:"specialization"</c> —
/// e.g. the E2 onager 700|80|11, balanced for armor niches instead of the
/// ladder). Null is treated as standard.
/// </param>
public sealed record TowerSpec(
    string Id,
    TowerClass Class,
    int Tier,
    int MaxTier,
    int CostTotal,
    double Dps,
    double DamagePerShot,
    int? ReloadTicks,
    double RangeCells,
    int Hp,
    string? AmmoItem,
    int AmmoPerShot,
    TowerGrowthSpec? TierGrowth,
    TowerTraits? Traits,
    string? UpgradesTo = null,
    string? TierLine = null)
{
    /// <summary>
    /// Deserializes a tower collection document (the <c>items</c> array of
    /// towers.json). JSON property names are snake_case; unknown keys are
    /// ignored (mod extension seam, K-2). Damaged payloads throw
    /// <see cref="JsonException"/> — content validation is the caller's
    /// (<see cref="UpgradeLaw"/>, C-02 schema checks).
    /// </summary>
    public static IReadOnlyList<TowerSpec> LoadCollection(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var document = JsonSerializer.Deserialize(json, TowerSpecJsonContext.Default.TowerSpecDocument)
            ?? throw new JsonException("Tower collection deserialized to null.");
        return document.Items ?? Array.Empty<TowerSpec>();
    }
}

/// <summary>Root shape of a tower collection file: content_type + items.</summary>
/// <param name="ContentType">K-2 content kind declaration ("Building").</param>
/// <param name="Items">Tower definitions.</param>
public sealed record TowerSpecDocument(string? ContentType, IReadOnlyList<TowerSpec>? Items);

/// <summary>
/// System.Text.Json source-generation context for tower content (AOT red
/// line, 07 D-05: EraCore serialization goes through generated metadata
/// only). Kept in this file as a secondary type so its coverage rides on
/// TowerSpecTests (T2 file mapping).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TowerSpecDocument))]
[JsonSerializable(typeof(TowerSpec))]
[JsonSerializable(typeof(TowerClass))]
public sealed partial class TowerSpecJsonContext : JsonSerializerContext;
