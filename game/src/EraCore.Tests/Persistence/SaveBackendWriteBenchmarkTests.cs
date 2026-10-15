using System.Diagnostics;
using EraCore.Contracts.Persistence;
using EraCore.IO;
using EraCore.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Persistence;

// [F-164] Web 版约束：C-04 双后端写吞吐对比留档（T-100 / 07 D-32）。
//
// Judgment recorded with the numbers: tower-defense saves are LOW-FREQUENCY
// events (wave boundaries; N7 keeps one checkpoint per level), so the JSON
// fallback is acceptable when 100 slot writes stay far below interactive
// latencies. The ceiling below is the acceptability threshold with ~50x
// headroom over the measured desktop numbers — loose on purpose: this is a
// recorded benchmark, not a performance gate (G-gates stay in the quiet
// window per 08 §3 discipline). Numbers land in the test output (ITestOutputHelper)
// and in the task report.
public sealed class SaveBackendWriteBenchmarkTests : IDisposable
{
    private const int SlotCount = 100;

    /// <summary>Acceptability ceiling: 100 slot writes must finish within 10 s
    /// (measured desktop JSON ≈ tens-to-hundreds of ms — see test output).</summary>
    private const long JsonAcceptableTotalMs = 10_000;

    // Wave-boundary scale payloads (T-057 checkpoint: compressed sim
    // snapshot + command tail; 4 KiB + 1 KiB is the e1 order of magnitude).
    private const int SimPayloadBytes = 4 * 1024;
    private const int TailPayloadBytes = 1 * 1024;

    private readonly ITestOutputHelper _output;

    public SaveBackendWriteBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        Root = Path.Combine(
            DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));
        Files = new DevFileStore(Root);
    }

    private string Root { get; }

    private DevFileStore Files { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void Writing_100_level_slots_json_fallback_stays_within_the_acceptable_ceiling()
    {
        var sqlitePort = new SqliteSavePort(Files, "bench-sqlite/profile.db");
        var jsonPort = new JsonFileSavePort(Files, "bench-json");

        var sqliteMs = TimeSlotWrites(sqlitePort, marker: 0xA0);
        var jsonMs = TimeSlotWrites(jsonPort, marker: 0x5A);

        _output.WriteLine(
            $"save-backend benchmark: {SlotCount} distinct level slots, payload "
            + $"{SimPayloadBytes + TailPayloadBytes} bytes/slot");
        _output.WriteLine($"  sqlite (desktop, WAL + whole-db rotation): {sqliteMs} ms");
        _output.WriteLine($"  json   (Web fallback, temp+rename, per-slot rotation): {jsonMs} ms");

        // Both backends really wrote every slot (last-slot readback), then
        // the acceptability threshold on the JSON fallback.
        Assert.Equal(SlotCount, sqlitePort.ReadLevel(SlotId(SlotCount - 1), SaveVersion.Current).WaveCheckpoint);
        Assert.Equal(SlotCount, jsonPort.ReadLevel(SlotId(SlotCount - 1), SaveVersion.Current).WaveCheckpoint);
        Assert.True(
            jsonMs < JsonAcceptableTotalMs,
            $"JSON fallback wrote {SlotCount} slots in {jsonMs} ms, above the recorded "
            + $"acceptability ceiling of {JsonAcceptableTotalMs} ms (low-frequency saves).");
    }

    private static string SlotId(int index) => $"level.e1.m{index + 1:D2}";

    /// <summary>Times one port writing every distinct slot once, then re-reading the last.</summary>
    private long TimeSlotWrites(ISavePort port, byte marker)
    {
        // One warm-up write per port so first-use costs (native library load,
        // JIT of the serializer) do not distort the comparison.
        port.WriteLevel(SlotId(0), MarkerBlob(marker, 0, 0), SaveVersion.Current);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < SlotCount; i++)
        {
            port.WriteLevel(SlotId(i), MarkerBlob(marker, i + 1, i + 1), SaveVersion.Current);
        }

        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    private static LevelSaveBlobDto MarkerBlob(byte marker, int sequence, int waveCheckpoint)
    {
        var sim = new byte[SimPayloadBytes];
        var tail = new byte[TailPayloadBytes];
        sim[0] = marker;
        sim[1] = (byte)(sequence & 0xFF);
        tail[0] = (byte)~marker;
        tail[1] = (byte)(sequence & 0xFF);
        return new LevelSaveBlobDto(waveCheckpoint, sim, tail);
    }
}
