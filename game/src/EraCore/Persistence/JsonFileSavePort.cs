using System.Text.Json;
using EraCore.Contracts.Persistence;
using EraCore.IO;

namespace EraCore.Persistence;

/// <summary>
/// Web fallback save backend (K-7 / T-100 / 07 D-32, R-E2): pure JSON files
/// under the injected <see cref="IFileStore"/> root — the campaign domain is
/// ONE document (<c>saves/campaign.json</c>) and the level domain is one
/// file per slot (<c>saves/levels/slot_&lt;encoded id&gt;.json</c>). This
/// backend never touches <c>Microsoft.Data.Sqlite</c>: the native
/// <c>e_sqlite3</c> library cannot ship inside the wasm export, so the Web
/// build runs entirely on BCL file IO.
///
/// Durability discipline (mirror of the SQLite backend's D-11 contract):
/// there is no WAL here — the commit point is an ATOMIC FILE REPLACE. Every
/// write serializes the full document, writes it to a
/// <c>&lt;file&gt;.tmp</c> sibling through a write-through stream that
/// fsyncs, then renames over the live file in one
/// <c>File.Move(overwrite)</c> step: a crash can never expose a half-written
/// document (verified by crash-injection tests — a torn image survives only
/// as ignored <c>.tmp</c> garbage, never as the live file). Before every
/// write the previous file is rotated into <c>&lt;file&gt;.bak</c> and
/// <c>&lt;file&gt;.bak2</c> (at most two generations, 07 D-11 "每槽保留最近
/// 2 版" — rotation is per FILE: writing one slot never touches another
/// slot's generations, see the semantic-difference tests).
///
/// Version and corruption semantics align with <see cref="SqliteSavePort"/>:
/// each envelope carries the format-version authority (the analogue of the
/// <c>schema_version</c> row); reads of a different stored version go
/// through the same decision points as <see cref="SaveMigrator"/> (v1 is the
/// identity; newer-than-requested or an unresolvable older version fails as
/// <see cref="SaveCorruptException"/> carrying the stored version); a
/// decodable envelope with a damaged payload string fails as
/// <see cref="SaveCorruptException"/> with the stored version and the
/// <see cref="JsonException"/> inner exception.
/// </summary>
public sealed class JsonFileSavePort : ISavePort
{
    /// <summary>Campaign domain document (relative to the store root).</summary>
    public const string CampaignFileName = "campaign.json";

    /// <summary>Level domain directory (relative to the store root).</summary>
    public const string LevelsDirectoryName = "levels";

    /// <summary>Suffix of the newest backup generation (the previous file).</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>Suffix of the oldest backup generation (two generations maximum).</summary>
    public const string BackupSuffixGeneration2 = ".bak2";

    /// <summary>Suffix of the in-flight atomic-write sibling (never read back).</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Default store root (relative to the injected IFileStore root).</summary>
    public const string DefaultStoreRootRelativePath = "saves";

    private readonly IFileStore _files;
    private readonly string _storeRootRelativePath;
    private readonly object _writeGate = new();

    /// <param name="fileStore">Root-injected file access (D-03); owns every path.</param>
    /// <param name="storeRootRelativePath">Store directory relative to the store root.</param>
    public JsonFileSavePort(
        IFileStore fileStore,
        string storeRootRelativePath = DefaultStoreRootRelativePath)
    {
        _files = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        if (string.IsNullOrWhiteSpace(storeRootRelativePath))
        {
            throw new ArgumentException(
                "A store root relative path is required.", nameof(storeRootRelativePath));
        }

        _storeRootRelativePath = storeRootRelativePath;
    }

    /// <summary>Campaign domain file (relative to the injected root).</summary>
    public string CampaignRelativePath => AppendRelative(CampaignFileName);

    /// <summary>Level domain directory (relative to the injected root).</summary>
    public string LevelsRelativePath => AppendRelative(LevelsDirectoryName);

    /// <summary>Level slot file path for <paramref name="levelSlotId"/> (relative, encoded).</summary>
    public string LevelSlotRelativePath(string levelSlotId)
    {
        if (string.IsNullOrWhiteSpace(levelSlotId))
        {
            throw new ArgumentException("A level slot id is required.", nameof(levelSlotId));
        }

        return AppendRelative(LevelsDirectoryName, EncodeSlotFileName(levelSlotId));
    }

    /// <summary>Relative path of the newest backup generation of a store file.</summary>
    public static string BackupGeneration1RelativePath(string fileRelativePath) =>
        fileRelativePath + BackupSuffix;

    /// <summary>Relative path of the oldest backup generation of a store file.</summary>
    public static string BackupGeneration2RelativePath(string fileRelativePath) =>
        fileRelativePath + BackupSuffixGeneration2;

