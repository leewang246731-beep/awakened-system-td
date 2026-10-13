using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// Threat multiplier mapping table: all 12 cells of v3.2 appendix F.2
// asserted table-driven, plus band edges and out-of-band clamping.
// Float tolerance 0.005: F.2 prints the table rounded to 2 decimals
// (0.83/1.17/1.53/1.07); the tolerance is half that last digit, source of
// both the values and the rounding = v3.2 附录 F.2.
// [F-049] multiplier mapping table
public class ThreatMultipliersTests
{
    [Theory] // legal — the 12-cell F.2 table, one row per threat level
    [InlineData(0.3, 0.5, 0.50, 2.0)] // t=0.3
    [InlineData(0.6, 1.0, 0.83, 1.53)] // t=0.6 (0.8333/1.5333 rounded in F.2)
    [InlineData(0.9, 1.5, 1.17, 1.07)] // t=0.9 (1.1667/1.0667 rounded in F.2)
    [InlineData(1.2, 2.0, 1.50, 0.6)] // t=1.2
    public void Multipliers_fill_all_twelve_F2_cells(double t, double count, double hp, double interval)
    {
        ThreatMultipliers m = Threat.Multipliers(t);

        Assert.Equal(count, m.Count, precision: 2);
        Assert.Equal(hp, m.Hp, precision: 2);
        Assert.Equal(interval, m.Interval, precision: 2);
    }

    [Fact] // legal — count multiplier is exactly (5/3)t on all four anchors
    public void Count_multiplier_hits_F2_anchors_exactly()
    {
        // 5t/3 evaluates exactly on the F.2 column (binary-exact 0.5/1.0/1.5/2.0)
        Assert.Equal(0.5, Threat.Multipliers(0.3).Count, precision: 9);
        Assert.Equal(1.0, Threat.Multipliers(0.6).Count, precision: 9);
        Assert.Equal(1.5, Threat.Multipliers(0.9).Count, precision: 9);
        Assert.Equal(2.0, Threat.Multipliers(1.2).Count, precision: 9);
    }

    [Fact] // boundary — band edges t=0.3/t=1.2 keep interval inside [0.5, 2]
    public void Interval_stays_inside_clamp_at_band_edges()
    {
        Assert.Equal(2.0, Threat.Multipliers(0.3).Interval, precision: 3);
        Assert.Equal(0.6, Threat.Multipliers(1.2).Interval, precision: 3);
    }

    [Theory] // boundary — clamp 外: only Interval clamps; Count/Hp extrapolate linearly
    [InlineData(0.0, 0.0, 0.1666666667, 2.0)] // below band: interval pinned at 2.0
    [InlineData(2.0, 3.3333333333, 2.3888888889, 0.5)] // above band: 0.5+1.7/0.9, interval pinned at 0.5
    public void Multipliers_extrapolate_but_clamp_interval(double t, double count, double hp, double interval)
    {
        ThreatMultipliers m = Threat.Multipliers(t);

        Assert.Equal(count, m.Count, precision: 3);
        Assert.Equal(hp, m.Hp, precision: 3);
        Assert.Equal(interval, m.Interval, precision: 3);
    }

    [Theory] // illegal — negative threat is rejected
    [InlineData(-0.1)]
    [InlineData(-5.0)]
    public void Multipliers_reject_negative_threat(double t)
    {
        Assert.Throws<ArgumentException>(() => Threat.Multipliers(t));
    }

    [Fact] // legal — the record is a structural value type (F.2 rows compare by value)
    public void Multiplier_records_compare_structurally()
    {
        Assert.Equal(Threat.Multipliers(0.9), Threat.Multipliers(0.9));
        Assert.NotEqual(Threat.Multipliers(0.9), Threat.Multipliers(1.2));
    }
}
