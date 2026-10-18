using EraCore.Contracts.Sim;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [F-161] T-139 队列 tap（宿主广播捕获缝）：Enqueued 事件按入队序携带
// (seq, command)；观察者纯只读契约（不突变队列/不抛）；无订阅者时行为
// 与加缝前逐位一致（零耦合纪律的 Sim 侧证明面——单机路径永不订阅）。
public class SimCommandQueueTapTests
{
    private static SimCommand Command(long tick) => new(
        tick, SimCommandKind.PlayerBuild, "{\"verb\":\"build-labor-camp\",\"cell\":{\"x\":4,\"y\":20}}");

    [Fact] // legal — tap 依入队序携带 seq 与命令原文
    public void Tap_fires_per_enqueue_with_assigned_seq_and_verbatim_command()
    {
        var queue = new SimCommandQueue();
        var seen = new List<(long Seq, SimCommand Command)>();
        queue.Enqueued += (seq, command) => seen.Add((seq, command));

        long first = queue.Enqueue(Command(30));
        long second = queue.Enqueue(Command(10));

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, seen.Count);
        Assert.Equal((1L, 30L), (seen[0].Seq, seen[0].Command.IssuedAtTick));
        Assert.Equal((2L, 10L), (seen[1].Seq, seen[1].Command.IssuedAtTick));
        Assert.Equal(seen[0].Command, Command(30)); // 命令记录逐字段原样（payload 逐字节）
    }

    [Fact] // legal — 无订阅者（=单机路径）行为不变：seq/待处理/出队结果一致
    public void Without_subscribers_enqueue_behaviour_is_unchanged()
    {
        var withTap = new SimCommandQueue();
        var withoutTap = new SimCommandQueue();
        int tapFires = 0;
        withTap.Enqueued += (_, _) => tapFires++;

        long seqA = withTap.Enqueue(Command(5));
        long seqB = withoutTap.Enqueue(Command(5));

        Assert.Equal(seqA, seqB);
        Assert.Equal(1, tapFires);
        Assert.Equal(withoutTap.PendingCount, withTap.PendingCount);
        Assert.Equal(withoutTap.LastSeq, withTap.LastSeq);

        IReadOnlyList<SimCommandQueue.Entry> dueA = withTap.DequeueDue(5);
        IReadOnlyList<SimCommandQueue.Entry> dueB = withoutTap.DequeueDue(5);
        Assert.Equal(dueB.Count, dueA.Count);
        Assert.Equal(dueB[0].Command, dueA[0].Command);
        Assert.Equal(dueB[0].Seq, dueA[0].Seq);
    }

    [Fact] // boundary — 观察者不得影响排序：乱序 tick 入队后 tap 序=入队序，DequeueDue 仍按 (tick,seq) 全序
    public void Tap_order_is_enqueue_order_while_due_order_stays_tick_then_seq()
    {
        var queue = new SimCommandQueue();
        var tapOrder = new List<long>();
        queue.Enqueued += (_, command) => tapOrder.Add(command.IssuedAtTick);

        queue.Enqueue(Command(50));
        queue.Enqueue(Command(20));
        queue.Enqueue(Command(40));

        Assert.Equal(new long[] { 50, 20, 40 }, tapOrder); // tap=入队序（广播期刊口径）
        IReadOnlyList<SimCommandQueue.Entry> due = queue.DequeueDue(50);
        Assert.Equal(new long[] { 20, 40, 50 }, due.Select(entry => entry.Command.IssuedAtTick)); // 出队=全序
    }

    [Fact] // boundary — 拒收路径（非法参数）不触发 tap：未入队成功的事件不存在
    public void Rejected_enqueue_never_fires_the_tap()
    {
        var queue = new SimCommandQueue();
        int fires = 0;
        queue.Enqueued += (_, _) => fires++;

        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(new SimCommand(-1, SimCommandKind.PlayerBuild, "{}")));

        Assert.Equal(0, fires);
        Assert.Equal(0, queue.LastSeq);
    }
}