    /// <inheritdoc />
    public void WriteCampaign(ICampaignMeta meta, int formatVersion)
    {
        ArgumentNullException.ThrowIfNull(meta);
        EnsureWritableVersion(formatVersion);
        var payloadJson = CampaignSaveJsonContext.SerializeCampaign(CampaignMetaDto.FromMeta(meta));

        lock (_writeGate)
        {
            var relativePath = CampaignRelativePath;
            RotateBackupsBeforeWrite(relativePath);
            AlignStoredVersionBeforeWrite(relativePath);
            WriteFileAtomically(relativePath, JsonSaveFileContext.SerializeEnvelope(
                new SaveFileDocument(formatVersion, payloadJson)));
        }
    }

    /// <inheritdoc />
    public ICampaignMeta ReadCampaign(int formatVersion)
    {
        var relativePath = CampaignRelativePath;
        if (!_files.Exists(relativePath))
        {
            // Same split as the SQLite backend: a store that exists (levels
            // present) without a campaign document vs. no store at all.
            // IFileStore.Exists is a FILE probe, so the levels-domain check
            // goes through the physical directory.
            var storeHasLevelDomain = Directory.Exists(ResolvePhysical(LevelsRelativePath));
            throw new InvalidOperationException(storeHasLevelDomain
                ? "No campaign state has been saved in this store yet."
                : "The save store does not exist — nothing has been saved yet.");
        }

        var (storedVersion, payloadJson) = ReadEnvelope(relativePath);
        ResolveStoredVersion(storedVersion, formatVersion);
        try
        {
            return CampaignSaveJsonContext.DeserializeCampaign(payloadJson);
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
            var relativePath = LevelSlotRelativePath(levelSlotId);
            RotateBackupsBeforeWrite(relativePath);
            AlignStoredVersionBeforeWrite(relativePath);
            WriteFileAtomically(relativePath, JsonSaveFileContext.SerializeEnvelope(
                new SaveFileDocument(formatVersion, JsonSaveFileContext.SerializeLevelPayload(
                    new LevelSaveBlobDto(blob.WaveCheckpoint, blob.SimSnapshotPayload, blob.CommandTailPayload)))));
        }
    }

