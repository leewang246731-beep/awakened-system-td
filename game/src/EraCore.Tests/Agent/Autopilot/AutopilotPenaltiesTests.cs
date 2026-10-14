using EraCore.Agent.Autopilot;
using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using Xunit;

namespace EraCore.Tests.Agent.Autopilot;

// [F-093][C-13] T-081 三罚则执法矩阵（v3.2 §11.8 全托管口径后三条）：
// ① 首通无成就（成就跳过钩子）② 收益 ×0.8（验收断言 100→80）③ 仅无尽全托管榜记分
// （隔离通道，T-080 三榜挂接点）。三罚则按【关卡参与】判定，接管降级不回退。
public class AutopilotPenaltiesTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");

    private static AutopilotParticipation Snap(bool level, bool wave, AutopilotMode mode) =>
        new(level, wave, mode);

    // ------------------------------------------------ ② 收益 ×0.8 ------------------------------------------

    public static TheoryData<double, double> YieldRows => new()
    {
        // legal/boundary：floor(x × 0.8)——验收锚 100→80 逐点钉死
        { 0.0, 0.0 },     // 零收益零罚
        { 1.0, 0.0 },     // 不足 1 整数部分的差额如实向下取整
        { 2.5, 2.0 },     // 2.5×0.8=2.0 恰整
        { 3.0, 2.0 },     // 3×0.8=2.4 → floor 2
        { 10.0, 8.0 },
        { 80.0, 64.0 },
        { 99.0, 79.0 },   // 99×0.8=79.2 → 79
        { 100.0, 80.0 },  // 验收数值断言（任务卡原文 100→80）
        { 1000.0, 800.0 },
    };

    [Theory] // legal：收益罚标量核 floor(x×0.8) 表驱动（验收锚 100→80）
    [MemberData(nameof(YieldRows))]
    public void ScaleYield_floors_at_zero_point_eight(double granted, double expected)
    {
        Assert.Equal(expected, AutopilotPenalties.ScaleYield(granted));
    }

    [Fact] // legal：任务卡验收断言原文——收益 100 → 80（精确数值）
    public void Acceptance_anchor_one_hundred_yields_eighty()
    {
        Assert.Equal(80.0, AutopilotPenalties.ScaleYield(100.0));
        Assert.Equal(0.8, AutopilotPenalties.AutopilotYieldFactor);
    }

    [Fact] // boundary：负收益拒绝（收益不存在负口径——防御位，不得静默取绝对值）
    public void Negative_yield_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AutopilotPenalties.ScaleYield(-0.1));
    }

    [Fact] // legal：整行战利品 ×0.8——全额入账行（Granted=Accepted+Refused=100 → 80/80/0）
    public void Fully_accepted_grant_line_scales_to_eighty_percent()
    {
        var grant = new ResourceGrant(Wood, Granted: 100.0, Accepted: 100.0, Refused: 0.0);

        ResourceGrant scaled = AutopilotPenalties.ApplyYieldPenalty(grant);

        Assert.Equal(80.0, scaled.Granted);   // 罚后授予 80
        Assert.Equal(80.0, scaled.Accepted);  // 全额入账 80
        Assert.Equal(0.0, scaled.Refused);
        Assert.Equal(scaled.Granted, scaled.Accepted + scaled.Refused); // 行不变量保持
    }

    [Fact] // legal：部分入账行——池拒绝份额从罚后授予里如实扣（Accepted 不越界）
    public void Partially_accepted_grant_line_preserves_the_line_invariant()
    {
        var grant = new ResourceGrant(Wood, Granted: 100.0, Accepted: 60.0, Refused: 40.0);

        ResourceGrant scaled = AutopilotPenalties.ApplyYieldPenalty(grant);

        Assert.Equal(80.0, scaled.Granted);
        Assert.Equal(60.0, scaled.Accepted);  // min(60, 80)——已入账不回抽
        Assert.Equal(20.0, scaled.Refused);   // 80-60：差额并入未入账
        Assert.Equal(scaled.Granted, scaled.Accepted + scaled.Refused);
    }

    [Fact] // legal：整表逐行 ×0.8（多资源 Loot 表）
    public void Loot_table_scales_line_by_line()
    {
        var stone = new ResourceKey("era1:res-stone");
        IReadOnlyList<ResourceGrant> loot = new[]
        {
            new ResourceGrant(Wood, 100.0, 100.0, 0.0),
            new ResourceGrant(stone, 50.0, 40.0, 10.0),
        };

        IReadOnlyList<ResourceGrant> scaled = AutopilotPenalties.ApplyYieldPenalty(loot);

        Assert.Equal(2, scaled.Count);
        Assert.Equal(80.0, scaled[0].Granted);
        Assert.Equal(40.0, scaled[1].Granted); // 50×0.8 恰整
        Assert.Equal(40.0, scaled[1].Accepted);
        Assert.Equal(0.0, scaled[1].Refused);  // min(40,40)——不变量收敛
        Assert.All(scaled, line => Assert.Equal(line.Granted, line.Accepted + line.Refused));
    }

    // ------------------------------------------------ ① 首通无成就 ----------------------------------------

    [Fact] // legal：托管参与的关卡首通不授成就（钩子清空授予列表）
    public void First_clear_achievements_are_skipped_when_autopilot_participated()
    {
        AutopilotParticipation participated =
            Snap(level: true, wave: true, AutopilotMode.FullAutopilot);

        Assert.False(AutopilotPenalties.FirstClearAchievementEligible(participated));
        Assert.Empty(AutopilotPenalties.FilterFirstClearAchievements(
            participated, new[] { "ach:first-clear", "ach:no-leak" }));
    }

    [Fact] // legal：手动局首通照常授（未参与——钩子原样放行）
    public void Manual_first_clear_achievements_pass_through()
    {
        AutopilotParticipation manual = Snap(level: false, wave: false, AutopilotMode.Off);
        string[] achievements = { "ach:first-clear", "ach:no-leak" };

        Assert.True(AutopilotPenalties.FirstClearAchievementEligible(manual));
        Assert.Equal(achievements, AutopilotPenalties.FilterFirstClearAchievements(manual, achievements));
    }

    [Fact] // boundary：接管降级不回退——参与过的托管局（已降级 Assisted）首通仍无成就
    public void Takeover_downgrade_does_not_roll_back_the_achievement_penalty()
    {
        // 当波/当关已生效则不回退：接管后快照 Mode=Assisted 但参与位仍在
        AutopilotParticipation downgraded = Snap(level: true, wave: true, AutopilotMode.Assisted);

        Assert.False(AutopilotPenalties.FirstClearAchievementEligible(downgraded));
        Assert.Empty(AutopilotPenalties.FilterFirstClearAchievements(
            downgraded, new[] { "ach:first-clear" }));
    }

    // ------------------------------------------------ ③ 仅无尽全托管榜记分 ----------------------------------

    public static TheoryData<bool, bool, string?> ScoreRows => new()
    {
        // 参与托管 + 无尽局 → 无尽全托管隔离通道（唯一合法去处）
        { true, true, AutopilotPenalties.EndlessAutopilotChannelId },
        // 参与托管 + 非无尽局 → null（无榜记分——防系统代玩刷分空心化 R11）
        { true, false, null },
        // 手动局（任意模式）→ 标准通道照常（T-080 手动榜与本通道分记）
        { false, true, AutopilotPenalties.StandardChannelId },
        { false, false, AutopilotPenalties.StandardChannelId },
    };

    [Theory] // legal：记分路由矩阵——托管只进无尽隔离通道，手动局照常
    [MemberData(nameof(ScoreRows))]
    public void Score_channel_routing_matrix(bool participated, bool isEndless, string? expectedChannel)
    {
        AutopilotParticipation participation =
            Snap(participated, participated, AutopilotMode.FullAutopilot);

        Assert.Equal(expectedChannel, AutopilotPenalties.RouteScoreChannel(participation, isEndless));
    }

    [Fact] // legal：标准榜资格——托管参与局永不进标准榜（三榜互不混排 B 侧执法）
    public void Autopilot_runs_are_never_eligible_for_standard_boards()
    {
        Assert.False(AutopilotPenalties.EligibleForStandardBoards(
            Snap(true, true, AutopilotMode.FullAutopilot)));
        Assert.True(AutopilotPenalties.EligibleForStandardBoards(
            Snap(false, false, AutopilotMode.Off)));
    }

    [Fact] // legal：T-080 挂接点——无尽全托管通道 id 冻结 + 记分数据面可构造
    public void Endless_autopilot_channel_placeholder_is_frozen_and_addressable()
    {
        Assert.Equal("endless.autopilot", AutopilotPenalties.EndlessAutopilotChannelId);
        var entry = new AutopilotScoreEntry(
            AutopilotPenalties.EndlessAutopilotChannelId, Wave: 12, ActionsIssued: 34, PointsSpentByAutopilot: 21);
        Assert.Equal(AutopilotPenalties.EndlessAutopilotChannelId, entry.ChannelId);
        Assert.Equal(12, entry.Wave);
    }

    // ------------------------------------------------ 三罚则联合矩阵 ----------------------------------------

    public static TheoryData<bool> ParticipationRows => new() { true, false };

    [Theory] // legal：三罚则联合矩阵——参与位单一判定源，三条罚则同开同关
    [MemberData(nameof(ParticipationRows))]
    public void All_three_penalties_key_off_the_same_participation_flag(bool participated)
    {
        AutopilotParticipation participation =
            Snap(participated, participated, AutopilotMode.FullAutopilot);
        var grant = new ResourceGrant(Wood, 100.0, 100.0, 0.0);

        // ① 首通无成就
        Assert.Equal(!participated, AutopilotPenalties.FirstClearAchievementEligible(participation));
        // ② 收益 ×0.8（执法函数对任意输入恒成立；参与的调用语义由宿主按①同源判定）
        Assert.Equal(80.0, AutopilotPenalties.ApplyYieldPenalty(grant).Granted);
        // ③ 仅无尽记分
        Assert.Equal(
            participated ? AutopilotPenalties.EndlessAutopilotChannelId : AutopilotPenalties.StandardChannelId,
            AutopilotPenalties.RouteScoreChannel(participation, isEndlessRun: true));
    }
}
