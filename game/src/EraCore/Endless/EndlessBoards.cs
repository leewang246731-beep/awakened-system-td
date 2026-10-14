using System.Text;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;

namespace EraCore.Endless;

/// <summary>
/// The three endless boards (T-080, F-006; SP-7c 三榜分立): 手动 / 系统辅助 /
/// 全托管 — every settled run records on exactly ONE board, chosen by the
/// system-action share of the run (routing lives in <see cref="EndlessRun"/>;
/// the boards themselves never re-judge).
/// </summary>
public enum EndlessBoardKind
{
    /// <summary>手动榜 — share 0% (SP-7c).</summary>
    Manual = 0,

    /// <summary>系统辅助榜 — share 1%~89% (SP-7c).</summary>
    Assisted = 1,

    /// <summary>全托管榜 — share ≥90% (SP-7c); channel-isolated via T-081.</summary>
    Autopilot = 2,
}

/// <summary>
/// Channel ids of the three boards (T-080 [NEW-原型校准]; the autopilot
/// channel id is the T-081 reserved constant, not re-declared here).
/// </summary>
public static class EndlessBoardChannels
{
    /// <summary>手动榜通道 id（与 T-081 <c>standard</c> 通道分记——本榜只收无尽手动局）.</summary>
    public const string Manual = "endless.manual";

    /// <summary>系统辅助榜通道 id.</summary>
    public const string Assisted = "endless.assisted";

    /// <summary>
    /// Channel id of the given board; the autopilot board reuses the
    /// T-081 reserved hook <see cref="Agent.Autopilot.AutopilotPenalties.EndlessAutopilotChannelId"/>.
    /// </summary>
    public static string Of(EndlessBoardKind board) => board switch
    {
        EndlessBoardKind.Manual => Manual,
        EndlessBoardKind.Assisted => Assisted,
        EndlessBoardKind.Autopilot => Agent.Autopilot.AutopilotPenalties.EndlessAutopilotChannelId,
        _ => throw new ArgumentOutOfRangeException(nameof(board), board, "Unknown endless board."),
    };
}

/// <summary>
/// One settled endless run's leaderboard row (T-080): the SP-7c score plus
/// its components, stamped with the board it belongs to and the global
/// arrival sequence (同分排序「先到达者列前」— SP-7c: the timestamp is taken
/// at the wave boundary; the persisted counter keeps the order stable
/// across save/load).
/// </summary>
/// <param name="Board">Board the run was routed to.</param>
/// <param name="Arrival">Global arrival sequence number (smaller = earlier).</param>
/// <param name="WavesSurvived">Completed waves (死亡终局时的存活波数).</param>
/// <param name="EliteKills">Elite kills over the whole run.</param>
/// <param name="BossKills">Boss kills over the whole run.</param>
/// <param name="AffixBonusPoints">词缀加权分 (T-080 [NEW-原型校准]).</param>
/// <param name="Score">Total three-board score (SP-7c + 词缀加权).</param>
public sealed record EndlessScoreRecord(
    EndlessBoardKind Board,
    long Arrival,
    long WavesSurvived,
    long EliteKills,
    long BossKills,
    long AffixBonusPoints,
    long Score);

/// <summary>
/// The three endless leaderboards (T-080 三榜雏形): one append list per
/// board, global arrival counter, SP-7c ordering (score DESC, then arrival
/// ASC — 同分先到达者列前). Encodes to/from one deterministic payload that
/// travels through the K-7 campaign document as a reserved layout snapshot
/// (slot <see cref="BoardsSlotId"/> — the same carrier pattern as
/// <c>LegacyLedger.LedgerSlotId</c>; payload format owned by T-080/C 泳道).
/// </summary>
public sealed class EndlessBoards
{
    private static readonly byte[] Magic = { (byte)'E', (byte)'1', (byte)'E', (byte)'B' };

    private const byte FormatVersion = 1;

    /// <summary>
    /// Reserved layout-snapshot slot id of the boards payload inside the
    /// campaign document (merge discipline: a commit drops exactly this
    /// slot from the previous layouts and appends the fresh one — every
    /// other slot, e.g. <c>legacy:campaign-ledger</c>, passes through
    /// untouched).
    /// </summary>
    public const string BoardsSlotId = "endless:boards";

    private readonly List<EndlessScoreRecord>[] _boards =
    {
        new(), new(), new(),
    };

    private long _arrival;

    /// <summary>Builds empty boards (fresh profile).</summary>
    public EndlessBoards()
    {
    }

    /// <summary>
    /// Appends one settled run and stamps its arrival: the record's
    /// <see cref="EndlessScoreRecord.Arrival"/> is assigned here from the
    /// global counter (a caller-supplied stamp is refused — 先到达者列前 is
    /// ledger-owned).
    /// </summary>
    /// <exception cref="ArgumentNullException">record is null.</exception>
    public EndlessScoreRecord Add(EndlessScoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Board is not (EndlessBoardKind.Manual or EndlessBoardKind.Assisted or EndlessBoardKind.Autopilot))
        {
            throw new ArgumentOutOfRangeException(nameof(record), record.Board, "Unknown endless board.");
        }

