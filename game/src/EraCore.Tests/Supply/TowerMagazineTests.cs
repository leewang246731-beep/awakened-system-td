using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Logistics;
using EraCore.Supply;
using Xunit;

namespace EraCore.Tests.Supply;

/// <summary>
/// Unit tests of the tower magazine adapter (弹药入塔 seam, T-048, F-046):
/// resource gating (accept 才卸), the frozen items→shots conversion
/// (1 件弹药 = 10 发, pinned against <see cref="AmmoConverter.ShotsPerItem"/>
/// and <see cref="Carrier.ShotsPerItem"/>), forwarding onto the real
/// <see cref="WeaponFire"/> shot reserve, and the monotonic cumulative
/// counter the supply monitor polls.
/// </summary>
// [C-06][F-020][F-046] 弹药入塔：换算锚点 + 计数器 + 资源门
public class TowerMagazineTests
{
    private static readonly ResourceKey Arrow = new("era1:ammo-arrow");

    private static readonly ResourceKey Wood = new("era1:res-wood");

    private static WeaponFire ArmedTower(int shots = 0) =>
        new(new EntityId(1), Tests.Combat.CombatContent.Tower("era1:tower-wood-arrow"), shots);

    [Fact] // legal — the three shot-per-item anchors cannot drift apart
    public void Shots_per_item_anchors_stay_equal()
    {
        // [F-020] 1 件弹药 = 10 发 (v3.2 §5): converter, carrier and magazine
        // all derive from one frozen number
        Assert.Equal(10, AmmoConverter.ShotsPerItem);
        Assert.Equal(AmmoConverter.ShotsPerItem, Carrier.ShotsPerItem);
    }

    [Fact] // legal — accepted items land on the weapon reserve ×10
    public void Accepted_items_convert_to_shots_on_the_weapon()
    {
        WeaponFire weapon = ArmedTower(shots: 0);
        var magazine = new TowerMagazine(Arrow, weapon);

        int accepted = magazine.TryAccept(Arrow, 3);

        Assert.Equal(3, accepted);
        Assert.Equal(3, magazine.ItemsAccepted);
        Assert.Equal(30, magazine.CumulativeShotsDelivered);
        Assert.Equal(30, weapon.AmmoShotsRemaining); // × AmmoConverter.ShotsPerItem
    }

    [Fact] // legal — partial amounts and repeated loads accumulate monotonically
    public void Repeated_loads_accumulate()
    {
        WeaponFire weapon = ArmedTower(shots: 5);
        var magazine = new TowerMagazine(Arrow, weapon);

        magazine.TryAccept(Arrow, 1);
        magazine.TryAccept(Arrow, 2);

        Assert.Equal(3, magazine.ItemsAccepted);
        Assert.Equal(30, magazine.CumulativeShotsDelivered);
        Assert.Equal(35, weapon.AmmoShotsRemaining); // 5 initial + 30 delivered
    }

    [Fact] // legal — a wrong resource is refused without side effects
    public void Wrong_resource_is_refused_untouched()
    {
        WeaponFire weapon = ArmedTower(shots: 0);
        var magazine = new TowerMagazine(Arrow, weapon);

        Assert.Equal(0, magazine.TryAccept(Wood, 2));
        Assert.Equal(0, magazine.ItemsAccepted);
        Assert.Equal(0, magazine.CumulativeShotsDelivered);
        Assert.Equal(0, weapon.AmmoShotsRemaining);
    }

    [Fact] // legal — zero amount is a legal no-op accept
    public void Zero_amount_is_a_no_op()
    {
        var magazine = new TowerMagazine(Arrow, ArmedTower());
        Assert.Equal(0, magazine.TryAccept(Arrow, 0));
        Assert.Equal(0, magazine.ItemsAccepted);
    }

    [Fact] // illegal — null resource / negative amount / null ctor args
    public void Illegal_inputs_are_rejected()
    {
        var magazine = new TowerMagazine(Arrow, ArmedTower());
        Assert.Throws<ArgumentNullException>(() => magazine.TryAccept(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => magazine.TryAccept(Arrow, -1));
        Assert.Throws<ArgumentNullException>(() => new TowerMagazine(null!, ArmedTower()));
        Assert.Throws<ArgumentNullException>(() => new TowerMagazine(Arrow, null!));
    }
}
