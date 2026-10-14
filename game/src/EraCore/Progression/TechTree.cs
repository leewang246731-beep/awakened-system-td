using EraCore.Contracts.Foundation;
using EraCore.Economy;

namespace EraCore.Progression;

/// <summary>
/// One per-resource balance of the active research: the locked charge of
/// the cost mix line and how much of it has been paid so far.
/// </summary>
/// <param name="Resource">Payment resource of the mix line.</param>
/// <param name="Charge">Locked charge (whole items, split at research start).</param>
/// <param name="Paid">Amount paid toward this line so far.</param>
public sealed record TechCostBalance(ResourceKey Resource, double Charge, double Paid);

/// <summary>
/// The installment slot of the tree (F-050 分期): the single node currently
/// being paid off — at most one exists at any time, and no second research
/// may begin until it is fully paid. The formula total is split across the
/// node's cost mix at research start (see <see cref="TechTree.SplitCost"/>)
/// and each resource balance pays off independently.
/// </summary>
/// <param name="NodeId">The node being researched.</param>
/// <param name="TotalCost">Total formula cost (whole items) locked at research start.</param>
/// <param name="Balances">Per-resource charge/paid balances of the mix.</param>
/// <param name="Paid">Amount actually withdrawn from the pool so far (all lines summed).</param>
public sealed record ActiveResearch(
    string NodeId,
    long TotalCost,
    IReadOnlyList<TechCostBalance> Balances,
    double Paid)
{
    /// <summary>Total still owed across all mix lines.</summary>
    public double Remaining => Math.Max(0.0, Balances.Sum(balance => Math.Max(0.0, balance.Charge - balance.Paid)));
}

/// <summary>
/// Result of one <see cref="TechTree.PayInstallment"/> call: what was
/// requested, what the pool actually surrendered (partial installments are
/// legal — the pool may be short), whether the payment completed the node.
/// </summary>
/// <param name="Requested">Installment amount the caller asked to pay.</param>
/// <param name="Applied">Amount actually withdrawn and credited.</param>
/// <param name="RefusedOverPay">Requested amount above the remaining balance (never charged).</param>
/// <param name="Paid">Total paid after this installment.</param>
/// <param name="Remaining">TotalCost − Paid after this installment.</param>
/// <param name="Completed">Whether the node unlocked with this payment.</param>
public sealed record InstallmentResult(
    double Requested,
    double Applied,
    double RefusedOverPay,
    double Paid,
    double Remaining,
    bool Completed);

/// <summary>
/// Per-gate verdict of <see cref="TechTree.CheckGates"/> (F-050 三重闸门
/// matrix): each of the three frozen gates plus the installment slot and
/// the unlock state, individually inspectable so UI/tests can name the
/// exact blocking gate (谓词式提示 seam).
/// </summary>
/// <param name="NodeId">The inspected node.</param>
/// <param name="TopologyPassed">拓扑前置: every prerequisite is unlocked.</param>
/// <param name="MaterialsPassed">材料依赖: every material line is present in the pool.</param>
/// <param name="LevelBindingPassed">关卡绑定: every bound level is captured (占领).</param>
/// <param name="EraPassed">时代: the campaign era has reached the node's era.</param>
/// <param name="SlotFree">分期: no other node is currently being paid off.</param>
/// <param name="NotYetUnlocked">The node is not already unlocked.</param>
/// <param name="MissingPrerequisites">Ids of the unlocked-missing prerequisites (empty when passing).</param>
/// <param name="MissingMaterials">Material lines lacking pool coverage (empty when passing).</param>
/// <param name="UncapturedLevels">Bound level ids not yet captured (empty when passing).</param>
public sealed record TechGateReport(
    string NodeId,
    bool TopologyPassed,
    bool MaterialsPassed,
    bool LevelBindingPassed,
    bool EraPassed,
    bool SlotFree,
    bool NotYetUnlocked,
    IReadOnlyList<string> MissingPrerequisites,
    IReadOnlyList<TechMaterial> MissingMaterials,
    IReadOnlyList<string> UncapturedLevels)
{
    /// <summary>True when research may begin right now (all gates green).</summary>
    public bool CanBegin => TopologyPassed && MaterialsPassed && LevelBindingPassed && EraPassed && SlotFree && NotYetUnlocked;
}

