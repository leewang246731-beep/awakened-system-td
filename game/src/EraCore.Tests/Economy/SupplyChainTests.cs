using EraCore.Contracts.Foundation;
using EraCore.Economy;
using Xunit;

namespace EraCore.Tests.Economy;

// [C-06][F-022] Chain-depth assembly validator (T-034): 链深 ≤3 —
// 资源(1)→中间件(2)→弹药(3) 合法，深于 3 的组装被拒绝并点名节点与深度
// （F-022 验收）；环与孤儿输入同样被拦截。深度按节点数计（原料=1）。
public class SupplyChainTests
{
    private static readonly ResourceKey Wood = new("era1:res-wood");
    private static readonly ResourceKey Plank = new("era1:res-plank");
    private static readonly ResourceKey Arrow = new("era1:ammo-arrow");
    private static readonly ResourceKey Charcoal = new("era1:res-charcoal");

    /// <summary>Canonical E1 chain: 木材 → 木板 → 箭矢弹药 (depth 1→2→3).</summary>
    private static SupplyChain CanonicalE1Chain()
    {
        var chain = new SupplyChain();
        chain.RegisterRaw(Wood);
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Wood }, Plank));
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Plank }, Arrow));
        return chain;
    }

    // ---- 公式组 5/6：链深 ≤3 表 ------------------------------------------

    [Theory] // legal — depth table: raw=1, crafted=1+deepest input
    [InlineData("era1:res-wood", 1)]
    [InlineData("era1:res-plank", 2)]
    [InlineData("era1:ammo-arrow", 3)]
    public void DepthOf_counts_nodes_from_raw_to_crafted(string key, int expectedDepth)
    {
        SupplyChain chain = CanonicalE1Chain();

        Assert.Equal(expectedDepth, chain.DepthOf(new ResourceKey(key)));
    }

    [Fact] // boundary — the canonical 资源→中间件→弹药 chain is exactly the ceiling and passes
    public void Canonical_chain_of_depth_three_validates_clean()
    {
        SupplyChain chain = CanonicalE1Chain();

        Assert.Empty(chain.Validate());
        Assert.Equal(new[] { Wood }, chain.RawResources);
        Assert.Equal(2, chain.Recipes.Count); // registration order preserved
    }

    [Fact] // boundary — the shipped E1 anchor wiring (wood → arrow) is depth 2, legal
    public void E1_anchor_recipe_wiring_is_within_the_ceiling()
    {
        var chain = new SupplyChain();
        chain.RegisterRaw(Wood);
        chain.RegisterRecipe(
            new SupplyChain.ChainRecipe(
                AmmoConverter.E1ArrowRecipe.Inputs.Select(static i => i.Resource).ToArray(),
                AmmoConverter.E1ArrowRecipe.AmmoResource));

        Assert.Equal(2, chain.DepthOf(AmmoConverter.E1ArrowRecipe.AmmoResource));
        Assert.Empty(chain.Validate());
    }

    [Fact] // illegal — F-022 验收: 链深=4 的组装被拒绝并给出点名提示
    public void Chain_of_depth_four_is_rejected_with_a_named_violation()
    {
        var chain = new SupplyChain();
        chain.RegisterRaw(Wood); // 1
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Wood }, Plank)); // 2
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Plank }, Charcoal)); // 3
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Charcoal }, Arrow)); // 4

        IReadOnlyList<string> violations = chain.Validate();

        Assert.Contains(
            violations, v => v.StartsWith("chain_depth:era1:ammo-arrow=4 exceeds the ceiling 3", StringComparison.Ordinal));
        Assert.Equal(4, chain.DepthOf(Arrow)); // 深度可查询，只是组装被拒
    }

    [Fact] // illegal — an intermediate crafted from a depth-3 node is the rejected hop
    public void Extending_a_legal_chain_by_one_stage_is_rejected()
    {
        SupplyChain chain = CanonicalE1Chain(); // depth 3 legal
        Assert.Empty(chain.Validate());

        var deep = new ResourceKey("era1:res-arrowhead");
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Arrow }, deep)); // depth 4

        Assert.Contains(
            chain.Validate(),
            v => v.StartsWith($"chain_depth:{deep.Value}=4 exceeds the ceiling 3", StringComparison.Ordinal));
    }

    [Fact] // illegal — a recipe loop has no finite depth and is reported, not crashed on
    public void Cycles_are_reported_as_violations()
    {
        var chain = new SupplyChain();
        var a = new ResourceKey("era1:res-a");
        var b = new ResourceKey("era1:res-b");
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { b }, a));
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { a }, b));

        IReadOnlyList<string> violations = chain.Validate();

        Assert.Contains(violations, v => v.StartsWith($"cycle:{a.Value}", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith($"cycle:{b.Value}", StringComparison.Ordinal));
    }

    [Fact] // illegal — an input that is neither raw nor crafted is orphan wiring
    public void Unknown_inputs_are_reported()
    {
        var chain = new SupplyChain();
        var ghost = new ResourceKey("era1:res-ghost");
        chain.RegisterRaw(Wood);
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Wood, ghost }, Arrow));

        Assert.Contains(chain.Validate(), v => v.StartsWith($"unknown_input:{ghost.Value}", StringComparison.Ordinal));
    }

    // ---- 注册合同 ---------------------------------------------------------

    [Fact] // illegal — one key, one meaning: duplicates rejected across raws and outputs
    public void Duplicate_keys_are_rejected()
    {
        var chain = new SupplyChain();
        chain.RegisterRaw(Wood);
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Wood }, Plank));

        Assert.Throws<InvalidOperationException>(() => chain.RegisterRaw(Wood));
        Assert.Throws<InvalidOperationException>(() => chain.RegisterRaw(Plank)); // 已是产物
        Assert.Throws<InvalidOperationException>(
            () => chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Wood }, Plank)));
        Assert.Throws<InvalidOperationException>(
            () => chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Plank }, Wood))); // 已是原料
    }

    [Fact] // illegal — a stage with no inputs is not a stage
    public void Empty_input_list_is_rejected()
    {
        var chain = new SupplyChain();

        Assert.Throws<ArgumentException>(
            () => chain.RegisterRecipe(new SupplyChain.ChainRecipe(Array.Empty<ResourceKey>(), Arrow)));
    }

    [Fact] // illegal — null wiring rejected
    public void Null_arguments_are_rejected()
    {
        var chain = new SupplyChain();

        Assert.Throws<ArgumentNullException>(() => chain.RegisterRaw(null!));
        Assert.Throws<ArgumentNullException>(
            () => chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { Wood }, null!)));
        Assert.Throws<ArgumentNullException>(
            () => chain.RegisterRecipe(new SupplyChain.ChainRecipe(null!, Arrow)));
        Assert.Throws<ArgumentNullException>(
            () => chain.RegisterRecipe(new SupplyChain.ChainRecipe(new ResourceKey[] { null! }, Arrow)));
        Assert.Throws<ArgumentNullException>(() => chain.DepthOf(null!));
    }

    [Fact] // illegal — DepthOf on unknown or cyclic nodes throws (Validate is the reporter)
    public void DepthOf_rejects_unknown_and_cyclic_nodes()
    {
        SupplyChain chain = CanonicalE1Chain();

        Assert.Throws<InvalidOperationException>(() => chain.DepthOf(new ResourceKey("era1:res-nothing")));

        var loop = new SupplyChain();
        var a = new ResourceKey("era1:res-a");
        loop.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { a }, a)); // self-loop
        Assert.Throws<InvalidOperationException>(() => loop.DepthOf(a));
    }

    [Fact] // illegal — self-feeding recipes are cycles in Validate too
    public void Self_loop_is_reported_by_validate()
    {
        var chain = new SupplyChain();
        var a = new ResourceKey("era1:res-a");
        chain.RegisterRecipe(new SupplyChain.ChainRecipe(new[] { a }, a));

        Assert.Contains(chain.Validate(), v => v.StartsWith($"cycle:{a.Value}", StringComparison.Ordinal));
    }
}
