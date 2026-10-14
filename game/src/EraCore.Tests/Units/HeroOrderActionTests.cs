using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Tests.Agent.Actions;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// [F-039] [K-3] HeroOrder 三动词真实现（T-066）：注册表第 4 个真 handler
// （move/skill/retreat 从 not-implemented 档变真）——E2 可调、E1 落时代门、
// 未部署英雄显式拒绝（可选性）、冷却/无目标拒绝、schema 关闭词表拒绝。
public class HeroOrderActionTests
{
    private const int CellSize = 32;

    private static HeroOrderAction NewHandler(Hero? hero, IReadOnlyList<Unit>? enemies = null, long tick = 0) =>
        new(
            hero: () => hero,
            advanceCell: () => new CellCoord(6, 0),
            enemies: () => enemies ?? Array.Empty<Unit>(),
            cellSizePx: CellSize)
        {
            TickSource = () => tick,
        };

    private static ActionRegistry NewRegistry(HeroOrderAction handler, int era = EraOrdinals.E2)
    {
        CapturingLogSink sink = new();
        return new ActionRegistry(sink, worldView: null, eraOrdinalProvider: () => era, heroOrder: handler);
    }

    private static ActionRegistry NewRegistryWithoutHandler(int era = EraOrdinals.E2, CapturingLogSink? sink = null)
    {
        sink ??= new CapturingLogSink();
        return new ActionRegistry(sink, worldView: null, eraOrdinalProvider: () => era);
    }

    private static Hero NewE2Hero(float x = 500, float y = 500) =>
        new(UnitsContent.LoadHeroCatalog().Get("era2:hero-spearwarden"), new EntityId(7), x, y);

    private static Hero NewE1Hero(float x = 500, float y = 500) =>
        new(UnitsContent.LoadHeroCatalog().Get("era1:hero-firstfire"), new EntityId(8), x, y);

    private static List<Unit> OneEnemyNearHero(Hero hero) =>
        new() { new Unit(new EntityId(21), "era2:enemy-warrior", hero.X + 50, hero.Y, 300, 4, 1.17f) };

    // ---- 注册表路径：三动词 Ok -----------------------------------------------

    [Fact] // legal — move 走注册表 Ok：载荷=受令确认（stance/cell）
    public void Move_order_returns_ok_with_an_advance_ack()
    {
        Hero hero = NewE2Hero();
        ActionOutcome outcome = NewRegistry(NewHandler(hero)).Invoke(ActionJsonSamples.HeroOrder());

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var ack = Assert.IsType<HeroOrderAck>(outcome.Payload);
        Assert.Equal("move", ack.Order);
        Assert.Equal(HeroStance.Advance, ack.Stance);
        Assert.Null(ack.SkillStatus);
        Assert.Equal(6 * CellSize + CellSize / 2f, hero.OrderedX!.Value, 3);
    }

    [Fact] // legal — retreat 走注册表 Ok
    public void Retreat_order_returns_ok_and_aims_home()
    {
        Hero hero = NewE2Hero(x: 999);
        ActionOutcome outcome = NewRegistry(NewHandler(hero)).Invoke(HeroOrderJson.Retreat());

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.Equal(HeroStance.Retreat, ((HeroOrderAck)outcome.Payload!).Stance);
        Assert.Equal(999, hero.OrderedX!.Value, 3);
    }

    [Fact] // legal — skill 走注册表 Ok：命中近敌、ack 带技能结算态
    public void Skill_order_returns_ok_with_the_settlement_status()
    {
        Hero hero = NewE1Hero();
        var enemies = OneEnemyNearHero(hero);
        ActionOutcome outcome = NewRegistry(NewHandler(hero, enemies)).Invoke(HeroOrderJson.Skill());

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var ack = Assert.IsType<HeroOrderAck>(outcome.Payload);
        Assert.Equal(HeroSkillStatus.Fired, ack.SkillStatus);
        Assert.Equal(30, enemies[0].SlowPercent); // 战吼落敌（SP-6 L181）
    }

    // ---- 注册表路径：拒绝 -------------------------------------------------

    [Fact] // boundary — E1 落时代门：HeroOrder 解锁于 E2（G.2 行）
    public void HeroOrder_in_era1_is_capability_locked()
    {
        var sink = new CapturingLogSink();
        ActionRegistry registry = new(
            sink, worldView: null, eraOrdinalProvider: () => EraOrdinals.E1, heroOrder: NewHandler(NewE2Hero()));

        ActionOutcome outcome = registry.Invoke(ActionJsonSamples.HeroOrder());
        Assert.Equal(ActionResultStatus.ErrCapabilityLocked, outcome.Result.Status);
        Assert.Contains("capability-locked:HeroOrder", outcome.Result.ErrorMessage);
    }

