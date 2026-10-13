namespace EraCore.Presentation;

/// <summary>
/// Visibility state of the C-22 system hologram panel (T-055, F-111).
/// The three states of the thin version:
/// <list type="bullet">
/// <item><see cref="Bubble"/> — collapsed chat bubble (default avatar +
/// latest report line, the resting look between orders);</item>
/// <item><see cref="Expanded"/> — full panel: HUD strip + order input +
/// quick wheel (the input surfaces live here);</item>
/// <item><see cref="Hidden"/> — "system off" (pillar 4: 关闭系统可全通关):
/// panel, order box and wheel are all hidden; the HUD metrics survive in
/// neutral-banner form (functionality lossless).</item>
/// </list>
/// </summary>
public enum PanelState
{
    /// <summary>Collapsed chat bubble — resting state while the system is on.</summary>
    Bubble,

    /// <summary>Expanded panel — order input + quick wheel visible.</summary>
    Expanded,

    /// <summary>System off — every system-owned surface hidden (HUD neutral).</summary>
    Hidden,
}

/// <summary>
/// Pure three-state machine behind the C-22 panel visibility (T-055,
/// F-111/F-115/F-116; pillar 4 off-switch). No engine types (D-01) so the
/// Godot presenter only mirrors <see cref="State"/> into node visibility.
/// Rules:
/// <list type="number">
/// <item>While enabled: <see cref="SetExpanded"/> toggles
/// <see cref="PanelState.Bubble"/> ↔ <see cref="PanelState.Expanded"/>;</item>
/// <item><see cref="SetSystemEnabled"/>(false) hides everything and remembers
/// the prior state; repeated disabling keeps the first memory (the state is
/// already Hidden — nothing new to remember);</item>
/// <item><see cref="SetSystemEnabled"/>(true) restores the remembered state
/// (default <see cref="PanelState.Bubble"/> when disabled from scratch);</item>
/// <item><see cref="SetExpanded"/> while Hidden is rejected (returns false,
/// state unchanged) — with the system off there is no order input at all.</item>
/// </list>
/// </summary>
public sealed class PanelStateMachine
{
    private PanelState _restoredState = PanelState.Bubble;

    /// <param name="systemEnabled">Initial switch position (default on).</param>
    public PanelStateMachine(bool systemEnabled = true)
    {
        SystemEnabled = systemEnabled;
        State = systemEnabled ? PanelState.Bubble : PanelState.Hidden;
    }

    /// <summary>Current visibility state.</summary>
    public PanelState State { get; private set; }

    /// <summary>Whether the system (agent surfaces) is enabled at all.</summary>
    public bool SystemEnabled { get; private set; }

    /// <summary>Panel (bubble or expanded body) is on screen.</summary>
    public bool PanelVisible => State != PanelState.Hidden;

    /// <summary>Order input box visible (expanded state only).</summary>
    public bool OrderBoxVisible => State == PanelState.Expanded;

    /// <summary>Quick wheel visible (expanded state only; wheel = input surface).</summary>
    public bool WheelVisible => State == PanelState.Expanded;

    /// <summary>Hud render mode: holo skin while the system is on, neutral banner while off (functionality kept).</summary>
    public bool HudNeutral => State == PanelState.Hidden;

    /// <summary>
    /// Toggles between the collapsed bubble and the expanded panel.
    /// </summary>
    /// <returns>true when the transition happened; false when the system is
    /// off (<see cref="PanelState.Hidden"/> rejects input-surface changes).</returns>
    public bool SetExpanded(bool expanded)
    {
        if (State == PanelState.Hidden)
        {
            return false;
        }

        State = expanded ? PanelState.Expanded : PanelState.Bubble;
        return true;
    }

    /// <summary>
    /// Moves the off-switch. Disabling hides the system and remembers where
    /// it was; enabling restores exactly that state (pillar 4: toggling the
    /// system back on never loses the player's working layout).
    /// </summary>
    public void SetSystemEnabled(bool enabled)
    {
        if (enabled == SystemEnabled)
        {
            return; // switch position unchanged — no transition, memory kept
        }

        if (enabled)
        {
            State = _restoredState;
        }
        else
        {
            _restoredState = State; // Hidden-over-Hidden keeps the older memory
            State = PanelState.Hidden;
        }

        SystemEnabled = enabled;
    }
}
