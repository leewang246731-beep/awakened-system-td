using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [C-10] [F-032] 护盾框架（T-066）：先吸收/破盾 5 s 后 20/s 再生/穿甲打断
// 3 s——表驱动手算（30 Hz 整数累加：每 tick +20 分子、每满 30 出 1 点，
// 任意 30-tick 窗口恰回 20 点）。
public class ShieldStateTests
{
    // ---- 手算基准 ----------------------------------------------------------
    // 破盾于 tick B → 恢复门 = B+150（5 s）。自门起第 n tick 累计回复
    // floor(2n/3) 点（20/s ÷ 30 Hz = 2/3 点每 tick）：n=1→0、n=2→1、
    // n=3→2、n=15→10、n=30→20、n=150→100（回满）。
    // 穿甲命中于 tick P → 阻断门 = P+90（3 s），与破盾门取 max。

    [Fact] // legal — 盾先于 HP 吸收：部分命中不落 HP、全额吸收返回实收
    public void Absorb_takes_raw_points_before_hp_and_reports_the_absorbed_amount()
    {
        var shield = new ShieldState(maxPoints: 100);
        var unit = new Unit(new(1), "era1:enemy-tribesman", 0, 0, maxHp: 100, armor: 6, 1.28f);
        unit.SetShield(100);

        int settled = unit.ApplyDamage(40, tick: 10);

        Assert.Equal(40, settled); // 全部由盾吸收
        Assert.Equal(60, unit.Shield);
        Assert.Equal(100, unit.Hp); // HP 分毫未动（护盾先于 HP 吸收）
    }

    [Fact] // legal — 溢出穿透：击破余盾后剩余伤害走甲公式入 HP
    public void Overflow_past_the_break_spills_into_hp_through_the_armor_formula()
    {
        var unit = new Unit(new(1), "era1:enemy-tribesman", 0, 0, maxHp: 300, armor: 6, 1.28f);
        unit.SetShield(30);

        int settled = unit.ApplyDamage(200, tick: 10);

        // 盾吸收 30；余 170 走 DirectInt(170, 甲 6)=164 → 共 194；盾归零
        Assert.Equal(30 + 164, settled);
        Assert.Equal(0, unit.Shield);
        Assert.Equal(300 - 164, unit.Hp);
        Assert.True(unit.Alive);
    }

