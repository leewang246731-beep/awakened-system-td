using EraCore.Combat;

namespace EraCore.Presentation;

/// <summary>
/// Fill tier of the ammo segment of a tower-head status bar (T-093,
/// F-120 三层信息②): the color decision only — pixel painting stays in
/// the Godot layer.
/// </summary>
public enum HeadBarTier
{
    /// <summary>Healthy fill (no tint).</summary>
    Normal = 0,

    /// <summary>A low fill — amber (visible but not alarming).</summary>
    Amber = 1,

    /// <summary>A critical fill — red (about to starve).</summary>
    Red = 2,

    /// <summary>The bar does not apply to this tower (no ammo consumption).</summary>
    Hidden = 3,
}

/// <summary>
/// One tower-head status bar view (T-093, F-120 三层信息② 塔头状态条):
/// ammo fill ratio + tier + reload progress ratio. E1 towers carry the
/// ammo bar only when their spec consumes ammo (<c>ammo_per_shot &gt; 0</c>);
/// towers without consumption (traps/campfires) hide it. The fill
/// reference is one delivered ammo item (1 件=10 发, v3.2 §5) — the same
/// unit the magazine counts in.
/// </summary>
/// <param name="AmmoRatio">Ammo fill in [0,1] (shots ÷ shots-per-item).</param>
/// <param name="AmmoTier">Ammo color tier (or <see cref="HeadBarTier.Hidden"/>).</param>
/// <param name="ReloadRatio">Reload progress in [0,1] (1 = just fired, 0 = ready).</param>
public readonly record struct TowerHeadBarView(
    double AmmoRatio,
    HeadBarTier AmmoTier,
    double ReloadRatio);

/// <summary>
/// Pure decisions of the tower-head status bars (T-093, F-120): ratio
/// math + tier thresholds. Presentation constants (not balance anchors):
/// the amber/red bands mirror the two-tier grey discipline of the build
/// bar (§10.7 两级灰度先例) applied vertically — amber at ≤30%, red at
/// ≤10% of the one-item reference.
/// </summary>
public static class TowerHeadBar
{
    /// <summary>Ammo fill ≤ this ratio shows amber (两级灰度先例的一级).</summary>
    public const double AmberRatio = 0.30;

    /// <summary>Ammo fill ≤ this ratio shows red (两级灰度先例的二级).</summary>
    public const double RedRatio = 0.10;

    /// <summary>
    /// Builds the bar view of one tower.
    /// </summary>
    /// <param name="spec">Tower registry row (ammo need + reload cycle).</param>
    /// <param name="ammoShotsRemaining">Weapon reserve in shots (<see cref="WeaponFire.AmmoShotsRemaining"/>).</param>
    /// <param name="shotsPerItem">Shots per ammo item (AmmoConverter.ShotsPerItem, v3.2 §5).</param>
    /// <param name="cooldownTicksRemaining">Reload cooldown ticks remaining.</param>
    /// <exception cref="ArgumentOutOfRangeException">shotsPerItem &lt; 1 or a remaining value is negative.</exception>
    public static TowerHeadBarView Build(
        TowerSpec spec, long ammoShotsRemaining, int shotsPerItem, int cooldownTicksRemaining)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (shotsPerItem < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(shotsPerItem), shotsPerItem, "must be >= 1.");
        }

        if (ammoShotsRemaining < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ammoShotsRemaining), ammoShotsRemaining, "must be >= 0.");
        }

        if (cooldownTicksRemaining < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldownTicksRemaining), cooldownTicksRemaining, "must be >= 0.");
        }

        if (spec.AmmoPerShot <= 0 || spec.AmmoItem is null)
        {
            return new TowerHeadBarView(0d, HeadBarTier.Hidden, 0d); // 无弹耗塔：不出弹药条
        }

        double ratio = Math.Clamp(ammoShotsRemaining / (double)shotsPerItem, 0d, 1d);
        HeadBarTier tier = ratio <= RedRatio ? HeadBarTier.Red
            : ratio <= AmberRatio ? HeadBarTier.Amber
            : HeadBarTier.Normal;
        double reload = spec.ReloadTicks is int reloadTicks && reloadTicks > 0
            ? Math.Clamp(cooldownTicksRemaining / (double)reloadTicks, 0d, 1d)
            : 0d;
        return new TowerHeadBarView(ratio, tier, reload);
    }
}
