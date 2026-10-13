using EraCore.Balance;
using EraCore.Contracts.Persistence;
using EraCore.Contracts.Sim;
using EraCore.Persistence;
using EraCore.Sim;

namespace EraCore.Checkpoint;

/// <summary>
/// Wave-boundary checkpoint store (C-04, T-057; F-001/F-004): the layer
/// between a running <see cref="Simulation"/> and the K-7 level save domain
/// of an injected <see cref="ISavePort"/>. It works entirely through the
/// T-041 extension points — free-form level slot ids and opaque blob
/// payloads — so no persistence backend change is required:
/// <list type="bullet">
/// <item><c>&lt;level&gt;#ckpt</c> — the CHECKPOINT wave snapshot
/// (<c>⌊胜波×0.75⌋ 且 ≥10</c>, from <see cref="Campaign.Checkpoint"/> of
/// <see cref="Campaign.WinWaves"/>' output). Permanent: rolling writes never
/// touch it.</item>
/// <item><c>&lt;level&gt;#roll-a</c> / <c>&lt;level&gt;#roll-b</c> — normal
/// wave-boundary snapshots, alternating so the newest write always lands in
/// the slot that does NOT hold the previous generation: at most the last TWO
/// generations survive (rolling retention) and a torn write can never
/// destroy both.</item>
/// </list>
/// <para>
/// Every save serializes the replay key (seed + command stream to the
/// boundary) via <see cref="WaveSnapshotRecord"/>; a restore REPLAYS it on a
/// fresh simulation instead of deserializing live state — the T-040
/// determinism invariant is the checkpoint format. Clearing (终局关失败)
/// writes a wave-0 tombstone row into every slot: wave numbers are 1-based,
/// so <c>0</c> is unrepresentable as a real boundary and reads report "no
/// snapshot" without needing a row-delete API on the frozen K-7 port.
/// </para>
/// </summary>
public sealed class WaveCheckpointStore
{
    /// <summary>Slot suffix of the permanent checkpoint-wave snapshot.</summary>
    public const string CheckpointSlotSuffix = "#ckpt";

    /// <summary>Slot suffix of rolling generation A.</summary>
    public const string RollingSlotSuffixA = "#roll-a";

    /// <summary>Slot suffix of rolling generation B.</summary>
    public const string RollingSlotSuffixB = "#roll-b";

    /// <summary>
    /// Tombstone wave marker (终局关 clear): wave numbers are 1-based, so 0
    /// can only ever mean "this slot was cleared".
    /// </summary>
    public const int ClearedWaveMarker = 0;

    private readonly ISavePort _port;
    private readonly string _levelSlotId;
    private readonly string[] _rollingSlots;
    private int _rollingCursor;
    private int? _lastSeenWave;
    private WaveSnapshotRecord? _checkpointBasis;

    /// <param name="port">K-7 save port owning the level domain (T-041).</param>
    /// <param name="levelSlotId">Level slot id the derived slots hang off.</param>
    /// <param name="threat">Clamped threat t of the level; drives 胜波/检查点.</param>
    /// <exception cref="ArgumentNullException">port is null.</exception>
    /// <exception cref="ArgumentException">
    /// levelSlotId blank, or threat negative (via <see cref="Campaign.WinWaves"/>).
    /// </exception>
    public WaveCheckpointStore(ISavePort port, string levelSlotId, double threat)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (string.IsNullOrWhiteSpace(levelSlotId))
        {
            throw new ArgumentException("A level slot id is required.", nameof(levelSlotId));
        }

