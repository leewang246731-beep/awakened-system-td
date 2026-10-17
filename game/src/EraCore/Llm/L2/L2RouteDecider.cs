using EraCore.Presentation;

namespace EraCore.Llm.L2;

/// <summary>
/// The T-137 three-tier route decider — the pure routing state machine the
/// order path consults BEFORE dispatching an order (F-078/C-27). Inputs: the
/// player's persisted tier selection, the live L2 runtime snapshot, the
/// platform tier row (<see cref="WebConstraints"/>). Output: one
/// <see cref="L2RouteDecision"/>. The matrix is the acceptance-critical
/// surface (08 T-137 行验收词：「失败降 L1」) — every L2-side failure phase
/// maps to an L1 verdict with a distinct reason, and L1 itself can never be
/// downgraded (it is the floor tier).
/// </summary>
public static class L2RouteDecider
{
    /// <summary>
    /// Decides the effective tier for one order.
    /// Matrix (frozen — mirrored 1:1 in L2RouteDeciderTests, the 降级回归判据):
    /// <list type="bullet">
    /// <item>L1 selected → L1 direct (the floor tier never downgrades);</item>
    /// <item>L2 selected + platform L2 off (Web) → L1, WebTier;</item>
    /// <item>L2 selected + ModelMissing → L1, ModelMissing;</item>
    /// <item>L2 selected + RuntimeMissing → L1, RuntimeMissing;</item>
    /// <item>L2 selected + Downloading/Starting/Idle → L1, NotReady;</item>
    /// <item>L2 selected + ProcessDead → L1, ProcessDead;</item>
    /// <item>L2 selected + Ready → L2 direct;</item>
    /// <item>L3 selected + platform L3 off (Web) → L1, WebTier (L3 BYOK 同受
    /// Web 零网络约束, T-106);</item>
    /// <item>L3 selected + desktop → L3 direct (its own failures stay in the
    /// gateway error path — L3 不经本降级矩阵, its rows are refusal-classified
    /// inside <c>LlmGateway</c>).</item>
    /// </list>
    /// FillTimeout/FillFailed are NOT decided here — they occur after dispatch
    /// (wave-gap window) and are appended by the fill queue when an order
    /// falls back.
    /// </summary>
    /// <param name="selection">Player-selected tier (persisted settings/l2-tier.json).</param>
    /// <param name="runtime">Live L2 lane snapshot (ignored for L1/L3 selections).</param>
    /// <param name="platform">Platform tier row (the only Web-fact source, 能力表驱动).</param>
    public static L2RouteDecision Decide(
        LlmTierKind selection,
        L2RuntimeSnapshot runtime,
        AgentTierAvailability platform)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(platform);

        return selection switch
        {
            LlmTierKind.L1Grammar => L2RouteDecision.Direct(LlmTierKind.L1Grammar),
            LlmTierKind.L2Local => DecideL2(runtime, platform),
            LlmTierKind.L3Cloud => platform.L3CloudByok
                ? L2RouteDecision.Direct(LlmTierKind.L3Cloud)
                : L2RouteDecision.DegradeToL1(L2DegradationReason.WebTier),
            _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, "Unknown tier selection."),
        };
    }

    private static L2RouteDecision DecideL2(L2RuntimeSnapshot runtime, AgentTierAvailability platform)
    {
        if (!platform.L2LocalModel)
        {
            return L2RouteDecision.DegradeToL1(L2DegradationReason.WebTier);
        }

        return runtime.Phase switch
        {
            L2RuntimePhase.Ready => L2RouteDecision.Direct(LlmTierKind.L2Local),
            L2RuntimePhase.ModelMissing => L2RouteDecision.DegradeToL1(L2DegradationReason.ModelMissing),
            L2RuntimePhase.RuntimeMissing => L2RouteDecision.DegradeToL1(L2DegradationReason.RuntimeMissing),
            L2RuntimePhase.Downloading or L2RuntimePhase.Starting or L2RuntimePhase.Idle
                => L2RouteDecision.DegradeToL1(L2DegradationReason.NotReady),
            L2RuntimePhase.ProcessDead => L2RouteDecision.DegradeToL1(L2DegradationReason.ProcessDead),
            // Unnamed/future phase values must not be silently absorbed into a
            // degradation reason — throw like the outer selection switch (batch-4 review #2).
            _ => throw new ArgumentOutOfRangeException(nameof(runtime.Phase), runtime.Phase, "unknown runtime phase."),
        };
    }
}
