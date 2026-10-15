using System.Collections.Concurrent;
using EraCore.Contracts.Persistence;
using EraCore.IO;
using EraCore.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EraCore.Tests.Persistence;

// [F-164] Web 版约束（07 D-32 / R-E2）：同一测试夹具双后端参数化全绿。
//
// The abstract fixture below is the ISavePort CONTRACT surface — every test
// runs once per backend (SQLite desktop / JSON Web fallback) through the
// two sealed subclasses at the end of this file. Backend-specific storage
// details (raw SQL, file paths, WAL images) live in the dedicated
// SqliteSavePort*/JsonFileSavePort suites; everything here must hold for
// BOTH backends through the port alone. Semantic differences between the
// backends are not hidden — they are asserted explicitly in
// JsonFileSavePortTests (declared-differences section).
public abstract class SavePortEquivalenceTests : IDisposable
{
    protected const string DbRelativePath = "saves/profile.db";
    protected const string JsonStoreRootRelativePath = "saves";

    protected SavePortEquivalenceTests()
    {
        Root = Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));
        Files = new DevFileStore(Root);
    }

    /// <summary>Per-test injected writable root (D-03; deleted in Dispose).</summary>
    protected string Root { get; }

    protected DevFileStore Files { get; }

    // ------------------------------------------------- backend seam (hooks)

    /// <summary>Creates the backend under test over the shared sandbox root.</summary>
    protected abstract ISavePort CreatePort();

    /// <summary>Damages the stored campaign payload while the version authority stays intact.</summary>
    protected abstract void DamageCampaignPayloadInStore();

    /// <summary>Builds a store that exists but carries no version authority.</summary>
    protected abstract void CreateStoreWithoutVersionAuthority();

    /// <summary>Builds an intact store whose authority says <paramref name="futureVersion"/>.</summary>
    protected abstract void CreateFutureVersionStore(int futureVersion);

    /// <summary>Opens the store as of backup generation 1 (.bak) or 2 (.bak2).</summary>
    protected abstract ISavePort CreateBackupGenerationPort(int generation);

    /// <summary>Counts backup generation files (.bak/.bak2) the backend produced.</summary>
    protected abstract int CountBackupGenerationFiles();

    /// <summary>Asserts the store holds exactly <paramref name="expected"/> level slots.</summary>
    protected abstract void AssertLevelSlotCount(int expected);

    public void Dispose()
    {
        // Only this fixture's own GUID sandbox — sibling GUIDs may belong to
        // concurrent runs (same policy as DevFileStoreTests).
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    // ------------------------------------------------------------ legal

    [Fact]
    public void Campaign_roundtrips_field_by_field()
    {
        CreatePort().WriteCampaign(SqliteSavePortTestBase.SampleCampaign(909090), SaveVersion.Current);

        var readBack = CreatePort().ReadCampaign(SaveVersion.Current);

        Assert.Equal(909090, readBack.WorldSeed);
        Assert.Equal(2, readBack.EraIndex);
        Assert.Equal(2, readBack.ResourcePool.Count);
        Assert.Equal(150, readBack.ResourcePool["steel"]);
        Assert.Equal(9, readBack.ResourcePool["intel"]);
        Assert.Equal(new[] { "tech.agri", "tech.smith" }, readBack.UnlockedTechs);
        Assert.Equal(new[] { "relic.cheb", "relic.map" }, readBack.RelicRegistry);
        Assert.Equal(2, readBack.LevelLayoutSnapshots.Count);
        Assert.Equal("level.e1.m01", readBack.LevelLayoutSnapshots[0].LevelId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, readBack.LevelLayoutSnapshots[0].LayoutPayload);
        Assert.Equal("level.e1.m02", readBack.LevelLayoutSnapshots[1].LevelId);
        Assert.Equal(new byte[] { 9, 8, 7 }, readBack.LevelLayoutSnapshots[1].LayoutPayload);
    }

    [Fact]
    public void Level_roundtrips_across_reopened_stores()
    {
        var port = CreatePort();
        var original = SqliteSavePortTestBase.MarkerBlob(0x3C, 7, 15);
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(1), SaveVersion.Current);

        port.WriteLevel("level.e1.m02", original, SaveVersion.Current);

        var readBack = CreatePort().ReadLevel("level.e1.m02", SaveVersion.Current);
        Assert.Equal(15, readBack.WaveCheckpoint);
        Assert.Equal(original.SimSnapshotPayload, readBack.SimSnapshotPayload);
        Assert.Equal(original.CommandTailPayload, readBack.CommandTailPayload);
    }

    [Fact]
    public void Overwriting_one_level_slot_leaves_other_slots_and_campaign_intact()
    {
        var port = CreatePort();
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(555), SaveVersion.Current);
        port.WriteLevel("slot.a", SqliteSavePortTestBase.MarkerBlob(0xA0, 1, 10), SaveVersion.Current);
        port.WriteLevel("slot.b", SqliteSavePortTestBase.MarkerBlob(0x5A, 1, 12), SaveVersion.Current);

        port.WriteLevel("slot.a", SqliteSavePortTestBase.MarkerBlob(0xA0, 2, 14), SaveVersion.Current);

        Assert.Equal(14, port.ReadLevel("slot.a", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(12, port.ReadLevel("slot.b", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(555, port.ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    [Fact]
    public void Slot_ids_with_separator_or_traversal_characters_roundtrip()
    {
        // The contract takes an opaque slot id string; a backend must not
        // silently lose slots whose ids contain path-shaped characters (and
        // the JSON file backend must not let them escape its levels dir).
        var port = CreatePort();
        port.WriteLevel("era1/m02:hard", SqliteSavePortTestBase.MarkerBlob(0x11, 1, 21), SaveVersion.Current);
        port.WriteLevel("..", SqliteSavePortTestBase.MarkerBlob(0x22, 1, 22), SaveVersion.Current);

        Assert.Equal(21, port.ReadLevel("era1/m02:hard", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(22, port.ReadLevel("..", SaveVersion.Current).WaveCheckpoint);
    }

    [Fact]
    public void Write_stamps_the_format_version_and_read_returns_it()
    {
        // 版本号往返：writes stamp the authority at Current; reads accept it
        // without migration (identity chain).
        CreatePort().WriteCampaign(SqliteSavePortTestBase.SampleCampaign(8), SaveVersion.Current);

        Assert.Equal(8, CreatePort().ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void First_write_creates_no_backup_generations()
    {
        var port = CreatePort();

        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(3), SaveVersion.Current);

        Assert.Equal(0, CountBackupGenerationFiles());
    }

    [Fact]
    public void Slot_payload_backups_rotate_two_generations_and_stay_recoverable()
    {
        // 07 D-11 contract wording: each slot keeps the two most recent
        // payload generations. Three writes → live = seq3, generation 1 =
        // seq2, generation 2 = seq1 — the same observable generations both
        // backends expose, one write to one slot at a time.
        var port = CreatePort();
        port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(0xA0, 1, 10), SaveVersion.Current);
        port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(0xA0, 2, 12), SaveVersion.Current);
        port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(0xA0, 3, 14), SaveVersion.Current);

        Assert.Equal(2, CountBackupGenerationFiles());
        Assert.Equal(14, port.ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);

        Assert.Equal(12, CreateBackupGenerationPort(1).ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(10, CreateBackupGenerationPort(2).ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);
    }

    [Fact]
    public void Campaign_payload_backups_rotate_and_stay_recoverable()
    {
        var port = CreatePort();
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(111), SaveVersion.Current);
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(222), SaveVersion.Current);
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(333), SaveVersion.Current);

        Assert.Equal(2, CountBackupGenerationFiles());
        Assert.Equal(333, port.ReadCampaign(SaveVersion.Current).WorldSeed);
        Assert.Equal(222, CreateBackupGenerationPort(1).ReadCampaign(SaveVersion.Current).WorldSeed);
        Assert.Equal(111, CreateBackupGenerationPort(2).ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    [Fact]
    public void Damaged_campaign_payload_read_throws_corrupt_with_stored_version()
    {
        CreatePort().WriteCampaign(SqliteSavePortTestBase.SampleCampaign(222), SaveVersion.Current);
        DamageCampaignPayloadInStore();

        var ex = Assert.Throws<SaveCorruptException>(
            () => CreatePort().ReadCampaign(SaveVersion.Current));

        Assert.Equal(SaveVersion.Current, ex.StoredVersion);
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
        Assert.Contains("campaign", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Store_without_version_authority_reads_as_corrupt()
    {
        CreateStoreWithoutVersionAuthority();

        var ex = Assert.Throws<SaveCorruptException>(
            () => CreatePort().ReadCampaign(SaveVersion.Current));

        Assert.Equal(0, ex.StoredVersion);
    }

    [Fact]
    public void Future_version_store_reads_and_writes_as_corrupt()
    {
        // Port-level migrator semantics: a store stamped with a version this
        // build cannot resolve fails identically on both backends — reads
        // AND writes (the SQLite write path migrates inside BeginWrite and
        // the JSON write path aligns before overwrite).
        CreateFutureVersionStore(SaveVersion.Current + 1);
        var port = CreatePort();

        var readEx = Assert.Throws<SaveCorruptException>(
            () => port.ReadCampaign(SaveVersion.Current));
        var writeEx = Assert.Throws<SaveCorruptException>(
            () => port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(1), SaveVersion.Current));

        Assert.Equal(SaveVersion.Current + 1, readEx.StoredVersion);
        Assert.Equal(SaveVersion.Current + 1, writeEx.StoredVersion);
    }

    [Fact]
    public void Write_heals_a_store_that_lacks_the_version_authority()
    {
        // The authority is ABSENT (not wrong): a write rebuilds the store
        // over it — the SQLite backend does the same when the database
        // exists without the schema_version table.
        CreateStoreWithoutVersionAuthority();

        var port = CreatePort();
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(77), SaveVersion.Current);

        Assert.Equal(77, port.ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    // ------------------------------------------------------------ illegal

    public static TheoryData<int> NonCurrentFormatVersions => new()
    {
        SaveVersion.Current + 1,
        SaveVersion.Current - 1, // == 0 today: "no version at all" is rejected too
    };

    [Theory]
    [MemberData(nameof(NonCurrentFormatVersions))]
    public void Write_rejects_non_current_format_versions(int formatVersion)
    {
        var port = CreatePort();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(1), formatVersion));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => port.WriteLevel("L", SqliteSavePortTestBase.MarkerBlob(1, 1, 10), formatVersion));
    }

    [Fact]
    public void Write_rejects_null_meta_null_blob_and_blank_slot_id()
    {
        var port = CreatePort();

        Assert.Throws<ArgumentNullException>(() => port.WriteCampaign(null!, SaveVersion.Current));
        Assert.Throws<ArgumentNullException>(
            () => port.WriteLevel("L", null!, SaveVersion.Current));
        Assert.Throws<ArgumentException>(
            () => port.WriteLevel("   ", SqliteSavePortTestBase.MarkerBlob(1, 1, 10), SaveVersion.Current));
        Assert.Throws<ArgumentException>(
            () => port.ReadLevel("", SaveVersion.Current));
    }

    [Fact]
    public void Reading_a_missing_store_or_slot_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(
            () => CreatePort().ReadCampaign(SaveVersion.Current));
        Assert.Throws<InvalidOperationException>(
            () => CreatePort().ReadLevel("level.e1.m01", SaveVersion.Current));

        var port = CreatePort();
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(6), SaveVersion.Current);
        Assert.Throws<InvalidOperationException>(
            () => port.ReadLevel("never-written-slot", SaveVersion.Current));
    }

    // --------------------------------------------------------- concurrency

    [Fact]
    public async Task Concurrent_writes_to_different_slots_serialize_without_loss()
    {
        const int writesPerThread = 15;
        var port = CreatePort();
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(4242), SaveVersion.Current);

        var failures = new ConcurrentQueue<Exception>();
        var slotA = Task.Run(() =>
        {
            try
            {
                for (var i = 1; i <= writesPerThread; i++)
                {
                    port.WriteLevel("slot.a", SqliteSavePortTestBase.MarkerBlob(0xA0, i, 10 + i), SaveVersion.Current);
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        });
        var slotB = Task.Run(() =>
        {
            try
            {
                for (var i = 1; i <= writesPerThread; i++)
                {
                    port.WriteLevel("slot.b", SqliteSavePortTestBase.MarkerBlob(0x5A, i, 20 + i), SaveVersion.Current);
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        });

        await Task.WhenAll(slotA, slotB);

        Assert.Empty(failures); // the backend's write gate serialized every call
        Assert.Equal(25, port.ReadLevel("slot.a", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(35, port.ReadLevel("slot.b", SaveVersion.Current).WaveCheckpoint);

        // The last write per slot won exactly: marker + sequence survive intact.
        var blobA = port.ReadLevel("slot.a", SaveVersion.Current);
        Assert.Equal(0xA0, blobA.SimSnapshotPayload[0]);
        Assert.Equal(writesPerThread, blobA.SimSnapshotPayload[1]);
        Assert.Equal((byte)(255 - 0xA0), blobA.CommandTailPayload[0]);
        var blobB = port.ReadLevel("slot.b", SaveVersion.Current);
        Assert.Equal(0x5A, blobB.SimSnapshotPayload[0]);
        Assert.Equal(writesPerThread, blobB.SimSnapshotPayload[1]);

        // And nothing leaked into other domains or slots.
        Assert.Equal(4242, port.ReadCampaign(SaveVersion.Current).WorldSeed);
        AssertLevelSlotCount(2);
    }

    [Fact]
    public async Task Concurrent_reads_during_writes_only_observe_complete_states()
    {
        const int writesPerThread = 15;
        var port = CreatePort();
        port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(7), SaveVersion.Current);
        // Prime the slot so concurrent readers never race a missing row
        // (that case is covered by the missing-slot unit test).
        port.WriteLevel("slot.a", SqliteSavePortTestBase.MarkerBlob(0xA0, 0, 30), SaveVersion.Current);

        var failures = new ConcurrentQueue<Exception>();
        var observed = new ConcurrentQueue<int>();

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= writesPerThread; i++)
            {
                port.WriteLevel("slot.a", SqliteSavePortTestBase.MarkerBlob(0xA0, i, 30 + i), SaveVersion.Current);
            }
        });
        var reader = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < writesPerThread * 4; i++)
                {
                    var blob = port.ReadLevel("slot.a", SaveVersion.Current);
                    // Every visible payload must be a fully-written marker
                    // blob: the right slot marker and a consistent sequence
                    // number in both payloads — torn writes would break this.
                    Assert.Equal(0xA0, blob.SimSnapshotPayload[0]);
                    Assert.Equal(blob.SimSnapshotPayload[1], blob.CommandTailPayload[1]);
                    observed.Enqueue(blob.SimSnapshotPayload[1]);
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Empty(failures);
        Assert.NotEmpty(observed); // reads really raced the writer
        Assert.Equal(30 + writesPerThread, port.ReadLevel("slot.a", SaveVersion.Current).WaveCheckpoint);
    }
}

/// <summary>
/// The equivalence suite bound to the desktop SQLite backend (T-100; the
/// pre-existing behaviors of T-041, now re-run through the shared contract
/// fixture).
/// </summary>
public sealed class SqliteBackendEquivalenceTests : SavePortEquivalenceTests
{
    private string DbPhysicalPath => Path.GetFullPath(Path.Combine(Root, DbRelativePath));

    protected override ISavePort CreatePort() => new SqliteSavePort(Files, DbRelativePath);

    protected override void DamageCampaignPayloadInStore()
    {
        using var raw = OpenRawConnection();
        using var command = raw.CreateCommand();
        command.CommandText = "UPDATE campaign_meta SET payload_json = '{ this is not json';";
        command.ExecuteNonQuery();
    }

    protected override void CreateStoreWithoutVersionAuthority()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPhysicalPath)!);
        using var raw = OpenRawConnection();
        using var command = raw.CreateCommand();
        command.CommandText = "CREATE TABLE something_else (x INTEGER);";
        command.ExecuteNonQuery();
    }

    protected override void CreateFutureVersionStore(int futureVersion)
    {
        CreatePort().WriteCampaign(SqliteSavePortTestBase.SampleCampaign(1), SaveVersion.Current);
        using var raw = OpenRawConnection();
        using var command = raw.CreateCommand();
        command.CommandText = "UPDATE schema_version SET version = $version;";
        command.Parameters.AddWithValue("$version", futureVersion);
        command.ExecuteNonQuery();
    }

    protected override ISavePort CreateBackupGenerationPort(int generation) => new SqliteSavePort(
        Files,
        DbRelativePath + (generation == 1
            ? SqliteSavePort.BackupSuffix
            : SqliteSavePort.BackupSuffixGeneration2));

    protected override int CountBackupGenerationFiles()
    {
        var savesDir = Path.GetDirectoryName(DbPhysicalPath)!;
        return Directory.EnumerateFiles(
            savesDir, Path.GetFileName(DbPhysicalPath) + ".bak*").Count();
    }

    protected override void AssertLevelSlotCount(int expected)
    {
        using var raw = OpenRawConnection();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM level_blob;";
        Assert.Equal(expected, Convert.ToInt64(command.ExecuteScalar()));
    }

    private SqliteConnection OpenRawConnection(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var directory = Path.GetDirectoryName(DbPhysicalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory); // SQLite creates files, not folders
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPhysicalPath,
            Mode = mode,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}

