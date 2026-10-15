using System.Text.Json;
using EraCore.Contracts.Persistence;
using EraCore.IO;
using Microsoft.Data.Sqlite;

namespace EraCore.Persistence;

/// <summary>
/// Desktop save backend (K-7 / T-041 / 07 D-11): ONE SQLite database, TWO
/// domains — the campaign-global family (<c>campaign_meta</c>, single JSON
/// payload row) and the per-level family (<c>level_blob</c>, one row per
/// level slot) — plus the <c>schema_version</c> authority table.
///
/// Durability discipline (D-11): the database runs in WAL mode with
/// <c>synchronous=FULL</c>, so every COMMIT is the journal commit point and
/// fsyncs before the caller is told the save succeeded — a crash can never
/// expose a half-written transaction (verified by crash-injection tests).
/// Before every write the previous database file is rotated into
/// <c>&lt;db&gt;.bak</c> and <c>&lt;db&gt;.bak2</c> (at most two generations)
/// through <see cref="IFileStore"/>, whose injected root is the ONLY path
/// authority (D-03): no absolute path is ever hard-coded here — the physical
/// SQLite path is derived from <see cref="IFileStore.Root"/> at open time.
///
/// Reads whose stored version differs from the requested one go through the
/// <see cref="SaveMigrator"/> chain (v1 is the identity; future hops are
/// registered as they ship). Damaged payloads surface as
/// <see cref="SaveCorruptException"/> carrying the stored version.
/// </summary>
public sealed class SqliteSavePort : ISavePort
{
    /// <summary>Suffix of the newest backup generation (the previous database).</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>Suffix of the oldest backup generation (two generations maximum).</summary>
    public const string BackupSuffixGeneration2 = ".bak2";

    private readonly IFileStore _files;
    private readonly string _dbRelativePath;
    private readonly SaveMigrator _migrator;
    private readonly object _writeGate = new();

    /// <param name="fileStore">Root-injected file access (D-03); owns every path.</param>
    /// <param name="databaseRelativePath">Database path relative to the store root.</param>
    /// <param name="migrator">
    /// Migration chain; defaults to <see cref="SaveMigrator.CreateDefault"/>.
    /// Tests of the chain mechanics inject steps here.
    /// </param>
    public SqliteSavePort(
        IFileStore fileStore,
        string databaseRelativePath = "saves/profile.db",
        SaveMigrator? migrator = null)
    {
        _files = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        if (string.IsNullOrWhiteSpace(databaseRelativePath))
        {
            throw new ArgumentException("A database relative path is required.", nameof(databaseRelativePath));
        }

        _dbRelativePath = databaseRelativePath;
        _migrator = migrator ?? SaveMigrator.CreateDefault();
    }

    /// <summary>Relative path of the newest backup generation (previous database).</summary>
    public string BackupGeneration1RelativePath => _dbRelativePath + BackupSuffix;

    /// <summary>Relative path of the oldest backup generation (two maximum).</summary>
    public string BackupGeneration2RelativePath => _dbRelativePath + BackupSuffixGeneration2;

    /// <inheritdoc />
    public void WriteCampaign(ICampaignMeta meta, int formatVersion)
    {
        ArgumentNullException.ThrowIfNull(meta);
        EnsureWritableVersion(formatVersion);
        var payloadJson = CampaignSaveJsonContext.SerializeCampaign(CampaignMetaDto.FromMeta(meta));

        lock (_writeGate)
        {
            using var connection = BeginWrite();
            using var transaction = connection.BeginTransaction();
            EnsureSchema(connection);
            ExecuteNonQuery(
                connection,
                "INSERT INTO campaign_meta (id, payload_json) VALUES (1, $payload) "
                + "ON CONFLICT(id) DO UPDATE SET payload_json = excluded.payload_json;",
                ("$payload", payloadJson));
            StampVersion(connection, formatVersion);
            transaction.Commit(); // WAL commit point — fsynced by synchronous=FULL (D-11)
        }
    }

    /// <inheritdoc />
    public ICampaignMeta ReadCampaign(int formatVersion)
    {
        using var connection = OpenDatabaseForRead();
        var storedVersion = ReadStoredVersionOrThrow(connection);
        if (storedVersion != formatVersion)
        {
            _migrator.Migrate(connection, storedVersion, formatVersion);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM campaign_meta WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "No campaign state has been saved in this store yet.");
        }