        var stamped = record with { Arrival = ++_arrival };
        _boards[(int)record.Board].Add(stamped);
        return stamped;
    }

    /// <summary>Records of one board in LEDGER order (arrival ASC), not ranked.</summary>
    public IReadOnlyList<EndlessScoreRecord> Of(EndlessBoardKind board) => _boards[(int)board];

    /// <summary>
    /// One board's ranking (SP-7c 同分排序): score DESC, then arrival ASC.
    /// Stable, allocation per call — the ranking is a view, the ledger
    /// order stays append-only.
    /// </summary>
    public IReadOnlyList<EndlessScoreRecord> Ranking(EndlessBoardKind board) =>
        _boards[(int)board]
            .OrderByDescending(record => record.Score)
            .ThenBy(record => record.Arrival)
            .ToList();

    /// <summary>Global arrival counter (persists across save/load).</summary>
    public long Arrival => _arrival;

    /// <summary>Next arrival stamp the ledger will assign (test observability).</summary>
    public long NextArrival => _arrival + 1;

    /// <summary>FNV-1a hash over the encoded payload (determinism anchor).</summary>
    public long ComputeStateHash()
    {
        byte[] payload = Encode();
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (byte b in payload)
            {
                hash ^= b;
                hash *= prime;
            }

            return unchecked((long)hash);
        }
    }

    /// <summary>
    /// Encodes the full board state to the deterministic payload bytes (the
    /// K-7 layout-payload carrier, slot <see cref="BoardsSlotId"/>). Same
    /// state ⇒ same bytes — the save/load round-trip and the determinism
    /// tests rely on it.
    /// </summary>
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(_arrival);
        foreach (List<EndlessScoreRecord> board in _boards)
        {
            writer.Write(board.Count);
            foreach (EndlessScoreRecord record in board)
            {
                writer.Write((int)record.Board);
                writer.Write(record.Arrival);
                writer.Write(record.WavesSurvived);
                writer.Write(record.EliteKills);
                writer.Write(record.BossKills);
                writer.Write(record.AffixBonusPoints);
                writer.Write(record.Score);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Rebuilds the boards from a payload (the load path). The payload is
    /// staged into a fresh instance first — a damaged or inconsistent
    /// payload leaves this instance untouched (LegacyLedger discipline).
    /// </summary>
    /// <exception cref="ArgumentNullException">payload is null.</exception>
    /// <exception cref="SaveCorruptException">Payload is damaged or internally inconsistent.</exception>
    public void DecodeInto(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
            {
                throw new SaveCorruptException("Bad magic: not an EraCore endless board payload.", storedVersion: 0);
            }

            byte version = reader.ReadByte();
            if (version != FormatVersion)
            {
                throw new SaveCorruptException($"Endless board format version {version} is not {FormatVersion}.", version);
            }

            var staged = new EndlessBoards();
            staged._arrival = reader.ReadInt64();
            if (staged._arrival < 0)
            {
                throw new SaveCorruptException($"Arrival counter {staged._arrival} is negative.", version);
            }

            var stagedBoards = new List<EndlessScoreRecord>[staged._boards.Length];
            for (int boardIndex = 0; boardIndex < stagedBoards.Length; boardIndex++)
            {
                int count = ReadCount(reader, version);
                var rows = new List<EndlessScoreRecord>(count);
                for (int i = 0; i < count; i++)
                {
                    int boardKind = reader.ReadInt32();
                    if (boardIndex != boardKind || boardKind is < 0 or > 2)
                    {
                        throw new SaveCorruptException(
                            $"Record {i} claims board {boardKind} inside board list {boardIndex}.", version);
                    }

                    var record = new EndlessScoreRecord(
                        (EndlessBoardKind)boardKind,
                        reader.ReadInt64(),
                        reader.ReadInt64(),
                        reader.ReadInt64(),
                        reader.ReadInt64(),
                        reader.ReadInt64(),
                        reader.ReadInt64());
                    if (record.Arrival <= 0)
                    {
                        throw new SaveCorruptException($"Arrival {record.Arrival} is not a positive stamp.", version);
                    }

                    if (record.Score < 0 || record.WavesSurvived < 0 || record.EliteKills < 0
                        || record.BossKills < 0 || record.AffixBonusPoints < 0)
                    {
                        throw new SaveCorruptException("Record tallies are negative.", version);
                    }

                    rows.Add(record);
                }

                if (rows.Count > 1)
                {
                    for (int i = 1; i < rows.Count; i++)
                    {
                        if (rows[i].Arrival <= rows[i - 1].Arrival)
                        {
                            throw new SaveCorruptException(
                                $"Board {boardIndex} arrivals are not strictly increasing.", version);
                        }
                    }
                }

                stagedBoards[boardIndex] = rows;
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new SaveCorruptException("Trailing bytes after the endless board payload.", version);
            }

            foreach (List<EndlessScoreRecord> rows in stagedBoards)
            {
                if (rows.Count > 0 && rows.Max(record => record.Arrival) > staged._arrival)
                {
                    throw new SaveCorruptException("A record's arrival exceeds the global counter.", version);
                }
            }

            // ---- validated end-to-end: commit
            for (int i = 0; i < stagedBoards.Length; i++)
            {
                _boards[i] = stagedBoards[i];
            }

            _arrival = staged._arrival;
        }
        catch (Exception ex) when (ex is EndOfStreamException or ObjectDisposedException or DecoderFallbackException)
        {
            throw new SaveCorruptException($"Endless board payload truncated at {payload.Length} bytes.", storedVersion: 0, ex);
        }
        catch (SaveCorruptException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new SaveCorruptException($"Endless board payload is inconsistent: {ex.Message}", FormatVersion, ex);
        }
    }

    private static int ReadCount(BinaryReader reader, byte version)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new SaveCorruptException($"Count {count} exceeds the remaining payload.", version);
        }

        return count;
    }
}

