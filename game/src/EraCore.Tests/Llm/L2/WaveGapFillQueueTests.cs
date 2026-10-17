using EraCore.Llm.L2;
using Xunit;

namespace EraCore.Tests.Llm.L2;

// [F-078] [F-163] T-137 波间异步队列：入队/开窗/单飞/超时放弃/迟到结果拒收/
// 清空。核心断言面=「不阻塞」——队列全部方法同步即返，填充任务在宿主线程
// 之外跑（超时=放弃本轮不卡波的判据）。
public class WaveGapFillQueueTests
{
    private static (WaveGapFillQueue Queue, Func<long> ClockRef) MakeQueue(long startMs = 0)
    {
        long now = startMs;
        var queue = new WaveGapFillQueue(() => now);
        return (queue, () => now);
    }

    // ------------------------------------------------ 入队与开窗

    [Fact] // L1 澄清军令入队 → 波间开窗取队头单飞（一次仅一道）
    public void Enqueue_then_open_window_promotes_the_head_as_single_in_flight()
    {
        var (queue, _) = MakeQueue();

        Assert.NotNull(queue.Enqueue("在东门建两座箭塔，坐标 (12,5) 和 (13,5)"));
        L2QueuedFill? second = queue.Enqueue("兽崽冲进东门了，全军集火 3号敌人");

        Assert.NotNull(second);
        Assert.Equal(2, queue.Count);

        L2QueuedFill? first = queue.OpenWindow();
        Assert.NotNull(first);
        Assert.Equal(1, first!.Id);
        Assert.Equal("在东门建两座箭塔，坐标 (12,5) 和 (13,5)", first.OrderText);

        // 单飞：开窗不并发放第二道
        Assert.Null(queue.OpenWindow());
        Assert.Single(queue.Waiting);
        Assert.Same(first, queue.InFlight);
    }

    [Fact] // 空白军令不入队（无文本无可填充面）
    public void Blank_orders_are_refused()
    {
        var (queue, _) = MakeQueue();

        Assert.Null(queue.Enqueue(""));
        Assert.Null(queue.Enqueue("   "));
        Assert.Equal(0, queue.Count);
    }

    [Fact] // 空队列开窗 → null（波间无事可做）
    public void Opening_an_empty_window_yields_null()
    {
        var (queue, _) = MakeQueue();

        Assert.Null(queue.OpenWindow());
    }

    // ------------------------------------------------ 超时=放弃本轮

    [Fact] // （判据面）超时路径全序：开窗→时钟过线→开窗发现过期→清队→迟到 Complete 返回 null
    public void Timeout_sequence_abandons_and_refuses_the_late_answer()
    {
        long now = 0;
        var queue = new WaveGapFillQueue(() => now);

        queue.Enqueue("两分钟后提醒我升级箭塔");
        L2QueuedFill? started = queue.OpenWindow();
        Assert.NotNull(started);
        long deadline = queue.InFlightDeadlineMs;
        Assert.Equal(WaveGapFillQueue.BudgetMs, deadline); // budget = frozen 25 s

        // Not yet expired at deadline-1; expired at deadline.
        now = deadline - 1;
        Assert.False(queue.InFlightExpired);
        now = deadline;
        Assert.True(queue.InFlightExpired);

        L2QueuedFill? expired = queue.AbandonInFlight();
        Assert.Same(started, expired);
        Assert.Null(queue.InFlight);
        Assert.Equal(0, queue.Count);

        // The late answer arrives after abandonment → stale, refused.
        Assert.Null(queue.Complete(started!.Id, L2IntentFillResult.Succeeded("{\"action\":\"SetAlarm\"}")));
    }

    [Fact] // 中窗过期由宿主逐帧发现：InFlightExpired 轮询位翻转
    public void Host_frame_polling_observes_the_deadline_crossing()
    {
        long now = 10_000;
        var queue = new WaveGapFillQueue(() => now);

        queue.Enqueue("敌情怎么样");
        _ = queue.OpenWindow();

        Assert.False(queue.InFlightExpired);
        now = 10_000 + WaveGapFillQueue.BudgetMs - 1;
        Assert.False(queue.InFlightExpired);
        now += 1;
        Assert.True(queue.InFlightExpired);
    }

    // ------------------------------------------------ 完成与队列推进

