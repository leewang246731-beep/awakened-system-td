using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;
using EraCore.Economy;

namespace EraCore.Progression;

/// <summary>
/// The offline rules block of signin.json (appendix G.4 四规则 frozen
/// values + the structural knobs): income cap/diminishing curve, catch-up
/// turn cap and pacing, invasion chance.
/// </summary>
/// <param name="FullRateHours">Full-rate offline window — 4h (G.4 离线收益 4h 封顶).</param>
/// <param name="DiminishedRate">Rate beyond the window — 25% (G.4 超出 25% 递减).</param>
/// <param name="EquivalentCapHours">Equivalent-hours ceiling — 8h (G.4 再封顶 8h 等效).</param>
/// <param name="CatchupTurnCap">Max catch-up turns — 50 (G.4 追帧 ≤50 回合).</param>
/// <param name="TurnsPerHour">Turns accrued per absent hour (structural knob).</param>
/// <param name="InvasionChancePerTurn">Per-turn invasion probability (structural knob).</param>
/// <param name="Income">Offline income lines (resource per effective hour).</param>
public sealed record OfflineRules(
    double FullRateHours,
    double DiminishedRate,
    double EquivalentCapHours,
    int CatchupTurnCap,
    double TurnsPerHour,
    double InvasionChancePerTurn,
    IReadOnlyList<OfflineIncomeSpec> Income);

/// <summary>One offline income line: resource units per effective hour.</summary>
/// <param name="Resource">Resource id string.</param>
/// <param name="PerHour">Units per effective hour.</param>
public sealed record OfflineIncomeLine(string? Resource, double? PerHour);

/// <summary>
/// Validated offline income line (materialized from
/// <see cref="OfflineIncomeLine"/>): resource key + non-negative rate.
/// </summary>
/// <param name="Resource">Granted resource.</param>
/// <param name="PerHour">Units per effective hour.</param>
public sealed record OfflineIncomeSpec(ResourceKey Resource, double PerHour);