    [Fact] // legal — 未部署英雄（可选性 F-039）：显式拒绝、绝不假成功、不崩溃
    public void Order_without_a_deployed_hero_refuses_explicitly()
    {
        ActionOutcome outcome = NewRegistry(NewHandler(hero: null)).Invoke(ActionJsonSamples.HeroOrder());

        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("hero-not-deployed", outcome.Result.ErrorMessage);
        Assert.Null(outcome.Payload);
    }

    [Fact] // boundary — 未接 handler 的注册表：显式拒绝（不猜测战局）
    public void HeroOrder_without_a_wired_handler_refuses()
    {
        ActionOutcome outcome = NewRegistryWithoutHandler().Invoke(ActionJsonSamples.HeroOrder());

        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("world-view-missing", outcome.Result.ErrorMessage);
    }

    [Fact] // legal — 冷却期 skill 拒绝且给出结构化理由（不消耗）
    public void Skill_order_on_cooldown_refuses_with_a_structured_reason()
    {
        Hero hero = NewE1Hero();
        var enemies = OneEnemyNearHero(hero);
        HeroOrderAction handler = NewHandler(hero, enemies);
        ActionRegistry registry = NewRegistry(handler);

        Assert.Equal(ActionResultStatus.Ok, registry.Invoke(HeroOrderJson.Skill()).Result.Status);
        ActionOutcome second = registry.Invoke(HeroOrderJson.Skill());
        Assert.Equal(ActionResultStatus.Err, second.Result.Status);
        Assert.Contains("skill-not-fired:OnCooldown", second.Result.ErrorMessage);
    }

    [Fact] // boundary — 词表外 order 值在 schema 关闭门被拒（ErrSchemaDenied）
    public void Unknown_order_value_is_denied_by_the_closed_schema()
    {
        ActionOutcome outcome = NewRegistry(NewHandler(NewE2Hero())).Invoke(HeroOrderJson.UnknownOrder());

        Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.Contains("order", outcome.Result.ErrorMessage);
    }

    // ---- 直调路径 ---------------------------------------------------------

    [Fact] // legal — 直调三动词与注册表同语义（同一条执行路径）
    public void Direct_invocation_matches_the_registry_semantics()
    {
        Hero hero = NewE2Hero();
        HeroOrderAction handler = NewHandler(hero, OneEnemyNearHero(hero), tick: 40);

        Assert.True(handler.Invoke(HeroOrderJson.Retreat()).Ok);
        Assert.Equal(HeroStance.Retreat, hero.Stance);

        Assert.True(handler.Invoke(HeroOrderJson.Skill()).Ok);
        Assert.True(handler.Invoke(HeroOrderJson.Move()).Ok);
        Assert.Equal(HeroStance.Advance, hero.Stance);
    }

    [Fact] // illegal — 非 HeroOrder 载荷/空白载荷确实被拒绝
    public void Direct_invocation_rejects_foreign_payloads()
    {
        HeroOrderAction handler = NewHandler(NewE2Hero());
        Assert.Throws<ArgumentException>(() => handler.Invoke(LookAroundJsonStub()));
        Assert.Throws<ArgumentException>(() => handler.Invoke("  "));
    }

    [Fact] // illegal — 缺 order 字段确实被拒绝
    public void Direct_invocation_refuses_a_missing_order_field()
    {
        HeroOrderResult result = NewHandler(NewE2Hero()).Invoke(HeroOrderJson.NoOrderField());
        Assert.False(result.Ok);
        Assert.Contains("order-missing", result.Reason);
    }

    private static string LookAroundJsonStub() => ActionJsonSamples.LookAround();
}

/// <summary>HeroOrder 载荷构造（方法体外构造字面 JSON——T4 纪律）。</summary>
internal static class HeroOrderJson
{
    public static string Move() => ActionJsonSamples.HeroOrder(); // 同一构造源（move 样例）

    public static string Retreat() => "{\"action\":\"HeroOrder\",\"order\":\"retreat\"}";

    public static string Skill() => "{\"action\":\"HeroOrder\",\"order\":\"skill\"}";

    public static string UnknownOrder() => "{\"action\":\"HeroOrder\",\"order\":\"dance\"}";

    public static string NoOrderField() => "{\"action\":\"HeroOrder\"}";
}
