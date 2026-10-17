using System.IO;
using EraCore.Llm.L2;
using Godot;

namespace EraTD.L2;

// T-137 (C-27/F-078) presentation 侧端口实现：下载 wire 端口（Godot
// HttpRequest 字节区间 GET——Range 续传头）+ 流式文件端口（注入数据根下的
// 追加/读/移动）。EraCore 持有引擎与策略；本文件只搬字节（CS-1.3 同源纪律：
// 真网络/真磁盘只在表现层）。R9 纪律：路径全部由注入根拼接，零盘符字面量。

/// <summary>
/// Godot-backed <see cref="IL2DownloadWirePort"/>: one byte-range GET per
/// call over an <see cref="HttpRequest"/> node. Zero redirect following (a
/// 3xx surfaces as an error — the artifact URL is the only legal origin),
/// total length parsed from Content-Range.
/// </summary>
public sealed class GodotDownloadWirePort : IL2DownloadWirePort, IDisposable
{
    private readonly HttpRequest _request;

    /// <summary>Creates the port over one owner node (adds its HttpRequest child).</summary>
    public GodotDownloadWirePort(Node owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _request = new HttpRequest
        {
            MaxRedirects = 0,
            Timeout = 120d,
            UseThreads = true,
            BodySizeLimit = 64 * 1024 * 1024, // ci-allow: R7 T-137 单分片上限=引擎 DownloadChunkBytes 的 16 倍余量（工程域常量）
        };
        owner.AddChild(_request);
    }

    /// <inheritdoc />
    public async Task<L2RangeReply> FetchRangeAsync(
        L2Artifact artifact, long offset, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        (long total, byte[] chunk) = await GetRangeAsync(
            artifact.DownloadUrl, $"bytes={offset}-{offset + maxBytes - 1}", cancellationToken)
            .ConfigureAwait(false);
        return new L2RangeReply(total, chunk);
    }

    /// <inheritdoc />
    public async Task<long> ProbeLengthAsync(L2Artifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // 1-byte range GET keeps HEAD-flavoured semantics while staying on
        // the same code path (some CDNs mishandle HEAD).
        (long total, _) = await GetRangeAsync(
            artifact.DownloadUrl, "bytes=0-0", cancellationToken).ConfigureAwait(false);
        return total;
    }

    private async Task<(long Total, byte[] Chunk)> GetRangeAsync(
        string url, string rangeHeader, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<(long, byte[])>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(long result, long code, string[] headers, byte[] body)
        {
            if (result != (long)HttpRequest.Result.Success)
            {
                completion.TrySetException(new IOException($"download transport failure result={result}"));
                return;
            }

            if (code is < 200 or > 299)
            {
                completion.TrySetException(new IOException($"download http status {code}"));
                return;
            }

            long total = ParseTotalLength(headers, code, body);
            if (total < 0)
            {
                completion.TrySetException(new IOException("download reply missing content-range/length"));
                return;
            }

            completion.TrySetResult((total, body ?? []));
        }

        _request.RequestCompleted += OnCompleted;
        CancellationTokenRegistration cancel = cancellationToken.Register(
            () =>
            {
                _request.CancelRequest();
                completion.TrySetCanceled(cancellationToken);
            });

        string[] headerLines = [$"Range: {rangeHeader}"];
        Error sent = _request.Request(url, headerLines, Godot.HttpClient.Method.Get);
        if (sent != Error.Ok)
        {
            _request.RequestCompleted -= OnCompleted;
            cancel.Dispose();
            throw new IOException($"download request rejected result={sent}");
        }

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _request.RequestCompleted -= OnCompleted;
            cancel.Dispose();
        }
    }

    /// <summary>Total length of the reply: Content-Range total, else Content-Length (a tail-range 200 reply carries no Content-Range).</summary>
    private static long ParseTotalLength(string[] headers, long code, byte[] body)
    {
        foreach (string header in headers)
        {
            if (header.StartsWith("Content-Range:", StringComparison.OrdinalIgnoreCase))
            {
                string value = header["Content-Range:".Length..].Trim();
                int slash = value.LastIndexOf('/');
                if (slash >= 0 && long.TryParse(value[(slash + 1)..], out long total))
                {
                    return total;
                }
            }

            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(header["Content-Length:".Length..].Trim(), out long length))
            {
                return length; // 200 full-body reply: length IS the total
            }
        }

        return code == 200 && body is { Length: > 0 } ? body.Length : -1;
    }

    /// <inheritdoc />
    public void Dispose() => _request?.QueueFree();
}

/// <summary>
/// Streaming <see cref="IL2DownloadFilePort"/> over the injected data root
/// (D-03): every relative path resolves under the root — players install on
/// any drive, the code never assumes one (R9).
/// </summary>
public sealed class DataRootDownloadFilePort : IL2DownloadFilePort
{
    private readonly string _root;

    /// <param name="root">Absolute injected data root (dev store root or release saves root).</param>
    public DataRootDownloadFilePort(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("data root must be non-empty", nameof(root));
        }

        _root = Path.GetFullPath(root);
    }

    /// <summary>Absolute path of one relative path under the root (escape-checked).</summary>
    public string FullPath(string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(_root, relativePath));
        string normalizedRoot = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"relative path '{relativePath}' escapes the data root.");
        }

        return full;
    }

    /// <inheritdoc />
    public long SizeOf(string relativePath) => File.Exists(FullPath(relativePath))
        ? new FileInfo(FullPath(relativePath)).Length
        : -1;

    /// <inheritdoc />
    public void Delete(string relativePath)
    {
        string full = FullPath(relativePath);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    /// <inheritdoc />
    public Stream OpenAppend(string relativePath)
    {
        string full = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return new System.IO.FileStream(
            full, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read);
    }

    /// <inheritdoc />
    public Stream OpenRead(string relativePath) => File.OpenRead(FullPath(relativePath));

    /// <inheritdoc />
    public void Move(string fromRelative, string toRelative)
    {
        string from = FullPath(fromRelative);
        string to = FullPath(toRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
    }
}
