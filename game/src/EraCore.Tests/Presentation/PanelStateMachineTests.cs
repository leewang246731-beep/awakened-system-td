using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-111] [F-115] [F-116] T-055 面板三态机（气泡/展开/隐藏）：
// 合法切换、边界（关→开恢复记忆态、重复开关幂等）、非法（隐藏态拒绝展开）。
public class PanelStateMachineTests
{
    // ---- legal — enabled-state toggling ------------------------------------

    [Fact] // legal — 默认上电=Bubble（系统在、面板收起为气泡）
    public void Default_start_is_bubble_with_system_enabled()
    {
        var machine = new PanelStateMachine();

        Assert.True(machine.SystemEnabled);
        Assert.Equal(PanelState.Bubble, machine.State);
        Assert.True(machine.PanelVisible);
        Assert.False(machine.OrderBoxVisible);
        Assert.False(machine.WheelVisible);
        Assert.False(machine.HudNeutral);
    }

    [Fact] // legal — Bubble↔Expanded 往返，输入面可见性随之翻转
    public void SetExpanded_toggles_between_bubble_and_expanded()
    {
        var machine = new PanelStateMachine();

        Assert.True(machine.SetExpanded(true));
        Assert.Equal(PanelState.Expanded, machine.State);
        Assert.True(machine.PanelVisible);
        Assert.True(machine.OrderBoxVisible);
        Assert.True(machine.WheelVisible);

        Assert.True(machine.SetExpanded(false));
        Assert.Equal(PanelState.Bubble, machine.State);
        Assert.False(machine.OrderBoxVisible);
        Assert.False(machine.WheelVisible);
    }

    // ---- boundary — off-switch memory --------------------------------------

    [Fact] // boundary — 从 Expanded 关闭→Hidden（全输入面隐藏+HUD 中性），再开→Expanded 原样恢复
    public void Off_switch_hides_then_restores_the_remembered_state()
    {
        var machine = new PanelStateMachine();
        machine.SetExpanded(true);

        machine.SetSystemEnabled(false);
        Assert.False(machine.SystemEnabled);
        Assert.Equal(PanelState.Hidden, machine.State);
        Assert.False(machine.PanelVisible);
        Assert.False(machine.OrderBoxVisible);
        Assert.False(machine.WheelVisible);
        Assert.True(machine.HudNeutral);

        machine.SetSystemEnabled(true);
        Assert.Equal(PanelState.Expanded, machine.State);
        Assert.True(machine.OrderBoxVisible);
        Assert.False(machine.HudNeutral);
    }

    [Fact] // boundary — 从 Bubble 关闭再开→Bubble（记忆的是关闭瞬间的态）
    public void Off_from_bubble_restores_bubble()
    {
        var machine = new PanelStateMachine();

        machine.SetSystemEnabled(false);
        machine.SetSystemEnabled(true);

        Assert.Equal(PanelState.Bubble, machine.State);
    }

    [Fact] // boundary — 上电即关（构造 false）→Hidden；再开→默认 Bubble
    public void Constructed_disabled_starts_hidden_and_reenables_to_bubble()
    {
        var machine = new PanelStateMachine(systemEnabled: false);

        Assert.Equal(PanelState.Hidden, machine.State);
        Assert.False(machine.SystemEnabled);

        machine.SetSystemEnabled(true);
        Assert.Equal(PanelState.Bubble, machine.State);
    }

    [Fact] // boundary — 重复关闭不覆盖记忆：Hidden 态再关一次，恢复目标仍是首个被记的 Expanded
    public void Repeated_disable_keeps_the_first_memory()
    {
        var machine = new PanelStateMachine();
        machine.SetExpanded(true);

        machine.SetSystemEnabled(false);
        machine.SetSystemEnabled(false); // already Hidden — must not clobber memory
        machine.SetSystemEnabled(true);

        Assert.Equal(PanelState.Expanded, machine.State);
    }

    [Fact] // boundary — 已处于目标开关位时 SetSystemEnabled 为无操作（状态与记忆均不变）
    public void Redundant_switch_calls_are_no_ops()
    {
        var machine = new PanelStateMachine();

        machine.SetSystemEnabled(true);

        Assert.Equal(PanelState.Bubble, machine.State);
        machine.SetSystemEnabled(false);
        machine.SetSystemEnabled(false);
        Assert.Equal(PanelState.Hidden, machine.State);
    }

    // ---- illegal — hidden rejects input-surface transitions -----------------

    [Fact] // illegal — 系统关闭（Hidden）下展开/收起被拒绝：返回 false 且状态不变
    public void SetExpanded_is_rejected_while_hidden()
    {
        var machine = new PanelStateMachine();
        machine.SetSystemEnabled(false);

        Assert.False(machine.SetExpanded(true));
        Assert.Equal(PanelState.Hidden, machine.State);

        Assert.False(machine.SetExpanded(false));
        Assert.Equal(PanelState.Hidden, machine.State);
    }
}
