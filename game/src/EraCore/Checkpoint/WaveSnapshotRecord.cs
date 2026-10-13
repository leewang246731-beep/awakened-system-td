using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;

namespace EraCore.Checkpoint;

/// <summary>
/// Wave-boundary snapshot payload of one level run (C-04, T-057; K-7 level
/// domain extension point): the replay key pair "seed + command stream up to
/// the boundary tick" — exactly what <see cref="SimulationReplay"/> needs to
/// rebuild the simulation to that boundary. Stored as the
/// <see cref="Contracts.Persistence.ILevelSaveBlob.SimSnapshotPayload"/> of
/// an <see cref="Contracts.Persistence.ISavePort"/> level slot; a restore
/// replays it on a fresh <see cref="Simulation"/> (determinism contract of
/// T-040), never deserializing live objects.
/// </summary>
/// <param name="Seed">Level seed of the run (replay key #1).</param>
/// <param name="Wave">Wave number that just ENDED at the boundary (1-based).</param>
/// <param name="Tick">Tick of the boundary (end of <paramref name="Wave"/>).</param>
/// <param name="Commands">Command stream issued at or before <paramref name="Tick"/> (replay key #2).</param>
public sealed record WaveSnapshotRecord(
    long Seed,
    int Wave,
    long Tick,
    IReadOnlyList<SimCommandLog> Commands);

/// <summary>
/// Incremental command tail a rolling boundary save carries on top of its
/// basis record (K-7 "command tail since the boundary"): the commands issued
/// in the open interval (basis tick, boundary tick], plus the boundary's own
/// coordinates so a restore knows how far past the basis to run. An empty
/// command list means the boundary IS the basis tick.
/// </summary>
/// <param name="BoundaryWave">Wave that ended at the boundary this tail replays to.</param>
/// <param name="BoundaryTick">Tick of that boundary.</param>
/// <param name="Commands">Commands with basis tick &lt; IssuedAtTick ≤ boundary tick.</param>
public sealed record WaveTailRecord(
    int BoundaryWave,
    long BoundaryTick,
    IReadOnlyList<SimCommandLog> Commands);

/// <summary>
/// Restored view of one saved boundary (C-04, T-057): a self-sufficient
/// replay specification — basis commands plus tail commands concatenated
/// (the queue re-orders by (tick, seq) anyway), the boundary coordinates,
/// and the seed. <see cref="Replay"/> turns it back into a live simulation.
/// </summary>
/// <param name="Wave">Wave the snapshot restores to (the wave that ended).</param>
/// <param name="Tick">Tick the snapshot restores to.</param>
/// <param name="Seed">Level seed of the original run.</param>
/// <param name="Commands">Full command stream to replay, ≤ <paramref name="Tick"/>.</param>
public sealed record RestoredWaveSnapshot(
    int Wave,
    long Tick,
    long Seed,
    IReadOnlyList<SimCommandLog> Commands)
{
    /// <summary>
    /// Rebuilds the simulation at this boundary: fresh seeded instance
    /// (IRng replay contract), full command stream, run to the boundary
    /// tick. This is the ONLY restore path — the deterministic replay is
    /// the checkpoint format (T-040 invariant consumed here).
    /// </summary>
    /// <exception cref="ArgumentException">A logged command kind is unknown.</exception>
    public Simulation Replay(SimulationConfig config, IRng rng)
    {
        var simulation = new Simulation(config, rng, Seed);
        simulation.EnqueueRange(SimulationReplay.ToCommands(Commands));
        simulation.Run(Tick);
        return simulation;
    }
}

/// <summary>
/// UTF-8 JSON codec of the checkpoint payloads (source-generated metadata
/// only, D-05; snake_case to match the content file convention).
/// </summary>
public static class CheckpointPayload
{
    /// <summary>Serializes a basis snapshot record.</summary>
    public static byte[] Encode(WaveSnapshotRecord record) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, CheckpointJsonContext.Default.WaveSnapshotRecord));

    /// <summary>Serializes a command tail record.</summary>
    public static byte[] Encode(WaveTailRecord record) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, CheckpointJsonContext.Default.WaveTailRecord));

    /// <summary>Decodes a basis snapshot record.</summary>
    /// <exception cref="JsonException">Malformed payload JSON.</exception>
    public static WaveSnapshotRecord DecodeSnapshot(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Deserialize(payload, CheckpointJsonContext.Default.WaveSnapshotRecord)
            ?? throw new JsonException("Wave snapshot payload deserialized to null.");
    }

    /// <summary>Decodes a command tail record.</summary>
    /// <exception cref="JsonException">Malformed payload JSON.</exception>
    public static WaveTailRecord DecodeTail(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Deserialize(payload, CheckpointJsonContext.Default.WaveTailRecord)
            ?? throw new JsonException("Wave tail payload deserialized to null.");
    }
}

/// <summary>
/// Source-generated JSON metadata for the checkpoint payloads (AOT red line
/// D-05: EraCore serialization goes through generated metadata only).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WaveSnapshotRecord))]
[JsonSerializable(typeof(WaveTailRecord))]
[JsonSerializable(typeof(SimCommandLog))]
public sealed partial class CheckpointJsonContext : JsonSerializerContext;
