using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Sim;

namespace EraCore.Checkpoint;

/// <summary>
/// Result of a recovery-equivalence verification (C-04, T-057 — the soul
/// assertion of the checkpoint system).
/// </summary>
/// <param name="Verified">True when every compared tick matched.</param>
/// <param name="FirstDivergenceTick">First mismatching tick; null when verified.</param>
/// <param name="ComparedTicks">Ticks compared (horizon + the tick-0 sample).</param>
/// <param name="CheckpointWave">Checkpoint wave of the level.</param>
/// <param name="CheckpointTick">Tick the checkpoint restore replayed from zero to.</param>
/// <param name="KillAtWave">Wave whose boundary the interruption was killed at.</param>
/// <param name="KillTick">Tick the interrupted world was abandoned at.</param>
/// <param name="HorizonTick">Last tick both worlds were compared at.</param>
public sealed record RecoveryEquivalenceResult(
    bool Verified,
    long? FirstDivergenceTick,
    long ComparedTicks,
    int CheckpointWave,
    long CheckpointTick,
    int KillAtWave,
    long KillTick,
    long HorizonTick);

/// <summary>
/// Recovery-equivalence harness (C-04, T-057): the direct consumer of the
/// T-040 determinism invariant. It plays the same level in three worlds —
/// (A) uninterrupted, (B) interrupted and killed at a chosen wave boundary
/// while a <see cref="WaveCheckpointStore"/> saves every boundary, (C)
/// restored from the checkpoint by deterministic replay and continued — and
/// proves the state hash of the restored world equals the uninterrupted
/// world at EVERY tick: "从检查点恢复重放 N 波 ⇒ 与从未中断连续跑逐 tick
/// StateHash 相等".
/// </summary>
public static class RecoveryEquivalence
{
    /// <summary>
    /// Runs the three worlds and compares per-tick state hashes.
    /// Commands after the checkpoint tick are re-issued into the restored
    /// world (the fixed command list stands in for the player repeating the
    /// same actions) — exactly the resume contract of a real session.
    /// </summary>
    /// <param name="config">Level config (rebuilt per world).</param>
    /// <param name="rngFactory">Fresh rng per world (reseeded with the seed).</param>
    /// <param name="seed">Level seed (replay key #1, all worlds).</param>
    /// <param name="commands">Full command stream (replay key #2).</param>
    /// <param name="threat">Threat of the level (checkpoint wave derivation).</param>
    /// <param name="killAtWave">
    /// The interruption dies right AFTER the boundary that ends this wave
    /// (must be &gt; the checkpoint wave so the restore replays ≥1 wave).
    /// </param>
    /// <param name="horizonTick">
    /// Last compared tick — must be reachable past the kill boundary so the
    /// restored world proves itself BEYOND the interruption point.
    /// </param>
    /// <param name="savePort">Port the interrupted world saves through.</param>
    /// <param name="levelSlotId">Level slot id of the checkpoint store.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// killAtWave ≤ checkpoint wave, or horizonTick non-positive.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The kill wave was not reached within the horizon, or no checkpoint
    /// was captured before the kill.
    /// </exception>
    public static RecoveryEquivalenceResult VerifyAcrossRestore(
        SimulationConfig config,
        Func<IRng> rngFactory,
        long seed,
        IReadOnlyList<SimCommand> commands,
        double threat,
        int killAtWave,
        long horizonTick,
        ISavePort savePort,
        string levelSlotId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rngFactory);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(savePort);
        if (string.IsNullOrWhiteSpace(levelSlotId))
        {
            throw new ArgumentException("A level slot id is required.", nameof(levelSlotId));
        }

        if (horizonTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonTick), horizonTick, "Horizon must be positive.");
        }

        int checkpointWave = Campaign.Checkpoint(Campaign.WinWaves(threat));
        if (killAtWave <= checkpointWave)
        {
            throw new ArgumentOutOfRangeException(
                nameof(killAtWave), killAtWave,
                $"Kill wave must be past the checkpoint wave ({checkpointWave}) so the restore replays at least one wave.");
        }

        // World A — uninterrupted reference timeline, hash at every tick
        // (the frame-end snapshot already carries the hash Step computed).
        var timeline = new List<long>(unchecked((int)horizonTick) + 1);
        var worldA = new Simulation(config, rngFactory(), seed);
        worldA.EnqueueRange(commands);
        timeline.Add(worldA.ComputeStateHash()); // tick 0 (no frame yet)
        for (long tick = 1; tick <= horizonTick; tick++)
        {
            worldA.Step();
            timeline.Add(worldA.Snapshots.Latest!.StateHash);
        }

        // World B — same run, boundaries saved through the store, killed at
        // the killAtWave boundary. Its own hashes must match world A's to
        // the kill tick (saves are read-only observations of the run).
        var store = new WaveCheckpointStore(savePort, levelSlotId, threat);
        var worldB = new Simulation(config, rngFactory(), seed);
        worldB.EnqueueRange(commands);
        long killTick = -1;
        while (worldB.CurrentTick < horizonTick)
        {
            worldB.Step();
            _ = store.ObserveBoundary(worldB, commands);
            if (worldB.Snapshots.Latest!.StateHash != timeline[(int)worldB.CurrentTick])
            {
                throw new InvalidOperationException(
                    $"The interrupted world itself diverged from the uninterrupted timeline at tick "
                    + $"{worldB.CurrentTick} — the boundary-save path must be read-only, this indicts the save path.");
            }

            if (worldB.CurrentWave > killAtWave)
            {
                killTick = worldB.CurrentTick; // boundary that ended killAtWave is saved — kill here
                break;
            }
        }

        if (killTick < 0)
        {
            throw new InvalidOperationException(
                $"Kill wave {killAtWave} was not reached within the horizon ({horizonTick} ticks; "
                + $"world B stopped at wave {worldB.CurrentWave}).");
        }

        if (!store.TryRestoreCheckpoint(out RestoredWaveSnapshot checkpoint))
        {
            throw new InvalidOperationException(
                $"No checkpoint captured before the kill at wave {killAtWave}.");
        }

        // World C — restored from the checkpoint by replay (seed + commands
        // to the checkpoint tick), then continued to the horizon with the
        // post-checkpoint commands re-issued. Compared against world A at
        // EVERY tick, replay prefix included.
        var worldC = new Simulation(config, rngFactory(), checkpoint.Seed);
        worldC.EnqueueRange(SimulationReplay.ToCommands(checkpoint.Commands));
        foreach (SimCommand command in commands)
        {
            if (command.IssuedAtTick > checkpoint.Tick)
            {
                worldC.Enqueue(command);
            }
        }

        long? firstDivergence = null;
        if (worldC.ComputeStateHash() != timeline[0])
        {
            firstDivergence = 0;
        }

        for (long tick = 1; tick <= horizonTick && firstDivergence is null; tick++)
        {
            worldC.Step();
            if (worldC.Snapshots.Latest!.StateHash != timeline[(int)tick])
            {
                firstDivergence = tick;
            }
        }

        return new RecoveryEquivalenceResult(
            firstDivergence is null,
            firstDivergence,
            horizonTick + 1,
            checkpointWave,
            checkpoint.Tick,
            killAtWave,
            killTick,
            horizonTick);
    }
}