/// <summary>
/// Technology tree engine (T-073, F-050/F-051/F-052): the triple gate
/// (拓扑前置/材料依赖/关卡绑定), the frozen research-cost formula and the
/// single-installment-slot rule, paying out of the campaign
/// <see cref="ResourceStock"/> pool. Cost formula (v3.2 §7, frozen):
/// <c>60 + max(0,Σ成本)^1.11 × 20 × (1 + 0.02×已解锁数)</c> — the Σ成本 of
/// each node comes from content (tower nodes = towers.json cost_total,
/// cross-checked by tests), the 全局因子 grows +2% per unlocked node.
/// Materials are consumed when research BEGINS (the commitment model:
/// parts are committed up front, installments pay the formula cost after);
/// a begin whose material withdrawal cannot complete in full is rolled
/// back atomically.
/// </summary>
public sealed class TechTree
{
    /// <summary>Cost formula base term 60 (v3.2 §7).</summary>
    public const double CostBaseTerm = 60.0;

    /// <summary>Cost formula exponent 1.11 (v3.2 §7).</summary>
    public const double CostExponent = 1.11;

    /// <summary>Cost formula scale 20 (v3.2 §7).</summary>
    public const double CostScale = 20.0;

    /// <summary>Global factor: +2% per already-unlocked node (v3.2 §7).</summary>
    public const double CostPerUnlockedFactor = 0.02;

    private readonly Dictionary<string, TechNodeSpec> _nodes;
    private readonly ResourceStock _pool;
    private readonly List<string> _unlockedInOrder = new();
    private readonly HashSet<string> _capturedLevels = new();

    private ActiveResearch? _active;

    // [T-074 小扩展：C-20 跃迁事务] _eraIndex 由 readonly 改可写——跃迁提交时
    // SetEraIndex(+1) 切换科技时代根，任一步失败由事务日志以旧值回调恢复；
    // 除此之外时代根不可变（v3.2 §5：仓库/时代只在跃迁时推进）。
    private int _eraIndex;

    /// <param name="nodes">Validated node table (<see cref="TechTreeData.Parse"/> output).</param>
    /// <param name="pool">Campaign resource pool — materials and installments withdraw here.</param>
    /// <param name="eraIndex">Current campaign era (1=E1 … 6=终章); nodes of later eras stay gated.</param>
    /// <exception cref="ArgumentNullException">nodes or pool is null.</exception>
    /// <exception cref="ArgumentException">era outside 1..6 or empty node table.</exception>
    public TechTree(IReadOnlyList<TechNodeSpec> nodes, ResourceStock pool, int eraIndex)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(pool);
        if (nodes.Count == 0)
        {
            throw new ArgumentException("A tech tree needs at least one node.", nameof(nodes));
        }

        if (eraIndex is < 1 or > 6)
        {
            throw new ArgumentException($"Era index must be 1..6 (got {eraIndex}).", nameof(eraIndex));
        }

