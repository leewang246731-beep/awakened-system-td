using EraCore.CampaignLoop;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Procedural;
using EraCore.Sim;
using EraCore.Tests.Levels;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using Xunit.Abstractions;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Procedural.ProceduralEntryContent;

namespace EraCore.Tests.Procedural;

// [C-19][F-007][F-048] T-140 硬验收②：≥10 局中位 40~90 分钟——10 个不同
// seed 生成关（三档梯轮转 4/3/3）+headless 自动通关（命令流=生成器产物
// 的教学脚本，offsystem 命令流先例）实测通关时刻；中位落带断言+全表
// 输出（实测数字全录 REV-20261018-T140——超带=调参重测，勿造假）。
// 附同 seed 双跑全等（硬验收①引擎面）。
public class ProceduralDurationChainTests : SqliteSavePortTestBase
{
    private readonly ITestOutputHelper _output;

    public ProceduralDurationChainTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The 10-run ladder: seeds 20260101..20260110, tiers cycling
    /// regular-1/regular-2/endgame (4/3/3) — different seeds, different maps.
    /// </summary>
    private static readonly (long Seed, string TierId)[] Runs =
    {
        (20260101L, "regular-1"),
        (20260102L, "regular-2"),
        (20260103L, "endgame"),
        (20260104L, "regular-1"),
        (20260105L, "regular-2"),
        (20260106L, "endgame"),
        (20260107L, "regular-1"),
        (20260108L, "regular-2"),
        (20260109L, "regular-1"),
        (20260110L, "endgame"),
    };

    private sealed record ChainLine(
        int Index,
        long Seed,
        string TierId,
        double Threat,
        int WinWaves,
        double Grace,
        int Width,
        int Height,
        int Entries,
        long VictoryTick,
        double VictoryMinutes,
        int Towers,
        int Issued,
        int Applied,
        bool? BossDead,
        long StateHash);

    private static EraCore.CampaignLoop.HandcraftedLevel LoadLevel(long seed, string tierId)
    {
        ProceduralTier tier = Load().TryGetTier(tierId)
            ?? throw new InvalidOperationException($"档位 {tierId} 缺席注册表");
        ProceduralLevel generated = ProceduralLevelGenerator.Generate(
            new ProceduralRequest(seed, tier, Era1Pack(), Load().Constraints),
            derived => new SimRng(derived));
        return HandcraftedLevelData.Parse(generated.ToLevelJson()).Items.Single();
    }

    private ChainLine RunOnce(int index, long seed, string tierId, ISavePort port)
    {
        EraCore.CampaignLoop.HandcraftedLevel level = LoadLevel(seed, tierId);
        SimulationConfig config = BuildConfig(level, "era1");
        IReadOnlyList<SimCommand> commands = BuildCommands(level, "era1");
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t140.{level.Id}", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            port);

        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(
            intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long horizon = graceTicks + ((level.WinWaves + 12) * intervalTicks); // 胜波+12 波窗（清场余量，E5 先例加宽版）

        long steps = 0;
        while (session.Machine.State == GameFlowState.InLevel && steps < horizon)
        {
            session.RunFor(600);
            steps += 600;
        }

        Assert.True(session.Machine.State == GameFlowState.Victory,
            $"未通关（state={session.Machine.State}）: seed={seed} tier={tierId} wave={session.Sim.CurrentWave} " +
            $"tick={session.Sim.CurrentTick} issued={session.IssuedCommands.Count} " +
            $"applied={session.Sim.AppliedCommandCount} towers={session.Sim.Towers.Count} " +
            $"alive={session.Sim.Units.Count(u => u.Alive)} " +
            $"bossAlive={session.Sim.Units.Any(u => u.IsBoss && u.Alive)}");

        bool? bossDead = level.Waves.BossEnemyId is null ? null
            : session.Sim.Units.Where(u => u.IsBoss).All(u => !u.Alive);
        return new ChainLine(
            index,
            seed,
            tierId,
            level.Threat,
            level.WinWaves,
            level.FirstWaveGraceMultiplier,
            level.Map.Width,
            level.Map.Height,
            level.Map.EntryCells.Count,
            session.Sim.CurrentTick,
            session.Sim.CurrentTick / (double)(Simulation.TicksPerSecond * 60),
            session.Sim.Towers.Count,
            session.IssuedCommands.Count,
            (int)session.Sim.AppliedCommandCount,
            bossDead,
            session.Sim.ComputeStateHash());
    }

    [Fact]
    public void Ten_generated_levels_median_victory_time_lands_in_the_40_90_band()
    {
        // legal — 硬验收②：10 局全部 Victory；中位（10 局=第 5/6 序均值）
        // 落 [40,90] 分钟带；命令流全量签发且零拒收；终局档 Boss 必死；
        // 全表输出（实测时刻=REV 留档材料）。
        var lines = new List<ChainLine>();
        for (int i = 0; i < Runs.Length; i++)
        {
            lines.Add(RunOnce(i + 1, Runs[i].Seed, Runs[i].TierId, CreatePort()));
        }

        _output.WriteLine("T-140 程序关 10 局时长表（victory=headless 通关实测分钟；threat=档位 §7 派生）:");
        foreach (ChainLine line in lines)
        {
            _output.WriteLine(
                $"  #{line.Index} seed={line.Seed} tier={line.TierId} t={line.Threat:F2} win={line.WinWaves} " +
                $"grace={line.Grace:F2} map={line.Width}x{line.Height} entries={line.Entries} " +
                $"victory={line.VictoryMinutes:F2}min tick={line.VictoryTick} towers={line.Towers} " +
                $"commands={line.Issued}/{line.Applied} bossDead={line.BossDead?.ToString() ?? "-"} " +
                $"hash={line.StateHash}");
        }

        var sorted = lines.Select(l => l.VictoryMinutes).OrderBy(m => m).ToList();
        double median = (sorted[4] + sorted[5]) / 2.0; // 10 局中位=第 5/6 序统计量均值
        _output.WriteLine($"中位={median:F2}min（band=40~90min）");
        Assert.InRange(median, 40.0, 90.0);

        Assert.All(lines, line => Assert.Equal(line.Issued, line.Applied)); // 零拒收
        Assert.All(lines, line => Assert.True(line.VictoryMinutes > 0));
        Assert.All(
            lines.Where(l => l.TierId == "endgame"),
            line => Assert.True(line.BossDead == true)); // 终局档 Boss 必死
        Assert.Equal(9, lines.Single(l => l.Index == 3).Towers); // 终局档塔数=计划 9（6 箭+3 落石）
        Assert.Equal(3, lines.Single(l => l.Index == 1).Towers); // 常规一档塔数=计划 3（2 箭+1 落石）
    }

    [Fact]
    public void Same_seed_double_run_of_generated_levels_hashes_identically()
    {
        // legal — 硬验收①引擎面：同 seed 生成关双跑通关——通关刻+终局哈希+
        // 塔数全等（生成关=固定 seed+布局参数的非随机重掷证明）。
        foreach ((long seed, string tierId) in new[] { Runs[0], Runs[1], Runs[2] })
        {
            ChainLine first = RunOnce(0, seed, tierId, CreatePort());
            ChainLine second = RunOnce(0, seed, tierId, CreatePort());
            Assert.Equal(first.VictoryTick, second.VictoryTick);
            Assert.Equal(first.StateHash, second.StateHash);
            Assert.Equal(first.Towers, second.Towers);
        }
    }
}
