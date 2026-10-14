namespace EraCore.Sim.EntityGroups;

/// <summary>
/// One typed entity group of the simulation (C-12 T-067, F-155; G.1 条 2
/// 类型化分组数组): a dense <c>T[]</c> backing store with an active count —
/// entities are appended in registration order and removed by
/// <b>swap-remove</b> (the last live element moves into the removed slot),
/// so removal is O(1) with no per-element shifting and no allocation.
/// <para>
/// Iteration discipline (迭代索引修正): the group is iterated by plain index
/// <c>for (int i = 0; i &lt; group.Count; i++)</c>. When the body removes the
/// element at <c>i</c> via <see cref="RemoveAtSwap"/>, the element swapped
/// into slot <c>i</c> has NOT been visited yet — the caller must repeat the
/// same index (<c>i--</c> in a <c>for</c> loop). <see cref="RemoveWhere"/>
/// packages exactly that protocol; hand-written loops that remove must apply
/// the correction themselves (EntityGroupTests pins the semantics).
/// </para>
/// <para>
/// Determinism: append order is registration order and swap-remove is a pure
/// mechanical move, so group iteration order is a deterministic function of
/// the command stream — the state hash may iterate groups directly. The group
/// implements <see cref="IReadOnlyList{T}"/> so orchestrator public surfaces
/// that previously exposed <c>List&lt;T&gt;</c> keep their exact contract.
/// </para>
/// <para>
/// G.1 条 2 names one further member of the pattern — 对象池 (object pooling
/// of recycled entity instances). Entity allocation profiling has not shown
/// GC pressure on the 30 Hz tick path (spike①: 1.336 ms mean logic frame,
/// docs/engineering/spike1-report.md), so this milestone ships the grouping
/// arrays; pooling lands with the entity recycling profiling that justifies
/// it.
/// </para>
/// </summary>
/// <typeparam name="T">Reference entity type held by this group.</typeparam>
public sealed class EntityGroup<T> : IReadOnlyList<T>
    where T : class
{
    private const int InitialCapacity = 16;
    private T[] _items;
    private int _count;

    /// <summary>Creates an empty group with a small dense backing array.</summary>
    public EntityGroup()
    {
        _items = new T[InitialCapacity];
        _count = 0;
    }

    /// <summary>Number of live entities in the group.</summary>
    public int Count => _count;

    /// <summary>Dense element access — valid indices are <c>0 .. Count-1</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">index outside the live range.</exception>
    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must lie in [0,{_count}).");
            }

            return _items[index];
        }
    }

    /// <summary>Appends one entity at the live tail (registration order).</summary>
    /// <exception cref="ArgumentNullException">item is null.</exception>
    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[_count++] = item;
    }

    /// <summary>
    /// Appends a batch at the live tail in source order — the wave-spawner
    /// registration path (batch materialization order is the group order).
    /// </summary>
    /// <exception cref="ArgumentNullException">items is null or contains a null entry.</exception>
    public void AddRange(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (int i = 0; i < items.Count; i++)
        {
            Add(items[i]);
        }
    }

    /// <summary>
    /// Removes the entity at <paramref name="index"/> by swapping the LAST
    /// live element into its slot (O(1), no shift, no allocation). Iteration
    /// correction duty: the swapped-in element is unvisited — a loop removing
    /// at its own cursor must repeat the index (see class remarks).
    /// </summary>
    /// <returns>The removed entity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">index outside the live range.</exception>
    public T RemoveAtSwap(int index)
    {
        T removed = this[index]; // range check + read in one step
        _count--;
        _items[index] = _items[_count];
        _items[_count] = null!; // release the slot for the GC (dense array invariant)
        return removed;
    }

    /// <summary>
    /// Removes every entity matching <paramref name="predicate"/> using the
    /// canonical index-corrected sweep (swap-remove + repeat-index), so the
    /// visitor sees every surviving element exactly once regardless of how
    /// many removals interleave.
    /// </summary>
    /// <returns>Number of entities removed.</returns>
    /// <exception cref="ArgumentNullException">predicate is null.</exception>
    public int RemoveWhere(Predicate<T> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int removed = 0;
        for (int i = 0; i < _count; i++)
        {
            if (predicate(_items[i]))
            {
                _ = RemoveAtSwap(i);
                removed++;
                i--; // 迭代索引修正：换入者未访问过，本槽位需重访
            }
        }

        return removed;
    }

    /// <summary>Replaces the entity at <paramref name="index"/> in place (id/order preserved).</summary>
    /// <exception cref="ArgumentNullException">item is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">index outside the live range.</exception>
    public void ReplaceAt(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (index < 0 || index >= _count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must lie in [0,{_count}).");
        }

        _items[index] = item;
    }

    /// <summary>Determines whether the group holds this entity instance (reference equality).</summary>
    public bool Contains(T item) =>
        item is not null && Array.IndexOf(_items, item, 0, _count) >= 0;

    /// <summary>Dense-order enumerator over the live range.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _items[i];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
