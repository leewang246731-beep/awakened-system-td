using Xunit;
using EraCore.Legacy;
using EraCore.Progression;

namespace EraCore.Tests.Legacy;

// [C-20][F-059] 叠加执法（T-074）：乘法叠加/总下限 50%/单一咽喉 +
// Progression 减免接入（TechTree.BeginResearch(reliefShare)）。
public class StackingRuleTests
{
    // ---- multiplicative stacking --------------------------------- legal

    [Fact] // legal — 两份 10% 减免乘法叠加=19%，不是加法 20%
    public void reliefs_stack_multiplicatively()
    {
        Assert.Equal(0.19, StackingRule.CombinedShare(new[] { 0.10, 0.10 }), 9);
        Assert.Equal(0.271, StackingRule.CombinedShare(new[] { 0.10, 0.10, 0.10 }), 9); // 1−0.9³
    }

    [Fact] // legal — 大图书馆 15% + 残卷 10% + 纪念馆 4%（附录 F.3 全景叠加）
    public void appendix_f3_full_stack_example()
    {
        double share = StackingRule.CombinedShare(new[] { 0.15, 0.10, 0.04 });
        Assert.Equal(1.0 - (0.85 * 0.90 * 0.96), share, 9);
        double cost = StackingRule.Apply(3000.0, new[] { 0.15, 0.10, 0.04 });
        Assert.Equal(3000.0 * (1.0 - share), cost, 9);
    }

    [Fact] // boundary — 空清单=零减免；单份额原样
    public void empty_stack_is_zero_relief()
    {
        Assert.Equal(0.0, StackingRule.CombinedShare(Array.Empty<double>()), 12);
        Assert.Equal(0.15, StackingRule.CombinedShare(new[] { 0.15 }), 12);
    }

    [Fact] // boundary — 总下限恰 50%：钳制在 MaxTotalReliefShare
    public void total_relief_floors_at_fifty_percent()
    {
        double share = StackingRule.CombinedShare(new[] { 0.30, 0.30, 0.30 }); // 乘法本可到 65.7%
        Assert.Equal(0.5, share, 12); // F.3 总下限
        Assert.Equal(1500.0, StackingRule.Apply(3000.0, new[] { 0.30, 0.30, 0.30 }), 9);
        double justUnder = StackingRule.CombinedShare(new[] { 0.25, 0.30 }); // 47.5% 未触底
        Assert.Equal(0.475, justUnder, 12);
    }

