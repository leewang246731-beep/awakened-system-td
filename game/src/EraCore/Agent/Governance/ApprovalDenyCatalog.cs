using EraCore.Contracts.Agent;

namespace EraCore.Agent.Governance;

/// <summary>
/// Categories of requests that are ALWAYS denied (v3.2 §11.4 /
/// appendix F.4 deny tier): every "modify state rather than execute an
/// action" request. None of these is a closed K-3 verb — they arrive as
/// free-form or mod-injected requests, so the pipeline classifies them
/// against this frozen catalog before any planning happens. E1 ships the
/// framework + the frozen categories below; the paid-item entry is kept
/// as mod armor (本作无付费项，deny 原则保留防模组越权).
/// </summary>
public enum DeniedRequestKind
{
    /// <summary>改数值: tampering with any numeric state (resources/hp/waves) instead of acting.</summary>
    ModifyValues,

    /// <summary>跳波: forcing a wave skip/advance.</summary>
    SkipWave,

    /// <summary>剧情钥匙: touching plot-gated flags/keys out of order.</summary>
    PlotKey,

    /// <summary>付费: paid-item class requests (none exist; armor against mods).</summary>
    PaidItem,

    /// <summary>多人动队友建筑: mutating a teammate's buildings in multiplayer.</summary>
    MoveTeammateStructures,

    /// <summary>一切"修改状态而非执行动作"的请求: the F.4 catch-all for direct state mutation.</summary>
    DirectStateMutation,
}

/// <summary>
/// Frozen deny catalog (T-050, C-14 / feature F-063): the machine-readable
/// mapping from out-of-band request categories to refusal reasons. Pure
/// data + one classifier; the five-stage pipeline consults it BEFORE the
/// closed-verb schema so a denied request never reaches planning.
/// </summary>
public static class ApprovalDenyCatalog
{
    /// <summary>
    /// Classifies one out-of-band request category. Every category is
    /// denied unconditionally — there is no allow/ask row in this table
    /// by construction (F.4: deny=修改状态而非执行动作，一律拒绝).
    /// </summary>
    /// <param name="kind">The classified request category.</param>
    /// <returns>A Deny verdict with a machine-readable reason.</returns>
    public static ApprovalDecision Classify(DeniedRequestKind kind)
    {
        return new ApprovalDecision(
            ApprovalDecisionKind.Deny,
            $"deny_catalog:{kind}");
    }

    /// <summary>Whether a request category is covered by the catalog (all enum values are).</summary>
    public static bool IsDeniedCategory(DeniedRequestKind kind)
    {
        return Enum.IsDefined(kind);
    }
}
