// T2: pure contract — K-6 sim-side draft (T-023 remainder).

namespace EraCore.Contracts.Sim;

/// <summary>
/// Closed verb set of presentation→simulation commands (K-6 draft). Player
/// interactions and approved system actions both enter the deterministic
/// core ONLY through this queue — never by mutating state directly
/// (03-decomposition §1.2 determinism principle; design v3.2 §6).
/// </summary>
public enum SimCommandKind
{
    /// <summary>
    /// Player build/upgrade/sell via the same BuildPlan execution path as the
    /// system, under the same cell limit (≤ 4+2×E cells, v3.2 §6).
    /// </summary>
    PlayerBuild,

    /// <summary>Approved system action (K-3 validated action JSON) executed at its scheduled tick.</summary>
    SystemAction,

    /// <summary>Pause / game-speed change (presentation-time control, journaled for replay).</summary>
    TimeControl,
}

/// <summary>
/// One command enqueued into the simulation (K-6 draft). Commands apply at
/// tick boundaries in enqueue order; the K-8 journal records the same
/// stream, so the replay invariant "same seed + same command stream ⇒ same
/// state hash" holds (03-decomposition §1.2).
/// </summary>
/// <param name="IssuedAtTick">Tick at which the command was enqueued (ordering key, K-1 IClock domain).</param>
/// <param name="Kind">Which closed verb this command carries.</param>
/// <param name="PayloadJson">
/// Command payload as validated JSON — K-3 schema for system actions,
/// K-2 content ids for builds. Unvalidated payloads are never accepted.
/// </param>
public sealed record SimCommand(
    long IssuedAtTick,
    SimCommandKind Kind,
    string PayloadJson);
