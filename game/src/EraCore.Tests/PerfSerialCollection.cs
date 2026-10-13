using Xunit;

namespace EraCore.Tests;

// W2 批 3 根因修复（F1）：今日 run_all 两红的根因已定性——Spike1/Spike2 与
// FlowFieldManagerTests 的 TierA 性能用例没有 [Collection] 标注，xUnit v2 按
// 类各建默认集合、集合间并行，计时敏感测试互相偷 CPU 墙钟（T-043 回归线
// 1.336×2.25 ms 的并发劣化容差按"单邻居"负载设计，多邻居叠加即超限——
// REV-20261014-wave2-round1 §四已实测留档过一次并发超限）。

/// <summary>
/// Exclusive serial collection for every wall-clock budget test (F1):
/// <see cref="Spikes.Spike1PerformanceTests"/>,
/// <see cref="Spikes.Spike2CoexistenceTests"/> and
/// <see cref="Pathfinding.FlowFieldManagerTests"/> (its TierA 300×300 flood
/// envelope case). xUnit v2 semantics
/// (<see href="https://xunit.net/docs/running-tests-in-parallel"/>): a
/// collection flagged <c>DisableParallelization = true</c> is "guaranteed
/// not to run in parallel against any other test" — stronger than a plain
/// collection, which only serializes its own members while still running
/// alongside other collections. That exclusivity is the point: the perf
/// medians must not share the CPU with the checkpoint suite, the spikes, or
/// any other collection.
/// <para>
/// 取舍（整类并入）：FlowFieldManagerTests 内非计时用例（分桶/LRU/脏区/切片
/// 等价性）一并进串行集合——它们是小图秒级逻辑用例（最大 256×256），串行
/// 代价可忽略；xUnit 集合粒度=类，单拆计时用例需要拆类，得不偿失。
/// </para>
/// <para>
/// CheckpointHeavyCollection 保持原样、不并入（读码裁定）：一个类只能属于
/// 一个集合，两类 Checkpoint/性能类互不相交故无归属冲突；perf-serial 的
/// DisableParallelization 使其独占运行（不与任何并行集合重叠），Checkpoint
/// 重负载与性能用例的互扰由该独占性消除，无需合并成巨型串行集合。
/// FlowFieldTests 未纳入：其 Stopwatch 仅出现于注释，切片等价用例断言的是
/// 预算切片机制（frames≥2）与逐位哈希相等，非墙钟数值，并行 CPU 偷取只会
/// 增加帧数不会翻转断言。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfSerialCollection
{
    public const string Name = "EraCore.Tests.PerfSerial";
}
