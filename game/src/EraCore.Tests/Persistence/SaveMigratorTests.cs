using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EraCore.Tests.Persistence;

// [C-04] K-7 save backend: forward migration chain (T-041)
public class SaveMigratorTests : SqliteSavePortTestBase
{
    // ------------------------------------------------------------ legal

    [Fact]
    public void Migrate_from_v1_to_v1_is_the_identity()
    {
        using var connection = OpenRawConnection();
        CreateVersionedStore(connection, SaveVersion.Current);

        SaveMigrator.CreateDefault().Migrate(connection, SaveVersion.Current, SaveVersion.Current);

        Assert.Equal(SaveVersion.Current, ScalarLong(connection, "SELECT version FROM schema_version WHERE id = 1;"));
    }

    [Fact]
    public void Fabricated_v0_store_migrates_to_v1_with_field_mapping()
    {
        // No v0 ever shipped (v1 is the first format); this fabricates the
        // legacy-shaped store the migration MECHANISM must handle, then runs
        // a real v0 -> v1 step that maps the old columns field-by-field into
        // the v1 layout. It proves chain execution, not a historical upgrade.
        CreateFabricatedV0Store();
        var migrator = SaveMigrator.CreateDefault();
        migrator.Register(new FabricatedV0ToV1Step());

        var meta = CreatePort(migrator).ReadCampaign(SaveVersion.Current);

        Assert.Equal(424242, meta.WorldSeed);
        Assert.Equal(2, meta.EraIndex);
        Assert.Equal(150, meta.ResourcePool["steel"]);
        Assert.Equal(9, meta.ResourcePool["intel"]);
        using (var connection = OpenRawConnection())
        {
            Assert.Equal(SaveVersion.Current, ScalarLong(connection, "SELECT version FROM schema_version WHERE id = 1;"));
            Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'campaign_meta_v0';"));
        }

        // The migrated store must also accept v1 level traffic — the step
        // created the complete dual-domain schema, not just campaign_meta.
        var port = CreatePort(migrator);
        port.WriteLevel("level.e1.m01", MarkerBlob(0xA0, 1, 11), SaveVersion.Current);
        Assert.Equal(11, port.ReadLevel("level.e1.m01", SaveVersion.Current).WaveCheckpoint);
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public void Migrate_to_a_lower_target_reports_future_store_as_corrupt()
    {
        using var connection = OpenRawConnection();
        CreateVersionedStore(connection, 2);

        var ex = Assert.Throws<SaveCorruptException>(
            () => SaveMigrator.CreateDefault().Migrate(connection, 2, 1));

        Assert.Equal(2, ex.StoredVersion);
    }

    [Fact]
    public void Broken_chain_rolls_the_whole_migration_back()
    {
        // Register 1 -> 2 but not 2 -> 3: asking for 3 fails mid-chain, and
        // the single wrapping transaction must undo the applied hop too —
        // a store never ends up BETWEEN versions.
        using var connection = OpenRawConnection();
        CreateVersionedStore(connection, 1);
        var migrator = SaveMigrator.CreateDefault();
        migrator.Register(new MarkerStep(1, 2));

        var ex = Assert.Throws<SaveCorruptException>(() => migrator.Migrate(connection, 1, 3));

        Assert.Equal(1, ex.StoredVersion);
        Assert.Equal(1, ScalarLong(connection, "SELECT version FROM schema_version WHERE id = 1;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'migration_marker_v2';"));
    }

    // ------------------------------------------------------------ illegal

    [Fact]
    public void Register_rejects_multi_hop_steps()
    {
        var migrator = SaveMigrator.CreateDefault();

        Assert.Throws<ArgumentException>(() => migrator.Register(new MarkerStep(1, 3)));
    }

    [Fact]
    public void Register_rejects_duplicate_hop_origins()
    {
        var migrator = SaveMigrator.CreateDefault();
        migrator.Register(new MarkerStep(1, 2));

        Assert.Throws<ArgumentException>(() => migrator.Register(new MarkerStep(1, 2)));
    }

    [Fact]
    public void Migrate_rejects_null_connection()
    {
        Assert.Throws<ArgumentNullException>(
            () => SaveMigrator.CreateDefault().Migrate(null!, 1, 1));
    }

    // ------------------------------------------------------------- helpers

    private static void CreateVersionedStore(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE schema_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL); "
            + "INSERT INTO schema_version (id, version) VALUES (1, $version);";
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    /// <summary>Builds the fabricated legacy-shaped store (schema_version = 0).</summary>
    private void CreateFabricatedV0Store()
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE schema_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL); "
            + "INSERT INTO schema_version (id, version) VALUES (1, 0); "
            + "CREATE TABLE campaign_meta_v0 (world_seed INTEGER NOT NULL, era INTEGER NOT NULL, resources_json TEXT NOT NULL); "
            + "INSERT INTO campaign_meta_v0 (world_seed, era, resources_json) VALUES (424242, 2, $resources);";
        command.Parameters.AddWithValue("$resources", "{\"steel\":150,\"intel\":9}");
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The fabricated v0 -&gt; v1 hop used by the chain test: reads the legacy
    /// columns, writes the mapped fields into the v1 JSON payload row, and
    /// creates the rest of the v1 dual-domain schema.
    /// </summary>
    private sealed class FabricatedV0ToV1Step : ISaveMigrationStep
    {
        public int FromVersion => 0;

        public int ToVersion => 1;

        public void Apply(SqliteConnection connection)
        {
            long worldSeed;
            int era;
            string resourcesJson;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT world_seed, era, resources_json FROM campaign_meta_v0;";
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new SaveCorruptException("v0 store has no campaign row to migrate.", 0);
                }

                worldSeed = reader.GetInt64(0);
                era = reader.GetInt32(1);
                resourcesJson = reader.GetString(2);
            }

            var resources = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, long>>(resourcesJson)
                ?? new Dictionary<string, long>();
            var payload = CampaignSaveJsonContext.SerializeCampaign(new CampaignMetaDto(
                worldSeed, era, resources,
                new List<string>(), new List<string>(), new List<LevelLayoutSnapshotDto>()));

            using var apply = connection.CreateCommand();
            apply.CommandText =
                "CREATE TABLE campaign_meta (id INTEGER PRIMARY KEY CHECK (id = 1), payload_json TEXT NOT NULL); "
                + "INSERT INTO campaign_meta (id, payload_json) VALUES (1, $payload); "
                + "CREATE TABLE level_blob (level_slot_id TEXT PRIMARY KEY, wave_checkpoint INTEGER NOT NULL, "
                + "sim_snapshot BLOB NOT NULL, command_tail BLOB NOT NULL); "
                + "DROP TABLE campaign_meta_v0;";
            apply.Parameters.AddWithValue("$payload", payload);
            apply.ExecuteNonQuery();
        }
    }

    /// <summary>A minimal single-hop step whose footprint is easy to verify or roll back.</summary>
    private sealed class MarkerStep : ISaveMigrationStep
    {
        public MarkerStep(int fromVersion, int toVersion)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
        }

        public int FromVersion { get; }

        public int ToVersion { get; }

        public void Apply(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE migration_marker_v2 (x INTEGER NOT NULL); "
                + "INSERT INTO migration_marker_v2 (x) VALUES (7);";
            command.ExecuteNonQuery();
        }
    }
}
