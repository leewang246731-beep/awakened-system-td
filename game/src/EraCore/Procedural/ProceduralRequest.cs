using EraCore.Combat;

namespace EraCore.Procedural;

/// <summary>
/// Difficulty tier row of the procedural generator (T-140): the pacing
/// knobs ONE generated level derives everything else from. Threat is NOT a
/// field — it is derived live through the frozen §7 formula
/// <see cref="EraCore.Balance.Threat.ClampThreat"/>(AdjacentEnemyCamps),
/// aligning the generated levels with the handcrafted calibration
/// (常规带 0.3/0.6 + 终局带 0.9 = camp counts 0/1/2; the band guard rejects
/// deeper camp counts because threat past 0.9 shortens the wave-interval
/// enough to leave the 40~90 分钟档 structurally, T-013/F-007 duration band).
/// Numbers are host-supplied from <c>game/content/campaign/procedural.json</c>
/// (RL7 — the generator itself hardcodes no pacing values).
/// </summary>
/// <param name="Id">Tier id (content row id, e.g. "regular-1").</param>
/// <param name="Slot">Slot ordinal of levels generated under this tier.</param>
/// <param name="AdjacentEnemyCamps">§7 formula input — must lie in [0, 2] (threat band 0.3~0.9).</param>
/// <param name="WaveBaseCount">Wave 基数 of the generated wave table.</param>
/// <param name="WaveBeginWave">Wave 起 of the ramp.</param>
/// <param name="WaveSpacing">Wave 间隔 of the ramp.</param>
/// <param name="WaveScaling">成长 divisor of the ramp.</param>
/// <param name="BatchesPerWave">Batches per wave.</param>
/// <param name="ValleySeconds">波谷 seconds (&lt; wave interval of the derived threat).</param>
/// <param name="ArrowTowerId">Content id of the arrow (ammo-fed) tower line.</param>
/// <param name="ArrowTowerCount">Arrow towers of the auto-build plan.</param>
/// <param name="RockTowerId">Content id of the rock (heavy) tower line.</param>
/// <param name="RockTowerCount">Rock towers of the auto-build plan.</param>
/// <param name="WaveOneTowerCount">Towers built inside the wave-1 grace tranche.</param>
/// <param name="TowerWaveStep">Wave spacing between the later tower tranches.</param>
/// <param name="BossLineEnabled">Whether the level carries the final-wave boss row (终局带).</param>
/// <param name="BossEnemyId">Boss content id (required when the boss line is enabled).</param>
/// <param name="LaborCamps">Labor camps of the economy chain.</param>
/// <param name="Harvesters">Harvesters of the economy chain (currency resource, 1 seat).</param>
/// <param name="Converters">Ammo converters of the economy chain (E1 arrow recipe).</param>
/// <param name="HarvesterLanePorters">Porters assigned per harvester → converter lane.</param>
/// <param name="StockMultiplier">Initial grant = tower sticker total × this multiplier (e1-final 手工锚 900/360≈2.5 先例).</param>
/// <param name="BudgetSeconds">Design budget row emitted into the level JSON (handcrafted loader band [900,2400]s; the 40~90 分钟档 exceeds it by design — F-007 vs 手工关 15~40 分钟带, v3.2 §3).</param>
/// <param name="StrategyObjectives">SP-8a pool ids of the objectives block.</param>
/// <param name="ChallengeObjectives">SP-8b pool ids of the objectives block.</param>
/// <param name="NameZh">Tier label (Chinese) of generated level names.</param>
/// <param name="NameEn">Tier label (English) of generated level names.</param>
public sealed record ProceduralTier(
    string Id,
    int Slot,
    int AdjacentEnemyCamps,
    int WaveBaseCount,
    int WaveBeginWave,
    int WaveSpacing,
    double WaveScaling,
    int BatchesPerWave,
    double ValleySeconds,
    string ArrowTowerId,
    int ArrowTowerCount,
    string RockTowerId,
    int RockTowerCount,
    int WaveOneTowerCount,
    int TowerWaveStep,
    bool BossLineEnabled,
    string? BossEnemyId,
    int LaborCamps,
    int Harvesters,
    int Converters,
    int HarvesterLanePorters,
    double StockMultiplier,
    int BudgetSeconds,
    IReadOnlyList<string> StrategyObjectives,
    IReadOnlyList<string> ChallengeObjectives,
    string NameZh,
    string NameEn)
{
    /// <summary>Threat of every level under this tier — the frozen §7 formula, live.</summary>
    public double Threat => EraCore.Balance.Threat.ClampThreat(AdjacentEnemyCamps);

    /// <summary>胜波 of every level under this tier — §7 formula 10+⌊t×10⌋, live.</summary>
    public int WinWaves => EraCore.Balance.Campaign.WinWaves(Threat);

    /// <summary>Total towers of the auto-build plan.</summary>
    public int TotalTowerCount => ArrowTowerCount + RockTowerCount;

    /// <summary>
    /// Validates the tier row: non-blank id, camp count inside the
    /// 0.3~0.9 threat band (deeper camps are the endless domain, not the
    /// level generator's), positive wave knobs, positive tower counts with
    /// the wave-one tranche inside the total, the boss pair present exactly
    /// when the boss line is on, a non-negative economy chain with at least
    /// one converter when any arrow tower exists (ammo flows through it),
    /// a positive stock multiplier and a positive budget, and non-empty
    /// distinct objective pools.
    /// </summary>
    /// <exception cref="ArgumentException">Any rule above is violated.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("Tier id must not be blank.", nameof(Id));
        }

        if (AdjacentEnemyCamps is < 0 || AdjacentEnemyCamps > 2) // ci-allow: R7 T-140 营邻接上界=2（§7 公式 clamp(0.3+0.3n) 于 n=2 到 0.9 终局带——超过即离开程序关威胁标定带，与手工关终局 0.9 对齐）
        {
            throw new ArgumentException(
                $"AdjacentEnemyCamps must lie in [0, 2] (threat band 0.3~0.9, §7 手工关标定带; got {AdjacentEnemyCamps}).",
                nameof(AdjacentEnemyCamps));
        }

        if (WaveBaseCount < 1 || WaveSpacing < 1 || BatchesPerWave < 1)
        {
            throw new ArgumentException(
                $"Wave knobs must be positive (base={WaveBaseCount}, spacing={WaveSpacing}, batches={BatchesPerWave}).");
        }

        if (WaveScaling < 0.1)
        {
            throw new ArgumentException($"WaveScaling must be >= 0.1 (got {WaveScaling}).", nameof(WaveScaling));
        }

        if (ValleySeconds < 0 || ValleySeconds >= EraCore.Balance.Campaign.WaveIntervalSeconds(Threat))
        {
            throw new ArgumentException(
                $"ValleySeconds must lie in [0, wave interval) (got {ValleySeconds}).", nameof(ValleySeconds));
        }

        if (ArrowTowerCount < 1 || RockTowerCount < 1 || WaveOneTowerCount < 1 || WaveOneTowerCount > TotalTowerCount)
        {
            throw new ArgumentException(
                $"Tower plan is malformed (arrow={ArrowTowerCount}, rock={RockTowerCount}, wave-one={WaveOneTowerCount}).");
        }

        if (TowerWaveStep < 1)
        {
            throw new ArgumentException($"TowerWaveStep must be positive (got {TowerWaveStep}).", nameof(TowerWaveStep));
        }

        if (BossLineEnabled != (BossEnemyId is not null))
        {
            throw new ArgumentException("The boss pair (enabled flag + enemy id) must be present together.");
        }

        if (LaborCamps < 0 || Harvesters < 1 || Converters < 1 || HarvesterLanePorters < 1)
        {
            throw new ArgumentException(
                $"Economy chain is malformed (camps={LaborCamps}, harvesters={Harvesters}, converters={Converters}, porters={HarvesterLanePorters}).");
        }

        if (StockMultiplier <= 0 || BudgetSeconds < 1)
        {
            throw new ArgumentException(
                $"StockMultiplier and BudgetSeconds must be positive (got {StockMultiplier}, {BudgetSeconds}).");
        }

        ValidatePool(StrategyObjectives, "strategy");
        ValidatePool(ChallengeObjectives, "challenge");
        if (string.IsNullOrWhiteSpace(NameZh) || string.IsNullOrWhiteSpace(NameEn))
        {
            throw new ArgumentException("Tier labels must not be blank.");
        }
    }

    private static void ValidatePool(IReadOnlyList<string> pool, string name)
    {
        ArgumentNullException.ThrowIfNull(pool, name);
        if (pool.Count == 0 || pool.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"The {name} objective pool must be non-empty and blank-free.");
        }

        if (pool.Distinct(StringComparer.Ordinal).Count() != pool.Count)
        {
            throw new ArgumentException($"The {name} objective pool contains duplicate ids.");
        }
    }
}

