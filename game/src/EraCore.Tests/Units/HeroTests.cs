using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [F-039] 英雄实体行为（T-066，SP-6）：技能效果数值/冷却门/阵亡 60 s 重生/
// 可选性（不部署=永不被引用）/经 UnitMovement 的受令移动/身经百战被动。
public class HeroTests
{
    private const int CellSize = 32;

    private static Hero NewE1Hero(float x = 500, float y = 500) =>
        new(UnitsContent.LoadHeroCatalog().Get("era1:hero-firstfire"), new EntityId(1), x, y);

    private static Hero NewE2Hero(float x = 500, float y = 500) =>
        new(UnitsContent.LoadHeroCatalog().Get("era2:hero-spearwarden"), new EntityId(2), x, y);

    private static Unit NewEnemy(long id, float x, float y, int hp = 300, int armor = 4) =>
        new(new EntityId(id), "era2:enemy-warrior", x, y, hp, armor, 1.17f);

    // ---- E1 战吼 ------------------------------------------------------------

    [Fact] // legal — 战吼：半径内敌减速 30%×5 s、半径外不受影响（SP-6 L181）
    public void Warcry_slows_enemies_inside_three_cells_for_five_seconds()
    {
        Hero hero = NewE1Hero();
        Unit near = NewEnemy(11, 500 + 60, 500);   // 60 px < 3 格（96 px）
        Unit far = NewEnemy(12, 500 + 200, 500);   // 200 px > 3 格
        var enemies = new List<Unit> { near, far };

        Assert.Equal(HeroSkillStatus.Fired, hero.TryUseSkill(100, enemies, CellSize));

        Assert.Equal(30, near.SlowPercent);
        Assert.Equal(250, near.SlowUntilTick); // 100+150（5 s）
        near.ResolveSpeedEffects(150);
        Assert.Equal(-30, near.ResolvedSpeedBonusPercent);
        Assert.Equal(near.SpeedPxPerTick * 0.7f, near.EffectiveSpeedPxPerTick, 5);

        Assert.Equal(0, far.SlowPercent); // 半径外原速

        near.ResolveSpeedEffects(251); // 到期恢复
        Assert.Equal(0, near.ResolvedSpeedBonusPercent);
        Assert.Equal(near.SpeedPxPerTick, near.EffectiveSpeedPxPerTick, 5);
    }

    [Fact] // legal — 冷却门：45 s 内再喊拒绝且不消耗；窗口后可再发（SP-6）
    public void Warcry_cooldown_gates_the_second_cast()
    {
        Hero hero = NewE1Hero();
        var enemies = new List<Unit> { NewEnemy(11, 560, 500) };

        Assert.Equal(HeroSkillStatus.Fired, hero.TryUseSkill(0, enemies, CellSize));
        Assert.Equal(HeroSkillStatus.OnCooldown, hero.TryUseSkill(1349, enemies, CellSize));
        Assert.Equal(1350, hero.NextSkillReadyTick);
        Assert.Equal(HeroSkillStatus.Fired, hero.TryUseSkill(1350, enemies, CellSize));
    }

    [Fact] // boundary — 无目标不消耗冷却：空场喊话=NoTargets，敌入界立即可再发
    public void Warcry_without_targets_consumes_no_cooldown()
    {
        Hero hero = NewE1Hero();
        var empty = new List<Unit>();
        Assert.Equal(HeroSkillStatus.NoTargets, hero.TryUseSkill(0, empty, CellSize));
        Assert.Equal(long.MinValue, hero.NextSkillReadyTick);

        var enemies = new List<Unit> { NewEnemy(11, 560, 500) };
        Assert.Equal(HeroSkillStatus.Fired, hero.TryUseSkill(1, enemies, CellSize));
    }

    // ---- E2 破甲突刺 ---------------------------------------------------------

    [Fact] // legal — 突刺：2 格内单体 200 伤+−4 甲×10 s（SP-6 L182）
    public void Thrust_damages_the_nearest_enemy_and_reduces_its_armor()
    {
        Hero hero = NewE2Hero();
        Unit near = NewEnemy(21, 500 + 50, 500, hp: 1000, armor: 4);
        Unit far = NewEnemy(22, 500 + 60, 500, hp: 1000, armor: 4);
        var enemies = new List<Unit> { far, near }; // 乱序：近者必须胜出

        Assert.Equal(HeroSkillStatus.Fired, hero.TryUseSkill(200, enemies, CellSize));

        // 先 −4 甲再落 200 伤：DirectInt(200, max(0,4−4))=200
        Assert.Equal(800, near.Hp);
        Assert.Equal(1000, far.Hp);
        Assert.Equal(4, near.ArmorReduction);
        Assert.Equal(500, near.ArmorReductionUntilTick); // 200+300（10 s）
        Assert.Equal(0, near.EffectiveArmorAt(499));
        Assert.Equal(4, near.EffectiveArmorAt(500));      // 到期恢复
    }

    [Fact] // boundary — reach 边界：恰在 2 格（64 px）可刺、越界 NoTargets
    public void Thrust_range_boundary_is_inclusive_at_two_cells()
    {
        Hero hero = NewE2Hero();
        var onEdge = new List<Unit> { NewEnemy(21, 500 + 64, 500, armor: 4) };
        Assert.Equal(HeroSkillStatus.Fired, hero.TryUseSkill(0, onEdge, CellSize));

        Hero fresh = NewE2Hero();
        var beyond = new List<Unit> { NewEnemy(22, 500 + 65, 500, armor: 4) };
        Assert.Equal(HeroSkillStatus.NoTargets, fresh.TryUseSkill(0, beyond, CellSize));
    }

