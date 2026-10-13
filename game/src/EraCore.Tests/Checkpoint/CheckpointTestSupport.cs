using EraCore.Balance;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Checkpoint;

// T-057 (C-04) 测试基建：波界 tick 推算（威胁度/波间隔/首波宽限全部来自
// Balance 公式与 fixture 波配置，禁手填数值——CS-4.4）与快速关卡运行器。

/// <summary>
/// Heavy-weight collection of the checkpoint suite: the multi-world
/// equivalence and golden runs saturate a core for tens of seconds each.
/// Serializing them into ONE xUnit collection keeps at most one of them
/// running at any moment, so they cannot stack concurrent CPU load onto the
/// spike-1 performance budget test (T-043 回归线 1.336×2.25 ms——并发劣化
/// 容差按单邻居负载设计).
/// </summary>
[CollectionDefinition(Name)]
public sealed class CheckpointHeavyCollection
{
    public const string Name = "EraCore.Checkpoint.Heavy";
}

/// <summary>
/// Shared fixture of the checkpoint suite: wave-tick arithmetic derived from
/// the frozen <see cref="Campaign"/> formulas and the
/// <see cref="SimE1Fixture"/> wave knobs (threat 1.2 ⇒ interval 72 s = 2160
/// ticks, grace ×1.5), plus a fast "run to tick" helper. Every number traces
/// to a formula call — no hand-copied tick values (CS-2.3/CS-4.4).
/// </summary>
internal static class CheckpointFixture
{
    /// <summary>Fixture threat (t=1.2: interval mult 0.6, count mult 2.0).</summary>
    public const double Threat = 1.2;

    /// <summary>FirstWaveGraceMultiplier of the fixture wave config (WaveSpawnConfig default).</summary>
    public const double GraceMultiplier = 1.5;

    /// <summary>Ticks per wave (Campaign.WaveIntervalSeconds × tick rate).</summary>
    public static long IntervalTicks => (long)Math.Round(
        Campaign.WaveIntervalSeconds(Threat) * Simulation.TicksPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>Ticks before wave 1 spawns (interval × grace multiplier).</summary>
    public static long GraceTicks => (long)Math.Round(
        IntervalTicks * GraceMultiplier, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Tick at which <paramref name="wave"/> ENDS (wave+1 installs):
    /// grace + wave × interval. Wave 0 has no boundary → 0.
    /// </summary>
    public static long BoundaryTickOf(int wave) =>
        wave < 1 ? 0 : GraceTicks + (wave * IntervalTicks);

    /// <summary>Checkpoint wave of the fixture threat (16: 胜波 22 ⇒ ⌊16.5⌋).</summary>
    public static int CheckpointWave => Campaign.Checkpoint(Campaign.WinWaves(Threat));

    /// <summary>
    /// Fresh simulation with the milestone command stream enqueued, run to
    /// <paramref name="tick"/> (all milestone commands apply by tick 600).
    /// </summary>
    public static Simulation RunTo(long tick)
    {
        var simulation = new Simulation(SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        simulation.EnqueueRange(SimE1Fixture.MilestoneCommands());
        simulation.Run(tick);
        return simulation;
    }

    /// <summary>
    /// Long-horizon variant of the fixture config: identical map/seed/commands
    /// and the SAME wave formula structure, only the roster base count is
    /// thinned (BaseCount 10→2). The checkpoint/equivalence properties under
    /// test (wave-boundary detection, checkpoint wave, replay determinism)
    /// are enemy-density independent; the thinning keeps the 30-wave
    /// integration inside the suite's CPU budget so it cannot inflate the
    /// spike-1 performance medians that run concurrently (T-043 回归线).
    /// The golden regression keeps the FULL-density fixture.
    /// </summary>
    public static SimulationConfig LongHorizonConfig() => SimE1Fixture.Config() with
    {
        Waves = SimE1Fixture.Config().Waves with { BaseCount = 2 },
    };

    /// <summary>Long-horizon (thin roster) variant of <see cref="RunTo"/>.</summary>
    public static Simulation RunToLongHorizon(long tick)
    {
        var simulation = new Simulation(LongHorizonConfig(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        simulation.EnqueueRange(SimE1Fixture.MilestoneCommands());
        simulation.Run(tick);
        return simulation;
    }

    /// <summary>The milestone stream plus one late command (tail exercise).</summary>
    public static IReadOnlyList<Contracts.Sim.SimCommand> MilestonePlusLate(Contracts.Sim.SimCommand late) =>
        SimE1Fixture.MilestoneCommands().Append(late).ToArray();
}