/// <summary>
/// Era content pack one generation consumes (T-140): the enemy roster, the
/// tower catalog (sticker prices feed the initial-grant sizing) and the era
/// identity of the emitted level row. Host-supplied from the era content
/// tables (RL7); the generator performs no IO.
/// </summary>
/// <param name="EraId">Era content id of emitted rows (e.g. "era1:era-ancient").</param>
/// <param name="RegularRoster">Regular roster ids (distinct, boss-free).</param>
/// <param name="Towers">Tower catalog rows referenced by the tier plan.</param>
/// <param name="CurrencyResource">Build-currency resource id of the era (initial grant + harvester seats).</param>
/// <param name="WarehouseLevel">Era warehouse level (v3.2 附录 F.3 pool cap).</param>
/// <param name="CarrierTier">Carrier tier of the plan's assign-carrier rows (closed vocabulary).</param>
public sealed record ProceduralEraContent(
    string EraId,
    IReadOnlyList<string> RegularRoster,
    IReadOnlyList<TowerSpec> Towers,
    string CurrencyResource,
    int WarehouseLevel,
    string CarrierTier)
{
    /// <summary>
    /// Validates the pack: non-blank ids, a non-empty distinct roster, a
    /// tower catalog that resolves both plan tower lines, a non-blank
    /// currency and carrier tier.
    /// </summary>
    /// <exception cref="ArgumentException">Any rule above is violated.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EraId) || string.IsNullOrWhiteSpace(CurrencyResource)
            || string.IsNullOrWhiteSpace(CarrierTier))
        {
            throw new ArgumentException("EraId, CurrencyResource and CarrierTier must not be blank.");
        }

        ArgumentNullException.ThrowIfNull(RegularRoster);
        if (RegularRoster.Count == 0 || RegularRoster.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The regular roster must be non-empty and blank-free.");
        }

        if (RegularRoster.Distinct(StringComparer.Ordinal).Count() != RegularRoster.Count)
        {
            throw new ArgumentException("The regular roster contains duplicate ids.");
        }

        ArgumentNullException.ThrowIfNull(Towers);
        if (Towers.Count == 0)
        {
            throw new ArgumentException("The tower catalog must not be empty.");
        }
    }

    /// <summary>Sticker total of one tower id (cost_total); null when absent from the catalog.</summary>
    public double? TowerCost(string towerId) =>
        Towers.FirstOrDefault(t => t.Id == towerId)?.CostTotal;
}

