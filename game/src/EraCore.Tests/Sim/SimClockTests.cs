using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-154] SimClock（K-1 IClock 手动驱动）：30 Hz tick 域的手动推进与
// 单调性守卫。T-040。
public class SimClockTests
{
    [Fact] // legal — 从 0 起，Advance 逐 tick 前进，NowMs 按 tick 域换算
    public void Advance_steps_one_tick_and_derives_diagnostics_ms()
    {
        var clock = new SimClock();
        Assert.Equal(0, clock.Tick);

        clock.Advance();
        clock.Advance();
        Assert.Equal(2, clock.Tick);
        Assert.Equal((2 * 1000L) / 30L, clock.NowMs); // 66 ms — tick 域推导，非墙钟
    }

    [Fact] // legal — 指定起始 tick 构造 + Set 向前跳（测试/诊断缝）
    public void Set_moves_forward_to_an_absolute_tick()
    {
        var clock = new SimClock(100);
        Assert.Equal(100, clock.Tick);
        Assert.Equal((100 * 1000L) / 30L, clock.NowMs);

        clock.Set(150);
        Assert.Equal(150, clock.Tick);
    }

    [Fact] // boundary — Set 不允许回退（EnergyLedger.Settle 等子系统假设单调钟）
    public void Set_rejects_backwards_jumps()
    {
        var clock = new SimClock(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Set(9));
        Assert.Equal(10, clock.Tick); // 状态未被污染
    }

    [Fact] // illegal — 负 tick 构造被拒
    public void Constructor_rejects_negative_ticks()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimClock(-1));
    }
}
