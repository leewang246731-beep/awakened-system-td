// T2: pure contract — K-3 system action schema, T-021, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Actions;

/// <summary>Outcome statuses of a system action (K-3).</summary>
public enum ActionResultStatus
{
    /// <summary>Executed; points were charged as declared.</summary>
    Ok,

    /// <summary>Generic failure (state validation, unsatisfiable precondition, …).</summary>
    Err,

    /// <summary>
    /// Era/capability not unlocked yet (ERR_CAPABILITY_LOCKED, N9/A2) —
    /// answered with clarifying copy, never silently dropped.
    /// </summary>
    ErrCapabilityLocked,

    /// <summary>Action JSON failed closed-schema validation (RL6 gate).</summary>
    ErrSchemaDenied,

    /// <summary>Approval gate returned deny (danger bit or player refusal).</summary>
    ErrPermissionDenied,

    /// <summary>
    /// System-point budget guard refused: insufficient points or the
    /// per-wave spend would exceed 40% of stock (N4; the plan-level 30%
    /// resource guard lives in the same C-14 ledger).
    /// </summary>
    ErrBudgetDenied,

    /// <summary>Referenced target does not exist or is not addressable.</summary>
    ErrTargetInvalid,
}

/// <summary>
/// Execution result of one system action (K-3). Consumed by the agent
/// pipeline's self-correction loop (≤ 2 retries, C-13) and journaled
/// through K-8.
/// </summary>
/// <param name="Status">Outcome class; anything but <see cref="ActionResultStatus.Ok"/> is a failure.</param>
/// <param name="ErrorMessage">Failure cause for logs/journal; null on success.</param>
/// <param name="PointsSpent">System points actually charged (0 on failure — failed actions never bill).</param>
public sealed record ActionResult(
    ActionResultStatus Status,
    string? ErrorMessage = null,
    int PointsSpent = 0);