/// <summary>One sign-in milestone reward (decorative only, F-053).</summary>
/// <param name="Id">Reward id.</param>
/// <param name="RewardClass">Whitelisted decorative class.</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
/// <param name="Aliases">Colloquial names.</param>
public sealed record SignInReward(
    string Id,
    string RewardClass,
    string? NameZh = null,
    string? NameEn = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// One consecutive-login milestone (F-053 连续登录里程碑): the streak day
/// the reward unlocks at. 断签不清零 — the streak only ever grows, missing
/// days merely pause it.
/// </summary>
/// <param name="Day">Streak day this milestone triggers on.</param>
/// <param name="Reward">The decorative reward granted.</param>
public sealed record SignInMilestone(int Day, SignInReward Reward);

/// <summary>JSON shape of signin.json offline block.</summary>
public sealed record OfflineRulesDto(
    double? FullRateHours,
    double? DiminishedRate,
    double? EquivalentCapHours,
    int? CatchupTurnCap,
    double? TurnsPerHour,
    double? InvasionChancePerTurn,
    IReadOnlyList<OfflineIncomeLine>? Income);

/// <summary>JSON shape of one signin.json milestones[] row.</summary>
public sealed record SignInMilestoneDto(int? Day, SignInRewardDto? Reward);

/// <summary>JSON shape of one milestone reward block.</summary>
public sealed record SignInRewardDto(
    string? Id,
    string? RewardClass,
    string? NameZh,
    string? NameEn,
    IReadOnlyList<string>? Aliases);

/// <summary>Root shape of signin.json.</summary>
/// <param name="ContentType">K-2 content kind declaration ("DropTable", thin mapping).</param>
/// <param name="Offline">The rules block.</param>
/// <param name="Milestones">Consecutive-login milestones.</param>
public sealed record SignInDocument(
    string? ContentType,
    OfflineRulesDto? Offline,
    IReadOnlyList<SignInMilestoneDto>? Milestones);

/// <summary>
/// Validated sign-in/offline document: the rules plus the milestone table.
/// </summary>
/// <param name="Rules">Offline rules (G.4).</param>
/// <param name="Milestones">Milestones in ascending day order.</param>
public sealed record SignInTable(OfflineRules Rules, IReadOnlyList<SignInMilestone> Milestones);

/// <summary>
/// Loader + validator of the sign-in document (T-073): positive finite
/// rules, ascending unique milestone days ≥ 1, every reward inside the
/// decorative <see cref="RewardWhitelist"/> (F-053: 里程碑仅装饰性内容与
/// 蓝图 — parse-time rejection of balance-touching rewards).
/// </summary>
public static class SignInData
{
    /// <summary>
    /// Parses and validates a signin document. Damaged payloads throw
    /// <see cref="JsonException"/>; violations throw
    /// <see cref="ArgumentException"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">json is null.</exception>
    /// <exception cref="JsonException">Payload is not valid JSON for the shape.</exception>
    /// <exception cref="ArgumentException">Validation failed (message names the row).</exception>
    public static SignInTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SignInDocument document = JsonSerializer.Deserialize(
            json, SignInJsonContext.Default.SignInDocument)
            ?? throw new JsonException("Sign-in document deserialized to null.");

        OfflineRulesDto raw = document.Offline
            ?? throw new ArgumentException("Sign-in document is missing its offline rules block.");

        double fullRateHours = raw.FullRateHours ?? -1.0;
        double diminishedRate = raw.DiminishedRate ?? -1.0;
        double equivalentCapHours = raw.EquivalentCapHours ?? -1.0;
        double turnsPerHour = raw.TurnsPerHour ?? -1.0;
        double invasionChance = raw.InvasionChancePerTurn ?? -1.0;
        int catchupTurnCap = raw.CatchupTurnCap ?? 0;
        if (fullRateHours <= 0 || diminishedRate < 0 || equivalentCapHours <= 0
            || turnsPerHour <= 0 || invasionChance < 0 || invasionChance > 1
            || catchupTurnCap < 1)
        {
            throw new ArgumentException("Offline rules block carries an out-of-band value (hours/rates/turn cap must be positive, chance in [0,1]).");
        }

        var income = new List<OfflineIncomeSpec>();
        foreach (OfflineIncomeLine? line in raw.Income ?? Array.Empty<OfflineIncomeLine>())
        {
            if (line is null || string.IsNullOrWhiteSpace(line.Resource) || line.PerHour is < 0)
            {
                throw new ArgumentException("An offline income line is malformed (resource id / per_hour).");
            }

            income.Add(new OfflineIncomeSpec(new ResourceKey(line.Resource.Trim()), line.PerHour ?? 0.0));
        }

        var milestones = new List<SignInMilestone>();
        foreach (SignInMilestoneDto? row in document.Milestones ?? Array.Empty<SignInMilestoneDto>())
        {
            if (row is null || row.Day is < 1)
            {
                throw new ArgumentException("A sign-in milestone is missing its day (≥ 1).");
            }

            SignInRewardDto? reward = row.Reward
                ?? throw new ArgumentException($"Milestone day {row.Day} is missing its reward block.");
            if (string.IsNullOrWhiteSpace(reward.Id) || string.IsNullOrWhiteSpace(reward.RewardClass))
            {
                throw new ArgumentException($"Milestone day {row.Day} reward is missing id/reward_class.");
            }

            if (!RewardWhitelist.IsAllowed(reward.RewardClass))
            {
                throw new ArgumentException(
                    $"Milestone day {row.Day} reward_class '{reward.RewardClass}' is outside the frozen decorative whitelist {string.Join("/", RewardWhitelist.Classes)} (F-053/R9).");
            }

            milestones.Add(new SignInMilestone(row.Day ?? 0, new SignInReward(
                reward.Id.Trim(), reward.RewardClass.Trim(), reward.NameZh, reward.NameEn, reward.Aliases)));
        }

        milestones.Sort((left, right) => left.Day.CompareTo(right.Day));
        for (int i = 1; i < milestones.Count; i++)
        {
            if (milestones[i].Day == milestones[i - 1].Day)
            {
                throw new ArgumentException($"Milestone day {milestones[i].Day} is declared twice.");
            }
        }

        return new SignInTable(
            new OfflineRules(
                fullRateHours,
                diminishedRate,
                equivalentCapHours,
                catchupTurnCap,
                turnsPerHour,
                invasionChance,
                income),
            milestones);
    }
}

