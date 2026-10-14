using EraCore.Economy;
using EraCore.Contracts.Foundation;
using EraCore.Progression;
using Xunit;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Progression;

// [F-050][F-051][F-052] 科技树引擎（T-073）：三重闸门矩阵（拓扑/材料/关卡
// 绑定各正反+时代闸）/成本公式表驱动（60+max(0,Σ)^1.11×20×全局因子，含
// 逐解锁 +2% 递增与 F.5 箭楼锚点）/分期约束（同时至多 1、付清前不开新、
// 短池分期）/材料守恒。三行 legal/boundary/illegal 全覆盖（CS-3.1）；
// 期望值全部手算自冻结公式（注释含代入，容差 ±2=末位舍入）。[C-19]
public class TechTreeTests
{
    private static TechTree NewTree(ResourceStock pool, int era = 1) =>
        new(TechTreeData.Parse(MinimalTechTree), pool, era);

    private static ResourceStock RichPool(double wood = 5000, double stone = 5000, double food = 5000)
    {
        var clock = new MutableWallClock();
        var pool = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
        _ = pool.Deposit(Wood, wood);
        _ = pool.Deposit(Stone, stone);
        _ = pool.Deposit(Food, food);
        return pool;
    }

    // ------------------------------------------------------- legal

    [Fact] // legal — 成本公式表驱动：60+max(0,n)^1.11×20×(1+0.02×u)（v3.2 §7）
    [Trait("Feature", "F-051")]
    public void Cost_formula_table()
    {
        // 手算代入（Python 独立复算；期望值非实现回显，precision=0 位小数断言）：
        //   n=15,u=0 : 60+15^1.11×20×1     = 60+403.80  = 463.80 → 464
        //   n=30,u=0 : 60+30^1.11×20×1     = 60+872.30  = 932.30 → 932
        //   n=60,u=0 : 60+60^1.11×20×1     = 60+1882.69 = 1942.69 → 1943
        //   n=90,u=0 : 60+90^1.11×20×1     = 60+2952.84 = 3012.84 → 3013（F.5 箭楼锚 ≈3000/33 倍）
        //   n=90,u=5 : 幂项×(1+0.02×5)=1.10 = 3308.12 → 3308
        //   n=90,u=10: 幂项×(1+0.02×10)=1.20 = 3603.40 → 3603
        //   n=2430,u=0（火油塔）           = 114625.26 → 114625
        var pool = RichPool();
        TechTree tree = NewTree(pool);
        TechNodeSpec node = tree.Find("t:root")!;

        Assert.Equal(932, tree.ResearchCost(node)); // 整数公式输出（AwayFromZero 舍入）
        Assert.Equal(464, TechTree.RawCost(15, 0), 0);
        Assert.Equal(932, TechTree.RawCost(30, 0), 0);
        Assert.Equal(1943, TechTree.RawCost(60, 0), 0);
        Assert.Equal(3013, TechTree.RawCost(90, 0), 0);
        Assert.Equal(3308, TechTree.RawCost(90, 5), 0);
        Assert.Equal(3603, TechTree.RawCost(90, 10), 0);
        Assert.Equal(114625, TechTree.RawCost(2430, 0), 0);

        // 全局因子递增（F-051：每多解锁 1 项成本 +2%）：因子只乘幂项——
        // 每步增量恰为 n=90 幂项的 2%，且严格单调。
        double powerTerm = TechTree.RawCost(90, 0) - TechTree.CostBaseTerm;
        double previous = TechTree.RawCost(90, 0);
        for (int unlocked = 1; unlocked <= 4; unlocked++)
        {
            double current = TechTree.RawCost(90, unlocked);
            Assert.Equal(0.02 * powerTerm, current - previous, 9);
            Assert.True(current > previous);
            previous = current;
        }
    }

    [Fact] // legal — F.5 研究锚点：箭楼 n=90→≈3000（33 倍，参照系 31 倍带内）
    [Trait("Feature", "F-051")]
    public void Arrow_tower_anchor_lands_near_3000()
    {
        double cost = TechTree.RawCost(90, 0);
        Assert.InRange(cost, 2850.0, 3150.0); // ≈3000 的 ±5% 带
        Assert.InRange(cost / 90.0, 31.0, 35.0); // 33 倍（v3.2 附录 F.5 原文口径）
    }

