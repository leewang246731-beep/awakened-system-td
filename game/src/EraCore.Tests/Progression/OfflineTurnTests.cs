using EraCore.Economy;
using EraCore.Progression;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Progression;

// [F-053] 离线岁月四规则+每日签到（T-073，附录 G.4 冻结值）：4h 封顶+超出
// 25% 递减+8h 等效封顶曲线 / 追帧 ≤50 回合 / 回合钟只在战役界面推进（进行
// 中关卡豁免）/ 入侵限当前时代 / 连续登录里程碑（装饰性、断签不清零）。
// 三行 legal/boundary/illegal 全覆盖（CS-3.1）。[C-19]
public class OfflineTurnTests
{
    private const long HourMs = 3_600_000L;

    private const long DayMs = 86_400_000L;

    private static OfflineTurn NewOffline(MutableWallClock clock, ResourceStock pool, bool enabled = true) =>
        new(SignInData.Parse(MinimalSignIn), clock, pool, new SimRng(Seed), eraIndex: 1, enabled);

    // ------------------------------------------------------- legal

    [Fact] // legal — 曲线表驱动：4h 内全额；超出按 25%；等效封顶 8h（G.4 规则 4）
    [Trait("Feature", "F-053")]
    public void Offline_income_curve_table()
    {
        SignInTable table = SignInData.Parse(MinimalSignIn);
        OfflineRules rules = table.Rules;
        Assert.Equal(4.0, rules.FullRateHours, 9); // G.4 冻结值（signin.json）
        Assert.Equal(0.25, rules.DiminishedRate, 9);
        Assert.Equal(8.0, rules.EquivalentCapHours, 9);
        Assert.Equal(50, rules.CatchupTurnCap);

        // 手算代入：2h→2 / 4h→4 / 6h→4+0.25×2=4.5 / 12h→4+0.25×8=6 / 20h→4+0.25×16=8 / 100h→8（封顶）
        Assert.Equal(2.0, OfflineTurn.EffectiveHoursFor(rules, 2.0), 9);
        Assert.Equal(4.0, OfflineTurn.EffectiveHoursFor(rules, 4.0), 9);
        Assert.Equal(4.5, OfflineTurn.EffectiveHoursFor(rules, 6.0), 9);
        Assert.Equal(6.0, OfflineTurn.EffectiveHoursFor(rules, 12.0), 9);
        Assert.Equal(8.0, OfflineTurn.EffectiveHoursFor(rules, 20.0), 9);
        Assert.Equal(8.0, OfflineTurn.EffectiveHoursFor(rules, 100.0), 9);
        Assert.Equal(0.0, OfflineTurn.EffectiveHoursFor(rules, 0.0), 9);
    }

    [Fact] // legal — 结算入池：收入=费率×等效小时，封顶由池上限执法（入池只计上限内）
    [Trait("Feature", "F-053")]
    public void Settle_deposits_capped_income_into_the_pool()
    {
        var clock = new MutableWallClock();
        ResourceStock pool = FreshPool(clock);
        OfflineTurn offline = NewOffline(clock, pool);

        clock.NowMs += 6 * HourMs; // 6h → 4.5 等效小时
        OfflineSettlement settlement = offline.SettleAbsence();
        Assert.True(settlement.InCampaignMode);
        Assert.Equal(6.0, settlement.AbsentHours, 9);
        Assert.Equal(4.5, settlement.EffectiveHours, 9);
        OfflineIncomeGrant grant = Assert.Single(settlement.Income);
        Assert.Equal(Wood, grant.Resource);
        Assert.Equal(12.0 * 4.5, grant.Granted, 9); // 12/h × 4.5h（signin.json 结构旋钮）
        Assert.Equal(grant.Granted, grant.Accepted + grant.Refused, 9); // 守恒
        Assert.Equal(12.0 * 4.5, pool.AmountOf(Wood), 9);
    }

