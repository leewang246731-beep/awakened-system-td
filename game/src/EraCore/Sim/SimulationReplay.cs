using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Sim;

/// <summary>One (tick, state hash) sample of a recorded run.</summary>
/// <param name="Tick">Simulation tick the hash was taken at (end of tick).</param>
/// <param name="StateHash">Whole-state FNV hash at that tick.</param>
public sealed record SimHashSample(long Tick, long StateHash);

/// <summary>
/// Journal-friendly form of one <see cref="SimCommand"/> (K-8 replay
/// stream): the kind is serialized by name so the golden file stays
/// readable; the payload rides verbatim.
/// </summary>
/// <param name="IssuedAtTick">Tick the command applies at.</param>
/// <param name="Kind">Closed verb kind name (<see cref="SimCommandKind"/>).</param>
/// <param name="PayloadJson">Validated payload JSON, verbatim.</param>
public sealed record SimCommandLog(long IssuedAtTick, string Kind, string PayloadJson);

/// <summary>
/// Everything a replay needs (C-12, T-040; 03-decomposition §1.2): the
/// seed, the command stream and the hash timeline of the original run. The
/// level CONFIG is deliberately not part of the record — it is content
/// data (era1 JSON) rebuilt by the caller, so the golden stays
/// data-driven (CS-4.4) and config drift is caught as hash drift, not
/// hidden inside a serialized blob.
/// </summary>
/// <param name="Seed">Level seed (replay key #1).</param>
/// <param name="Ticks">Ticks the original run advanced.</param>
/// <param name="SampleEvery">Sampling stride of <see cref="Samples"/> (1 = every tick).</param>
/// <param name="Commands">Command stream in enqueue order (replay key #2).</param>
/// <param name="Samples">Hash timeline of the original run.</param>
public sealed record SimulationRunRecord(
    long Seed,
    long Ticks,
    int SampleEvery,
    IReadOnlyList<SimCommandLog> Commands,
    IReadOnlyList<SimHashSample> Samples);

/// <summary>
/// Result of verifying a replay against its record.
/// </summary>
/// <param name="Verified">True when every sample matched.</param>
/// <param name="FirstDivergenceTick">Tick of the first mismatching sample; null when verified.</param>
/// <param name="ComparedSamples">Number of samples compared.</param>
public sealed record SimReplayVerification(bool Verified, long? FirstDivergenceTick, int ComparedSamples);

