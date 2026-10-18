using System.Text.Json;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using EraNet;
using Xunit;

namespace EraCore.Tests.Net;

// [F-161] T-139 线报文层：会话快照（seed+广播命令流+tick，K-7 记录形状）、
// 握手（协议版本+核心版本+内容指纹）、内容指纹（顺序敏感 FNV-1a）与
// EraNetJsonContext 全类型往返（AOT 线格式冻结面——snake_case 对齐仓库惯例）。
public class NetWireMessagesTests
{
    [Fact] // legal — 会话快照 JSON 往返逐字段全等（线格式冻结）
    public void Session_snapshot_round_trips_through_the_wire_context()
    {
        var snapshot = new NetSessionSnapshot(
            Seed: 20261014,
            Tick: 4321,
            SnapshotEveryTicks: 6,
            Commands: new SimCommandLog[]
            {
                new(60, "PlayerBuild", "{\"verb\":\"build-labor-camp\",\"cell\":{\"x\":4,\"y\":20}}"),
                new(120, "TimeControl", "{\"speed\":2}"),
            });

        string json = JsonSerializer.Serialize(snapshot, EraNetJsonContext.Default.NetSessionSnapshot);
        NetSessionSnapshot back = JsonSerializer.Deserialize(json, EraNetJsonContext.Default.NetSessionSnapshot)!;

        Assert.Equal(snapshot.Seed, back.Seed);
        Assert.Equal(snapshot.Tick, back.Tick);
        Assert.Equal(snapshot.SnapshotEveryTicks, back.SnapshotEveryTicks);
        Assert.Equal(snapshot.Commands, back.Commands); // 记录级逐项全等（线格式冻结）
        Assert.Contains("\"snapshot_every_ticks\":6", json); // snake_case 线格式锚
    }

    [Fact] // legal — SimCommand 线形状：Kind 走字符串（与 K-8 回放流同词表）
    public void Sim_command_wire_form_uses_string_kind()
    {
        var command = new SimCommand(240, SimCommandKind.SystemAction, "{\"action\":\"LookAround\"}");
        string json = JsonSerializer.Serialize(command, EraNetJsonContext.Default.SimCommand);
        var back = JsonSerializer.Deserialize<SimCommand>(json, EraNetJsonContext.Default.SimCommand)!;

        Assert.Equal(command, back);
        Assert.Contains("\"kind\":\"SystemAction\"", json);
    }

    [Fact] // legal — 内容指纹：同清单同序=同指纹；任一文件变=指纹变；序变=指纹变
    public void Content_fingerprint_is_order_sensitive_and_content_sensitive()
    {
        string[] files = { "{\"towers\":[1]}", "{\"enemies\":[2]}" };
        string baseline = NetContentFingerprint.Compute(files);

        Assert.Equal(baseline, NetContentFingerprint.Compute(new[] { "{\"towers\":[1]}", "{\"enemies\":[2]}" }));
        Assert.NotEqual(baseline, NetContentFingerprint.Compute(new[] { "{\"towers\":[1]}", "{\"enemies\":[3]}" }));
        Assert.NotEqual(baseline, NetContentFingerprint.Compute(new[] { "{\"enemies\":[2]}", "{\"towers\":[1]}" })); // 序敏感
        Assert.NotEqual(
            NetContentFingerprint.Compute(new[] { "ab", "c" }),
            NetContentFingerprint.Compute(new[] { "a", "bc" })); // 拼接歧义被长度分隔堵死
    }

    [Fact] // legal — 握手匹配：三字段全等才可入房；任一不等拒绝
    public void Handshake_matches_only_on_all_three_frozen_fields()
    {
        var host = new NetHandshakeInfo(NetWireProtocol.Version, "1.0.0", "abc");
        Assert.True(new NetHandshakeInfo(NetWireProtocol.Version, "1.0.0", "abc").Matches(host));
        Assert.False(new NetHandshakeInfo(NetWireProtocol.Version, "1.0.1", "abc").Matches(host)); // 核心版本
        Assert.False(new NetHandshakeInfo(NetWireProtocol.Version, "1.0.0", "abd").Matches(host)); // 内容指纹
        Assert.False(new NetHandshakeInfo(NetWireProtocol.Version + 1, "1.0.0", "abc").Matches(host)); // 协议版本
    }

    [Fact] // boundary — 协议版本冻结：T-139 v1=1（bump=破坏性变更须过评审）
    public void Protocol_version_is_frozen_at_v1()
    {
        Assert.Equal(1, NetWireProtocol.Version);
    }

    [Fact] // legal — 快照节拍常量=验收词换算：30 Hz × 200 ms = 6 tick（非手填）
    public void Snapshot_cadence_derives_from_the_acceptance_word()
    {
        Assert.Equal(6, NetTiming.SnapshotCadenceTicks);
        Assert.Equal(Simulation.TicksPerSecond * 200 / 1000, NetTiming.SnapshotCadenceTicks);
    }

    [Fact] // legal — 军师请求/结论往返（枚举字符串+空文本字段）
    public void Advisor_messages_round_trip()
    {
        var request = new AdvisorOrderRequest(7, AdvisorOrderRequestKind.Confirm, null, 12);
        string requestJson = JsonSerializer.Serialize(request, EraNetJsonContext.Default.AdvisorOrderRequest);
        Assert.Equal(request, JsonSerializer.Deserialize<AdvisorOrderRequest>(requestJson, EraNetJsonContext.Default.AdvisorOrderRequest));

        var reply = new AdvisorOrderReply(
            7, 3, "守住东门", EraCore.Agent.Pipeline.PipelineOutcomeKind.AwaitingApproval, null, 12, "方案卡挂起");
        string replyJson = JsonSerializer.Serialize(reply, EraNetJsonContext.Default.AdvisorOrderReply);
        Assert.Equal(reply, JsonSerializer.Deserialize<AdvisorOrderReply>(replyJson, EraNetJsonContext.Default.AdvisorOrderReply));
    }
}
