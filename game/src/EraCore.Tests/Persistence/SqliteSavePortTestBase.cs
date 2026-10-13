using EraCore.Contracts.Persistence;
using EraCore.IO;
using EraCore.Persistence;
using Microsoft.Data.Sqlite;

namespace EraCore.Tests.Persistence;

/// <summary>
/// Shared fixture for the SQLite save-port test suite (T-041): a GUID
/// sandbox under the D-03 dev-root scratch area, disposed per test
/// (xUnit instantiates the class once per test method; CS-3.8).
/// All physical paths are derived from the injected store root — no
/// workstation path appears in any test.
/// </summary>
public abstract class SqliteSavePortTestBase : IDisposable
{
    protected const string DbRelativePath = "saves/profile.db";

    protected SqliteSavePortTestBase()
    {
        Root = Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));
        Files = new DevFileStore(Root);
    }

    /// <summary>Per-test injected writable root (D-03; deleted in Dispose).</summary>
    protected string Root { get; }

    protected DevFileStore Files { get; }

    protected string DbPhysicalPath => Path.GetFullPath(Path.Combine(Root, DbRelativePath));

    protected SqliteSavePort CreatePort(SaveMigrator? migrator = null) =>
        new(Files, DbRelativePath, migrator);

    /// <summary>Opens a raw test connection to the store's database file.</summary>
    protected SqliteConnection OpenRawConnection(string? physicalPath = null, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var target = physicalPath ?? DbPhysicalPath;
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory); // SQLite creates files, not folders
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = target,
            Mode = mode,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Reads a file that another connection may hold open (SQLite opens
    /// with write access, which plain File.ReadAllBytes's share mode denies)
    /// — the crash-injection capture path.
    /// </summary>
    protected static byte[] ReadAllBytesShared(string physicalPath)
    {
        using var stream = new FileStream(
            physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    protected static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    protected static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    internal static CampaignMetaDto SampleCampaign(long worldSeed) => new(
        worldSeed,
        2,
        new Dictionary<string, long> { ["steel"] = 150, ["intel"] = 9 },
        new List<string> { "tech.agri", "tech.smith" },
        new List<string> { "relic.cheb", "relic.map" },
        new List<LevelLayoutSnapshotDto>
        {
            new("level.e1.m01", new byte[] { 1, 2, 3, 4 }),
            new("level.e1.m02", new byte[] { 9, 8, 7 }),
        });

    /// <summary>
    /// A level blob whose payloads carry a slot marker plus the write
    /// sequence number, so torn or swapped reads are detectable by value.
    /// </summary>
    internal static LevelSaveBlobDto MarkerBlob(byte marker, int sequence, int waveCheckpoint)
    {
        var sim = new byte[257];
        var tail = new byte[61];
        sim[0] = marker;
        sim[1] = (byte)(sequence & 0xFF);
        tail[0] = (byte)~marker;
        tail[1] = (byte)(sequence & 0xFF);
        return new LevelSaveBlobDto(waveCheckpoint, sim, tail);
    }

    public void Dispose()
    {
        // Only this fixture's own GUID sandbox — sibling GUIDs may belong to
        // concurrent runs (same policy as DevFileStoreTests).
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