/// <summary>
/// Constraint set of one generation (T-140 约束集): the size/terrain-ratio/
/// entry-count/grace bands the layout draw rolls inside, plus the reroll
/// budget. Host-supplied from content (RL7); the defaults live in
/// <c>game/content/campaign/procedural.json</c>, not in code. The
/// loader-compatible band pins water/rock to 0 — the handcrafted loading
/// face rejects nonzero rows for era1 (固定布局可建性口径), so the default
/// entry keeps every generated product loadable by the EXISTING pipeline
/// unchanged; wider bands exercise the reroll chain at the core level.
/// </summary>
/// <param name="WidthMin">Map width band lower bound.</param>
/// <param name="WidthMax">Map width band upper bound.</param>
/// <param name="HeightMin">Map height band lower bound.</param>
/// <param name="HeightMax">Map height band upper bound.</param>
/// <param name="ForestRatioMin">Forest ratio band lower bound.</param>
/// <param name="ForestRatioMax">Forest ratio band upper bound.</param>
/// <param name="RockRatioMin">Rock ratio band lower bound.</param>
/// <param name="RockRatioMax">Rock ratio band upper bound.</param>
/// <param name="WaterRatioMin">Water ratio band lower bound.</param>
/// <param name="WaterRatioMax">Water ratio band upper bound.</param>
/// <param name="EntryCountMin">Spawn-point count band lower bound.</param>
/// <param name="EntryCountMax">Spawn-point count band upper bound.</param>
/// <param name="GraceMin">First-wave grace band lower bound (non-teaching band ×1.5~2, §6).</param>
/// <param name="GraceMax">First-wave grace band upper bound.</param>
/// <param name="MaxRerolls">Reroll budget — attempts beyond the first that may be drawn before giving up.</param>
public sealed record ProceduralConstraints(
    int WidthMin,
    int WidthMax,
    int HeightMin,
    int HeightMax,
    double ForestRatioMin,
    double ForestRatioMax,
    double RockRatioMin,
    double RockRatioMax,
    double WaterRatioMin,
    double WaterRatioMax,
    int EntryCountMin,
    int EntryCountMax,
    double GraceMin,
    double GraceMax,
    int MaxRerolls)
{
    /// <summary>
    /// Validates the constraint set: non-degenerate ordered bands, ratio
    /// band legs inside [0,1] with per-attempt sums leaving plain terrain
    /// (sum ≤ 1 per the <see cref="EraCore.World.GridWorld"/> contract),
    /// entry counts ≥ 1, the grace band inside the §6 non-teaching band
    /// [1.5, 2.0] (procedural levels are never teaching levels), and a
    /// non-negative reroll budget.
    /// </summary>
    /// <exception cref="ArgumentException">Any rule above is violated.</exception>
    public void Validate()
    {
        if (WidthMin < 3 || HeightMin < 3 || WidthMax < WidthMin || HeightMax < HeightMin) // ci-allow: R7 T-140 最小图面 3×3（与手工关装载面 map ≥3×3 同界，T-085 schema 下界）
        {
            throw new ArgumentException(
                $"Size band is malformed ({WidthMin}..{WidthMax} × {HeightMin}..{HeightMax}).");
        }

        ValidateRatioBand("forest", ForestRatioMin, ForestRatioMax);
        ValidateRatioBand("rock", RockRatioMin, RockRatioMax);
        ValidateRatioBand("water", WaterRatioMin, WaterRatioMax);
        if (ForestRatioMax + RockRatioMax + WaterRatioMax > 1.0 + 1e-9)
        {
            throw new ArgumentException("Ratio band upper bounds must sum to at most 1.");
        }

        if (EntryCountMin < 1 || EntryCountMax < EntryCountMin)
        {
            throw new ArgumentException($"Entry count band is malformed ({EntryCountMin}..{EntryCountMax}).");
        }

        if (GraceMin < 1.5 - 1e-9 || GraceMax > 2.0 + 1e-9 || GraceMin > GraceMax)
        {
            throw new ArgumentException(
                $"Grace band must lie inside the non-teaching band [1.5, 2.0] (§6; got {GraceMin}..{GraceMax}).");
        }

        if (MaxRerolls < 0)
        {
            throw new ArgumentException($"MaxRerolls must be non-negative (got {MaxRerolls}).", nameof(MaxRerolls));
        }
    }

    private static void ValidateRatioBand(string name, double min, double max)
    {
        if (min < 0 || max > 1 || min > max)
        {
            throw new ArgumentException($"The {name} ratio band is malformed ({min}..{max}).");
        }
    }
}

