using EraCore.Checkpoint;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using Xunit;
using static EraCore.Tests.Combat.Era2EstateFixture;

namespace EraCore.Tests.Combat;

// [F-014] [C-09] T-065 塔产执行核：在位升级（差价+旧塔 50% 返还+同格替换）
// 与出售（30% 返还+残骸账目）在真 BuildingRegistry/ResourceStock 上落地；
// 数值全部自 era2 内容表（RL7），50%/30% 为引擎侧经济常量（[NEW]/§11.4）。
public class TowerEstateOpsTests
{
    private static CellCoord Cell(int x, int y) => new(x, y);

    // ------------------------------------------------------------ upgrade

    [Fact] // legal — 差价 180 + 旧塔 50% 返还 45（[NEW] 经济），同格同 id 替换
    public void Upgrade_charges_the_differential_and_refunds_half_the_old_row()
    {
        var fixture = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));
        double afterPlace = fixture.Stock.AmountOf(Currency); // 5000−90

        var receipt = fixture.Estate.ApplyUpgrade(tower, toTier: 2, out string? refusal);

        Assert.NotNull(receipt);
        Assert.Null(refusal);
        // 箭楼 90 → 弩塔 270（F.5）：差价=270−90=180、旧塔返还=90×50%=45
        Assert.Equal(180.0, receipt!.Charged, 9);
        Assert.Equal(45.0, receipt.Refunded, 9);
        Assert.Equal(135.0, receipt.NetCost, 9);
        Assert.Equal(afterPlace - 180.0 + 45.0, fixture.Stock.AmountOf(Currency), 9);

        // 同格替换：同一实体 id、同一格，行换成弩塔 T2
        Assert.True(fixture.Buildings.TryGetById(tower, out var building));
        Assert.Equal("era2:tower-crossbow", building.ContentId);
        Assert.Equal(2, building.Tier);
        Assert.Equal(Cell(5, 5), building.Cell);
        Assert.True(fixture.Buildings.TryGetAt(Cell(5, 5), out var atCell));
        Assert.Equal(tower, atCell.Id);
    }

    [Fact] // legal — 升级优于卖旧重建（50% 返还 > 30% 出售返还，任务卡经济取向）
    public void Upgrade_refund_beats_the_sell_refund_rate()
    {
        // [F-014] 50% > 30%：同塔两条路径的净成本——升级 135 vs 卖+重建 243
        Assert.True(TowerEstateOps.UpgradeRefundFraction > TowerEstateOps.SellRefundFraction);

        var fixture = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));
        double baseline = fixture.Stock.AmountOf(Currency);

        var receipt = fixture.Estate.ApplyUpgrade(tower, toTier: 2, out _);
        Assert.NotNull(receipt);
        double upgradeNet = baseline - fixture.Stock.AmountOf(Currency);
        Assert.Equal(135.0, upgradeNet, 9);

        // 卖旧（90×30%=27）+ 全价重建 270 → 净 243 > 135
        double sellRebuildNet = 270.0 - 0.3 * 90.0;
        Assert.True(upgradeNet < sellRebuildNet, $"升级净成本 {upgradeNet} 应低于卖旧重建 {sellRebuildNet}");
    }

    [Fact] // legal — 全链三步：箭楼→弩塔→连弩→火油，成本阶梯 90→270→810→2430
    public void Full_line_upgrade_walks_the_F5_ladder()
    {
        var fixture = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(6, 6));
        double start = fixture.Stock.AmountOf(Currency);
        double net = 0;

        foreach (int tier in new[] { 2, 3, 4 })
        {
            var receipt = fixture.Estate.ApplyUpgrade(tower, tier, out string? refusal);
            Assert.Null(refusal);
            Assert.NotNull(receipt);
            net += receipt!.NetCost;
        }

        // 三步「差价−旧塔返还」净额：(180−45)+(540−135)+(1620−405)=135+405+1215
        Assert.Equal(135.0 + 405.0 + 1215.0, net, 9);
        Assert.Equal(start - net, fixture.Stock.AmountOf(Currency), 9);
        Assert.True(fixture.Buildings.TryGetById(tower, out var building));
        Assert.Equal("era2:tower-fire-oil", building.ContentId);
        Assert.Equal(4, building.Tier);
    }

    [Fact] // boundary — to_tier 只能是当前+1；线尾无后继；特化线无线
    public void Tier_requests_outside_the_next_step_are_refused()
    {
        var fixture = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));

        // 同级/跳级/降级均拒绝
        Assert.False(fixture.Estate.TryQuoteUpgrade(tower, 1, out _, out string? sameTier));
        Assert.Contains("to-tier-must-be-next", sameTier);
        Assert.False(fixture.Estate.TryQuoteUpgrade(tower, 3, out _, out string? skip));
        Assert.Contains("to-tier-must-be-next", skip);

        // 线尾（火油 T4）无后继行
        EntityId fireOil = fixture.Estate.Place(Era2Content.FireOil, Cell(7, 7));
        Assert.False(fixture.Estate.TryQuoteUpgrade(fireOil, 4, out _, out string? atEnd));
        Assert.Contains("no-successor", atEnd);

        // 特化线（弩炮 max_tier=3，F.5 无 T4 行）同样无线
        EntityId onager = fixture.Estate.Place(Era2Content.Onager, Cell(8, 8));
        Assert.False(fixture.Estate.TryQuoteUpgrade(onager, 4, out _, out string? special));
        Assert.Contains("no-successor", special);
    }

    [Fact] // illegal — 未知目标 / 弹药不足：拒绝且零突变
    public void Unknown_targets_and_short_funds_refuse_without_mutation()
    {
        var fixture = Create();
        Assert.False(fixture.Estate.TryQuoteUpgrade(new EntityId(9999), 2, out _, out string? unknown));
        Assert.StartsWith("target-unknown:9999", unknown);

        // 恰好买得起箭楼后的余量不够差价 180 → 拒绝且塔与池原样
        var tight = Create(seedStock: 90.0);
        EntityId tower = tight.Estate.Place(Era2Content.Arrow, Cell(5, 5));
        double stockBefore = tight.Stock.AmountOf(Currency);
        Assert.False(tight.Estate.TryQuoteUpgrade(tower, 2, out _, out string? funds));
        Assert.StartsWith("insufficient-funds", funds);
        Assert.True(tight.Buildings.TryGetById(tower, out var unchanged));
        Assert.Equal("era2:tower-arrow", unchanged.ContentId);
        Assert.Equal(stockBefore, tight.Stock.AmountOf(Currency), 9);
    }

    [Fact] // illegal — 后继行破律（成本非 ×3）→ growth-law 拒绝（律校验负向）
    public void Broken_successor_rows_are_refused_by_the_growth_law()
    {
        // [F-027] 合成内容走同一 JSON 管线（RL7）：270 破坏 ×3 严格梯（90×3=270？
        // 用 280 破坏）→ 拒绝升级，不放行不修补
        var broken = Era2Content.Load(Era2Content.PairLineJson(
            "pierce", "pierce", dpsLow: 30, dpsHigh: 60, costLow: 90, costHigh: 280,
            rangeLow: 6.5, rangeHigh: 8.0));
        var world = new EraCore.World.GridWorld(12, 12, new EraCore.Tests.Sim.SimRng(7), 0, 0, 0);
        var buildings = new BuildingRegistry(world);
        var stock = new EraCore.Economy.ResourceStock(new EraCore.Sim.SimClock(), 2);
        _ = stock.Deposit(Currency, 5000);
        var estate = new TowerEstateOps(buildings, broken, stock, Currency, FailureTiers.RepairCostFraction);
        EntityId tower = estate.Place(broken[0], Cell(3, 3));

        Assert.False(estate.TryQuoteUpgrade(tower, 2, out _, out string? refusal));
        Assert.StartsWith("growth-law:", refusal);
        Assert.Contains("cost_step", refusal); // 90×3=270≠280 命名违例
        Assert.True(buildings.TryGetById(tower, out var stillOld));
        Assert.Equal("era2:tower-pair-low", stillOld.ContentId);
    }

    // ------------------------------------------------------------ sell

    [Fact] // legal — 出售返还 30%（v3.2 §11.4）+ 残骸账目接 FailureTiers 语义
    public void Sell_refunds_30_percent_and_leaves_a_ruin_with_the_repair_bill()
    {
        var fixture = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Crossbow, Cell(4, 4));
        double before = fixture.Stock.AmountOf(Currency);

        var receipt = fixture.Estate.ApplySell(tower, out string? refusal);

        Assert.NotNull(receipt);
        Assert.Null(refusal);
        Assert.Equal(81.0, receipt!.Refunded, 9); // 弩塔 270×30%=81（§11.4 拆除返还 30%）
        Assert.Equal(before + 81.0, fixture.Stock.AmountOf(Currency), 9);

        // 塔移除、格释放（占用与登记双清）
        Assert.False(fixture.Buildings.TryGetById(tower, out _));
        Assert.False(fixture.Buildings.TryGetAt(Cell(4, 4), out _));

        // 残骸账目（薄版）：修复账=原造价×FailureTiers.RepairCostFraction（30% 权威单源）
        TowerRuin ruin = Assert.Single(fixture.Estate.Ruins);
        Assert.Equal(Cell(4, 4), ruin.Cell);
        Assert.Equal("era2:tower-crossbow", ruin.ContentId);
        Assert.Equal(2, ruin.Tier);
        Assert.Equal(270.0, ruin.OriginalCostPoints, 9);
        Assert.Equal(270.0 * FailureTiers.RepairCostFraction, ruin.RepairCostPoints, 9); // 81
        Assert.Equal(81.0, ruin.SellRefundPoints, 9);
    }

    [Fact] // illegal — 未知目标出售拒绝；重复出售第二笔 target-unknown
    public void Selling_an_unknown_or_already_sold_tower_refuses()
    {
        var fixture = Create();
        Assert.False(fixture.Estate.TryQuoteSell(new EntityId(4242), out _, out string? unknown));
        Assert.StartsWith("target-unknown:4242", unknown);

        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(4, 4));
        double before = fixture.Stock.AmountOf(Currency);
        Assert.NotNull(fixture.Estate.ApplySell(tower, out _));
        Assert.Null(fixture.Estate.ApplySell(tower, out string? second));
        Assert.StartsWith("target-unknown", second);
        Assert.Equal(before + 27.0, fixture.Stock.AmountOf(Currency), 9); // 只返还一次 27
        Assert.Single(fixture.Estate.Ruins);
    }

    [Fact] // boundary — 残骸按出售顺序留痕（确定性账目）
    public void Ruins_accumulate_in_sell_order()
    {
        var fixture = Create();
        EntityId first = fixture.Estate.Place(Era2Content.Arrow, Cell(2, 2));
        EntityId second = fixture.Estate.Place(Era2Content.Onager, Cell(3, 3));

        Assert.NotNull(fixture.Estate.ApplySell(first, out _));
        Assert.NotNull(fixture.Estate.ApplySell(second, out _));

        Assert.Equal(2, fixture.Estate.Ruins.Count);
        Assert.Equal("era2:tower-arrow", fixture.Estate.Ruins[0].ContentId);
        Assert.Equal("era2:tower-onager", fixture.Estate.Ruins[1].ContentId);
        Assert.Equal(700.0 * FailureTiers.RepairCostFraction, fixture.Estate.Ruins[1].RepairCostPoints, 9);
    }

    [Fact] // illegal — 非法构造与放置拒绝（守卫）
    public void Placement_refuses_occupied_cells_and_unknown_specs()
    {
        var fixture = Create();
        EntityId tower = fixture.Estate.Place(Era2Content.Arrow, Cell(5, 5));
        Assert.Throws<InvalidOperationException>(() => fixture.Estate.Place(Era2Content.Crossbow, Cell(5, 5)));

        var stranger = Era2Content.Load("""
            {"items":[{"id":"era2:tower-stranger","class":"pierce","tier":1,"max_tier":2,
              "cost_total":50,"dps":10.0,"damage_per_shot":10,"reload_ticks":30,
              "range_cells":5.0,"hp":100,"ammo_item":null,"ammo_per_shot":1}]}
            """)[0];
        Assert.Throws<InvalidOperationException>(() => fixture.Estate.Place(stranger, Cell(9, 9)));

        Assert.True(fixture.Buildings.TryGetAt(Cell(5, 5), out var kept));
        Assert.Equal(tower, kept.Id); // 拒绝路径零突变
    }
}
