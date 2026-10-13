using System.Text.Json;
using EraCore.Agent.Governance;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Journal;
using Xunit;

namespace EraCore.Tests.Agent.Governance;

// [C-14][F-064] System point ledger (T-050): the full rule matrix of
// v3.2 §11.4 — ceiling 1+2×E (E1 off, E2=5 … E6=13), wave-start refill,
// Boss-wave half refill (向下取整) + combat ×2, pricing from the frozen
// tariff, the per-wave ⌊cap×40%⌋ spend line, never-bill-on-failure,
// anti-forged-refill monotonicity, and the JSONL audit trail.

/// <summary>[C-14][F-064] Budget chokepoint: ceiling/refill/Boss/40%/audit matrix.</summary>
public class SystemPointLedgerTests
{
    private static SystemPointLedger NewLedger(int era) => new(era);

    // ---- 组 1：上限公式与 E1 关闭 ----------------------------------------

    [Theory] // legal — ceiling = 1 + 2 × era (E2=5 → E6=13; §11.4)
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    [InlineData(3, 7)]
    [InlineData(4, 9)]
    [InlineData(5, 11)]
    [InlineData(6, 13)]
    public void Ceiling_follows_one_plus_two_times_era(int era, int expectedCeiling)
    {
        var ledger = NewLedger(era);
        Assert.Equal(expectedCeiling, ledger.Ceiling);
    }

    [Theory] // legal/boundary — points enabled from E2 on, off in E1 (§11.4 E1 不开启)
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    public void Enabled_starts_from_era_two(int era, bool expectedEnabled)
    {
        Assert.Equal(expectedEnabled, NewLedger(era).Enabled);
    }

