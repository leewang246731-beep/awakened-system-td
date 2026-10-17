namespace EraCore.Llm.L2;

/// <summary>
/// One wire range reply of the L2 download port (T-137): the artifact's
/// authoritative total length plus the requested byte slice. Ports return
/// failures as exceptions (transport contract mirrors <c>IHttpPort</c>).
/// </summary>
/// <param name="TotalLength">Server-reported total byte length (Content-Length of the 200/206 reply).</param>
/// <param name="Chunk">The requested bytes (may be shorter than requested at tail).</param>
public sealed record L2RangeReply(long TotalLength, byte[] Chunk);

/// <summary>
/// Wire port of the L2 downloader (T-137): byte-range GET against the public
/// artifact URLs. EraCore defines the contract and consumes it; the real
/// networking lives in the presentation host (CS-1.3 — same seam discipline
/// as <c>IHttpPort</c>; the LlmLayerGuard「EraCore 内零实现」红色线对本端口
/// 同样适用).
/// </summary>
public interface IL2DownloadWirePort
{
    /// <summary>Fetches one byte range [offset, offset+maxBytes) of the artifact.</summary>
    /// <exception cref="IOException">Transport failure (DNS/TLS/refused/reset).</exception>
    Task<L2RangeReply> FetchRangeAsync(
        L2Artifact artifact, long offset, int maxBytes, CancellationToken cancellationToken);

    /// <summary>Probes the artifact's total length without downloading body bytes.</summary>
    /// <exception cref="IOException">Transport failure.</exception>
    Task<long> ProbeLengthAsync(L2Artifact artifact, CancellationToken cancellationToken);
}

/// <summary>
/// File port of the L2 downloader (T-137): streaming append/read over the
/// injected data root (D-03 — the byte-array <c>IFileStore</c> cannot stream
/// 1.93 GB, so this port carries Streams; the presentation implementation
/// resolves every relative path under the injected root, never a drive
/// letter of its own). EraCore contains no implementation of this port.
/// </summary>
public interface IL2DownloadFilePort
{
    /// <summary>Byte length of one relative path, or -1 when absent.</summary>
    long SizeOf(string relativePath);

    /// <summary>Deletes one relative path (absent = no-op).</summary>
    void Delete(string relativePath);

    /// <summary>Appends (creating parents as needed) and returns a write stream.</summary>
    Stream OpenAppend(string relativePath);

    /// <summary>Opens an existing file for reading.</summary>
    /// <exception cref="FileNotFoundException">Path absent.</exception>
    Stream OpenRead(string relativePath);

    /// <summary>Renames <paramref name="fromRelative"/> to <paramref name="toRelative"/> (atomic promote of the .part file).</summary>
    void Move(string fromRelative, string toRelative);
}
