using EraCore.Contracts.Persistence;
using EraCore.IO;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

// [F-164] Web 版约束：C-04 Web JSON 后备后端（T-100 / 07 D-32 / R-E2）。
//
// JSON-backend-specific behavior: physical layout, slot-id encoding, the
// temp+rename crash-safety discipline, per-slot backup rotation, and the
// DECLARED semantic differences versus the SQLite backend (last section —
// capability differences are asserted as facts, never papered over).
public sealed class JsonFileSavePortTests : IDisposable
{
    public JsonFileSavePortTests()
    {
        Root = Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));
        Files = new DevFileStore(Root);
        Port = new JsonFileSavePort(Files);
    }

    private string Root { get; }

    private DevFileStore Files { get; }

    private JsonFileSavePort Port { get; }

    private string CampaignFile => JsonFileSavePort.DefaultStoreRootRelativePath
        + "/" + JsonFileSavePort.CampaignFileName;

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    // ------------------------------------------------------------ legal

    [Fact]
    public void Store_layout_is_campaign_file_plus_encoded_slot_files()
    {
        Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(1), SaveVersion.Current);
        Port.WriteLevel("level.e1.m01", SqliteSavePortTestBase.MarkerBlob(1, 1, 10), SaveVersion.Current);

        Assert.True(Files.Exists(CampaignFile));
        Assert.True(Files.Exists(Port.LevelSlotRelativePath("level.e1.m01")));
        Assert.Equal("saves/levels/slot_level.e1.m01.json", Port.LevelSlotRelativePath("level.e1.m01")
            .Replace('\\', '/'));
    }

    [Fact]
    public void Path_shaped_slot_ids_encode_into_the_levels_directory_only()
    {
        // Traversal and separator ids must not escape the levels dir — the
        // encoding is injective, so distinct ids keep distinct files.
        Port.WriteLevel("a/b", SqliteSavePortTestBase.MarkerBlob(1, 1, 1), SaveVersion.Current);
        Port.WriteLevel("..", SqliteSavePortTestBase.MarkerBlob(2, 1, 2), SaveVersion.Current);
        Port.WriteLevel("CON", SqliteSavePortTestBase.MarkerBlob(3, 1, 3), SaveVersion.Current);

        Assert.True(Files.Exists("saves/levels/slot_a%2Fb.json"));
        Assert.True(Files.Exists("saves/levels/slot_...json"));
        Assert.True(Files.Exists("saves/levels/slot_CON.json")); // device name neutralized by prefix
        Assert.False(Files.Exists("saves/a")); // nothing escaped into the store root
        // And nothing outside the injected root (plain File.Exists: the
        // store itself fail-closes on escaping paths, which would throw).
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(Root, "..", "a"))));
        Assert.Equal(1, Port.ReadLevel("a/b", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(2, Port.ReadLevel("..", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(3, Port.ReadLevel("CON", SaveVersion.Current).WaveCheckpoint);
    }

    [Fact]
    public void Successful_write_leaves_no_temp_residue()
    {
        Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(5), SaveVersion.Current);
        Port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 1, 1), SaveVersion.Current);

        var storeDir = Path.GetFullPath(Path.Combine(Root, JsonFileSavePort.DefaultStoreRootRelativePath));
        Assert.Empty(Directory.EnumerateFiles(
            storeDir, "*" + JsonFileSavePort.TempSuffix, SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Stale_temp_file_from_an_interrupted_write_is_ignored()
    {
        // Crash injection at the temp-write stage: the interrupted image
        // lives only in the .tmp sibling — the live file keeps the complete
        // previous state and the next write recovers by overwriting the temp.
        Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(42), SaveVersion.Current);

        Files.WriteAllBytes(
            CampaignFile + JsonFileSavePort.TempSuffix,
            "{\"formatVersion\":1,\"payl"u8.ToArray()); // torn mid-document

        Assert.Equal(42, Port.ReadCampaign(SaveVersion.Current).WorldSeed);
        Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(43), SaveVersion.Current);
        Assert.Equal(43, Port.ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    [Fact]
    public void Damaged_level_payload_read_throws_corrupt_with_stored_version()
    {
        Port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 1, 10), SaveVersion.Current);
        Files.WriteAllBytes(
            Port.LevelSlotRelativePath("L1"),
            JsonSaveFileContext.SerializeEnvelope(
                new SaveFileDocument(SaveVersion.Current, "{ this is not json")));

        var ex = Assert.Throws<SaveCorruptException>(
            () => Port.ReadLevel("L1", SaveVersion.Current));

        Assert.Equal(SaveVersion.Current, ex.StoredVersion);
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
        Assert.Contains("L1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undecodable_store_file_reads_as_corrupt_without_authority()
    {
        Files.WriteAllBytes(CampaignFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var ex = Assert.Throws<SaveCorruptException>(
            () => Port.ReadCampaign(SaveVersion.Current));

        Assert.Equal(0, ex.StoredVersion);
    }

    [Fact]
    public void Write_refuses_an_undecodable_store_file_it_cannot_identify()
    {
        // The SQLite backend likewise fails its engine probe on a non-database
        // file instead of recycling it; only an ABSENT authority (valid JSON,
        // no formatVersion) heals — that split is asserted in the shared
        // equivalence suite.
        Files.WriteAllBytes(CampaignFile, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var ex = Assert.Throws<SaveCorruptException>(
            () => Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(1), SaveVersion.Current));

        Assert.Equal(0, ex.StoredVersion);
    }

    [Fact]
    public void Reading_missing_campaign_after_level_writes_reports_no_campaign_state()
    {
        Port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 1, 1), SaveVersion.Current);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Port.ReadCampaign(SaveVersion.Current));

        Assert.Contains("No campaign state", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_null_file_store_and_blank_store_path()
    {
        Assert.Throws<ArgumentNullException>(() => new JsonFileSavePort(null!));
        Assert.Throws<ArgumentException>(() => new JsonFileSavePort(Files, "  "));
        Assert.Throws<ArgumentException>(() => Port.LevelSlotRelativePath(" "));
    }

    [Fact]
    public void Backup_recovery_by_file_swap_restores_the_previous_state()
    {
        // The user-facing recovery operation on the Web/desktop file store:
        // copy <file>.bak over <file> — no engine tooling required.
        Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(111), SaveVersion.Current);
        Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(222), SaveVersion.Current);

        File.Copy(
            Path.GetFullPath(Path.Combine(Root, CampaignFile + JsonFileSavePort.BackupSuffix)),
            Path.GetFullPath(Path.Combine(Root, CampaignFile)),
            overwrite: true);

        Assert.Equal(111, Port.ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    // ------------------------------------------- declared semantic differences
    //
    // Honest capability statements (07 D-32): the JSON fallback is NOT the
    // SQLite backend; where behavior differs, the difference is asserted
    // here so the contract suite above never accidentally hides it.

    [Fact]
    public void Difference_rotation_is_per_file_not_whole_store()
    {
        // D-11 wording "每槽保留最近 2 版 payload 滚动备份": writing slot B
        // rotates ONLY slot B's file. The SQLite backend realizes the same
        // per-slot guarantee by rotating the whole database file — a strictly
        // larger blast radius that also snapshots the campaign domain — so a
        // cross-domain-coherent .bak generation is a SQLite-ONLY extra, not a
        // contract property (the shared suite asserts the per-slot contract).
        Port.WriteLevel("A", SqliteSavePortTestBase.MarkerBlob(1, 1, 10), SaveVersion.Current);
        Port.WriteLevel("A", SqliteSavePortTestBase.MarkerBlob(1, 2, 11), SaveVersion.Current);
        var generationA = Files.ReadAllBytes(
            Port.LevelSlotRelativePath("A") + JsonFileSavePort.BackupSuffix);

        Port.WriteLevel("B", SqliteSavePortTestBase.MarkerBlob(2, 1, 20), SaveVersion.Current);

        // Slot A's generations are untouched by slot B's write...
        Assert.Equal(
            generationA,
            Files.ReadAllBytes(Port.LevelSlotRelativePath("A") + JsonFileSavePort.BackupSuffix));
        Assert.False(Files.Exists(
            Port.LevelSlotRelativePath("A") + JsonFileSavePort.BackupSuffixGeneration2));
        // ...and slot A's live payload never moved either.
        Assert.Equal(11, Port.ReadLevel("A", SaveVersion.Current).WaveCheckpoint);
    }

    [Fact]
    public void Difference_durability_is_temp_rename_replace_not_wal()
    {
        // No WAL, no -shm, no engine sidecars: the commit point is the atomic
        // file replace itself. The SQLite backend's WAL + synchronous=FULL
        // pair is asserted in its own suite; here the JSON store declares the
        // other discipline — only .json/.bak/.bak2 files exist after traffic.
        Port.WriteCampaign(SqliteSavePortTestBase.SampleCampaign(1), SaveVersion.Current);
        Port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 1, 1), SaveVersion.Current);
        Port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 2, 2), SaveVersion.Current);

        var storeDir = Path.GetFullPath(Path.Combine(Root, JsonFileSavePort.DefaultStoreRootRelativePath));
        var names = Directory.EnumerateFiles(storeDir, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[]
            {
                "campaign.json",
                "slot_L1.json",
                "slot_L1.json.bak",
            },
            names);
    }

    [Fact]
    public void Difference_concurrency_is_a_single_process_write_gate()
    {
        // The JSON backend serializes writers with an in-process lock and
        // keeps readers complete via the atomic replace — there is no
        // cross-PROCESS file locking (SQLite's busy discipline). The Web
        // export is single-threaded by design (threads-off, F-144/D-31), and
        // the desktop primary is SQLite; what is asserted here is that two
        // SEQUENTIAL port instances over one store interleave without loss.
        var secondInstance = new JsonFileSavePort(Files);
        Port.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 1, 10), SaveVersion.Current);

        secondInstance.WriteLevel("L1", SqliteSavePortTestBase.MarkerBlob(1, 2, 12), SaveVersion.Current);

        Assert.Equal(12, Port.ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(12, secondInstance.ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);
    }
}