    [Theory] // illegal — era domain is 1..6, anything else is refused at construction
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public void Constructor_refuses_era_out_of_domain(int era)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewLedger(era));
    }

    // ---- 组 2：每波回满 + Boss 变体 ---------------------------------------

    [Theory] // legal — normal wave start refills to the ceiling, every era (§11.4 每波开始回满至上限)
    [InlineData(2, 5)]
    [InlineData(3, 7)]
    [InlineData(4, 9)]
    [InlineData(5, 11)]
    [InlineData(6, 13)]
    public void Normal_wave_refills_to_ceiling(int era, int expectedCeiling)
    {
        var ledger = NewLedger(era);
        Assert.True(ledger.BeginWave(waveNumber: 1, isBossWave: false));
        Assert.Equal(expectedCeiling, ledger.Points);
    }

    [Theory] // legal — Boss wave refill target = ⌊ceiling × 50%⌋ (向下取整, §11.4)
    [InlineData(2, 2)]  // ⌊5/2⌋
    [InlineData(3, 3)]  // ⌊7/2⌋
    [InlineData(4, 4)]  // ⌊9/2⌋
    [InlineData(5, 5)]  // ⌊11/2⌋
    [InlineData(6, 6)]  // ⌊13/2⌋
    public void Boss_wave_refills_to_half_ceiling_floored(int era, int expectedTarget)
    {
        var ledger = NewLedger(era);
        Assert.True(ledger.BeginWave(waveNumber: 1, isBossWave: true));
        Assert.Equal(expectedTarget, ledger.Points);
    }

    [Fact] // boundary — refill fills but never confiscates: points held above a halved Boss target stay
    public void Boss_refill_never_confiscates_points_above_the_halved_target()
    {
        var ledger = NewLedger(era: 2);
        ledger.BeginWave(1, isBossWave: false);
        Assert.Equal(5, ledger.Points);

        Assert.True(ledger.BeginWave(2, isBossWave: true)); // target ⌊5/2⌋=2, balance 5 stays
        Assert.Equal(5, ledger.Points);
    }

    [Fact] // legal — a wave that drained points above the halved Boss target keeps them (refill fills, never cuts)
    public void Boss_refill_keeps_a_balance_above_the_halved_target()
    {
        var ledger = NewLedger(era: 2);
        ledger.BeginWave(1, isBossWave: false);
        Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.BuildPlan).Status); // −2 → 3

        Assert.True(ledger.BeginWave(2, isBossWave: true)); // target 2 < 3, balance stays 3
        Assert.Equal(3, ledger.Points);
    }

    [Fact] // boundary — E1 waves refill nothing (points disabled)
    public void E1_wave_refill_grants_nothing()
    {
        var ledger = NewLedger(era: 1);
        Assert.True(ledger.BeginWave(1, isBossWave: false));
        Assert.Equal(0, ledger.Points);
    }

    // ---- 组 3：计价（含 Boss 战斗 ×2）-------------------------------------

    [Theory] // legal — prices come from the frozen tariff; combat doubles only on Boss waves
    [InlineData(SystemActionKind.BuildPlan, 2, 2)]
    [InlineData(SystemActionKind.FocusFire, 1, 2)]
    [InlineData(SystemActionKind.TriggerScroll, 1, 2)]
    [InlineData(SystemActionKind.SetStockpile, 1, 1)]
    [InlineData(SystemActionKind.SpawnScout, 1, 1)]
    [InlineData(SystemActionKind.LookAround, 0, 0)]
    public void Price_of_applies_boss_surge_to_combat_only(
        SystemActionKind verb, int normalPrice, int bossPrice)
    {
        var ledger = NewLedger(era: 2);
        ledger.BeginWave(1, isBossWave: false);
        Assert.Equal(normalPrice, ledger.PriceOf(verb));

        ledger.BeginWave(2, isBossWave: true);
        Assert.Equal(bossPrice, ledger.PriceOf(verb));
    }

    [Fact] // legal — TrySpend bills the surged price on a Boss wave
    public void Boss_wave_spend_charges_the_doubled_combat_price()
    {
        var ledger = NewLedger(era: 2);
        ledger.BeginWave(1, isBossWave: true);
        Assert.Equal(2, ledger.Points);

        var result = ledger.TrySpend(SystemActionKind.TriggerScroll); // combat 1 → 2 on Boss
        Assert.Equal(ActionResultStatus.Ok, result.Status);
        Assert.Equal(2, result.PointsSpent);
        Assert.Equal(0, ledger.Points);
    }

    // ---- 组 4：波内 40% 支出线 ---------------------------------------------

    [Theory] // legal — per-wave spend cap = ⌊ceiling × 40%⌋ (E2=2, E3=2, E4=3, E5=4, E6=5)
    [InlineData(2, 2)]
    [InlineData(3, 2)] // ⌊7×0.4⌋ = 2.8 → 2
    [InlineData(4, 3)] // ⌊9×0.4⌋ = 3.6 → 3
    [InlineData(5, 4)] // ⌊11×0.4⌋ = 4.4 → 4
    [InlineData(6, 5)] // ⌊13×0.4⌋ = 5.2 → 5
    public void Wave_spend_cap_is_forty_percent_of_ceiling_floored(int era, int expectedCap)
    {
        var ledger = NewLedger(era);
        ledger.BeginWave(1, isBossWave: false);
        Assert.Equal(expectedCap, ledger.WaveSpendCap);
    }

    [Theory] // boundary — spending exactly up to the cap line is allowed (≤ 上限×40%)
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(6, 5)]
    public void Spending_exactly_to_the_cap_line_is_allowed(int era, int cap)
    {
        var ledger = NewLedger(era);
        ledger.BeginWave(1, isBossWave: false);

        for (int i = 0; i < cap; i++)
        {
            var result = ledger.TrySpend(SystemActionKind.SetStockpile); // logistics, 1 pt
            Assert.Equal(ActionResultStatus.Ok, result.Status);
        }

        Assert.Equal(cap, ledger.WaveSpent);
        Assert.Equal(cap, ledger.WaveSpendCap);
    }

    [Theory] // illegal — one point over the cap line is refused with ErrBudgetDenied and bills nothing
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    [InlineData(6, 5)]
    public void Spending_over_the_cap_line_is_denied_and_never_bills(int era, int cap)
    {
        var ledger = NewLedger(era);
        ledger.BeginWave(1, isBossWave: false);
        for (int i = 0; i < cap; i++)
        {
            Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.SetStockpile).Status);
        }

        int balanceAfterCap = ledger.Points;

        var denied = ledger.TrySpend(SystemActionKind.SetStockpile);
        Assert.Equal(ActionResultStatus.ErrBudgetDenied, denied.Status);
        Assert.Equal(0, denied.PointsSpent); // failed actions never bill
        Assert.Equal(balanceAfterCap, ledger.Points);
        Assert.Equal(cap, ledger.WaveSpent);
        Assert.Contains("wave_cap_exceeded", denied.ErrorMessage);
    }

    [Fact] // legal — a fresh wave re-arms the 40% line (counter resets at BeginWave)
    public void New_wave_re_arms_the_spend_cap()
    {
        var ledger = NewLedger(era: 2);
        ledger.BeginWave(1, isBossWave: false);
        Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.SetStockpile).Status);
        Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.SetStockpile).Status);
        Assert.Equal(ActionResultStatus.ErrBudgetDenied, ledger.TrySpend(SystemActionKind.SetStockpile).Status);

        Assert.True(ledger.BeginWave(2, isBossWave: false));
        Assert.Equal(0, ledger.WaveSpent);
        Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.SetStockpile).Status);
    }

    // ---- 组 5：余额/E1/拒绝序 ----------------------------------------------

    [Fact] // legal — refusal order: insufficient balance is reported before the wave cap when both would trip
    public void Insufficient_balance_is_reported_before_the_wave_cap()
    {
        var ledger = NewLedger(era: 6);
        ledger.BeginWave(1, isBossWave: true);
        Assert.Equal(6, ledger.Points); // ⌊13/2⌋

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.SetStockpile).Status);
        }

        Assert.Equal(1, ledger.Points);      // balance 1
        Assert.Equal(5, ledger.WaveSpent);   // cap 5 reached

        var denied = ledger.TrySpend(SystemActionKind.FocusFire); // Boss combat = 2 > balance 1
        Assert.Equal(ActionResultStatus.ErrBudgetDenied, denied.Status);
        Assert.Contains("insufficient_balance", denied.ErrorMessage);
    }

    [Theory] // legal — free verbs (0 pt) succeed even while E1 has points disabled
    [InlineData(1, SystemActionKind.LookAround)]
    [InlineData(1, SystemActionKind.SetAlarm)]
    [InlineData(2, SystemActionKind.LookAround)]
    public void Free_verbs_succeed_even_in_e1(int era, SystemActionKind verb)
    {
        var ledger = NewLedger(era);
        var result = ledger.TrySpend(verb);
        Assert.Equal(ActionResultStatus.Ok, result.Status);
        Assert.Equal(0, result.PointsSpent);
    }

    [Fact] // illegal — priced verbs are refused in E1 (points not enabled)
    public void Priced_verbs_are_refused_while_e1_points_disabled()
    {
        var ledger = NewLedger(era: 1);
        var result = ledger.TrySpend(SystemActionKind.SpawnScout); // 1 pt verb in E1
        Assert.Equal(ActionResultStatus.ErrBudgetDenied, result.Status);
        Assert.Contains("points_not_enabled_e1", result.ErrorMessage);
        Assert.Equal(0, ledger.Points);
    }

    // ---- 组 6：时代注入 -----------------------------------------------------

    [Fact] // legal — era transition re-anchors the ceiling and clamps the balance
    public void Set_era_re_anchors_ceiling_and_clamps_balance()
    {
        var ledger = NewLedger(era: 6);
        ledger.BeginWave(1, isBossWave: false);
        Assert.Equal(13, ledger.Points);

        ledger.SetEra(2);
        Assert.Equal(5, ledger.Ceiling);
        Assert.Equal(5, ledger.Points); // 13 clamped into the E2 ceiling
    }

    [Theory] // illegal — era transitions stay in the 1..6 domain
    [InlineData(0)]
    [InlineData(7)]
    public void Set_era_refuses_out_of_domain(int era)
    {
        var ledger = NewLedger(era: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.SetEra(era));
    }

    // ---- 组 7：计划原子预检（K-5 整案拒绝）----------------------------------

    [Theory] // legal/boundary — a whole plan must fit balance AND remaining 40% headroom
    [InlineData(2, 0, 2, true)]    // exactly at cap line: coverable
    [InlineData(2, 0, 3, false)]   // over cap (2) and under nothing else
    [InlineData(2, 2, 1, false)]   // cap headroom exhausted after spending 2
    [InlineData(6, 0, 5, true)]    // E6 cap 5
    [InlineData(6, 0, 6, false)]
    [InlineData(1, 0, 1, false)]   // E1: any priced plan is uncoverable
    [InlineData(1, 0, 0, true)]    // E1: a free plan is trivially coverable
    public void Can_cover_plan_checks_balance_and_cap_headroom(
        int era, int preSpend, int planCost, bool expected)
    {
        var ledger = NewLedger(era);
        ledger.BeginWave(1, isBossWave: false);
        for (int i = 0; i < preSpend; i++)
        {
            Assert.Equal(ActionResultStatus.Ok, ledger.TrySpend(SystemActionKind.SetStockpile).Status);
        }

        Assert.Equal(expected, ledger.CanCoverPlan(planCost));
    }

    [Fact] // illegal — negative plan costs are refused (no negative billing)
    public void Can_cover_plan_refuses_negative_cost()
    {
        var ledger = NewLedger(era: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.CanCoverPlan(-1));
    }

    [Fact] // legal — CanCoverPlan is a pure query: nothing is charged
    public void Can_cover_plan_does_not_bill()
    {
        var ledger = NewLedger(era: 2);
        ledger.BeginWave(1, isBossWave: false);
        _ = ledger.CanCoverPlan(2);
        Assert.Equal(5, ledger.Points);
        Assert.Equal(0, ledger.WaveSpent);
    }

    // ---- 组 8：攻击性用例（顺序化并发/伪造回满）------------------------------

    [Fact] // legal — sequentialized concurrency semantics: a spend storm can never cross the cap
    public void Spend_storm_never_crosses_the_cap_line()
    {
        var ledger = NewLedger(era: 4);
        ledger.BeginWave(1, isBossWave: false);
        int cap = ledger.WaveSpendCap; // 3
        int ok = 0;

        for (int i = 0; i < 100; i++)
        {
            var result = ledger.TrySpend(SystemActionKind.FocusFire); // 1 pt each
            if (result.Status == ActionResultStatus.Ok)
            {
                ok++;
            }
            else
            {
                Assert.Equal(ActionResultStatus.ErrBudgetDenied, result.Status);
            }

            // invariants after every interleaving step (D-12 sequential semantics)
            Assert.True(ledger.WaveSpent <= cap, $"wave spend {ledger.WaveSpent} crossed cap {cap}");
            Assert.True(ledger.Points >= 0, "balance went negative");
        }

        Assert.Equal(cap, ok);
        Assert.Equal(9 - cap, ledger.Points); // 9 − 3
    }

    [Theory] // illegal — forged/duplicate refills (same or regressed wave number) are rejected and grant nothing
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    public void Forged_refill_calls_are_rejected(long firstWave, long forgedWave)
    {
        var ledger = NewLedger(era: 2);
        Assert.True(ledger.BeginWave(firstWave, isBossWave: false));
        int pointsAfterFirst = ledger.Points;
        Assert.Equal(5, pointsAfterFirst);

        Assert.False(ledger.BeginWave(forgedWave, isBossWave: true)); // forged: no Boss perks either
        Assert.Equal(pointsAfterFirst, ledger.Points);
        Assert.False(ledger.InBossWave);
        Assert.Equal(firstWave, ledger.Wave);

        // the ledger recovers: the next monotonic wave still works
        Assert.True(ledger.BeginWave(firstWave + 1, isBossWave: false));
    }

    [Fact] // illegal — wave zero cannot be begun after construction (waves are 1-based monotonic)
    public void Wave_zero_is_not_a_lawful_first_wave()
    {
        var ledger = NewLedger(era: 2);
        Assert.False(ledger.BeginWave(0, isBossWave: false));
        Assert.Equal(0, ledger.Points);
    }

    // ---- 组 9：JSONL 审计 ---------------------------------------------------

    [Fact] // legal — every movement/refusal lands in the journal, ordered, with strictly increasing seq
    public void Movements_and_refusals_are_journaled_as_ordered_jsonl()
    {
        var journal = new CapturingJournalWriter();
        var clock = new SteppedClock();
        clock.Advance(1); // stamp the whole scenario at tick 1 of the 30 Hz domain
        var ledger = new SystemPointLedger(eraIndex: 2, journal, clock);

        ledger.BeginWave(1, isBossWave: false);                          // wave_refill
        ledger.TrySpend(SystemActionKind.BuildPlan);                     // spend_ok
        ledger.TrySpend(SystemActionKind.BuildPlan);                     // spend_ok
        ledger.TrySpend(SystemActionKind.BuildPlan);                     // spend_denied (cap)

        Assert.Equal(4, journal.Events.Count);
        Assert.All(journal.Events, evt => Assert.Equal(JournalEventKind.Validation, evt.Kind));
        Assert.Equal(1, clock.Tick);
        Assert.All(journal.Events, evt => Assert.Equal(1, evt.Tick)); // K-1 clock stamps the 30 Hz domain

        long previousSeq = 0;
        foreach (var evt in journal.Events)
        {
            Assert.True(evt.Seq > previousSeq, "journal seq must strictly increase");
            previousSeq = evt.Seq;
        }

        // payload shape round-trips through the frozen context (audit lines stay parseable)
        var refill = JsonSerializer.Deserialize(
            journal.Events[0].PayloadJson, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        Assert.NotNull(refill);
        Assert.Equal("wave_refill", refill!.Type);
        Assert.Equal(5, refill.BalanceAfter);

        var spend = JsonSerializer.Deserialize(
            journal.Events[1].PayloadJson, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        Assert.Equal(SystemActionKind.BuildPlan, spend!.Verb);
        Assert.Equal(2, spend.Amount);

        var denial = JsonSerializer.Deserialize(
            journal.Events[3].PayloadJson, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        Assert.Equal("spend_denied", denial!.Type);
        Assert.Contains("wave_cap_exceeded", denial.Reason);
    }

    [Fact] // legal — a forged refill is itself journaled (refusals are auditable too)
    public void Forged_refill_is_journaled_as_a_rejection_event()
    {
        var journal = new CapturingJournalWriter();
        var ledger = new SystemPointLedger(eraIndex: 2, journal);
        ledger.BeginWave(3, isBossWave: false);
        ledger.BeginWave(3, isBossWave: true);

        var evt = JsonSerializer.Deserialize(
            journal.Events[1].PayloadJson, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        Assert.Equal("wave_refill_rejected", evt!.Type);
        Assert.Contains("non_monotonic_wave", evt.Reason);
    }

    [Fact] // legal — no journal/clock wired: the ledger still runs, tick defaults to -1 (outside a run)
    public void Ledger_runs_without_journal_and_the_tick_domain_stays_clean()
    {
        var journal = new CapturingJournalWriter();
        var ledger = new SystemPointLedger(eraIndex: 2, journal, clock: null);
        ledger.BeginWave(1, isBossWave: false);

        Assert.Single(journal.Events);
        Assert.Equal(-1, journal.Events[0].Tick); // K-8: -1 outside a run
    }
}
