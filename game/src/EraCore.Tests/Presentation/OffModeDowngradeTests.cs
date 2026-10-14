using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-097] T-075 关闭系统 E1 全量降级（§10.7 警报降级中性横幅+音效，
// 功能无损）：开关两态×表面矩阵（输入面/徽记全隐、HUD 中性、警报通道）+
// 防线警报决策（双开关位照发、0.40 阈值边界、中性措辞不带人格）+ 非法完整度拒绝。
public class OffModeDowngradeTests
{
    // ---- legal — surface matrix (开关×表面) -------------------------------

    [Fact] // legal — 开系统：全部输入表面在、HUD 全息皮、警报走人格气泡
    public void Surfaces_on_shows_every_surface_with_holo_hud_and_persona_alarm()
    {
        OffModeSurfaces surfaces = OffModeDowngrade.Surfaces(systemEnabled: true);

        Assert.True(surfaces.OrderInputVisible);
        Assert.True(surfaces.WheelVisible);
        Assert.True(surfaces.PlanCardVisible);
        Assert.True(surfaces.TrustBadgeVisible);
        Assert.False(surfaces.HudNeutral);
        Assert.Equal(AlarmChannel.PersonaBubble, surfaces.Alarm);
    }

    [Fact] // legal — 关系统：输入面/方案卡/徽记全隐，HUD 转中性横幅，警报改中性通道
    public void Surfaces_off_hides_personas_but_keeps_neutral_hud_and_alarm()
    {
        OffModeSurfaces surfaces = OffModeDowngrade.Surfaces(systemEnabled: false);

        Assert.False(surfaces.OrderInputVisible);
        Assert.False(surfaces.WheelVisible);
        Assert.False(surfaces.PlanCardVisible);
        Assert.False(surfaces.TrustBadgeVisible);
        Assert.True(surfaces.HudNeutral);
        Assert.Equal(AlarmChannel.NeutralBanner, surfaces.Alarm);
    }

    // ---- legal — defense alarm matrix (降级矩阵：开关×完整度) --------------

    [Theory] // legal — 表驱动降级矩阵：两态开关×三档完整度 → 是否照发+通道
    [InlineData(true, 1.00, false, AlarmChannel.PersonaBubble)]
    [InlineData(true, 0.40, false, AlarmChannel.PersonaBubble)]
    [InlineData(true, 0.39, true, AlarmChannel.PersonaBubble)]
    [InlineData(true, 0.00, true, AlarmChannel.PersonaBubble)]
    [InlineData(false, 1.00, false, AlarmChannel.NeutralBanner)]
    [InlineData(false, 0.40, false, AlarmChannel.NeutralBanner)]
    [InlineData(false, 0.39, true, AlarmChannel.NeutralBanner)]
    [InlineData(false, 0.00, true, AlarmChannel.NeutralBanner)]
    public void DefenseAlarm_matrix_fires_below_the_line_in_both_switch_positions(
        bool enabled, double integrity, bool fires, AlarmChannel channel)
    {
        DefenseAlarmView view = OffModeDowngrade.DefenseAlarmAt(enabled, integrity);

        Assert.Equal(fires, view.Fires);
        Assert.Equal(channel, view.Channel);
        if (!fires)
        {
            Assert.Equal(string.Empty, view.Text);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(view.Text));
        }
    }

    [Fact] // boundary — 警报不缺失：关系统时低完整度照发（§10.7 功能无损）
    public void DefenseAlarm_off_mode_never_silences_a_crisis()
    {
        DefenseAlarmView view = OffModeDowngrade.DefenseAlarmAt(systemEnabled: false, 0.35d);

        Assert.True(view.Fires);
        Assert.Equal(AlarmChannel.NeutralBanner, view.Channel);
        Assert.Equal("防线完整度 35%", view.Text);
    }

    [Fact] // boundary — 阈值边界：恰好 40% 不发，39.9% 才发（严格低于）
    public void DefenseAlarm_threshold_is_strictly_below_forty_percent()
    {
        Assert.False(OffModeDowngrade.DefenseAlarmAt(true, OffModeDowngrade.DefenseAlarmBelow).Fires);
        Assert.True(
            OffModeDowngrade.DefenseAlarmAt(true, OffModeDowngrade.DefenseAlarmBelow - 0.0001d).Fires);
    }

    [Theory] // boundary — 开系统=人格台词原文（F-105 台词库），关系统=中性事实句
    [InlineData(true)]
    [InlineData(false)]
    public void DefenseAlarm_text_carries_persona_wording_only_while_on(bool enabled)
    {
        DefenseAlarmView view = OffModeDowngrade.DefenseAlarmAt(enabled, 0.10d);

        if (enabled)
        {
            Assert.Equal(OffModeDowngrade.PersonaAlarmLine, view.Text);
        }
        else
        {
            Assert.NotEqual(OffModeDowngrade.PersonaAlarmLine, view.Text);
            Assert.StartsWith("防线完整度 ", view.Text);
            Assert.EndsWith("%", view.Text);
            Assert.DoesNotContain("建议", view.Text); // 中性措辞不带军师口吻
        }
    }

    [Theory] // boundary — 中性句百分数四舍五入（AwayFromZero）且不变式数字
    [InlineData(0.355, "36")]
    [InlineData(0.354, "35")]
    [InlineData(0.001, "0")]
    public void DefenseAlarm_neutral_text_rounds_the_percent_away_from_zero(
        double integrity, string expectedPercent)
    {
        DefenseAlarmView view = OffModeDowngrade.DefenseAlarmAt(false, integrity);

        Assert.Equal($"防线完整度 {expectedPercent}%", view.Text);
    }

    // ---- illegal — rejected inputs -----------------------------------------

    [Theory] // illegal — 完整度出 [0,1] 拒绝（双态各测）
    [InlineData(true, -0.01)]
    [InlineData(true, 1.01)]
    [InlineData(false, -1.0)]
    [InlineData(false, 2.0)]
    public void DefenseAlarm_rejects_integrity_outside_unit_range(bool enabled, double integrity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OffModeDowngrade.DefenseAlarmAt(enabled, integrity));
    }
}
