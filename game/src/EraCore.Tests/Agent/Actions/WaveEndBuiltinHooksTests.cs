using EraCore.Agent.Actions;
using EraCore.Tests.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-075] T-070 内置 WaveEnd 钩子（守成完成度检查）：汇报主张"守住"且漏怪
// 路径未补 → 拦截并回喂"先补防线"；主张非守住（如实承认失守）或已补防
// → 放行；缺省探针（无运行时漏怪数据）恒放行（惰性接线面）。

/// <summary>[F-075] Built-in held-completeness hook: block held claims while leak paths stay unpatched.</summary>
public class WaveEndBuiltinHooksTests
{
    private static WaveEndContext Report(int wave, bool claimsHeld) =>
        new(wave, claimsHeld, ActionJsonSamples.ReportToPlayer(), WorldView: null);

    [Theory] // illegal — 主张守住 + 漏怪路径未补 → Block 回喂（条数入原因）
    [InlineData(1)]
    [InlineData(3)]
    public void Held_claims_with_unpatched_leaks_are_blocked(int leaks)
    {
        var hook = new HeldCompletenessHook(() => leaks);
        WaveEndHookVerdict verdict = hook.Review(Report(wave: 4, claimsHeld: true));
        Assert.Equal(WaveEndVerdictKind.Block, verdict.Kind);
        Assert.Contains($"{leaks} 条漏怪路径未补", verdict.Feedback);
        Assert.Contains("先补防线", verdict.Feedback);
    }

    [Fact] // legal — 如实失守汇报（未主张守住）不拦：诚实汇报不被完成度把关卡住
    public void Honest_loss_reports_pass()
    {
        var hook = new HeldCompletenessHook(() => 3);
        WaveEndHookVerdict verdict = hook.Review(Report(wave: 4, claimsHeld: false));
        Assert.Equal(WaveEndVerdictKind.Pass, verdict.Kind);
    }

    [Fact] // legal — 漏怪路径已补（0 条）→ 放行
    public void Patched_leaks_let_the_report_ship()
    {
        var hook = new HeldCompletenessHook(() => 0);
        WaveEndHookVerdict verdict = hook.Review(Report(wave: 4, claimsHeld: true));
        Assert.Equal(WaveEndVerdictKind.Pass, verdict.Kind);
    }

    [Fact] // legal — 缺省探针（运行时漏怪数据未接线）恒 0：放行（惰性行为）
    public void Default_probe_reports_zero_leaks()
    {
        var hook = new HeldCompletenessHook();
        Assert.Equal(WaveEndVerdictKind.Pass, hook.Review(Report(wave: 4, claimsHeld: true)).Kind);
    }

    [Fact] // legal — 组合：拦截→补防→放行（链上完整回喂闭环）
    public void Feedback_loop_closes_once_leaks_are_patched()
    {
        int leaks = 2;
        var chain = new WaveEndHookChain(new IWaveEndHook[] { new HeldCompletenessHook(() => leaks) });

        WaveEndReviewResult blocked = chain.Review(Report(wave: 6, claimsHeld: true));
        Assert.False(blocked.Ship);
        Assert.Equal("held-completeness", blocked.BlockedBy);

        leaks = 0; // 按回喂原因补防后复评
        WaveEndReviewResult shipped = chain.Review(Report(wave: 6, claimsHeld: true));
        Assert.True(shipped.Ship);
        Assert.False(shipped.RoundCapped);
    }
}
