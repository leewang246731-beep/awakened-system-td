using System.Security.Cryptography;
using EraCore.Llm.L2;
using Xunit;

namespace EraCore.Tests.Llm.L2;

// [F-078] T-137 下载引擎（集成级·网络 mock）：FakeWirePort 供字节，真临时目录
// 落真文件——完整下载/断点续传/取消保活/sha256 失配单次重试/尺寸门拒收/
// 已完整免下载。绝不下真模型（1.9 GB 真下=验收红线；mock 的是 wire 端口，
// 被测系统=引擎本身，非被 mock 对象——CS-3.4 端口 mock 合规）。
public class L2DownloadEngineTests : IDisposable
{
    private readonly string _root;

    public L2DownloadEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "era-l2-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ------------------------------------------------ 支撑：真文件端口 + 假 wire

    /// <summary>Real file port over the sandbox root (the presentation port behaves identically over the injected data root).</summary>
    private sealed class RealFilePort : IL2DownloadFilePort
    {
        private readonly string _root;

        public RealFilePort(string root) => _root = root;

        private string Full(string relative) => Path.GetFullPath(Path.Combine(_root, relative));

        public long SizeOf(string relativePath) => File.Exists(Full(relativePath))
            ? new FileInfo(Full(relativePath)).Length
            : -1;

        public void Delete(string relativePath)
        {
            string full = Full(relativePath);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }

        public Stream OpenAppend(string relativePath)
        {
            string full = Full(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return new FileStream(full, FileMode.Append, FileAccess.Write);
        }

        public Stream OpenRead(string relativePath) => File.OpenRead(Full(relativePath));

        public void Move(string fromRelative, string toRelative)
        {
            string from = Full(fromRelative);
            string to = Full(toRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
        }
    }

    /// <summary>
    /// In-memory artifact source: serves deterministic bytes by range
    /// (optionally in smaller slices than requested — real networks chunk),
    /// counts calls, honours the cancellation token, and supports per-offset
    /// failure injection for transport tests.
    /// </summary>
    private sealed class FakeWire : IL2DownloadWirePort
    {
        private readonly byte[] _bytes;
        public int ServeSlice { get; set; } = int.MaxValue;
        public int RangeCalls { get; private set; }
        public int LengthProbes { get; private set; }
        public Func<long, Exception?>? FailAtOffset { get; set; }

        public FakeWire(byte[] bytes) => _bytes = bytes;

        public Task<long> ProbeLengthAsync(L2Artifact artifact, CancellationToken cancellationToken)
        {
            LengthProbes++;
            return Task.FromResult((long)_bytes.Length);
        }

        public Task<L2RangeReply> FetchRangeAsync(
            L2Artifact artifact, long offset, int maxBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailAtOffset?.Invoke(offset) is { } failure)
            {
                return Task.FromException<L2RangeReply>(failure);
            }

            RangeCalls++;
            int take = (int)Math.Min(Math.Min(maxBytes, ServeSlice), _bytes.Length - offset);
            var slice = new byte[take];
            Array.Copy(_bytes, offset, slice, 0, take);
            return Task.FromResult(new L2RangeReply(_bytes.Length, slice));
        }
    }

    private static L2Artifact ArtifactOf(byte[] content, string? sha = null) => new(
        "model", "https://example.invalid/model.gguf", "model.gguf", "models",
        content.Length, sha ?? ShaOf(content));

    private static string ShaOf(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] ContentOf(long length)
    {
        var bytes = new byte[length];
        var rng = new Random(42);
        rng.NextBytes(bytes);
        return bytes;
    }

    // ------------------------------------------------ happy path + resume