    [Fact] // legal — 追帧 ≤50 回合：长离线恰好 50；入侵全部当前时代（G.4 规则 2+3）
    [Trait("Feature", "F-053")]
    public void Catchup_turns_are_capped_and_invasions_are_era_locked()
    {
        var clock = new MutableWallClock();
        ResourceStock pool = FreshPool(clock);
        OfflineTurn offline = NewOffline(clock, pool);

        clock.NowMs += 200 * HourMs; // 200 回合原始值 → 钳到 50
        OfflineSettlement settlement = offline.SettleAbsence();
        Assert.Equal(50, settlement.TurnsSimulated);
        Assert.True(settlement.TurnCapped);
        // invasion_chance_per_turn=1.0（夹具）→ 每回合必有入侵，且时代恒 1
        Assert.Equal(50, settlement.Invasions.Count);
        Assert.All(settlement.Invasions, invasion => Assert.Equal(1, invasion.EraIndex));
        Assert.Equal(1, settlement.Invasions[0].TurnIndex);
        Assert.Equal(50, settlement.Invasions[^1].TurnIndex);

        // 短离线：2h=2 回合，不触上限
        clock.NowMs += 2 * HourMs;
        OfflineSettlement shortAbsence = offline.SettleAbsence();
        Assert.Equal(2, shortAbsence.TurnsSimulated);
        Assert.False(shortAbsence.TurnCapped);
        Assert.Equal(2, shortAbsence.Invasions.Count);
    }

    [Fact] // legal — 规则 1：回合钟只在战役界面推进——进关即冻结（先结一次账）
    [Trait("Feature", "F-053")]
    public void Turn_clock_freezes_while_a_level_is_in_progress()
    {
        var clock = new MutableWallClock();
        ResourceStock pool = FreshPool(clock);
        OfflineTurn offline = NewOffline(clock, pool);

        clock.NowMs += 1 * HourMs;
        OfflineSettlement beforeLevel = offline.EnterLevel(); // 进关先结算 1h
        Assert.Equal(1.0, beforeLevel.EffectiveHours, 9);
        Assert.True(offline.InLevel);

        clock.NowMs += 5 * HourMs; // 关内 5h：豁免——零计提
        OfflineSettlement duringLevel = offline.SettleAbsence();
        Assert.False(duringLevel.InCampaignMode);
        Assert.Equal(0.0, duringLevel.EffectiveHours, 9);
        Assert.Empty(duringLevel.Income);
        Assert.Equal(0, duringLevel.TurnsSimulated);

        offline.ReturnToCampaign(); // 锚点重置为出关时刻
        clock.NowMs += 2 * HourMs;
        OfflineSettlement afterLevel = offline.SettleAbsence();
        Assert.Equal(2.0, afterLevel.EffectiveHours, 9); // 只计战役界面 2h
    }

    [Fact] // legal — 每日签到：连日递增、里程碑触发、断签不清零、重复签到幂等
    [Trait("Feature", "F-053")]
    public void Signin_streak_milestones_and_break_tolerance()
    {
        var clock = new MutableWallClock();
        ResourceStock pool = FreshPool(clock);
        OfflineTurn offline = NewOffline(clock, pool);

        // 第 1~2 天：连签 2
        SignInCheckIn day1 = offline.CheckIn();
        Assert.True(day1.NewDay);
        Assert.Equal(1, day1.TotalLoginDays);
        Assert.Equal(1, day1.ConsecutiveDays);
        clock.NowMs += DayMs;
        SignInCheckIn day2 = offline.CheckIn();
        Assert.Equal(2, day2.ConsecutiveDays);
        Assert.Empty(day2.NewlyClaimed);

        // 断签 5 天：连续数不清零（停在 2），总天数+1
        clock.NowMs += 5 * DayMs;
        SignInCheckIn broken = offline.CheckIn();
        Assert.Equal(2, broken.ConsecutiveDays); // G.4/F-053 断签不清零
        Assert.Equal(3, broken.TotalLoginDays);

        // 恢复连签：第 3 天连续 → 里程碑（day 3 装饰奖励）触发且只触发一次
        clock.NowMs += DayMs;
        SignInCheckIn resume = offline.CheckIn();
        Assert.Equal(3, resume.ConsecutiveDays);
        SignInMilestone milestone = Assert.Single(resume.NewlyClaimed);
        Assert.Equal(3, milestone.Day);
        Assert.Equal("c:streak-3", milestone.Reward.Id);
        Assert.Equal("decoration", milestone.Reward.RewardClass); // R9 白名单类

        // 同日重复签到：非新日，无重复领取
        SignInCheckIn sameDay = offline.CheckIn();
        Assert.False(sameDay.NewDay);
        Assert.Empty(sameDay.NewlyClaimed);
        Assert.Equal(3, sameDay.ConsecutiveDays);

        // 再断再连：连续数从 3 继续（不清零语义）
        clock.NowMs += 9 * DayMs;
        Assert.Equal(3, offline.CheckIn().ConsecutiveDays);
        clock.NowMs += DayMs;
        Assert.Equal(4, offline.CheckIn().ConsecutiveDays);
    }

    // ------------------------------------------------------- boundary

