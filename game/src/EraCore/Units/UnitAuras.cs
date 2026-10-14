namespace EraCore.Units;

/// <summary>
/// War-drum aura pass (T-066; SP-3b L120 战鼓：半径 3 格友军 +15% 速度 —
/// the centurion row, and the Ironhoof boss row carries the same aura per
/// the T-066 task card). One pass over the unit list recomputes the aura
/// channel for every unit: reset to 0, then every drummer grants its bonus
/// to every OTHER living unit whose centre lies within its radius
/// (inclusive). Where auras overlap the STRONGER bonus wins — the same
/// "take the max, never stack" discipline the support class carries
/// (SP-1a L31 同类光环取最大不叠加).
/// <para>
/// The pass is order-independent (max fold) and holds no state, so calling
/// it once per tick before <see cref="Unit.ResolveSpeedEffects"/> keeps the
/// whole speed pipeline replay-stable.
/// </para>
/// </summary>
public static class UnitAuras
{
    /// <summary>
    /// Recomputes the war-drum aura channel of every unit in the list.
    /// Units are allies of each other by construction (one enemy roster);
    /// heroes live outside this pass.
    /// </summary>
    /// <returns>Number of units currently carrying a positive aura bonus.</returns>
    public static int ApplyWarDrums(IReadOnlyList<Unit> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        foreach (Unit unit in units)
        {
            unit.SetAuraSpeedBonus(0);
        }

        foreach (Unit drummer in units)
        {
            if (!drummer.Alive || drummer.WarDrumsRadiusPx <= 0 || drummer.WarDrumsSpeedBonusPercent <= 0)
            {
                continue;
            }

            float radiusSquared = drummer.WarDrumsRadiusPx * drummer.WarDrumsRadiusPx;
            foreach (Unit ally in units)
            {
                if (!ally.Alive || ReferenceEquals(ally, drummer))
                {
                    continue;
                }

                float dx = ally.X - drummer.X;
                float dy = ally.Y - drummer.Y;
                if ((dx * dx) + (dy * dy) <= radiusSquared
                    && drummer.WarDrumsSpeedBonusPercent > ally.AuraSpeedBonusPercent)
                {
                    ally.SetAuraSpeedBonus(drummer.WarDrumsSpeedBonusPercent);
                }
            }
        }

        int affected = 0;
        foreach (Unit unit in units)
        {
            if (unit.AuraSpeedBonusPercent > 0)
            {
                affected++;
            }
        }

        return affected;
    }
}