/// <summary>One offline income banking line of a settlement.</summary>
/// <param name="Resource">Granted resource.</param>
/// <param name="Granted">Offered amount (rate × effective hours).</param>
/// <param name="Accepted">Accepted by the pool (入池只计上限内).</param>
/// <param name="Refused">Refused above the headroom.</param>
public sealed record OfflineIncomeGrant(ResourceKey Resource, double Granted, double Accepted, double Refused);

/// <summary>
/// One invasion event of the catch-up simulation (G.4 rule 2): always of
/// the CURRENT era — the record carries the era it belongs to, and the
/// engine stamps the campaign's era, never anything else.
/// </summary>
/// <param name="TurnIndex">Catch-up turn the invasion fired on (1-based).</param>
/// <param name="EraIndex">Era of the invasion — equals the current campaign era.</param>
public sealed record InvasionRecord(int TurnIndex, int EraIndex);

/// <summary>
/// Result of one absence settlement (G.4 rules 3+4): the raw and effective
/// absence, the income lines, the catch-up turns simulated and the
/// invasions they spawned.
/// </summary>
/// <param name="InCampaignMode">False when the settlement ran while a level was in progress (rule 1 exemption — zero accrual).</param>
/// <param name="Enabled">Whether the offline system is on (feature flag).</param>
/// <param name="AbsentHours">Raw hours since the last settlement anchor.</param>
/// <param name="EffectiveHours">Capped/diminished equivalent hours actually paid.</param>
/// <param name="Income">Income banking lines (empty when none).</param>
/// <param name="TurnsSimulated">Catch-up turns simulated (≤ CatchupTurnCap).</param>
/// <param name="TurnCapped">Whether the raw turn count was clamped to the cap.</param>
/// <param name="Invasions">Invasion events of the catch-up turns.</param>
public sealed record OfflineSettlement(
    bool InCampaignMode,
    bool Enabled,
    double AbsentHours,
    double EffectiveHours,
    IReadOnlyList<OfflineIncomeGrant> Income,
    int TurnsSimulated,
    bool TurnCapped,
    IReadOnlyList<InvasionRecord> Invasions);

/// <summary>
/// Result of one daily check-in (F-053): whether it was a new login day,
/// the standing counters and the milestones newly claimed.
/// </summary>
/// <param name="NewDay">True when this call was the day's first check-in.</param>
/// <param name="TotalLoginDays">Total distinct login days (never decreases).</param>
/// <param name="ConsecutiveDays">Consecutive-login streak (断签不清零 — pauses, never resets).</param>
/// <param name="NewlyClaimed">Milestones whose streak day was reached by this check-in.</param>
public sealed record SignInCheckIn(
    bool NewDay,
    int TotalLoginDays,
    int ConsecutiveDays,
    IReadOnlyList<SignInMilestone> NewlyClaimed);

/// <summary>Serializable state of the sign-in ledger.</summary>
/// <param name="TotalLoginDays">Distinct login days so far.</param>
/// <param name="ConsecutiveDays">Current streak value.</param>
/// <param name="LastCheckInDayIndex">Day index (NowMs ÷ 86400000) of the last check-in; −1 before the first.</param>
/// <param name="ClaimedMilestoneIds">Milestone rewards already granted.</param>
public sealed record SignInState(
    int TotalLoginDays,
    int ConsecutiveDays,
    long LastCheckInDayIndex,
    IReadOnlyList<string> ClaimedMilestoneIds);

/// <summary>
/// 离线岁月 engine (T-073, F-053; appendix G.4 四规则 as the base
/// semantics, v3.x packaged as 每日签到): the campaign-interface turn
/// clock, the 4h-cap/25%-diminishing/8h-equivalent offline income curve,
/// the ≤50-turn catch-up with era-locked invasions, and the daily check-in
/// with consecutive-login milestones (decorative only, 断签不清零). All
/// time enters through <see cref="IClock"/> (CS-1.4); all randomness
/// through <see cref="IRng"/>. The feature flag (G.4 可关) makes every
/// settlement a zero-accrual no-op.
/// </summary>
public sealed class OfflineTurn
{
    /// <summary>Milliseconds of one UTC day — the check-in boundary.</summary>
    public const long MsPerDay = 86_400_000L;

