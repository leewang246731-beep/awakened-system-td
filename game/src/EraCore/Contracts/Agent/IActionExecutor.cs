// T2: pure contract — K-5 agent pipeline and card ports, T-023, draft (K number re-homed per 03-decomposition §6 in round 2; signatures pending C-13 ratification, 07-decided-plan D-21).

using EraCore.Contracts.Actions;

namespace EraCore.Contracts.Agent;

/// <summary>
/// Stage 5 execution port (K-5 draft, C-13): runs one plan step through the
/// ONLY lawful path — closed-schema validation, state validation, action
/// registry and budget ledger (RL6: no bypass, no test backdoor). Failure
/// returns a typed <see cref="ActionResult"/> and feeds the ≤ 2-retry
/// self-correction loop.
/// </summary>
public interface IActionExecutor
{
    /// <summary>Validates and executes one step's action JSON against simulation state.</summary>
    ActionResult Execute(SystemActionKind kind, string actionJson);
}
