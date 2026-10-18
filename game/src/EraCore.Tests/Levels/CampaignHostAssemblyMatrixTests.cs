using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using EraCore.Units;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;

namespace EraCore.Tests.Levels;

// [C-12][F-005][F-055] T-160 战役宿主面验收①——全 29 关装载矩阵：e1~e6.json
// 每一行经生产组装律（EraLevelAssembly.BuildConfig/BuildCommands——表现层
// CampaignMenuUi 走的正是这同一条路径）组装为引擎级 LevelSession：
//   ①构造即证（Menu 态+胜波=数据行 win_waves+命令流非空+种子=map.seed）；
//   ②同 seed 双装载双构造+同命令流，跑到宽限+一个波间隔状态哈希全等
//     （T-085 确定性③的六时代全量面——「全 24 关可构造」口径的实际全集
//     =29 关：E1~E4 十九关+E5/E6 各五关）；
//   ③各时代组装面（era faces）：E1~E4 践踏调参族挂载/E3/E4/E5 能源风险表
//     非空/E4 辐射行>0/E5 力场塔反湮灭位/E1/E2/E6 无能源通道+e6-final 的
//     通关之门（requires_wonder）数据位在位。
public class CampaignHostAssemblyMatrixTests : SqliteSavePortTestBase
{
    private static readonly (string File, string EraDir)[] Sets =
    {
        ("e1.json", "era1"),
        ("e2.json", "era2"),
        ("e3.json", "era3"),
        ("e4.json", "era4"),
        ("e5.json", "era5"),
        ("e6.json", "era6"),
    };

    public static IEnumerable<object[]> AllLevelRows() => Sets.SelectMany(set =>
        HandcraftedLevelData.Load(set.File).Items
            .OrderBy(level => level.Slot)
            .Select(level => new object[] { set.File, set.EraDir, level.Id }));

    public static IEnumerable<object[]> AllSets() => Sets.Select(set => new object[] { set.File, set.EraDir });

    [Theory]
    [MemberData(nameof(AllSets))]
    public void Every_chapter_file_is_the_expected_shape(string file, string eraDir)
    {
        // legal — 章文件量级锚：E1=5（4+终局）/E2=4/E3~E6=5，全 29 行；
        // 槽位 1..N 无洞；e6-final 声明通关之门（requires_wonder 数据位）；
        // era 目录映射=生产组装律 EraDirOf（宿主内容装载根的寻址面）。
        Assert.Equal($"e{eraDir["era".Length..]}.json", file);
        HandcraftedLevelSet set = HandcraftedLevelData.Load(file);
        int expected = file == "e2.json" ? 4 : 5;
        Assert.Equal(expected, set.Items.Count);
        Assert.Equal(Enumerable.Range(1, expected), set.Items.Select(level => level.Slot).OrderBy(slot => slot));

        int total = Sets.Sum(row => row.File == "e2.json" ? 4 : 5);
        Assert.True(total >= 24, $"全战役量级应 ≥24 关（任务口径），实际 {total}");

        if (file == "e6.json")
        {
            HandcraftedLevel final = set.Items.Single(level => level.Slot == 5);
            Assert.Equal("campaign:wonder-era-gate", final.RequiresWonder); // 通关之门（附录 F.3）
        }
    }

