using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [C-10] Unit 实体与 E1 薄版直伤结算（T-039）：max(d−甲, ceil(10%·d))——
// 任务卡口径；T-038 Combat 落地后由 v3.2 §6 全式接管（合并切换点）。
// [F-035] 敌人单位体系的行为面；K-6 IUnit 草案实现类型面。
public class UnitTests
{
    private static Unit NewUnit(int maxHp = 100, int armor = 0, float speed = 1.0f) =>
        new(new EntityId(1), "era1:enemy-test", 16f, 16f, maxHp, armor, speed);

    [Fact] // legal — Unit 实现 K-6 IUnit 草案（C-09 塔 targeting 的冻结缝）
    public void Unit_implements_the_K6_IUnit_seam()
    {
        Unit unit = NewUnit(maxHp: 280, armor: 3, speed: 0.96f);

        Assert.IsAssignableFrom<IUnit>(unit);
        IUnit seam = unit;
        Assert.Equal(new EntityId(1), seam.Id);
        Assert.Equal("era1:enemy-test", seam.ContentId);
        Assert.Equal(280, seam.MaxHp);
        Assert.Equal(280, seam.Hp);
        Assert.Equal(3, seam.Armor);
        Assert.Equal(0, seam.Shield); // E1 无护盾（v3.2 §6：护盾 E3 起）
        Assert.Equal(0.96f, seam.SpeedPxPerTick);
        Assert.Equal(16f, seam.X);
        Assert.Equal(16f, seam.Y);
    }

    [Theory] // legal — 直伤公式各行：装甲减伤与 10% 下限
    [InlineData(10, 6, 4)]    // 踏群者甲 6 吃木箭塔单发 10 → 4（SP-3a L107 × towers.json d=10）
    [InlineData(10, 0, 10)]   // 无甲全额
    [InlineData(100, 8, 92)]  // 撞木甲 8（SP-3a L105）
    [InlineData(100, 3, 97)]  // 蛮勇士甲 3（SP-3a L104）
    [InlineData(3, 6, 1)]     // 甲超过伤害 → 10% 下限 ceil(0.3)=1，护甲永不归零伤害
    [InlineData(5, 4, 1)]     // 5−4=1 与 ceil(0.5)=1 恰相等
    [InlineData(1, 0, 1)]     // 最小伤害 1
    [InlineData(17, 0, 17)]
    public void ApplyDamage_settles_max_damage_minus_armor_against_ten_percent_floor(
        int rawDamage, int armor, int expectedEffective)
    {
        Unit unit = NewUnit(maxHp: 1000, armor: armor);

        int dealt = unit.ApplyDamage(rawDamage, tick: 7);

        Assert.Equal(expectedEffective, dealt);
        Assert.Equal(1000 - expectedEffective, unit.Hp);
        Assert.True(unit.Alive);
    }

    [Fact] // boundary — 甲恰等于伤害时走 10% 分支（d−甲=0）
    public void ApplyDamage_with_armor_equal_to_damage_takes_ten_percent_branch()
    {
        Unit unit = NewUnit(maxHp: 100, armor: 10);

        int dealt = unit.ApplyDamage(10, tick: 1);

        Assert.Equal(1, dealt); // max(0, ceil(10%×10)=1) = 1
        Assert.Equal(99, unit.Hp);
    }

    [Theory] // illegal — 非正伤害以 ArgumentOutOfRangeException 拒绝（确实拒绝）
    [InlineData(0)]
    [InlineData(-5)]
    public void ApplyDamage_rejects_non_positive_damage(int rawDamage)
    {
        Unit unit = NewUnit();
        Assert.Throws<ArgumentOutOfRangeException>(() => unit.ApplyDamage(rawDamage, tick: 1));
    }