    [Fact] // boundary — 零时长结算：无收入无回合；feature flag 关=零计提
    [Trait("Feature", "F-053")]
    public void Zero_absence_and_disabled_flag_settle_nothing()
    {
        var clock = new MutableWallClock();
        ResourceStock pool = FreshPool(clock);
        OfflineTurn offline = NewOffline(clock, pool);

        OfflineSettlement immediate = offline.SettleAbsence();
        Assert.Equal(0.0, immediate.AbsentHours, 9);
        Assert.Empty(immediate.Income);
        Assert.Equal(0, immediate.TurnsSimulated);

        var offClock = new MutableWallClock();
        var offPool = FreshPool(offClock);
        OfflineTurn disabled = NewOffline(offClock, offPool, enabled: false);
        offClock.NowMs += 30 * HourMs;
        OfflineSettlement frozen = disabled.SettleAbsence();
        Assert.False(frozen.Enabled);
        Assert.Equal(0.0, frozen.EffectiveHours, 9);
        Assert.Empty(frozen.Income);
        Assert.Equal(0, frozen.TurnsSimulated);
        Assert.Equal(0.0, offPool.AmountOf(Wood), 9); // 池未动
    }

    [Fact] // boundary — 真实 signin.json 锚点：G.4 四冻结值+里程碑天数表+白名单类
    [Trait("Feature", "F-053")]
    public void Real_signin_table_carries_the_frozen_anchors()
    {
        SignInTable table = SignInData.Parse(ReadCampaignContent("signin.json"));
        Assert.Equal(4.0, table.Rules.FullRateHours, 9);
        Assert.Equal(0.25, table.Rules.DiminishedRate, 9);
        Assert.Equal(8.0, table.Rules.EquivalentCapHours, 9);
        Assert.Equal(50, table.Rules.CatchupTurnCap);
        Assert.Equal(new[] { 3, 7, 14, 30 }, table.Milestones.Select(milestone => milestone.Day));
        Assert.All(table.Milestones.Select(milestone => milestone.Reward.RewardClass),
            rewardClass => Assert.Contains(rewardClass, RewardWhitelist.Classes)); // 全装饰类（F-053/R9）
        Assert.Equal(2, table.Rules.Income.Count); // 双资源收入行
    }

    // ------------------------------------------------------- illegal

    [Fact] // illegal — 时钟倒流/越界时代/空参：拒绝
    [Trait("Feature", "F-053")]
    public void Illegal_inputs_are_rejected()
    {
        var clock = new MutableWallClock { NowMs = 10_000 };
        ResourceStock pool = FreshPool(clock);
        OfflineTurn offline = NewOffline(clock, pool);
        clock.NowMs = 5_000; // 倒流
        Assert.Throws<InvalidOperationException>(() => offline.SettleAbsence());

        Assert.Throws<ArgumentException>(() => new OfflineTurn(
            SignInData.Parse(MinimalSignIn), clock, pool, new SimRng(Seed), eraIndex: 0)); // 越界时代
        Assert.Throws<ArgumentNullException>(() => new OfflineTurn(null!, clock, pool, new SimRng(Seed), 1));

        // 形状违规的签到表：里程碑重复天数 / 白名单外奖励类 / 越界规则值
        Assert.Throws<ArgumentException>(() => SignInData.Parse(
            """{ "offline": { "full_rate_hours": 4, "diminished_rate": 0.25, "equivalent_cap_hours": 8, "catchup_turn_cap": 50, "turns_per_hour": 1, "invasion_chance_per_turn": 0.5 }, "milestones": [ { "day": 3, "reward": { "id": "c:a", "reward_class": "decoration" } }, { "day": 3, "reward": { "id": "c:b", "reward_class": "title" } } ] }"""));

        Assert.Throws<ArgumentException>(() => SignInData.Parse(
            """{ "offline": { "full_rate_hours": 4, "diminished_rate": 0.25, "equivalent_cap_hours": 8, "catchup_turn_cap": 50, "turns_per_hour": 1, "invasion_chance_per_turn": 0.5 }, "milestones": [ { "day": 3, "reward": { "id": "c:a", "reward_class": "tech_boost" } } ] }"""));

        Assert.Throws<ArgumentException>(() => SignInData.Parse(
            """{ "offline": { "full_rate_hours": -1, "diminished_rate": 0.25, "equivalent_cap_hours": 8, "catchup_turn_cap": 50, "turns_per_hour": 1, "invasion_chance_per_turn": 0.5 }, "milestones": [] }"""));
    }
}
