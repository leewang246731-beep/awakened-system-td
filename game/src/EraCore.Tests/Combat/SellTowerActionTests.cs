using EraCore.Agent.Actions;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Tests.Agent.Actions;
using Xunit;
using static EraCore.Tests.Combat.Era2EstateFixture;

namespace EraCore.Tests.Combat;

// [F-014] [C-03] T-065 SellTower 动作注册表落地。验收锚：confirm_sell 缺省
// 拒绝（F-014——缺字段/假值一律 ErrSchemaDenied，v3.2 §11.4「永不自动允许」）
// + true 时拆除返还 30% + 残骸记录留痕（Checkpoint FailureTiers 语义薄版）。
public class SellTowerActionTests
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

    // ------------------------------------------------ confirm_sell 危险位矩阵

    [Theory] // illegal — 缺字段 / 显式 false / 类型错 → ErrSchemaDenied（缺省拒绝）
    [InlineData(null)]
    [InlineData("false")]
    public void Missing_or_false_danger_bit_is_schema_denied(string? confirmSell)
    {
        // [F-014] v3.2 §11.4/F.4：confirm_sell:true 显式危险位，永不自动允许
        var (registry, fixture, _) = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        ActionOutcome outcome = registry.Invoke(EstateActionJson.SellTower(tower.Value, confirmSell));

        Assert.Equal(ActionResultStatus.ErrSchemaDenied, outcome.Result.Status);
        Assert.Contains("confirm_sell", outcome.Result.ErrorMessage);
        Assert.True(fixture.Buildings.TryGetById(tower, out var kept)); // 危险位拒绝路径零突变
        Assert.Equal("era2:tower-arrow", kept.ContentId);
        Assert.Empty(fixture.Estate.Ruins);
    }

    [Fact] // legal — 显式 true → 拆除返还 30% + 残骸记录
    public void Explicit_true_sells_refunds_30_percent_and_records_the_ruin()
    {
        var (registry, fixture, sink) = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Crossbow, Cell(4, 4));
        double before = fixture.Stock.AmountOf(Currency);

        ActionOutcome outcome = registry.Invoke(EstateActionJson.SellTower(tower.Value, "true"));

        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        var receipt = Assert.IsType<SellTowerReceipt>(outcome.Payload);
        Assert.Equal(81.0, receipt.Refunded, 9); // 270×30%（§11.4）
        Assert.Equal(before + 81.0, fixture.Stock.AmountOf(Currency), 9);
        Assert.False(fixture.Buildings.TryGetById(tower, out _)); // 塔已拆
        Assert.Equal(Cell(4, 4), Assert.Single(fixture.Estate.Ruins).Cell); // 废墟留痕于原格
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("verdict=ok")
            && e.Message.Contains("SellTower"));
    }

    [Fact] // illegal — true 但目标未知 → ErrTargetInvalid
    public void True_with_unknown_target_is_err_target_invalid()
    {
        var (registry, _, _) = Create();

        ActionOutcome outcome = registry.Invoke(EstateActionJson.SellTower(31337, "true"));

        Assert.Equal(ActionResultStatus.ErrTargetInvalid, outcome.Result.Status);
        Assert.Contains("target-unknown:31337", outcome.Result.ErrorMessage);
        Assert.Null(outcome.Payload);
    }

    [Fact] // boundary — 未接 TowerEstate：not-implemented:SellTower 诚实拒绝（前缀延续）
    public void Registry_without_an_estate_refuses_sell_honestly()
    {
        var sink = new CapturingLogSink();
        var registry = new ActionRegistry(sink, eraOrdinalProvider: () => EraOrdinals.E2);

        ActionOutcome outcome = registry.Invoke(EstateActionJson.SellTower(5, "true"));

        Assert.Equal(ActionResultStatus.Err, outcome.Result.Status);
        Assert.Contains("not-implemented:SellTower", outcome.Result.ErrorMessage);
        Assert.Contains("tower-estate", outcome.Result.ErrorMessage);
    }

    [Fact] // boundary — E1 落解锁时代门（SellTower=E2 动词）
    public void Era1_calls_hit_the_unlock_gate_before_the_handler()
    {
        var (registry, fixture, _) = Create(era: EraOrdinals.E1);
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        ActionOutcome outcome = registry.Invoke(EstateActionJson.SellTower(tower.Value, "true"));

        Assert.Equal(ActionResultStatus.ErrCapabilityLocked, outcome.Result.Status);
        Assert.True(fixture.Buildings.TryGetById(tower, out var kept)); // 门拒绝路径零突变
        Assert.Empty(fixture.Estate.Ruins);
    }

    // ------------------------------------------------ 端到端：升级后出售

    [Fact] // legal — 升级过的塔按【当前行】计返还（弩塔 270×30%=81，非箭楼 90×30%=27）
    public void Selling_an_upgraded_tower_refunds_on_the_current_row()
    {
        var (registry, fixture, _) = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));
        Assert.Equal(ActionResultStatus.Ok,
            registry.Invoke(EstateActionJson.UpgradeTower(tower.Value, toTier: 2)).Result.Status);
        double afterUpgrade = fixture.Stock.AmountOf(Currency);

        ActionOutcome outcome = registry.Invoke(EstateActionJson.SellTower(tower.Value, "true"));

        var receipt = Assert.IsType<SellTowerReceipt>(outcome.Payload);
        Assert.Equal(81.0, receipt.Refunded, 9);
        Assert.Equal(afterUpgrade + 81.0, fixture.Stock.AmountOf(Currency), 9);

        // 残骸账按当前行（弩塔 270）计修复账单（FailureTiers 30% 单源）
        TowerRuin ruin = Assert.Single(fixture.Estate.Ruins);
        Assert.Equal(270.0, ruin.OriginalCostPoints, 9);
        Assert.Equal(EraCore.Checkpoint.FailureTiers.RepairCostFraction * 270.0, ruin.RepairCostPoints, 9);
    }
}
