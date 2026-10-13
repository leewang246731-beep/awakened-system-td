using EraCore.Agent.Governance;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-076] Trust model (T-050): every frozen F.4 rule with positive
// AND negative cases — correct alarms (+2, trivial ignored), false alarms
// (−5, per-wave capped, ±15s jitter exempt), advice outcomes (±3), the
// per-wave ±1 regression toward 50, the 0..100 clamp, era reset, the
// <40 halving hint and the three-tier badge thresholds.

/// <summary>[C-14][F-076] Trust rules, clamps, regression, reset, badges.</summary>
public class TrustModelTests
{
    // ---- 起点 ----------------------------------------------------------------

    [Fact] // legal — a model starts at the neutral reset value 50 (可托付-neutral)
    public void New_model_starts_at_50()
    {
        var trust = new TrustModel();
        Assert.Equal(50, trust.Score);
        Assert.Equal(TrustBadge.Trustworthy, trust.Badge);
        Assert.False(trust.SuggestHalfFrequency);
    }

    // ---- 规则 1：正确警报 +2（平凡不计）---------------------------------------

    [Fact] // legal — a non-trivial correct alarm earns +2
    public void Correct_alarm_earns_plus_two()
    {
        var trust = new TrustModel();
        trust.RecordCorrectAlarm(isTrivial: false);
        Assert.Equal(52, trust.Score);
    }

    [Fact] // negative — a trivial correct alarm earns nothing (平凡警报不计)
    public void Trivial_correct_alarm_earns_nothing()
    {
        var trust = new TrustModel();
        trust.RecordCorrectAlarm(isTrivial: true);
        Assert.Equal(50, trust.Score);
    }

    [Fact] // legal — three non-trivial correct alarms stack to +6
    public void Correct_alarms_stack()
    {
        var trust = new TrustModel();
        for (int i = 0; i < 3; i++)
        {
            trust.RecordCorrectAlarm(isTrivial: false);
        }

        Assert.Equal(56, trust.Score);
    }

    // ---- 规则 2：误报 −5（每波封顶；±15s 抖动豁免）-------------------------------

    [Fact] // legal — a false alarm beyond the grace window costs −5
    public void False_alarm_costs_minus_five()
    {
        var trust = new TrustModel();
        bool charged = trust.RecordFalseAlarm(withinAlarmJitter: false);
        Assert.True(charged);
        Assert.Equal(45, trust.Score);
        Assert.Equal(1, trust.FalseAlarmsThisWave);
    }

    [Fact] // negative — repeated false alarms in the SAME wave never double-bill (每波封顶)
    public void False_alarms_are_capped_per_wave()
    {
        var trust = new TrustModel();
        Assert.True(trust.RecordFalseAlarm(withinAlarmJitter: false));  // −5
        Assert.False(trust.RecordFalseAlarm(withinAlarmJitter: false)); // capped
        Assert.False(trust.RecordFalseAlarm(withinAlarmJitter: false)); // capped
        Assert.Equal(45, trust.Score);
        Assert.Equal(1, trust.FalseAlarmsThisWave);
    }

    [Fact] // legal — a new wave re-arms the cap (EndWave closes the wave)
    public void New_wave_re_arms_the_false_alarm_cap()
    {
        var trust = new TrustModel();
        trust.RecordFalseAlarm(withinAlarmJitter: false); // 45
        trust.EndWave();                                  // regression 45 → 46, cap re-armed
        Assert.True(trust.RecordFalseAlarm(withinAlarmJitter: false));
        Assert.Equal(41, trust.Score); // 46 − 5
    }

    [Fact] // legal — an alarm whose threat lands within ±15s is exempt (抖动豁免)
    public void Alarm_jitter_hit_is_exempt()
    {
        var trust = new TrustModel();
        bool charged = trust.RecordFalseAlarm(withinAlarmJitter: true);
        Assert.False(charged);
        Assert.Equal(50, trust.Score);
        Assert.Equal(0, trust.FalseAlarmsThisWave);
    }

