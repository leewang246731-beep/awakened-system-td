using System.Reflection;
using EraCore;
using EraCore.Net;
using Xunit;

namespace EraCore.Tests.Net;

// [F-161] T-131 联机预留——预留面冻结契约：两个端口接口的成员集恰为
// 「status+上行+下行」三件（Speculative Generality 红线的可执行形式——
// 加宽=先过评审），且 EraCore 内零实现（实现域=T-139，禁半截实现混入）。
public class NetReservationContractTests
{
    private static System.Reflection.Assembly CoreAssembly => typeof(Guard).Assembly;

    [Fact] // boundary — ICommandTransport 成员集恰为预留三件，不得静默加宽
    public void Command_transport_surface_is_frozen_to_status_submit_drain()
    {
        var members = typeof(ICommandTransport)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "DrainIncoming", "Submit", "get_Status" }, members);
    }

    [Fact] // boundary — ISnapshotChannel 成员集恰为预留三件，不得静默加宽
    public void Snapshot_channel_surface_is_frozen_to_status_publish_drain()
    {
        var members = typeof(ISnapshotChannel)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "DrainIncoming", "Publish", "get_Status" }, members);
    }

    [Fact] // boundary — 状态枚举恰三值且序数稳定（线协议词表冻结）
    public void Link_status_enum_is_frozen_to_three_ordered_values()
    {
        var names = Enum.GetNames<NetLinkStatus>();
        var values = Enum.GetValues<NetLinkStatus>();

        Assert.Equal(new[] { "Offline", "Connecting", "Online" }, names);
        Assert.Equal(new[] { 0, 1, 2 }, values.Select(v => (int)v));
    }

    [Fact] // boundary — EraCore 内零实现：预留=纯端口，实现随 T-139 落地
    public void No_EraCore_type_implements_the_reserved_ports()
    {
        var implementors = CoreAssembly.GetExportedTypes()
            .Where(t => t != typeof(ICommandTransport) && t != typeof(ISnapshotChannel)
                && (typeof(ICommandTransport).IsAssignableFrom(t)
                    || typeof(ISnapshotChannel).IsAssignableFrom(t)))
            .ToList();

        Assert.Empty(implementors);
    }

    [Fact] // boundary — 状态机是词表不是传输：不实现任何预留端口
    public void State_machine_is_vocabulary_not_a_transport()
    {
        Assert.False(typeof(ICommandTransport).IsAssignableFrom(typeof(NetLinkStateMachine)));
        Assert.False(typeof(ISnapshotChannel).IsAssignableFrom(typeof(NetLinkStateMachine)));
    }

    [Fact] // legal — 预留载荷复用既有确定性类型（SimCommand/SimHashSample），零新线格式
    public void Reserved_payloads_reuse_existing_deterministic_types()
    {
        MethodInfo submit = typeof(ICommandTransport).GetMethod("Submit")!;
        MethodInfo drainCommands = typeof(ICommandTransport).GetMethod("DrainIncoming")!;
        MethodInfo publish = typeof(ISnapshotChannel).GetMethod("Publish")!;
        MethodInfo drainDigests = typeof(ISnapshotChannel).GetMethod("DrainIncoming")!;

        Assert.Equal(
            "EraCore.Contracts.Sim.SimCommand",
            submit.GetParameters().Single().ParameterType.FullName);
        Assert.Equal(
            "EraCore.Sim.SimHashSample",
            publish.GetParameters().Single().ParameterType.FullName);
        Assert.Equal(
            "EraCore.Contracts.Sim.SimCommand",
            drainCommands.ReturnParameter.ParameterType.GetGenericArguments().Single().FullName);
        Assert.Equal(
            "EraCore.Sim.SimHashSample",
            drainDigests.ReturnParameter.ParameterType.GetGenericArguments().Single().FullName);
    }
}
