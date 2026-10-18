using EraCore.CampaignLoop;
using EraCore.Contracts.Sim;
using EraCore.Procedural;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.Sim;
using Xunit;
using EraCore.Tests.Levels;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;
using static EraCore.Tests.Procedural.ProceduralEntryContent;

namespace EraCore.Tests.Procedural;

// [C-12][C-19][F-007] T-140 生成产物走既有装载管线：JSON → 手工关装载器
//（HandcraftedLevelData.Parse 全结构校验）→ BuildConfig/BuildCommands 组装
//（CampaignLoop 先例）→ LevelSession 起局；同 seed 双跑状态哈希全等
//（同 seed 同图的引擎面证明）。
public class ProceduralPipelineTests : SqliteSavePortTestBase
{
    private static ProceduralLevel Generate(long seed, ProceduralTier? tier = null) =>
        ProceduralLevelGenerator.Generate(
            new ProceduralRequest(seed, tier ?? Load().DefaultTier(), Era1Pack(), Load().Constraints),
            derived => new SimRng(derived));

    [Fact]
    public void Generated_json_loads_through_the_existing_handcrafted_pipeline()
    {
        // legal — 同构装载面：三个档位的生成 JSON 全部通过既有手工关装载器
        //（T-085 schema 全校验：威胁带/胜波公式/宽限带/预算带/坐标界/
        // 命令流实体引用/波谷界/目标池），era 字段与手工关同族。
        foreach (ProceduralTier tier in Load().Tiers)
        {
            ProceduralLevel generated = Generate(20261018, tier);
            var set = HandcraftedLevelData.Parse(generated.ToLevelJson());
            var loaded = Assert.Single(set.Items);
            Assert.Equal(generated.Id, loaded.Id);
            Assert.Equal(generated.Era, loaded.Era);
            Assert.Equal(generated.Threat, loaded.Threat, 5);
            Assert.Equal(generated.WinWaves, loaded.WinWaves);
            Assert.Equal(generated.Map.Seed, loaded.Map.Seed);
            Assert.Equal(generated.Map.Width, loaded.Map.Width);
            Assert.Equal(generated.Map.Height, loaded.Map.Height);
            Assert.Equal(generated.Map.EntryCells.Count, loaded.Map.EntryCells.Count);
            Assert.Equal(generated.CommandScript.Count, loaded.CommandScript.Count);
            Assert.Equal(generated.Waves.BossEnemyId, loaded.Waves.BossEnemyId);
        }
    }

    [Fact]
    public void Every_procedural_step_uses_the_closed_handcrafted_verb_vocabulary()
    {
        // legal — 命令动词面：生成命令流全部落在手工关封闭动词表内
        //（E1 薄版动词——同构性的一部分，不是私有方言）。
        ProceduralLevel generated = Generate(20261018, Load().Tiers[2]);
        Assert.All(generated.CommandScript, step => Assert.Contains(step.Verb, HandcraftedLevelData.ScriptVerbs));
        Assert.All(
            generated.CommandScript.Where(s => s.CarrierTier is not null),
            step => Assert.True(HandcraftedLevelData.CarrierTiers.Contains(step.CarrierTier!)));
    }

    [Fact]
    public void Generated_level_assembles_and_starts_a_session()
    {
        // legal — 组装面：装载行 → SimulationConfig（BuildConfig 先例）→
        // LevelSession 起局，胜波=§7 公式，种子=map.seed（replay 键）。
        var set = HandcraftedLevelData.Parse(Generate(20261018).ToLevelJson());
        var level = Assert.Single(set.Items);
        var config = new LevelSessionConfig(BuildConfig(level, "era1"), $"t140.{level.Id}", level.Threat, level.Map.Seed);
        var session = new LevelSession(config, new SimRng(level.Map.Seed), CreatePort());

        Assert.Equal(EraCore.CampaignLoop.GameFlowState.Menu, session.Machine.State);
        Assert.Equal(level.WinWaves, session.WinWaves);
        Assert.Equal(level.Map.Seed, session.Sim.Seed);
        session.Machine.StartLevel();
        Assert.Equal(EraCore.CampaignLoop.GameFlowState.InLevel, session.Machine.State);
    }

    [Fact]
    public void Same_seed_double_run_replays_identically()
    {
        // legal — 确定性（引擎面）：同 seed+同命令流双跑到宽限+两波窗，
        // 状态哈希/终局刻/签发数全等——生成关=固定 seed+布局参数的
        // 引擎面证明（手工关同构先例 HandcraftedLevelAssemblyTests）。
        foreach (ProceduralTier tier in Load().Tiers)
        {
            var set = HandcraftedLevelData.Parse(Generate(31415, tier).ToLevelJson());
            var level = Assert.Single(set.Items);
            var config = BuildConfig(level, "era1");
            var commands = BuildCommands(level, "era1");
            Assert.NotEmpty(commands);

            long intervalTicks = (long)Math.Round(
                EraCore.Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
                MidpointRounding.AwayFromZero);
            long graceTicks = (long)Math.Round(
                intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
            long horizon = Math.Min(graceTicks + intervalTicks + intervalTicks, 60_000);

            (long firstHash, long firstTick, int firstIssued) = RunOnce(level, config, commands, horizon);
            (long secondHash, long secondTick, int secondIssued) = RunOnce(level, config, commands, horizon);

            Assert.Equal(firstTick, secondTick);
            Assert.Equal(firstIssued, secondIssued);
            Assert.Equal(firstHash, secondHash);
        }
    }

    private (long Hash, long Tick, int Issued) RunOnce(
        Levels.HandcraftedLevel level,
        SimulationConfig config,
        IReadOnlyList<SimCommand> commands,
        long horizon)
    {
        var session = new LevelSession(
            new LevelSessionConfig(config, $"t140.{level.Id}.replay", level.Threat, level.Map.Seed),
            new SimRng(level.Map.Seed),
            CreatePort());
        session.Machine.StartLevel();
        foreach (SimCommand command in commands)
        {
            session.Enqueue(command);
        }

        session.RunFor(horizon);
        return (session.Sim.ComputeStateHash(), session.Sim.CurrentTick, session.IssuedCommands.Count);
    }
}
