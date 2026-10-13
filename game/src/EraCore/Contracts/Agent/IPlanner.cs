// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

using EraCore.Contracts.Intents;

namespace EraCore.Contracts.Agent;

/// <summary>
/// Stage 3 of the five-stage pipeline (K-6 draft, C-13): turns a routed
/// intent into a concrete plan (steps / costs / risks). Planning reads
/// simulation state but mutates nothing.
/// </summary>
public interface IPlanner
{
    /// <summary>Builds the plan for one intent, flagging danger bits among the risks.</summary>
    Plan Plan(Intent intent);
}