    [Fact] // legal — a jitter hit does not consume the per-wave cap
    public void Jitter_hit_does_not_consume_the_cap()
    {
        var trust = new TrustModel();
        trust.RecordFalseAlarm(withinAlarmJitter: true);  // exempt, cap intact
        Assert.True(trust.RecordFalseAlarm(withinAlarmJitter: false)); // still charges −5
        Assert.Equal(45, trust.Score);
    }

    // ---- 规则 3：采纳守住 +3 / 失守 −3 ------------------------------------------

    [Fact] // legal — advice adopted and held earns +3
    public void Advice_held_earns_plus_three()
    {
        var trust = new TrustModel();
        trust.RecordAdviceOutcome(held: true);
        Assert.Equal(53, trust.Score);
    }

    [Fact] // legal — advice adopted but lost costs −3
    public void Advice_lost_costs_minus_three()
    {
        var trust = new TrustModel();
        trust.RecordAdviceOutcome(held: false);
        Assert.Equal(47, trust.Score);
    }

    // ---- 规则 4：每波向 50 回归 ±1 ----------------------------------------------

    [Theory] // legal — regression moves one step toward 50, from either side
    [InlineData(52, 51)]
    [InlineData(51, 50)]
    [InlineData(48, 49)]
    [InlineData(49, 50)]
    public void Wave_end_regresses_one_step_toward_50(int start, int expected)
    {
        var trust = new TrustModel();
        DriveScoreTo(trust, start);
        Assert.Equal(start, trust.Score); // arrange: exact start reached

        trust.EndWave();
        Assert.Equal(expected, trust.Score);
    }

    [Fact] // boundary — already at 50: no move (回归 ±1 has no direction at the anchor)
    public void Wave_end_at_anchor_does_not_move()
    {
        var trust = new TrustModel();
        trust.EndWave();
        Assert.Equal(50, trust.Score);
    }

    [Fact] // legal — regression alternates with events without losing the anchor direction
    public void Regression_and_events_interleave_correctly()
    {
        var trust = new TrustModel();
        trust.RecordCorrectAlarm(isTrivial: false); // 52
        trust.EndWave();                            // 51
        trust.RecordAdviceOutcome(held: false);     // 48
        trust.EndWave();                            // 49
        trust.EndWave();                            // 50
        trust.EndWave();                            // 50 (anchor)
        Assert.Equal(50, trust.Score);
    }

    // ---- 规则 5：0~100 夹取 -----------------------------------------------------

    [Fact] // boundary — the score clamps at the 100 ceiling
    public void Score_clamps_at_ceiling()
    {
        var trust = new TrustModel();
        for (int i = 0; i < 20; i++)
        {
            trust.RecordAdviceOutcome(held: true); // 50 + 3×20 = 110 → clamp
        }

        Assert.Equal(100, trust.Score);
    }

    [Fact] // boundary — the score clamps at the 0 floor
    public void Score_clamps_at_floor()
    {
        var trust = new TrustModel();
        for (int i = 0; i < 20; i++)
        {
            trust.RecordAdviceOutcome(held: false); // 50 − 3×20 = −10 → clamp
        }

        Assert.Equal(0, trust.Score);
    }

    [Fact] // boundary — clamped extremes still regress toward 50 one step per wave
    public void Clamped_extremes_still_regress()
    {
        var trust = new TrustModel();
        for (int i = 0; i < 20; i++)
        {
            trust.RecordAdviceOutcome(held: true);
        }

        trust.EndWave();
        Assert.Equal(99, trust.Score);
    }

    // ---- 规则 6：跨时代重置 50 ---------------------------------------------------

    [Fact] // legal — era reset returns the score to 50 and clears wave accumulators
    public void Era_reset_returns_to_50_and_re_arms_the_cap()
    {
        var trust = new TrustModel();
        for (int i = 0; i < 10; i++)
        {
            trust.RecordAdviceOutcome(held: true); // 80
        }

        trust.ResetForNewEra();
        Assert.Equal(50, trust.Score);
        Assert.Equal(TrustBadge.Trustworthy, trust.Badge);
        Assert.Equal(0, trust.FalseAlarmsThisWave);

        // the cap re-armed: a false alarm in the new era charges again
        Assert.True(trust.RecordFalseAlarm(withinAlarmJitter: false));
        Assert.Equal(45, trust.Score);
    }

