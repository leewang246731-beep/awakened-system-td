using EraCore.Economy;
using Xunit;

namespace EraCore.Tests.Economy;

// [C-06][F-021] §5.3 efficiency law (T-034): 效率 = min(供给率, 能源
// satisfaction)，结果 clamp 到 [0,1]；需求≤0 恒 1 由 EnergyLedger 侧
// （SatisfactionOf）负责并在其测试覆盖——本表驱动 min 链本身的全部
// 组合形态。min/clamp 只在两个入参中取值、不做算术，故断言用精确相等。
// [C-06][F-021]
public class EfficiencyLawTests
{
    // ---- 公式组 4/6：效率 min 表 -----------------------------------------

    [Theory] // legal — the min composition over the (supply, energy) matrix
    [InlineData(1.0, 1.0, 1.0)] // both full
    [InlineData(0.5, 1.0, 0.5)] // supply limits
    [InlineData(1.0, 0.5, 0.5)] // energy limits (能源 0.5 ⇒ 加工 0.5)
    [InlineData(0.25, 0.5, 0.25)] // the smaller dimension wins
    [InlineData(0.0, 1.0, 0.0)] // starved supply ⇒ 0
    [InlineData(1.0, 0.0, 0.0)] // starved energy ⇒ 0
    [InlineData(0.0, 0.0, 0.0)] // both starved
    [InlineData(0.75, 0.8, 0.75)] // fractional both
    public void Min_takes_the_smaller_dimension(double supply, double energy, double expected)
    {
        Assert.Equal(expected, EfficiencyLaw.Min(supply, energy));
    }

    [Theory] // boundary — over-supplied dimensions clamp into [0,1], never above
    [InlineData(0.8, 1.5, 0.8)] // energy above 1 clamps after the min
    [InlineData(1.5, 0.9, 0.9)] // supply above 1 cannot leak through
    [InlineData(1.5, 1.5, 1.0)] // both above ⇒ exactly 1
    [InlineData(-0.5, 1.0, 0.0)] // a negative rate clamps to 0
    [InlineData(1.0, -0.5, 0.0)]
    [InlineData(0.0, 0.0, 0.0)] // the closed lower corner
    [InlineData(1.0, 1.0, 1.0)] // the closed upper corner
    public void Min_clamps_into_the_unit_interval(double supply, double energy, double expected)
    {
        Assert.Equal(expected, EfficiencyLaw.Min(supply, energy));
    }

    [Theory] // illegal — non-finite rates are wiring bugs, not slowdowns
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Min_rejects_non_finite_supply_rates(double bad)
    {
        Assert.Throws<ArgumentException>(() => EfficiencyLaw.Min(bad, 1.0));
    }

    [Theory] // illegal — same on the energy dimension
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Min_rejects_non_finite_energy_satisfaction(double bad)
    {
        Assert.Throws<ArgumentException>(() => EfficiencyLaw.Min(1.0, bad));
    }
}
