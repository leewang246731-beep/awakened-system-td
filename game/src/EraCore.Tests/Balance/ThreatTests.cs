using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// Threat level tests (SP-9 邻接敌营判定口径 + v3.2 §7 clamp formula).
// Float tolerance 1e-9: expected values are exact decimal literals of the
// frozen formula outputs (0.3+0.3×n lands on clean doubles for the table
// rows); 1e-9 absorbs the last-ulp rounding of the additions.
// [C-19] threat level (F-047)
public class ThreatTests
{
    [Theory] // legal — SP-9 代入示例 three anchors (v3.2 §7 frozen)
    [InlineData(0, 0.3)] // 孤关: 0.3+0.3×0 = 0.3 (lower bound, no clamp active)
    [InlineData(2, 0.9)] // 双面受敌: 0.3+0.6 = 0.9 (in band)
    [InlineData(5, 1.2)] // 围城: 0.3+1.5 = 1.8 → clamp → 1.2 (upper bound)
    public void ClampThreat_matches_SP9_examples(int camps, double expected)
    {
        Assert.Equal(expected, Threat.ClampThreat(camps), precision: 9);
    }

    [Theory] // legal — every in-band step of the 0.3 ladder
    [InlineData(1, 0.6)]
    [InlineData(3, 1.2)] // 0.3+0.9 = 1.2 exactly on the cap, clamp inactive
    public void ClampThreat_steps_by_three_tenths(int camps, double expected)
    {
        Assert.Equal(expected, Threat.ClampThreat(camps), precision: 9);
    }

    [Theory] // boundary — beyond the cap the value pins at Threat.Max
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(1000)]
    public void ClampThreat_pins_at_max_above_three_camps(int camps)
    {
        Assert.Equal(Threat.Max, Threat.ClampThreat(camps), precision: 9);
    }

    [Fact] // boundary — zero camps lands exactly on Threat.Min
    public void ClampThreat_floor_equals_threat_min()
    {
        Assert.Equal(Threat.Min, Threat.ClampThreat(0), precision: 9);
    }

    [Theory] // illegal — negative camp counts are rejected, not clamped
    [InlineData(-1)]
    [InlineData(-100)]
    public void ClampThreat_rejects_negative_camps(int camps)
    {
        Assert.Throws<ArgumentException>(() => Threat.ClampThreat(camps));
    }
}
