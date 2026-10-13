using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EraCore.Tests.Persistence;

// 崩溃注入：C-04（T-041 核心验收）
//
// Every scenario here interrupts a write at the storage-engine level and
// then asserts the SAME invariant: after recovery the store reads either
// the complete previous state or fails as SaveCorruptException — never a
// mix of old and new payload halves.
// [F-160] 两级存档架构
public class SqliteSavePortCrashInjectionTests : SqliteSavePortTestBase
{
    // State layout shared by the two image-restore scenarios:
    private const long SeedInMainFile = 111;     // folded into the main db file
    private const long SeedCommittedInWal = 222; // committed, frames still WAL-only
    private const long SeedUncommitted = 999;    // in-flight rewrite, never committed

    // ------------------------------------------------------------ legal

    [Fact]
    public void Outside_write_lock_mid_transaction_leaves_the_store_fully_at_the_old_state()
    {
        var port = CreatePort();
        port.WriteCampaign(SampleCampaign(SeedInMainFile), SaveVersion.Current);
        var oldBlob = MarkerBlob(0xA0, 3, 5);
        port.WriteLevel("L1", oldBlob, SaveVersion.Current);

        // "Crash": a second connection opens a write transaction and inserts
        // an in-flight row it never commits, wedging the write lock.
        using (var outside = OpenRawConnection())
        {
            using (var txn = outside.BeginTransaction())
            {
                using var command = outside.CreateCommand();
                command.CommandText = "INSERT INTO level_blob (level_slot_id, wave_checkpoint, "
                    + "sim_snapshot, command_tail) VALUES ('inflight-slot', 99, x'01', x'02');";
                command.ExecuteNonQuery();

                // The port's write must fail (busy), not queue silently.
                var writeFailure = Assert.Throws<SqliteException>(
                    () => port.WriteLevel("L1", MarkerBlob(0xA0, 9, 13), SaveVersion.Current));
                Assert.Contains("locked", writeFailure.Message, StringComparison.OrdinalIgnoreCase);
            } // the uncommitted in-flight row rolls back with the transaction

            Assert.Throws<InvalidOperationException>(
                () => port.ReadLevel("inflight-slot", SaveVersion.Current));
        }

        // Complete OLD state: nothing half-written survived the failure.
        Assert.Equal(SeedInMainFile, port.ReadCampaign(SaveVersion.Current).WorldSeed);
        var blob = port.ReadLevel("L1", SaveVersion.Current);
        Assert.Equal(5, blob.WaveCheckpoint);
        Assert.Equal(oldBlob.SimSnapshotPayload, blob.SimSnapshotPayload);
        Assert.Equal(oldBlob.CommandTailPayload, blob.CommandTailPayload);
    }

    [Fact]
    public void Restored_mid_transaction_wal_image_recovers_the_last_committed_state()
    {
        // Snapshot the database + WAL exactly while an uncommitted
        // transaction sits in the WAL — the on-disk image a hard crash
        // (power loss / kill -9) leaves behind.
        CaptureMidTransactionCrashImage(out var crashedDatabase, out var crashedWal);

        RestoreCrashImage(crashedDatabase, crashedWal);

        // WAL recovery replays every COMMITTED frame (seed 222) and ignores
        // the uncommitted tail (seed 999): a complete state, never a mix.
        var recovered = CreatePort().ReadCampaign(SaveVersion.Current);
        Assert.Equal(SeedCommittedInWal, recovered.WorldSeed);
    }

    [Fact]
    public void Deleted_wal_falls_back_to_the_last_checkpointed_state()
    {
        CaptureMidTransactionCrashImage(out var crashedDatabase, out _);

        // The D-11 "temp + rename" worst case: the WAL is lost entirely; the
        // main file must still decode as a complete earlier state.
        RestoreCrashImage(crashedDatabase, walBytes: null);

        var recovered = CreatePort().ReadCampaign(SaveVersion.Current);
        Assert.Equal(SeedInMainFile, recovered.WorldSeed);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Builds a store whose main file holds <see cref="SeedInMainFile"/>,
    /// commits <see cref="SeedCommittedInWal"/> while an outside reader pins
    /// the checkpoint (so its frames stay WAL-only), then performs an
    /// UNCOMMITTED payload rewrite to <see cref="SeedUncommitted"/> and
    /// captures the database + WAL bytes with that transaction in flight.
    /// </summary>
    private void CaptureMidTransactionCrashImage(out byte[] crashedDatabase, out byte[] crashedWal)
    {
        var port = CreatePort();
        port.WriteCampaign(SampleCampaign(SeedInMainFile), SaveVersion.Current);

        var walPath = DbPhysicalPath + "-wal";

        // Outside connection used in two phases: first a read transaction
        // that pins the checkpoint mark, then the in-flight writer.
        using (var pinning = OpenRawConnection(mode: SqliteOpenMode.ReadWrite))
        {
            // Phase 1 — deferred BEGIN: a real read transaction (Microsoft
            // Data.Sqlite's default BeginTransaction is BEGIN IMMEDIATE,
            // which would hold the WAL writer lock instead of pinning the
            // checkpoint mark). While the pin is held, the port's 222 commit
            // cannot be folded into the main file; releasing the pin right
            // after leaves the frames WAL-only (no checkpoint runs of its
            // own accord at this tiny page count).
            using (var pin = pinning.BeginTransaction(deferred: true))
            {
                using (var pinCommand = pinning.CreateCommand())
                {
                    pinCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
                    pinCommand.ExecuteScalar();
                }

                port.WriteCampaign(SampleCampaign(SeedCommittedInWal), SaveVersion.Current);
                pin.Rollback();
            }

            // Phase 2 — a FRESH immediate transaction rewrites the payload
            // to the never-committed 999 state (upgrading the stale phase-1
            // snapshot would fail as SQLITE_BUSY_SNAPSHOT — WAL guarding
            // against exactly that lost update). Frames land in the WAL, no
            // commit frame is ever written.
            using var inflight = pinning.BeginTransaction();
            using (var writer = pinning.CreateCommand())
            {
                writer.CommandText = "UPDATE campaign_meta SET payload_json = $payload;";
                writer.Parameters.AddWithValue(
                    "$payload",
                    CampaignSaveJsonContext.SerializeCampaign(SampleCampaign(SeedUncommitted)));
                writer.ExecuteNonQuery();
            }

            crashedDatabase = ReadAllBytesShared(DbPhysicalPath);
            crashedWal = ReadAllBytesShared(walPath);
            Assert.True(crashedWal.Length > 0, "expected in-flight frames in the WAL");

            inflight.Rollback(); // the in-flight transaction dies here
        }
    }

    /// <summary>
    /// Overwrites the live store with a captured crash image. A null WAL
    /// simulates its complete loss; the -shm sidecar is always removed so
    /// recovery rebuilds from the two files alone.
    /// </summary>
    private void RestoreCrashImage(byte[] databaseBytes, byte[]? walBytes)
    {
        var walPath = DbPhysicalPath + "-wal";
        var shmPath = DbPhysicalPath + "-shm";

        foreach (var sidecar in new[] { walPath, shmPath })
        {
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        File.WriteAllBytes(DbPhysicalPath, databaseBytes);
        if (walBytes is not null)
        {
            File.WriteAllBytes(walPath, walBytes);
        }
    }
}