        try
        {
            return CampaignSaveJsonContext.DeserializeCampaign(reader.GetString(0));
        }
        catch (JsonException ex)
        {
            throw new SaveCorruptException(
                $"The campaign payload JSON is damaged and cannot be decoded "
                + $"(stored format version {storedVersion}).",
                storedVersion,
                ex);
        }
    }

    /// <inheritdoc />
    public void WriteLevel(string levelSlotId, ILevelSaveBlob blob, int formatVersion)
    {
        if (string.IsNullOrWhiteSpace(levelSlotId))
        {
            throw new ArgumentException("A level slot id is required.", nameof(levelSlotId));
        }

        ArgumentNullException.ThrowIfNull(blob);
        EnsureWritableVersion(formatVersion);

        lock (_writeGate)
        {
            using var connection = BeginWrite();
            using var transaction = connection.BeginTransaction();
            EnsureSchema(connection);
            ExecuteNonQuery(
                connection,
                "INSERT INTO level_blob (level_slot_id, wave_checkpoint, sim_snapshot, command_tail) "
                + "VALUES ($id, $wave, $sim, $tail) ON CONFLICT(level_slot_id) DO UPDATE SET "
                + "wave_checkpoint = excluded.wave_checkpoint, sim_snapshot = excluded.sim_snapshot, "
                + "command_tail = excluded.command_tail;",
                ("$id", levelSlotId),
                ("$wave", blob.WaveCheckpoint),
                ("$sim", blob.SimSnapshotPayload),
                ("$tail", blob.CommandTailPayload));
            StampVersion(connection, formatVersion);
            transaction.Commit(); // WAL commit point — fsynced by synchronous=FULL (D-11)
        }
    }

    /// <inheritdoc />
    public ILevelSaveBlob ReadLevel(string levelSlotId, int formatVersion)
    {
        if (string.IsNullOrWhiteSpace(levelSlotId))
        {
            throw new ArgumentException("A level slot id is required.", nameof(levelSlotId));
        }

        using var connection = OpenDatabaseForRead();
        var storedVersion = ReadStoredVersionOrThrow(connection);
        if (storedVersion != formatVersion)
        {
            _migrator.Migrate(connection, storedVersion, formatVersion);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT wave_checkpoint, sim_snapshot, command_tail "
            + "FROM level_blob WHERE level_slot_id = $id;";
        command.Parameters.AddWithValue("$id", levelSlotId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"No save blob exists for level slot '{levelSlotId}'.");
        }

        return new LevelSaveBlobDto(
            reader.GetInt32(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2));
    }

    /// <summary>
    /// Durability pragmas applied to every connection this port opens (07
    /// D-11): WAL journal plus <c>synchronous=FULL</c>, which makes each
    /// COMMIT an fsynced journal commit point — the atomic-write half of the
    /// crash-safety contract (the other half being all mutations inside one
    /// transaction). Public so tests can verify the pragmas directly.
    /// </summary>
    public static void ApplyDurabilityPragmas(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous = FULL;");
    }

    // ------------------------------------------------------------ write path

    /// <summary>
    /// Rotation + connection + pragmas + write-version alignment shared by
    /// both write operations. MUST be called while holding <c>_writeGate</c>
    /// (the public writers take it with a plain <c>lock</c>, which releases
    /// even on throw). If version alignment throws (e.g. a store from a
    /// newer format), the freshly opened connection is disposed HERE: the
    /// caller's <c>using</c> only disposes what BeginWrite returns, so a
    /// throw on the return path would otherwise leak the native file handle
    /// (found by the T-100 dual-backend write-refusal equivalence test).
    /// </summary>
    private SqliteConnection BeginWrite()
    {
        RotateBackupsBeforeWrite();
        var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
        try
        {
            ApplyDurabilityPragmas(connection);
            var storedVersion = TryReadStoredVersion(connection);
            if (storedVersion is int stored && stored != SaveVersion.Current)
            {
                _migrator.Migrate(connection, stored, SaveVersion.Current);
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rolls the backup generations forward before a mutating write:
    /// .bak2 := .bak, .bak := current database (at most two generations).
    /// The copy is taken after a best-effort WAL checkpoint so the main file
    /// is self-contained. Rotation is advisory: with an outside connection
    /// holding the file, the rotation is skipped (existing generations
    /// survive) — store atomicity is guaranteed by the write transaction,
    /// never by the rotation itself.
    /// </summary>
    private void RotateBackupsBeforeWrite()
    {
        if (!_files.Exists(_dbRelativePath))
        {
            return; // first write into this store — nothing to back up yet
        }

        using (var connection = OpenConnection(SqliteOpenMode.ReadWrite))
        {
            ApplyDurabilityPragmas(connection);
            ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        // The copy is taken through the injected file store and is
        // best-effort: when an outside connection pins the database file
        // (Windows share semantics deny parallel readers of a file opened
        // for write), rotation is skipped and the existing generations
        // survive — store atomicity is guaranteed by the write transaction,
        // never by the rotation itself.
        byte[] currentDatabase;
        try
        {
            currentDatabase = _files.ReadAllBytes(_dbRelativePath);
        }
        catch (IOException)
        {
            return; // outside holder: keep current generations, write proceeds
        }

        if (_files.Exists(BackupGeneration1RelativePath))
        {
            _files.WriteAllBytes(
                BackupGeneration2RelativePath,
                _files.ReadAllBytes(BackupGeneration1RelativePath));
        }

        _files.WriteAllBytes(BackupGeneration1RelativePath, currentDatabase);
    }

    /// <summary>Creates the dual-domain schema and the version row if absent.</summary>
    private static void EnsureSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            "CREATE TABLE IF NOT EXISTS schema_version ("
            + "id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL);");
        ExecuteNonQuery(
            connection,
            "CREATE TABLE IF NOT EXISTS campaign_meta ("
            + "id INTEGER PRIMARY KEY CHECK (id = 1), payload_json TEXT NOT NULL);");
        ExecuteNonQuery(
            connection,
            "CREATE TABLE IF NOT EXISTS level_blob ("
            + "level_slot_id TEXT PRIMARY KEY, wave_checkpoint INTEGER NOT NULL, "
            + "sim_snapshot BLOB NOT NULL, command_tail BLOB NOT NULL);");
    }

    private static void StampVersion(SqliteConnection connection, int formatVersion)
    {
        ExecuteNonQuery(
            connection,
            "INSERT INTO schema_version (id, version) VALUES (1, $version) "
            + "ON CONFLICT(id) DO UPDATE SET version = excluded.version;",
            ("$version", formatVersion));
    }

    private static void EnsureWritableVersion(int formatVersion)
    {
        if (formatVersion != SaveVersion.Current)
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatVersion),
                formatVersion,
                $"Writes always emit the current format version ({SaveVersion.Current}); "
                + "only reads migrate forward.");
        }
    }

    // ------------------------------------------------------------- read path

    private SqliteConnection OpenDatabaseForRead()
    {
        if (!_files.Exists(_dbRelativePath))
        {
            throw new InvalidOperationException(
                "The save database does not exist — nothing has been saved yet.");
        }

        // ReadWrite (not ReadWriteCreate): a read must never fabricate a store,
        // and migrations on the read path need write access to the real one.
        var connection = OpenConnection(SqliteOpenMode.ReadWrite);
        ApplyDurabilityPragmas(connection);
        return connection;
    }

    /// <summary>
    /// Stored format version; a database file without the version authority
    /// is not an EraCore save store and fails as corrupt.
    /// </summary>
    private static int ReadStoredVersionOrThrow(SqliteConnection connection)
    {
        var stored = TryReadStoredVersion(connection);
        return stored ?? throw new SaveCorruptException(
            "The database exists but carries no schema_version authority row — "
            + "it is not an intact EraCore save store.",
            0);
    }

    private static int? TryReadStoredVersion(SqliteConnection connection)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master "
                + "WHERE type = 'table' AND name = 'schema_version';";
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
            {
                return null;
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version WHERE id = 1;";
        return command.ExecuteScalar() is { } result ? Convert.ToInt32(result) : null;
    }

    // -------------------------------------------------------------- plumbing

    private SqliteConnection OpenConnection(SqliteOpenMode mode)
    {
        // Physical path derived from the injected root at open time (D-03):
        // the port itself never contains an absolute path literal. SQLite
        // creates missing FILES but not their parent directories, so the
        // create-mode write path materializes the directory tree itself.
        var physicalPath = Path.GetFullPath(Path.Combine(_files.Root, _dbRelativePath));
        if (mode == SqliteOpenMode.ReadWriteCreate)
        {
            var directory = Path.GetDirectoryName(physicalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = physicalPath,
            Mode = mode,
            Pooling = false, // deterministic file-handle release (rotation copies, test cleanup)
            DefaultTimeout = 3, // fail fast when an outside writer wedges the file
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Shared parameterized-command helper for the persistence backend.</summary>
    internal static void ExecuteNonQuery(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