    [Fact] // legal — era reset from below the halving line clears the halving hint too
    public void Era_reset_clears_the_halving_hint()
    {
        var trust = new TrustModel();
        DriveScoreTo(trust, 39);
        Assert.True(trust.SuggestHalfFrequency);

        trust.ResetForNewEra();
        Assert.False(trust.SuggestHalfFrequency);
    }

    // ---- 规则 7：三档徽记 + <40 减半提示 -----------------------------------------

    [Theory] // legal — badge boundaries: <40 skeptical, 40~69 trustworthy, ≥70 devoted (correction #15)
    [InlineData(39, TrustBadge.Skeptical, true)]
    [InlineData(40, TrustBadge.Trustworthy, false)]
    [InlineData(69, TrustBadge.Trustworthy, false)]
    [InlineData(70, TrustBadge.Devoted, false)]
    [InlineData(100, TrustBadge.Devoted, false)]
    [InlineData(0, TrustBadge.Skeptical, true)]
    public void Badge_boundaries_follow_the_frozen_thresholds(
        int score, TrustBadge expectedBadge, bool expectedHalving)
    {
        var trust = new TrustModel();
        DriveScoreTo(trust, score);
        Assert.Equal(score, trust.Score); // arrange: exact boundary reached

        Assert.Equal(expectedBadge, trust.Badge);
        Assert.Equal(expectedHalving, trust.SuggestHalfFrequency);
    }

    [Fact] // legal — the frozen constants carry their F.4 anchors (drift guard)
    public void Frozen_constants_match_f4()
    {
        Assert.Equal(2, TrustModel.CorrectAlarmBonus);
        Assert.Equal(5, TrustModel.FalseAlarmPenalty);
        Assert.Equal(3, TrustModel.AdviceHeldBonus);
        Assert.Equal(3, TrustModel.AdviceLostPenalty);
        Assert.Equal(50, TrustModel.RegressionTarget);
        Assert.Equal(1, TrustModel.RegressionStep);
        Assert.Equal(15, TrustModel.AlarmJitterGraceSeconds);
        Assert.Equal(40, TrustModel.SkepticalBelow);
        Assert.Equal(70, TrustModel.DevotedAtLeast);
        Assert.Equal(100, TrustModel.MaxScore);
        Assert.Equal(0, TrustModel.MinScore);
    }

    /// <summary>
    /// Drives the score to an exact value using ONLY the model's own
    /// scoring rules (held advice +3 / lost advice −3 / correct alarm +2 /
    /// false alarm −5, at most one per wave) — no test backdoor on the
    /// score itself. Greedy over residues mod 3: ±2 resolves via
    /// alarm/false-alarm, ±1 via a +3→−5 or +2→−3 ladder; a guarded loop
    /// fails loudly instead of hanging on any unreachable residue.
    /// </summary>
    private static void DriveScoreTo(TrustModel trust, int target)
    {
        for (int guard = 0; guard < 100 && trust.Score != target; guard++)
        {
            int delta = target - trust.Score;
            if (delta >= 3)
            {
                trust.RecordAdviceOutcome(held: true);
            }
            else if (delta <= -3)
            {
                trust.RecordAdviceOutcome(held: false);
            }
            else if (delta == 2 || delta == -1)
            {
                trust.RecordCorrectAlarm(isTrivial: false); // +2 lands 2; from −1 leaves −3 for advice
            }
            else if (delta == 1)
            {
                trust.RecordAdviceOutcome(held: true); // overshoot to −2, resolved next pass
            }
            else if (delta == -2)
            {
                if (!trust.RecordFalseAlarm(withinAlarmJitter: false))
                {
                    trust.EndWave(); // cap already consumed in this wave: close it (±1) and retry
                }
            }
        }

        Assert.Equal(target, trust.Score);
    }
}