/// <summary>
/// One generation request (T-140): run seed + era pack + difficulty tier +
/// constraint set. Pure input record — validated eagerly so a malformed
/// request fails before any draw.
/// </summary>
/// <param name="Seed">Run seed — the determinism root (same seed ⇒ same level).</param>
/// <param name="Tier">Difficulty tier row (threat derivation + wave knobs + build plan).</param>
/// <param name="Era">Era content pack of the target era.</param>
/// <param name="Constraints">Constraint set of the layout draw.</param>
public sealed record ProceduralRequest(
    long Seed,
    ProceduralTier Tier,
    ProceduralEraContent Era,
    ProceduralConstraints Constraints)
{
    /// <summary>
    /// Validates the whole request: tier/era/constraints self-validate, the
    /// tier's tower lines and boss row resolve against the era pack, and
    /// the boss id never collides with the regular roster (一线一种).
    /// </summary>
    /// <exception cref="ArgumentException">Any rule above is violated.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Tier);
        ArgumentNullException.ThrowIfNull(Era);
        ArgumentNullException.ThrowIfNull(Constraints);
        Tier.Validate();
        Era.Validate();
        Constraints.Validate();

        if (Era.TowerCost(Tier.ArrowTowerId) is not { } arrowCost || arrowCost <= 0)
        {
            throw new ArgumentException(
                $"Arrow tower '{Tier.ArrowTowerId}' is absent from the era tower catalog.", nameof(Tier));
        }

        if (Era.TowerCost(Tier.RockTowerId) is not { } rockCost || rockCost <= 0)
        {
            throw new ArgumentException(
                $"Rock tower '{Tier.RockTowerId}' is absent from the era tower catalog.", nameof(Tier));
        }

        if (Tier.BossEnemyId is not null && Era.RegularRoster.Contains(Tier.BossEnemyId))
        {
            throw new ArgumentException(
                $"Boss enemy '{Tier.BossEnemyId}' must not appear in the regular roster (一线一种).", nameof(Tier));
        }
    }
}
