using EraCore.Contracts.Foundation;

namespace EraCore.Economy;

/// <summary>
/// Chain-depth assembly validator (链深 ≤3, C-06 E1 thin, T-034, F-022;
/// design v3.2 §5 L74 链深 ≤3): the build-time check that refuses to
/// assemble a production chain deeper than three stages. Depth counts
/// nodes, not hops: a raw resource sits at depth 1, every crafted node
/// sits at 1 + the deepest of its inputs — the canonical E1 chain
/// 资源(1) → 中间件(2) → 弹药(3) fits, an intermediate crafted FROM an
/// intermediate already at depth 3 lands at 4 and is rejected with a
/// violation naming the node and its depth (F-022 acceptance: 构造链深=4
/// 的建造组合被拒绝并给出提示). The validator also flags cycles (a cycle
/// has no finite depth — it can never be built, only rejected) and inputs
/// that are neither registered raw materials nor crafted by any recipe
/// (orphan wiring). Deterministic: registration order is preserved and
/// drives both <see cref="Validate"/> output order and the state hash.
/// </summary>
public sealed class SupplyChain
{
    /// <summary>
    /// Frozen depth ceiling: 链深 ≤3 (v3.2 §5 L74) — raw(1) →
    /// intermediate(2) → ammo(3) is the deepest legal assembly.
    /// </summary>
    public const int MaxDepth = 3;

    private readonly List<ResourceKey> _rawOrder = new();
    private readonly HashSet<ResourceKey> _raws = new();
    private readonly List<ChainRecipe> _recipeOrder = new();
    private readonly Dictionary<ResourceKey, ChainRecipe> _recipes = new();

    /// <summary>One production stage of the chain: a crafted node and its inputs.</summary>
    /// <param name="Inputs">Input resources — raw or crafted by another stage; at least one.</param>
    /// <param name="Output">The crafted resource.</param>
    public sealed record ChainRecipe(IReadOnlyList<ResourceKey> Inputs, ResourceKey Output);

    /// <summary>
    /// Registers a raw resource (mine/forest/farm node — the depth-1
    /// sources). The same key as a raw or as any recipe output is rejected:
    /// one key, one meaning.
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="InvalidOperationException">the key is already registered (raw or output).</exception>
    public void RegisterRaw(ResourceKey resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_raws.Contains(resource) || _recipes.ContainsKey(resource))
        {
            throw new InvalidOperationException(
                $"{resource.Value} is already registered in this chain (raw or recipe output); one key, one meaning.");
        }

        _raws.Add(resource);
        _rawOrder.Add(resource);
    }

    /// <summary>
    /// Registers one production stage. Inputs may reference resources
    /// registered later (forward wiring is legal while assembling);
    /// <see cref="Validate"/> is the point where every input must resolve.
    /// The output key must be fresh (not raw, not another output) and the
    /// input list non-empty — a stage that crafts from nothing is not a
    /// stage.
    /// </summary>
    /// <exception cref="ArgumentNullException">recipe (or its parts) is null.</exception>
    /// <exception cref="ArgumentException">recipe has no inputs.</exception>
    /// <exception cref="InvalidOperationException">the output key is already registered.</exception>
    public void RegisterRecipe(ChainRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(recipe.Output);
        ArgumentNullException.ThrowIfNull(recipe.Inputs);
        if (recipe.Inputs.Count == 0)
        {
            throw new ArgumentException("A chain stage needs at least one input.", nameof(recipe));
        }

        foreach (ResourceKey input in recipe.Inputs)
        {
            ArgumentNullException.ThrowIfNull(input, nameof(recipe));
        }

        if (_raws.Contains(recipe.Output) || _recipes.ContainsKey(recipe.Output))
        {
            throw new InvalidOperationException(
                $"{recipe.Output.Value} is already registered in this chain (raw or recipe output); one key, one meaning.");
        }

        _recipes.Add(recipe.Output, recipe);
        _recipeOrder.Add(recipe);
    }

    /// <summary>Raw resources in registration order.</summary>
    public IReadOnlyList<ResourceKey> RawResources => _rawOrder;

    /// <summary>Production stages in registration order.</summary>
    public IReadOnlyList<ChainRecipe> Recipes => _recipeOrder;

    /// <summary>
    /// Depth of one node: 1 for a raw resource, 1 + the deepest input for
    /// a crafted one. Cyclic nodes have no finite depth and unknown keys
    /// none at all — both throw (the assembly-time verdict for those is
    /// <see cref="Validate"/>, which reports them as violations instead).
    /// </summary>
    /// <exception cref="ArgumentNullException">node is null.</exception>
    /// <exception cref="InvalidOperationException">node is unknown, or sits on/above a cycle.</exception>
    public int DepthOf(ResourceKey node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return DepthOf(node, new HashSet<ResourceKey>(), new Dictionary<ResourceKey, int>());
    }

    /// <summary>
    /// Runs the assembly check over the registered chain. An empty result
    /// passes. Violations, in registration order:
    /// <c>unknown_input:{res}</c> (an input neither raw nor crafted),
    /// <c>cycle:{output}</c> (a recipe loop), and
    /// <c>chain_depth:{output}={depth}&gt;3</c> (the F-022 rejection — the
    /// string names the offending node and depth so the build UI can show
    /// exactly which stage to refuse).
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var violations = new List<string>();
        foreach (ChainRecipe recipe in _recipeOrder)
        {
            foreach (ResourceKey input in recipe.Inputs)
            {
                if (!_raws.Contains(input) && !_recipes.ContainsKey(input))
                {
                    violations.Add($"unknown_input:{input.Value} feeds {recipe.Output.Value} but is neither raw nor crafted");
                }
            }
        }

        foreach (ChainRecipe recipe in _recipeOrder)
        {
            if (TryDepth(recipe.Output, new HashSet<ResourceKey>(), new Dictionary<ResourceKey, int>(), out int depth))
            {
                if (depth > MaxDepth)
                {
                    violations.Add(
                        $"chain_depth:{recipe.Output.Value}={depth} exceeds the ceiling {MaxDepth} (v3.2 §5 链深≤3 — refuse this assembly)");
                }
            }
            else
            {
                violations.Add($"cycle:{recipe.Output.Value} sits on or above a recipe loop — no finite depth");
            }
        }

        return violations;
    }

    private int DepthOf(ResourceKey node, HashSet<ResourceKey> visiting, Dictionary<ResourceKey, int> memo)
    {
        if (!TryDepth(node, visiting, memo, out int depth))
        {
            throw new InvalidOperationException($"{node.Value} sits on or above a recipe loop — its depth is undefined.");
        }

        return depth;
    }

    private bool TryDepth(
        ResourceKey node, HashSet<ResourceKey> visiting, Dictionary<ResourceKey, int> memo, out int depth)
    {
        depth = 0;
        if (_raws.Contains(node))
        {
            depth = 1;
            return true;
        }

        if (!_recipes.TryGetValue(node, out ChainRecipe? recipe))
        {
            return false; // unknown node — caller decides (throw or violation)
        }

        if (memo.TryGetValue(node, out int cached))
        {
            depth = cached;
            return true;
        }

        if (!visiting.Add(node))
        {
            return false; // cycle
        }

        int deepest = 0;
        foreach (ResourceKey input in recipe.Inputs)
        {
            if (!TryDepth(input, visiting, memo, out int inputDepth))
            {
                visiting.Remove(node);
                return false;
            }

            deepest = Math.Max(deepest, inputDepth);
        }

        visiting.Remove(node);
        depth = deepest + 1;
        memo[node] = depth;
        return true;
    }
}