    [Theory] // illegal — 份额域违例拒绝
    [InlineData(-0.01)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void out_of_domain_shares_rejected(double share)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackingRule.CombinedShare(new[] { share }));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackingRule.Apply(100.0, new[] { share }));
    }

    [Fact] // illegal — 负成本拒绝
    public void negative_cost_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackingRule.Apply(-1.0, Array.Empty<double>()));
    }

    // ---- Progression 接入 ------------------------------------------ legal

    [Fact] // legal — TechTree.ResearchCost(node, share) 按钳制后份额折价
    public void tech_tree_applies_choked_relief_to_formula_total()
    {
        var pool = LegacyFixture.Pool(0, 0, 0);
        var node = new TechNodeSpec(
            "test:tech-cost", 1, TechBranch.Main, TechNodeKind.TowerUnlock,
            Array.Empty<string>(), Array.Empty<TechMaterial>(), Array.Empty<string>(),
            CostSum: 90, // F.5 锚点：n=90 → ≈3013
            CostMix: new[] { new TechCostLine(new Contracts.Foundation.ResourceKey("era1:res-wood"), 1.0) },
            Unlocks: Array.Empty<string>());
        var tree = new TechTree(new[] { node }, pool, 1);
        long full = tree.ResearchCost(node);
        long relieved = tree.ResearchCost(node, 0.10); // 残卷 10%
        Assert.Equal(full, tree.ResearchCost(node, 0.0)); // 0=原价
        Assert.Equal((long)Math.Round(full * 0.9, MidpointRounding.AwayFromZero), relieved);
        Assert.True(relieved < full);
    }

    [Theory] // illegal — TechTree 减免接入域 [0,0.5] 外拒绝（咽喉钳制口径）
    [InlineData(-0.01)]
    [InlineData(0.51)]
    [InlineData(1.0)]
    public void tech_tree_rejects_relief_outside_choke_domain(double share)
    {
        var pool = LegacyFixture.Pool(0, 0, 0);
        var node = new TechNodeSpec(
            "test:tech-cost2", 1, TechBranch.Main, TechNodeKind.TowerUnlock,
            Array.Empty<string>(), Array.Empty<TechMaterial>(), Array.Empty<string>(),
            10, new[] { new TechCostLine(new Contracts.Foundation.ResourceKey("era1:res-wood"), 1.0) },
            Array.Empty<string>());
        var tree = new TechTree(new[] { node }, pool, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.ResearchCost(node, share));
    }

    [Fact] // legal — BeginResearch 带减免：分期总额锁定折后价（F-059 全链）
    public void begin_research_locks_the_relieved_total()
    {
        var pool = LegacyFixture.Pool(5000, 0, 0); // 材料免设、支付足额
        var node = new TechNodeSpec(
            "test:tech-install", 1, TechBranch.Main, TechNodeKind.TowerUnlock,
            Array.Empty<string>(), Array.Empty<TechMaterial>(), Array.Empty<string>(),
            90, new[] { new TechCostLine(new Contracts.Foundation.ResourceKey("era1:res-wood"), 1.0) },
            Array.Empty<string>());
        var tree = new TechTree(new[] { node }, pool, 1);
        tree.BeginResearch(node.Id, reliefShare: 0.15); // 大图书馆
        Assert.NotNull(tree.Active);
        Assert.Equal(tree.ResearchCost(node, 0.15), tree.Active!.TotalCost);
        var result = tree.PayInstallment(new Contracts.Foundation.ResourceKey("era1:res-wood"), 500);
        Assert.Equal(500, result.Paid, 9);
        Assert.True(result.Remaining > 0); // 未付清
    }

    [Fact] // legal — 缺省 0 保持 T-073 原行为（回归锚）
    public void begin_research_without_relief_keeps_original_total()
    {
        var pool = LegacyFixture.Pool(5000, 0, 0);
        var node = new TechNodeSpec(
            "test:tech-plain", 1, TechBranch.Main, TechNodeKind.TowerUnlock,
            Array.Empty<string>(), Array.Empty<TechMaterial>(), Array.Empty<string>(),
            90, new[] { new TechCostLine(new Contracts.Foundation.ResourceKey("era1:res-wood"), 1.0) },
            Array.Empty<string>());
        var tree = new TechTree(new[] { node }, pool, 1);
        tree.BeginResearch(node.Id); // 旧签名
        Assert.Equal(tree.ResearchCost(node), tree.Active!.TotalCost);
    }

    // ---- TechTree era root ----------------------------------------- legal

    [Fact] // legal — SetEraIndex 切换时代根：E2 节点闸门由拒到过
    public void era_root_switch_flips_the_era_gate()
    {
        var pool = LegacyFixture.Pool(0, 0, 0);
        var node = new TechNodeSpec(
            "test:tech-e2x", 2, TechBranch.Main, TechNodeKind.TowerUnlock,
            Array.Empty<string>(), Array.Empty<TechMaterial>(), Array.Empty<string>(),
            10, new[] { new TechCostLine(new Contracts.Foundation.ResourceKey("era1:res-wood"), 1.0) },
            Array.Empty<string>());
        var tree = new TechTree(new[] { node }, pool, 1);
        Assert.False(tree.CheckGates(node.Id).EraPassed);
        tree.SetEraIndex(2); // [T-074 小扩展]
        Assert.Equal(2, tree.EraIndex);
        Assert.True(tree.CheckGates(node.Id).EraPassed);
        tree.SetEraIndex(1); // 回滚路径
        Assert.False(tree.CheckGates(node.Id).EraPassed);
    }

    [Theory] // illegal — 时代根域 1..6 外拒绝
    [InlineData(0)]
    [InlineData(7)]
    public void era_root_domain_enforced(int era)
    {
        var tree = LegacyFixture.Tree(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.SetEraIndex(era));
    }
}
