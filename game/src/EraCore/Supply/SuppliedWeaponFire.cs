using EraCore.Combat;
using EraCore.Contracts.Foundation;

namespace EraCore.Supply;

/// <summary>
/// WeaponFire 联动 of the 断供降速 logic layer (T-048, F-046): paces one
/// tower's <see cref="WeaponFire"/> loop by its
/// <see cref="TowerSupplyMonitor"/> efficiency so the fire rate is
/// <c>base × efficiency</c> (塔射速乘 efficiency). The wrapper owns ONLY
/// the pacing seam — reload counting, shot accounting, hit settlement and
/// the structured fire events stay in the reused WeaponFire, untouched.
/// <para>
/// Pacing mechanics (断链减速不停摆): the weapon's cooldown counter only
/// advances when this wrapper calls <c>WeaponFire.Tick()</c>; a fractional
/// credit accumulator adds the tower's efficiency each tick and spends
/// whole ticks on the cooldown — at efficiency 0.5 a 30-tick reload takes
/// exactly 60 ticks (DPS halves), at 0.2 it takes 150 (DPS ×0.2). At
/// efficiency 0 the cooldown freezes armed, so the tower fires at most the
/// shot already in cycle and then holds — it never throws, never loses
/// magazine shots, and resumes exactly where the curve recovers (恢复路径
/// 后效率回升 ⇒ 射速回升). Towers without ammo demand read efficiency 1
/// and pace exactly like a bare WeaponFire.
/// </para>
/// <para>
/// Deterministic: pure per-tick credit arithmetic over the monitor's
/// curve, no RNG, no wall clock (CS-1.4).
/// </para>
/// </summary>
public sealed class SuppliedWeaponFire
{
    private readonly WeaponFire _weapon;
    private readonly TowerSupplyMonitor _monitor;

    /// <summary>
    /// Wraps the fire loop of the tower registered on
    /// <paramref name="monitor"/> under <paramref name="towerId"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="towerId"/> is not registered on the monitor.
    /// </exception>
    public SuppliedWeaponFire(WeaponFire weapon, TowerSupplyMonitor monitor, EntityId towerId)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(towerId);
        if (!monitor.Towers.Contains(towerId))
        {
            throw new ArgumentException(
                $"Tower {towerId.Value} is not registered on the monitor — register the demand first.",
                nameof(towerId));
        }

        _weapon = weapon;
        _monitor = monitor;
        TowerId = towerId;
    }

    /// <summary>Stable id of the paced tower (monitor registration key).</summary>
    public EntityId TowerId { get; }

    /// <summary>The wrapped fire loop (reload/ammo/event concerns stay there).</summary>
    public WeaponFire Weapon => _weapon;

    /// <summary>
    /// Efficiency applied by the last <see cref="Tick"/> (curve value; 1
    /// before the first tick — no throttling before data).
    /// </summary>
    public double Efficiency { get; private set; } = 1.0;

    /// <summary>
    /// Full-rate DPS anchor from the registration
    /// (<c>damage_per_shot × 发/s</c>) — the base the efficiency scales.
    /// </summary>
    public double FullDps => _monitor.FullDpsOf(TowerId);

    /// <summary>
    /// Current effective DPS: <c>FullDps × Efficiency</c> — the observable
    /// the 断链 DPS 序列 samples assert against (DPS 按效率下降).
    /// </summary>
    public double EffectiveDps => FullDps * Efficiency;

    /// <summary>
    /// Advances exactly one simulation tick: reads the tower's efficiency
    /// from the monitor and advances the wrapped weapon cooldown by that
    /// many ticks (fractional credit, whole-tick spend — see the class
    /// remarks). Call once per 30 Hz tick, after
    /// <see cref="TowerSupplyMonitor.Tick"/> so the pacing follows the
    /// freshest curve value.
    /// </summary>
    public void Tick()
    {
        Efficiency = _monitor.EfficiencyOf(TowerId);
        _credit += Efficiency;
        while (_credit >= 1.0)
        {
            _weapon.Tick();
            _credit -= 1.0;
        }
    }

    /// <summary>
    /// Fires when the wrapped loop is ready (cooldown spent AND shots
    /// available); otherwise returns null touching nothing. Delegates to
    /// <see cref="WeaponFire.TryFire"/> — the pacing above is the only
    /// difference from a bare weapon loop.
    /// </summary>
    /// <param name="targetId">Target of the shot.</param>
    /// <param name="tick">Current simulation tick (event timestamp).</param>
    public WeaponFireEvent? TryFire(EntityId targetId, long tick) => _weapon.TryFire(targetId, tick);

    private double _credit;
}