    [Fact] // 完成后下一道等下一窗（不自动链启——宿主节奏 owns the cadence）
    public void Completion_frees_the_slot_but_the_next_order_waits_for_the_next_window()
    {
        var (queue, _) = MakeQueue();

        queue.Enqueue("第一道");
        queue.Enqueue("第二道");

        _ = queue.OpenWindow();
        L2FillCompletion? landed =
            queue.Complete(queue.InFlight!.Id, L2IntentFillResult.Succeeded("{\"action\":\"LookAround\"}"));

        Assert.NotNull(landed);
        Assert.Equal("第一道", landed!.Order.OrderText);
        Assert.True(landed.Result.Ok);

        Assert.Null(queue.InFlight);      // slot freed
        Assert.Single(queue.Waiting);    // but NOT auto-chained
        L2QueuedFill? next = queue.OpenWindow();
        Assert.NotNull(next);
        Assert.Equal("第二道", next!.OrderText);
    }

    [Fact] // 失败完成也走 Complete（含降级理由——宿主拿它出横幅文案）
    public void Failed_completion_carries_the_degradation_reason()
    {
        var (queue, _) = MakeQueue();

        queue.Enqueue("这道令模型没懂");
        _ = queue.OpenWindow();

        L2QueuedFill? started = queue.InFlight;
        L2FillCompletion? landed =
            queue.Complete(started!.Id, L2IntentFillResult.Failed("no_tool_call", L2DegradationReason.FillFailed));

        Assert.NotNull(landed);
        Assert.False(landed!.Result.Ok);
        Assert.Equal(L2DegradationReason.FillFailed, landed.Result.Degradation);
        Assert.Equal("no_tool_call", landed.Result.ErrorKey);
    }

    [Fact] // 空闲时 Complete 返回 null（无在飞单可落）
    public void Completing_with_nothing_in_flight_is_a_no_op()
    {
        var (queue, _) = MakeQueue();

        Assert.Null(queue.Complete(999, L2IntentFillResult.Succeeded("{}")));
    }

    [Fact] // Clear：档位切换/强制关窗 → 全清并报数
    public void Clear_drops_everything_and_reports_the_count()
    {
        var (queue, _) = MakeQueue();

        queue.Enqueue("一");
        queue.Enqueue("二");
        queue.Enqueue("三");
        _ = queue.OpenWindow();

        Assert.Equal(3, queue.Clear());
        Assert.Equal(0, queue.Count);
        Assert.Null(queue.InFlight);
        Assert.Null(queue.OpenWindow());
    }

    // ------------------------------------------------ 不阻塞断言（验收判据）

    [Fact] // 队列方法全部同步即返（无 Task 面）——模拟 tick 永不因队列等待
    public void Every_queue_operation_is_synchronous_by_construction()
    {
        // No queue method returns Task/Task<> — the queue can never make a
        // caller await anything (不阻塞模拟 tick 的结构保证).
        Assert.All(
            typeof(WaveGapFillQueue).GetMethods().Where(m => m.DeclaringType == typeof(WaveGapFillQueue)),
            m => Assert.False(
                m.ReturnType.IsAssignableTo(typeof(Task)),
                $"queue method {m.Name} must stay synchronous"));
    }

    [Fact] // 宿主形态（集成判据）：填充端口挂起期间，队列照常服务下一窗操作（不阻塞）
    public async Task Queue_stays_responsive_while_a_fill_port_is_in_flight()
    {
        var gate = new TaskCompletionSource<L2IntentFillResult>();
        IL2IntentFillPort hangingPort = new DelegatePort(_ => gate.Task);

        var (queue, _) = MakeQueue();
        queue.Enqueue("挂起这道");

        L2QueuedFill? started = queue.OpenWindow();
        Assert.NotNull(started);

        // Fire the fill WITHOUT awaiting it (the host's wave-gap lane), then
        // keep using the queue — it must not block on the port.
        Task<L2IntentFillResult> inFlight = hangingPort.FillAsync(started!.OrderText, CancellationToken.None);
        Assert.False(inFlight.IsCompleted);

        queue.Enqueue("后续军令照常入队");
        L2QueuedFill? nextWaiting = Assert.Single(queue.Waiting);
        Assert.Equal("后续军令照常入队", nextWaiting.OrderText);

        // Release the gate; the host's completion path still lands through Complete.
        gate.SetResult(L2IntentFillResult.Succeeded("{\"action\":\"LookAround\"}"));
        L2IntentFillResult landed = await inFlight;
        Assert.True(landed.Ok);
        L2FillCompletion? completed = queue.Complete(started.Id, landed);
        Assert.NotNull(completed);
        Assert.True(completed!.Result.Ok);
    }

    private sealed class DelegatePort(Func<string, Task<L2IntentFillResult>> fill) : IL2IntentFillPort
    {
        public Task<L2IntentFillResult> FillAsync(string orderText, CancellationToken cancellationToken) =>
            fill(orderText);
    }
}