/// <summary>
/// The equivalence suite bound to the Web JSON fallback backend (T-100 /
/// 07 D-32 / R-E2).
/// </summary>
public sealed class JsonBackendEquivalenceTests : SavePortEquivalenceTests
{
    private static string CampaignFile => JsonFileSavePort.DefaultStoreRootRelativePath
        + "/" + JsonFileSavePort.CampaignFileName;

    protected override ISavePort CreatePort() => new JsonFileSavePort(Files, JsonStoreRootRelativePath);

    protected override void DamageCampaignPayloadInStore()
    {
        // Envelope stays decodable with its version authority intact; only
        // the payload string becomes garbage — the JSON analogue of the
        // SQLite payload_json UPDATE.
        WriteRawEnvelope(CampaignFile, new SaveFileDocument(
            SaveVersion.Current, "{ this is not json"));
    }

    protected override void CreateStoreWithoutVersionAuthority()
    {
        // Intact JSON document with no formatVersion field: readable but
        // carrying no authority (the analogue of a database without the
        // schema_version table).
        WriteRawEnvelope(CampaignFile, new SaveFileDocument(null, null));
    }

    protected override void CreateFutureVersionStore(int futureVersion)
    {
        WriteRawEnvelope(CampaignFile, new SaveFileDocument(
            futureVersion,
            CampaignSaveJsonContext.SerializeCampaign(SqliteSavePortTestBase.SampleCampaign(1))));
    }