/// <summary>
/// Deterministic replay harness (C-12, T-040): captures a run as
/// (seed, command stream, hash timeline), replays it on a fresh
/// <see cref="Simulation"/> and verifies the per-tick state hashes — the
/// executable form of the invariant "same seed + same command stream ⇒
/// same state hash at every tick". Also carries the JSON round-trip used
/// by the golden regression (source-generated metadata only, D-05).
/// </summary>
public sealed class SimulationReplay
{
    /// <summary>
    /// Runs a fresh simulation and records its hash timeline.
    /// Sampling: the initial state (tick 0) is always sample[0]; then one
    /// sample every <paramref name="sampleEvery"/> completed ticks.
    /// </summary>
    public static SimulationRunRecord Capture(
        SimulationConfig config,
        IRng rng,
        long seed,
        IReadOnlyList<SimCommand> commands,
        long ticks,
        int sampleEvery = 1)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(commands);
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Tick count must be non-negative.");
        }

        if (sampleEvery < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleEvery), sampleEvery, "Sample stride must be at least 1.");
        }

        var simulation = new Simulation(config, rng, seed);
        simulation.EnqueueRange(commands);

        var samples = new List<SimHashSample> { new(0, simulation.ComputeStateHash()) };
        for (long tick = 1; tick <= ticks; tick++)
        {
            simulation.Step();
            if (tick % sampleEvery == 0)
            {
                samples.Add(new SimHashSample(tick, simulation.ComputeStateHash()));
            }
        }

        var logs = new List<SimCommandLog>(commands.Count);
        foreach (SimCommand command in commands)
        {
            logs.Add(new SimCommandLog(command.IssuedAtTick, command.Kind.ToString(), command.PayloadJson));
        }

        return new SimulationRunRecord(seed, ticks, sampleEvery, logs, samples);
    }

    /// <summary>
    /// Replays a record on a fresh simulation and returns the recomputed
    /// hash timeline (same sampling stride as the record).
    /// </summary>
    public static IReadOnlyList<SimHashSample> ReplayHashes(SimulationRunRecord record, SimulationConfig config, IRng rng)
    {
        ArgumentNullException.ThrowIfNull(record);
        SimulationRunRecord replayed = Capture(
            config, rng, record.Seed, ToCommands(record.Commands), record.Ticks, record.SampleEvery);
        return replayed.Samples;
    }

    /// <summary>
    /// Replays the record and compares every sample against the original
    /// timeline. Sample counts must match — a shorter replay is a
    /// divergence at the first missing tick.
    /// </summary>
    public static SimReplayVerification Verify(SimulationRunRecord record, SimulationConfig config, IRng rng)
    {
        IReadOnlyList<SimHashSample> replayed = ReplayHashes(record, config, rng);
        int compared = Math.Min(record.Samples.Count, replayed.Count);
        for (int i = 0; i < compared; i++)
        {
            if (record.Samples[i].Tick != replayed[i].Tick || record.Samples[i].StateHash != replayed[i].StateHash)
            {
                return new SimReplayVerification(false, record.Samples[i].Tick, compared);
            }
        }

        if (replayed.Count != record.Samples.Count)
        {
            long missing = record.Samples.Count > compared
                ? record.Samples[compared].Tick
                : replayed[compared].Tick;
            return new SimReplayVerification(false, missing, compared);
        }

        return new SimReplayVerification(true, null, compared);
    }

    /// <summary>Converts journal command logs back into SimCommand records.</summary>
    public static IReadOnlyList<SimCommand> ToCommands(IReadOnlyList<SimCommandLog> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var commands = new List<SimCommand>(logs.Count);
        foreach (SimCommandLog log in logs)
        {
            if (!Enum.TryParse<SimCommandKind>(log.Kind, out SimCommandKind kind))
            {
                throw new ArgumentException($"Unknown SimCommandKind '{log.Kind}'.", nameof(logs));
            }

            commands.Add(new SimCommand(log.IssuedAtTick, kind, log.PayloadJson));
        }

        return commands;
    }

    /// <summary>Serializes a record via the source-generated context (D-05).</summary>
    public static string Serialize(SimulationRunRecord record) =>
        JsonSerializer.Serialize(record, SimReplayJsonContext.Default.SimulationRunRecord);

    /// <summary>Deserializes a record via the source-generated context (D-05).</summary>
    /// <exception cref="JsonException">Malformed record JSON.</exception>
    public static SimulationRunRecord Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Record JSON must not be null or blank.", nameof(json));
        }

        return JsonSerializer.Deserialize(json, SimReplayJsonContext.Default.SimulationRunRecord)
            ?? throw new JsonException("Replay record deserialized to null.");
    }
}

/// <summary>
/// Golden file envelope: the replay record plus provenance header fields
/// (schema/task/regeneration commands), mirroring wave-e2-golden.json's
/// layout. Content discipline: rows are engine-produced only (CS-6.1).
/// </summary>
/// <param name="Schema">Golden schema urn (urn:eratd:golden:sim:1).</param>
/// <param name="Task">Producing task id.</param>
/// <param name="Seed">Level seed of the recorded run.</param>
/// <param name="Ticks">Ticks of the recorded run (E1 milestone horizon: 1800 = 60 s).</param>
/// <param name="SampleEvery">Sampling stride in ticks (300 for the golden).</param>
/// <param name="RegenCommand">Documented regeneration entry point (CS-6.1).</param>
/// <param name="CheckCommand">Documented check entry point.</param>
/// <param name="Notes">Provenance annotations.</param>
/// <param name="Commands">Command stream, enqueue order.</param>
/// <param name="Samples">Engine-produced hash timeline — never hand-edited.</param>
public sealed record SimGoldenFile(
    string Schema,
    string Task,
    long Seed,
    long Ticks,
    int SampleEvery,
    string? RegenCommand,
    string? CheckCommand,
    string? Notes,
    IReadOnlyList<SimCommandLog> Commands,
    IReadOnlyList<SimHashSample> Samples);

/// <summary>
/// Source-generated JSON metadata for replay records and golden files
/// (AOT red line D-05: EraCore serialization goes through generated
/// metadata only; snake_case to match the content file convention).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SimulationRunRecord))]
[JsonSerializable(typeof(SimGoldenFile))]
[JsonSerializable(typeof(SimCommandLog))]
[JsonSerializable(typeof(SimHashSample))]
public sealed partial class SimReplayJsonContext : JsonSerializerContext;