        _nodes = nodes.ToDictionary(node => node.Id, node => node);
        _pool = pool;
        _eraIndex = eraIndex;
    }

    /// <summary>All nodes of the tree in content order.</summary>
    public IReadOnlyList<TechNodeSpec> Nodes => _nodes.Values.ToList();

    /// <summary>Unlocked node ids in unlock order.</summary>
    public IReadOnlyList<string> UnlockedIds => _unlockedInOrder;

    /// <summary>Number of unlocked nodes — the 全局因子 input.</summary>
    public int UnlockedCount => _unlockedInOrder.Count;

    /// <summary>Current campaign era of the tree (the era-gate input). [T-074 小扩展：C-20 事务需读现值以校验/回滚]</summary>
    public int EraIndex => _eraIndex;

    /// <summary>
    /// Switches the tech-tree era root (T-074 任务卡步骤「科技时代根切换」).
    /// [T-074 小扩展：C-20 跃迁事务专用] — 提交侧调用 (era+1) 打开新时代节点，
    /// 失败回滚由事务日志以快照旧值回调本方法；除 C-20 事务外无调用方。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">eraIndex outside 1..6.</exception>
    public void SetEraIndex(int eraIndex)
    {
        if (eraIndex is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(eraIndex), eraIndex, "Era index must be 1..6 (E1=1 … 终章=6).");
        }

        _eraIndex = eraIndex;
    }

    /// <summary>
    /// The frozen research cost of one node under one merged relief share
    /// (F-059 接入点). [T-074 小扩展：C-20 Legacy.StackingRule 作为唯一咽喉
    /// 合并残卷/奇观/纪念馆减免后的份额在此作用于公式总价；份额已含 50%
    /// 下限钳制，本方法只做 [0,0.5] 域校验与乘法。]
    /// </summary>
    /// <exception cref="ArgumentNullException">node is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">reliefShare outside [0,0.5].</exception>
    public long ResearchCost(TechNodeSpec node, double reliefShare)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!double.IsFinite(reliefShare) || reliefShare is < 0 or > 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(reliefShare), reliefShare, "Relief share must be in [0,0.5] (StackingRule 钳制口径).");
        }

        return (long)Math.Round(RawCost(node.CostSum, UnlockedCount) * (1.0 - reliefShare), MidpointRounding.AwayFromZero);
    }

    /// <summary>The single installment slot; null when no research is running (F-050 分期).</summary>
    public ActiveResearch? Active => _active;

    /// <summary>Level ids captured (占领) so far — the 关卡绑定 gate input.</summary>
    public IReadOnlyList<string> CapturedLevelIds => _capturedLevels.ToList();

    /// <summary>Node lookup by id; null when unknown.</summary>
    public TechNodeSpec? Find(string nodeId) =>
        _nodes.TryGetValue(nodeId, out TechNodeSpec? node) ? node : null;

    /// <summary>
    /// The frozen research cost of one node right now (whole items,
    /// midpoint-away-from-zero): <c>60 + max(0,Σ成本)^1.11 × 20 ×
    /// (1+0.02×已解锁数)</c>. Grows strictly with every unlock.
    /// </summary>
    /// <exception cref="ArgumentNullException">node is null.</exception>
    public long ResearchCost(TechNodeSpec node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return (long)Math.Round(RawCost(node.CostSum, UnlockedCount), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Raw (unrounded) formula value — public so tests can verify the
    /// rounding boundary against the exact double, and hosts can show the
    /// precise number.
    /// </summary>
    public static double RawCost(long costSum, int unlockedCount) =>
        CostBaseTerm + (Math.Pow(Math.Max(0.0, costSum), CostExponent) * CostScale
            * (1.0 + (CostPerUnlockedFactor * unlockedCount)));

    /// <summary>
    /// Splits a locked total across a cost mix deterministically: every
    /// line but the LAST takes <c>floor(total × share)</c>, the last line
    /// absorbs the remainder — the split always sums to the total exactly,
    /// same total + same mix ⇒ same split (no largest-remainder ordering
    /// ambiguity).
    /// <para>
    /// 超载守卫（REV-20261014-wave3-round2 #6）：本方法是公开静态缝隙，可被
    /// 宿主/测试以任意手工构造的 mix 直调，不经 <see cref="TechTreeData.Parse"/>
    /// 的装载校验。shareSum &gt; 1+1e-6 时非末行分配额之和可超过 total，末行
    /// 吸收项 <c>total − allocated</c> 变负（负扣费=逆向充值漏洞）——与装载侧
    /// 同口径的 1e-6 浮点容差下直接拒载。欠载（&lt; 1−1e-6）侧由装载校验把守，
    /// 直调侧末行吸收仍精确回 total，无金额漏洞，不在此重复设卡。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">mix is null or empty.</exception>
    /// <exception cref="ArgumentException">mix is empty, or its shares sum to more than 1 + 1e-6 (over-allocation would make the last line's charge negative).</exception>
    public static IReadOnlyList<(ResourceKey Resource, long Charge)> SplitCost(
        long total, IReadOnlyList<TechCostLine> mix)
    {
        ArgumentNullException.ThrowIfNull(mix);
        if (mix.Count == 0)
        {
            throw new ArgumentException("A cost mix needs at least one line.", nameof(mix));
        }

        double shareSum = 0.0;
        foreach (TechCostLine line in mix)
        {
            shareSum += line.Share;
        }

        if (shareSum > 1.0 + 1e-6)
        {
            throw new ArgumentException(
                $"Cost-mix shares sum to {shareSum} — must not exceed 1 (+1e-6 float tolerance); "
                + "the last line's remainder-absorb would go negative (over-allocation).",
                nameof(mix));
        }

        var split = new List<(ResourceKey, long)>(mix.Count);
        long allocated = 0;
        for (int i = 0; i < mix.Count - 1; i++)
        {
            long charge = (long)Math.Floor(total * mix[i].Share);
            split.Add((mix[i].Resource, charge));
            allocated += charge;
        }

        split.Add((mix[^1].Resource, total - allocated));
        return split;
    }

    /// <summary>
    /// Registers a captured (占领) level id — feeds the 关卡绑定 gate.
    /// </summary>
    /// <exception cref="ArgumentNullException">levelId is null.</exception>
    /// <exception cref="ArgumentException">levelId blank.</exception>
    /// <exception cref="InvalidOperationException">levelId already captured.</exception>
    public void RegisterCapture(string levelId)
    {
        ArgumentNullException.ThrowIfNull(levelId);
        if (string.IsNullOrWhiteSpace(levelId))
        {
            throw new ArgumentException("Level id must not be blank.", nameof(levelId));
        }

        if (!_capturedLevels.Add(levelId.Trim()))
        {
            throw new InvalidOperationException($"Level {levelId} is already captured.");
        }
    }

    /// <summary>
    /// Inspects every gate of one node without side effects.
    /// </summary>
    /// <exception cref="ArgumentException">nodeId unknown.</exception>
    public TechGateReport CheckGates(string nodeId)
    {
        TechNodeSpec node = RequireNode(nodeId);

        var missingPrereqs = new List<string>();
        foreach (string prerequisite in node.Prerequisites)
        {
            if (!_unlockedInOrder.Contains(prerequisite))
            {
                missingPrereqs.Add(prerequisite);
            }
        }

        var missingMaterials = new List<TechMaterial>();
        foreach (TechMaterial material in node.Materials)
        {
            if (_pool.AmountOf(material.Resource) < material.Amount)
            {
                missingMaterials.Add(material);
            }
        }

        var uncaptured = new List<string>();
        foreach (string levelId in node.BoundLevelIds)
        {
            if (!_capturedLevels.Contains(levelId))
            {
                uncaptured.Add(levelId);
            }
        }

        return new TechGateReport(
            nodeId,
            missingPrereqs.Count == 0,
            missingMaterials.Count == 0,
            uncaptured.Count == 0,
            node.EraIndex <= _eraIndex,
            _active is null,
            !_unlockedInOrder.Contains(nodeId),
            missingPrereqs,
            missingMaterials,
            uncaptured);
    }

    /// <summary>
    /// Begins researching a node (F-050): every gate must pass, then the
    /// material lines are withdrawn from the pool (commitment model) and
    /// the installment slot opens with the formula cost locked at the
    /// CURRENT unlock count. Refusals name the blocking gate in the
    /// exception message.
    /// </summary>
    /// <exception cref="ArgumentException">nodeId unknown.</exception>
    /// <exception cref="InvalidOperationException">Any gate failed, or materials vanished mid-withdrawal (rolled back).</exception>
    public void BeginResearch(string nodeId) => BeginResearch(nodeId, reliefShare: 0.0);

    /// <summary>
    /// Begins researching a node under one merged relief share
    /// (F-059/F.3): the installment total locks at
    /// <see cref="ResearchCost(TechNodeSpec, double)"/>.
    /// [T-074 小扩展：C-20 Legacy.StackingRule 唯一咽喉的 Progression 接入点；
    /// 缺省 0 保持原行为。]
    /// </summary>
    /// <exception cref="ArgumentException">nodeId unknown, or reliefShare outside [0,0.5].</exception>
    /// <exception cref="InvalidOperationException">Any gate failed, or materials vanished mid-withdrawal (rolled back).</exception>
    public void BeginResearch(string nodeId, double reliefShare)
    {
        TechNodeSpec node = RequireNode(nodeId);
        TechGateReport report = CheckGates(nodeId);
        if (!report.CanBegin)
        {
            throw new InvalidOperationException(RefusalMessage(report));
        }

        var withdrawn = new List<(ResourceKey Resource, double Amount)>();
        foreach (TechMaterial material in node.Materials)
        {
            double taken = _pool.TryWithdraw(material.Resource, material.Amount);
            if (taken < material.Amount)
            {
                foreach ((ResourceKey resource, double amount) in withdrawn)
                {
                    _ = _pool.Deposit(resource, amount);
                }

                _ = _pool.Deposit(material.Resource, taken);
                throw new InvalidOperationException(
                    $"Material withdrawal for {nodeId} came up short ({taken} of {material.Amount} {material.Resource.Value}) — rolled back.");
            }

            withdrawn.Add((material.Resource, material.Amount));
        }

        long total = ResearchCost(node, reliefShare);
        var balances = new List<TechCostBalance>();
        foreach ((ResourceKey resource, long charge) in SplitCost(total, node.CostMix))
        {
            balances.Add(new TechCostBalance(resource, charge, 0.0));
        }

        _active = new ActiveResearch(nodeId, total, balances, 0.0);
    }

    /// <summary>
    /// Pays one installment of one resource toward the active research
    /// (F-051 分期付款): withdraws from the pool (a short pool credits what
    /// it has — partial installments are legal), the line completes when
    /// its locked charge is covered, and the NODE unlocks the moment every
    /// mix line is paid off. Over-pay above a line's remaining balance is
    /// refused, never charged.
    /// </summary>
    /// <exception cref="ArgumentNullException">resource is null.</exception>
    /// <exception cref="ArgumentException">amount not finite or ≤ 0, or the resource is not part of the active node's mix.</exception>
    /// <exception cref="InvalidOperationException">No active research.</exception>
    public InstallmentResult PayInstallment(ResourceKey resource, double amount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!double.IsFinite(amount) || amount <= 0)
        {
            throw new ArgumentException($"Installment must be finite and positive (got {amount}).", nameof(amount));
        }

        if (_active is null)
        {
            throw new InvalidOperationException("No research is active — begin one before paying.");
        }

        int line = Array.FindIndex(_active.Balances.ToArray(), balance => balance.Resource.Equals(resource));
        if (line < 0)
        {
            throw new ArgumentException(
                $"Resource {resource.Value} is not part of the active research's cost mix.", nameof(resource));
        }

        ActiveResearch research = _active;
        TechCostBalance balance = research.Balances[line];
        double lineRemaining = Math.Max(0.0, balance.Charge - balance.Paid);
        double chargeable = Math.Min(amount, lineRemaining);
        double applied = _pool.TryWithdraw(resource, chargeable);
        var balances = research.Balances.ToArray();
        balances[line] = balance with { Paid = balance.Paid + applied };
        double paid = research.Paid + applied;
        bool completed = balances.All(entry => entry.Paid >= entry.Charge);
        _active = completed ? null : research with { Balances = balances, Paid = paid };
        if (completed)
        {
            _unlockedInOrder.Add(research.NodeId);
        }

        double remaining = Math.Max(0.0, balances.Sum(entry => Math.Max(0.0, entry.Charge - entry.Paid)));
        return new InstallmentResult(amount, applied, amount - chargeable, paid, remaining, completed);
    }

    private TechNodeSpec RequireNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("Node id must not be blank.", nameof(nodeId));
        }

        return Find(nodeId) ?? throw new ArgumentException($"Unknown tech node {nodeId}.", nameof(nodeId));
    }

    private static string RefusalMessage(TechGateReport report) =>
        !report.NotYetUnlocked ? $"Node {report.NodeId} is already unlocked."
        : !report.TopologyPassed ? $"Topology gate: {report.NodeId} misses prerequisites {Join(report.MissingPrerequisites)}."
        : !report.MaterialsPassed ? $"Material gate: {report.NodeId} misses materials {Join(report.MissingMaterials.Select(m => $"{m.Amount}×{m.Resource.Value}"))}."
        : !report.LevelBindingPassed ? $"Level-binding gate: {report.NodeId} requires captured levels {Join(report.UncapturedLevels)}."
        : !report.EraPassed ? $"Era gate: {report.NodeId} belongs to a later era."
        : !report.SlotFree ? "Installment slot busy: pay off the active research first (同时至多 1 节点)."
        : $"Node {report.NodeId} is not researchable.";

    private static string Join(IEnumerable<string> parts) => string.Join(", ", parts);
}