    [Fact] // legal — 全链绿灯：材料足够+无前置+无绑定 → 开研→扣材料→分期付清→解锁
    [Trait("Feature", "F-050")]
    public void Begin_and_pay_off_unlocks_the_node()
    {
        ResourceStock pool = RichPool();
        TechTree tree = NewTree(pool);
        TechGateReport initial = tree.CheckGates("t:root");
        Assert.True(initial.CanBegin);

        double woodBefore = pool.AmountOf(Wood);
        tree.BeginResearch("t:root");
        Assert.NotNull(tree.Active);
        Assert.Equal("t:root", tree.Active!.NodeId);
        Assert.Equal(932, tree.Active.TotalCost); // 60+30^1.11×20（手算 932.30）
        Assert.Equal(woodBefore - 20.0, pool.AmountOf(Wood), 9); // 材料 20 木在开研即扣（承诺模型）

        InstallmentResult first = tree.PayInstallment(Wood, 400.0);
        Assert.False(first.Completed);
        Assert.Equal(400.0, first.Applied, 9);
        Assert.Empty(tree.UnlockedIds);

        InstallmentResult second = tree.PayInstallment(Wood, 700.0); // 超额部分拒收不扣
        Assert.True(second.Completed);
        Assert.Equal(532.0, second.Applied, 0); // 932−400=532 尾款
        Assert.Equal(0.0, second.Remaining, 9);
        Assert.Null(tree.Active);
        Assert.Equal(new[] { "t:root" }, tree.UnlockedIds);
        Assert.Equal(woodBefore - 20.0 - 932.0, pool.AmountOf(Wood), 6); // 守恒：材料+全款
    }

    [Fact] // legal — 闸门矩阵：三重闸门各自独立拦截（正/反逐项）
    [Trait("Feature", "F-050")]
    public void Triple_gate_matrix()
    {
        ResourceStock pool = RichPool();
        TechTree tree = NewTree(pool);

        // 反-拓扑：mid 需要 root 已解锁
        TechGateReport blockedTopology = tree.CheckGates("t:mid");
        Assert.False(blockedTopology.TopologyPassed);
        Assert.Contains("t:root", blockedTopology.MissingPrerequisites);
        Assert.True(blockedTopology.MaterialsPassed); // 其余闸门不受牵连（逐项可判）
        Assert.False(blockedTopology.CanBegin);
        Assert.Throws<InvalidOperationException>(() => tree.BeginResearch("t:mid"));

        // 正-拓扑：解锁 root 后拓扑转绿，但反-关卡绑定仍拦
        tree.BeginResearch("t:root");
        _ = tree.PayInstallment(Wood, 2000.0);
        TechGateReport blockedLevel = tree.CheckGates("t:mid");
        Assert.True(blockedLevel.TopologyPassed);
        Assert.False(blockedLevel.LevelBindingPassed);
        Assert.Contains("campaign:level-x", blockedLevel.UncapturedLevels);
        Assert.Throws<InvalidOperationException>(() => tree.BeginResearch("t:mid"));

        // 正-关卡绑定：占领后转绿
        tree.RegisterCapture("campaign:level-x");
        TechGateReport open = tree.CheckGates("t:mid");
        Assert.True(open.LevelBindingPassed);
        Assert.True(open.CanBegin);
    }

    [Fact] // legal — 支付分摊：floor+末行补差——分摊恰等于总额且确定（E1 池上限可容纳）
    [Trait("Feature", "F-051")]
    public void Cost_mix_split_is_exact_and_deterministic()
    {
        var halfHalf = new[] { new TechCostLine(Wood, 0.5), new TechCostLine(Stone, 0.5) };
        IReadOnlyList<(ResourceKey Resource, long Charge)> split = TechTree.SplitCost(3013, halfHalf);
        Assert.Equal((Wood, 1506L), split[0]); // floor(3013×0.5)=1506
        Assert.Equal((Stone, 1507L), split[1]); // 末行补差=3013−1506
        Assert.Equal(3013L, split.Sum(line => line.Charge)); // 分摊之和=总额（无丢失）

        var threeWay = new[]
        {
            new TechCostLine(Wood, 0.6), new TechCostLine(Stone, 0.3), new TechCostLine(Food, 0.1),
        };
        IReadOnlyList<(ResourceKey Resource, long Charge)> split3 = TechTree.SplitCost(932, threeWay);
        Assert.Equal(559L, split3[0].Charge); // floor(932×0.6)=559
        Assert.Equal(279L, split3[1].Charge); // floor(932×0.3)=279
        Assert.Equal(94L, split3[2].Charge); // 补差 932−838
        Assert.Equal(932L, split3.Sum(line => line.Charge));
    }

