using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// [F-049] wave count formula (C-19 前体): v3.2 §6 with 07 correction #26
// bracketization — add first, divide after, clamp 0..40 pre-multiplier,
// multiply by the threat count multiplier last, floor.
public class WaveFormulaTests
{
    // E2 golden parameter set (F.5 wave-5 anchor: 6 skirmishers at t=0.75).
    private const int Base = 10;
    private const int Begin = 5;
    private const int Spacing = 5;
    private const double Scaling = 2.0;
    private const double Mult075 = 1.25; // (5/3)×0.75 threat count multiplier

    [Theory] // legal — ramp ladder of the golden parameter set
    [InlineData(5, 6)] // (10+0)/2 × 1.25 = 6.25 → 6 (F.5 wave-5 anchor)
    [InlineData(6, 6)] // (10+0)/2 × 1.25 = 6.25 → 6 (one step before ramp)
    [InlineData(10, 6)] // (10+1)/2 × 1.25 = 6.875 → 6
    [InlineData(20, 8)] // (10+3)/2 × 1.25 = 8.125 → 8
    [InlineData(30, 9)] // (10+5)/2 × 1.25 = 9.375 → 9
    public void Count_follows_ramp_ladder(int wave, int expected)
    {
        Assert.Equal(expected, WaveFormula.Count(wave, Base, Begin, Spacing, Scaling, Mult075));
    }

    [Fact] // legal — 07 #26 bracketization: (基数+步进) as a whole is divided
    public void Count_adds_first_then_divides()
    {
        // (6 + ⌊(9−5)/2⌋) / 2.0 = 8/2 = 4; a base+⌊(wave−begin)/(spacing×scaling)⌋
        // misreading would give 6+1 = 7 — this row pins the bracketization.
        Assert.Equal(4, WaveFormula.Count(9, baseCount: 6, beginWave: 5, spacing: 2, scaling: 2.0, threatMultiplier: 1.0));
    }

    [Theory] // legal — clamp 0..40 applies before the threat multiplier
    [InlineData(100, 1, 1, 1, 1.0, 1.0, 40)] // ramped 99+ → clamped to 40
    [InlineData(100, 1, 1, 1, 1.0, 2.0, 80)] // ×2 AFTER the clamp: 40×2 = 80 (v3.2 §6 bracket order)
    [InlineData(1, 0, 5, 5, 1.0, 2.0, 0)] // base 0 → 0 regardless of multiplier
    [InlineData(1, 4, 5, 5, 1.0, 0.0, 0)] // zero multiplier → 0
    public void Count_clamps_before_multiplier(
        int wave, int baseCount, int beginWave, int spacing, double scaling, double mult, int expected)
    {
        Assert.Equal(expected, WaveFormula.Count(wave, baseCount, beginWave, spacing, scaling, mult));
    }

    [Fact] // boundary — exactly at beginWave the ramp contributes 0
    public void Count_at_begin_wave_has_zero_increment()
    {
        Assert.Equal(7, WaveFormula.Count(9, baseCount: 7, beginWave: 9, spacing: 3, scaling: 1.0, threatMultiplier: 1.0));
    }

    [Fact] // boundary — one wave below begin floors negative and clamps to 0 increment
    public void Count_below_begin_wave_clamps_increment_to_zero()
    {
        // ⌊(7−9)/3⌋ = ⌊−0.67⌋ = −1 → max(0, −1) = 0 → stays at the base
        Assert.Equal(7, WaveFormula.Count(7, baseCount: 7, beginWave: 9, spacing: 3, scaling: 1.0, threatMultiplier: 1.0));
    }

    [Theory] // illegal — guards: no division by zero, guarded return values
    [InlineData(-1, 5, 5, 1, 2.0, 1.0)] // negative wave → guard value 0
    [InlineData(-100, 5, 5, 1, 2.0, 1.0)]
    public void Count_returns_zero_for_negative_waves(
        int wave, int baseCount, int beginWave, int spacing, double scaling, double mult)
    {
        Assert.Equal(0, WaveFormula.Count(wave, baseCount, beginWave, spacing, scaling, mult));
    }

    [Theory] // illegal — spacing 0 / negative disables the ramp instead of dividing by zero
    [InlineData(0)]
    [InlineData(-3)]
    public void Count_with_non_positive_spacing_returns_guarded_base(int spacing)
    {
        // increment guarded to 0 → (10+0)/2.0 × 1.25 = 6.25 → 6, no ZeroDivideException
        Assert.Equal(6, WaveFormula.Count(30, Base, Begin, spacing, Scaling, Mult075));
    }

    [Theory] // boundary — scaling below 0.1 is lifted to 0.1 by the formula
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(0.05)]
    public void Count_floors_scaling_at_one_tenth(double scaling)
    {
        // (10 + ⌊25/5⌋) / 0.1 = 150 → clamped 40
        Assert.Equal(40, WaveFormula.Count(30, Base, Begin, Spacing, scaling, 1.0));
    }

    [Fact] // illegal — negative threat multiplier yields the guard value 0
    public void Count_rejects_negative_multiplier_via_guard()
    {
        Assert.Equal(0, WaveFormula.Count(10, Base, Begin, Spacing, Scaling, -1.5));
    }
}