/// <summary>
/// Campaign-domain persistence of the three boards (T-080 落账): the boards
/// payload rides the K-7 CAMPAIGN document as the reserved layout snapshot
/// <see cref="EndlessBoards.BoardsSlotId"/> — the merge keeps every other
/// field (pool, techs, relics, the legacy ledger slot…) untouched, so the
/// endless lane never races the legacy lane's campaign writes (each lane
/// owns exactly its own slot).
/// </summary>
public static class EndlessBoardStore
{
    /// <summary>
    /// Loads the boards from the campaign domain. A store with no campaign
    /// document yet yields EMPTY boards (a fresh profile's first endless
    /// run must not crash the settle path).
    /// </summary>
    /// <exception cref="ArgumentNullException">port is null.</exception>
    /// <exception cref="SaveCorruptException">The stored payload is damaged.</exception>
    public static EndlessBoards Load(ISavePort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        ICampaignMeta meta;
        try
        {
            meta = port.ReadCampaign(SaveVersion.Current);
        }
        catch (InvalidOperationException)
        {
            // No campaign document in this store yet — fresh profile.
            return new EndlessBoards();
        }

        return FromMeta(meta);
    }

    /// <summary>
    /// Rebuilds boards from an already-read campaign meta (no port
    /// contact); absent slot ⇒ empty boards.
    /// </summary>
    /// <exception cref="ArgumentNullException">meta is null.</exception>
    /// <exception cref="SaveCorruptException">The stored payload is damaged.</exception>
    public static EndlessBoards FromMeta(ICampaignMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ILevelLayoutSnapshot? slot = meta.LevelLayoutSnapshots
            .FirstOrDefault(layout => layout.LevelId == EndlessBoards.BoardsSlotId);
        if (slot is null)
        {
            return new EndlessBoards();
        }

        var boards = new EndlessBoards();
        boards.DecodeInto(slot.LayoutPayload);
        return boards;
    }

    /// <summary>
    /// Commits one settled record: append to the boards, then write the
    /// campaign document with the fresh payload merged under exactly the
    /// endless slot. <paramref name="previousMeta"/> supplies the document
    /// to preserve (WorldSeed, era, pool, techs, relics, other slots);
    /// null (or a store with no document yet) falls back to a minimal
    /// shell document owned by the endless lane.
    /// </summary>
    /// <returns>The stamped record actually stored (arrival assigned).</returns>
    /// <exception cref="ArgumentNullException">boards or record is null.</exception>
    public static EndlessScoreRecord Commit(
        EndlessBoards boards,
        EndlessScoreRecord record,
        ISavePort port,
        ICampaignMeta? previousMeta)
    {
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(port);

        EndlessScoreRecord stamped = boards.Add(record);
        port.WriteCampaign(MergeInto(boards, previousMeta), SaveVersion.Current);
        return stamped;
    }

    /// <summary>
    /// Pure merge (no port contact): the previous document with the endless
    /// slot replaced by the fresh payload. A null previous meta becomes the
    /// minimal shell document (seed 0, the run's starting era, empty
    /// families) — hosts normally pass the live campaign meta.
    /// </summary>
    public static ICampaignMeta MergeInto(EndlessBoards boards, ICampaignMeta? previousMeta)
    {
        ArgumentNullException.ThrowIfNull(boards);
        int era = previousMeta?.EraIndex ?? EndlessEnemyPool.MinEraIndex;
        var layouts = (previousMeta?.LevelLayoutSnapshots ?? Array.Empty<ILevelLayoutSnapshot>())
            .Where(layout => layout.LevelId != EndlessBoards.BoardsSlotId)
            .Select(layout => new LevelLayoutSnapshotDto(layout.LevelId, layout.LayoutPayload))
            .ToList();
        layouts.Add(new LevelLayoutSnapshotDto(EndlessBoards.BoardsSlotId, boards.Encode()));

        return new CampaignMetaDto(
            previousMeta?.WorldSeed ?? 0L,
            era,
            new Dictionary<string, long>(previousMeta?.ResourcePool ?? new Dictionary<string, long>(), StringComparer.Ordinal),
            new List<string>(previousMeta?.UnlockedTechs ?? new List<string>()),
            new List<string>(previousMeta?.RelicRegistry ?? new List<string>()),
            layouts);
    }
}
