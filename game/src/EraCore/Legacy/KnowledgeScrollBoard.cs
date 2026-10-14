namespace EraCore.Legacy;

/// <summary>
/// Result of reading one knowledge scroll (T-074, F-058/F-099): the
/// restored memory fragment (读残卷=恢复系统上一纪元的记忆) and the relief
/// the fragment grants on its bound tech node.
/// </summary>
/// <param name="ScrollId">Scroll id read.</param>
/// <param name="TechNode">Tech node the relief binds to.</param>
/// <param name="ReliefShare">Relief share (content row value).</param>
/// <param name="MemoryZh">Restored memory fragment, Chinese.</param>
/// <param name="MemoryEn">Restored memory fragment, English.</param>
public sealed record KnowledgeScrollRead(
    string ScrollId,
    string TechNode,
    double ReliefShare,
    string MemoryZh,
    string MemoryEn);

/// <summary>
/// Knowledge-scroll board (T-074, F-058/F-059): the F.3 enforcement of
/// 知识残卷同一科技节点至多 1 张 — the second scroll bound to an already
/// served node is rejected outright. The per-node relief flows out through
/// <see cref="StackingRule"/> (single choke) into the research pricing seam.
/// </summary>
public sealed class KnowledgeScrollBoard
{
    private readonly IReadOnlyDictionary<string, KnowledgeScrollSpec> _catalog;
    private readonly Dictionary<string, string> _reliefsByNode = new(StringComparer.Ordinal); // techNode → scrollId
    private readonly List<string> _readInOrder = new();

    /// <param name="table">Archaeology content table (the scroll catalog).</param>
    /// <exception cref="ArgumentNullException">table is null.</exception>
    public KnowledgeScrollBoard(ArchaeologyTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _catalog = table.Scrolls.ToDictionary(scroll => scroll.Id, scroll => scroll, StringComparer.Ordinal);
        if (_catalog.Count != table.Scrolls.Count)
        {
            throw new ArgumentException("The knowledge-scroll catalog carries duplicate ids.", nameof(table));
        }
    }

    /// <summary>Scroll ids in read order (deterministic ledger order).</summary>
    public IReadOnlyList<string> ReadScrollIds => _readInOrder;

    /// <summary>
    /// Reads one scroll (F-099: restores the memory fragment) and binds its
    /// relief to the scroll's tech node.
    /// </summary>
    /// <exception cref="ArgumentNullException">scrollId is null.</exception>
    /// <exception cref="ArgumentException">scrollId is blank or unknown.</exception>
    /// <exception cref="InvalidOperationException">
    /// The scroll was already read, or the node already has a scroll
    /// (F.3: 同一科技节点至多 1 张).
    /// </exception>
    public KnowledgeScrollRead Read(string scrollId)
    {
        if (string.IsNullOrWhiteSpace(scrollId))
        {
            throw new ArgumentException("Scroll id must not be blank.", nameof(scrollId));
        }

        string id = scrollId.Trim();
        if (!_catalog.TryGetValue(id, out KnowledgeScrollSpec? spec))
        {
            throw new ArgumentException($"Unknown knowledge scroll '{id}'.", nameof(scrollId));
        }

        if (_readInOrder.Contains(id))
        {
            throw new InvalidOperationException($"Knowledge scroll '{id}' was already read.");
        }

        if (_reliefsByNode.TryGetValue(spec.TechNode, out string? holder))
        {
            throw new InvalidOperationException(
                $"Tech node '{spec.TechNode}' already carries scroll '{holder}' — F.3: 同一科技节点至多 1 张.");
        }

        _reliefsByNode[spec.TechNode] = id;
        _readInOrder.Add(id);
        return new KnowledgeScrollRead(id, spec.TechNode, spec.ReliefShare, spec.MemoryZh, spec.MemoryEn);
    }

    /// <summary>True when the scroll was already read.</summary>
    public bool IsRead(string scrollId) => _readInOrder.Contains(scrollId);

    /// <summary>The relief share bound to one tech node (0 when none).</summary>
    public double ReliefFor(string techNode) =>
        _reliefsByNode.ContainsKey(techNode) ? _catalog[_reliefsByNode[techNode]].ReliefShare : 0.0;
}