    private void WriteRawEnvelope(string relativePath, SaveFileDocument document)
    {
        Files.WriteAllBytes(relativePath, JsonSaveFileContext.SerializeEnvelope(document));
    }

    protected override ISavePort CreateBackupGenerationPort(int generation)
    {
        // The recovery operation a user (or the Web shell) performs: copy
        // the store, swap each generation-N file over its live sibling, and
        // open a port over the result — the file-based analogue of opening
        // the port on the SQLite .bak database.
        var storeRoot = Path.GetFullPath(Path.Combine(Root, JsonStoreRootRelativePath));
        var restoreRoot = Path.Combine(Root, "restore-g" + generation);
        if (Directory.Exists(restoreRoot))
        {
            Directory.Delete(restoreRoot, recursive: true);
        }

        CopyDirectory(new DirectoryInfo(storeRoot), new DirectoryInfo(restoreRoot));
        var suffix = generation == 1
            ? JsonFileSavePort.BackupSuffix
            : JsonFileSavePort.BackupSuffixGeneration2;
        foreach (var backup in Directory.EnumerateFiles(restoreRoot, "*" + suffix, SearchOption.AllDirectories))
        {
            File.Copy(backup, backup[..^suffix.Length], overwrite: true);
        }

        return new JsonFileSavePort(Files, Path.GetRelativePath(Root, restoreRoot));
    }

    protected override int CountBackupGenerationFiles() =>
        Directory.EnumerateFiles(
            Path.GetFullPath(Path.Combine(Root, JsonStoreRootRelativePath)),
            "*.bak*", SearchOption.AllDirectories).Count();

    protected override void AssertLevelSlotCount(int expected)
    {
        var levelsDir = Path.GetFullPath(Path.Combine(
            Root, JsonStoreRootRelativePath, JsonFileSavePort.LevelsDirectoryName));
        Assert.Equal(expected, Directory.GetFiles(levelsDir, "slot_*.json").Length);
    }

    private static void CopyDirectory(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (var file in source.GetFiles())
        {
            file.CopyTo(Path.Combine(target.FullName, file.Name));
        }

        foreach (var dir in source.GetDirectories())
        {
            CopyDirectory(dir, new DirectoryInfo(Path.Combine(target.FullName, dir.Name)));
        }
    }
}
