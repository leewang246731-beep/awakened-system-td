using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001][F-154] 确定性首证（T-040 核心验收）：同 seed+同命令流 → 逐 tick
// 状态哈希相等；不同 seed 在首个分叉 tick 后不等；命令插入时点不同 → 分叉且
// 分叉点=命令 tick（03-decomposition §1.2 回放不变式的编排级首证）。
public class SimDeterminismTests
{
    private const long Ticks = 1_800; // 60 s @ 30 Hz（任务卡口径）

    /// <summary>Runs one independent simulation and samples the hash EVERY tick.</summary>
    private static IReadOnlyList<long> HashTimeline(long seed, IReadOnlyList<Contracts.Sim.SimCommand>? commands = null)
    {
        var record = SimulationReplay.Capture(
            SimE1Fixture.Config(), new SimRng(seed), seed, commands ?? SimE1Fixture.MilestoneCommands(), Ticks,
            sampleEvery: 1);
        Assert.Equal(Ticks + 1, record.Samples.Count); // tick 0..1800
        return record.Samples.Select(s => s.StateHash).ToArray();
    }

    [Fact] // legal — ①同 seed 同命令流：两次独立运行逐 tick（1801 采样）全等
    public void Same_seed_and_command_stream_produce_identical_per_tick_hashes()
    {
        var first = HashTimeline(SimE1Fixture.Seed);
        var second = HashTimeline(SimE1Fixture.Seed);

        Assert.Equal(1_801, first.Count);
        Assert.Equal(second.Count, first.Count);
        Assert.Equal(first, second); // 逐 tick 全等——确定性首证主断言

        // 判别力自查：篡改任一采样后必不相等（防恒真空比较，CS-3.3）
        var tampered = second.ToArray();
        tampered[^1] = unchecked(tampered[^1] + 1);
        Assert.NotEqual(first, tampered);

        // 哈希在 tick 间真实推进（非恒定值）
        Assert.True(first.Distinct().Count() > 1_700, $"哈希几乎不随 tick 变化：{first.Distinct().Count()} 个不同值");
    }

    [Fact] // legal — ②不同 seed：首个分叉 tick（=0，地形哈希即分叉）之后哈希全不等
    public void Different_seed_diverges_from_the_first_fork_tick_onwards()
    {
        var baseline = HashTimeline(SimE1Fixture.Seed);
        var other = HashTimeline(SimE1Fixture.Seed + 1);

        int firstDivergence = 0;
        while (firstDivergence < baseline.Count && baseline[firstDivergence] == other[firstDivergence])
        {
            firstDivergence++;
        }

        Assert.InRange(firstDivergence, 0, baseline.Count - 1); // 必有分叉
        for (int t = firstDivergence; t < baseline.Count; t++)
        {
            Assert.NotEqual(baseline[t], other[t]); // 分叉后逐 tick 不等（地形哈希驻留在每 tick 聚合里）
        }
    }

    [Fact] // legal — ③命令插入时点不同：分叉点=命令 tick，其后持续不等
    public void Command_timing_shift_forks_exactly_at_the_command_tick()
    {
        const long earlyTick = 600;
        const long lateTick = 900;
        var early = HashTimeline(SimE1Fixture.Seed, new[] { SimCommands.BuildLaborCamp(earlyTick, SimE1Fixture.LaborCampCell) });
        var late = HashTimeline(SimE1Fixture.Seed, new[] { SimCommands.BuildLaborCamp(lateTick, SimE1Fixture.LaborCampCell) });

        // 分叉前（t < 600）：两条流逐 tick 相等
        for (int t = 0; t < earlyTick; t++)
        {
            Assert.Equal(early[t], late[t]);
        }

        // 分叉点=命令 tick：哈希在命令应用的 tick 末即不同
        Assert.NotEqual(early[(int)earlyTick], late[(int)earlyTick]);

        // 其后（含 lateTick 之后）持续不等：账本累计量（delivered/surplus）
        // 从分叉起单调漂移，永不回归
        for (int t = (int)earlyTick; t < early.Count; t++)
        {
            Assert.NotEqual(early[t], late[t]);
        }
    }

    [Fact] // legal — 回放验证器首证：Verify(记录) 对同 seed 重放返回全绿
    public void Replay_verifier_confirms_a_captured_run()
    {
        var record = SimulationReplay.Capture(
            SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed,
            SimE1Fixture.MilestoneCommands(), Ticks, sampleEvery: 1);

        var verification = SimulationReplay.Verify(record, SimE1Fixture.Config(), new SimRng(777));
        Assert.True(verification.Verified);
        Assert.Null(verification.FirstDivergenceTick);
        Assert.Equal(Ticks + 1, verification.ComparedSamples);
    }
}
