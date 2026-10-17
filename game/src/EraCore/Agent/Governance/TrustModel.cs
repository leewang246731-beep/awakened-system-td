namespace EraCore.Agent.Governance;

/// <summary>
/// Three-tier trust badge (v3.2 §11.4 / F.4, thresholds frozen by
/// correction #15): 0~100 分 → <40 将信将疑 / 40~69 可托付 / ≥70 言听计从.
/// The badge modulates WORDING and suggestion frequency only — never
/// capability (F.4: 只调制主动性不影响功能).
/// </summary>
public enum TrustBadge
{
    /// <summary>Score &lt; 40 — 将信将疑 (skeptical): suggestions halved.</summary>
    Skeptical,

    /// <summary>Score 40..69 — 可托付 (trustworthy).</summary>
    Trustworthy,

    /// <summary>Score ≥ 70 — 言听计从 (devoted).</summary>
    Devoted,
}

/// <summary>
/// Content-facing tier keys — the single source of the badge→lowercase-key
/// mapping (story tables' tier names / finale trust lines). Strict: unknown
/// tiers throw (callers wanting a lenient fallback wrap this themselves —
/// see TrustBadgeNarrativeExtensions.PoolName).
/// </summary>
public static class TrustBadgeKeys
{
    public static string StrictKey(TrustBadge badge) => badge switch
    {
        TrustBadge.Skeptical => "skeptical",
        TrustBadge.Trustworthy => "trustworthy",
        TrustBadge.Devoted => "devoted",
        _ => throw new ArgumentOutOfRangeException(nameof(badge), badge, "unknown trust badge tier."),
    };
}

/// <summary>
/// Trust model (T-050, C-14 / feature F-076), frozen rules (v3.2
/// appendix F.4):
/// <list type="bullet">
/// <item>正确警报 +2 — trivial alarms score nothing (the CALLER declares
/// isTrivial; the model never guesses alarm quality).</item>
/// <item>误报 −5, capped per wave: at most one −5 lands per wave (每波
/// 封顶 — repeated false alarms in one wave are recorded but never
/// double-bill); alarm-jitter hits (actual threat within ±15 s, caller
/// declares withinAlarmJitter) are exempt (闹钟 ±15s 抖动豁免).</item>
/// <item>采纳守住 +3 / 失守 −3 (advice outcome: held vs lost).</item>
/// <item>Every wave end regresses the score one step toward 50
/// (向 50 回归 ±1; already at 50 → no move).</item>
/// <item>Score lives in [0, 100]; every mutation clamps.</item>
/// <item>跨时代重置: era jump resets to 50 and clears wave accumulators.</item>
/// <item>Score &lt; 40 → SuggestHalfFrequency (建议频率减半, 软性 — a
/// hint for the presenter, never a capability gate).</item>
/// </list>
/// </summary>
public sealed class TrustModel
{
    /// <summary>Score floor (F.4: 0~100).</summary>
    public const int MinScore = 0;

    /// <summary>Score ceiling (F.4: 0~100).</summary>
    public const int MaxScore = 100; // ci-allow: R7 T-050 信任度 0~100 为 v3.2 附录 F.4 冻结域常量（信任度量纲，非内容平衡数值）

    /// <summary>Correct non-trivial alarm bonus (F.4: +2).</summary>
    public const int CorrectAlarmBonus = 2;

    /// <summary>False-alarm penalty per occurrence (F.4: −5).</summary>
    public const int FalseAlarmPenalty = 5;

    /// <summary>False alarms per wave cap: one penalized occurrence per wave (F.4: 每波封顶; the cap equals the single-occurrence penalty — the only non-inventing reading of the frozen text).</summary>
    public const int FalseAlarmPerWaveCap = 5; // ci-allow: R7 T-050 误报每波封顶值=单次罚分（v3.2 附录 F.4 冻结域常量，非内容平衡数值）

    /// <summary>Advice adopted AND held bonus (F.4: +3).</summary>
    public const int AdviceHeldBonus = 3;

    /// <summary>Advice adopted but lost penalty (F.4: −3).</summary>
    public const int AdviceLostPenalty = 3;

    /// <summary>Regression anchor — also the era-jump reset value (F.4: 向 50 回归 / 跨时代重置 50).</summary>
    public const int RegressionTarget = 50; // ci-allow: R7 T-050 回归锚 50/跨时代重置 50 为 v3.2 附录 F.4 冻结域常量（信任度量纲，非内容平衡数值）

    /// <summary>Per-wave regression step toward the anchor (F.4: ±1).</summary>
    public const int RegressionStep = 1;

