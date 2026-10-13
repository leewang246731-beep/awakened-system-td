using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Sim;
using EraCore.Supply;
using Xunit;

namespace EraCore.Tests.Agent.Pipeline;

// [C-13][F-071] T-053 DEBT ③ 行为回归（单元面）：TryPayCost 先于 Place 的
// 窄窗——Place 抛出时补偿回滚资源（库存复原+拒绝码具名补偿），正常/恰好
// /不足三径保持退役 TryPayCost 的冻结语义。文件名按 T2 映射挂在 Agent/
// Pipeline 泳道（T-053 并行纪律），被测类型=Sim 侧 BuildCostCompensation。
// [C-13][F-071]
public class BuildCostCompensationTests
{
    private static ResourceStock Stock(double wood = 100.0)
    {
        var stock = new ResourceStock(new SimTickClock(() => 0), ResourceStock.E1WarehouseLevel);
        if (wood > 0)
        {
            _ = stock.Deposit(new ResourceKey("era1:res-wood"), wood);
        }

        return stock;
    }

    [Fact] // legal：成功路径照旧全额扣费
    public void Successful_place_charges_the_full_cost()
    {
        ResourceStock stock = Stock(100.0);
        bool ran = false;

        string? rejection = BuildCostCompensation.TryPayThenPlace(
            stock, new ResourceKey("era1:res-wood"), 30.0, () => ran = true);

        Assert.Null(rejection);
        Assert.True(ran);
        Assert.Equal(70.0, stock.AmountOf(new ResourceKey("era1:res-wood")));
    }

    [Fact] // legal：零成本（控制/免费建造）不动库存
    public void Free_place_never_touches_the_stock()
    {
        ResourceStock stock = Stock(5.0);

        Assert.Null(BuildCostCompensation.TryPayThenPlace(
            stock, new ResourceKey("era1:res-wood"), 0.0, () => { }));
        Assert.Equal(5.0, stock.AmountOf(new ResourceKey("era1:res-wood")));
    }

    [Fact] // boundary：余额恰好=成本（可扣可建）
    public void Exact_balance_covers_the_cost()
    {
        ResourceStock stock = Stock(30.0);

        Assert.Null(BuildCostCompensation.TryPayThenPlace(
            stock, new ResourceKey("era1:res-wood"), 30.0, () => { }));
        Assert.Equal(0.0, stock.AmountOf(new ResourceKey("era1:res-wood")));
    }

    [Fact] // illegal：不足 → 拒付且 place 不运行
    public void Insufficient_stock_refuses_without_running_the_placement()
    {
        ResourceStock stock = Stock(29.0);
        bool overspent = false;

        string? rejection = BuildCostCompensation.TryPayThenPlace(
            stock, new ResourceKey("era1:res-wood"), 30.0, () => overspent = true);

        Assert.Equal("insufficient-resources", rejection);
        Assert.False(overspent);
        Assert.Equal(29.0, stock.AmountOf(new ResourceKey("era1:res-wood")));
    }

    [Fact] // illegal：窄窗本体——Place 抛出 → 全额补偿回滚 + 拒绝码具名（库存复原）
    public void Throwing_placement_refunds_the_withdrawal_in_full()
    {
        ResourceStock stock = Stock(100.0);
        var wood = new ResourceKey("era1:res-wood");

        string? rejection = BuildCostCompensation.TryPayThenPlace(
            stock, wood, 30.0,
            () => throw new InvalidOperationException("cell already owned by a foreign registry"));

        Assert.StartsWith(BuildCostCompensation.RefundedRejectionPrefix, rejection, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", rejection, StringComparison.Ordinal);
        Assert.Equal(100.0, stock.AmountOf(wood)); // 复原到扣款前
    }

    [Fact] // boundary：非托底异常类型（如 OutOfMemoryException）不吞——补偿只兜子系统契约拒绝
    public void Unexpected_exception_kinds_propagate()
    {
        ResourceStock stock = Stock(100.0);

        Assert.Throws<NotSupportedException>(
            () => BuildCostCompensation.TryPayThenPlace(
                stock, new ResourceKey("era1:res-wood"), 30.0,
                () => throw new NotSupportedException("boom")));
    }

    [Fact] // illegal：null 参数防御
    public void Arguments_are_validated()
    {
        Assert.Throws<ArgumentNullException>(
            () => BuildCostCompensation.TryPayThenPlace(null!, new ResourceKey("era1:res-wood"), 1.0, () => { }));
        Assert.Throws<ArgumentNullException>(
            () => BuildCostCompensation.TryPayThenPlace(Stock(), null!, 1.0, () => { }));
        Assert.Throws<ArgumentNullException>(
            () => BuildCostCompensation.TryPayThenPlace(Stock(), new ResourceKey("era1:res-wood"), 1.0, null!));
    }
}