    [Fact] // legal — 多资源分摊付款：每资源独立分期，全部付清才解锁
    [Trait("Feature", "F-051")]
    public void Mixed_cost_pays_off_per_resource()
    {
        IReadOnlyList<TechNodeSpec> nodes = TechTreeData.Parse(
            """
            {
              "items": [
                { "id": "t:mix", "era_index": 1, "branch": "main", "kind": "system_upgrade",
                  "prerequisites": [], "bound_levels": [],
                  "materials": [ { "resource": "era1:res-wood", "amount": 10 } ],
                  "cost_sum": 30,
                  "cost_mix": [ { "resource": "era1:res-wood", "share": 0.6 }, { "resource": "era1:res-stone", "share": 0.4 } ],
                  "unlocks": [] }
              ]
            }
            """);
        ResourceStock pool = RichPool();
        var tree = new TechTree(nodes, pool, 1);

        tree.BeginResearch("t:mix");
        TechCostBalance woodLine = tree.Active!.Balances.Single(balance => balance.Resource.Equals(Wood));
        TechCostBalance stoneLine = tree.Active.Balances.Single(balance => balance.Resource.Equals(Stone));
        Assert.Equal(559.0, woodLine.Charge, 0); // floor(932×0.6)
        Assert.Equal(373.0, stoneLine.Charge, 0); // 补差 932−559

        InstallmentResult woodPaid = tree.PayInstallment(Wood, 559.0); // 木线付清
        Assert.False(woodPaid.Completed); // 石线未付 → 不解锁
        InstallmentResult stonePartial = tree.PayInstallment(Stone, 300.0);
        Assert.False(stonePartial.Completed);
        InstallmentResult stoneFinal = tree.PayInstallment(Stone, 100.0); // 只收尾款 73
        Assert.True(stoneFinal.Completed);
        Assert.Equal(73.0, stoneFinal.Applied, 0);
        Assert.Equal(new[] { "t:mix" }, tree.UnlockedIds);
    }

    [Fact] // legal — 闸门矩阵（材料反面）：石料不足 → 材料闸红且拓扑/绑定不受牵连
    [Trait("Feature", "F-050")]
    public void Material_gate_blocks_alone()
    {
        var clock = new MutableWallClock();
        var pool = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
        _ = pool.Deposit(Wood, 5000); // 只有木，无石
        TechTree tree = NewTree(pool);

        tree.RegisterCapture("campaign:level-x");
        TechGateReport report = tree.CheckGates("t:mid"); // 拓扑反（root 未解锁）
        Assert.False(report.MaterialsPassed);
        Assert.Single(report.MissingMaterials, material => material.Resource.Equals(Stone));
        Assert.Equal(40.0, report.MissingMaterials[0].Amount, 9);
        Assert.Throws<InvalidOperationException>(() => tree.BeginResearch("t:mid"));
    }

    [Fact] // legal — 时代闸：E2 节点在 E1 战役不可开研（F-050 时代条件）
    [Trait("Feature", "F-050")]
    public void Era_gate_blocks_future_era_nodes()
    {
        ResourceStock pool = RichPool();
        TechTree tree = NewTree(pool, era: 1);
        TechGateReport report = tree.CheckGates("t:future");
        Assert.False(report.EraPassed);
        Assert.False(report.CanBegin);
        Assert.Throws<InvalidOperationException>(() => tree.BeginResearch("t:future"));

        TechTree eraTwo = NewTree(pool, era: 2);
        Assert.True(eraTwo.CheckGates("t:future").EraPassed);
    }

    // ------------------------------------------------------- boundary