    [Theory]
    [MemberData(nameof(AllLevelRows))]
    public void Every_level_assembles_into_a_real_session_through_the_production_path(
        string file, string eraDir, string levelId)
    {
        // legal — 生产组装律（UI 同路径）：SimulationConfig 全字段来自关卡行+era
        // 表；LevelSession 胜波=数据行 win_waves；种子=map.seed；命令流非空；
        // era 目录映射=EraDirOf（关卡行时代 id → 内容目录的寻址面）。
        HandcraftedLevel level = HandcraftedLevelData.Load(file).TryGet(levelId)!;
        Assert.Equal(eraDir, EraLevelAssembly.EraDirOf(level));
        SimulationConfig config = EraLevelAssembly.BuildConfig(level, ContentRoot());
        IReadOnlyList<SimCommand> commands = EraLevelAssembly.BuildCommands(level, ContentRoot());
        Assert.NotEmpty(commands);

        var session = new LevelSession(
            new LevelSessionConfig(config, $"matrix.{level.Id}", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort());
        Assert.Equal(GameFlowState.Menu, session.Machine.State);
        Assert.Equal(level.WinWaves, session.WinWaves);
        Assert.Equal(level.Map.Seed, session.Sim.Seed);
        session.Machine.StartLevel();
        Assert.Equal(GameFlowState.InLevel, session.Machine.State);
    }

    [Theory]
    [MemberData(nameof(AllLevelRows))]
    public void Era_faces_of_the_production_assembly(string file, string eraDir, string levelId)
    {
        // legal — 各时代组装面（Era3/Era4/Era5LevelPlaythroughTests 的挂载律
        // 在生产组装律上的复核）：践踏族（E1~E4）、能源表（E3 过热/E4 过热+
        // 熔毁+辐射/E5 过热+湮灭+力场塔）、E1/E2/E6 无风险通道。
        HandcraftedLevel level = HandcraftedLevelData.Load(file).TryGet(levelId)!;
        SimulationConfig config = EraLevelAssembly.BuildConfig(level, ContentRoot());
        int era = EraLevelAssembly.EraIndexOf(level);
        Assert.Equal(int.Parse(eraDir["era".Length..]), era);

        switch (era)
        {
            case 1:
            case 2:
                Assert.Null(config.EnergyRisk);
                Assert.NotNull(config.Waves.Trample);
                break;
            case 3:
                Assert.NotNull(config.EnergyRisk);
                Assert.Null(config.EnergyRisk.Meltdown);
                Assert.Equal(new TrampleProfile(100, 45), config.Waves.Trample);
                break;
            case 4:
                Assert.NotNull(config.EnergyRisk);
                Assert.NotNull(config.EnergyRisk.Meltdown);
                Assert.True(config.RadiationZoneHpDecayPerSecond > 0);
                Assert.Equal(new TrampleProfile(0, 45), config.Waves.Trample);
                break;
            case 5:
                Assert.NotNull(config.EnergyRisk);
                Assert.Null(config.EnergyRisk.Meltdown);
                Assert.NotNull(config.EnergyRisk.Annihilation);
                Assert.Equal("era5:tower-force-field", config.AnnihilationForceFieldTowerId);
                Assert.Null(config.Waves.Trample); // E5/E6 无践踏（Era5/Era6LevelPlaythroughTests 同口径）
                break;
            case 6:
                Assert.Null(config.EnergyRisk); // 零点能=无风险
                Assert.Null(config.Waves.Trample);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(AllLevelRows))]
    public void Same_seed_double_assembly_replays_identically(string file, string eraDir, string levelId)
    {
        // legal — 确定性③六时代全量：同 seed 双构造+同命令流，跑到宽限+一个
        // 波间隔状态哈希全等；命令全部入队且全应用（载荷与经济面合法）；
        // era 目录映射=EraDirOf（寻址面复核）。
        HandcraftedLevel level = HandcraftedLevelData.Load(file).TryGet(levelId)!;
        Assert.Equal(eraDir, EraLevelAssembly.EraDirOf(level));
        SimulationConfig config = EraLevelAssembly.BuildConfig(level, ContentRoot());
        IReadOnlyList<SimCommand> commands = EraLevelAssembly.BuildCommands(level, ContentRoot());

        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long horizon = Math.Min(graceTicks + intervalTicks, 30_000);

        long firstHash = RunOnce(level, config, commands, horizon, out int firstIssued, out long firstTick);
        long secondHash = RunOnce(level, config, commands, horizon, out int secondIssued, out long secondTick);

        Assert.Equal(firstIssued, secondIssued);
        Assert.Equal(commands.Count, firstIssued);
        Assert.Equal(firstTick, secondTick);
        Assert.Equal(firstHash, secondHash);
    }

    private long RunOnce(
        HandcraftedLevel level,
        SimulationConfig config,
        IReadOnlyList<SimCommand> commands,
        long horizon,
        out int issued,
        out long finalTick)
    {
        var session = new LevelSession(
            new LevelSessionConfig(config, $"matrix.{level.Id}.replay", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort());
        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        for (long i = 0; i < horizon; i++)
        {
            session.RunFor(1);
        }

        issued = session.IssuedCommands.Count;
        finalTick = session.Sim.CurrentTick;
        return session.Sim.ComputeStateHash();
    }
}