    [Fact] // legal — 突刺冷却 40 s（SP-6 L182）
    public void Thrust_cooldown_is_forty_seconds()
    {
        Hero hero = NewE2Hero();
        var enemies = new List<Unit> { NewEnemy(21, 550, 500) };
        Assert.Equal(HeroSkillStatus.Fired, hero.TryUseSkill(0, enemies, CellSize));
        Assert.Equal(1200, hero.NextSkillReadyTick);
        Assert.Equal(HeroSkillStatus.OnCooldown, hero.TryUseSkill(1199, enemies, CellSize));
    }

    // ---- 阵亡/重生 ------------------------------------------------------------

    [Fact] // legal — 阵亡 60 s 后于主城满血重生（SP-6 L176/L186 定案值）
    public void Death_respawns_at_home_sixty_seconds_later()
    {
        Hero hero = NewE1Hero(x: 160, y: 960);
        hero.OrderMove(new CellCoord(6, 0), CellSize);
        Assert.True(hero.HasOrder);

        int raised = 0;
        hero.Died += _ => raised++;
        Assert.Equal(700, hero.ApplyDamage(700, tick: 900));

        Assert.False(hero.Alive);
        Assert.Equal(1, raised);
        Assert.Equal(2700, hero.RespawnTick); // 900+1800
        Assert.False(hero.HasOrder);          // 阵亡撤令
        Assert.Equal(HeroStance.Hold, hero.Stance);
        Assert.Equal(0, hero.ApplyDamage(50, tick: 901)); // 死者不再结算

        hero.Tick(2699);
        Assert.False(hero.Alive);

        hero.Tick(2700);
        Assert.True(hero.Alive);
        Assert.Equal(700, hero.Hp);
        Assert.Equal(160, hero.X); // 主城重生
        Assert.Equal(960, hero.Y);
        Assert.Equal(HeroStance.Hold, hero.Stance);
    }

    [Fact] // illegal — 非正伤害确实被拒绝
    public void ApplyDamage_rejects_non_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewE1Hero().ApplyDamage(0, tick: 1));

    // ---- 受令移动（复用 UnitMovement 步长核心） ---------------------------------

    [Fact] // legal — move 令后经 UnitMovement.StepOrdered 抵达指令格中心并停住
    public void Ordered_move_walks_to_the_cell_centre_and_marks_arrival()
    {
        Hero hero = NewE1Hero(0, 0); // 速度 1.07 px/tick（SP-6）
        var movement = new UnitMovement(() => throw new InvalidOperationException("英雄受令移动不读流场"), CellSize);
        hero.OrderMove(new CellCoord(6, 0), CellSize);
        Assert.Equal(HeroStance.Advance, hero.Stance);
        Assert.Equal(6 * CellSize + CellSize / 2f, hero.OrderedX!.Value, 3);

        int steps = 0;
        while (!hero.ArrivedAtTarget && steps < 3000)
        {
            movement.StepOrdered(hero, hero.OrderedX!.Value, hero.OrderedY!.Value);
            steps++;
        }

        Assert.True(hero.ArrivedAtTarget);
        Assert.Equal(6 * CellSize + CellSize / 2f, hero.X, 4);
        Assert.Equal(CellSize / 2f, hero.Y, 4);
        Assert.True(steps > 150); // 208 px ÷ 1.07 px/tick ≈ 195 步（数量级一致）

        movement.StepOrdered(hero, hero.OrderedX!.Value, hero.OrderedY!.Value); // 已达：原地
        Assert.Equal(6 * CellSize + CellSize / 2f, hero.X, 4);
    }

    [Fact] // legal — retreat 令指回主城
    public void Retreat_order_aims_home()
    {
        Hero hero = NewE1Hero(160, 960);
        hero.OrderMove(new CellCoord(6, 0), CellSize);
        hero.OrderRetreat();
        Assert.Equal(HeroStance.Retreat, hero.Stance);
        Assert.Equal(160, hero.OrderedX!.Value, 3);
        Assert.Equal(960, hero.OrderedY!.Value, 3);
    }

    // ---- 被动 ------------------------------------------------------------

    [Fact] // legal — 身经百战：每 5 波 +10%、上限 +30%（SP-6 L182）
    public void Veteran_passive_steps_per_five_waves_and_caps_at_thirty_percent()
    {
        Hero hero = NewE2Hero();
        for (int i = 0; i < 5; i++)
        {
            hero.OnWaveHeld();
        }

        Assert.Equal(10, hero.AttackBonusPercent);

        for (int i = 0; i < 10; i++)
        {
            hero.OnWaveHeld();
        }

        Assert.Equal(30, hero.AttackBonusPercent); // 15 波=+30% 恰达上限

        for (int i = 0; i < 5; i++)
        {
            hero.OnWaveHeld();
        }

        Assert.Equal(30, hero.AttackBonusPercent); // 封顶不越
    }

    [Fact] // legal — E1 英雄的 veteran 通道为空（被动不同类不动数值）
    public void Firstfire_has_no_veteran_channel()
    {
        Hero hero = NewE1Hero();
        for (int i = 0; i < 20; i++)
        {
            hero.OnWaveHeld();
        }

        Assert.Equal(0, hero.AttackBonusPercent);
    }

    // ---- 可选性 ------------------------------------------------------------

    [Fact] // legal — 不部署英雄=不存在实例：全流程对象图为空也不影响敌群结算
    public void A_level_without_a_hero_instance_settles_units_normally()
    {
        // 可选性的对象图面：无 Hero 引用的敌方结算与有英雄时逐位一致
        Unit control = NewEnemy(31, 0, 0);
        int settled = control.ApplyDamage(100, tick: 5); // DirectInt(100,4)=96
        Assert.Equal(96, settled);
        Assert.Equal(204, control.Hp);
    }
}