    [Fact] // legal — 血量归零：恰一次死亡事件、死亡 tick、Alive=false、Hp 夹 0
    public void Death_settles_once_with_event_and_tick_stamp()
    {
        Unit unit = NewUnit(maxHp: 100, armor: 6);
        int raised = 0;
        Unit? delivered = null;
        unit.Died += u =>
        {
            raised++;
            delivered = u;
        };

        int first = unit.ApplyDamage(40, tick: 30);  // 40−6=34 → Hp 66
        int lethal = unit.ApplyDamage(100, tick: 45); // 100−6=94 → Hp 0（过量夹 0）
        int after = unit.ApplyDamage(50, tick: 50);   // 死者不再结算

        Assert.Equal(34, first);
        Assert.Equal(94, lethal);
        Assert.Equal(0, after);
        Assert.Equal(0, unit.Hp);          // 过量不落负
        Assert.False(unit.Alive);
        Assert.Equal(45, unit.DeathTick);
        Assert.Equal(1, raised);           // 恰一次
        Assert.Same(unit, delivered);
    }

    [Fact] // boundary — 致死一击恰等于剩余 HP（零界击杀）
    public void Death_at_exact_zero_hp()
    {
        Unit unit = NewUnit(maxHp: 34, armor: 6);
        int raised = 0;
        unit.Died += _ => raised++;

        unit.ApplyDamage(40, tick: 9); // 34 伤害恰清空 34 HP

        Assert.Equal(0, unit.Hp);
        Assert.False(unit.Alive);
        Assert.Equal(9, unit.DeathTick);
        Assert.Equal(1, raised);
    }

    [Fact] // legal — 死亡事件在伤害调用内联同步派发（CS-1.5 语义）
    public void Died_event_fires_inline_during_the_damaging_call()
    {
        Unit unit = NewUnit(maxHp: 10);
        long observedAtDeath = -1;
        unit.Died += _ => observedAtDeath = unit.DeathTick; // 若内联，此刻已可见

        unit.ApplyDamage(10, tick: 12);

        Assert.Equal(12, observedAtDeath);
    }

    [Theory] // illegal — 构造参数违约：空 contentId/非正 HP/负甲/负速度
    [InlineData("", 100, 0, 1.0f)]
    [InlineData("era1:enemy-test", 0, 0, 1.0f)]
    [InlineData("era1:enemy-test", -5, 0, 1.0f)]
    [InlineData("era1:enemy-test", 100, -1, 1.0f)]
    [InlineData("era1:enemy-test", 100, 0, -0.1f)]
    public void Constructor_rejects_malformed_state(string contentId, int maxHp, int armor, float speed)
    {
        Assert.Throws<ArgumentException>(
            () => new Unit(new EntityId(1), contentId, 0f, 0f, maxHp, armor, speed));
    }

    [Fact] // legal — 踏犯调参仅经 spawn 期注入（正三元组），非法组合拒绝
    public void Trample_tuning_requires_positive_triple()
    {
        Unit unit = NewUnit();
        unit.SetTrample(60, 64f, 45);
        Assert.Equal(60, unit.TrampleDamage);
        Assert.Equal(64f, unit.TrampleRadiusPx);
        Assert.Equal(45, unit.TramplePeriodTicks);

        Assert.Throws<ArgumentException>(() => unit.SetTrample(0, 64f, 45));
        Assert.Throws<ArgumentException>(() => unit.SetTrample(60, 0f, 45));
        Assert.Throws<ArgumentException>(() => unit.SetTrample(60, 64f, 0));
    }

    // ------------------------------------------------ T-066 全量新增（护盾/速度通道/甲减）

    [Fact] // legal — 护盾前置：盾满格吃整发、HP 不动（F-032 接 Unit.ApplyDamage 前置）
    public void Shield_absorbs_the_whole_hit_before_hp()
    {
        Unit unit = NewUnit(maxHp: 200, armor: 6);
        unit.SetShield(80);

        int settled = unit.ApplyDamage(50, tick: 10);

        Assert.Equal(50, settled);
        Assert.Equal(30, unit.Shield);
        Assert.Equal(200, unit.Hp);
        Assert.True(unit.Alive);
    }