    /// <summary>Alarm-jitter grace window in seconds (F.4: 闹钟 ±15s 抖动豁免 — caller-side measurement, model-side exemption).</summary>
    public const int AlarmJitterGraceSeconds = 15; // ci-allow: R7 T-050 闹钟 ±15s 抖动豁免窗口为 v3.2 附录 F.4 冻结域常量（时间量纲，非内容平衡数值）

    /// <summary>Skeptical tier boundary: score &lt; 40 (F.4 correction #15).</summary>
    public const int SkepticalBelow = 40; // ci-allow: R7 T-050 三档徽记阈值 <40/40~69/≥70 为 v3.2 附录 F.4 冻结域常量（修正表 #15；信任度量纲）

    /// <summary>Devoted tier boundary: score ≥ 70 (F.4 correction #15).</summary>
    public const int DevotedAtLeast = 70; // ci-allow: R7 T-050 三档徽记阈值 <40/40~69/≥70 为 v3.2 附录 F.4 冻结域常量（修正表 #15；信任度量纲）

    private int _score;
    private int _falseAlarmsChargedThisWave;

    /// <summary>Creates a model at the reset value 50 (start of an era).</summary>
    public TrustModel()
    {
        _score = RegressionTarget;
    }

    /// <summary>Current score, always within [0, 100].</summary>
    public int Score => _score;

    /// <summary>Three-tier badge of the current score (F.4 thresholds).</summary>
    public TrustBadge Badge => _score < SkepticalBelow
        ? TrustBadge.Skeptical
        : _score < DevotedAtLeast ? TrustBadge.Trustworthy : TrustBadge.Devoted;

    /// <summary>Soft pacing hint: suggestion frequency halved below 40 (F.4: 低于 40 建议频率减半，软性).</summary>
    public bool SuggestHalfFrequency => _score < SkepticalBelow;

    /// <summary>Penalized false alarms recorded in the current wave (observability for the war report).</summary>
    public int FalseAlarmsThisWave => _falseAlarmsChargedThisWave;

    /// <summary>
    /// Correct-alarm outcome. Trivial alarms (caller-declared) score
    /// nothing — 平凡警报不计 (F.4).
    /// </summary>
    /// <param name="isTrivial">Caller's declaration that the alarm was trivial (e.g. routine wave the player already saw).</param>
    public void RecordCorrectAlarm(bool isTrivial)
    {
        if (isTrivial)
        {
            return;
        }

        _score = Guard.Clamp(_score + CorrectAlarmBonus, MinScore, MaxScore);
    }

    /// <summary>
    /// False-alarm outcome. Within the ±15 s alarm-jitter grace
    /// (caller-declared) the report is exempt — no penalty and it does
    /// not consume the per-wave cap. Beyond the grace, the first false
    /// alarm of the wave costs −5; further false alarms in the SAME wave
    /// are recorded but never double-bill (每波封顶).
    /// </summary>
    /// <param name="withinAlarmJitter">Caller's declaration that a real threat arrived within ±15 s of the alarm.</param>
    /// <returns>true when this call charged the penalty (first of the wave, outside the grace).</returns>
    public bool RecordFalseAlarm(bool withinAlarmJitter)
    {
        if (withinAlarmJitter)
        {
            return false;
        }

        if (_falseAlarmsChargedThisWave >= 1)
        {
            return false;
        }

        _falseAlarmsChargedThisWave++;
        _score = Guard.Clamp(_score - FalseAlarmPenalty, MinScore, MaxScore);
        return true;
    }

    /// <summary>
    /// Advice outcome (F.4: 采纳守住 +3、失守 −3): the player adopted the
    /// system's suggestion; held = it worked, lost = it failed.
    /// </summary>
    public void RecordAdviceOutcome(bool held)
    {
        _score = Guard.Clamp(
            _score + (held ? AdviceHeldBonus : -AdviceLostPenalty),
            MinScore, MaxScore);
    }

    /// <summary>
    /// Wave end (F.4: 每波向 50 回归 ±1): moves the score one step toward
    /// the anchor (no move when already there) and re-arms the per-wave
    /// false-alarm cap for the next wave.
    /// </summary>
    public void EndWave()
    {
        if (_score > RegressionTarget)
        {
            _score = Guard.Clamp(_score - RegressionStep, MinScore, MaxScore);
        }
        else if (_score < RegressionTarget)
        {
            _score = Guard.Clamp(_score + RegressionStep, MinScore, MaxScore);
        }

        _falseAlarmsChargedThisWave = 0;
    }

    /// <summary>
    /// Era jump (F.4: 跨时代重置 50): score resets to the anchor and wave
    /// accumulators clear. The &lt;40 halving and the reset connect
    /// naturally — a new era always starts 可托付-neutral.
    /// </summary>
    public void ResetForNewEra()
    {
        _score = RegressionTarget;
        _falseAlarmsChargedThisWave = 0;
    }
}