    [Fact] // 全量下载+sha256 验证+原子晋升：终稿字节与源全等
    public async Task Full_download_verifies_digest_and_promotes_atomically()
    {
        byte[] content = ContentOf(10_000);
        var artifact = ArtifactOf(content);
        var engine = new L2DownloadEngine(new FakeWire(content), new RealFilePort(_root));
        var progressList = new List<L2DownloadProgress>();

        L2DownloadResult result = await engine.RunAsync(
            artifact, p => progressList.Add(p), CancellationToken.None);

        Assert.True(result.IsUsable);
        Assert.Equal(L2DownloadOutcome.Completed, result.Outcome);
        string finalPath = Path.Combine(_root, "models", artifact.FileName);
        Assert.True(File.Exists(finalPath));
        Assert.Equal(content, await File.ReadAllBytesAsync(finalPath));
        Assert.False(File.Exists(finalPath + ".part"));

        // progress is monotonic and ends at 100%
        Assert.Equal(100, progressList[^1].Percent);
        Assert.Equal(content.Length, progressList[^1].DownloadedBytes);
        for (int i = 1; i < progressList.Count; i++)
        {
            Assert.True(progressList[i].DownloadedBytes >= progressList[i - 1].DownloadedBytes);
        }
    }

    [Fact] // 断点续传：预置半截 .part → 只补后半，终稿与源全等（前缀字节零重取）
    public async Task Existing_part_resumes_from_its_length_without_refetching_earlier_bytes()
    {
        byte[] content = ContentOf(10_000);
        var artifact = ArtifactOf(content);
        var files = new RealFilePort(_root);
        var wire = new FakeWire(content);

        // Pre-place a valid half-part (correct prefix bytes).
        string partPath = Path.Combine(_root, "models", artifact.FileName + ".part");
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        await File.WriteAllBytesAsync(partPath, content[..5_000]);

        L2DownloadResult result = await new L2DownloadEngine(wire, files)
            .RunAsync(artifact, null, CancellationToken.None);

        Assert.True(result.IsUsable);
        // The final file is byte-identical to the source (prefix preserved + suffix appended).
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, "models", artifact.FileName)));
        Assert.False(File.Exists(partPath)); // promoted away
    }

    [Fact] // 断点续传（判据面）：续传运行的首次进度=已有 .part 长度，wire 首请求偏移=同值
    public async Task Resume_run_reports_part_length_as_first_progress_and_first_wire_offset()
    {
        byte[] content = ContentOf(8_000);
        var artifact = ArtifactOf(content);
        var files = new RealFilePort(_root);

        string partPath = Path.Combine(_root, "models", artifact.FileName + ".part");
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        await File.WriteAllBytesAsync(partPath, content[..3_000]);

        var wire = new RecordingWire(new FakeWire(content));
        var progressList = new List<L2DownloadProgress>();
        var engine = new L2DownloadEngine(wire, files);

        L2DownloadResult result = await engine.RunAsync(artifact, p => progressList.Add(p), CancellationToken.None);

        Assert.True(result.IsUsable);
        Assert.Equal(3_000, progressList[0].DownloadedBytes); // resume start, not zero
        Assert.Equal(3_000, wire.RequestedOffsets[0]);         // wire never refetches settled bytes
        Assert.All(wire.RequestedOffsets, offset => Assert.True(offset >= 3_000));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, "models", artifact.FileName)));
    }

    private sealed class RecordingWire(IL2DownloadWirePort inner) : IL2DownloadWirePort
    {
        public List<long> RequestedOffsets { get; } = [];

        public Task<L2RangeReply> FetchRangeAsync(
            L2Artifact artifact, long offset, int maxBytes, CancellationToken cancellationToken)
        {
            RequestedOffsets.Add(offset);
            return inner.FetchRangeAsync(artifact, offset, maxBytes, cancellationToken);
        }

        public Task<long> ProbeLengthAsync(L2Artifact artifact, CancellationToken cancellationToken) =>
            inner.ProbeLengthAsync(artifact, cancellationToken);
    }

    // ------------------------------------------------ 取消

    [Fact] // 取消：.part 保留（不为已付字节买单第二次）→ 下轮续传完成
    public async Task Cancel_keeps_the_part_file_and_the_next_run_resumes_to_completion()
    {
        byte[] content = ContentOf(20_000);
        var artifact = ArtifactOf(content);
        var files = new RealFilePort(_root);
        using var cancel = new CancellationTokenSource();

        // Serve in 4 KB slices; cancel once the settled prefix reaches 12 KB.
        var wire = new FakeWire(content)
        {
            ServeSlice = 4_000,
            FailAtOffset = offset =>
            {
                if (offset >= 12_000)
                {
                    cancel.Cancel(); // the NEXT loop-head token check aborts cleanly (player cancel path)
                }

                return null;
            },
        };

        var engine = new L2DownloadEngine(wire, files);
        L2DownloadResult cancelled = await engine.RunAsync(artifact, null, cancel.Token);

        Assert.Equal(L2DownloadOutcome.Cancelled, cancelled.Outcome);
        string partPath = Path.Combine(_root, "models", artifact.FileName + ".part");
        Assert.True(File.Exists(partPath));
        Assert.Equal(12_000, new FileInfo(partPath).Length); // exactly the settled prefix

        // Second run (no failure) resumes and completes.
        L2DownloadResult finished = await new L2DownloadEngine(new FakeWire(content), files)
            .RunAsync(artifact, null, CancellationToken.None);
        Assert.True(finished.IsUsable);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, "models", artifact.FileName)));
    }

    // ------------------------------------------------ 校验门

    [Fact] // sha256 失配：清盘重下一次；二次仍失配 → 诚实失败（两文件皆清）
    public async Task Digest_mismatch_restarts_once_then_fails_honestly()
    {
        byte[] content = ContentOf(6_000);
        var artifact = ArtifactOf(content, sha: "deadbeef" + new string('0', 56)); // wrong digest by construction
        var files = new RealFilePort(_root);
        var wire = new FakeWire(content);

        L2DownloadResult result = await new L2DownloadEngine(wire, files)
            .RunAsync(artifact, null, CancellationToken.None);

        Assert.Equal(L2DownloadOutcome.VerificationFailed, result.Outcome);
        Assert.Contains("digest", result.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "models", artifact.FileName)));
        Assert.False(File.Exists(Path.Combine(_root, "models", artifact.FileName + ".part")));
        Assert.Equal(2, wire.RangeCalls); // 6000 bytes < one 4 MiB chunk ⇒ one call per attempt: restarted exactly once
    }

    [Fact] // 尺寸门：服务端总长与清单不符 → 立即失败不落盘晋升
    public async Task Server_total_length_mismatch_fails_without_promotion()
    {
        byte[] content = ContentOf(1_000);
        var artifact = new L2Artifact(
            "model", "https://example.invalid/model.gguf", "model.gguf", "models",
            SizeBytes: 2_000, Sha256Hex: null); // manifest claims 2000, server has 1000
        var files = new RealFilePort(_root);

        L2DownloadResult result = await new L2DownloadEngine(new FakeWire(content), files)
            .RunAsync(artifact, null, CancellationToken.None);

        Assert.Equal(L2DownloadOutcome.VerificationFailed, result.Outcome);
        Assert.Contains("size gate", result.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "models", artifact.FileName)));
    }

    [Fact] // 已完整（尺寸+摘要验过）：零 wire 调用直接 Completed（二次安装免流量）
    public async Task Already_verified_final_file_downloads_nothing()
    {
        byte[] content = ContentOf(500);
        var artifact = ArtifactOf(content);
        var files = new RealFilePort(_root);
        string finalPath = Path.Combine(_root, "models", artifact.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllBytesAsync(finalPath, content);

        var wire = new FakeWire(content);
        L2DownloadResult result = await new L2DownloadEngine(wire, files)
            .RunAsync(artifact, null, CancellationToken.None);

        Assert.Equal(L2DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(0, wire.RangeCalls); // zero wire calls — the file was already verified by size+digest
    }

    // ------------------------------------------------ 传输失败

    [Fact] // 传输中断：TransportFailed + .part 保留（会话间断点续传依据）
    public async Task Transport_failure_keeps_partial_for_a_later_resume()
    {
        byte[] content = ContentOf(12_000);
        var artifact = ArtifactOf(content);
        var files = new RealFilePort(_root);
        var wire = new FakeWire(content)
        {
            ServeSlice = 4_000,
            FailAtOffset = offset => offset >= 4_000 ? new IOException("simulated DNS loss") : null,
        };

        L2DownloadResult result = await new L2DownloadEngine(wire, files)
            .RunAsync(artifact, null, CancellationToken.None);

        Assert.Equal(L2DownloadOutcome.TransportFailed, result.Outcome);
        string partPath = Path.Combine(_root, "models", artifact.FileName + ".part");
        Assert.True(File.Exists(partPath));
        Assert.Equal(4_000, new FileInfo(partPath).Length);
    }
}
