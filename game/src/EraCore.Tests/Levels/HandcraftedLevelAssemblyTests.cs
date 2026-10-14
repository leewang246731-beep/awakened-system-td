using EraCore.CampaignLoop;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraCore.Units;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Levels;

// [C-12][C-19][F-001][F-055] T-085 关卡接入：每条手工关行组装为引擎级
// LevelSessionConfig/SimulationConfig（CampaignLoop 先例），并以同 seed 双构造
// 断言引擎面确定性（手工关=固定 seed+布局参数 → 双装载双构造状态哈希全等）。
public class HandcraftedLevelAssemblyTests : SqliteSavePortTestBase
{
    private static readonly (string Id, string EraDir)[] Batch =
    {
        ("campaign:level-e1-01", "era1"),
        ("campaign:level-e1-02", "era1"),
        ("campaign:level-e1-03", "era1"),
        ("campaign:level-e1-04", "era1"),
        ("campaign:level-e2-01", "era2"),
        ("campaign:level-e2-02", "era2"),
        ("campaign:level-e2-03", "era2"),
    };

    private static HandcraftedLevel Level(string id, string eraDir) =>
        HandcraftedLevelData.Load(eraDir == "era1" ? "e1.json" : "e2.json").TryGet(id)
        ?? throw new InvalidOperationException($"缺关卡行 {id}");

