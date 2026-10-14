using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Tests.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-073][F-075] T-070 DEBT-3：卷轴触发 3 s 冷却=90 tick@30Hz（SP-2 #12/
// F.1/§11.4 仲裁"1 点+3s 冷却，不占常驻上限"）。窗口语义：T 触发 →
// T..T+89 拒、T+90 放（恰好满 3 s）；失败不消耗窗口（"失败动作不扣费"
// 同律）；tick 回退（回放乱序）不触发窗口。

/// <summary>[F-073] TriggerScroll cooldown window (90 ticks = 3 s @ 30 Hz).</summary>
public class ScrollTriggerCooldownTests
{
    [Fact] // legal — 初始无窗口：任意 tick 首触发不受限
    public void Fresh_state_never_blocks()
    {
        var cooldown = new ScrollTriggerCooldown();
        Assert.False(cooldown.OnCooldown(0, out int remaining));
        Assert.Equal(0, remaining);
        Assert.False(cooldown.OnCooldown(10_000, out _));
    }

    [Theory] // boundary — T 触发后：T+89 拒（余 1）、T+90 放（满 3 s）
    [InlineData(0, 89, true, 1)]
    [InlineData(0, 90, false, 0)]
    [InlineData(500, 589, true, 1)]
    [InlineData(500, 590, false, 0)]
    public void Window_blocks_until_ninety_ticks_elapse(long accepted, long probe, bool expectedBlocked, int expectedRemaining)
    {
        var cooldown = new ScrollTriggerCooldown();
        cooldown.Accept(accepted);
        Assert.Equal(expectedBlocked, cooldown.OnCooldown(probe, out int remaining));
        Assert.Equal(expectedRemaining, remaining);
    }

    [Fact] // legal — 窗口内剩余计数递减：T+0 余 90、T+1 余 89 …
    public void Remaining_counts_down_inside_the_window()
    {
        var cooldown = new ScrollTriggerCooldown();
        cooldown.Accept(1_000);
        Assert.True(cooldown.OnCooldown(1_000, out int first));
        Assert.Equal(90, first);
        Assert.True(cooldown.OnCooldown(1_001, out int second));
        Assert.Equal(89, second);
        Assert.True(cooldown.OnCooldown(1_045, out int middle));
        Assert.Equal(45, middle);
    }

    [Fact] // boundary — tick 回退（回放/乱序）不读作冷却中
    public void Backward_ticks_read_as_off_cooldown()
    {
        var cooldown = new ScrollTriggerCooldown();
        cooldown.Accept(500);
        Assert.False(cooldown.OnCooldown(499, out _));
        Assert.False(cooldown.OnCooldown(0, out _));
    }

    [Fact] // legal — 新触发重开窗口：T+90 再触发后 T+91 又被拒
    public void Accepting_again_reopens_the_window()
    {
        var cooldown = new ScrollTriggerCooldown();
        cooldown.Accept(0);
        Assert.False(cooldown.OnCooldown(90, out _));
        cooldown.Accept(90);
        Assert.True(cooldown.OnCooldown(91, out int remaining));
        Assert.Equal(89, remaining);
    }

    [Fact] // boundary — 常量=90 tick（3 s @ 30 Hz，冻结口径）
    public void Cooldown_window_is_ninety_ticks()
    {
        Assert.Equal(90, ScrollTriggerCooldown.CooldownTicks);
    }

    [Fact] // legal — 注册表唯一门：失败触发不消耗窗口（两次未成功调用都不得报 scroll-cooldown）
    public void Failed_registry_triggers_do_not_consume_the_cooldown()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E2);

        // 无装备卷轴（缺省 armed=null）→ 首次触发失败（handler 档拒绝或未接线拒绝，档位随 T-068 演进）
        ActionOutcome first = registry.Invoke(ActionJsonSamples.TriggerScroll());
        Assert.NotEqual(ActionResultStatus.Ok, first.Result.Status);
        Assert.DoesNotContain("scroll-cooldown", first.Result.ErrorMessage, StringComparison.Ordinal);

        // 第二次触发必须同样不是 scroll-cooldown——失败未开窗（成功才 Accept）
        ActionOutcome second = registry.Invoke(ActionJsonSamples.TriggerScroll());
        Assert.NotEqual(ActionResultStatus.Ok, second.Result.Status);
        Assert.DoesNotContain("scroll-cooldown", second.Result.ErrorMessage, StringComparison.Ordinal);
    }
}