    /// <inheritdoc />
    public ILevelSaveBlob ReadLevel(string levelSlotId, int formatVersion)
    {
        if (string.IsNullOrWhiteSpace(levelSlotId))
        {
            throw new ArgumentException("A level slot id is required.", nameof(levelSlotId));
        }

        var relativePath = LevelSlotRelativePath(levelSlotId);
        if (!_files.Exists(relativePath))
        {
            throw new InvalidOperationException(
                $"No save blob exists for level slot '{levelSlotId}'.");
        }

        var (storedVersion, payloadJson) = ReadEnvelope(relativePath);
        ResolveStoredVersion(storedVersion, formatVersion);
        try
        {
            return JsonSaveFileContext.DeserializeLevelPayload(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new SaveCorruptException(
                $"The level payload JSON for slot '{levelSlotId}' is damaged and cannot "
                + $"be decoded (stored format version {storedVersion}).",
                storedVersion,
                ex);
        }
    }

    // ------------------------------------------------------------ write path

    /// <summary>
    /// Rolls the backup generations of ONE file forward before a mutating
    /// write: <c>.bak2 := .bak</c>, <c>.bak := current file</c> (at most two
    /// generations, 07 D-11). Rotation is per file and advisory: with an
    /// outside handle pinning the file the rotation is skipped (existing
    /// generations survive) — store atomicity is guaranteed by the atomic
    /// replace, never by the rotation itself. The copy is taken after the
    /// previous write's fsync, so a generation is always a complete document.
    /// </summary>
    private void RotateBackupsBeforeWrite(string relativePath)
    {
        if (!_files.Exists(relativePath))
        {
            return; // first write into this file — nothing to back up yet
        }

        byte[] currentFile;
        try
        {
            currentFile = ReadFileShared(ResolvePhysical(relativePath));
        }
        catch (IOException)
        {
            return; // outside holder: keep current generations, write proceeds
        }

        var generation1 = BackupGeneration1RelativePath(relativePath);
        if (_files.Exists(generation1))
        {
            WriteFileAtomically(
                BackupGeneration2RelativePath(relativePath),
                ReadFileShared(ResolvePhysical(generation1)));
        }

        WriteFileAtomically(generation1, currentFile);
    }

    /// <summary>
    /// Write-path version alignment (mirror of the SQLite backend's
    /// <c>BeginWrite</c> migrate step): a file whose authority is ABSENT is
    /// overwritten (the SQLite backend likewise heals a database without the
    /// <c>schema_version</c> table); an authority that is present but not the
    /// current version must resolve forward or the write fails as
    /// <see cref="SaveCorruptException"/>; an undecodable file also fails —
    /// the write never silently recycles a file it cannot identify.
    /// </summary>
    private void AlignStoredVersionBeforeWrite(string relativePath)
    {
        if (!_files.Exists(relativePath))
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = ReadFileShared(ResolvePhysical(relativePath));
        }
        catch (IOException)
        {
            return; // outside holder: the atomic replace below still decides
        }

        SaveFileDocument document;
        try
        {
            document = JsonSaveFileContext.DeserializeEnvelope(bytes);
        }
        catch (JsonException ex)
        {
            throw new SaveCorruptException(
                $"The save file '{relativePath}' exists but is not a decodable "
                + "EraCore JSON save envelope — it carries no format-version authority.",
                0,
                ex);
        }

        if (document.FormatVersion is int stored)
        {
            ResolveStoredVersion(stored, SaveVersion.Current);
        }
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

    /// <summary>
    /// Read-path version resolution with the SAME decision points as
    /// <see cref="SaveMigrator.Migrate"/>: equal versions are the identity;
    /// a stored version newer than the target never migrates backward; an
    /// older version needs a registered hop. v1 ships no hops (the SQLite
    /// chain ships none either) — future format bumps register their steps
    /// in BOTH backends under the same review discipline as
    /// <see cref="SaveVersion"/>.
    /// </summary>
    private static void ResolveStoredVersion(int storedVersion, int targetVersion)
    {
        if (storedVersion == targetVersion)
        {
            return; // identity: nothing to do (v1 -> v1)
        }

        if (storedVersion > targetVersion)
        {
            throw new SaveCorruptException(
                $"Save store is at format version {storedVersion}, newer than the requested "
                + $"{targetVersion}; backward migration does not exist.",
                storedVersion);
        }

        throw new SaveCorruptException(
            $"No migration step is registered for {storedVersion} -> {storedVersion + 1}; the "
            + "chain from the stored version to the requested version is broken.",
            storedVersion);
    }

    // ------------------------------------------------------------- read path

    private (int StoredVersion, string PayloadJson) ReadEnvelope(string relativePath)
    {
        SaveFileDocument document;
        try
        {
            document = JsonSaveFileContext.DeserializeEnvelope(
                ReadFileShared(ResolvePhysical(relativePath)));
        }
        catch (JsonException ex)
        {
            throw new SaveCorruptException(
                $"The save file '{relativePath}' exists but is not a decodable EraCore "
                + "JSON save envelope — it carries no format-version authority.",
                0,
                ex);
        }

        // A missing FormatVersion field deserializes to null: the file is
        // intact JSON but carries no authority — the analogue of a database
        // without the schema_version table (stored version 0).
        var storedVersion = document.FormatVersion
            ?? throw new SaveCorruptException(
                $"The save file '{relativePath}' carries no format-version authority — "
                + "it is not an intact EraCore save store.",
                0);
        var payloadJson = document.PayloadJson
            ?? throw new SaveCorruptException(
                $"The save file '{relativePath}' carries no payload.",
                storedVersion);

        return (storedVersion, payloadJson);
    }

    // -------------------------------------------------------------- plumbing

    private string AppendRelative(params string[] segments) =>
        Path.Join(new[] { _storeRootRelativePath }.Concat(segments).ToArray());

    /// <summary>
    /// Injective, filesystem-safe slot file name: unreserved id characters
    /// pass through, everything else (separators, <c>%</c>, traversal
    /// segments, device names) percent-encodes — the encoded name can never
    /// leave the levels directory, and distinct ids map to distinct files.
    /// </summary>
    private static string EncodeSlotFileName(string levelSlotId) =>
        "slot_" + Uri.EscapeDataString(levelSlotId) + ".json";

    /// <summary>
    /// Physical path derived from the injected root at use time (D-03): the
    /// port itself never contains an absolute path literal — the same
    /// discipline as <see cref="SqliteSavePort"/>'s open path.
    /// </summary>
    private string ResolvePhysical(string relativePath) =>
        Path.GetFullPath(Path.Combine(_files.Root, relativePath));

    /// <summary>
    /// The atomic-write commit point: write-through temp file, fsync, then a
    /// single <c>File.Move(overwrite)</c> — an atomic replace within one
    /// volume, so a reader either sees the whole previous document or the
    /// whole new one, never a mix.
    /// </summary>
    private void WriteFileAtomically(string relativePath, byte[] bytes)
    {
        var physicalPath = ResolvePhysical(relativePath);
        var directory = Path.GetDirectoryName(physicalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = physicalPath + TempSuffix;
        using (var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.WriteThrough))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true); // fsync BEFORE the rename becomes visible
        }

        ReplaceWithRetry(tempPath, physicalPath);
    }

    /// <summary>
    /// The atomic replace with a bounded busy-retry: Windows MoveFileEx fails
    /// fast with access-denied when an outside transient holder (antivirus,
    /// search indexer) pins the destination without sharing delete access —
    /// retrying a few times absorbs transients (the JSON analogue of the
    /// SQLite backend's busy timeout), and a persistent holder still fails
    /// loudly instead of queueing writes silently.
    /// </summary>
    private static void ReplaceWithRetry(string tempPath, string physicalPath)
    {
        const int attempts = 5;
        const int retryDelayMs = 25;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, physicalPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts
                && (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(retryDelayMs);
            }
        }
    }

    /// <summary>
    /// Read that tolerates a concurrent atomic replace: the delete-share
    /// lets a writer rename over the path while this reader keeps streaming
    /// the complete old document (the SQLite test suite's shared-read helper
    /// pattern).
    /// </summary>
    private static byte[] ReadFileShared(string physicalPath)
    {
        using var stream = new FileStream(
            physicalPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
