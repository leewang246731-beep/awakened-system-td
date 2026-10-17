using EraCore.Agent.Actions;

namespace EraCore.Agent.Autopilot;

/// <summary>
/// Campaign-lane era gate of the full-autopilot hosting right (T-134, v3.2
/// §4 L62 系统形态列「E5 未来 = 全托管可选（设计既定）」): the campaign
/// system-stage ladder (E1 绑定期 → E2 执行期 → E3 自主期 → E4 多核期 →
/// E5 全托管可选) hands the FULL hosting right over at E5 — before that the
/// campaign lane refuses the engage (see
/// <see cref="AutopilotDirector.EngageFullAutopilot(int)"/>) while the
/// endless tri-board lane (SP-7 手动/系统辅助/全托管三榜, 任选时代) is
/// explicitly OUTSIDE this gate (the raw
/// <see cref="AutopilotDirector.EngageFullAutopilot()"/> stays the endless /
/// harness entry).
/// </summary>
/// <remarks>
/// Data face: era5/levels.json slot 3 unlocks feature <c>full_autopilot</c>
/// (era5/era.json system_stage_features <c>full_autopilot_optional</c> 同源)
/// — the schedule row, the era row and this gate are three faces of one
/// design sentence. The unlock ordinal is a structure constant of the system
/// stage ladder (EraOrdinals 量纲), not a balance number (R7 家族).
/// </remarks>
public static class AutopilotEraGate
{
    /// <summary>
    /// First era ordinal whose CAMPAIGN may engage full autopilot: E5
    /// (全托管可选， v3.2 §4 L62). E1~E4 refuse; 终章 (E6, T-135 域) keeps
    /// the unlocked state E5 hands over.
    /// </summary>
    public const int FullAutopilotUnlockEra = EraOrdinals.E5; // ci-allow: R7 T-134 v3.2 §4 L62 系统形态列「E5 全托管可选」（时代序号量纲，非平衡数值）

    /// <summary>
    /// Whether the campaign lane at <paramref name="campaignEraIndex"/> may
    /// engage full autopilot (pure predicate of the gate — the director's
    /// era-gated engage throws its exception through this).
    /// </summary>
    public static bool CampaignMayEngageFullAutopilot(int campaignEraIndex) =>
        campaignEraIndex >= FullAutopilotUnlockEra;
}