    [Theory] // legal — 破盾再生时序表：门 B+150 前不回、之后按 floor(2n/3) 手算
    [InlineData(0, 0)]     // 门上第 1 tick：分子 20 → 0 点
    [InlineData(1, 0)]     // 门上第 0+1=1 tick 起算的对照行（同上）
    [InlineData(30, 20)]   // 1 s → 20 点（20/s 冻结值）
    [InlineData(45, 30)]   // 1.5 s → 30 点
    [InlineData(149, 99)]  // 4.97 s → floor(2×149/3)=99
    [InlineData(150, 100)] // 5 s → 100 点恰回满
    public void Broken_shield_regenerates_twenty_per_second_after_the_five_second_break(
        int ticksAfterGate,
        int expectedPoints)
    {
        // boundary 对照行（0,0）首行与次行同值：门前后都不多回
        var shield = new ShieldState(maxPoints: 100);
        long breakTick = 1000;
        Assert.Equal(100, shield.Absorb(100, pierce: false, tick: breakTick));

        // 门 B+150 前一步：仍为 0（5 s 打断窗口闭合）
        for (long t = breakTick + 1; t < breakTick + 150; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(0, shield.CurrentPoints);

        long gate = breakTick + 150;
        for (long t = gate; t < gate + ticksAfterGate; t++)
        {
            shield.Tick(t);
        }

        int clamped = Math.Min(100, expectedPoints);
        Assert.Equal(clamped, shield.CurrentPoints);
        if (ticksAfterGate == 0)
        {
            Assert.Equal(0, shield.CurrentPoints); // 门上首 tick 未出整点
        }
    }

    [Fact] // legal — 未破之盾的局部损伤持续再生（穿甲是唯一门）
    public void Partially_damaged_shield_keeps_regenerating_without_a_break()
    {
        var shield = new ShieldState(maxPoints: 100);
        Assert.Equal(40, shield.Absorb(40, pierce: false, tick: 0));
        Assert.Equal(60, shield.CurrentPoints);

        for (long t = 1; t <= 30; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(80, shield.CurrentPoints); // 60+20（1 s @20/s）
    }

    [Fact] // legal — 穿甲打断：未破之盾被穿甲命中后 3 s 内不回、门后照回
    public void Pierce_hit_interrupts_regeneration_of_an_unbroken_shield_for_three_seconds()
    {
        var shield = new ShieldState(maxPoints: 100);
        Assert.Equal(50, shield.Absorb(50, pierce: true, tick: 500)); // 穿甲命中
        Assert.Equal(50, shield.CurrentPoints);

        for (long t = 501; t <= 589; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(50, shield.CurrentPoints); // 3 s（90 ticks）窗口内零回复

        for (long t = 590; t <= 619; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(70, shield.CurrentPoints); // 门后 1 s 回 20
    }

    [Fact] // legal — 破盾+穿甲双门取 max：破盾门 1150 被穿甲门 1190 推后
    public void Pierce_on_a_broken_shield_pushes_the_resume_gate_to_the_later_stamp()
    {
        var shield = new ShieldState(maxPoints: 100);
        Assert.Equal(100, shield.Absorb(100, pierce: false, tick: 1000)); // 破盾
        Assert.Equal(0, shield.Absorb(50, pierce: true, tick: 1100));     // 打在破盾残壳上仍刷新打断门

        for (long t = 1001; t <= 1189; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(0, shield.CurrentPoints); // max(1000+150, 1100+90)=1190 之前不回

        for (long t = 1190; t <= 1219; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(20, shield.CurrentPoints); // 门后 1 s 恰 20 点
    }

    [Fact] // boundary — 满盾不积累分子：回满后再受伤从零起重算
    public void Full_shield_banks_no_numerator_for_later()
    {
        var shield = new ShieldState(maxPoints: 50);
        for (long t = 0; t < 30; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(50, shield.CurrentPoints); // 满盾

        for (long t = 30; t < 90; t++)
        {
            shield.Tick(t); // 满盾期 2 s：分子若被积累应在此后爆发
        }

        Assert.Equal(20, shield.Absorb(20, pierce: false, tick: 90));
        shield.Tick(91); // 1 tick：分子 20 → 0 点（无银行余粮）
        Assert.Equal(30, shield.CurrentPoints);
    }

    [Fact] // boundary — 非穿甲命中不打断：破盾门只认 5 s
    public void Non_pierce_hits_do_not_extend_any_gate()
    {
        var shield = new ShieldState(maxPoints: 100);
        Assert.Equal(100, shield.Absorb(100, pierce: false, tick: 1000));
        Assert.Equal(0, shield.Absorb(80, pierce: false, tick: 1100)); // 打在残壳上：无吸收、无门

        for (long t = 1001; t <= 1149; t++)
        {
            shield.Tick(t);
        }

        Assert.Equal(0, shield.CurrentPoints);
        shield.Tick(1150);
        shield.Tick(1151);
        shield.Tick(1152);
        Assert.Equal(2, shield.CurrentPoints); // 门后 3 tick 恰 2 点（未被打断）
    }

    [Fact] // legal — 吸收总账（T-114 观测位）：初始充能+再生重吸收逐点入账、纯观察不改行为
    public void AbsorbedTotal_banks_every_absorbed_point_including_reabsorbed_regen()
    {
        var shield = new ShieldState(maxPoints: 100);
        Assert.Equal(40, shield.Absorb(40, pierce: false, tick: 0)); // 返回=实吸收
        Assert.Equal(60, shield.CurrentPoints);
        Assert.Equal(40, shield.AbsorbedTotal);

        for (long t = 1; t <= 45; t++)
        {
            shield.Tick(t); // 未破之盾持续 20/s：45 ticks → +30 点
        }

        Assert.Equal(90, shield.CurrentPoints);
        Assert.Equal(90, shield.Absorb(90, pierce: false, tick: 46)); // 全额吸收并破盾
        Assert.Equal(130, shield.AbsorbedTotal); // 40+90：再生回补的点再次入账

        for (long t = 47; t <= 246; t++)
        {
            shield.Tick(t); // 破盾门 46+150=196 起至 246 共 51 ticks → floor(51×2/3)=34 点
        }

        Assert.Equal(34, shield.CurrentPoints);
        Assert.Equal(30, shield.Absorb(30, pierce: true, tick: 250)); // 穿甲命中：只吸收不计门
        Assert.Equal(160, shield.AbsorbedTotal); // 130+30：第三轮重吸收
        Assert.Equal(4, shield.CurrentPoints);
    }

    [Theory] // illegal — 非法构造与非正吸收确实被拒绝
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_non_positive_max_points(int maxPoints) =>
        Assert.Throws<ArgumentException>(() => new ShieldState(maxPoints));

    [Fact] // illegal — 非正吸收确实被拒绝
    public void Absorb_rejects_non_positive_incoming() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShieldState(100).Absorb(0, pierce: false, tick: 1));

    [Fact] // legal — Unit 侧门：TickShield 无盾 no-op、SetShield(0) 摘除
    public void Unit_without_a_shield_is_a_no_op_and_zero_removes_it()
    {
        var unit = new Unit(new(1), "era1:enemy-tribesman", 0, 0, 100, 0, 1.28f);
        unit.TickShield(5); // 无盾：不抛
        Assert.Equal(0, unit.Shield);

        unit.SetShield(40);
        Assert.Equal(40, unit.Shield);
        unit.SetShield(0);
        Assert.Equal(0, unit.Shield);
        Assert.Equal(30, unit.ApplyDamage(30, tick: 6)); // 无盾：全额直落 HP
        Assert.Equal(70, unit.Hp);
    }

    [Fact] // illegal — SetShield 负值确实被拒绝
    public void SetShield_rejects_negative_points() =>
        Assert.Throws<ArgumentException>(() =>
            new Unit(new(1), "era1:enemy-tribesman", 0, 0, 100, 0, 1.28f).SetShield(-1));
}
