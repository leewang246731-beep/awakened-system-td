using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Logistics;

namespace EraCore.Supply;

/// <summary>
/// Tower-side ammo magazine adapter (弹药入塔 seam of the 断供降速 logic
/// layer, T-048, F-046): the <see cref="ICargoReceiver"/> porters unload
/// ammo items into, wired straight onto the tower's <see cref="WeaponFire"/>
/// shot reserve. Accepted items convert to shots through the frozen anchor
/// <see cref="AmmoConverter.ShotsPerItem"/> (= <see cref="Carrier.ShotsPerItem"/>,
/// equality pinned by the AmmoConverter tests) via
/// <see cref="WeaponFire.ReloadShots"/>, and the cumulative delivered-shot
/// counter this class exposes is the differential-polling source the
/// <see cref="TowerSupplyMonitor"/> measures its efficiency curve against
/// (轮询弹药计数差分).
/// <para>
/// The magazine is a pass-through with unlimited room in E1: the acceptance
/// policy (capacity, overflow refusal feeding the 待卸队列) is Combat-side
/// work; what this adapter owns is the resource check and the
/// items→shots→counter bookkeeping, both pure and monotonic — the counter
/// only ever grows, so the monitor's regression guard holds. Deterministic
/// (CS-1.4: no clock, no RNG).
/// </para>
/// </summary>
public sealed class TowerMagazine : ICargoReceiver
{
    private readonly ResourceKey _ammo;
    private readonly WeaponFire _weapon;

    /// <summary>
    /// Wires the magazine of <paramref name="weapon"/> for the ammo item
    /// <paramref name="ammo"/> (the tower spec's <c>ammo_item</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException">an argument is null.</exception>
    public TowerMagazine(ResourceKey ammo, WeaponFire weapon)
    {
        ArgumentNullException.ThrowIfNull(ammo);
        ArgumentNullException.ThrowIfNull(weapon);
        _ammo = ammo;
        _weapon = weapon;
    }

    /// <summary>Ammo item this magazine accepts (tower spec ammo_item).</summary>
    public ResourceKey Ammo => _ammo;

    /// <summary>Ammo items accepted since construction.</summary>
    public long ItemsAccepted { get; private set; }

    /// <summary>
    /// Cumulative shots delivered into the tower (items ×
    /// <see cref="AmmoConverter.ShotsPerItem"/>) — the monotonic counter the
    /// supply monitor polls (交付流 observability seam).
    /// </summary>
    public long CumulativeShotsDelivered => ItemsAccepted * AmmoConverter.ShotsPerItem;

    /// <summary>
    /// <see cref="ICargoReceiver"/> seam: accepts up to
    /// <paramref name="amount"/> items of the configured ammo resource,
    /// converts them to shots onto the weapon reserve and returns the
    /// accepted part in [0, amount]. A wrong resource is refused without
    /// side effects (the porter stays queued and retries — accept 才卸).
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">amount &lt; 0.</exception>
    public int TryAccept(ResourceKey resource, int amount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Accept amount must be non-negative.");
        }

        if (!resource.Equals(_ammo))
        {
            return 0;
        }

        _weapon.ReloadShots(amount * AmmoConverter.ShotsPerItem);
        ItemsAccepted += amount;
        return amount;
    }
}