    private static readonly OfflineSettlement DisabledSettlement = new(
        true, false, 0.0, 0.0, Array.Empty<OfflineIncomeGrant>(), 0, false, Array.Empty<InvasionRecord>());

    private readonly SignInTable _table;
    private readonly IClock _clock;
    private readonly ResourceStock _pool;
    private readonly IRng _rng;
    private readonly int _eraIndex;
    private readonly bool _enabled;

    private bool _inLevel;
    private long _anchorMs;
    private int _totalLoginDays;
    private int _consecutiveDays;
    private long _lastCheckInDay = -1;
    private readonly HashSet<string> _claimedMilestones = new();

    /// <param name="table">Validated table (<see cref="SignInData.Parse"/> output).</param>
    /// <param name="clock">Wall-clock port (K-1) — the only time source.</param>
    /// <param name="pool">Campaign pool the offline income deposits into.</param>
    /// <param name="rng">Seeded random port for invasion draws.</param>
    /// <param name="eraIndex">Current campaign era — every invasion is locked to it (G.4 rule 2).</param>
    /// <param name="enabled">Feature flag (G.4 可关): false freezes every settlement.</param>
    /// <exception cref="ArgumentNullException">table, clock, pool or rng is null.</exception>
    /// <exception cref="ArgumentException">era outside 1..6.</exception>
    public OfflineTurn(SignInTable table, IClock clock, ResourceStock pool, IRng rng, int eraIndex, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(rng);
        if (eraIndex is < 1 or > 6)
        {
            throw new ArgumentException($"Era index must be 1..6 (got {eraIndex}).", nameof(eraIndex));
        }

        _table = table;
        _clock = clock;
        _pool = pool;
        _rng = rng;
        _eraIndex = eraIndex;
        _enabled = enabled;
        _anchorMs = clock.NowMs;
    }

    /// <summary>Current sign-in ledger state.</summary>
    public SignInState State => new(
        _totalLoginDays, _consecutiveDays, _lastCheckInDay, _claimedMilestones.ToList());

    /// <summary>Whether a level is currently in progress (turn clock frozen).</summary>
    public bool InLevel => _inLevel;

    /// <summary>
    /// Freezes the turn clock for a running level (G.4 rule 1: 回合钟只在
    /// 战役界面推进，进行中关卡豁免): settles the campaign-interface time up
    /// to NOW first, so nothing between the last settlement and the level
    /// start is lost, then stops accruing.
    /// </summary>
    public OfflineSettlement EnterLevel()
    {
        OfflineSettlement settlement = SettleAbsence();
        _inLevel = true;
        return settlement;
    }

    /// <summary>
    /// Resumes the turn clock on the campaign interface: the time spent in
    /// the level is EXEMPT — the anchor restarts at now.
    /// </summary>
    public void ReturnToCampaign()
    {
        _inLevel = false;
        _anchorMs = _clock.NowMs;
    }

