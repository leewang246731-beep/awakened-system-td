using EraCore.Agent.Autopilot;
using EraCore.Contracts.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Autopilot;

// [F-093][C-13] T-081 轮内配额执法（AutopilotRoundQuota）：≤3 动作/轮硬限
// 的守卫单测——第 4 动作拒入、汇报收尾段不占配额、轮边界清零。
public class AutopilotRoundQuotaTests
{
    [Fact] // legal：三个非汇报动作依次进入，配额 3/3
    public void Three_actions_enter_and_exhaust_the_quota()
    {
        var quota = new AutopilotRoundQuota();

        Assert.True(quota.TryEnter(SystemActionKind.LookAround));
        Assert.True(quota.TryEnter(SystemActionKind.BuildPlan));
        Assert.True(quota.TryEnter(SystemActionKind.FocusFire));

        Assert.Equal(3, quota.Used);
        Assert.True(quota.Exhausted);
        Assert.Equal(3, AutopilotRoundQuota.ActionsPerRound);
    }

    [Fact] // boundary：第 4 个动作拒入本轮（不排队、不执行——调用方不得送进管线）
    public void Fourth_action_is_refused_entry_into_the_round()
    {
        var quota = new AutopilotRoundQuota();
        _ = quota.TryEnter(SystemActionKind.LookAround);
        _ = quota.TryEnter(SystemActionKind.BuildPlan);
        _ = quota.TryEnter(SystemActionKind.FocusFire);

        Assert.False(quota.TryEnter(SystemActionKind.RerouteLogistics));

        Assert.Equal(3, quota.Used); // 拒绝不占用、不改变已用配额
    }

    [Fact] // legal：轮末汇报（ReportToPlayer）是"→汇报"收尾段，配额满也放行且不计数
    public void Round_final_report_enters_freely_beyond_the_quota()
    {
        var quota = new AutopilotRoundQuota();
        _ = quota.TryEnter(SystemActionKind.LookAround);
        _ = quota.TryEnter(SystemActionKind.BuildPlan);
        _ = quota.TryEnter(SystemActionKind.FocusFire);

        Assert.True(quota.TryEnter(SystemActionKind.ReportToPlayer));
        Assert.Equal(3, quota.Used); // 汇报不占 3 动作配额
    }

    [Fact] // legal：BeginRound 清零——下一轮配额完整重来
    public void BeginRound_resets_the_window_for_the_next_round()
    {
        var quota = new AutopilotRoundQuota();
        _ = quota.TryEnter(SystemActionKind.BuildPlan);
        _ = quota.TryEnter(SystemActionKind.BuildPlan);
        _ = quota.TryEnter(SystemActionKind.BuildPlan);
        Assert.True(quota.Exhausted);

        quota.BeginRound();

        Assert.Equal(0, quota.Used);
        Assert.False(quota.Exhausted);
    }

    [Fact] // boundary：新窗口从零开始——第一动作即占 1 格（非汇报动词没有免费首格）
    public void Fresh_window_charges_the_first_non_report_action()
    {
        var quota = new AutopilotRoundQuota();

        Assert.True(quota.TryEnter(SystemActionKind.BuildPlan));
        Assert.Equal(1, quota.Used);
    }
}
