using System.Text.Json;
using EraCore.Sim;
using Xunit;

namespace EraCore.Tests.Sim;

// [C-12][F-001] SimulationReplay：(seed, 命令流) 记录 → 重放 → 逐 tick 哈希
// 比对（T-040 回放要件）；JSON 往返走 source-generator（D-05）。
public class SimulationReplayTests
{
    private static SimulationRunRecord SampleRecord(int sampleEvery = 30) => SimulationReplay.Capture(
        SimE1Fixture.Config(), new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed,
        SimE1Fixture.MilestoneCommands(), ticks: 600, sampleEvery: sampleEvery);

    [Fact] // legal — 采样节奏：首采样=初始态(tick 0)，其后每 stride 一采
    public void Capture_samples_the_initial_state_then_every_stride()
    {
        var record = SampleRecord(sampleEvery: 30);
        Assert.Equal(0, record.Samples[0].Tick);
        Assert.Equal(30, record.Samples[1].Tick);
        Assert.Equal(600, record.Samples[^1].Tick);
        Assert.Equal(21, record.Samples.Count);
        Assert.Equal(600, record.Ticks);
        Assert.Equal(10, record.Commands.Count);
        Assert.Equal(SimE1Fixture.Seed, record.Seed);
    }

    [Fact] // legal — 同 seed 重放：时间线逐点相等（Verify 全绿）
    public void Same_seed_replay_reproduces_the_timeline()
    {
        var record = SampleRecord();
        var replayed = SimulationReplay.ReplayHashes(record, SimE1Fixture.Config(), new SimRng(int.MinValue));

        Assert.Equal(record.Samples.Count, replayed.Count);
        for (int i = 0; i < record.Samples.Count; i++)
        {
            Assert.Equal(record.Samples[i].Tick, replayed[i].Tick);
            Assert.Equal(record.Samples[i].StateHash, replayed[i].StateHash);
        }
    }

    [Fact] // illegal — 篡改记录任一哈希：Verify 报 false 且分叉 tick 定位到该采样
    public void Tampered_record_fails_verification_at_the_first_bad_sample()
    {
        var record = SampleRecord(sampleEvery: 30);
        var tamperedSamples = record.Samples.ToArray();
        tamperedSamples[5] = tamperedSamples[5] with { StateHash = unchecked(tamperedSamples[5].StateHash + 1) };
        var tampered = record with { Samples = tamperedSamples };

        var verification = SimulationReplay.Verify(tampered, SimE1Fixture.Config(), new SimRng(1));
        Assert.False(verification.Verified);
        Assert.Equal(150, verification.FirstDivergenceTick); // 第 5 个采样 = tick 150
    }

    [Fact] // legal — 命令日志 ↔ SimCommand 往返保形
    public void Command_logs_round_trip_to_sim_commands()
    {
        var commands = SimulationReplay.ToCommands(SampleRecord().Commands);
        var original = SimE1Fixture.MilestoneCommands();
        Assert.Equal(original.Count, commands.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i], commands[i]); // record 相等：tick+kind+payload
        }
    }

    [Fact] // illegal — 未知命令 Kind 名被拒
    public void Unknown_command_kind_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            SimulationReplay.ToCommands(new[] { new SimCommandLog(1, "Cheat", "{}") }));
    }

    [Fact] // legal — 记录 JSON 往返（source-generator 元数据，D-05）保形
    public void Record_json_round_trips_through_the_source_context()
    {
        var record = SampleRecord(sampleEvery: 60);
        string json = SimulationReplay.Serialize(record);
        Assert.Contains("\"seed\"", json); // snake_case 命名策略
        var parsed = SimulationReplay.Deserialize(json);
        Assert.Equal(record.Seed, parsed.Seed);
        Assert.Equal(record.Ticks, parsed.Ticks);
        Assert.Equal(record.SampleEvery, parsed.SampleEvery);
        Assert.Equal(record.Commands, parsed.Commands);
        Assert.Equal(record.Samples, parsed.Samples);
    }

    [Fact] // illegal — 空白/坏 JSON 被拒
    public void Deserialize_rejects_blank_and_malformed_json()
    {
        Assert.Throws<ArgumentException>(() => SimulationReplay.Deserialize(""));
        Assert.Throws<ArgumentException>(() => SimulationReplay.Deserialize("   "));
        Assert.Throws<JsonException>(() => SimulationReplay.Deserialize("{not json"));
    }
}