    /// <summary>
    /// Settles the absence since the last anchor (G.4 rules 3+4): income at
    /// full rate up to <c>full_rate_hours</c>, 25% beyond, equivalent hours
    /// capped at <c>equivalent_cap_hours</c>; catch-up turns ≤
    /// <c>catchup_turn_cap</c>; invasion draws per turn. While a level is
    /// in progress (or the feature flag is off) the settlement accrues
    /// nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The clock moved backwards.</exception>
    public OfflineSettlement SettleAbsence()
    {
        if (!_enabled)
        {
            return DisabledSettlement;
        }

        if (_inLevel)
        {
            return new OfflineSettlement(
                false, true, 0.0, 0.0, Array.Empty<OfflineIncomeGrant>(), 0, false, Array.Empty<InvasionRecord>());
        }

        long nowMs = _clock.NowMs;
        long elapsedMs = nowMs - _anchorMs;
        if (elapsedMs < 0)
        {
            throw new InvalidOperationException($"Clock moved backwards (anchor {_anchorMs} > now {nowMs}).");
        }

        _anchorMs = nowMs;
        double absentHours = elapsedMs / 3_600_000.0;
        double effectiveHours = EffectiveHoursFor(_table.Rules, absentHours);

        var income = new List<OfflineIncomeGrant>();
        foreach (OfflineIncomeSpec line in _table.Rules.Income)
        {
            double granted = line.PerHour * effectiveHours;
            if (granted <= 0)
            {
                continue;
            }

            double accepted = _pool.Deposit(line.Resource, granted);
            income.Add(new OfflineIncomeGrant(line.Resource, granted, accepted, granted - accepted));
        }

        int rawTurns = (int)Math.Floor(absentHours * _table.Rules.TurnsPerHour);
        bool capped = rawTurns > _table.Rules.CatchupTurnCap;
        int turns = Math.Min(rawTurns, _table.Rules.CatchupTurnCap);

        var invasions = new List<InvasionRecord>();
        for (int turn = 1; turn <= turns; turn++)
        {
            if (_rng.NextDouble() < _table.Rules.InvasionChancePerTurn)
            {
                invasions.Add(new InvasionRecord(turn, _eraIndex));
            }
        }

        return new OfflineSettlement(true, true, absentHours, effectiveHours, income, turns, capped, invasions);
    }

    /// <summary>
    /// The daily check-in (F-053 每日签到): first call of a UTC day counts
    /// the login. A missed day does NOT reset the streak (断签不清零) — the
    /// counter pauses and the next login resumes from its standing value;
    /// milestones fire when the streak reaches their day, once each.
    /// </summary>
    public SignInCheckIn CheckIn()
    {
        long day = _clock.NowMs / MsPerDay;
        if (_lastCheckInDay == day)
        {
            return new SignInCheckIn(false, _totalLoginDays, _consecutiveDays, Array.Empty<SignInMilestone>());
        }

        if (_lastCheckInDay >= 0 && day == _lastCheckInDay + 1)
        {
            _consecutiveDays++;
        }
        else if (_lastCheckInDay < 0)
        {
            _consecutiveDays = 1;
        }
        // else: 断签 — streak stays (不清零), only TotalLoginDays grows.

        _totalLoginDays++;
        _lastCheckInDay = day;

        var claimed = new List<SignInMilestone>();
        foreach (SignInMilestone milestone in _table.Milestones)
        {
            if (_consecutiveDays >= milestone.Day && _claimedMilestones.Add(milestone.Reward.Id))
            {
                claimed.Add(milestone);
            }
        }

        return new SignInCheckIn(true, _totalLoginDays, _consecutiveDays, claimed);
    }

    /// <summary>
    /// The frozen offline curve (G.4 rule 4): full rate up to
    /// <paramref name="absentHours"/> = full-rate window, diminished rate
    /// beyond, equivalent hours capped — with the shipped 4h/25%/8h
    /// numbers: 6h away pays 4.5h, 20h away pays exactly 8h, everything
    /// beyond 20h pays 8h.
    /// </summary>
    public static double EffectiveHoursFor(OfflineRules rules, double absentHours)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (absentHours <= rules.FullRateHours)
        {
            return Math.Max(0.0, absentHours);
        }

        double effective = rules.FullRateHours + (rules.DiminishedRate * (absentHours - rules.FullRateHours));
        return Math.Min(effective, rules.EquivalentCapHours);
    }
}

/// <summary>
/// System.Text.Json source-generation context for sign-in content (AOT
/// red line, 07 D-05). Kept in this file as a secondary type so its
/// coverage rides on OfflineTurnTests (T2 file mapping).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SignInDocument))]
[JsonSerializable(typeof(OfflineRulesDto))]
[JsonSerializable(typeof(OfflineIncomeLine))]
[JsonSerializable(typeof(SignInMilestoneDto))]
[JsonSerializable(typeof(SignInRewardDto))]
public sealed partial class SignInJsonContext : JsonSerializerContext;
