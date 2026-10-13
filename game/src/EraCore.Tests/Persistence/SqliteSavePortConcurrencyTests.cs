using System.Collections.Concurrent;
using EraCore.Contracts.Persistence;
using EraCore.Persistence;
using Xunit;

namespace EraCore.Tests.Persistence;

// [F-160] 两级存档架构：C-04 并行写安全——写锁序列化，不同关卡不互损（T-041）
public class SqliteSavePortConcurrencyTests : SqliteSavePortTestBase
{
    private const int WritesPerThread = 15;

    // ------------------------------------------------------------ legal

    [Fact]
    public async Task Concurrent_writes_to_different_slots_serialize_without_loss()
    {
        var port = CreatePort();
        port.WriteCampaign(SampleCampaign(4242), SaveVersion.Current);

        var failures = new ConcurrentQueue<Exception>();
        var slotA = Task.Run(() =>
        {
            try
            {
                for (var i = 1; i <= WritesPerThread; i++)
                {
                    port.WriteLevel("slot.a", MarkerBlob(0xA0, i, 10 + i), SaveVersion.Current);
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
                for (var i = 1; i <= WritesPerThread; i++)
                {
                    port.WriteLevel("slot.b", MarkerBlob(0x5A, i, 20 + i), SaveVersion.Current);
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        });

        await Task.WhenAll(slotA, slotB);

        Assert.Empty(failures); // the port's write gate serialized every call
        Assert.Equal(25, port.ReadLevel("slot.a", SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(35, port.ReadLevel("slot.b", SaveVersion.Current).WaveCheckpoint);

        // The last write per slot won exactly: marker + sequence survive intact.
        var blobA = port.ReadLevel("slot.a", SaveVersion.Current);
        Assert.Equal(0xA0, blobA.SimSnapshotPayload[0]);
        Assert.Equal(WritesPerThread, blobA.SimSnapshotPayload[1]);
        Assert.Equal((byte)(255 - 0xA0), blobA.CommandTailPayload[0]);
        var blobB = port.ReadLevel("slot.b", SaveVersion.Current);
        Assert.Equal(0x5A, blobB.SimSnapshotPayload[0]);
        Assert.Equal(WritesPerThread, blobB.SimSnapshotPayload[1]);

        // And nothing leaked into other domains or slots.
        Assert.Equal(4242, port.ReadCampaign(SaveVersion.Current).WorldSeed);
        using (var raw = OpenRawConnection())
        {
            Assert.Equal(2, ScalarLong(raw, "SELECT COUNT(*) FROM level_blob;"));
        }

        // Rotation ran WritesPerThread*2 + 1 times; generations stay capped.
        var savesDir = Path.GetDirectoryName(DbPhysicalPath)!;
        Assert.Equal(2, Directory.EnumerateFiles(
            savesDir, Path.GetFileName(DbPhysicalPath) + ".bak*").Count());
    }

    // ------------------------------------------------------------ boundary

    [Fact]
    public async Task Concurrent_reads_during_writes_only_observe_complete_states()
    {
        var port = CreatePort();
        port.WriteCampaign(SampleCampaign(7), SaveVersion.Current);
        // Prime the slot so concurrent readers never race a missing row
        // (that case is covered by the missing-slot unit test).
        port.WriteLevel("slot.a", MarkerBlob(0xA0, 0, 30), SaveVersion.Current);

        var failures = new ConcurrentQueue<Exception>();
        var observed = new ConcurrentQueue<int>();

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= WritesPerThread; i++)
            {
                port.WriteLevel("slot.a", MarkerBlob(0xA0, i, 30 + i), SaveVersion.Current);
            }
        });
        var reader = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < WritesPerThread * 4; i++)
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
        Assert.Equal(30 + WritesPerThread, port.ReadLevel("slot.a", SaveVersion.Current).WaveCheckpoint);
    }
}
