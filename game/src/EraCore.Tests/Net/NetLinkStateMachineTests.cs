using EraCore.Net;
using Xunit;

namespace EraCore.Tests.Net;

// [F-161] T-131 联机预留——NetLinkStateMachine：链路状态机合法/非法迁移与
// 提交门（预留面唯一行为组件；T-139 各传输实现共享的迁移词表）。
public class NetLinkStateMachineTests
{
    [Fact] // legal — 全生命周期：Offline→Connecting→Online→Offline，提交门随动
    public void Full_lifecycle_walks_legal_transitions_and_gates_submission()
    {
        var machine = new NetLinkStateMachine();

        Assert.Equal(NetLinkStatus.Offline, machine.Status);
        Assert.False(machine.CanSubmit);

        machine.BeginConnect();
        Assert.Equal(NetLinkStatus.Connecting, machine.Status);
        Assert.False(machine.CanSubmit); // 握手中不可提交

        machine.MarkOnline();
        Assert.Equal(NetLinkStatus.Online, machine.Status);
        Assert.True(machine.CanSubmit); // 唯一可提交态

        machine.MarkOffline();
        Assert.Equal(NetLinkStatus.Offline, machine.Status);
        Assert.False(machine.CanSubmit);
    }

    [Fact] // legal — 握手失败回 Offline 后可重新发起连接（重连路径）
    public void Failed_handshake_returns_offline_and_reconnect_is_possible()
    {
        var machine = new NetLinkStateMachine();
        machine.BeginConnect();
        machine.MarkOffline(); // Connecting → Offline（握手失败/中止）

        machine.BeginConnect(); // 重连合法
        machine.MarkOnline();
        Assert.True(machine.CanSubmit);
    }

    [Fact] // boundary — Offline 上的重复断线事件=合法空操作（双 drop 回调常态）
    public void MarkOffline_from_offline_is_a_legal_no_op()
    {
        var machine = new NetLinkStateMachine();

        machine.MarkOffline(); // 未连先断
        machine.MarkOffline(); // 已断再断

        Assert.Equal(NetLinkStatus.Offline, machine.Status);
    }

    [Fact] // illegal — Connecting 中重复 BeginConnect 被拒且状态不被污染
    public void BeginConnect_while_connecting_throws_and_keeps_state()
    {
        var machine = new NetLinkStateMachine();
        machine.BeginConnect();

        Assert.Throws<InvalidOperationException>(() => machine.BeginConnect());
        Assert.Equal(NetLinkStatus.Connecting, machine.Status); // 拒绝不改状态
    }

    [Fact] // illegal — Online 中 BeginConnect 被拒（在线重连=调用方缺陷）
    public void BeginConnect_while_online_throws()
    {
        var machine = new NetLinkStateMachine();
        machine.BeginConnect();
        machine.MarkOnline();

        Assert.Throws<InvalidOperationException>(() => machine.BeginConnect());
        Assert.Equal(NetLinkStatus.Online, machine.Status);
    }

    [Fact] // illegal — Offline 直达 Online 被拒（必须经 Connecting）
    public void MarkOnline_from_offline_throws()
    {
        var machine = new NetLinkStateMachine();

        Assert.Throws<InvalidOperationException>(() => machine.MarkOnline());
        Assert.Equal(NetLinkStatus.Offline, machine.Status);
    }

    [Fact] // illegal — Online 重复 MarkOnline 被拒（在线链接不再确认）
    public void MarkOnline_while_online_throws()
    {
        var machine = new NetLinkStateMachine();
        machine.BeginConnect();
        machine.MarkOnline();

        Assert.Throws<InvalidOperationException>(() => machine.MarkOnline());
        Assert.Equal(NetLinkStatus.Online, machine.Status);
    }

    [Fact] // boundary — 非法迁移异常携带当前态与目标动作（可诊断信息）
    public void Illegal_transition_message_names_current_state_and_action()
    {
        var machine = new NetLinkStateMachine();

        var ex = Assert.Throws<InvalidOperationException>(() => machine.MarkOnline());

        Assert.Contains("Offline", ex.Message);
        Assert.Contains("online", ex.Message);
    }
}