    private static string ContentText(string eraDir, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", eraDir, file);
            if (File.Exists(probe))
            {
                return File.ReadAllText(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{eraDir}/{file}");
    }

    /// <summary>组装一关为引擎级 SimulationConfig（全部字段出自关卡行与 era 内容表）。</summary>
    internal static SimulationConfig BuildConfig(HandcraftedLevel level, string eraDir)
    {
        var entries = level.Map.EntryCells.Select(c => new CellCoord(c.X, c.Y)).ToArray();
        HeroSpec? hero = level.HeroId is null ? null : HeroSpecCatalog.Parse(ContentText("era2", "heroes.json")).Get(level.HeroId);
        var stock = level.InitialStock
            .Select(line => new KeyValuePair<ResourceKey, double>(new ResourceKey(line.Resource), line.Amount))
            .ToList();
        return new SimulationConfig(
            level.Map.Width,
            level.Map.Height,
            level.Map.ForestRatio,
            level.Map.RockRatio,
            level.Map.WaterRatio,
            new CellCoord(level.Map.BaseCell.X, level.Map.BaseCell.Y),
            entries,
            TowerSpec.LoadCollection(ContentText(eraDir, "towers.json")),
            EnemySpecCatalog.Parse(ContentText(eraDir, "enemies.json")),
            new WaveSpawnConfig(
                BaseCount: level.Waves.BaseCount,
                BeginWave: level.Waves.BeginWave,
                Spacing: level.Waves.Spacing,
                Scaling: level.Waves.Scaling,
                ThreatLevel: level.Threat,
                RegularRoster: level.Waves.RegularRoster,
                EliteLineEnabled: level.Waves.EliteLineEnabled,
                EliteEnemyId: level.Waves.EliteEnemyId,
                FirstWaveGraceMultiplier: level.FirstWaveGraceMultiplier,
                ValleySeconds: level.Waves.ValleySeconds,
                BatchesPerWave: level.Waves.BatchesPerWave,
                EntryCells: entries),
            stock,
            WarehouseCell: new CellCoord(level.Map.WarehouseCell.X, level.Map.WarehouseCell.Y),
            Costs: new BuildCostPolicy(new ResourceKey(level.InitialStock[0].Resource)),
            Hero: hero,
            HeroCell: level.Map.HeroCell is null ? null : new CellCoord(level.Map.HeroCell.X, level.Map.HeroCell.Y),
            WarehouseLevel: WarehouseLevel(eraDir));
    }

    /// <summary>命令流 → SimCommand：wave k 步骤落在 k 波开场谷内（wave 1 在宽限尾）。</summary>
    internal static IReadOnlyList<SimCommand> BuildCommands(HandcraftedLevel level, string eraDir)
    {
        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long valleyTicks = (long)Math.Round(level.Waves.ValleySeconds * Simulation.TicksPerSecond, MidpointRounding.AwayFromZero);

        var commands = new List<SimCommand>();
        foreach (ScriptStep step in level.CommandScript.OrderBy(s => s.Wave))
        {
            long tick = Math.Max(0, graceTicks + ((step.Wave - 1) * intervalTicks) - valleyTicks);
            commands.Add(new SimCommand(tick, SimCommandKind.PlayerBuild, Payload(step, eraDir)));
        }

        return commands;
    }

    private static string Payload(ScriptStep step, string eraDir) => step.Verb switch
    {
        "build-labor-camp" => CellPayload("build-labor-camp", step.Cell!),
        "build-harvester" => "{\"verb\":\"build-harvester\",\"resource\":\"" + step.Resource
            + "\",\"seats\":" + step.Seats + ",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "build-converter" => step.ConverterRecipe is null
            ? CellPayload("build-converter", step.Cell!)
            : "{\"verb\":\"build-converter\",\"recipe\":" + ConverterRecipePayload(eraDir, step.ConverterRecipe)
                + ",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "build-tower" => "{\"verb\":\"build-tower\",\"tower\":\"" + step.Tower
            + "\",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "assign-carrier" => "{\"verb\":\"assign-carrier\",\"from\":" + step.FromEntity + ",\"to\":" + step.ToEntity
            + (step.CarrierTier is null ? "" : ",\"tier\":\"" + step.CarrierTier + "\"") + "}",
        "upgrade-tower" => "{\"verb\":\"upgrade-tower\",\"target\":" + step.Entity + ",\"to_tier\":" + step.ToTier + "}",
        "hero-order" => step.Order == "move"
            ? "{\"verb\":\"hero-order\",\"order\":\"move\",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}"
            : "{\"verb\":\"hero-order\",\"order\":\"" + step.Order + "\"}",
        _ => throw new InvalidOperationException($"未登记命令动词 {step.Verb}"),
    };

    private static string CellPayload(string verb, CellPoint cell) =>
        "{\"verb\":\"" + verb + "\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}";

    [Theory]
    [MemberData(nameof(BatchRows))]
    public void Every_level_assembles_into_a_session_with_the_data_win_waves(string id, string eraDir)
    {
        // legal — [F-055][F-001] 组装即通关卡接入：SimulationConfig 全字段来自
        // 关卡行+era 表；LevelSession 胜波=数据行 win_waves（§7 公式双面一致）；
        // 种子=map.seed（手工关 replay 键）。
        HandcraftedLevel level = Level(id, eraDir);
        var config = new LevelSessionConfig(
            BuildConfig(level, eraDir), $"level.{level.Id}", level.Threat, level.Map.Seed);
        var session = new LevelSession(config, new SimRng(level.Map.Seed), CreatePort());

        Assert.Equal(GameFlowState.Menu, session.Machine.State);
        Assert.Equal(level.WinWaves, session.WinWaves);
        Assert.Equal(level.Map.Seed, session.Sim.Seed);
        session.Machine.StartLevel();
        Assert.Equal(GameFlowState.InLevel, session.Machine.State);
    }

    [Theory]
    [MemberData(nameof(BatchRows))]
    public void Same_seed_double_assembly_replays_identically(string id, string eraDir)
    {
        // legal — [F-055] 确定性③（引擎面）：同 seed 双构造+同命令流，跑到
        // 宽限+一个波间隔（首波入场+交战窗）状态哈希全等——手工关"非随机
        // 重掷"的引擎面证明；命令全部入队且全应用（payload 与经济面合法）。
        HandcraftedLevel level = Level(id, eraDir);
        SimulationConfig config = BuildConfig(level, eraDir);
        IReadOnlyList<SimCommand> commands = BuildCommands(level, eraDir);
        Assert.NotEmpty(commands);

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
            new LevelSessionConfig(config, $"level.{level.Id}.replay", level.Threat, level.Map.Seed),
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

    [Fact]
    public void Command_payloads_follow_the_fixture_shapes_and_apply_cleanly()
    {
        // legal — [F-055] 组装面命令零拒收（SimE1Fixture/SimE2Fixture 载荷形状）：
        // 抽最重的一关（E2 L2-03：升级+英雄令+配方转换器）跑到宽限+一间隔，
        // 凡到达签发时刻的命令全部被应用——关卡数据不是只在装载器里好看的
        //（未到时刻的命令按波锚自然落在窗口外，不计入）。
        HandcraftedLevel level = Level("campaign:level-e2-03", "era2");
        SimulationConfig config = BuildConfig(level, "era2");
        IReadOnlyList<SimCommand> commands = BuildCommands(level, "era2");
        var session = new LevelSession(
            new LevelSessionConfig(config, "level.e2-03.payload", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort());
        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        long intervalTicks = (long)Math.Round(
            EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long horizon = Math.Min(graceTicks + intervalTicks, 30_000);
        session.RunFor(horizon);

        Assert.Equal(commands.Count, session.IssuedCommands.Count);
        long dueAtHorizon = commands.Count(command => command.IssuedAtTick <= session.Sim.CurrentTick);
        Assert.True(dueAtHorizon >= 6, "窗口内应至少覆盖首波命令组与首个英雄令");
        Assert.Equal((int)dueAtHorizon, session.Sim.AppliedCommandCount);
    }

    public static IEnumerable<object[]> BatchRows() => Batch.Select(b => new object[] { b.Id, b.EraDir });
}
