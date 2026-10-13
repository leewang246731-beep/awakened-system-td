using EraCore.Contracts.Sim;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001] SimCommandQueue：(tick, seq) 全序入队/按期出队——表现层与
// 系统动作进入确定性核心的唯一通道（03-decomposition §1.2）。T-040。
public class SimCommandQueueTests
{
    private static SimCommand Cmd(long tick, string payload) =>
        new(tick, SimCommandKind.PlayerBuild, payload);

    [Fact] // legal — 乱序入队后按 (IssuedAtTick, seq) 全序出队
    public void DequeueDue_returns_commands_in_tick_then_seq_order()
    {
        var queue = new SimCommandQueue();
        long seqA = queue.Enqueue(Cmd(10, "a"));
        long seqB = queue.Enqueue(Cmd(5, "b"));
        long seqC = queue.Enqueue(Cmd(10, "c"));
        long seqD = queue.Enqueue(Cmd(5, "d"));

        Assert.Equal(1, seqA);
        Assert.Equal(4, seqD); // seq 单调 issued

        var atFive = queue.DequeueDue(5);
        Assert.Equal(2, atFive.Count);
        Assert.Equal("b", atFive[0].Command.PayloadJson);
        Assert.Equal(seqB, atFive[0].Seq);
        Assert.Equal("d", atFive[1].Command.PayloadJson);

        var atTen = queue.DequeueDue(10);
        Assert.Equal(2, atTen.Count);
        Assert.Equal("a", atTen[0].Command.PayloadJson); // 同 tick 按 seq
        Assert.Equal("c", atTen[1].Command.PayloadJson);

        Assert.Equal(0, queue.PendingCount);
        Assert.Empty(queue.DequeueDue(100));
    }

    [Fact] // legal — 未到期不出队；迟到的命令在下一轮一并出队
    public void DequeueDue_holds_future_commands_and_flushes_late_ones()
    {
        var queue = new SimCommandQueue();
        queue.Enqueue(Cmd(50, "future"));
        Assert.Empty(queue.DequeueDue(49));
        Assert.Equal(1, queue.PendingCount);

        var due = queue.DequeueDue(80); // 迟到命令（tick 50 < 80）合法入账
        Assert.Single(due);
        Assert.Equal(50, due[0].Command.IssuedAtTick);
    }

    [Fact] // boundary — 空队列出队恒空，LastSeq 从 0 起
    public void Empty_queue_dequeues_nothing()
    {
        var queue = new SimCommandQueue();
        Assert.Equal(0, queue.LastSeq);
        Assert.Empty(queue.DequeueDue(long.MaxValue));
    }

    [Fact] // illegal — null 命令与负 IssuedAtTick 被拒
    public void Enqueue_rejects_null_and_negative_tick()
    {
        var queue = new SimCommandQueue();
        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(Cmd(-1, "x")));
        Assert.Equal(0, queue.PendingCount); // 拒绝后队列未受污染
    }
}