    [Fact] // boundary — 分期约束：未付清前开第二项被拒（同时至多 1 节点）
    [Trait("Feature", "F-050")]
    public void Second_research_is_refused_until_paid_off()
    {
        ResourceStock pool = RichPool();
        TechTree tree = NewTree(pool);
        tree.BeginResearch("t:root");
        _ = tree.PayInstallment(Wood, 100.0); // 部分付款，未付清

        Assert.False(tree.CheckGates("t:sys").SlotFree);
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => tree.BeginResearch("t:sys"));
        Assert.Contains("Installment slot busy", refusal.Message); // 确实因分期闸拒绝
        _ = tree.PayInstallment(Wood, 2000.0); // 付清
        Assert.True(tree.CheckGates("t:sys").SlotFree);
        tree.BeginResearch("t:sys"); // 现在合法
    }

    [Fact] // boundary — 短池分期：池不足只入账实扣部分，付清前不解锁
    [Trait("Feature", "F-051")]
    public void Short_pool_installments_credit_partial_payment()
    {
        var clock = new MutableWallClock();
        var pool = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
        _ = pool.Deposit(Wood, 20.0 + 300.0); // 材料 20 + 仅够 300 的款
        TechTree tree = NewTree(pool);

        tree.BeginResearch("t:root");
        InstallmentResult first = tree.PayInstallment(Wood, 500.0); // 要 500，池只有 300
        Assert.Equal(300.0, first.Applied, 9);
        Assert.False(first.Completed);
        Assert.NotNull(tree.Active); // 仍在研究（未付清）

        _ = pool.Deposit(Wood, 1000.0); // 补款后再付
        InstallmentResult second = tree.PayInstallment(Wood, 1000.0);
        Assert.True(second.Completed);
        Assert.Equal(632.0, second.Applied, 0); // 只收尾款 932−300=632，超额拒收
    }

    [Fact] // boundary — 已解锁节点不可重复开研（NotYetUnlocked 闸）
    [Trait("Feature", "F-050")]
    public void Unlocked_node_cannot_be_researched()
    {
        ResourceStock pool = RichPool();
        TechTree tree = NewTree(pool);
        tree.BeginResearch("t:root");
        _ = tree.PayInstallment(Wood, 2000.0);
        Assert.False(tree.CheckGates("t:root").NotYetUnlocked);
        Assert.Throws<InvalidOperationException>(() => tree.BeginResearch("t:root"));
    }

    // ------------------------------------------------------- illegal

    [Fact] // illegal — 未知节点/空 id：ArgumentException 拒绝
    [Trait("Feature", "F-050")]
    public void Unknown_node_is_rejected()
    {
        ResourceStock pool = RichPool();
        TechTree tree = NewTree(pool);
        Assert.Throws<ArgumentException>(() => tree.CheckGates("t:ghost"));
        Assert.Throws<ArgumentException>(() => tree.BeginResearch("t:ghost"));
        Assert.Throws<ArgumentException>(() => tree.CheckGates(" "));
    }

    [Fact] // illegal — 无在研时付款/负数与零付款/重复占领/越界时代：全部拒绝
    [Trait("Feature", "F-050")]
    public void Illegal_operations_are_rejected()
    {
        ResourceStock pool = RichPool();
        TechTree tree = NewTree(pool);
        Assert.Throws<InvalidOperationException>(() => tree.PayInstallment(Wood, 10.0)); // 无在研

        tree.BeginResearch("t:root");
        Assert.Throws<ArgumentException>(() => tree.PayInstallment(Wood, 0.0));
        Assert.Throws<ArgumentException>(() => tree.PayInstallment(Wood, -5.0));
        Assert.Throws<ArgumentException>(() => tree.PayInstallment(Wood, double.NaN));
        Assert.Throws<ArgumentException>(() => tree.PayInstallment(Stone, 5.0)); // 非本节点分摊资源
        Assert.Throws<ArgumentNullException>(() => tree.PayInstallment(null!, 5.0));

        tree.RegisterCapture("campaign:level-x");
        Assert.Throws<InvalidOperationException>(() => tree.RegisterCapture("campaign:level-x")); // 重复占领
        Assert.Throws<ArgumentException>(() => tree.RegisterCapture(" "));

        Assert.Throws<ArgumentException>(() => NewTree(pool, era: 0));
        Assert.Throws<ArgumentNullException>(() => new TechTree(null!, pool, 1));
        Assert.Throws<ArgumentNullException>(() => new TechTree(TechTreeData.Parse(MinimalTechTree), null!, 1));
    }

    [Fact] // illegal — 材料中途消失的窄窗：回滚补偿（开研原子性）
    [Trait("Feature", "F-050")]
    public void Material_shortfall_mid_begin_rolls_back()
    {
        // 双材料节点：木够、石差一口气 → 构造石恰好不足的池
        IReadOnlyList<TechNodeSpec> nodes = TechTreeData.Parse(
            """
            {
              "items": [
                { "id": "t:two", "era_index": 1, "branch": "main", "kind": "system_upgrade",
                  "prerequisites": [], "bound_levels": [],
                  "materials": [ { "resource": "era1:res-wood", "amount": 10 }, { "resource": "era1:res-stone", "amount": 50 } ],
                  "cost_sum": 20, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }], "unlocks": [] }
              ]
            }
            """);
        var clock = new MutableWallClock();
        var pool = new ResourceStock(clock, ResourceStock.E1WarehouseLevel);
        _ = pool.Deposit(Wood, 100.0);
        _ = pool.Deposit(Stone, 49.5); // 差 0.5
        var tree = new TechTree(nodes, pool, 1);

        Assert.Throws<InvalidOperationException>(() => tree.BeginResearch("t:two"));
        Assert.Null(tree.Active); // 未开研
        Assert.Equal(100.0, pool.AmountOf(Wood), 9); // 木被回滚补回
        Assert.Equal(49.5, pool.AmountOf(Stone), 9); // 石未被扣
    }
}
