// T2: pure contract — K-1 foundation port, T-019 remainder, frozen (round 2 backfill; 07-decided-plan D-21).

namespace EraCore.Contracts.Foundation;

/// <summary>
/// Background-job port (K-1; basis D-31 — flow-field thread model). EraCore
/// holds ZERO direct thread dependencies: every off-thread workload
/// (flow-field recomputation, …) is dispatched through this port. The
/// desktop Godot adapter bridges a worker thread; the single-threaded Web
/// build degrades to time-sliced main-thread chunks; tests run jobs
/// synchronously (03-decomposition §1.2 port table).
/// </summary>
public interface IJobPort
{
    /// <summary>
    /// Enqueues a named workload. Determinism rule (D-31): results swap into
    /// the simulation only at tick boundaries by the caller (C-11); this
    /// port itself never mutates simulation state.
    /// </summary>
    /// <param name="name">Diagnostic job name (surfaced on the performance overlay, D-31).</param>
    /// <param name="workload">The pure computation to run off-thread.</param>
    /// <returns>A handle for completion polling and fault observation.</returns>
    IJobHandle Enqueue(string name, Action workload);
}

/// <summary>Handle to one enqueued background job (K-1, D-31).</summary>
public interface IJobHandle
{
    /// <summary>Diagnostic job name given at enqueue time.</summary>
    string Name { get; }

    /// <summary>True once the workload returned (successfully or faulted).</summary>
    bool IsComplete { get; }

    /// <summary>Exception thrown by the workload, if any — observed, never rethrown on the simulation thread.</summary>
    Exception? Fault { get; }
}
