namespace EraCore.Balance;

/// <summary>
/// Shield points (v3.2 §6: 护盾 E3 起, regen 20/s after a 5 s break once
/// broken). Pure static formula, stateless. E1/E2 rows of any golden table
/// pass a startWave beyond their range and stay at 0.
/// </summary>
public static class Shield
{
    /// <summary>
    /// Shield points of one wave: 10 + 8×wave once the wave has reached
    /// <paramref name="startWave"/> (the first wave of the shielded era),
    /// 0 before — the E3 shield system must not leak backwards into E1/E2.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="startWave"/> is negative.
    /// </exception>
    public static int At(int wave, int startWave)
    {
        if (startWave < 0)
        {
            throw new ArgumentException(
                $"Shield start wave must be non-negative (got {startWave}).",
                nameof(startWave));
        }

        if (wave < startWave)
        {
            return 0;
        }

        return 10 + (8 * wave);
    }
}
