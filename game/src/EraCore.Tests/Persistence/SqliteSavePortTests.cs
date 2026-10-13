using System.Text.Json;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EraCore.Tests.Persistence;

// [F-160] 两级存档架构：C-04 SQLite 端口（单库双域/WAL/原子写/滚动备份，T-041）
public class SqliteSavePortTests : SqliteSavePortTestBase
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Campaign_roundtrips_across_reopened_connections_field_by_field()
    {
        CreatePort().WriteCampaign(SampleCampaign(909090), SaveVersion.Current);

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
    public void Level_roundtrips_across_reopened_connections()
    {
        var port = CreatePort();
        var original = MarkerBlob(0x3C, 7, 15);
        port.WriteCampaign(SampleCampaign(1), SaveVersion.Current);

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
        port.WriteCampaign(SampleCampaign(555), SaveVersion.Current);
        port.WriteLevel("slot.a", MarkerBlob(0xA0, 1, 10), SaveVersion.Current);
        port.WriteLevel("slot.b", MarkerBlob(0x5A, 1, 12), SaveVersion.Current);

        port.WriteLevel("slot.a", MarkerBlob(0xA0, 2, 14), SaveVersion.Current);

        Assert.Equal(14, port.ReadLevel("slot.a", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(12, port.ReadLevel("slot.b", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(555, port.ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    [Fact]
    public void Write_stamps_the_format_version_and_read_returns_it()
    {
        // 版本号往返：writes stamp schema_version at Current; reads accept it
        // without migration (identity chain).
        CreatePort().WriteCampaign(SampleCampaign(8), SaveVersion.Current);

        using var raw = OpenRawConnection();
        Assert.Equal(SaveVersion.Current, ScalarLong(raw, "SELECT version FROM schema_version WHERE id = 1;"));
        Assert.Equal(8, CreatePort().ReadCampaign(SaveVersion.Current).WorldSeed);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Store_runs_in_wal_journal_mode_with_synchronous_full()
    {
        // 07 D-11 durability pair, asserted against the real database file:
        // WAL is persistent in the file header; synchronous=FULL (numeric 2)
        // is applied by the port to every connection it opens.
        CreatePort().WriteCampaign(SampleCampaign(2), SaveVersion.Current);

        using var raw = OpenRawConnection(mode: SqliteOpenMode.ReadWrite);
        Assert.Equal("wal", ScalarString(raw, "PRAGMA journal_mode;"));

        SqliteSavePort.ApplyDurabilityPragmas(raw);
        Assert.Equal(2L, ScalarLong(raw, "PRAGMA synchronous;"));
    }

    [Fact]
    public void First_write_creates_no_backup_generations()
    {
        var port = CreatePort();

        port.WriteCampaign(SampleCampaign(3), SaveVersion.Current);

        Assert.True(Files.Exists(DbRelativePath));
        Assert.False(Files.Exists(DbRelativePath + SqliteSavePort.BackupSuffix));
        Assert.False(Files.Exists(DbRelativePath + SqliteSavePort.BackupSuffixGeneration2));
    }

    [Fact]
    public void Backup_generations_rotate_and_cap_at_two()
    {
        var port = CreatePort();
        port.WriteCampaign(SampleCampaign(111), SaveVersion.Current);            // state 1
        port.WriteLevel("L1", MarkerBlob(0xA0, 2, 5), SaveVersion.Current);     // state 2
        port.WriteCampaign(SampleCampaign(333), SaveVersion.Current);           // state 3
        port.WriteLevel("L1", MarkerBlob(0xA0, 4, 9), SaveVersion.Current);     // state 4

        var savesDir = Path.GetDirectoryName(DbPhysicalPath)!;
        var backupFiles = Directory.EnumerateFiles(
            savesDir, Path.GetFileName(DbPhysicalPath) + ".bak*");
        Assert.Equal(2, backupFiles.Count()); // at most two generations, ever

        // Live store holds state 4 (latest write).
        Assert.Equal(9, port.ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);

        // Generation 1 (.bak) is the previous database: state 3.
        var bakPort = new SqliteSavePort(Files, DbRelativePath + SqliteSavePort.BackupSuffix);
        Assert.Equal(333, bakPort.ReadCampaign(SaveVersion.Current).WorldSeed);
        Assert.Equal(5, bakPort.ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);

        // Generation 2 (.bak2) is the state before that: state 2.
        var bak2Port = new SqliteSavePort(Files, DbRelativePath + SqliteSavePort.BackupSuffixGeneration2);
        Assert.Equal(111, bak2Port.ReadCampaign(SaveVersion.Current).WorldSeed);
        Assert.Equal(5, bak2Port.ReadLevel("L1", SaveVersion.Current).WaveCheckpoint);
    }

    // ------------------------------------------------------------ illegal

    [Fact]
    public void Damaged_campaign_json_read_throws_corrupt_with_stored_version()
    {
        CreatePort().WriteCampaign(SampleCampaign(222), SaveVersion.Current);
        using (var raw = OpenRawConnection())
        {
            using var command = raw.CreateCommand();
            command.CommandText = "UPDATE campaign_meta SET payload_json = '{ this is not json';";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<SaveCorruptException>(
            () => CreatePort().ReadCampaign(SaveVersion.Current));

        Assert.Equal(SaveVersion.Current, ex.StoredVersion);
        Assert.IsType<JsonException>(ex.InnerException);
        Assert.Contains("campaign", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Database_without_schema_version_authority_reads_as_corrupt()
    {
        using (var raw = OpenRawConnection())
        {
            using var command = raw.CreateCommand();
            command.CommandText = "CREATE TABLE something_else (x INTEGER);";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<SaveCorruptException>(
            () => CreatePort().ReadCampaign(SaveVersion.Current));

        Assert.Equal(0, ex.StoredVersion);
    }

    [Fact]
    public void Reading_a_missing_database_or_slot_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(
            () => CreatePort().ReadCampaign(SaveVersion.Current));
        Assert.Throws<InvalidOperationException>(
            () => CreatePort().ReadLevel("level.e1.m01", SaveVersion.Current));

        var port = CreatePort();
        port.WriteCampaign(SampleCampaign(6), SaveVersion.Current);
        Assert.Throws<InvalidOperationException>(
            () => port.ReadLevel("never-written-slot", SaveVersion.Current));
    }

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
            () => port.WriteCampaign(SampleCampaign(1), formatVersion));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => port.WriteLevel("L", MarkerBlob(1, 1, 10), formatVersion));
    }

    [Fact]
    public void Write_rejects_null_meta_null_blob_and_blank_slot_id()
    {
        var port = CreatePort();

        Assert.Throws<ArgumentNullException>(() => port.WriteCampaign(null!, SaveVersion.Current));
        Assert.Throws<ArgumentNullException>(
            () => port.WriteLevel("L", null!, SaveVersion.Current));
        Assert.Throws<ArgumentException>(
            () => port.WriteLevel("   ", MarkerBlob(1, 1, 10), SaveVersion.Current));
        Assert.Throws<ArgumentException>(
            () => port.ReadLevel("", SaveVersion.Current));
    }
}
