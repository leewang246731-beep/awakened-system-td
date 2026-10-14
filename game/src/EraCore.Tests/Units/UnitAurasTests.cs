using EraCore.Contracts.Foundation;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [F-035] 战鼓光环（T-066，SP-3b L120）：半径 3 格友军 +15% 速度——
// 重置再施加、多鼓取最大不叠加（SP-1a L31 同类光环纪律）、死亡单位不参与。
public class UnitAurasTests
{
    private const int CellSize = 32;

    private static Unit NewUnit(long id, float x, float y) =>
        new(new EntityId(id), "era2:enemy-warrior", x, y, 300, 4, 1.17f);

    [Fact] // legal — 半径内（含边界）友军 +15%、半径外 0
    public void War_drums_grant_fifteen_percent_inside_three_cells()
    {
        Unit drummer = NewUnit(1, 500, 500);
        drummer.SetWarDrums(3.0f * CellSize, 15);
        Unit inside = NewUnit(2, 500 + 96, 500);  // 恰在半径边界（含）
        Unit outside = NewUnit(3, 500 + 97, 500); // 边界外 1 px
        var units = new List<Unit> { drummer, inside, outside };

        int affected = UnitAuras.ApplyWarDrums(units);

        Assert.Equal(1, affected);
        Assert.Equal(15, inside.AuraSpeedBonusPercent);
        Assert.Equal(0, outside.AuraSpeedBonusPercent);
        Assert.Equal(0, drummer.AuraSpeedBonusPercent); // 不自我增益

        inside.ResolveSpeedEffects(10);
        Assert.Equal(15, inside.ResolvedSpeedBonusPercent);
        Assert.Equal(inside.SpeedPxPerTick * 1.15f, inside.EffectiveSpeedPxPerTick, 5);
    }

    [Fact] // legal — 重置语义：鼓手离场/死亡后光环消失
    public void A_pass_without_a_living_drummer_resets_the_channel()
    {
        Unit drummer = NewUnit(1, 500, 500);
        drummer.SetWarDrums(3.0f * CellSize, 15);
        Unit ally = NewUnit(2, 540, 500);
        var units = new List<Unit> { drummer, ally };

        Assert.Equal(1, UnitAuras.ApplyWarDrums(units));
        Assert.Equal(15, ally.AuraSpeedBonusPercent);

        drummer.ApplyDamage(400, tick: 5); // 甲 4 → 396 结算 ≥300 HP 致死
        Assert.False(drummer.Alive);
        Assert.Equal(0, UnitAuras.ApplyWarDrums(units));
        Assert.Equal(0, ally.AuraSpeedBonusPercent);
    }

    [Fact] // legal — 双鼓重叠取最大不叠加（SP-1a L31）
    public void Overlapping_drummers_take_the_maximum_not_the_sum()
    {
        Unit weak = NewUnit(1, 500, 500);
        weak.SetWarDrums(3.0f * CellSize, 15);
        Unit strong = NewUnit(2, 540, 500);
        strong.SetWarDrums(3.0f * CellSize, 25);
        Unit ally = NewUnit(3, 560, 500);
        var units = new List<Unit> { weak, strong, ally };

        Assert.Equal(3, UnitAuras.ApplyWarDrums(units)); // strong/ally/weak 三者均在彼此鼓界内

        ally.ResolveSpeedEffects(1);
        Assert.Equal(25, ally.ResolvedSpeedBonusPercent);   // 取最大 25
        Assert.Equal(ally.SpeedPxPerTick * 1.25f, ally.EffectiveSpeedPxPerTick, 5);
        Assert.NotEqual(ally.SpeedPxPerTick * 1.40f, ally.EffectiveSpeedPxPerTick); // 15+25 叠加被拒
        weak.ResolveSpeedEffects(1);
        Assert.Equal(25, weak.ResolvedSpeedBonusPercent);   // 弱鼓手同样只收强鼓的 25
    }

    [Fact] // legal — 顺序无关：倒序列表结果一致（确定性）
    public void Application_is_order_independent()
    {
        Unit drummer = NewUnit(1, 500, 500);
        drummer.SetWarDrums(3.0f * CellSize, 15);
        Unit a = NewUnit(2, 520, 500);
        Unit b = NewUnit(3, 540, 500);
        var forward = new List<Unit> { drummer, a, b };
        var reverse = new List<Unit> { b, a, drummer };

        Assert.Equal(UnitAuras.ApplyWarDrums(forward), UnitAuras.ApplyWarDrums(reverse));
        Assert.Equal(a.AuraSpeedBonusPercent, reverse[1].AuraSpeedBonusPercent);
    }

    [Fact] // boundary — 死亡友军不收光环、无鼓单位列表为 no-op
    public void Dead_allies_receive_nothing_and_a_drumless_list_is_a_no_op()
    {
        Unit drummer = NewUnit(1, 500, 500);
        drummer.SetWarDrums(3.0f * CellSize, 15);
        Unit dead = NewUnit(2, 520, 500);
        dead.ApplyDamage(400, tick: 1); // 致死（396 结算 ≥ 300 HP）
        Assert.Equal(0, UnitAuras.ApplyWarDrums(new List<Unit> { drummer, dead }));
        Assert.Equal(0, dead.AuraSpeedBonusPercent);

        var plain = new List<Unit> { NewUnit(3, 0, 0) };
        Assert.Equal(0, UnitAuras.ApplyWarDrums(plain));
    }

    [Fact] // illegal — 空列表入参确实被拒绝
    public void Null_list_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => UnitAuras.ApplyWarDrums(null!));
}
