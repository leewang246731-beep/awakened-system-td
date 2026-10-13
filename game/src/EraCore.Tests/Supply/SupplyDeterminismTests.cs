using Xunit;

namespace EraCore.Tests.Supply;

/// <summary>
/// Replay determinism of the 断供降速 logic layer (T-048, F-154 seam):
/// the SAME 60 s wall-cut scenario (same seed, same terrain-edit command
/// stream) replays to EQUAL state hashes across every component that owns
/// state — monitor curve, detector events, all seven link guards,
/// converter — plus the fire-tick and efficiency traces; driving one extra
/// tick changes the fingerprint (the state is live, not frozen). The
/// scenario draws no RNG (the E1 supply lane is pure arithmetic; the seed
/// enters the world generator only), so the replay contract holds
/// unconditionally.
/// </summary>
// [F-046][F-154 前置] 同 seed + 同命令流（加墙/拆墙）⇒ 状态哈希相等
public class SupplyDeterminismTests
{
    /// <summary>Hash bundle of one scenario run — every stateful component of the lane.</summary>
    private sealed record ReplayFingerprint(
        long MonitorHash,
        long DetectorHash,
        long[] GuardHashes,
        long ConverterHash,
        int FiredCount,
        int AmmoShotsRemaining,
        long ShotsDelivered,
        int EventCount,
        long[] FireTicks,
        double[] EfficiencyTrace,
        long[] DeliveredTrace);

    private static ReplayFingerprint RunScenario(int untilTick)
    {
        var s = new SupplyIntegrationTests.Scenario();
        s.Run(untilTick);
        return new ReplayFingerprint(
            s.Monitor.ComputeStateHash(),
            s.Detector.ComputeStateHash(),
            s.AmmoGuards.Concat(s.WoodGuards).Select(g => g.ComputeStateHash()).ToArray(),
            s.Converter.ComputeStateHash(),
            s.Weapon.FiredCount,
            s.Weapon.AmmoShotsRemaining,
            s.ShotsDelivered,
            s.Events.Count,
            s.FireTicks.ToArray(),
            s.EfficiencyByTick.ToArray(),
            s.DeliveredByTick.ToArray());
    }

    [Fact] // legal — same seed + same command stream ⇒ identical replay
    public void WallCutScenarioReplaysIdentically()
    {
        // [F-154 前置] 断链重放：加墙(t=600)/拆墙(t=1200) 命令流固定，两次
        // 完整 60 s 模拟的每层状态哈希与采样序列全等
        ReplayFingerprint a = RunScenario(1800);
        ReplayFingerprint b = RunScenario(1800);

        Assert.Equal(a.MonitorHash, b.MonitorHash);
        Assert.Equal(a.DetectorHash, b.DetectorHash);
        Assert.Equal(a.GuardHashes, b.GuardHashes);
        Assert.Equal(a.ConverterHash, b.ConverterHash);
        Assert.Equal(a.FiredCount, b.FiredCount);
        Assert.Equal(a.AmmoShotsRemaining, b.AmmoShotsRemaining);
        Assert.Equal(a.ShotsDelivered, b.ShotsDelivered);
        Assert.Equal(a.EventCount, b.EventCount);
        Assert.Equal(a.FireTicks, b.FireTicks);
        Assert.Equal(a.EfficiencyTrace, b.EfficiencyTrace);
        Assert.Equal(a.DeliveredTrace, b.DeliveredTrace);
    }

    [Fact] // boundary — driving through the restore command moves every hash
    public void CrossingTheRestoreCommandChangesTheFingerprint()
    {
        // t=1199: wall still standing, porters parked; t=1201: the tear-down
        // ran (routes re-linked, monitor break cell cleared) and one more
        // tick was driven — every stateful layer moved
        ReplayFingerprint before = RunScenario(1199);
        ReplayFingerprint after = RunScenario(1201);

        Assert.NotEqual(before.MonitorHash, after.MonitorHash); // break cell cleared
        Assert.NotEqual(before.GuardHashes, after.GuardHashes); // reroutes re-linked the porters
        Assert.NotEqual(before.DetectorHash, after.DetectorHash); // streak state advanced
        Assert.True(after.FiredCount >= before.FiredCount);
        Assert.NotEqual(before.EfficiencyTrace.Length, after.EfficiencyTrace.Length);
    }

    [Fact] // boundary — one plain extra tick moves the observable traces
    public void OneExtraTickChangesTheFingerprint()
    {
        ReplayFingerprint shortRun = RunScenario(1200); // ends right after the wall comes down
        ReplayFingerprint longer = RunScenario(1201);

        // t=1201 is still a quiet tick (recovery deliveries land later), so
        // the fire list is untouched — the live parts are the traces:
        // exactly one more sample on each per-tick trace
        Assert.Equal(shortRun.FireTicks, longer.FireTicks);
        Assert.Equal(shortRun.FiredCount, longer.FiredCount);
        Assert.Equal(shortRun.EfficiencyTrace.Length + 1, longer.EfficiencyTrace.Length);
        Assert.Equal(shortRun.DeliveredTrace.Length + 1, longer.DeliveredTrace.Length);
    }
}
