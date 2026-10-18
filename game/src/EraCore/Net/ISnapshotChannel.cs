// T2: pure contract — T-131 联机快照推送端口预留（F-161 草案；实现域=T-139）。

using EraCore.Sim;

namespace EraCore.Net;

/// <summary>
/// Snapshot push channel of the reserved multiplayer face (T-131, F-161;
/// review doc docs/engineering/48-t131-multiplayer-architecture.md §五).
/// Host-to-client one-way lane for periodic state digests: the host
/// publishes its authoritative (tick, state hash) pair — the existing
/// <see cref="SimHashSample"/> record, i.e. exactly one row of the replay
/// hash timeline — and clients compare it against their locally recomputed
/// <c>Simulation.ComputeStateHash()</c> to detect divergence (desync alarm).
/// The cadence itself (08/T-139 acceptance: 200 ms = 6 ticks at the frozen
/// 30 Hz) lives in the T-139 host loop, not in this port.
/// <para>
/// Late-join and reconnect restore do NOT ride this channel: they reuse the
/// existing checkpoint system (<c>WaveSnapshotRecord</c> = seed + command
/// stream, restored by deterministic replay — K-7/K-6 untouched), which is
/// the「快照=检查点既有体系的网络化」argument of the review doc.
/// </para>
/// <para>
/// Reservation discipline: the member set is frozen to「status + publish +
/// drain」; payload widening (checkpoint records, deltas) is a T-139 review
/// decision.
/// </para>
/// </summary>
public interface ISnapshotChannel
{
    /// <summary>Current link status of this end (reported, never pushed).</summary>
    NetLinkStatus Status { get; }

    /// <summary>
    /// Publishes one digest toward the connected peers (host side). A digest
    /// published while the link is not <see cref="NetLinkStatus.Online"/> is
    /// dropped without error — digests are periodic, losing one edge sample
    /// is cheaper than buffering them.
    /// </summary>
    /// <param name="digest">The (tick, state hash) pair to push.</param>
    void Publish(SimHashSample digest);

    /// <summary>
    /// Drains every digest received since the previous call, oldest first
    /// (client side); empty when nothing arrived.
    /// </summary>
    /// <returns>Received digests in publish order (never null).</returns>
    IReadOnlyList<SimHashSample> DrainIncoming();
}
