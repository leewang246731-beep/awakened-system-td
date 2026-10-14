using EraCore.Agent.Actions;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Tests.Agent.Actions;
using Xunit;
using static EraCore.Tests.Combat.Era2EstateFixture;

namespace EraCore.Tests.Combat;

// [F-014] [C-03] T-065 UpgradeTower 动作注册表落地：K-3 唯一门（闭表
// Schema→解锁时代门→handler）下真实执行；含集成实测——升级在位后武器循环
// DPS 翻倍（30 Hz tick 实测，非数值断言直读）。
public class UpgradeTowerActionTests
{
    private static (ActionRegistry Registry, Era2EstateFixture Fixture, CapturingLogSink Sink) Create(int era = EraOrdinals.E2)
    {
        var fixture = Era2EstateFixture.Create();
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(
            sink, worldView: null, eraOrdinalProvider: () => era, towerEstate: fixture.Estate);
        return (registry, fixture, sink);
    }

    private static CellCoord Cell(int x, int y) => new(x, y);

    [Fact] // legal — E2 注册表调用 UpgradeTower → Ok + 结算回执（差价/返还逐值）
    public void Registry_executes_the_upgrade_and_returns_the_receipt()
    {
        var (registry, fixture, sink) = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        ActionOutcome outcome = registry.Invoke(EstateActionJson.UpgradeTower(tower.Value, toTier: 2));

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var receipt = Assert.IsType<UpgradeTowerReceipt>(outcome.Payload);
        Assert.Equal(180.0, receipt.Charged, 9);  // 差价 270−90
        Assert.Equal(45.0, receipt.Refunded, 9);  // 旧塔 50% 返还
        Assert.Equal(135.0, receipt.NetCost, 9);
        Assert.True(fixture.Buildings.TryGetById(tower, out var upgraded));
        Assert.Equal("era2:tower-crossbow", upgraded.ContentId);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("verdict=ok")
            && e.Message.Contains("UpgradeTower"));
    }

    [Fact] // integration — 升级在位 → 武器循环 DPS 翻倍实测（10 s 弹道累计）
    public void In_place_upgrade_doubles_measured_weapon_dps()
    {
        // [F-014] [F-027] 升级把行换成弩塔后，同一格新行上的武器循环 10 s
        // （300 ticks）输出按真 WeaponFire 循环手算落钉：箭楼 reload 15 →
        // 发射于 tick 0,15,…,285 共 20 发×15 伤=300；弩塔 reload 9 →
        // 0,9,…,297 共 34 发×18 伤=612。比值 612/300=2.04 落在 DPS ×2/级
        // ±15% 律带内（窗口边界多出的首发使比值略高于 2——带口径承接）。
        var (registry, fixture, _) = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        double before = MeasureDamage(fixture, tower);
        Assert.Equal(300.0, before, 6); // 20 发 × 15（30 DPS 行）

        ActionOutcome outcome = registry.Invoke(EstateActionJson.UpgradeTower(tower.Value, toTier: 2));
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);

        double after = MeasureDamage(fixture, tower);
        Assert.Equal(612.0, after, 6); // 34 发 × 18（60 DPS 行）
        Assert.InRange(after / before, 2.0 * 0.85, 2.0 * 1.15); // ×2/级律带实测
    }

    [Fact] // illegal — 未知目标 → ErrTargetInvalid + Warn 日志
    public void Unknown_tower_target_maps_to_err_target_invalid()
    {
        var (registry, _, sink) = Create();

        ActionOutcome outcome = registry.Invoke(EstateActionJson.UpgradeTower(777, toTier: 2));

        Assert.Equal(ActionResultStatus.ErrTargetInvalid, outcome.Result.Status);
        Assert.Null(outcome.Payload);
        Assert.Contains("target-unknown:777", outcome.Result.ErrorMessage);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("UpgradeTower"));
    }

    [Theory] // illegal — 律外/状态外拒绝（跳级/线尾/差价不足）
    [InlineData(1, "to-tier-must-be-next")]
    [InlineData(3, "to-tier-must-be-next")]
    [InlineData(4, "to-tier-must-be-next")]
    public void Illegal_tier_requests_refuse_with_the_estate_code(int tier, string expected)
    {
        var (registry, fixture, _) = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        ActionOutcome outcome = registry.Invoke(EstateActionJson.UpgradeTower(tower.Value, tier));

        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains(expected, outcome.Result.ErrorMessage);
    }

    [Fact] // boundary — 未接 TowerEstate 的注册表：诚实 not-implemented 拒绝（不假成功）
    public void Registry_without_an_estate_refuses_upgrade_honestly()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(sink, eraOrdinalProvider: () => EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(EstateActionJson.UpgradeTower(5, toTier: 2));

        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("not-implemented:UpgradeTower", outcome.Result.ErrorMessage);
        Assert.Contains("tower-estate", outcome.Result.ErrorMessage);
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("verdict=refused"));
    }

    [Fact] // boundary — E1 调用落解锁时代门（UpgradeTower=E2 动词，G.2 列）
    public void Era1_calls_hit_the_unlock_gate_before_the_handler()
    {
        var (registry, fixture, _) = Create(era: EraOrdinals.E1);
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        ActionOutcome outcome = registry.Invoke(EstateActionJson.UpgradeTower(tower.Value, toTier: 2));

        Assert.Equal(ActionResultStatus.ErrCapabilityLocked, outcome.Result.Status);
        Assert.Contains("UpgradeTower", outcome.Result.ErrorMessage);
        Assert.True(fixture.Buildings.TryGetById(tower, out var unchanged)); // 拒绝路径零突变
        Assert.Equal("era2:tower-arrow", unchanged.ContentId);
    }

    [Theory] // illegal — schema 越界先于 handler（to_tier 1~4 冻结域）
    [InlineData(0)]
    [InlineData(5)]
    public void Schema_violations_are_denied_before_the_handler(int tier)
    {
        var (registry, fixture, _) = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        ActionOutcome outcome = registry.Invoke(EstateActionJson.UpgradeTower(tower.Value, tier));

        Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.Contains("/to_tier", outcome.Result.ErrorMessage);
    }

    /// <summary>
    /// 真 WeaponFire 循环 300 ticks（10 s）的伤害累计：以当前行建循环、
    /// 冷却即射（无目标约束的速率上限口径），伤害=每发 d 的和。
    /// </summary>
    private static double MeasureDamage(Era2EstateFixture fixture, EntityId tower)
    {
        Assert.True(fixture.Buildings.TryGetById(tower, out var building));
        var spec = Era2Content.Tower(building.ContentId);
        var weapon = new WeaponFire(tower, spec, initialAmmoShots: 1_000_000);
        var target = new EntityId(900_000);
        double total = 0;
        for (int tick = 0; tick < 300; tick++)
        {
            WeaponFireEvent? shot = weapon.TryFire(target, tick);
            if (shot is not null)
            {
                total += shot.DamagePerShot;
            }

            weapon.Tick();
        }

        return total;
    }
}
