using System.Globalization;

namespace EraCore.Presentation;

/// <summary>
/// Where an alarm renders (T-075, F-097/§10.7): the system-on persona
/// channel (advisor report bubble) or the system-off neutral banner.
/// </summary>
public enum AlarmChannel
{
    /// <summary>System on — the advisor persona delivers the alarm (report bubble).</summary>
    PersonaBubble,

    /// <summary>System off — the neutral HUD banner shows the alarm (red), no persona wording.</summary>
    NeutralBanner,
}

/// <summary>
/// Off-mode surface matrix (T-075, F-097): which C-22 surfaces stay visible
/// for each switch position. All input surfaces (order box, quick wheel,
/// pending plan card, trust badge) hide when the system is off; the HUD
/// three-metric banner survives in neutral style and alarms keep arriving
/// through the neutral channel (功能无损).
/// </summary>
/// <param name="OrderInputVisible">Order input box visible.</param>
/// <param name="WheelVisible">Quick wheel visible.</param>
/// <param name="PlanCardVisible">Pending plan-card popup visible.</param>
/// <param name="TrustBadgeVisible">Advisor-avatar trust tier badge visible.</param>
/// <param name="HudNeutral">HUD renders as the neutral banner (off) instead of the holo skin (on).</param>
/// <param name="Alarm">Channel alarms render through.</param>
public sealed record OffModeSurfaces(
    bool OrderInputVisible,
    bool WheelVisible,
    bool PlanCardVisible,
    bool TrustBadgeVisible,
    bool HudNeutral,
    AlarmChannel Alarm);

/// <summary>
/// One defense-line alarm decision (pure): whether it fires at all, the
/// channel it renders through and the exact text of the line.
/// </summary>
/// <param name="Fires">true when the integrity crossed the warning line (crisis cue + red display).</param>
/// <param name="Channel">Render channel for the switch position.</param>
/// <param name="Text">Alarm line (persona template when on, neutral fact when off); empty when not firing.</param>
public sealed record DefenseAlarmView(bool Fires, AlarmChannel Channel, string Text);

/// <summary>
/// Pure decision core of the C-22 off-mode downgrade (T-075, F-097 +
/// §10.7 关闭系统=警报降级中性横幅+音效，功能无损). The Godot panel mirrors
/// these decisions into node visibility/styles; nothing here touches engine
/// types (D-01). The alarm line threshold is the frozen §10.3 warning
/// ("警告！防线完整度低于 40%" — F-105 台词库, 联动 F-111 HUD 完整度).
/// </summary>
public static class OffModeDowngrade
{
    /// <summary>Defense-integrity warning line (§10.3/F-105: 低于 40% 触发危机警报).</summary>
    public const double DefenseAlarmBelow = 0.40d;

    /// <summary>Persona alarm line while the system is on (F-105 台词库原文).</summary>
    public const string PersonaAlarmLine = "警告！防线完整度低于 40%，建议立即加固！";

    /// <summary>
    /// The full surface matrix for one switch position — the single source
    /// the panel mirrors (三指标/输入面/徽记/警报通道的开关×全降级判定).
    /// </summary>
    public static OffModeSurfaces Surfaces(bool systemEnabled) => new(
        OrderInputVisible: systemEnabled,
        WheelVisible: systemEnabled,
        PlanCardVisible: systemEnabled,
        TrustBadgeVisible: systemEnabled,
        HudNeutral: !systemEnabled,
        Alarm: systemEnabled ? AlarmChannel.PersonaBubble : AlarmChannel.NeutralBanner);

    /// <summary>
    /// Defense-line alarm decision for one integrity reading: fires strictly
    /// below <see cref="DefenseAlarmBelow"/> in BOTH switch positions
    /// (警报不缺失 — the off-mode never silences a crisis), and the text is
    /// persona-worded only while the system is on; off-mode states the bare
    /// fact ("防线完整度 35%") with no advisor voice (§10.7 中性措辞).
    /// </summary>
    /// <param name="systemEnabled">Switch position.</param>
    /// <param name="defenseIntegrity">Tower-line HP ratio, 0..1 (F-111 second metric).</param>
    /// <exception cref="ArgumentOutOfRangeException">Integrity outside [0, 1].</exception>
    public static DefenseAlarmView DefenseAlarmAt(bool systemEnabled, double defenseIntegrity)
    {
        if (defenseIntegrity < 0d || defenseIntegrity > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defenseIntegrity), defenseIntegrity, "defense integrity must be within [0,1].");
        }

        AlarmChannel channel = systemEnabled ? AlarmChannel.PersonaBubble : AlarmChannel.NeutralBanner;
        if (defenseIntegrity >= DefenseAlarmBelow)
        {
            return new DefenseAlarmView(Fires: false, channel, string.Empty);
        }

        string text = systemEnabled
            ? PersonaAlarmLine
            : "防线完整度 "
                + ((int)Math.Round(defenseIntegrity * 100d, MidpointRounding.AwayFromZero))
                    .ToString(CultureInfo.InvariantCulture)
                + "%";
        return new DefenseAlarmView(Fires: true, channel, text);
    }
}