        _port = port;
        _levelSlotId = levelSlotId;
        CheckpointWave = Campaign.Checkpoint(Campaign.WinWaves(threat));
        _rollingSlots = new[] { levelSlotId + RollingSlotSuffixA, levelSlotId + RollingSlotSuffixB };
    }

    /// <summary>
    /// The checkpoint wave of this level: max(10, ⌊胜波×0.75⌋) with 胜波 =
    /// <see cref="Campaign.WinWaves"/>(t) — the restore point after a normal
    /// level failure (v3.2 §3 / appendix F.5).
    /// </summary>
    public int CheckpointWave { get; }

    /// <summary>The injected save port (campaign-domain reads for tier checks).</summary>
    public ISavePort Port => _port;

    /// <summary>Level slot id this store was constructed for.</summary>
    public string LevelSlotId => _levelSlotId;

    /// <summary>Physical slot id of the permanent checkpoint snapshot.</summary>
    public string CheckpointSlotId => _levelSlotId + CheckpointSlotSuffix;

    /// <summary>Physical slot id of rolling generation A.</summary>
    public string RollingSlotAId => _rollingSlots[0];

    /// <summary>Physical slot id of rolling generation B.</summary>
    public string RollingSlotBId => _rollingSlots[1];

    /// <summary>
    /// Saves one wave boundary: the caller states which wave just ENDED with
    /// the live simulation standing at that boundary, plus every command
    /// issued by the boundary tick. Boundary semantics (wave that ended,
    /// snapshot at its end tick) come from the caller because the spawner
    /// publishes the NEXT wave's installation, not the boundary itself; the
    /// <see cref="ObserveBoundary"/> convenience derives it from
    /// <see cref="Simulation.CurrentWave"/> transitions.
    /// </summary>
    /// <param name="simulation">Live run standing at the boundary.</param>
    /// <param name="endedWave">Wave that just ended (1-based).</param>
    /// <param name="issuedCommands">Commands issued so far (IssuedAtTick ≤ boundary tick are recorded).</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">endedWave &lt; 1.</exception>
    public void SaveBoundary(Simulation simulation, int endedWave, IReadOnlyList<SimCommand> issuedCommands)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(issuedCommands);
        if (endedWave < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endedWave), endedWave, "Wave numbers are 1-based.");
        }

        long tick = simulation.CurrentTick;
        var boundary = new WaveSnapshotRecord(simulation.Seed, endedWave, tick, CommandsUpTo(issuedCommands, tick));

        if (endedWave == CheckpointWave)
        {
            // The checkpoint wave: basis with an empty tail, permanent slot.
            _port.WriteLevel(
                CheckpointSlotId,
                new LevelSaveBlobDto(endedWave, CheckpointPayload.Encode(boundary), CheckpointPayload.Encode(
                    new WaveTailRecord(endedWave, tick, Array.Empty<SimCommandLog>()))),
                SaveVersion.Current);
            _checkpointBasis = boundary;
            return;
        }

        WaveSnapshotRecord basis = ResolveRollingBasis(endedWave, boundary);
        var tail = new WaveTailRecord(
            endedWave, tick, CommandsBetween(issuedCommands, basis.Tick, tick));

        // Alternate slots: the previous complete generation always survives
        // in the other slot while this write is in flight (rolling retention
        // of the last two generations, torn-write tolerance).
        string slot = _rollingSlots[_rollingCursor];
        _rollingCursor = (_rollingCursor + 1) % _rollingSlots.Length;
        _port.WriteLevel(
            slot,
            new LevelSaveBlobDto(endedWave, CheckpointPayload.Encode(basis), CheckpointPayload.Encode(tail)),
            SaveVersion.Current);
    }

    /// <summary>
    /// Boundary convenience for a stepping loop: call once per tick with the
    /// live simulation; when <see cref="Simulation.CurrentWave"/> increases,
    /// the wave it transitioned FROM has just ended and is saved. The FIRST
    /// observed wave only sets the baseline (wave 1 installing is wave 1
    /// BEGINNING, not a completed boundary); a multi-wave jump in one call
    /// saves the last crossed boundary (earlier ticks are already gone —
    /// documented: steps between calls must be < one wave interval).
    /// </summary>
    /// <returns>The wave whose boundary was saved, or null this call.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The detected ended wave is &lt; 1 (only possible for a wave 0 → N
    /// jump, i.e. baseline skipped — treat the first wave as baseline).
    /// </exception>
    public int? ObserveBoundary(Simulation simulation, IReadOnlyList<SimCommand> issuedCommands)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(issuedCommands);

        int wave = simulation.CurrentWave;
        if (_lastSeenWave is null || wave <= _lastSeenWave.Value)
        {
            _lastSeenWave = wave;
            return null;
        }

        int endedWave = _lastSeenWave.Value;
        _lastSeenWave = wave;
        if (endedWave < 1)
        {
            // wave 0 → 1 is wave 1 beginning; anything wider means the
            // baseline was skipped between calls.
            return null;
        }

        SaveBoundary(simulation, endedWave, issuedCommands);
        return endedWave;
    }

    /// <summary>
    /// Restores the checkpoint-wave snapshot (normal-level failure path).
    /// False when the slot is absent, cleared (tombstone), or the level has
    /// not crossed its checkpoint wave yet.
    /// </summary>
    public bool TryRestoreCheckpoint(out RestoredWaveSnapshot snapshot) =>
        TryRestoreSlot(CheckpointSlotId, out snapshot);

    /// <summary>
    /// Restores the newest readable rolling boundary (crash-recovery path —
    /// the freshest complete generation across both rolling slots). False
    /// when both are absent or cleared.
    /// </summary>
    public bool TryRestoreLatestBoundary(out RestoredWaveSnapshot snapshot)
    {
        RestoredWaveSnapshot? best = null;
        foreach (string slot in _rollingSlots)
        {
            if (TryRestoreSlot(slot, out RestoredWaveSnapshot candidate)
                && (best is null || candidate.Wave > best.Wave))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            snapshot = null!;
            return false;
        }

        snapshot = best;
        return true;
    }

    /// <summary>
    /// Clears every slot of this level (终局关失败：只清本关存档+检查点) by
    /// writing wave-0 tombstones. The campaign domain of the same port is
    /// never touched. Subsequent boundary saves re-populate the slots.
    /// </summary>
    public void Clear()
    {
        var tombstone = new LevelSaveBlobDto(
            ClearedWaveMarker, Array.Empty<byte>(), Array.Empty<byte>());
        _port.WriteLevel(CheckpointSlotId, tombstone, SaveVersion.Current);
        foreach (string slot in _rollingSlots)
        {
            _port.WriteLevel(slot, tombstone, SaveVersion.Current);
        }

        _checkpointBasis = null;
    }

    // ------------------------------------------------------------ internals

    /// <summary>
    /// Basis of a rolling save: once the checkpoint exists and the boundary
    /// is past it, rolling blobs carry the CHECKPOINT record as basis and
    /// their delta as the tail (K-7 semantics); before that they are
    /// self-sufficient. The basis is cached from the write, falling back to
    /// a slot read (session resumed past a restored checkpoint).
    /// </summary>
    private WaveSnapshotRecord ResolveRollingBasis(int endedWave, WaveSnapshotRecord ownBoundary)
    {
        if (endedWave < CheckpointWave)
        {
            return ownBoundary;
        }

        if (_checkpointBasis is null
            && TryReadSlot(CheckpointSlotId, out WaveSnapshotRecord persisted, out _))
        {
            _checkpointBasis = persisted;
        }

        return _checkpointBasis ?? ownBoundary;
    }

    private bool TryRestoreSlot(string slotId, out RestoredWaveSnapshot snapshot)
    {
        if (!TryReadSlot(slotId, out WaveSnapshotRecord basis, out WaveTailRecord? tail))
        {
            snapshot = null!;
            return false;
        }

        if (tail is null)
        {
            snapshot = new RestoredWaveSnapshot(basis.Wave, basis.Tick, basis.Seed, basis.Commands);
            return true;
        }

        var commands = new List<SimCommandLog>(basis.Commands.Count + tail.Commands.Count);
        commands.AddRange(basis.Commands);
        commands.AddRange(tail.Commands);
        snapshot = new RestoredWaveSnapshot(tail.BoundaryWave, tail.BoundaryTick, basis.Seed, commands);
        return true;
    }

    /// <summary>
    /// Reads one slot raw. A missing database or slot row (fresh store) is
    /// "nothing saved", not damage; a damaged payload still surfaces as its
    /// decode exception — silence would hide corruption the T-041 crash
    /// contract promises to expose.
    /// </summary>
    private bool TryReadSlot(string slotId, out WaveSnapshotRecord basis, out WaveTailRecord? tail)
    {
        ILevelSaveBlob blob;
        try
        {
            blob = _port.ReadLevel(slotId, SaveVersion.Current);
        }
        catch (InvalidOperationException)
        {
            // No database file or no row for this slot: nothing saved yet.
            basis = null!;
            tail = null;
            return false;
        }

        if (blob.WaveCheckpoint <= ClearedWaveMarker || blob.SimSnapshotPayload.Length == 0)
        {
            basis = null!;
            tail = null;
            return false;
        }

        basis = CheckpointPayload.DecodeSnapshot(blob.SimSnapshotPayload);
        tail = blob.CommandTailPayload.Length == 0
            ? null
            : CheckpointPayload.DecodeTail(blob.CommandTailPayload);
        return true;
    }

    private static IReadOnlyList<SimCommandLog> CommandsUpTo(IReadOnlyList<SimCommand> commands, long tick)
    {
        var recorded = new List<SimCommandLog>();
        foreach (SimCommand command in commands)
        {
            if (command.IssuedAtTick <= tick)
            {
                recorded.Add(new SimCommandLog(command.IssuedAtTick, command.Kind.ToString(), command.PayloadJson));
            }
        }

        return recorded;
    }

    private static IReadOnlyList<SimCommandLog> CommandsBetween(
        IReadOnlyList<SimCommand> commands, long afterTick, long upToTick)
    {
        var recorded = new List<SimCommandLog>();
        foreach (SimCommand command in commands)
        {
            if (command.IssuedAtTick > afterTick && command.IssuedAtTick <= upToTick)
            {
                recorded.Add(new SimCommandLog(command.IssuedAtTick, command.Kind.ToString(), command.PayloadJson));
            }
        }

        return recorded;
    }
}