    [Fact] // legal — 穿甲弹命中打断盾再生（非穿甲不打断——对照在 ShieldStateTests）
    public void Pierce_shot_interrupts_unit_shield_regeneration()
    {
        Unit unit = NewUnit(maxHp: 200);
        unit.SetShield(60);
        unit.ApplyDamage(30, tick: 10, pierce: true); // 穿甲命中余 30

        for (long t = 11; t <= 99; t++)
        {
            unit.TickShield(t); // 3 s（90 ticks）阻断窗内不回
        }

        Assert.Equal(30, unit.Shield);

        for (long t = 100; t <= 129; t++)
        {
            unit.TickShield(t);
        }

        Assert.Equal(50, unit.Shield); // 门后 1 s 回 20
    }

    [Fact] // legal — 甲减 debuff：EffectiveArmorAt 折叠到期、伤害走减后甲（SP-6 破甲突刺）
    public void Armor_reduction_folds_into_the_damage_formula_until_expiry()
    {
        Unit unit = NewUnit(maxHp: 500, armor: 8);
        Assert.Equal(8, unit.EffectiveArmorAt(0));

        unit.ApplyArmorReduction(4, untilTick: 300);
        Assert.Equal(4, unit.EffectiveArmorAt(299));
        Assert.Equal(8, unit.EffectiveArmorAt(300)); // 到期即恢复

        int settled = unit.ApplyDamage(100, tick: 10); // DirectInt(100, 4)=96
        Assert.Equal(96, settled);
        Assert.Equal(404, unit.Hp);
    }

    [Fact] // boundary — 甲减超过基甲：夹 0（v3.2 §6 甲 clamp≥0）
    public void Armor_reduction_clamps_at_zero()
    {
        Unit unit = NewUnit(maxHp: 100, armor: 3);
        unit.ApplyArmorReduction(9, untilTick: 90);
        Assert.Equal(0, unit.EffectiveArmorAt(10));
    }

    [Fact] // legal — 速度通道：光环+冲锋为正、减速为负，总减速封顶 −50%（SP-1a L29）
    public void Speed_channels_resolve_with_the_fifty_percent_slow_cap()
    {
        Unit unit = NewUnit(speed: 2.0f);
        unit.SetAuraSpeedBonus(15);
        unit.ResolveSpeedEffects(10);
        Assert.Equal(15, unit.ResolvedSpeedBonusPercent);
        Assert.Equal(2.0f * 1.15f, unit.EffectiveSpeedPxPerTick, 5);

        unit.ApplySlow(30, untilTick: 100);
        unit.ResolveSpeedEffects(50);
        Assert.Equal(-15, unit.ResolvedSpeedBonusPercent); // +15−30
        Assert.Equal(2.0f * 0.85f, unit.EffectiveSpeedPxPerTick, 5);

        unit.ApplySlow(90, untilTick: 100); // 强减速重取强者
        unit.ResolveSpeedEffects(60);
        Assert.Equal(-75, unit.ResolvedSpeedBonusPercent);
        Assert.Equal(2.0f * 0.5f, unit.EffectiveSpeedPxPerTick, 5); // 封顶 −50%

        unit.ResolveSpeedEffects(101); // 双双到期+光环仍在
        Assert.Equal(15, unit.ResolvedSpeedBonusPercent);
    }

    [Fact] // legal — 冲锋突发通道：BeginChargeBurst 计时窗内 +50%、窗外消失
    public void Charge_burst_channel_is_time_boxed()
    {
        Unit unit = NewUnit(speed: 1.0f);
        unit.BeginChargeBurst(untilTick: 690, speedBonusPercent: 50);
        unit.ResolveSpeedEffects(645);
        Assert.Equal(50, unit.ResolvedSpeedBonusPercent);
        Assert.Equal(1.5f, unit.EffectiveSpeedPxPerTick, 5);

        unit.ResolveSpeedEffects(690);
        Assert.Equal(0, unit.ResolvedSpeedBonusPercent);
    }

    [Theory] // illegal — 非法 slow/deb 调参确实被拒绝
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(101)]
    public void ApplySlow_rejects_out_of_band_percents(int percent) =>
        Assert.Throws<ArgumentException>(() => NewUnit().ApplySlow(percent, untilTick: 10));

    [Fact] // illegal — 非法甲减确实被拒绝
    public void ApplyArmorReduction_rejects_non_positive() =>
        Assert.Throws<ArgumentException>(() => NewUnit().ApplyArmorReduction(0, untilTick: 10));
}
