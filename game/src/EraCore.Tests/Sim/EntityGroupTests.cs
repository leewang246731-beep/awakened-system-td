using EraCore.Sim.EntityGroups;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-155] 类型化分组单元（T-067；G.1 条 2 落地语义）：紧密数组追加序、
// swap-remove O(1) 语义、迭代索引修正（RemoveWhere/手写循环重访换入者）、
// IReadOnlyList 公共面、边界与非法输入。
public class EntityGroupTests
{
    private static Unit Unit(long id) => new(new EraCore.Contracts.Foundation.EntityId(id), "t:u", 0f, 0f, 1, 0, 1f);

    private static EntityGroup<Unit> Filled(int count)
    {
        var group = new EntityGroup<Unit>();
        for (int i = 0; i < count; i++)
        {
            group.Add(Unit(i + 1));
        }

        return group;
    }

    [Fact] // legal — 追加保持注册序（分组迭代序=命令流的确定性面）
    public void Add_appends_in_registration_order()
    {
        var group = Filled(5);
        Assert.Equal(5, group.Count);
        long[] ids = group.Select(u => u.Id.Value).ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact] // legal — 容量按 ×2 增长，越过初容 16 后仍保持序
    public void Capacity_growth_preserves_order()
    {
        var group = Filled(40);
        Assert.Equal(40, group.Count);
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(i + 1, group[i].Id.Value);
        }
    }

    [Fact] // legal — swap-remove O(1) 语义：尾元素换入被删槽，Count 减一，返回被删者
    public void RemoveAtSwap_moves_last_into_the_removed_slot()
    {
        var group = Filled(5);
        Unit removed = group.RemoveAtSwap(1); // 删 id=2，id=5 换入槽 1
        Assert.Equal(2L, removed.Id.Value);
        Assert.Equal(4, group.Count);
        Assert.Equal(1L, group[0].Id.Value);
        Assert.Equal(5L, group[1].Id.Value); // 换入者
        Assert.Equal(3L, group[2].Id.Value);
    }

    [Fact] // legal — 尾删：换入者即自身槽位，等价于弹出
    public void RemoveAtSwap_on_the_last_element_pops()
    {
        var group = Filled(3);
        Unit removed = group.RemoveAtSwap(2);
        Assert.Equal(3L, removed.Id.Value);
        Assert.Equal(2, group.Count);
        Assert.Equal(1L, group[0].Id.Value);
        Assert.Equal(2L, group[1].Id.Value);
    }

    [Fact] // boundary — 空组删除/越界访问必须拒绝
    public void Empty_group_rejects_removal_and_indexing()
    {
        var group = new EntityGroup<Unit>();
        Assert.True(group.Count == 0, $"空组 Count 应为 0（实际 {group.Count}）");
        Assert.Throws<ArgumentOutOfRangeException>(() => group.RemoveAtSwap(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = group[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => group.RemoveAtSwap(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = group[-1]);
    }

    [Fact] // boundary — 删空后的槽位访问仍以 Count 为界（紧密数组不变量）
    public void Removed_slot_is_inaccessible_after_count_shrinks()
    {
        var group = Filled(2);
        _ = group.RemoveAtSwap(0);
        Assert.True(group.Count == 1, $"删除后 Count 应为 1（实际 {group.Count}）");
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = group[1]);
    }

    [Fact] // illegal — null 实体禁止入组（分组承载非空实体，GC 槽位置 null 是内部细节）
    public void Null_member_is_rejected_on_every_mutator()
    {
        var group = new EntityGroup<Unit>();
        Assert.Throws<ArgumentNullException>(() => group.Add(null!));
        Assert.Throws<ArgumentNullException>(() => group.ReplaceAt(0, null!));
        group.Add(Unit(1));
        Assert.Throws<ArgumentNullException>(() => group.ReplaceAt(0, null!));
        Assert.Throws<ArgumentNullException>(() => group.RemoveWhere(null!));
        Assert.Throws<ArgumentNullException>(() => group.AddRange(null!));
        Assert.Throws<ArgumentNullException>(() => group.AddRange(new Unit?[] { null }!));
    }

    [Fact] // legal — 迭代中删除的索引修正：RemoveWhere 访问每个幸存者恰一次
    public void RemoveWhere_visits_every_survivor_exactly_once_with_index_correction()
    {
        var group = Filled(10);
        var visited = new List<long>();

        int removed = group.RemoveWhere(u =>
        {
            visited.Add(u.Id.Value);
            return u.Id.Value % 2 == 0; // 删除全部偶数 id
        });

        Assert.Equal(5, removed);
        Assert.Equal(5, group.Count);
        // 幸存者=奇数 id 全体（swap-remove 的机械序是确定性的：换入者占被删槽）
        Assert.Equal(new long[] { 1, 3, 5, 7, 9 }, group.Select(u => u.Id.Value).OrderBy(id => id).ToArray());

        // 迭代序证明换入者被重访：偶数 id 只在换入前被访问过一次（remove 判定
        // 本身已计入访问）；奇数从未重复、从未跳过
        Assert.Equal(10, visited.Count); // 每个元素至少一次（含被删者）
        Assert.Equal(5, visited.Count(id => id % 2 == 0));
        foreach (long odd in new long[] { 1, 3, 5, 7, 9 })
        {
            Assert.Equal(1, visited.Count(id => id == odd));
        }
    }

    [Fact] // legal — 手写循环的索引修正协议（i-- 重访换入者）与 RemoveWhere 等价
    public void Hand_written_loop_with_index_correction_matches_remove_where()
    {
        var a = Filled(12);
        var b = Filled(12);
        static bool Predicate(Unit u) => u.Id.Value is 3 or 7 or 11 or 12;

        int removedA = a.RemoveWhere(Predicate);

        int removedB = 0;
        for (int i = 0; i < b.Count; i++)
        {
            if (Predicate(b[i]))
            {
                _ = b.RemoveAtSwap(i);
                removedB++;
                i--; // 迭代索引修正：换入者未访问过，本槽位需重访
            }
        }

        Assert.Equal(removedA, removedB);
        // 同一判定下两条路径产生同一幸存序列（id 对照——两组各自 new 的实例不共享引用）
        Assert.Equal(
            a.Select(u => u.Id.Value).ToArray(),
            b.Select(u => u.Id.Value).ToArray());
        Assert.Equal(12 - removedA, a.Count);
    }

    [Fact] // legal — 全删空 → 空组；再追加继续工作（槽位置 null 的 GC 不变量）
    public void Removing_everything_empties_the_group_and_refill_works()
    {
        var group = Filled(8);
        Assert.Equal(8, group.RemoveWhere(_ => true));
        Assert.True(group.Count == 0, $"全删后 Count 应为 0（实际 {group.Count}）");

        group.Add(Unit(99));
        Assert.True(group.Count == 1, $"再追加后 Count 应为 1（实际 {group.Count}）");
        Assert.Equal(99L, group[0].Id.Value);
    }

    [Fact] // legal — ReplaceAt 原位替换：序与 Count 不变（升级同格同 id 换行的分组面）
    public void ReplaceAt_swaps_in_place_keeping_order_and_count()
    {
        var group = Filled(3);
        Unit replacement = Unit(3); // 同 id 的新实例
        group.ReplaceAt(2, replacement);
        Assert.Equal(3, group.Count);
        Assert.Same(replacement, group[2]);
        Assert.Equal(1L, group[0].Id.Value);
        Assert.Equal(2L, group[1].Id.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => group.ReplaceAt(3, replacement));
    }

    [Fact] // legal — Contains 走引用相等；IReadOnlyList/枚举器公共面一致
    public void Public_surface_behaves_as_readonly_list()
    {
        IReadOnlyList<Unit> view = Filled(3);
        Assert.Equal(3, view.Count);
        using IEnumerator<Unit> enumerator = view.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1L, enumerator.Current.Id.Value);

        var group = (EntityGroup<Unit>)view;
        Unit first = group[0];
        Assert.True(group.Contains(first));
        Assert.False(group.Contains(Unit(1))); // 同 id 不同实例=不含
        _ = group.RemoveAtSwap(0);
        Assert.False(group.Contains(first));
    }

    [Fact] // legal — AddRange 批量注册保序（波次批次物化路径）
    public void AddRange_appends_batch_in_source_order()
    {
        var group = Filled(2);
        Unit[] batch = { Unit(3), Unit(4), Unit(5) };
        group.AddRange(batch);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, group.Select(u => u.Id.Value).ToArray());
    }
}
