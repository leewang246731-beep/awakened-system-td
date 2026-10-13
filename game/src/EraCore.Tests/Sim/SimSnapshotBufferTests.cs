using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001] SimSnapshotBuffer：K-6 双缓冲产生者——Stage→SwapBuffers 两段
// 式发布，表现层只读 Latest/Previous 完整帧（03-decomposition §1.2 快照原则）。
// T-040。
public class SimSnapshotBufferTests
{
    private static ImmutableSimSnapshot Snap(long tick, long hash) =>
        new(tick, Array.Empty<IUnit>(), Array.Empty<IProjectile>(), Array.Empty<IBuilding>(), hash);

    [Fact] // legal — Stage→Swap 提升 Latest；再一帧后 Previous 保留上一完整帧
    public void Stage_then_swap_publishes_latest_and_keeps_previous()
    {
        var buffer = new SimSnapshotBuffer();
        Assert.Null(buffer.Latest);
        Assert.Null(buffer.Previous);
        Assert.False(buffer.HasStaged);

        buffer.Stage(Snap(1, 11));
        Assert.True(buffer.HasStaged);
        Assert.Null(buffer.Latest); // 未 swap 不发布半成品

        buffer.SwapBuffers();
        Assert.NotNull(buffer.Latest);
        Assert.Equal(1, buffer.Latest!.Tick);
        Assert.Equal(11, buffer.Latest.StateHash);
        Assert.Null(buffer.Previous);

        buffer.Stage(Snap(2, 22));
        buffer.SwapBuffers();
        Assert.Equal(2, buffer.Latest!.Tick);
        Assert.Equal(22, buffer.Latest.StateHash);
        Assert.NotNull(buffer.Previous);
        Assert.Equal(1, buffer.Previous!.Tick); // 上一完整帧可回看
        Assert.False(buffer.HasStaged);
    }

    [Fact] // boundary — 无 Stage 的 Swap 是 no-op（无内容可提升）
    public void Swap_without_stage_is_a_no_op()
    {
        var buffer = new SimSnapshotBuffer();
        buffer.SwapBuffers();
        Assert.Null(buffer.Latest);
        Assert.Null(buffer.Previous);

        buffer.Stage(Snap(7, 77));
        buffer.SwapBuffers();
        buffer.SwapBuffers(); // 第二次无 Stage：Latest 不变
        Assert.Equal(7, buffer.Latest!.Tick);
        Assert.Null(buffer.Previous);
    }

    [Fact] // boundary — Reset 清空三槽（关卡重开复用同一 buffer）
    public void Reset_clears_every_slot()
    {
        var buffer = new SimSnapshotBuffer();
        buffer.Stage(Snap(1, 11));
        buffer.SwapBuffers();
        buffer.Stage(Snap(2, 22));

        buffer.Reset();
        Assert.Null(buffer.Latest);
        Assert.Null(buffer.Previous);
        Assert.False(buffer.HasStaged);
    }

    [Fact] // illegal — Stage(null) 被拒
    public void Stage_rejects_null_snapshots()
    {
        var buffer = new SimSnapshotBuffer();
        Assert.Throws<ArgumentNullException>(() => buffer.Stage(null!));
    }

    [Fact] // legal — 视图记录是深只读值拷贝（改源数组不影响已发布帧）
    public void Published_views_are_value_copies()
    {
        var units = new IUnit[] { new SimUnitView(new EntityId(1), "era1:enemy-beastling", 1f, 2f, 40, 40, 0, 0, 1.6f) };
        var snapshot = new ImmutableSimSnapshot(3, units, Array.Empty<IProjectile>(), Array.Empty<IBuilding>(), 99);
        var buffer = new SimSnapshotBuffer();
        buffer.Stage(snapshot);
        buffer.SwapBuffers();

        units[0] = null!; // 篡改调用侧数组
        Assert.NotNull(buffer.Latest!.Units[0]);
        Assert.Equal(3, buffer.Latest.Tick);
        Assert.Equal(99, buffer.Latest.StateHash);
    }
}
